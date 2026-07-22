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
/// to the XbyK content item's DocumentTags text column.
///
/// Storage in XbyK (text approach):
///   class-specific table column (e.g. Plearn_Article.DocumentTags) = longtext (nvarchar(max))
///   Value = raw comma-separated tag names, e.g. "เช็กดวง, ดูดวงรายเดือน, สิ่งมงคลประจำวันเกิด"
///
/// ไม่ใช้ Taxonomy/Term และไม่แตะ CMS_ContentItemTag อีกต่อไป (ไม่ต้องรัน --tags ก่อน)
///
/// Run AFTER --sites --page-types --pages
/// </summary>
public class MigrateTagValuesCommandHandler(
    ILogger<MigrateTagValuesCommandHandler> logger,
    IDbContextFactory<KX13Context> kx13ContextFactory,
    SpoiledGuidContext spoiledGuidContext
) : IRequestHandler<MigrateTagValuesCommand, CommandResult>
{
    public async Task<CommandResult> Handle(MigrateTagValuesCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("==== START: MigrateTagValues (DocumentTags → text column) ====");

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
                tree.NodeSiteId,
                tree.NodeId,
                cls.ClassName
            }
        ).ToList();

        logger.LogInformation("Found {Count} K13 documents with DocumentTags", docsWithTags.Count);

        // ── Step 2: Load ClassName → ClassTableName from XbyK CMS_Class ─────
        var classTableNames = LoadClassTableNames();
        logger.LogInformation("Loaded {Count} class→table mappings", classTableNames.Count);

        // ── Step 3: Verify DocumentTags columns exist in target tables ────────
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

            // ── Verify target table + column ─────────────────────────────────
            if (!classTableNames.TryGetValue(doc.ClassName, out var tableName))
            {
                logger.LogWarning("No class table found in XbyK for class '{Class}' (DocId={DocId}) — skipped",
                    doc.ClassName, doc.DocumentId);
                skipped++;
                continue;
            }

            if (!tablesWithColumn.Contains(tableName))
            {
                logger.LogDebug(
                    "Table [{Table}] has no DocumentTags column — skipped DocId={DocId}",
                    tableName, doc.DocumentId);
                skipped++;
                continue;
            }

            // ── Normalize comma-separated string (trim ช่องว่างรอบแต่ละ tag) ──
            var tagsText = string.Join(", ", doc.DocumentTags!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            if (string.IsNullOrWhiteSpace(tagsText))
            {
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

            // ── UPDATE class-specific table column with raw text ─────────────
            var rowsUpdated = UpdateClassTable(tableName, contentItemId.Value, tagsText);
            if (rowsUpdated > 0)
            {
                updated += rowsUpdated;
                logger.LogInformation(
                    "Updated {Rows} row(s) in [{Table}] for DocId={DocId}: '{Tags}'",
                    rowsUpdated, tableName, doc.DocumentId, tagsText);
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

    // ── UPDATE DocumentTags text in the class-specific table ─────────────────
    // tableName is from CMS_Class.ClassTableName (trusted, not user input)
    private static int UpdateClassTable(string tableName, int contentItemId, string tagsText)
    {
        var updateSql = $@"
            UPDATE [{tableName}]
            SET    DocumentTags = @tags
            WHERE  ContentItemDataCommonDataID IN (
                SELECT ContentItemCommonDataID
                FROM   CMS_ContentItemCommonData
                WHERE  ContentItemCommonDataContentItemID = @id
            )";

        var p = new QueryDataParameters { { "tags", tagsText }, { "id", contentItemId } };
        ConnectionHelper.ExecuteQuery(updateSql, p, QueryTypeEnum.SQLQuery);

        var countSql = $@"
            SELECT COUNT(*) FROM [{tableName}]
            WHERE  ContentItemDataCommonDataID IN (
                SELECT ContentItemCommonDataID
                FROM   CMS_ContentItemCommonData
                WHERE  ContentItemCommonDataContentItemID = @id
            )
            AND DocumentTags = @tags";

        var countDs = ConnectionHelper.ExecuteQuery(countSql, p, QueryTypeEnum.SQLQuery);
        return Convert.ToInt32(countDs.Tables[0].Rows[0][0]);
    }
}
