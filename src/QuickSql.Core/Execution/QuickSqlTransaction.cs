using System.Data;
using System.Data.Common;
using QuickSql.Transactions;

namespace QuickSql.Execution;

/// <summary>一条真连接 + 一个真 DbTransaction + 环境槽位。构造与释放都归 BeginTransaction 那条同步路径：
/// 连接在这里被独占到事务结束、不再回池——这是事务的正确语义，也是"事务期间少一条可用连接"的代价来源。
/// 嵌套 = SAVEPOINT：在已有环境事务上再开不另起 DbTransaction，而是返回保存点事务，Commit 释放保存点、
/// Rollback 回退到保存点，只有根事务才真正提交/回滚那条 DbTransaction。保存点这句话由驱动发还是由我们发文本，
/// 在创建那一刻定下记账；本类只保证创建与拆除走同一条。环境槽位是 AsyncLocal 且只有同步写入才回到调用方的帧，
/// 所以本类不自己 begin，也不提供任何 async 成员。</summary>
public sealed class QuickSqlTransaction : IQuickSqlTransaction
{
    static readonly AsyncLocal<QuickSqlTransaction?> _ambient = new();

    readonly DbConnection _connection;
    readonly DbTransaction _transaction;
    readonly bool _ownsConnection;

    // ── 嵌套（SAVEPOINT）──────────────────────────────────────────────
    // 保存点事务共享外层的连接与 DbTransaction，不独占地占有它们。
    // _parent 非 null = 这是一只保存点事务，Commit/Rollback 走保存点路径而不是真提交/回滚。
    internal readonly QuickSqlTransaction? _parent;
    readonly string? _savepointName;

    /// <summary>这只保存点事务走的是文本那条路（而不是驱动 DbTransaction.Save）。由 BeginTransaction 在创建那一刻
    /// 定下来传进来，于是"创建它的那条路"就是"拆除它的那条路"：不存在驱动发的保存点被一句文本去 RELEASE
    /// （或反过来）这种跨路拆卸。根事务永远用不到这颗，留默认值即可。</summary>
    readonly bool _textSavepoints;

    // ── 传播（RequiresNew）──────────────────────────────────────────────
    // _suspended 持有被挂起的那条，Finish 时恢复到环境槽位。与 _parent 互斥：保存点事务不挂起任何东西。

    readonly QuickSqlTransaction? _suspended;

    /// <summary>这场事务除了在当前帧里，还钉在自己的 executor 上（由 BindSessionTransaction 置位），
    /// 根事务结束时据此摘钩。只有根事务会拿到 true：保存点事务共用外层的连接与归属，不重复钉。</summary>
    internal bool SessionScoped;

    /// <summary>保存点发号器：每在这条事务上创建一个嵌套保存点就 +1。只有根事务上的这份是权威的——
    /// 保存点事务各持自己的实例字段，发号前必须先解析到根再递增，否则二级嵌套会再发一个与父同名的
    /// 保存点（MySQL 血统上同名删旧的，中层 Commit 撤的就是最里层）。单调递增不回退：被回滚的号码也不再使用，避免名称碰撞。</summary>
    internal int _nextSavepointId;

    /// <summary>根事务构造：持有连接与 DbTransaction 的所有权。cluster 是这场事务钉住的那座集群，
    /// 之后要落别座集群的语句在取连接那一支被拒；suspended 是 RequiresNew 挂起的上一条环境事务，Finish 时恢复；
    /// isolationLevel 记下实际开出来的隔离级别，是保存点门禁的比对依据。</summary>
    internal QuickSqlTransaction(AdoExecutor executor, string cluster, DbConnection connection, DbTransaction transaction, bool ownsConnection = true, QuickSqlTransaction? suspended = null, IsolationLevel isolationLevel = IsolationLevel.Unspecified)
    {
        Executor = executor;
        Cluster = cluster;
        _connection = connection;
        _transaction = transaction;
        _ownsConnection = ownsConnection;
        _suspended = suspended;
        IsolationLevel = isolationLevel;
    }

    /// <summary>保存点事务构造：共享外层的连接、DbTransaction 与那座集群，不占有它们。
    /// textSavepoints 是创建它时走过的那条路（true = 文本，false = 驱动）；
    /// 隔离级别照抄父场：SAVEPOINT 没有自己的隔离级别，根的那一份就是这一支的答案。</summary>
    internal QuickSqlTransaction(QuickSqlTransaction parent, string savepointName, bool textSavepoints)
    {
        Executor = parent.Executor;
        Cluster = parent.Cluster;
        _connection = parent._connection;
        _transaction = parent._transaction;
        _ownsConnection = false;
        _parent = parent;
        _savepointName = savepointName;
        _textSavepoints = textSavepoints;
        IsolationLevel = parent.IsolationLevel;
    }

    /// <summary>这场事务实际开出来的隔离级别。根事务记构造时传的那一颗，保存点事务继承根；
    /// internal：它是保存点门禁的比对依据，不是公开读数。</summary>
    internal IsolationLevel IsolationLevel { get; }

