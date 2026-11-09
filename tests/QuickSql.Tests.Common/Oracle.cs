using System.Data.Common;
using QuickSql.Providers.Oracle;

namespace QuickSql.TestInfra;

/// <summary>Oracle 本尊这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/>、<see cref="OceanBase"/>、<see cref="OpenGauss"/>、<see cref="GaussDb"/>、
/// <see cref="Highgo"/>、<see cref="ShenTong"/>、<see cref="Gbase8c"/>、<see cref="Gbase8s"/>、
/// <see cref="PostgreSql"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做
/// <c>Type.GetType(string)</c>。
///
/// <para>它是 Oracle 血统的第三颗叶子、也是这一族<b>第一颗本尊</b>（v0.37 第三笔，而它作废的不是 §9.1 的一句
/// 表口径、是 <c>OracleProviderBase</c> 类注释第一段那位挂了四版的住户）。存在的理由与另十三份同形而多三格：
/// ① 它是第三颗"正典叶"，而它与前两颗的方向各不相同——MySQL 本尊按这一族<em>最新</em>那一侧写文本、
/// PG 本尊吃血统为<em>最老</em>那一侧攒下的形状，本尊这两格都不是：血统那份默认<em>就是</em>它自家的语法，
/// 所以它是第一颗"零覆写而无事可裁"的正典叶（全文在 <c>OracleProvider</c> 类注释第三、四段）；
/// ② 同族那颗零覆写叶（<see cref="ShenTong"/>）与它逐字同形，于是这一族第一次有<em>两颗</em>真 provider
/// 产同一份文本，而差别只在"为什么没走缝"那一句理由上；
/// ③ <c>DamengDialectTests</c> 里那颗血统探针今天传的正是 <see cref="DbKind.Oracle"/>——此前那十颗探针里
/// 唯一一颗借"有枚举值而无 provider"的库种的（本笔添第十一颗，即本册那颗 <c>OracleLeafProbe</c>），
/// 而本册让影子第一次有了可比的本体。</para>
///
/// <para>本机没有任何一台 Oracle，清单里那颗 <c>oracle</c> 键的 assembly（<c>Oracle.ManagedDataAccess</c>，
/// 而它的全部来路是 <c>OracleProviderBase</c> 类注释里那句"编译期不认识"）与 <c>Kdbndp</c>、
/// <c>DmProvider</c>、<c>MySqlConnector</c>、<c>Npgsql</c> 一样从未被解析成功过（§13.5 台账整行「未实测」），
/// 所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/> 的对应物，
/// 有了就会被误读成"这条解析路径验过"。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第十四家不需要第十四种假配置。</para></summary>
public static class Oracle
{
    public static OracleProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在本家与前十三家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
