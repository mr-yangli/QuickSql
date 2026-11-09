using System.Data.Common;
using QuickSql.Providers.Gbase8s;

namespace QuickSql.TestInfra;

/// <summary>GBase 8s（Informix 血统）这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、
/// <see cref="Dameng"/>、<see cref="MySql"/>、<see cref="TiDb"/>、<see cref="OceanBase"/>、<see cref="OpenGauss"/>、
/// <see cref="GaussDb"/>、<see cref="Highgo"/>、<see cref="ShenTong"/>、<see cref="Gbase8c"/> 同形：
/// <b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是第六档，也是这一批里唯一一颗<em>整族新增</em>的叶子（前十一颗里只有 SqlServer、虚谷同档，
/// 余下九颗都挂在某条血统之下）。它与 <see cref="Xugu"/> 那一档共有的两格是：只有一层，所以这份组装根也只交回
/// 一个 <see cref="Gbase8sProvider"/>，<c>MySqlDialectTests</c> 里那枚<em>空壳证据</em>用的血统基类探针在这里
/// 无从提出；以及它带进来的是<em>第四套分页形状</em>，而分页这一格在 §9.4 的账本上此前只有三套。</para>
///
/// <para><b>它多出的那一格与虚谷那一格恰好相反</b>：虚谷的价值是"第三家的字形"（标识符整条与达梦逐字同形），
/// 这一家的价值是<em>本仓库第一次有两颗策略值完全相同的 <see cref="QuickSql.Metadata.IdentifierPolicy"/></em>
/// （<see cref="QuickSql.Metadata.IdentifierPolicy.Gbase8s"/> 与 <c>PostgreSQL</c> 五格参数一字不差，
/// 由 <c>Gbase8sDialectTests</c> 里那句 <c>Assert.Equal</c> 量出来）。<b>同值而不同族</b>是这一句的全部信息量：
/// 两条出处不同的观察各自折向小写，于是"这一格是事实而不是偏好"第一次有了两份独立见证；
/// 而它同时把"接一家=又多一处对读"这条隐含预期<em>倒过来</em>——这一家在寻址之外全异形，
/// 所以对拍链在这里断在一颗叶子身上，而不是又接长一节。</para>
///
/// <para>本机没有任何一台 GBase 8s，清单里那颗 <c>gbase8s</c> 键的 assembly（<c>GBase.DataDB</c>）与
/// <c>Kdbndp</c>、<c>DmProvider</c>、<c>MySqlConnector</c>、<c>Oscar</c> 一样从未被解析成功过
/// （§13.5 台账整行「未实测」），所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/>
/// 的对应物，有了就会被误读成"这条解析路径验过"。<b>这一家还多一道前置：那一行改名之前要先证
/// "Informix 的就是 GBase 8s 的"</b>（本层每一格现在都挂在这一句借来的等价上，记 §14），
/// 所以就算驱动解析通了，它验的也只是"清单里那一行取得出工厂"，<em>不</em>验这一层写的任何一个字。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第十一家不需要第十一种假配置。</para></summary>
public static class Gbase8s
{
    public static Gbase8sProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在本家与前十一家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
