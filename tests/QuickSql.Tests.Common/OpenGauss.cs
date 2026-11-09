using System.Data.Common;
using QuickSql.Providers.OpenGauss;

namespace QuickSql.TestInfra;

/// <summary>openGauss 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/>、<see cref="OceanBase"/> 同形：<b>provider 由调用方直接构造并交进来</b>
/// （§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 PG 血统的第二颗叶子、分叉库第一颗，存在的理由与 <see cref="TiDb"/> 那一份同档：
/// 本机没有任何一台 openGauss（§13.4 授权的三台里没有它，官方镜像在 §13.1 第 4 项点名过而未起），
/// 清单里那颗 <c>opengauss</c> 键的 assembly（Npgsql）走的是金仓行 $comment 里"现场也可以注 Npgsql"
/// 那条兼容臂，而这个名字与 <c>Kdbndp</c> 一样从未被解析成功过（§13.5 台账里这一行整行「未实测」）——
/// 所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/> 的对应物，有了就会被误读成
/// "这条解析路径验过"。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第六家、第七家不需要第六种、第七种假配置。</para></summary>
public static class OpenGauss
{
    public static OpenGaussProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第六家上与前几家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
