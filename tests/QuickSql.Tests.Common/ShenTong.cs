using System.Data.Common;
using QuickSql.Providers.ShenTong;

namespace QuickSql.TestInfra;

/// <summary>神通这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/>、<see cref="OceanBase"/>、<see cref="OpenGauss"/>、<see cref="GaussDb"/>、
/// <see cref="Highgo"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 Oracle 血统的第二颗叶子、这一族第一颗"缝开着而没走过去"的壳（§9.1 行 902 的兑现，
/// v0.36 第五笔「到场、有缝、不覆」裁决的落地）。存在的理由与另八份同档而多一格：前八颗零覆写壳
/// 的"不覆写"里有五颗是被结构决定的（PG 与 MySQL 两族从来没有可覆的缝），本家是不覆写第一次
/// <em>真的是一种选择</em>——缝在、门开、走过没进。它与 <see cref="Dameng"/> 同族而不同形，
/// 差的恰好是那道缝 carry 的 IDENTITY 一句。</para>
///
/// <para>本机没有任何一台神通，清单里那颗 <c>shentong</c> 键的 assembly（Oscar，§9.1 行 902 第四列原词）
/// 与 <c>Kdbndp</c>、<c>DmProvider</c>、<c>MySqlConnector</c> 一样从未被解析成功过（§13.5 台账整行「未实测」），
/// 所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/> 的对应物，有了就会被误读成
/// "这条解析路径验过"。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第九家不需要第九种假配置。</para></summary>
public static class ShenTong
{
    public static ShenTongProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第九家上与前八家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
