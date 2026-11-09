using QuickSql.Execution;
using QuickSql.Generated;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§16.4① 读写分离与分库在<b>离线</b>能判的那一半：路由决策表、集群归属的换算、当场拒的那几处形状、
/// 在册表自己的配置判据、以及"点的只是连接、不改文本"那一条。全程 <see cref="SqlServer.NoDriverFactory"/> ——
/// 这一格里<em>没有一句 SQL 上线</em>。
///
/// <para>与集成册（<c>DataSourcePathTests</c>）的分工和分表那两册同一刀：本册钉的是<b>算出来的结论</b>，
/// 那一册钉的是<b>句子里真的换了连接</b>。"读走到从库"这件事在本册只能证到 <c>Resolve</c> 回了
/// <c>ConnectionRole.Replica</c>，证不到那条连接打开的是另一座库 —— 而读写分离最贵的一种错恰好是
/// "决策对了、连接没换"（症状是主库扛住全部读，压测时才看得见）。两册因此用同一批档名，
/// 那一册在活库上逐档读 <c>CommandLogEvent.Route</c>。</para>
///
/// <para><b>拒绝比产出更难写</b>，理由与 <see cref="SplitTableTests"/> 同一句：这一档的裁决大半是"不做"
/// （跨集群的连接、跨集群的一场事务、按请求换整座集群、权重与延迟探测）。所以每个负样本都断言
/// <em>消息里那条走得通的出路</em>，而不只是断言抛了；而每个负样本都配一条只改归属的正样本，
/// 否则"一律拒"这一版实现也是绿的。</para></summary>
public sealed class DataSourceTests
{
    const string ReplicaA = "Server=offline;Database=replica-a";
    const string ReplicaB = "Server=offline;Database=replica-b";
    const string ReportPrimary = "Server=offline;Database=report";
    const string ArchivePrimary = "Server=offline;Database=archive";

