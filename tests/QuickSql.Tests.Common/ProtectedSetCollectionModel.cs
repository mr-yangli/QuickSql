using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;

namespace QuickSql.TestInfra;

/// <summary>「写入口不是公共的」这一族集合导航的靶子实体（§5.7 的 <c>IncludeMany</c> 缝合在
/// <c>public List&lt;TChild&gt; Certs { get; protected set; } = [];</c> 这一形上的形状）。
///
/// <para><b>为什么必须是三颗而不是两颗</b>：缝合那一步旧形状只认公共写口，写口一受保护就抛出
/// 「没有这根导航属性」——两次不实的一句话（成员在场，集合导航也成立）。要钉住它，离线那一半需要
/// <see cref="OpenShop"/> 与 <see cref="GuardedShop"/> 这样<em>只差写口可见性</em>的一对孪生：
/// 同表、同列、同一根点名的边，<c>ToSql()</c> 必须逐字节相同 ——  accessor 的选择进不了发出的文本。
/// <see cref="SeededShop"/> 是量「集合位已经装着值」那一格的：它的初始化器预置一颗，缝合要在
/// 现串上<em>追加</em>而不是整串覆盖 —— 预置的那颗必须活着，捞到的每颗只挂一次。</para>
///
/// <para><b>三颗父型共用同一张表是有意的</b>：<c>OpenShop</c> 与 <code>GuardedShop</code> 逐字节对照
/// 的前提就是两张靶子物理上同一张 —— 列集、表名、外键列全都相同，唯一差的是 CLR 那半颗 set 的可见性。
/// 契约注册表按 CLR 类型分格、按 Orm 实例惰性建，同表名在这里没有第二家来认领；离线侧从不连库，
/// 真库侧 <c>EnsureTable</c> 幂等（先建的还在，后建的就过）。只被本文件的用例碰，不进任何既有黄金样本。</para></summary>
[Table("QuickSqlCertShop")]
public class OpenShop
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }

    /// <summary>公共写口那一臂的孪生：与 <see cref="GuardedShop.Certs"/> 只差 set 的可见性。</summary>
    [DaSchema.ForeignKey("ShopId")]
    public List<ShopCert>? Certs { get; set; }
}

/// <summary>ABP/DDD 的标配形状：集合预初始化成活串，写入口只对自己开放 —— 缝合这一档从前在这一形上
/// 报「没有这根导航属性」，本文件与 <c>MemberAccessors.BindCollection</c> 的那一岔就是为它立的。</summary>
[Table("QuickSqlCertShop")]
public class GuardedShop
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }

    [DaSchema.ForeignKey("ShopId")]
    public List<ShopCert> Certs { get; protected set; } = [];
}

/// <summary>写入口同样受保护，而集合位在建实体那一刻就<em>装着值</em>（初始化器预置一颗）。
/// 这一颗量的是缝合的选择：往现串里追加，而不是拿刚捞的那一批整串顶掉它。</summary>
[Table("QuickSqlCertShop")]
public class SeededShop
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }

    /// <summary>预置那一颗的 <c>No</c> 由用例自己写进期望值；<c>Id = -1</c> 是一个<em>库里不会有</em>
    /// 的键，这样"它是预置的、不是捞回来的"在断言里可分辨。</summary>
    [DaSchema.ForeignKey("ShopId")]
    public List<ShopCert> Certs { get; protected set; } = [new ShopCert { Id = -1, No = "预置的一颗" }];
}

/// <summary>集合里的一只证件：外键列 <c>ShopId</c> 由三颗父型导航上的 <c>[ForeignKey]</c> 点名，
/// 与 <see cref="Rack"/>→<see cref="ShelfSlot"/> 同属点名的那一档（父型名字进不了 <c>ShopId</c> 的约定）。</summary>
[Table("QuickSqlShopCert")]
public class ShopCert
{
    public int Id { get; set; }

    public int ShopId { get; set; }

    [QuickColumn(Length = 32)]
    public string? No { get; set; }
}
