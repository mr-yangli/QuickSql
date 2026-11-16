namespace QuickSql.Providers;

/// <summary>参数预算的公共常数。放 Abstractions 而不是内核：InListParameterBudget 的覆写者
/// （SQLite 是第一颗）要引用"保守值"这个名字，而它不能反向引用内核。</summary>
public static class ParameterBudgets
{
    /// <summary>Bulk 槽缺位时的兜底预算，也是 IN 列表臂的按住值：没有任何库种事实可依，
    /// 取一个低于所有已知上限的数——宁可多几次 round-trip，也不要撞上一次就整批失败的硬上限。
    /// 内核的 StatementEmitter.ConservativeParameterBudget 改指这里，两处不许各写一个 100。</summary>
    public const int Conservative = 100;
}
