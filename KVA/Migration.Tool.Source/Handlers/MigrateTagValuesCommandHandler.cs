using System.Data;
using System.Xml.Linq;
using CMS.DataEngine;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Tool.Common;
using Migration.Tool.Common.Abstractions;
using Migration.Tool.KX13.Context;
using Migration.Tool.Source.Services;

namespace Migration.Tool.Source.Handlers;

/// <summary>
/// Assigns K13 DocumentTags (comma-separated tag names stored in CmsDocument)
/// to XbyK content item taxonomy fields after pages are migrated.
///
/// Storage in XbyK 31.x (dual write):
///   1. class-specific table column  (e.g. Plearn_Article.DocumentTags)
///      Format: [{"Identifier":"guid1"},{"Identifier":"guid2"}]
///   2. CMS_ContentItemTag — one row per (LanguageMetadata, FieldGUID, TagGUID)
///      XbyK uses this table when loading the editing form and for tag search.
///
/// Run AFTER --sites --tags --page-types --pages
/// </summary>
public class MigrateTagValuesCommandHandler(
    ILogger<MigrateTagValuesCommandHandler> logger,
    IDbContextFactory<KX13Context> kx13ContextFactory,
    SpoiledGuidContext spoiledGuidContext
) : IRequestHandler<MigrateTagValuesCommand, CommandResult>
{
    public async Task<CommandResult> Handle(MigrateTagValuesCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("==== START: MigrateTagValues (DocumentTags → class table JSON + CMS_ContentItemTag) ====");

        await using var kx13Context = await kx13ContextFactory.CreateDbContextAsync(cancellationToken);

        // ── Step 1: K13 documents with non-empty DocumentTags ────────────────
        var docsWithTags = (
            from doc in kx13Context.CmsDocuments
            where !string.IsNullOrEmpty(doc.DocumentTags)
            join tree in kx13Context.CmsTrees on doc.DocumentNodeId equals tree.NodeId
            join cls in kx13Context.CmsClasses on tree.NodeClassId equals cls.ClassId
            select new
            {
                doc.DocumentId,
                DocumentGuid = doc.DocumentGuid ?? Guid.Empty,
                doc.DocumentTags,
                doc.DocumentTagGroupId,
                tree.NodeSiteId,
                tree.NodeId,
                cls.ClassName
            }
        ).ToList();

        logger.LogInformation("Found {Count} K13 documents with DocumentTags", docsWithTags.Count);

        // ── Step 2: K13 tag lookup (TagName → TagGuid) ──────────────────────
        var allK13Tags = kx13Context.CmsTags
            .Select(t => new { t.TagId, t.TagName, t.TagGroupId, t.TagGuid })
            .ToList();

        // ── Step 3: Load ClassName → ClassTableName from XbyK CMS_Class ─────
        var classTableNames = LoadClassTableNames();
        logger.LogInformation("Loaded {Count} class→table mappings", classTableNames.Count);

        // ── Step 4: Verify DocumentTags columns exist in target tables ────────
        var tablesWithColumn = GetTablesWithDocumentTagsColumn();
        logger.LogInformation("Tables with DocumentTags column: {Tables}",
            string.Join(", ", tablesWithColumn));

        // ── Step 5: Load DocumentTags field GUIDs per class (for CMS_ContentItemTag) ──
        var fieldGuids = LoadDocumentTagsFieldGuids();
        logger.LogInformation("Loaded DocumentTags field GUIDs for {Count} classes", fieldGuids.Count);

        int updated = 0;
        int skipped = 0;

        foreach (var doc in docsWithTags)
        {
            if (doc.DocumentGuid == Guid.Empty)
            {
                logger.LogWarning("DocumentId={DocId} has null DocumentGuid — skipped", doc.DocumentId);
                skipped++;
                continue;
            }

            // ── Verify target table has DocumentTags column ──────────────────
            if (!classTableNames.TryGetValue(doc.ClassName, out var tableName))
            {
                logger.LogWarning("No class table found in XbyK for class '{Class}' (DocId={DocId}) — skipped",
                    doc.ClassName, doc.DocumentId);
                skipped++;
                continue;
            }

            // ── Resolve tag names → XbyK tag GUIDs ──────────────────────────
            var tagNames = doc.DocumentTags!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var tagGuids = new List<Guid>();
            foreach (var tagName in tagNames)
            {
                var foundGuid = FindInStaticDict(tagName, doc.DocumentTagGroupId);

                if (!foundGuid.HasValue)
                {
                    var k13Tag = allK13Tags.FirstOrDefault(t =>
                        t.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase) &&
                        (!doc.DocumentTagGroupId.HasValue || t.TagGroupId == doc.DocumentTagGroupId.Value));

                    k13Tag ??= allK13Tags.FirstOrDefault(t =>
                        t.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase));

                    if (k13Tag != null)
                        foundGuid = k13Tag.TagGuid;
                }

                if (foundGuid.HasValue)
                {
                    tagGuids.Add(foundGuid.Value);
                    logger.LogTrace("Mapped '{Tag}' → {Guid}", tagName, foundGuid.Value);
                }
                else
                {
                    logger.LogWarning("Tag '{Tag}' not found (DocId={DocId}) — skipped", tagName, doc.DocumentId);
                }
            }

            if (tagGuids.Count == 0)
            {
                logger.LogWarning("No tags resolved for DocumentId={DocId} Tags='{Tags}' — skipped",
                    doc.DocumentId, doc.DocumentTags);
                skipped++;
                continue;
            }

            // ── Find XbyK ContentItemID via spoiled DocumentGUID ─────────────
            var spoiledGuid = spoiledGuidContext.EnsureDocumentGuid(
                doc.DocumentGuid, doc.NodeSiteId, doc.NodeId, doc.DocumentId);

            var contentItemId = GetContentItemId(spoiledGuid);
            if (contentItemId == null)
            {
                logger.LogWarning(
                    "ContentItemCommonData not found for DocumentGUID={Guid} (DocId={DocId}) — skipped",
                    spoiledGuid, doc.DocumentId);
                skipped++;
                continue;
            }

            // ── Build JSON: [{"Identifier":"guid1"},{"Identifier":"guid2"}] ──
            var json = "[" + string.Join(",",
                tagGuids.Select(g => $"{{\"Identifier\":\"{g:D}\"}}")) + "]";

            // ── Write 1: UPDATE class-specific table column (if it exists) ───
            // Some content types may have the column dropped (e.g. after external=true fix).
            if (tablesWithColumn.Contains(tableName))
            {
                var rowsUpdated = UpdateClassTable(tableName, contentItemId.Value, json);
                if (rowsUpdated > 0)
                {
                    updated += rowsUpdated;
                    logger.LogInformation(
                        "Updated {Rows} row(s) in [{Table}] for DocId={DocId}: {Json}",
                        rowsUpdated, tableName, doc.DocumentId, json);
                }
                else
                {
                    logger.LogWarning(
                        "No rows updated in [{Table}] for ContentItemID={Id} (DocId={DocId}) — pages not migrated?",
                        tableName, contentItemId.Value, doc.DocumentId);
                }
            }
            else
            {
                logger.LogDebug(
                    "Table [{Table}] has no DocumentTags column — skipping physical column update for DocId={DocId}",
                    tableName, doc.DocumentId);
            }

            // ── Write 2: INSERT into CMS_ContentItemTag ─────────────────────
            // XbyK reads from CMS_ContentItemTag when loading the admin edit form.
            // Without this, TagSelector shows empty even when the physical column has data.
            if (fieldGuids.TryGetValue(doc.ClassName, out var fieldGuid))
            {
                UpsertContentItemTags(contentItemId.Value, fieldGuid, tagGuids);
                logger.LogInformation(
                    "Upserted CMS_ContentItemTag for ContentItemID={Id} FieldGUID={FieldGuid} Tags={Count}",
                    contentItemId.Value, fieldGuid, tagGuids.Count);
                updated++;
            }
            else
            {
                logger.LogWarning(
                    "No taxonomy DocumentTags field GUID found for class '{Class}' — CMS_ContentItemTag skipped",
                    doc.ClassName);
            }
        }

        logger.LogInformation(
            "==== END: MigrateTagValues (Updated={Updated} rows, Skipped={Skipped} docs) ====",
            updated, skipped);

        return new GenericCommandResult();
    }

    // ── Load ClassName → ClassTableName from XbyK CMS_Class ─────────────────
    private static Dictionary<string, string> LoadClassTableNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ds = ConnectionHelper.ExecuteQuery(
            "SELECT ClassName, ClassTableName FROM CMS_Class WHERE ClassTableName IS NOT NULL AND ClassTableName != ''",
            null, QueryTypeEnum.SQLQuery);
        foreach (DataRow row in ds.Tables[0].Rows)
            result[row["ClassName"].ToString()!] = row["ClassTableName"].ToString()!;
        return result;
    }

    // ── Find tables that actually have a DocumentTags column ─────────────────
    private static HashSet<string> GetTablesWithDocumentTagsColumn()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ds = ConnectionHelper.ExecuteQuery(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE COLUMN_NAME = 'DocumentTags'",
            null, QueryTypeEnum.SQLQuery);
        foreach (DataRow row in ds.Tables[0].Rows)
            result.Add(row["TABLE_NAME"].ToString()!);
        return result;
    }

    // ── Load DocumentTags field GUIDs for taxonomy fields per class ──────────
    // Used to populate CMS_ContentItemTag.ContentItemTagFieldGUID
    private static Dictionary<string, Guid> LoadDocumentTagsFieldGuids()
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var ds = ConnectionHelper.ExecuteQuery(
            "SELECT ClassName, ClassFormDefinition FROM CMS_Class " +
            "WHERE ClassFormDefinition LIKE '%DocumentTags%' AND ClassFormDefinition LIKE '%taxonomy%'",
            null, QueryTypeEnum.SQLQuery);

        foreach (DataRow row in ds.Tables[0].Rows)
        {
            var className = row["ClassName"].ToString()!;
            var formDef = row["ClassFormDefinition"].ToString()!;
            try
            {
                var xml = XDocument.Parse(formDef);
                var guidStr = xml.Descendants("field")
                    .Where(f => f.Attribute("column")?.Value == "DocumentTags"
                             && f.Attribute("columntype")?.Value == "taxonomy"
                             && f.Attribute("external")?.Value != "true")
                    .Select(f => f.Attribute("guid")?.Value)
                    .FirstOrDefault();

                if (guidStr != null && Guid.TryParse(guidStr, out var guid))
                    result[className] = guid;
            }
            catch (Exception ex)
            {
                // Non-fatal — just skip this class
                _ = ex;
            }
        }
        return result;
    }

    // ── Get XbyK ContentItemID from the spoiled DocumentGUID ─────────────────
    private static int? GetContentItemId(Guid commonDataGuid)
    {
        var p = new QueryDataParameters { { "guid", commonDataGuid } };
        var ds = ConnectionHelper.ExecuteQuery(
            "SELECT ContentItemCommonDataContentItemID FROM CMS_ContentItemCommonData WHERE ContentItemCommonDataGUID = @guid",
            p, QueryTypeEnum.SQLQuery);
        if (ds.Tables[0].Rows.Count == 0) return null;
        return Convert.ToInt32(ds.Tables[0].Rows[0][0]);
    }

    // ── UPDATE DocumentTags in the class-specific table ───────────────────────
    // tableName is from CMS_Class.ClassTableName (trusted, not user input)
    private static int UpdateClassTable(string tableName, int contentItemId, string json)
    {
        var updateSql = $@"
            UPDATE [{tableName}]
            SET    DocumentTags = @json
            WHERE  ContentItemDataCommonDataID IN (
                SELECT ContentItemCommonDataID
                FROM   CMS_ContentItemCommonData
                WHERE  ContentItemCommonDataContentItemID = @id
            )";

        var p = new QueryDataParameters { { "json", json }, { "id", contentItemId } };
        ConnectionHelper.ExecuteQuery(updateSql, p, QueryTypeEnum.SQLQuery);

        var countSql = $@"
            SELECT COUNT(*) FROM [{tableName}]
            WHERE  ContentItemDataCommonDataID IN (
                SELECT ContentItemCommonDataID
                FROM   CMS_ContentItemCommonData
                WHERE  ContentItemCommonDataContentItemID = @id
            )
            AND DocumentTags = @json";

        var countDs = ConnectionHelper.ExecuteQuery(countSql, p, QueryTypeEnum.SQLQuery);
        return Convert.ToInt32(countDs.Tables[0].Rows[0][0]);
    }

    // ── Upsert CMS_ContentItemTag ─────────────────────────────────────────────
    // Deletes existing rows for this content item + field, then inserts fresh ones.
    // One row per (ContentItemLanguageMetadata, FieldGUID, TagGUID).
    private static void UpsertContentItemTags(int contentItemId, Guid fieldGuid, List<Guid> tagGuids)
    {
        // Remove stale tag assignments for this field on this content item
        var deleteSql = @"
            DELETE FROM CMS_ContentItemTag
            WHERE ContentItemTagContentItemLanguageMetadataID IN (
                SELECT ContentItemLanguageMetadataID
                FROM   CMS_ContentItemLanguageMetadata
                WHERE  ContentItemLanguageMetadataContentItemID = @id
            )
            AND ContentItemTagFieldGUID = @fieldGuid";

        var dp = new QueryDataParameters { { "id", contentItemId }, { "fieldGuid", fieldGuid } };
        ConnectionHelper.ExecuteQuery(deleteSql, dp, QueryTypeEnum.SQLQuery);

        // Insert one row per language metadata per tag
        foreach (var tagGuid in tagGuids)
        {
            var insertSql = @"
                INSERT INTO CMS_ContentItemTag (
                    ContentItemTagContentItemLanguageMetadataID,
                    ContentItemTagFieldGUID,
                    ContentItemTagTagGUID,
                    ContentItemTagGUID
                )
                SELECT
                    lm.ContentItemLanguageMetadataID,
                    @fieldGuid,
                    @tagGuid,
                    NEWID()
                FROM CMS_ContentItemLanguageMetadata lm
                WHERE lm.ContentItemLanguageMetadataContentItemID = @id";

            var ip = new QueryDataParameters
            {
                { "id", contentItemId },
                { "fieldGuid", fieldGuid },
                { "tagGuid", tagGuid }
            };
            ConnectionHelper.ExecuteQuery(insertSql, ip, QueryTypeEnum.SQLQuery);
        }
    }

    // ── Look up tag GUID from the static dicts populated by MigrateTagsCommandHandler ──
    private static Guid? FindInStaticDict(string tagName, int? documentTagGroupId)
    {
        if (MigrateTagsCommandHandler.TagIdToName.Count == 0) return null;

        if (documentTagGroupId.HasValue)
        {
            foreach (var (tagId, name) in MigrateTagsCommandHandler.TagIdToName)
            {
                if (name.Equals(tagName, StringComparison.OrdinalIgnoreCase) &&
                    MigrateTagsCommandHandler.TagIdToGroupId.TryGetValue(tagId, out var gid) &&
                    gid == documentTagGroupId.Value &&
                    MigrateTagsCommandHandler.TagIdToTermGuid.TryGetValue(tagId, out var guid))
                    return guid;
            }
        }

        foreach (var (tagId, name) in MigrateTagsCommandHandler.TagIdToName)
        {
            if (name.Equals(tagName, StringComparison.OrdinalIgnoreCase) &&
                MigrateTagsCommandHandler.TagIdToTermGuid.TryGetValue(tagId, out var guid))
                return guid;
        }

        return null;
    }
}