    /// <summary>当前帧可见的环境事务，且必须属于这个 executor。帧里没有、但这只实例钉着一场时回退读那一场：
    /// 装配层在自己的 async 帧里开事务，回到调用方那一帧时槽位已经空了，后面的语句会全落在事务外。
    /// 两个 orm 实例共享同一个 async 帧时，把语句塞进另一家库的事务里不报错、只是数据少一半，
    /// 所以判据只有一条：句柄归这只 executor，或者归它自己钉的那一场。</summary>
    internal static QuickSqlTransaction? For(AdoExecutor executor)
        => _ambient.Value is { } ambient && ReferenceEquals(ambient.Executor, executor)
            ? ambient
            : executor.SessionTransaction;

    /// <summary>不管属不属于谁 —— 只要帧里有环境事务。用于判是否需要创建保存点而不是新开事务。</summary>
    internal static bool HasAmbient => _ambient.Value is not null;

    /// <summary>当前帧可见的环境事务（不限 executor）。保存点创建时需要拿到外层的 DbTransaction。</summary>
    internal static QuickSqlTransaction? Ambient => _ambient.Value;

    /// <summary>清空环境槽位。<c>RequiresNew</c> 挂起当前事务时调用。</summary>
    internal static void ClearAmbient() => _ambient.Value = null;

    internal AdoExecutor Executor { get; }

    /// <summary>这场事务钉住的那座集群。取连接那一支按它判"这条语句能不能进这场事务"，
    /// 公开它是为了让会话侧能在开事务之前给出同一句话（DbContext 与测试都读它）。</summary>
    internal string Cluster { get; }

    internal DbConnection Connection => _connection;

    internal DbTransaction DbTransaction => _transaction;

    /// <summary>这是一只保存点事务而不是根事务：Commit/Rollback 走保存点路径，
    /// 不真正提交/回滚 DbTransaction，也不释放连接。</summary>
    public bool IsSavepoint => _parent is not null;

    public bool Completed { get; private set; }

    /// <summary>由 BeginTransaction 在调用方的同步帧里写入。</summary>
    internal void Install() => _ambient.Value = this;

    public void Commit()
    {
        if (Completed)
            throw new QuickSqlException("事务已经结束（提交或回滚），不能再提交一次。重复 Commit 通常是 finally 里又调了一遍——把它删掉，兜底是 Dispose。");

        if (_parent is not null)
        {
            // 保存点事务：释放保存点，改动并入外层事务。两条路做的是同一件事，见 _textSavepoints。
            if (_textSavepoints)
                Executor.ExecuteSavepoint(this, SavepointStep.Release, _savepointName!);
            else
                _transaction.Release(_savepointName!);

            Finish();
        }
        else
        {
            // Commit 抛（连接断、锁超时）也要收敛：Completed 置位、环境槽位摘掉、连接归还 ——
            // 否则槽里挂着一场"回不去也用不了"的已死事务，同流后续语句继续往死连接上撞。
            try
            {
                _transaction.Commit();
            }
            finally
            {
                Finish();
            }
        }
    }

    /// <summary>回滚。未结束时真的回滚并可以抛驱动错误（锁冲突、连接断了——那是要看见的），
    /// 已结束则直接返回（幂等）。Dispose 兜底走这一口没有例外豁免，
    /// 收口处要"回滚失败也不吞换业务异常"的那一格走 RollbackQuiet。</summary>
    public void Rollback()
    {
        if (Completed)
            return;

        try
        {
            if (_parent is not null)
            {
                // 保存点事务：回退到保存点，只撤销内层改动，外层事务继续。两条路见 _textSavepoints。
                if (_textSavepoints)
                    Executor.ExecuteSavepoint(this, SavepointStep.Rollback, _savepointName!);
                else
                    _transaction.Rollback(_savepointName!);
            }
            else
            {
                _transaction.Rollback();
            }
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>收口专用的回滚：无论成败都不抛，失败把原文当返回值交出去。
    /// 与 Rollback 同一条执行路径（含 finally 里的 Finish），差别只在异常的去向——
    /// 回滚失败绕不出这里，就没有机会盖掉调用方手里的业务异常。</summary>
    internal Exception? RollbackQuiet()
    {
        if (Completed)
            return null;

        try
        {
            Rollback();

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>不调 Commit 即回滚（兜底）。</summary>
    public void Dispose()
    {
        if (!Completed)
            Rollback();
    }

    void Finish()
    {
        Completed = true;

        // 保存点事务：还原外层到环境槽位，不 dispose 共享的 DbTransaction 与连接。
        if (_parent is not null)
        {
            _ambient.Value = _parent;
            return;
        }

        // 根事务：如果有被挂起的环境事务（RequiresNew），恢复它；否则清空槽位。
        _ambient.Value = _suspended;

        // 钉在实例上的那一场：结束即摘钩，否则这只 orm 之后的语句会走进一条已结束的事务里。
        // 谁钉的谁来摘——只有根事务持有实例归属，保存点事务共用外层那一条。
        if (SessionScoped)
            Executor.UnbindSessionTransaction(this);

        _transaction.Dispose();

        // 事务可能复用了 DbContext 绑的连接：那种情况下连接不归事务管——
        // 它还在 AsyncLocal 里，后续语句继续复用。
        if (_ownsConnection)
            ConnectionRelease.Release(_connection);
    }
}
