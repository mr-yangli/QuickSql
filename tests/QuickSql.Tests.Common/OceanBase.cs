using System.Data.Common;
using QuickSql.Providers.OceanBase;

namespace QuickSql.TestInfra;

/// <summary>OceanBase（MySQL 模）这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 MySQL 血统的第三颗叶子，存在的理由与 <see cref="TiDb"/> 那一份<em>同一条而多一格</em>：
/// 本机没有任何一台 OceanBase 实例（§13.4 授权的只有两台 SQL Server 与一台虚谷），清单里那颗 <c>oceanbase</c> 键
/// 与 <c>mysql</c>／<c>tidb</c> 两颗共用同一份 <c>MySqlConnector</c> 程序集名（清单第一次三键一 assembly），
/// 而这个名字从未被解析成功过（§13.5 台账里这一行整行「未实测」），所以这里同样<em>没有</em>
/// <see cref="SqlServer.Driver"/> 的对应物。多出来的那一格是本家独有的：双模库的"模式在连接上决定"
/// 今天没有任何程序入口能问出来（§14 那格参数名单的第二个实例）——这里造的每一颗 provider 都只在主张
/// "假设这条连接在 MySQL 模"，读这些用例时不许把那格读成已验证。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 数到第六家仍然不需要第六种假配置。</para></summary>
public static class OceanBase
{
    public static OceanBaseProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在这一家上与前面几家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
