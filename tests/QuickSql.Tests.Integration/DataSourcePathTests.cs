using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§16.4① 读写分离与分库在<b>活库</b>上的那一半（离线那一半在 <c>DataSourceTests</c>）。
///
/// <para><b>两册的分工不能互相顶替，而这一格里顶替的代价比别处大</b>：离线册能钉到
/// <c>Resolve</c> 回了 <c>ConnectionRole.Replica</c>，钉不到那条连接打开的是<em>另一座库</em>。
/// "决策对了、连接没换"这一种错在离线是全绿的 —— 它的症状是主库扛住全部读，要到压测才现形。
/// 所以本册的每一条裁判都问的是<em>库里的内容</em>：从库那座看不见主库刚写的行，主库那座看得见。
/// 只插不删、每条用例带本次运行独占的标签，与 <c>WritePathTests</c>/<c>SplitTablePathTests</c> 同一套姿势。</para>
///
/// <para><b>读写分离那几条为什么只在 SQLite 上量</b>：那一半要的是"一座永远落后的第二座库"，
/// 而本仓库没有任何一台配了复制的实例（§13.4 授权的清单里没有，也不该由一层测试去要求）。
/// SQLite 的"实例"就是本机临时目录里的一颗文件，于是主库与从库是两颗文件、而<em>从库那份内容是手写的替身</em>。
/// 这一格量的是"读确实走了另一条连接、另一座库"，它<em>不</em>量复制延迟 —— 延迟这件事本家测不到
/// （没有承接它的 SPI 槽位），规格 §16.4② 写的正是"所以写过之后当从库还旧着"。</para>
///
/// <para><b>分库那几条在两族上量，而两族给的是两种东西</b>：SQL Server 那一跑是同实例的两座真库
/// （<c>QuickSqlClusterA</c> ∶ <c>QuickSqlClusterB</c>，由 <c>SchemaSyncPolicy.Execute</c> 那条通路自己建出来，
/// 与夹具建 <c>TESTDB</c> 走同一颗 <c>CanCreateDatabase</c>），SQLite 那一跑是两颗文件。
/// 前者是"分库"这个词的字面形状，后者是它的结构形状 —— 两者都要，因为"两座库"最容易糊过去的一格
/// 恰恰是"其实一直在同一座库里换 schema"。其余三家（虚谷 / PG / MySQL）本跑跳过并写明原因：
/// 它们的第二座库要么要一台没批过的实例，要么要一条本层没有的配置通路。</para>
///
/// <para><b>连接串永不进版本库这一条在这里怎么兑现</b>：SQL Server 那两座的串是
/// <see cref="Database.ConnectionString"/> 在运行时改库名改出来的（主机与口令从来不落到任何一行输出或断言里），
/// SQLite 那两颗是 <see cref="Sqlite.ScratchConnectionString"/> 造的临时文件。</para></summary>
public sealed class DataSourcePathTests
{
    /// <summary>每条用例独占的一段标签：文件库按本次运行换名字（所以不必清理），
    /// 真库按它筛行（库名是固定的，只插不删）。</summary>
    readonly string Slot = Guid.NewGuid().ToString("N")[..8];

    string Tag(string part) => $"quicksql-ds/{Slot}/{part}";