    /// <summary>缺省把那两座具名集群都配上：<see cref="ReportRow"/> 与 <see cref="ArchiveNote"/> 在实体上
    /// 声明的就是它们，用例若各自再配一遍集群清单，"这一条测的是路由"与"这一条测的是配置"就分不开了。
    /// 从库那一颗参数例外 —— 它正是读写分离那半的本体，逐用例给。</summary>
    static QuickSqlOrm orm(IReadOnlyList<string>? replicas = null, IReadOnlyList<ClusterConfig>? clusters = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Replicas = replicas ?? [],
            Clusters = clusters ?? [new ClusterConfig("report", ReportPrimary), new ClusterConfig("archive", ArchivePrimary)],
        });

    static ConnectionSources sources(IReadOnlyList<string>? replicas = null, IReadOnlyList<ClusterConfig>? clusters = null)
        => new(SqlServer.OfflineConnectionString, replicas ?? [], clusters ?? [new ClusterConfig("report", ReportPrimary)]);

    // ── 决策表：顺序就是裁决 ──────────────────────────────────────────

    [Theory]
    // 写：四颗旗子怎么摆都是主库。这一档排在最前面是有意的——它不许被链上表态、事务、从库计数改写。
    [InlineData(RouteIntent.Write, ChainChoice.Auto, false, false, 0, ConnectionRole.Primary, RouteReason.WritePrimary)]
    [InlineData(RouteIntent.Write, ChainChoice.Auto, true, true, 2, ConnectionRole.Primary, RouteReason.WritePrimary)]
    // 链上点名主库：三格自动强制之外的第四格（刚切完复制、批处理尾巴上的核对读）。
    [InlineData(RouteIntent.Read, ChainChoice.Primary, false, false, 2, ConnectionRole.Primary, RouteReason.ChainPrimary)]
    [InlineData(RouteIntent.Read, ChainChoice.Primary, true, false, 2, ConnectionRole.Primary, RouteReason.ChainPrimary)]
    // 自动档：事务内 → 本流写过 → 没配从库 → 从库，四档按这个顺序落。
    [InlineData(RouteIntent.Read, ChainChoice.Auto, true, false, 2, ConnectionRole.Primary, RouteReason.InsideTransaction)]
    [InlineData(RouteIntent.Read, ChainChoice.Auto, false, true, 2, ConnectionRole.Primary, RouteReason.ReadAfterWrite)]
    // 本流写过这一档排在"没配从库"之前：两者的<em>角色</em>相同（都是主库），差别只在读出来的原因。
    // 那一格要的是"这条读是因为写过才落主库"，而不是"因为没得选才落主库"——排查复制延迟时这两种话说的是两件事。
    [InlineData(RouteIntent.Read, ChainChoice.Auto, false, true, 0, ConnectionRole.Primary, RouteReason.ReadAfterWrite)]
    [InlineData(RouteIntent.Read, ChainChoice.Auto, false, false, 0, ConnectionRole.Primary, RouteReason.ReadNoReplicaConfigured)]
    [InlineData(RouteIntent.Read, ChainChoice.Auto, false, false, 2, ConnectionRole.Replica, RouteReason.ReadReplica)]
    // 链上点名从库，且这座集群配了从库。这一档排在"事务内"与"本流写过"之前是有意的：
    // 前者要抛（那句读根本不该换连接），而后者的取舍是"显式表态压过写后读"——调用方点名要从库时，
    // 库不再替他兜那份新读旧，那一份责任此刻在他手上（§16.4② 的读法）。
    [InlineData(RouteIntent.Read, ChainChoice.Replica, false, false, 2, ConnectionRole.Replica, RouteReason.ChainReplica)]
    [InlineData(RouteIntent.Read, ChainChoice.Replica, false, true, 1, ConnectionRole.Replica, RouteReason.ChainReplica)]
    public void The_decision_table_lands_where_the_ruling_says(
        RouteIntent intent, ChainChoice choice, bool inTransaction, bool wroteInFlow, int replicas,
        ConnectionRole role, RouteReason reason)
    {
        var route = DataSourceRouting.Resolve("main", intent, choice, inTransaction, wroteInFlow, replicas);

        Assert.Equal(role, route.Role);
        Assert.Equal(reason, route.Reason);
        Assert.Equal(role == ConnectionRole.Primary, route.IsPrimary);
    }

    [Fact]
    public void The_cluster_name_is_carried_through_untouched()
    {
        // 结论里要带着"哪一座"：调用方可能同时读三座集群，而"这句走了哪条连接"那一格问的就是这座。
        Assert.Equal("report", DataSourceRouting.Resolve("report", RouteIntent.Write, ChainChoice.Auto, false, false, 0).Cluster);
        Assert.Equal("archive", DataSourceRouting.Resolve("archive", RouteIntent.Read, ChainChoice.Auto, false, false, 1).Cluster);
        Assert.Equal(DataSourceRouting.DefaultCluster, DataSourceRouting.Resolve("main", RouteIntent.Read, ChainChoice.Auto, false, false, 1).Cluster);
    }

    [Fact]
    public void A_write_is_primary_even_when_the_chain_is_not_asked_for_it()
    {
        // 上面那两行写档的读法：<c>ChainChoice.Replica</c> 在写意图前面<em>不参与判定</em>。
        // 公开 API 造不出这个组合（从库表态只在 arity 1 的读链上，见 IQuery.FromReplica），
        // 所以这一格是护栏而不是功能：万一将来有人把写侧也接上表态，它不会静默地把写发到从库。
        var route = DataSourceRouting.Resolve("main", RouteIntent.Write, ChainChoice.Replica, true, true, 3);

        Assert.Equal(ConnectionRole.Primary, route.Role);
        Assert.Equal(RouteReason.WritePrimary, route.Reason);
    }

    // ── 两处当场拒：从库表态落不了地的时候不静默改道 ────────────────────

    [Fact]
    public void Asking_for_a_replica_inside_a_transaction_is_refused_with_the_way_out()
    {
        var raised = Checks.Raised<QuickSqlException>(() => DataSourceRouting.Resolve(
            "report", RouteIntent.Read, ChainChoice.Replica, inTransaction: true, writtenInFlow: false, replicaCount: 2));

        // 消息里必须留一条出路（"在这条事务之外读"）：只说"不行"的拒绝会让人去翻文档。
        Assert.Contains("事务里点了 FromReplica()", raised.Message);
        Assert.Contains("请在这条事务之外读", raised.Message);
        Assert.Contains("report", raised.Message);
    }

    [Fact]
    public void Asking_for_a_replica_that_was_never_configured_is_refused_rather_than_silently_reading_the_primary()
    {
        var raised = Checks.Raised<QuickSqlException>(() => DataSourceRouting.Resolve(
            "main", RouteIntent.Read, ChainChoice.Replica, inTransaction: false, writtenInFlow: false, replicaCount: 0));

        // 这一格是本档唯一一处"能落到主库却偏要抛"的：静默落主库的症状不是报错而是容量与延迟，
        // 那条路正是要避开的那一类"看起来能用"。
        Assert.Contains("没有配任何从库", raised.Message);
        Assert.Contains("FromPrimary()", raised.Message);
    }

    [Fact]
    public void The_replica_that_was_not_configured_refuses_at_the_gate_not_at_the_connection()
    {
        // 判据同上一则，但走的是公开链：抛点必须在<em>碰驱动之前</em>。
        // 这条区分是有内容的：<c>NoDriverFactory</c> 被碰到就是 <c>DriverNotAvailableException</c> 那一档红，
        // 而这里要的是那句配置缺失 —— 于是"没配从库"永远不会被读成"机器上没装驱动"。
        // （<c>Checks.Raised</c> 比的是精确类型，所以抛出的是驱动那一档时这一则自己就红。）
        var raised = Checks.Raised<QuickSqlException>(() => orm().Select<Order>().FromReplica().ToList());

        Assert.Contains("没有配任何从库", raised.Message);
    }

    // ── 从库的选取：按配上的顺序轮询 ──────────────────────────────────

    [Fact]
    public void Replicas_are_polled_in_the_order_configured()
    {
        var rotation = sources([ReplicaA, ReplicaB]);

        // 第一句落在<em>清单的第一颗</em>：计数器是 Interlocked 递增<em>后</em>取的值，
        // 不减那一颗就悄悄变成"第一句打第二颗"，而那在没有监控的现场读起来像"配置顺序不管用"。
        Assert.Equal(ReplicaA, rotation.ConnectionStringFor("main", ConnectionRole.Replica));
        Assert.Equal(ReplicaB, rotation.ConnectionStringFor("main", ConnectionRole.Replica));
        Assert.Equal(ReplicaA, rotation.ConnectionStringFor("main", ConnectionRole.Replica));
        Assert.Equal(ReplicaB, rotation.ConnectionStringFor("main", ConnectionRole.Replica));
    }

    [Fact]
    public void A_cluster_without_replicas_answers_the_primary_string_for_both_roles()
    {
        var alone = sources();

        Assert.Equal(0, alone.ReplicaCount("main"));
        Assert.Equal(SqlServer.OfflineConnectionString, alone.ConnectionStringFor("main", ConnectionRole.Primary));

        // 这一格是"不声明就从库"那条路的兜底：角色要到 Replica 才换串，而换不到就还是主库那条 ——
        // 与"这条链没配从库"那一句拒绝合起来，读侧才有两种答案而不是三种。
        Assert.Equal(SqlServer.OfflineConnectionString, alone.ConnectionStringFor("main", ConnectionRole.Replica));
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(1, 3, 1)]
    [InlineData(2, 3, 2)]
    [InlineData(3, 3, 0)]
    [InlineData(7, 3, 1)]
    // 计数器是 <see cref="System.Threading.Interlocked"/> 递增出来的 int，绕回负数那一格必须折成非负：
    // -1 % 3 在 C# 里给 -1，而负的档位是"清单外的一颗从库"（索引越界），不是"最后一颗"。
    // 折法是按无符号低 32 位取模：4294967295 那颗恰好被 3 整除，所以 -1 这一格的答案是 0 而不是 2 ——
    // 断言写的是"落在清单里"，不是"落在最后一颗"。
    [InlineData(int.MinValue, 3, 2)]
    [InlineData(-1, 3, 0)]
    public void The_ordinal_folds_a_wrapped_counter_before_taking_mod(int counter, int replicaCount, int expected)
        => Assert.Equal(expected, DataSourceRouting.ReplicaOrdinal(counter, replicaCount));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Asking_for_an_ordinal_with_no_replicas_is_refused(int replicaCount)
    {
        var raised = Checks.Raised<QuickSqlException>(() => DataSourceRouting.ReplicaOrdinal(1, replicaCount));

        Assert.Contains("至少一颗从库", raised.Message);
        Assert.Contains(replicaCount.ToString(), raised.Message);
    }

    // ── 集群名：一张表属于哪座库 ──────────────────────────────────────

    [Fact]
    public void A_table_without_a_declaration_is_on_the_default_cluster()
    {
        Assert.Equal("main", DataSourceRouting.DefaultCluster);

        // 缺省那一格是本档全部的兼容底座：今天每个实体都从这里回 main，一条语句都不必改。
        Assert.Equal("main", DataSourceRouting.ClusterOf(new TableInfo(null, "QuickSqlOrder")));
        Assert.Equal("report", DataSourceRouting.ClusterOf(new TableInfo(null, "QuickSqlOrder", null, new DataSourceFacts("report"))));
    }

    [Fact]
    public void An_empty_cluster_name_is_refused_rather_than_read_as_main()
    {
        Assert.Equal("report", DataSourceRouting.ValidateName(" report ", "SomeEntity"));

        var blank = Checks.Raised<QuickSqlException>(() => DataSourceRouting.ValidateName("", "SomeEntity"));
        Assert.Contains("名字是空的", blank.Message);
        Assert.Contains("SomeEntity", blank.Message);

        // 只有空白的那一颗与空串同一判据：`"   "` 若被 Trim 成空再放行，症状是"实体在座叫不出名字的库里"。
        Assert.Contains("名字是空的", Checks.Raised<QuickSqlException>(
            () => DataSourceRouting.ValidateName("   ", "SomeEntity")).Message);
    }

    [Fact]
    public void Both_metadata_paths_put_each_table_on_the_cluster_it_declares()
    {
        // 这一则是 §13.11 那条"两侧逐字段相等"在路由那一面的读数：G1 抄的是字符串字面量、G2 抄的是注解实例，
        // 而<em>绝对值</em>在这里再钉一遍 —— 只比两侧一致的话，两侧同时把 report 读成 main 也是绿的，
        // 那条路的症状是"报表库的读全打在主库上"，一种永远不会报错的错。
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);
        var generated = new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet);
        var reflected = new EntitySchemaRegistry(new ContractResolver(provider, null));

        foreach (var (clr, expected) in new (Type clr, string cluster)[]
                 {
                     (typeof(ReportRow), "report"),
                     (typeof(ReportTotal), "report"),
                     (typeof(ArchiveNote), "archive"),
                     (typeof(Order), "main"),
                     (typeof(Customer), "main"),
                 })
        {
            Assert.Equal(expected, DataSourceRouting.ClusterOf(generated.Contract(clr).Table));
            Assert.Equal(expected, DataSourceRouting.ClusterOf(reflected.Contract(clr).Table));
        }

        // 分表 × 分库那一格组合：物理片属于模板所属的那座集群，而不是"分表就退回 main"。
        // 这一张是 TableInfo 第三、第四颗参数同时非空的唯一现场，漏抄任一颗都是一句"片名对、库错"。
        var archived = generated.Contract<ArchiveNote>().Table;

        Assert.Equal("archive", DataSourceRouting.ClusterOf(archived));
        Assert.Equal(SplitKind.Year, archived.Split!.Kind);
        Assert.Equal("QuickSqlArchiveNote_{year}", archived.Name);
    }

    // ── 链上表态：点的只是连接，不改文本 ───────────────────────────────

    [Fact]
    public void Naming_the_role_does_not_change_a_single_byte_of_the_model_or_the_sql()
    {
        var plain = orm([ReplicaA]).Select<Order>();
        var primary = plain.FromPrimary();
        var replica = plain.FromReplica();

        // §13.2 那句"两侧逐字节相等"在这一格的新读者：路由是执行策略，不进规范文本。
        // 若 Role 落进了 StableKey，缓存键会按主/从分成两份，而两份的内容一模一样。
        Assert.Equal(plain.Explain().ModelText, primary.Explain().ModelText);
        Assert.Equal(plain.Explain().ModelText, replica.Explain().ModelText);
        Assert.Equal(plain.ToSql(), primary.ToSql());
        Assert.Equal(plain.ToSql(), replica.ToSql());

        // 三句相等本身还不够：如果模型文本里本来就带着"这条链点过 FromPrimary"那一行，两边就都带着它，
        // 等式照样成立。所以这一格要问的是"文本里没有角色的字样"。
        Checks.Absent("Primary", plain.Explain().ModelText);
        Checks.Absent("Replica", plain.Explain().ModelText);
    }

    [Fact]
    public void A_chain_that_named_a_role_reaches_the_driver_while_a_refusal_stops_before_it()
    {
        // 两句话合起来钉的是抛点：<c>FromPrimary()</c> 决策通过、走到取连接那一格，
        // 于是碰到桩工厂（<c>DriverNotAvailableException</c> 就是它碰驱动的证据）；
        // 而上一则里 <c>FromReplica()</c> 那句没配从库的<em>碰不到</em>驱动。
        // 抛点错了，"这条链没配从库"就与"这台机器没装驱动"读起来是同一句红。
        var touched = Checks.Raised<DriverNotAvailableException>(() => orm().Select<Order>().FromPrimary().ToList());

        Assert.Contains("sqlserver", touched.Message);
    }

    // ── 在册表：配置侧的判据 ──────────────────────────────────────────

    [Fact]
    public void An_unregistered_cluster_is_refused_with_the_roster_in_the_message()
    {
        var raised = Checks.Raised<QuickSqlException>(() => sources().PrimaryConnectionString("warehouse"));

        Assert.Contains("不在册", raised.Message);
        Assert.Contains("main", raised.Message);
        Assert.Contains("report", raised.Message);

        // 报错里点名可选的那几座：这一句的读者是刚把集群名拼错的人，而不是正在读源码的人。
        Assert.Contains("QuickSqlOrmOptions.Clusters", raised.Message);
    }

    [Fact]
    public void A_declared_cluster_that_was_never_configured_refuses_the_statement_before_the_driver()
    {
        // 实体写了 <c>[DataSource("report")]</c> 而配置里没有那一座：本册里最贴现场的一条。
        // 判点在取连接的第一问（<c>ReplicaCount</c> 那一格），所以桩工厂碰不到 —— 红读起来是配置缺失。
        var raised = Checks.Raised<QuickSqlException>(
            () => orm(clusters: [new ClusterConfig("archive", ArchivePrimary)]).Select<ReportRow>().ToList());

        Assert.Contains("report", raised.Message);
        Assert.Contains("不在册", raised.Message);
    }

    [Fact]
    public void The_default_cluster_name_is_reserved_for_the_connection_string()
    {
        var raised = Checks.Raised<QuickSqlException>(() => sources(clusters: [new ClusterConfig("main", ReportPrimary)]));

        Assert.Contains("具名集群里出现了 main", raised.Message);
    }

    [Fact]
    public void A_blank_replica_string_is_refused_rather_than_dropped_from_the_roster()
    {
        var raised = Checks.Raised<QuickSqlException>(() => sources([ReplicaA, "  "]));

        // 静默丢掉那一颗的症状是"轮询少一台"，而它要到容量出问题时才看得见。
        Assert.Contains("有一颗空的", raised.Message);
        Assert.Contains("main", raised.Message);
    }

    [Fact]
    public void A_named_cluster_without_a_primary_string_is_refused_at_startup()
    {
        var raised = Checks.Raised<QuickSqlException>(() => sources(clusters: [new ClusterConfig("report", "")]));

        // 报错里要点名<em>哪一座</em>：清单上三条集群时，"Primary 是空的"这一句没有读者。
        Assert.Contains("report", raised.Message);
        Assert.Contains("没有主库串", raised.Message);
    }

    [Fact]
    public void The_roster_orders_names_and_knows_only_what_was_configured()
    {
        var many = sources(clusters: [new ClusterConfig("report", ReportPrimary), new ClusterConfig("archive", ArchivePrimary)]);

        // 段序按 Ordinal：DDL 分段与报错清单都读这一颗，而"先建哪一座"不该随注册顺序漂。
        Assert.Equal("archive、main、report", string.Join("、", many.ClusterNames));
        Assert.True(many.Knows("report"));
        Assert.False(many.Knows("warehouse"));
        Assert.Equal(0, many.ReplicaCount("report"));
        Assert.Equal(ArchivePrimary, many.PrimaryConnectionString("archive"));
    }

    // ── DI 注册口：那两道闸门的抛点 ───────────────────────────────────

    [Fact]
    public void A_per_request_connection_string_factory_refuses_a_startup_time_replica()
    {
        // 工厂串按请求解析（多租户每库一串），而从库与具名集群是启动期常量。两者配在一起时
        // main 的主库属于当前租户、它的从库属于写配置时那一条串 —— 症状是"读到别的租户的数据"。
        // 这一格不给静默的机会：要按请求换整座集群，就为那一座另建一颗 orm。
        var raised = Checks.Raised<InvalidOperationException>(() => new ServiceCollection().AddQuickSqlOrm(
            DbKind.SqlServer, _ => SqlServer.OfflineConnectionString, SqlServer.NoDriverFactory.Instance,
            b => b.Replicas = [ReplicaA]));

        Assert.Contains("按请求解析", raised.Message);
        Assert.Contains("启动期常量", raised.Message);
    }

    [Fact]
    public void A_per_request_connection_string_factory_refuses_a_startup_time_cluster_roster()
    {
        var raised = Checks.Raised<InvalidOperationException>(() => new ServiceCollection().AddQuickSqlOrm(
            DbKind.SqlServer, _ => SqlServer.OfflineConnectionString, SqlServer.NoDriverFactory.Instance,
            b => b.Clusters = [new ClusterConfig("report", ReportPrimary)]));

        Assert.Contains("按请求解析", raised.Message);
    }

    [Fact]
    public void The_registration_gate_runs_the_same_config_checks_as_the_runtime_roster()
    {
        // 注册期就把 ConnectionSources 建一遍：具名集群里出现 main 这件事，
        // 最便宜的现场是组装根，而不是第一条语句打出去之后。
        var raised = Checks.Raised<QuickSqlException>(() => new ServiceCollection().AddQuickSqlOrm(
            DbKind.SqlServer, SqlServer.OfflineConnectionString, SqlServer.NoDriverFactory.Instance,
            b => b.Clusters = [new ClusterConfig("main", ReportPrimary)]));

        Assert.Contains("具名集群里出现了 main", raised.Message);

        // 正样本：同一道闸门在配得好的那一份配置上不该响。
        var registered = new ServiceCollection().AddQuickSqlOrm(
            DbKind.SqlServer, SqlServer.OfflineConnectionString, SqlServer.NoDriverFactory.Instance,
            b =>
            {
                b.Replicas = [ReplicaA, ReplicaB];
                b.Clusters = [new ClusterConfig("report", ReportPrimary)];
            });

        Assert.NotEmpty(registered);
    }

    // ── 跨集群的读：编译口那道闸门 ───────────────────────────────────

    [Fact]
    public void A_join_across_two_clusters_is_refused_with_both_ways_out()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Select<ReportRow>()
            .InnerJoin<Order>((r, o) => r.Id == o.Id)
            .Select((r, o) => new { Report = r.Id, Order = o.Id })
            .ToSql());

        Assert.Contains("跨集群", raised.Message);
        Assert.Contains("report", raised.Message);
        Assert.Contains("main", raised.Message);
        Assert.Contains("QuickSqlReportRow", raised.Message);
        Assert.Contains("QuickSqlOrder", raised.Message);

        // 两条出路都要在消息里：改声明（同一座集群）与改形状（各查各的、内存里缝）。
        Assert.Contains("同一座集群", raised.Message);
        Assert.Contains("内存缝合", raised.Message);
    }

    [Fact]
    public void An_include_that_walks_into_another_cluster_is_refused()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Select<ReportRow>()
            .Include(r => r.Customer)
            .ToSql());

        // Include 与 JOIN 撞的是同一道闸门：引用导航落成 LEFT JOIN，而一条 SQL 打不到两座库。
        Assert.Contains("跨集群", raised.Message);
        Assert.Contains("QuickSqlCustomer", raised.Message);
    }

    [Fact]
    public void A_join_or_include_inside_one_cluster_is_not_refused()
    {
        // 正样本不可省：闸门若写成"只要出现 [DataSource] 就一律拒"，上面两则照样全绿。
        // 这两句同时钉住另一格——同集群的联表与 Include 走的还是那句 JOIN，路由没有改文本。
        var joined = orm().Select<ReportRow>()
            .InnerJoin<ReportTotal>((r, t) => r.Id == t.ReportRowId)
            .Select((r, t) => new { r.Id, t.Total })
            .ToSql();
        var included = orm().Select<ReportTotal>().Include(t => t.ReportRow).ToSql();

        Checks.Has("INNER JOIN [QuickSqlReportTotal]", joined);
        Checks.Has("LEFT JOIN [QuickSqlReportRow]", included);
    }

    [Fact]
    public void A_query_on_a_declared_cluster_still_reads_the_cluster_it_declares()
    {
        // 这一句看着平淡，它是"集群来自表位而不是来自配置"的证据：同一颗 orm 上读 Order 是 main、
        // 读 ReportRow 是 report，而两者之间没有任何一条链上的表态参与。
        Assert.Equal("report", orm().Schemas.Contract<ReportRow>().Table.Source!.Name);
        Assert.Equal("main", orm().Schemas.Contract<Order>().Table.Source?.Name ?? DataSourceRouting.DefaultCluster);
    }

    // ── 跨集群的写：一场事务只有一条连接 ─────────────────────────────

    [Fact]
    public void A_batch_that_spans_two_clusters_is_refused_before_any_connection()
    {
        var db = new DbContext(orm());

        db.Set<ReportRow>().Add(new ReportRow { Metric = "甲", Value = 1m, CapturedAt = new DateTime(2026, 3, 1) });
        db.Set<Customer>().Add(new Customer { Name = "乙", City = "丙" });

        var raised = Checks.Raised<QuickSqlException>(() => db.SaveChanges());

        Assert.Contains("一场事务只有一条连接", raised.Message);
        Assert.Contains("分两次 SaveChanges", raised.Message);
        Assert.Contains("各开一条会话", raised.Message);
    }

    [Fact]
    public void A_batch_inside_one_cluster_passes_the_gate_and_reaches_the_connection()
    {
        // 与上一则同一形状、只改集群归属：两行都在 report 上就<em>不</em>该拒。
        // 少了这一对，"整批拒"那一版实现（不看归属、只要声明过集群就拒）也是绿的。
        // 红读起来是驱动不可用，而不是配置或裁决 —— 那正是"闸门放行了"的离线读数。
        var db = new DbContext(orm());

        db.Set<ReportRow>().Add(new ReportRow { Metric = "甲", Value = 1m, CapturedAt = new DateTime(2026, 3, 1) });
        db.Set<ReportTotal>().Add(new ReportTotal { Total = 2m, ReportRowId = 1 });

        var touched = Checks.Raised<DriverNotAvailableException>(() => db.SaveChanges());

        Checks.Has("只生成 SQL 的测试不该碰驱动", touched.Message);
    }

    // ── DDL：按集群分组，而单座时文本逐字不变 ─────────────────────────

    [Fact]
    public void The_script_is_segmented_only_when_there_is_more_than_one_cluster()
    {
        var spread = script(orm(SchemaSyncPolicy.Script), typeof(Customer), typeof(ReportRow), typeof(ReportTotal));

        Assert.Contains("-- 集群 main", spread);
        Assert.Contains("-- 集群 report", spread);

        // 段序是 Ordinal 排的：报错与人工执行都在读这一份文本，而"先建哪一座"不该随注册顺序漂。
        Assert.True(spread.IndexOf("-- 集群 main", StringComparison.Ordinal)
                    < spread.IndexOf("-- 集群 report", StringComparison.Ordinal));

        var single = script(orm(SchemaSyncPolicy.Script), typeof(Customer), typeof(Order));

        // 只有一座时<em>不加</em>段头：那一份文本与这一格落地之前逐字相等，§13 的脚本金标基线因此
        // 不必为一件本来不存在的事改口径。少这一句，"分段"就成了对所有调用方的破坏性变更。
        Assert.DoesNotContain("-- 集群", single);
        Assert.Contains("CREATE TABLE", single);
    }

    [Fact]
    public void A_table_on_an_unregistered_cluster_is_refused_before_the_first_connection()
    {
        var only = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            SchemaSync = SchemaSyncPolicy.Script,
            Clusters = [new ClusterConfig("archive", ArchivePrimary)],
        });

        // 实体声明了 report 而配置里没有那一座：最便宜的现场是建表脚本，而不是第一条打往别处的语句。
        // 契约本身不查在册表（<c>ValidateName</c> 只管名字非空），所以这一红的发端就是这一格。
        var raised = Checks.Raised<QuickSqlException>(() =>
        {
            _ = only.Schemas.Contract<Customer>();
            _ = only.Schemas.Contract<ReportRow>();
            return only.SyncStructure();
        });

        Assert.Contains("不在册", raised.Message);
        Assert.Contains("report", raised.Message);
    }

    [Fact]
    public void Split_templates_are_skipped_by_the_whole_database_sync_not_routed()
    {
        var text = script(orm(SchemaSyncPolicy.Script), [typeof(ReportRow), typeof(ArchiveNote)], out var skipped);

        // 分表模板不进整库同步（那是 CreateSplitTables 的活），而它<em>被跳过</em>这件事要看得见。
        Assert.DoesNotContain("QuickSqlArchiveNote_{year}", text);
        Assert.Contains(skipped, line => line.Contains("QuickSqlArchiveNote_{year}", StringComparison.Ordinal));
        Assert.Contains(skipped, line => line.Contains("Year", StringComparison.Ordinal));

        // 这一格在分库之后多一层内容：跳过的那一张同时也是<em>另一座集群</em>的。
        // 分组只走在册的表，而 archive 那张被跳过 ⇒ 脚本里连 archive 的段头都不该出现 ——
        // 静默跳过会把"那座库还没建"读成"表已建"。
        Assert.Contains("QuickSqlReportRow", text);

        // 这一格在分库之后多一层内容：跳过的那一张同时也是<em>另一座集群</em>的，而分组只走在册的表 ——
        // archive 那张被跳过之后，脚本里只剩一座集群，于是段头也一并没有了。
        // 这里不留"那座库的表已建"的读数：它没建，而 skipped 那一格说的就是这件事。
        Assert.DoesNotContain("-- 集群", text);
    }

    static string script(QuickSqlOrm orm, params Type[] entities)
        => script(orm, entities, out _);

    static string script(QuickSqlOrm orm, Type[] entities, out IReadOnlyList<string> skipped)
    {
        foreach (var entity in entities)
            _ = orm.Schemas.Contract(entity);

        var result = orm.SyncStructure();
        skipped = result.SkippedSplitTemplates;

        return result.Ddl;
    }

    static QuickSqlOrm orm(SchemaSyncPolicy policy)
        => new(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            SchemaSync = policy,
            Clusters = [new ClusterConfig("report", ReportPrimary), new ClusterConfig("archive", ArchivePrimary)],
        });
}
