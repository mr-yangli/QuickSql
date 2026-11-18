namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的批量声明：高速面只有 L3 多值 VALUES + L5 逐条两档（没有装载通道，分号批也不声明——
/// "看起来收得下"与"一条批被它逐句执行、行数按句回报"之间差的还是那场没跑过的读数）。
///
/// <para><b>VALUES 臂走编号位置参数 ?N + 预算 150（2026-11-18 改判）</b>：Microsoft.Data.Sqlite 把命名参数
/// 映射进语句是按名查找（sqlite3_bind_parameter_index 逐颗扫），参数数平方级。净室探针实测（1000×29 单事务）：
/// 具名 @p 10 行块 42.8 ms、编号 ?1 同块 23.9 ms；编号 ?N 驱动认（ParameterName="?N" 绑位置 N），匿名 ? 配无名参被拒
/// （ParameterName must be set）。所以 SqliteDialect.ParameterNamer 把批量插入发射口（InsertEngine.EmitChunk）的参数名
/// 改成「? + (下标+1)」，编号 = 发射序 = 绑定序，按序绑定不串位（读侧/单条写仍具名）。
/// <b>预算从 333 收到 150</b>：编号消掉大半平方价后，块宽极点左移——QuickSql 活库扫描（1000×30，BDN）：
/// 333（10 行）29.9 ms/13.57 MB、200（6 行）25.97/13.26、<b>150（4-5 行）23.73/13.14</b>、100（3 行）24.96/13.69，
/// 150 是时间与分配双优的拐点（再小则每块编排与数组分配回升）。组合编号 + 预算 150：QuickSql 从 47 ms 降到 23.7 ms（≈2×）。
/// 旧账备查：具名时代预算 333 的曲线（2026-11-11，每语句 ≈250 µs 固定 + 3 ns×参数数²，极点 ~290 颗）已被编号方案取代。
/// 那笔"334 块各自 autocommit"的旧账（604 ms 的另一半）由 InsertEngine 的多块收口收——事务合一条。</para>
///
/// <para><b>IN 列表臂按住不动（覆写回保守值）</b>：谓词链的参数在 SQLite 族驱动上是同一颗平方级
/// 绑定价，而它的固定成本比 VALUES 臂低（读侧语句的编排更轻），极点更靠左——树册 §14 实测过按硬上限
/// 翻位 → 宽闭包 45.9 → 164 ms，臂宽 90（保守预算 100 × 9/10）是那条曲线的最低点。编号 namer 今天只挂在批量插入
/// 发射口，谓词链仍具名，所以 IN 臂的保守预算不动；两臂分开——
/// 这就是 <see cref="IBulkStrategy.InListParameterBudget"/> 存在的理由，本类是它的第一颗覆写者。</para></summary>
public sealed class SqliteBulk : IBulkStrategy
{
    public int ParameterBudget => 150;

    public string Describe =>
        "L3 多值 VALUES + L5 逐条（没有装载通道）；VALUES 臂编号位置参数 ?N（避 Microsoft.Data.Sqlite 按名查找平方价）"
        + "+ 参数预算 150（4-5 行块，时间与分配双优拐点），IN 列表臂保持保守值 100（plan-tree-query.md §14）";

    public int InListParameterBudget => ParameterBudgets.Conservative;
}
