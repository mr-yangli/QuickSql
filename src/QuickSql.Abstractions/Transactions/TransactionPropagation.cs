namespace QuickSql.Transactions;

/// <summary>事务传播模式。决定 BeginTransaction 在已有环境事务时的行为。</summary>
public enum TransactionPropagation
{
    /// <summary>加入当前环境事务（SAVEPOINT 嵌套）。没有环境事务时新开一条。默认值。</summary>
    Requires,

    /// <summary>挂起当前环境事务，新开一条独立事务。新事务提交或回滚不影响被挂起的那条。
    /// 结束后恢复被挂起的环境事务。</summary>
    RequiresNew,
}
