using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;

namespace QuickSql.Benchmarks;

/// <summary>基准用的实体。<b>不复用 <c>QuickSql.Tests.Common</c> 的那批</b>：那个项目是 xunit.v3 的测试应用，
/// 引它会往基准进程里塞第二个 <c>Main</c> 与一套 runner，而基准要的恰好是"除了被测路径什么都不发生"。
/// 代价是实体形状两边各写一份 —— 这里钉的是<b>性能形状</b>（列数、类型分布），不是解析规则，
/// 解析规则的等价性有 §13.11 的 19 条用例守着，不靠本文件。</summary>
[Table("BenchOrder")]
[Index(nameof(CreatedAt))]
public class BenchOrder
{
    public int Id { get; set; }

    public string? CustomerName { get; set; }

    public int CustomerId { get; set; }

    public decimal Amount { get; set; }

    public BenchState State { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public string? Note { get; set; }
}

[Table("BenchCustomer")]
public class BenchCustomer
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public string? City { get; set; }
}

[Table("BenchItem")]
public class BenchItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string? Sku { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }
}

public enum BenchState
{
    Draft,
    Placed,
    Paid,
    Shipped,
    Cancelled,
}

/// <summary>30 列宽行：§12.8 的 <c>MaterializeWide</c> 与（写路径落地后的）<c>BulkInsert10k</c> 共用这个形状。
/// 列数取 30 不是随手来的 —— 参数预算分块的算例（§12.3 的 <c>chunk(30列)=1900</c>）就按这一档算。</summary>
[Table("BenchWideRow")]
public class WideRow
{
    public int Id { get; set; }

    public string? Code1 { get; set; }

    public string? Code2 { get; set; }

    public string? Code3 { get; set; }

    public string Name1 { get; set; } = "x";

    public string Name2 { get; set; } = "x";

    public string Name3 { get; set; } = "x";

    public string? Note { get; set; }

    public string? Remark { get; set; }

    public string? Tag1 { get; set; }

    public string? Tag2 { get; set; }

    public int Qty1 { get; set; }

    public int Qty2 { get; set; }

    public int Qty3 { get; set; }

    public long Seq { get; set; }

    public long Version { get; set; }

    public decimal Money1 { get; set; }

    public decimal Money2 { get; set; }

    public decimal Money3 { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime SettledAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public Guid Key1 { get; set; }

    public Guid Key2 { get; set; }

    public bool Flag1 { get; set; }

    public bool Flag2 { get; set; }

    public bool Flag3 { get; set; }

    public BenchState State { get; set; }
}
