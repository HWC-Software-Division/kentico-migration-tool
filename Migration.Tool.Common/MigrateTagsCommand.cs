using MediatR;
using Migration.Tool.Common.Abstractions;

namespace Migration.Tool.Common;

/// <summary>
/// Command สำหรับ migrate K13 CMS_TagGroup/CMS_Tag → XbyK Taxonomy/Tag
/// ต้อง run ก่อน MigratePagesCommand เพราะ Pages จะ reference Taxonomy field
/// </summary>
public record MigrateTagsCommand : IRequest<CommandResult>, ICommand
{
    public static readonly int Rank = 1 + MigrateSitesCommand.Rank;

    public static string Moniker => "tags";
    public static string MonikerFriendly => "Tags (Taxonomy)";

    // Tags ต้องรันหลัง Sites เท่านั้น — ไม่ขึ้นกับ PageTypes เพราะต้องรันก่อน --page-types
    public Type[] Dependencies => [typeof(MigrateSitesCommand)];
}
