using System.Data.Common;
using QuickSql.Providers.Gbase8c;

namespace QuickSql.TestInfra;

/// <summary>GBase 8c 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/>、<see cref="OceanBase"/>、<see cref="OpenGauss"/>、<see cref="GaussDb"/>、
/// <see cref="Highgo"/>、<see cref="ShenTong"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），
/// 内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 PG 血统的第五颗叶子（§9.1 行 905 的兑现，v0.36 第六笔「点名了分岔、而分岔住在不写的那半句里」
/// 裁决的落地）。存在的理由与另九份同档而多两格：① 它那行点名的"分片语法差异"是本族第一颗落在
/// <em>仓库照发的那一句</em>（<c>CREATE TABLE</c>）之上的分岔，而它住的是句内子句位——所以"发射面之外"
/// 这一档从今天起要读两种：整句不发（神通的 <c>CREATE SCHEMA</c>）与句内缺省（本家的分片子句），
/// 后一种的重开条件握在仓库自己手里；② 它对 IDENTITY 那一问的前件是<em>借来的</em>——行 905 不答那一问，
/// 只把自己指向邻家（"与 openGauss 高度相似"），所以真实例第一问是"像到哪一层"而不是"认不认"。</para>
///
/// <para>本机没有任何一台 8c，清单里那颗 <c>gbase8c</c> 键的 assembly（Npgsql，与瀚高行同一颗名字、
/// 同一个来路）与 <c>Kdbndp</c>、<c>DmProvider</c>、<c>MySqlConnector</c> 一样从未被解析成功过
/// （§13.5 台账整行「未实测」），所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/>
/// 的对应物，有了就会被误读成"这条解析路径验过"。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第十家不需要第十种假配置。</para></summary>
public static class Gbase8c
{
    public static Gbase8cProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在本家与前九家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
