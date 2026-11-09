using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;

namespace QuickSql.TestInfra;

/// <summary>链上「旁边那一根」的靶子实体（§5.7 的 <c>IncludeMany(…).ThenInclude(…)</c>）：
/// 托盘 → 明细一层<em>集合</em>导航，明细 → 物资一根<em>引用</em>导航。
///
/// <para><b>为什么另起一张而不并进 <see cref="CascadeModel"/> 那一架一位一钩</b>：那一串三层全是集合边，
/// 每一层的父端都是上一层刚缝出来的那一批，而这一次要判的是「同一批子实体手里那根引用边」——
/// 靶子上必须同时有一根集合边和一根挂在集合子型上的引用边，那一串里挑不出这一形。
/// 并进 <c>Model.cs</c> 也不合适：那一册的列序本身就是黄金样本的一部分，加实体等于给整片样本添未测的账。</para>
///
/// <para><b>这三颗类型只被 <c>ThenInclude</c> 那一条链碰</b>：没有哪一条既有用例取过它们的契约，
/// 所以它们不进任何既有黄金样本（契约注册表是按 Orm 实例惰性建的，不碰就不在场）。</para>
///
/// <para>列形刻意留了两格坑：<see cref="PalletLine.MaterialId"/> 是非空 <c>int</c>，于是"这一行没有物资"
/// 在库里读作 <c>0</c> 而不是 <c>NULL</c> —— 引用那一档的加载器必须能让"键在库里配不到行"落回
/// <c>Material = null</c>，而不是把明细整行筛掉；<see cref="PalletLine.PalletId"/> 走的是
/// 约定档（<c>{父类型名}Id</c>），与架→位那颗点名的边分属两档。</para>
[Table("QuickSqlPallet")]
public class Pallet
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Label { get; set; }

    public List<PalletLine>? Lines { get; set; }
}

/// <summary>集合里的一只明细：它既是下一层集合加载的<em>子端</em>，又是引用加载那一档的<em>父端</em>
/// （<c>ThenInclude(l =&gt; l.Material)</c> 收的就是这一只）。</summary>
[Table("QuickSqlPalletLine")]
public class PalletLine
{
    public int Id { get; set; }

    public int PalletId { get; set; }

    [QuickColumn(Length = 16)]
    public string? Name { get; set; }

    /// <summary>引用导航的外键列。按约定 <c>{导航属性名}Id</c> 命中 <see cref="Material"/>（§10.2 的约定层），
    /// 与 <c>Order.CustomerId</c> 同一形。<b>0 = 这一行没有物资</b>，见册首那段。</summary>
    public int MaterialId { get; set; }

    public Material? Material { get; set; }
}

/// <summary>明细手里的那一只物资：只在这条链上以<em>引用</em>的方向出现，没有集合导航往它挂。</summary>
[Table("QuickSqlMaterial")]
public class Material
{
    public int Id { get; set; }

    [QuickColumn(Length = 16)]
    public string? Spec { get; set; }
}