    static QuickSqlOrm orm(
        string primary,
        IReadOnlyList<string>? replicas = null,
        IReadOnlyList<ClusterConfig>? clusters = null,
        QuickSqlAop? aop = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = primary,
            Replicas = replicas ?? [],
            Clusters = clusters ?? [],
            Aop = aop ?? new QuickSqlAop(),
            // 建表只由 EnsureTables 显式触发。ForceSchemaSync 开在这里是因为这几座库本来就是本册自造的，
            // 环境名不归它管（与 SplitTablePathTests 那颗 ShardDdl 同一判据）。
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

    /// <summary>把点名的那几张实体在<em>各自所属的那座集群</em>上建出来。<b>这一句就是"DDL 按集群路由"的现场</b>：
    /// 一次调用落在几座库上、每座建了哪几张，是分组的结果而不是用例的 arrange。</summary>
    static void EnsureTables(QuickSqlOrm target, params Type[] entities)
    {
        foreach (var entity in entities)
            _ = target.Schemas.Contract(entity);

        target.SyncStructureAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    static Order Row(string tag) => new()
    {
        CustomerName = tag,
        CustomerId = 4242,
        Amount = 12.5m,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 10, 1, 8, 0, 0),
        Note = tag,
    };

    static ReportRow Report(string tag) => new()
    {
        Metric = tag,
        Value = 1m,
        CapturedAt = new DateTime(2026, 10, 1, 8, 0, 0),
        CustomerId = 4242,
    };

    /// <summary>订阅 <c>Executed</c> 拿到的那份路由读数。<b>记的是 (操作, 路由) 而不是只有路由</b>：
    /// 一次 <c>SaveChanges</c> 在 SQLite 上还带回填自增键那一问，只按事件顺序断言会把那一句也算进去。</summary>
    static (QuickSqlAop Aop, List<(string Operation, DataSourceRoute Route)> Statements) Traced()
    {
        var statements = new List<(string, DataSourceRoute)>();
        var aop = new QuickSqlAop();

        // Executed 是唯一的发射点（AdoExecutor.Succeeded），而它手上一定有那次借用的路由结论 ——
        // 所以"这一句走了哪条连接、按哪一档"在本版是读得出的事实，不是靠翻库猜的东西。
        aop.Executed = e => statements.Add((e.OperationKind, e.Route));

        return (aop, statements);
    }

    static IEnumerable<RouteReason> ReadReasons(List<(string Operation, DataSourceRoute Route)> statements)
        => statements.Where(s => s.Operation == "select").Select(s => s.Route.Reason);

    static IEnumerable<ConnectionRole> ReadRoles(List<(string Operation, DataSourceRoute Route)> statements)
        => statements.Where(s => s.Operation == "select").Select(s => s.Route.Role);

    // ── 读写分离：两颗文件库，一主一从 ────────────────────────────────

    [Fact]
    public void A_default_read_lands_on_the_replica_and_sees_only_what_replication_got()
    {
        Database.JudgeWrittenFor(Database.Target.Sqlite, "主库与从库是两颗自建文件库（没有任何一台授权实例配了复制）");
        Database.Ready();

        var (primary, replica) = Files("split");
        var (aop, statements) = Traced();
        var split = orm(primary, replicas: [replica], aop: aop);

        EnsureTables(orm(primary), typeof(Order));
        EnsureTables(orm(replica), typeof(Order));

        var tag = Tag("read");

        // 写落在主库那一座。会话用完要先关掉：它的写后读帧还开着的话，下面那句默认读会落回主库，
        // 而这一格量的正是"另一条连接、另一座库"，帧没关就等于量错了东西。
        using (var db = new DbContext(split))
        {
            db.Set<Order>().Add(Row(tag));
            db.SaveChanges();
        }

        var viaReplica = split.Select<Order>().Where(o => o.CustomerName == tag).ToList();
        var viaPrimary = split.Select<Order>().Where(o => o.CustomerName == tag).FromPrimary().ToList();

        Assert.Empty(viaReplica);
        Assert.Single(viaPrimary);

        // 两句话合起来才是这一格：<b>同一句读、同一个模型、同一个进程，只差一条连接</b>。
        // 少了 FromPrimary 那一条，"从库里本来没这张表""写根本没落库"这些形状也是空的，照样绿。
        Assert.Equal([RouteReason.ReadReplica, RouteReason.ChainPrimary], [.. ReadReasons(statements)]);
        Assert.Contains(RouteReason.WritePrimary, statements.Select(s => s.Route.Reason));
    }

    [Fact]
    public async Task A_session_released_asynchronously_does_not_leave_the_flow_marked_as_written()
    {
        Database.JudgeWrittenFor(Database.Target.Sqlite, "同一座主库与它的替身从库，差别只在会话怎么关");
        Database.Ready();

        var (primary, replica) = Files("asyncdispose");
        var (aop, statements) = Traced();
        var split = orm(primary, replicas: [replica], aop: aop);

        EnsureTables(orm(primary), typeof(Order));
        EnsureTables(orm(replica), typeof(Order));

        var tag = Tag("async-dispose");

        // 与上一条用例唯一的区别是关它用的是 DisposeAsync。写后读那格长在 AsyncLocal 的一枚戳子上，
        // 而 async 被调方换 AsyncLocal 的值写不回调用方的帧（哪怕排在第一个 await 之前）：
        // DisposeAsync 里那段注销若落在异步帧上，本条流就永久留着"main 写过"，
        // 而会话早已 dispose —— 症状是这条流上后面每一句缺省读都回主库，读写分离静默失效且全绿。
        // 上面那条 A 案例走的是同步 Dispose，那条通路一直是好的，所以它挡不住这一格。
        await using (var db = new DbContext(split))
        {
            db.Set<Order>().Add(Row(tag));
            db.SaveChanges();
        }

        var afterRelease = split.Select<Order>().Where(o => o.CustomerName == tag).ToList();

        Assert.Empty(afterRelease);
        Assert.Equal(RouteReason.ReadReplica, statements[^1].Route.Reason);
    }

    [Fact]
    public void A_session_that_wrote_reads_its_own_row_back_while_an_explicit_replica_stays_stale()
    {
        Database.JudgeWrittenFor(Database.Target.Sqlite, "主从两座的陈旧差是手写的替身，不是量出来的延迟");
        Database.Ready();

        var (primary, replica) = Files("afterwrite");
        var (aop, statements) = Traced();
        var split = orm(primary, replicas: [replica], aop: aop);

        EnsureTables(orm(primary), typeof(Order));
        EnsureTables(orm(replica), typeof(Order));

        var tag = Tag("session");

        using var db = new DbContext(split);

        db.Set<Order>().Add(Row(tag));
        db.SaveChanges();

        var own = db.Set<Order>().Where(o => o.CustomerName == tag).ToList();
        var stale = db.Set<Order>().Where(o => o.CustomerName == tag).FromReplica().ToList();

        // 写过之后那句默认读落回主库：库测不到复制延迟，所以这座集群在本流上写过就当从库还旧着。
        Assert.Single(own);
        // 而显式点了 FromReplica() 的那一句<em>仍然</em>去从库，读到的是旧的那一份 ——
        // 这就是 §16.4② 那一档"显式表态压过写后读"的活体读数：库不再替你兜那份新读旧。
        Assert.Empty(stale);

        Assert.Equal([RouteReason.ReadAfterWrite, RouteReason.ChainReplica], [.. ReadReasons(statements)]);
        Assert.Contains(RouteReason.WritePrimary, statements.Select(s => s.Route.Reason));
    }

    [Fact]
    public void Two_replicas_are_visited_in_the_order_configured()
    {
        Database.JudgeWrittenFor(Database.Target.Sqlite, "两颗从库文件、每颗装着自己那一份内容");
        Database.Ready();

        var (primary, first, second) = ThreeFiles("rotate");
        var tag = Tag("rotate");

        EnsureTables(orm(primary), typeof(Order));
        EnsureTables(orm(first), typeof(Order));
        EnsureTables(orm(second), typeof(Order));

        // 给两颗"从库"各装一份只有它有的内容：第一颗有这一行，第二颗没有。
        // 于是"轮询真的在换连接"的读数是<em>行数</em>，而不是任何一处内部状态。
        using (var seeder = new DbContext(orm(first)))
        {
            seeder.Set<Order>().Add(Row(tag));
            seeder.SaveChanges();
        }

        var (aop, statements) = Traced();
        var split = orm(primary, replicas: [first, second], aop: aop);

        Assert.Single(split.Select<Order>().Where(o => o.CustomerName == tag).ToList());
        Assert.Empty(split.Select<Order>().Where(o => o.CustomerName == tag).ToList());
        Assert.Single(split.Select<Order>().Where(o => o.CustomerName == tag).ToList());

        // 三句读都落在从库角色上（缺省档），差别只在哪一颗 —— 而那一格由上面三行行数说话。
        Assert.Equal([ConnectionRole.Replica, ConnectionRole.Replica, ConnectionRole.Replica],
                     [.. ReadRoles(statements)]);
    }

    [Fact]
    public void Replica_choice_inside_a_transaction_is_refused_on_a_live_connection()
    {
        Database.JudgeWrittenFor(Database.Target.Sqlite, "事务占住的那一条是刚打开的真连接");
        Database.Ready();

        var (primary, replica) = Files("intrx");
        var split = orm(primary, replicas: [replica]);

        EnsureTables(orm(primary), typeof(Order));
        EnsureTables(orm(replica), typeof(Order));

        using var tran = split.BeginAmbientTransaction();

        var raised = Assert.Throws<QuickSqlException>(() => split.Select<Order>().FromReplica().ToList());

        // 同一处判据在离线册也有，但那里的裁判是 <c>Resolve</c> 本身；这里的裁判是<em>那条被事务占住的连接</em>：
        // 换一条连接去读说的就不是这个事务里的数据，而那种句子在活库上照样发得出去、照样返回行。
        Assert.Contains("事务里点了 FromReplica()", raised.Message);
        Assert.Contains("请在这条事务之外读", raised.Message);
    }

    // ── 分库：一座集群一张表，行落在自己那一座 ────────────────────────

    [Fact]
    public void Each_cluster_gets_its_own_tables_and_its_own_rows()
    {
        RequireTwoDatabases();
        Database.Ready();

        var (a, b) = Pair();
        var split = orm(a, clusters: [new ClusterConfig("report", b)]);

        EnsureTables(split, typeof(Order), typeof(ReportRow));

        var orders = NameOf<Order>(split);
        var reports = NameOf<ReportRow>(split);

        // DDL 按集群分组：一次调用，两张表落在两座库里。下面那两格 False 才是"分库"的字面形状——
        // 另一座连<em>表</em>都没有，所以行的归属不必再问：它没有可落的地方。
        Assert.True(HasTable(a, orders), $"缺省集群的那张 {orders} 没落在 A 座上");
        Assert.True(HasTable(b, reports), $"声明在 report 上的 {reports} 没落在 B 座上");
        Assert.False(HasTable(a, reports), $"A 座上出现了 report 集群的 {reports}：分组没按集群走");
        Assert.False(HasTable(b, orders), $"B 座上出现了缺省集群的 {orders}：分组没按集群走");

        var orderTag = Tag("order");
        var reportTag = Tag("report");

        using (var db = new DbContext(split))
        {
            // 一次 SaveChanges 只写一座集群 —— 这就是"按集群分两次保存"那条出路本身。
            db.Set<Order>().Add(Row(orderTag));
            db.SaveChanges();

            db.Set<ReportRow>().Add(Report(reportTag));
            db.SaveChanges();
        }

        // 各数自己那一座：行确实在<em>自己那一座</em>的物理表上，而不是同一座库里换了张表。
        Assert.Equal(1, TaggedCount(a, orders, "CustomerName", orderTag));
        Assert.Equal(1, TaggedCount(b, reports, "Metric", reportTag));
    }

    [Fact]
    public void A_cross_cluster_batch_is_refused_and_leaves_neither_database_touched()
    {
        RequireTwoDatabases();
        Database.Ready();

        var (a, b) = Pair();
        var split = orm(a, clusters: [new ClusterConfig("report", b)]);

        EnsureTables(split, typeof(Order), typeof(ReportRow));

        var orders = NameOf<Order>(split);
        var reports = NameOf<ReportRow>(split);
        var orderTag = Tag("refused-order");
        var reportTag = Tag("refused-report");

        var raised = Assert.Throws<QuickSqlException>(() =>
        {
            using var db = new DbContext(split);

            db.Set<Order>().Add(Row(orderTag));
            db.Set<ReportRow>().Add(Report(reportTag));

            return db.SaveChanges();
        });

        Assert.Contains("一场事务只有一条连接", raised.Message);
        Assert.Contains("分两次 SaveChanges", raised.Message);

        // 离线册钉的是"它在碰连接之前就拒"；这里钉的是<em>库里没有半批</em>：
        // "一批 = 一场事务"那句承诺在活库上的形状就是两格都还是 0，而不是第一格已经进了库。
        Assert.Equal(0, TaggedCount(a, orders, "CustomerName", orderTag));
        Assert.Equal(0, TaggedCount(b, reports, "Metric", reportTag));
    }

    [Fact]
    public void A_statement_for_another_cluster_is_refused_inside_an_open_transaction()
    {
        RequireTwoDatabases();
        Database.Ready();

        var (a, b) = Pair();
        var split = orm(a, clusters: [new ClusterConfig("report", b)]);

        EnsureTables(split, typeof(Order), typeof(ReportRow));

        using var tran = split.BeginAmbientTransaction();

        var raised = Assert.Throws<QuickSqlException>(() => split.Select<ReportRow>().ToList());

        Assert.Contains("钉在集群 main 上", raised.Message);
        Assert.Contains("集群 report", raised.Message);

        // 同一场事务里，本集群那一句照常发得出去：拒的是跨座，不是"事务里不许读"。
        var inside = Tag("inside");
        Assert.Empty(split.Select<Order>().Where(o => o.CustomerName == inside).ToList());
    }

    [Fact]
    public void A_transaction_on_a_second_cluster_is_refused_while_the_first_is_open()
    {
        RequireTwoDatabases();
        Database.Ready();

        var (a, b) = Pair();
        var split = orm(a, clusters: [new ClusterConfig("report", b)]);

        using var outer = split.BeginAmbientTransaction();

        var raised = Assert.Throws<QuickSqlException>(() => split.BeginTransaction("report"));

        // 同一座集群上嵌套是保存点，跨集群的嵌套不是（两阶段与 XA 规格没裁，所以这里不给形状）。
        Assert.Contains("同一座集群里嵌套才是保存点", raised.Message);
        Assert.Contains("请按集群分别开事务", raised.Message);

        // 内层那一座没被开出来，所以 outer 提交时 B 座上不会多任何东西。
        outer.Commit();
    }

    // ── 夹具：两座库与问库的那几句 raw ────────────────────────────────

    static void RequireTwoDatabases()
        => Assert.SkipWhen(
            Database.Family is not (Database.Target.SqlServer or Database.Target.Sqlite),
            $"第二座库在这一家要么要一台没批过的实例、要么要一条本层没有的配置通路（本跑 {Database.Family}）：" +
            "多集群那几条今天只能在 SQL Server（同实例两座真库）与 SQLite（两颗文件）上量。");

    /// <summary>SQL Server 那一跑是同实例的两座真库，名字固定（库留着，行按标签筛）；
    /// SQLite 那一跑是两颗文件。库名从 <see cref="Database.ConnectionString"/> 在运行时改出来，
    /// 所以主机与口令<em>不进本册任何一行</em>。</summary>
    static (string A, string B) Pair()
        => Database.Family == Database.Target.Sqlite
            ? (Sqlite.ScratchConnectionString("ds-shared-a"), Sqlite.ScratchConnectionString("ds-shared-b"))
            : (Rebase("QuickSqlClusterA"), Rebase("QuickSqlClusterB"));

    static string Rebase(string catalog)
    {
        var builder = Database.Driver.CreateConnectionStringBuilder()
                      ?? throw new InvalidOperationException("驱动没有提供连接串构建器，无法只改库名。");

        builder.ConnectionString = Database.ConnectionString;
        builder["Initial Catalog"] = catalog;

        return builder.ConnectionString;
    }

    /// <summary>本次运行独占的几颗文件库。<b>名字里带 slot，所以不删、不复用</b>：SQLite 的连接是池化的，
    /// 删一颗正被占用的文件读起来像"分库坏了"，而换一颗新名字同样得到一座空库。</summary>
    (string Primary, string Replica) Files(string kind)
        => (Scratch(kind, "primary"), Scratch(kind, "replica"));

    (string Primary, string First, string Second) ThreeFiles(string kind)
        => (Scratch(kind, "primary"), Scratch(kind, "replica-1"), Scratch(kind, "replica-2"));

    string Scratch(string kind, string role) => Sqlite.ScratchConnectionString($"ds-{kind}-{role}-{Slot}");

    static string NameOf<T>(QuickSqlOrm target) where T : class => target.Schemas.Contract<T>().Table.Name;

    static int TaggedCount(string connectionString, string table, string column, string tag)
    {
        using var connection = Database.Provider.Connectors.CreateConnection(connectionString);

        connection.Open();

        using var command = connection.CreateCommand();

        // 裁判走 raw 是有意的：问"这一行的物理落点在哪座库"不该用被测的那条路由去问。
        // 标识符是契约里那两张表与那一列的名字（本层自己的拼法），值走参数。
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @tag";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tag";
        parameter.Value = tag;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(command.ExecuteScalar());
    }

    static bool HasTable(string connectionString, string table)
    {
        using var connection = Database.Provider.Connectors.CreateConnection(connectionString);

        connection.Open();

        using var command = connection.CreateCommand();

        // 这一句在 SQLite 上"表不存在"就是 0 行，而在 SQL Server 上问的是目录 —— 两族各一句、
        // 且只在被闸门挡住的那两族之外才走到，所以这里不给"通用写法"。
        command.CommandText = Database.Family == Database.Target.Sqlite
            ? $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'"
            : $"SELECT CASE WHEN OBJECT_ID(N'dbo.{table}', N'U') IS NULL THEN 0 ELSE 1 END";

        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }
}
