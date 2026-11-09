namespace QuickSql.Transactions;

/// <summary>事务句柄。同步 IDisposable，不是 IAsyncDisposable，也没有 await 形式的入口：ADO.NET 侧
/// BeginTransaction 与 Commit/Rollback 根本没有异步重载，而 AsyncLocal 依赖被调方同步写入才能回到调用方的帧，
/// await orm.TransactionAsync() 会得到"对象拿到了、后面的写入却没进事务"的句柄。begin 必须同步，
/// 恢复槽位的 Dispose 也必须同步（异步 Dispose 在另一个帧里执行，等于没恢复）。</summary>
public interface IQuickSqlTransaction : IDisposable
{
    /// <summary>已提交或已回滚。结束之后 Commit 抛、Rollback 静默（幂等）。</summary>
    bool Completed { get; }

    /// <summary>SAVEPOINT 嵌套事务（true）还是根事务（false）。
    /// 嵌套事务的 Commit 释放保存点、Rollback 回滚到保存点，只有根事务真正提交或回滚底层连接。</summary>
    bool IsSavepoint { get; }

    void Commit();

    void Rollback();
}
