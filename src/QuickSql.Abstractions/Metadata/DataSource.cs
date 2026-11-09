namespace QuickSql.Metadata;

/// <summary>一条连接在这座集群里扮的角色。写侧只有 Primary 一条路：
/// 从库上的写在任何一家都是"要么报错要么静默丢失"，本家不给它一个能表达出来的形状。</summary>
public enum ConnectionRole
{
    Primary,
    Replica,
}

/// <summary>链式侧对角色的显式表态。Auto 才是交给 DataSourceRouting 的那一档。</summary>
public enum ChainChoice
{
    Auto,
    Primary,
    Replica,
}

/// <summary>这条语句在库里干的是哪一档。路由只认这一颗与链式表态，不认操作名字符串——
/// ExecutionTag.Operation 是给钩子与日志读的（"load-ref"、"concurrency-read" 那一族），
/// 拿它当路由判据等于把公开文本格式变成正确性依赖。</summary>
public enum RouteIntent
{
    Read,
    Write,
}

/// <summary>落在主档上的原因。每一档都要能被离线用例逐字断言——读写分离最难验收的部分正是
/// "这句话到底走了哪条连接、为什么"，而路由读数比翻库便宜得多。</summary>
public enum RouteReason
{
    /// <summary>写语句，主库，没有第二档。</summary>
    WritePrimary,

    /// <summary>链上点了 FromPrimary()。</summary>
    ChainPrimary,

    /// <summary>链上点了 FromReplica()，而这座集群配了从库。</summary>
    ChainReplica,

    /// <summary>事务里的读。不换连接：换一条连接去读就不是这个事务里的读，
    /// 未提交的自己看不见、别人看不见的自己看见了，都是。</summary>
    InsideTransaction,

    /// <summary>同一条执行流上这座集群写过，之后的读落主库。这一档是本家对复制延迟唯一的让步方式：
    /// 库测不到延迟，所以只要写过就当从库还旧着。</summary>
    ReadAfterWrite,

    /// <summary>这座集群一个从库都没配，读只能落主库。</summary>
    ReadNoReplicaConfigured,

    /// <summary>缺省档：读走从库。</summary>
    ReadReplica,
}

/// <summary>实体属于哪一座集群的声明，活在 TableInfo.Source 这一格里。
/// 只有名字、没有连接串：连接串归 QuickSqlOrmOptions，实体只点名它要哪一座。
/// 这条分工是"真库连接串永不进版本库"那一条在类型层的样子。</summary>
public sealed record DataSourceFacts(string Name);

/// <summary>一次路由的结论：去哪座集群、用哪个角色、为什么。</summary>
public readonly record struct DataSourceRoute(string Cluster, ConnectionRole Role, RouteReason Reason)
{
    public bool IsPrimary => Role == ConnectionRole.Primary;
}

/// <summary>集群名与角色的换算。全仓库只有一份实现：读侧、写侧、装载通道、事务与 DDL 都从这里取，
/// 与 SplitRouting 同一条理由——"同一句读在两个地方算出两个角色"这种偏差没有能发生的形状。</summary>
public static class DataSourceRouting
{
    /// <summary>没声明集群的实体所属的那一座，也是 QuickSqlOrmOptions.ConnectionString 那一条串所属的那一座。</summary>
    public const string DefaultCluster = "main";

    /// <summary>一张表属于哪座集群。缺省即 DefaultCluster，所以今天全部实体都在 main 上，
    /// 而 main 就是那条唯一的连接串——不声明的调用方行为一字不变。</summary>
    public static string ClusterOf(TableInfo table) => table.Source?.Name ?? DefaultCluster;

    /// <summary>校验一个集群名：不许空、不许只有空白。DefaultCluster 不许被实体再声明一遍
    /// （那一格由 QuickSqlOrm 的启动判据管：它比的是"在册的具名集群"，而这里是"用户写了什么"）。</summary>
    public static string ValidateName(string name, string entityName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new QuickSqlException($"实体 {entityName} 声明的数据源名字是空的；要么给一个具名集群，要么整条声明都不要写");

        return name.Trim();
    }

    /// <summary>一次路由的判定。顺序就是裁决：写即主库；链上点名主库即主库；链上点名从库则
    /// 事务里当场拒（那句读根本不该换连接）、没配从库也当场拒（"你要从库而这座集群没有"不该静默落主库，
    /// 那正是"看起来能用"那一类）、否则从库；剩下自动档按"事务内、本流写过、没配从库、从库"逐档落。</summary>
    public static DataSourceRoute Resolve(string cluster, RouteIntent intent, ChainChoice choice, bool inTransaction, bool writtenInFlow, int replicaCount)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (intent == RouteIntent.Write)
            return new DataSourceRoute(cluster, ConnectionRole.Primary, RouteReason.WritePrimary);

        if (choice == ChainChoice.Primary)
            return new DataSourceRoute(cluster, ConnectionRole.Primary, RouteReason.ChainPrimary);

        if (choice == ChainChoice.Replica)
        {
            if (inTransaction)
                throw new QuickSqlException($"集群 {cluster} 的事务里点了 FromReplica()：事务内的读必须落在被这条事务独占的那条连接上，换一条连接去读说的就不是这个事务里的数据。要去从库请在这条事务之外读。");

            if (replicaCount == 0)
                throw new QuickSqlException($"集群 {cluster} 没有配任何从库，FromReplica() 落不了地。要么在 QuickSqlOrmOptions 里给它配从库，要么这处改 FromPrimary() 或去掉表态。");

            return new DataSourceRoute(cluster, ConnectionRole.Replica, RouteReason.ChainReplica);
        }

        if (inTransaction)
            return new DataSourceRoute(cluster, ConnectionRole.Primary, RouteReason.InsideTransaction);

        if (writtenInFlow)
            return new DataSourceRoute(cluster, ConnectionRole.Primary, RouteReason.ReadAfterWrite);

        return replicaCount == 0
            ? new DataSourceRoute(cluster, ConnectionRole.Primary, RouteReason.ReadNoReplicaConfigured)
            : new DataSourceRoute(cluster, ConnectionRole.Replica, RouteReason.ReadReplica);
    }

    /// <summary>从库的选取：按配上的顺序轮询。没有权重、没有延迟探测：那两件事都要 SPI 槽位，
    /// 而 IDbProvider 的子 SPI 里没有一格承接库侧延迟，所以本家不假装会挑"最不老的那一台"。</summary>
    public static int ReplicaOrdinal(int counter, int replicaCount)
    {
        if (replicaCount <= 0)
            throw new QuickSqlException($"从库计数要求至少一颗从库，实际是 {replicaCount}");

        // 取模前折成非负：计数器是 Interlocked 递增出来的，绕回 int.MinValue 时 -1 % n 会给出负的档位。
        return (int)(((long)counter & uint.MaxValue) % replicaCount);
    }
}
