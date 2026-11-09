namespace QuickSql;

/// <summary>一只会话同时只许一个操作在飞的那颗闸门（EF Core 的 ConcurrencyDetector 是同一条语义）。
/// Enter 用一颗 Interlocked 整数抓闸，抓不到（上一个操作还在飞）就抛——拒绝必须是一句听得懂的话，
/// 而不是跟踪器深处一颗随机 NRE（并发写更险：双倍落库且全程无异常，只有对账才看得见）。
/// 两句实话：尽力检测——上一个操作收尾之后才进场的第二个操作抓不到（那是顺序使用，本来就该放行）；
/// 抓到即拒——抓到的那一次两边语义都确定。结论不因闸门存在而松动：不要共用会话，而不是指望闸门兜底。
/// 闸口收在七个读出口与两个写口（读侧七个公开执行口，FirstAsync 走 CappedAsync 不另设闸；
/// 写侧 SaveChangesAsync 与 LoadAsync）。根链没有会话，天然不进闸。闸口嵌套不设重入：七个读口彼此不调用。</summary>
sealed class SessionGate
{
    int _busy;

    /// <summary>进场。已有操作在飞就抛：同一只会话不接受第二个并发操作。</summary>
    public void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new QuickSqlException(
                "同一只 DbContext 上开出了第二个操作，而上一个还没有完：DbContext 不是线程安全的，"
                + "并发请各用各的会话（各 new 各的，或各开各的流）。这颗闸门是尽力检测：抓到即拒，"
                + "但不保证抓到所有重叠 —— 所以不要共用，而不是指望它兜底。");
    }

    /// <summary>出场。与  一一配对（try/finally 那一对）。</summary>
    public void Exit() => Interlocked.Exchange(ref _busy, 0);
}
