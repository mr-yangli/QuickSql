using System.Data.Common;
using QuickSql.Providers.GaussDb;

namespace QuickSql.TestInfra;

/// <summary>GaussDB 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/>、<see cref="OceanBase"/>、<see cref="OpenGauss"/> 同形：
/// <b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 PG 血统的第三颗叶子、第二颗分叉库壳（§9.1 的 openGauss/GaussDB 那一<em>共行</em>的第二颗兑现，
/// v0.36 第二笔「到场即照本行再挂」裁决的落地），存在的理由与 <see cref="OpenGauss"/> 那一份同档而更薄一档：
/// 本机没有任何一台 GaussDB，清单里那颗 <c>gaussdb</c> 键的 assembly（Npgsql）走的是金仓行 $comment
/// 里那条兼容臂——与 <c>Kdbndp</c>、<c>DmProvider</c>、<c>MySqlConnector</c> 一样从未被解析成功过
/// （§13.5 台账里这一行整行「未实测」），而 GaussDB 厂商自己分发的那份托管驱动连名字都没有入账读数。
/// 所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/> 的对应物，有了就会被误读成
/// "这条解析路径验过"。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第七家、第八家不需要第七种、第八种假配置。</para></summary>
public static class GaussDb
{
    public static GaussDbProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第七家上与前几家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
