using System.Data.Common;
using QuickSql.Providers.ClickHouse;

namespace QuickSql.TestInfra;

/// <summary>ClickHouse 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Xugu"/>、<see cref="Gbase8s"/>、
/// <see cref="Sqlite"/>、<see cref="DuckDb"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），
/// 内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是单层 provider 的第二颗【文档口径行】（继 DuckDb 而来）。<b>【实测·2026-10-20】第一台引擎
/// 到场（server 26.9.21.9 ∶ 驱动 7.14.0）</b>：驱动解析、引擎连通、golden 十份里九份逐字回放过
///（§13.5 第十七本台账第三笔）；不过离线册这里<em>仍然没有</em> <see cref="SqlServer.Driver"/>、
/// <see cref="Sqlite.Driver"/> 的对应物 —— 离线册拿 <see cref="SqlServer.NoDriverFactory"/>，
/// 碰驱动就是 §3 那条约束的红；活库那一侧走的是探针（<c>obj/logs/v39-ch-*.md</c>），
/// 不是这里的任何一条。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：离线侧这条串永远不被任何驱动解析
///（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。本家的"建库"整格不可走
///（provider 侧 <c>CanCreateDatabase=false</c> —— 引擎型库没有"锚点库"那一档）。那颗占位串要不要带
/// 库名一问【实测·2026-10-20 活库】已有答案：<c>Database=…</c> 被驱动认账（探针把
/// <c>Database=quicksql_probe</c> 点进去，非限定名当场落那座库；另两颗学到的：用户名键是
/// <c>Username=</c> —— <c>User=</c>/<c>User ID=</c> 被静默无视、按缺省用户去认证而失败）。</para></summary>
public sealed class ClickHouse
{
    public static ClickHouseProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第十家上与前面九家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
