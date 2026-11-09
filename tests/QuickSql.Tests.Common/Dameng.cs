using System.Data.Common;
using QuickSql.Providers.Dameng;

namespace QuickSql.TestInfra;

/// <summary>达梦这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/> 同形：
/// <b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>与金仓那一份<em>同一条理由</em>：本机没有任何一台达梦，清单里那一行的程序集名也从未被解析成功过
/// （§13.5 台账里这一行整行「未实测」），所以这里<em>没有</em> <see cref="SqlServer.Driver"/>
/// 的对应物 —— 有了它就会被误读成"这条解析路径验过"。第一次连上真库要补的是连接串解析与真库用例。</para>
///
/// <para>连接串占位符仍然复用 <see cref="SqlServer.OfflineConnectionString" /> 那一份：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第三家不需要第三种假配置。</para></summary>
public static class Dameng
{
    public static DamengProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第三家上与前两家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
