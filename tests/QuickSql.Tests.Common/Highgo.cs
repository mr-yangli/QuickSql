using System.Data.Common;
using QuickSql.Providers.Highgo;

namespace QuickSql.TestInfra;

/// <summary>瀚高 HighGo 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/>、<see cref="OceanBase"/>、<see cref="OpenGauss"/>、<see cref="GaussDb"/> 同形：
/// <b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 PG 血统的第四颗叶子（§9.1 行 907 的兑现），存在的理由与前几家同档而处境独占一档：
/// 厂商行（§9.1）的坑列一颗方言级分岔都没点名（权限模型、审计策略都在方言层之外），所以它既不背
/// openGauss 那笔的【推断】前件、也不像金仓那样有名点过的 <c>SERIAL</c> vs <c>IDENTITY</c> 待裁——
/// 真实例到场的第一问是"这一家'兼容 PG'兼容的是哪一代 PG"（行里没有读数）。本机没有任何一台瀚高，
/// 清单里那颗 <c>highgo</c> 键的 assembly（Npgsql）走的是血统协议自己的驱动（来路全文在清单 $comment
/// 与本家类注释第四段）——与 <c>Kdbndp</c>、<c>DmProvider</c>、<c>MySqlConnector</c> 一样从未被解析成功过
/// （§13.5 台账里这一行整行「未实测」）。所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/>
/// 的对应物，有了就会被误读成"这条解析路径验过"。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第九家不需要第九种假配置。</para></summary>
public static class Highgo
{
    public static HighgoProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在本家与前八家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
