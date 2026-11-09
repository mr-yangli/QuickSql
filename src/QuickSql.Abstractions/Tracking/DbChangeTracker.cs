namespace QuickSql.Tracking;

/// <summary>会话的跟踪器视图（db.ChangeTracker）。
/// 放成抽象类而不是接口，是因为实现侧要带的东西（每类型的节点表、身份映射）没有一个属于调用方要看的部分；
/// 而它必须落在 Abstractions 里，因为 DbContext 的公开属性要有类型可写。
/// 非线程安全：一个会话同一时刻只归一个执行流。</summary>
public abstract class DbChangeTracker
{
    /// <summary>当前被跟踪的实例数（所有实体类型合计）。Detached 的不算。</summary>
    public abstract int Count { get; }

    /// <summary>是否有会被 SaveChanges 写出去的实例。判定含最近一次比较的结论，
    /// 所以"改了又改回来"在这里是 false，而不是"曾经碰过"。</summary>
    public abstract bool HasChanges { get; }

    /// <summary>逐个实例的入口，顺序是"实体类型首次出现序 + 类型内跟踪序"而不是库里的行序。
    /// 这个顺序进了 SaveChanges 的语句序（同类型内按跟踪序），所以日志对账时它是有意义的；
    /// 跨类型的那一段另算——会话按外键依赖排，删除那一档还额外走逆序。</summary>
    public abstract IEnumerable<EntityEntry> Entries();

    /// <summary>只要某一类实体的入口。筛的是入口的泛型参数（那份跟踪器的 T），不是 GetType()：
    /// 一只派生实例若当初是按基类纳入会话的，它就出现在基类的这一档里，而它写出去的也是基类那张表的列——
    /// 与 EntityEntry.EntityType 同一个口径。泛型参数是不变的，所以两份跟踪器不会互相串门。</summary>
    public IEnumerable<EntityEntry<T>> Entries<T>() where T : class
    {
        foreach (var entry in Entries())
            if (entry is EntityEntry<T> typed)
                yield return typed;
    }

    /// <summary>把每个实例的当前值与快照比一遍，据此更新 EntityState 与脏列集合。
    /// 返回本次改动状态的实例数（0 就是"没有任何东西变过"）。
    /// AutoDetectChangesEnabled = false 时它仍会在这里跑；不跑的是链式物化之前那一次。</summary>
    public abstract int DetectChanges();

    /// <summary>把会话清空：所有实例转 Detached，未写出的改动就此丢弃。
    /// 不发任何语句——想丢弃前先确认没有待写的改动，看 HasChanges。</summary>
    public abstract void Clear();
}
