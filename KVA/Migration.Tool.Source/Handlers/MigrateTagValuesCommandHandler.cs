using System.Data;
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
/// Storage in XbyK 31.x:
///   - DocumentTags is a class-specific field stored in the content-type table
///     (e.g. Plearn_Article.DocumentTags) — NOT in CMS_ContentItemCommonData.
///   - Format: [{"Identifier":"guid1"},{"Identifier":"guid2"}]
///   - The field must NOT have external="true" (TagTaxonomyFieldMigration removes it
///     during --page-types so XbyK creates the column automatically via UMT).
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
        logger.LogInformation("==== START: MigrateTagValues (DocumentTags → class table JSON) ====");

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
        // CmsTagMapper sets TagGUID = cmsTag.TagGuid → K13 TagGuid == XbyK TagGuid
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

            if (!tablesWithColumn.Contains(tableName))
            {
                logger.LogWarning(
                    "Table '{Table}' has no DocumentTags column (class '{Class}') — re-run --page-types first",
                    tableName, doc.ClassName);
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

            // ── UPDATE class-specific table for ALL version rows ─────────────
            // Each version has a row in Plearn_Article linked via ContentItemDataCommonDataID
            // → CMS_ContentItemCommonData.ContentItemCommonDataID
            // → CMS_ContentItemCommonData.ContentItemCommonDataContentItemID = contentItemId
            var rowsUpdated = UpdateClassTable(tableName, contentItemId.Value, json);

            if (rowsUpdated > 0)
            {
                updated += rowsUpdated;
                logger.LogInformation(
                    "Updated {Rows} row(s) in [{Table}] for DocId={DocId} Class={Class}: {Json}",
                    rowsUpdated, tableName, doc.DocumentId, doc.ClassName, json);
            }
            else
            {
                logger.LogWarning(
                    "No rows updated in [{Table}] for ContentItemID={Id} (DocId={DocId}) — pages not migrated?",
                    tableName, contentItemId.Value, doc.DocumentId);
                skipped++;
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

        // Return count of rows that now hold the JSON value
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
