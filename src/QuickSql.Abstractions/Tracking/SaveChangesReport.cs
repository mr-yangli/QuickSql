using QuickSql.Aop;

namespace QuickSql.Tracking;

/// <summary>一条 SaveChanges 里做的那件事（按 FK 依赖拓扑排序：先 Insert、再 Update、最后 Delete）。
/// 只有这三档：Unmodified/Detached 的实例不进报告，所以报告里没有第四种操作可表达。</summary>
public enum WriteOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>一次写操作的记账（每实体每操作的受影响行数与生成的 SQL）。
/// StatementCount 与 Rows 分开是因为这两件事在批量合并之前必然不相等：报告侧按脏列集合分桶——
/// 同一批里脏列相同的那些合进一条记录，而语句一条都没合，一个桶的 StatementCount 就等于它装了几条；
/// 只有一个数的报告会让人以为已经合过了。
/// Rows 三档来源各不相同，别把它们相加以后当"库动了多少行"：Insert 上是进语句的行数
/// （带 OUTPUT 的那一档不回库报行数，而回填了键就等于证明了行存在），Update 与 Delete 上是
/// 谓词命中数（不是值真的变了几行）。Columns 给的是 SQL 侧的列名：Insert 是列清单（自增列不在里面），
/// Update 是脏列，Delete 一律为空（那条语句里没有 SET，条件只有主键）。
/// Sql 是语句文本，多块时是第一块的文本而不是全部；逐条的出处是 Aop.Executed，那里才有每一条。</summary>
public sealed record SaveChangesOp(
    WriteOperation Operation,
    string Table,
    int Rows,
    int StatementCount,
    IReadOnlyList<string> Columns,
    string Sql);

/// <summary>一次 SaveChanges 的完整决策记录。与写侧三条链的 Explain() 同一取向：
/// 先给出"发了什么"，再让人自己去对账，所以这里没有"回滚了没有"这种只有执行层知道的事；
/// 失败也不在这里表达——校验或库错的形态是抛（ValidationFailedException 带每行的定位），
/// 事务那一层的"哪半没成功"由 TransactionResult 记。
/// HookReplay 记这一批里改写类钩子实际改过的每一步，只记真改了的，没订阅或"看过但没改"都是空：
/// SaveChanges 发的那些语句是跟踪器自己拼的（并发列的 WHERE 与 OUTPUT 只存在于这一条路上），
/// 三条链的 Explain() 看不见它们；一份说不出"最后执行的那一条被改过几步"的报告，现场排障只能靠猜——
/// SaveChangesOp.Sql 是每一步改完之后的最终文本，两者一对上就是回放。</summary>
public sealed record SaveChangesReport(IReadOnlyList<SaveChangesOp> Operations, IReadOnlyList<QuickSql.Aop.HookTrace> HookReplay)
{
    /// <summary>各档 Rows 之和：UPDATE/DELETE 那一档上是"谓词命中数"而不是"值真的变了几行"。
    /// 报告非空而这里是 0 只证明没有行匹配上主键，不证明语句没发过。</summary>
    public int AffectedRows
    {
        get
        {
            var rows = 0;

            foreach (var operation in Operations)
                rows += operation.Rows;

            return rows;
        }
    }

    /// <summary>发出的语句条数（分块按块计）。</summary>
    public int StatementCount
    {
        get
        {
            var statements = 0;

            foreach (var operation in Operations)
                statements += operation.StatementCount;

            return statements;
        }
    }

    /// <summary>逐行摘要，给人贴进日志。它不是断言：数字与 Operations 同源，只是排好了版式。</summary>
    public override string ToString()
    {
        var text = $"SaveChanges：{Operations.Count} 个操作、{StatementCount} 条语句、受影响 {AffectedRows} 行。";

        foreach (var operation in Operations)
            text += $"\n  {operation.Operation} {operation.Table}：{operation.Rows} 行 / {operation.StatementCount} 条语句"
                  + (operation.Columns.Count > 0 ? $"（列：{string.Join("、", operation.Columns)}）" : "")
                  + $" {operation.Sql}";

        return text;
    }
}
