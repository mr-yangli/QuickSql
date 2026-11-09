using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;

namespace QuickSql.TestInfra;

/// <summary>级联用例的靶子实体：每对父子代表边解算的<em>一档</em>或<em>一条退出</em>。
/// 旗舰那一（<see cref="Order"/> 的 <c>Items</c>，约定档 + 明细列在子端）已经在 <c>Model.cs</c> 里，
/// 这里补齐另外两档、一种"推不出"、一种"基数对不上"和两种关法。
///
/// <b>这些类型都只被级联用例碰</b>：没有哪一条既有用例枚举过它们，所以加实体不会改动别的黄金样本。</summary>

/// <summary>第一档 + 属性级退出：<c>Card.BoardId</c> 按约定命中，但 <c>[NoCascade]</c> 打在那根导航上 ⇒
/// <c>Add(board)</c> 不带卡片，<c>Remove(board)</c> 也不删卡片（明细要自己管）。</summary>
[Table("QuickSqlBoard")]
public class Board
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Title { get; set; }

    [NoCascade]
    public List<Card>? Cards { get; set; }
}

[Table("QuickSqlCard")]
public class Card
{
    public int Id { get; set; }

    public int BoardId { get; set; }

    [QuickColumn(Length = 32)]
    public string? Text { get; set; }
}

/// <summary>第二档：<c>[ForeignKey]</c> 点名。子端那一列不叫 <c>RackId</c>，靠声明接上。
/// 子端<em>故意没有</em>回指的引用导航 —— 有的话第一档先命中，这一格就测不到第二档了。</summary>
[Table("QuickSqlStorageRack")]
public class Rack
{
    public int Id { get; set; }

    /// <summary>这一列看着多余，却是这张表能被子端<em>看见</em>的前提：一个只有自增主键的实体在
    /// <c>InsertEngine</c> 那里<em>没有任何可写的列</em>，那句 <c>INSERT</c> 只剩 <c>DEFAULT VALUES</c> 一种写法，
    /// 而那一档没接线（§12.3）。真的级联现场里父总是有内容的，所以这里补一列，而不是去补那一档 DDL。</summary>
    [QuickColumn(Length = 32)]
    public string? Label { get; set; }

    [DaSchema.ForeignKey("RackKey")]
    public List<ShelfSlot>? Slots { get; set; }
}

[Table("QuickSqlShelfSlot")]
public class ShelfSlot
{
    public int Id { get; set; }

    public int RackKey { get; set; }

    [QuickColumn(Length = 16)]
    public string? Bin { get; set; }

    /// <summary>第三层：级联有深度，摘行也有深度。这一格存在的唯一理由是要能问出
    /// "从甲那一串里摘掉的一只，它自己手里那一串怎么办" —— 答"跟级联删除走同一条路"才是自洽的，
    /// 而那句话只有三层才判得出来（两层时孙那一档压根不存在）。
    /// 子端那一列叫 <c>SlotId</c> 而父型是 <c>ShelfSlot</c>：约定档（<c>ShelfSlotId</c>）命不中，
    /// 所以这里也要点名 —— 真库那一半要走到"落键"那一步才付这一格的账（离线只走到标删）。</summary>
    [DaSchema.ForeignKey("SlotId")]
    public List<SlotHook>? Hooks { get; set; }
}

[Table("QuickSqlSlotHook")]
public class SlotHook
{
    public int Id { get; set; }

    public int SlotId { get; set; }

    [QuickColumn(Length = 16)]
    public string? Name { get; set; }
}

/// <summary>三档都命不中：子端没有指向父的任何列 ⇒ 级联要落键的那一刻响（裁决④，不静默不级联）。</summary>
[Table("QuickSqlFolder")]
public class Folder
{
    public int Id { get; set; }

    public List<Paper>? Papers { get; set; }
}

[Table("QuickSqlPaper")]
public class Paper
{
    public int Id { get; set; }

    [QuickColumn(Length = 64)]
    public string? Title { get; set; }
}

/// <summary>基数对不上：父是复合主键（两列），而那根导航只点了一列。
/// 级联不替它猜"哪个键进哪一列"—— 猜错写出去的就是一个库里不属于任何父行的键。</summary>
[Table("QuickSqlLedger")]
public class Ledger
{
    [Da.Key]
    public int Book { get; set; }

    [Da.Key]
    public int Line { get; set; }

    [DaSchema.ForeignKey("LedgerRef")]
    public List<LedgerNote>? Notes { get; set; }
}

[Table("QuickSqlLedgerNote")]
public class LedgerNote
{
    public int Id { get; set; }

    public int LedgerRef { get; set; }

    [QuickColumn(Length = 32)]
    public string? Memo { get; set; }
}

/// <summary>类级退出：<c>[NoCascade]</c> 打在这张表上，它手里所有集合导航都不带人。</summary>
[Table("QuickSqlCrate")]
[NoCascade]
public class Crate
{
    public int Id { get; set; }

    public List<Parcel>? Parcels { get; set; }
}

[Table("QuickSqlParcel")]
public class Parcel
{
    public int Id { get; set; }

    public int CrateId { get; set; }
}
