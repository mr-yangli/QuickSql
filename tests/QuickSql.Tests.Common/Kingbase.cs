using System.Data.Common;
using QuickSql.Providers.Kingbase;

namespace QuickSql.TestInfra;

/// <summary>金仓这一系的组装根，与 <see cref="SqlServer"/> 同形：<b>provider 由调用方直接构造并交进来</b>
/// （§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它存在的唯一理由是"第二个方言族在离线侧可测"这一件事，所以这里<b>没有</b> <see cref="SqlServer.Driver"/>
/// 的对应物：本机没有任何一台金仓，清单里那一行的程序集名也从未被解析成功过（§13.5 台账里的「未实测」）。
/// 等现场第一次连上真库，这一格要补的是连接串解析与真库用例，而不是在这里先猜一个默认值。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString" /> 那一份：这条串永远不被
/// 任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红），
/// 为它造第二个"看着像真配置的假配置"只会多一个会被误当成现场配置的字符串。</para></summary>
public static class Kingbase
{
    public static KingbaseProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在金仓这一系上与 T-SQL 那一系享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
