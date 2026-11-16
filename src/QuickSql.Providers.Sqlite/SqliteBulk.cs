namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的批量声明：高速面只有 L3 多值 VALUES + L5 逐条两档（没有装载通道，分号批也不声明——
/// "看起来收得下"与"一条批被它逐句执行、行数按句回报"之间差的还是那场没跑过的读数）。
///
/// <para><b>VALUES 臂的预算 333（≈ 每条语句 300 颗参数）</b>：不是 SQLITE_MAX_VARIABLE_NUMBER（32766）
/// —— 那颗上限是合法值（树册探针实测 32766 颗收得下、32767 抛 too many SQL variables），但合法不等于快。
/// 2026-11-11 活库三档实测（1000 行 × 29 列 + 一条事务，docs/quicksql/plan-cross-orm-bench.md §4.4）：
/// 每块 1016 行（预算 32766）2467 ms、每块 65 行（2100）189 ms、每块 10 行（333）50.5 ms ——
/// 曲线闭合在 <b>每语句成本 ≈ 250 µs 固定 + 3 ns × 参数数²</b>：Microsoft.Data.Sqlite 把命名参数
/// 映射进语句是按名查找，参数数平方级。极点落在"每条语句 ~100 条、~290 颗参数"，预算 333 恰好
/// 把 29 列宽表压到每块 10 行；窄表随列数自动放宽块行数（333 × 0.9 ÷ 列数），参数数仍 ≤ 300。
/// 那笔"334 块各自 autocommit"的旧账（604 ms 的另一半）由 InsertEngine 的多块收口收——事务合一条。</para>
///
/// <para><b>IN 列表臂按住不动（覆写回保守值）</b>：谓词链的参数在 SQLite 族驱动上是同一颗平方级
/// 绑定价，而它的固定成本比 VALUES 臂低（读侧语句的编排更轻），极点更靠左——树册 §14 实测过按硬上限
/// 翻位 → 宽闭包 45.9 → 164 ms，臂宽 90（保守预算 100 × 9/10）是那条曲线的最低点。写侧 VALUES 臂
/// 没有按名查找以外的形状差，但两条曲线的极点不重合，所以两臂分开——
/// 这就是 <see cref="IBulkStrategy.InListParameterBudget"/> 存在的理由，本类是它的第一颗覆写者。</para></summary>
public sealed class SqliteBulk : IBulkStrategy
{
    public int ParameterBudget => 333;

    public string Describe =>
        "L3 多值 VALUES + L5 逐条（没有装载通道）；VALUES 臂参数预算 333（每条语句 ≤300 颗参数，"
        + "plan-cross-orm-bench.md §4.4 的三档实测曲线），IN 列表臂保持保守值 100（plan-tree-query.md §14）";

    public int InListParameterBudget => ParameterBudgets.Conservative;
}
