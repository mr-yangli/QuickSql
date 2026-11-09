using System.Globalization;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§16.4 分表在<b>活库</b>上的那一半（离线那一半在 <c>SplitTableTests</c>）。
///
/// <para><b>两册的分工不能互相顶替</b>：离线册钉的是"算出来的名字与发出去的句子"，它自证不了
/// "库里真有这么一张表、行真的进了那一张"。所以这里用<em>同一批日期、同一批桶号</em>，
/// 把片清单交给目录逐张问存在，再把行按片读回来数一遍 —— 分表最贵的一种错恰好是两条都绿的那种：
/// 写入按 <c>…20260115</c> 建表、读取按 <c>…20260112</c> 问表，两边各自都自洽，行从此读不到。
/// 本轮就抓到过一次（<c>SplitRouting.Resolve</c> 少一步 <c>Floor</c>，Week/Season 档），
/// 那一颗在离线是被换算用例撞出来的，但它真正的形状是"数据进了没人看的表"。</para>
///
/// <para><b>裁判为什么大半走 ORM 自己</b>：剪枝正确性最好的证人不是 SQL 文本，是"只给二月那段范围，
/// 一月那一行就不出现"。文本层能证明句子只打了二月的表，证不了三月那张表里确实没有那一行。
/// 只有两处例外用 raw SQL：逐片行数（证明行<em>物理上</em>在那一张上，而不是被 UNION 的算法侥幸对上）
/// 与"库里没有任何名字带占位符的表"（模板漏建的那一种事故在这里现形）。</para>
///
/// <para><b>建片要 DDL 权限，本层默认没有</b>：<c>CreateSplitTables*</c> 走的是
/// <c>SchemaSyncPolicy.Execute</c> 那道闸门，非 Development 环境要 <c>ForceSchemaSync=true</c>
/// （闸门本身由离线册 <c>Creating_shards_without_permission_is_refused_before_any_connection</c> 钉住）。
/// 这里为建片单独开一台 orm 并显式点上那颗开关，读与写仍走 <see cref="Database.Orm"/> ——
/// 不是为了便利，是为了让"这一格知道自己在建表"留在用例里而不是留在环境变量里。</para>
///
/// <para><b>片是哪一跑建的、为什么每条用例还要再要一遍</b>：片表不归夹具管（夹具按类型建表，而分表实体
/// 的<em>类型名不是表名</em>），所以由本册自己建。<c>CreateSplitTables*</c> 是"先问存在再发 DDL"的，
/// 于是每条用例开头再要一次同一份清单不花什么，换来的却是<em>用例之间没有顺序依赖</em> ——
/// xunit 不保证类内顺序，从前有一版把"建片"写成第一条用例的副作用，另几条在它后面跑才绿，
/// 那种绿在换一台机器、换一次并行度时就红了，而红读起来像分表坏了。</para>
///
/// <para><b>只插不删</b>，与 <see cref="WritePathTests"/>/<see cref="BulkPathTests"/> 同一套姿势：
/// 每条用例带本次运行独占的标签，片表本身留着（那是本档唯一一处由被测代码自己建的表，删掉就等于
/// 下一跑没建过），人工清理按 <c>Message/Name LIKE 'quicksql-split/%'</c>。</para></summary>
public sealed class SplitTablePathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    /// <summary>只服务建片那几条：把 DDL 闸门显式掰开，其余一切照 <see cref="Database.Orm"/>。</summary>
    static QuickSqlOrm ShardDdl => new(new QuickSqlOrmOptions
    {
        Provider = Database.Provider,
        ConnectionString = Database.ConnectionString,
        ForceSchemaSync = true,
    });

    readonly string Batch = "quicksql-split/" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Jan = new(2026, 1, 9, 8, 0, 0);
    static readonly DateTime Feb = new(2026, 2, 9, 8, 0, 0);
    static readonly DateTime Mar = new(2026, 3, 9, 8, 0, 0);

    string tag(string part) => $"{Batch}/{part}";

    /// <summary>本册用到的那三个月的片都在。<b>幂等，所以每条用例开头都要一句</b>：写侧要目标表存在，
    /// 而"哪一条用例先跑"不是本册能定的事。</summary>
    static Task MonthsReady()
        => ShardDdl.CreateSplitTablesAsync<ShardLog>(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31), TestContext.Current.CancellationToken);

    static Task BucketsReady(params int[] buckets)
        => ShardDdl.CreateSplitTablesAsync<TenantRow>(buckets, TestContext.Current.CancellationToken);

    static ShardLog Log(DateTime at, string note)
        => new() { Id = Guid.NewGuid(), OccurredAt = at, Message = note, Level = 1, CustomerId = 7 };

    static TenantRow Tenant(int tenantId, string note)
        => new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = note };

    /// <summary>片名 → 一张可以问目录的 <see cref="TableInfo"/>。<b>Split 必须置 null</b>：那张表在库里
    /// 是以实名存在的，而带 Split 的那一颗是模板，拿它去问等于拿模板名去问一张不存在的表。</summary>
    static TableInfo Shard(string name) => Orm.Schemas.Contract<ShardLog>().Table with { Name = name, Split = null };

    static TableInfo Bucket(string name) => Orm.Schemas.Contract<TenantRow>().Table with { Name = name, Split = null };

    // ── DDL：范围就是片清单 ───────────────────────────────────────────

    [Fact]
    public async Task The_range_creates_exactly_those_shards_and_the_second_call_creates_none()
    {
        Database.Ready();

        var first = await ShardDdl.CreateSplitTablesAsync<ShardLog>(
            new DateTime(2026, 1, 1), new DateTime(2026, 3, 31), TestContext.Current.CancellationToken);

        Assert.Equal(["QuickSqlShardLog_202601", "QuickSqlShardLog_202602", "QuickSqlShardLog_202603"], first.Requested);

        // 建了几张 + 已经在几张 = 清单全长：这一格不许有"报上了却没建"的那一种中间态。
        Assert.Equal(3, first.Created.Count + first.AlreadyThere.Count);

        foreach (var name in first.Requested)
            Assert.True(Database.HasTable(Shard(name)), $"建片报交了 {name}，目录里却问不到这张表");

        var again = await ShardDdl.CreateSplitTablesAsync<ShardLog>(
            new DateTime(2026, 1, 1), new DateTime(2026, 3, 31), TestContext.Current.CancellationToken);

        // 幂等是"先问存在再发 DDL"那一颗的全部意义：第二趟一张都不建，而不是撞一条"表已存在"。
        Assert.Empty(again.Created);
        Assert.Equal(3, again.AlreadyThere.Count);

        // 索引一条都不随片建（理由在 EntityContract.ForShardDdl 与规格 §16.4），而结果必须把没建的名点名。
        Assert.Equal(["IX_ShardLog_Level"], first.IndexesNotCreated);
    }

    [Fact]
    public async Task The_hash_entity_creates_the_buckets_it_was_given()
    {
        Database.Ready();

        var result = await ShardDdl.CreateSplitTablesAsync<TenantRow>([0, 3], TestContext.Current.CancellationToken);

        Assert.Equal(["QuickSqlTenantRow_00", "QuickSqlTenantRow_03"], result.Requested);
        Assert.Equal(2, result.Created.Count + result.AlreadyThere.Count);

        Assert.True(Database.HasTable(Bucket("QuickSqlTenantRow_00")));
        Assert.True(Database.HasTable(Bucket("QuickSqlTenantRow_03")));

        // 没要的桶不该被顺手建出来：那一句"默认建全部"在桶档上是同一颗雷（离线钉过文本，这里钉库里）。
        // 证人为什么是 2 号：本册碰桶的只有这一条与下一条，这一条要 0/3、下一条按值 4/5/7 要 0/1/3，
        // 于是 2 是全册唯一一张<em>没有任何一条用例要过的</em>桶 —— 挑 1 或 3 的话，这一句的绿就取决于
        // xunit 先跑了哪一条，而那种红读起来像"分表会多建表"。
        Assert.False(Database.HasTable(Bucket("QuickSqlTenantRow_02")));
    }

    [Fact]
    public void The_template_is_not_a_table_in_the_live_database()
    {
        Database.Ready();

        // 模板名带占位符，它不该在任何一次建表里落到库里（整库同步跳过分表实体那条判据的活库证人）。
        // 前两句是<em>家族无关</em>的（HasTable 内部按家问目录），所以它们不挂在下面那道闸门里 ——
        // 这一条是本册唯一一处"闸门不在第一句"，为的就是那两句在四家都跑。
        Assert.False(Database.HasTable(Orm.Schemas.Contract<ShardLog>().Table));
        Assert.False(Database.HasTable(Orm.Schemas.Contract<TenantRow>().Table));

        Database.JudgeWrittenFor(Database.Target.SqlServer, "这一句问的是 sys.tables 里名字带 { 的那几行，方括号是目录名不是引号形");

        // 名字是自家路由从固定模板算出来的，不是用户输入；这里仍然按参数绑，不用 LIKE 拼串。
        var leaked = Database.Rows(
            "SELECT name FROM sys.tables WHERE name LIKE @pattern ESCAPE '\\'",
            ("@pattern", "%\\{%"));

        Assert.Empty(leaked);
    }

    // ── 写侧：一行进一张，进的是算出来的那一张 ─────────────────────────

    [Fact]
    public async Task A_batch_spanning_three_months_lands_one_row_per_shard()
    {
        Database.JudgeWrittenFor(Database.Target.SqlServer, "逐片数行数那颗 [dbo].[表] 方括号形");

        await MonthsReady();

        var note = tag("months");
        var affected = await Orm.InsertRange([Log(Jan, note), Log(Feb, note), Log(Mar, note)])
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, affected);

        // 逐片各一行。这是"UNION 读得到"之外唯一的一处证人：读侧算对了名字而写侧算错了，
        // 合并读照样能看见三行（都从同一批算错的表里读），而库里其实是 3∶0∶0。
        foreach (var shard in new[] { "QuickSqlShardLog_202601", "QuickSqlShardLog_202602", "QuickSqlShardLog_202603" })
            Assert.Equal(1, CountIn(shard, "[Message]", note));

        var read = await Orm.Select<ShardLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31))
            .Where(o => o.Message == note)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, read.Count);
        Assert.Equal([Jan, Feb, Mar], read.Select(o => o.OccurredAt).OrderBy(d => d).ToArray());
    }

    [Fact]
    public async Task Pruning_to_one_month_hides_the_rows_that_live_in_the_other_two()
    {
        Database.JudgeWrittenFor(Database.Target.SqlServer, "剪枝那一句要靠'二月这张表里就一行'来收尾");

        await MonthsReady();

        var note = tag("prune");

        await Orm.InsertRange([Log(Jan, note), Log(Feb, note), Log(Mar, note)]).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        var february = await Orm.Select<ShardLog>().SplitTables(new DateTime(2026, 2, 1), new DateTime(2026, 2, 28))
            .Where(o => o.Message == note)
            .ToListAsync(TestContext.Current.CancellationToken);

        // 剪枝不是优化而是定义：这一句只打 202602 那一张，所以一三月那两行必须不出现。
        var row = Assert.Single(february);
        Assert.Equal(Feb, row.OccurredAt);

        // 中段那一张里物理上就是这一行（上面读到的两行不在，靠的是这张表里没有它们）。
        Assert.Equal(1, CountIn("QuickSqlShardLog_202602", "[Message]", note));
        Assert.Equal(0, CountIn("QuickSqlShardLog_202602", "[Message]", tag("months")));
    }

    [Fact]
    public async Task A_hash_batch_spreads_over_buckets_and_a_bucket_read_sees_only_its_own_rows()
    {
        Database.JudgeWrittenFor(Database.Target.SqlServer, "逐桶数行数那颗 [dbo].[表] 方括号形");

        await BucketsReady(0, 1, 3);

        var note = tag("buckets");

        // 值故意取 4/5/7 而不是 1/2/3：取模算出的是 0/1/3，而"把值当桶号"会写进一张不存在的 _04 当场炸。
        // 至于"整数键直接取模、不经 GetHashCode()"那一句口径，本册证不了（int 的哈希在两家都等于自身），
        // 它由离线册的死期望数钉住；这里只证行各归其桶。2 号桶这一条刻意不碰，理由写在上面那条用例里。
        var affected = await Orm.InsertRange([Tenant(4, note), Tenant(5, note), Tenant(7, note)])
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, affected);

        foreach (var bucket in new[] { 0, 1, 3 })
            Assert.Equal(1, CountIn($"QuickSqlTenantRow_{bucket:D2}", "[Name]", note));

        var only = await Orm.Select<TenantRow>().SplitTables(1).Where(o => o.Name == note)
            .ToListAsync(TestContext.Current.CancellationToken);

        var tenant = Assert.Single(only);
        Assert.Equal(5, tenant.TenantId);

        var pair = await Orm.Select<TenantRow>().SplitTables(3, 0).Where(o => o.Name == note)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([4, 7], pair.Select(o => o.TenantId).OrderBy(id => id).ToArray());
    }

    // ── UPDATE / DELETE：钉住那一片 ───────────────────────────────────

    [Fact]
    public async Task An_update_pinned_to_one_shard_changes_that_shard_only()
    {
        Database.JudgeWrittenFor(Database.Target.SqlServer, "改中一片而另一片不动，只能逐片数出来");

        await MonthsReady();

        var note = tag("update");

        await Orm.InsertRange([Log(Jan, note), Log(Feb, note)]).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        var affected = await Orm.Update<ShardLog>().SplitTable(Jan).Set(o => o.Level, 42)
            .Where(o => o.Message == note)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);

        Assert.Equal(1, CountWhere("QuickSqlShardLog_202601", "[Level] = 42", note));
        Assert.Equal(0, CountWhere("QuickSqlShardLog_202602", "[Level] = 42", note));

        // 换一片删：另一片那一行必须还在。删错目标表在合并读里读得出来吗？读不出来 —— 它只是少了一行。
        var deleted = await Orm.Delete<ShardLog>().SplitTable(Feb).Where(o => o.Message == note)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal(0, CountIn("QuickSqlShardLog_202602", "[Message]", note));
        Assert.Equal(1, CountIn("QuickSqlShardLog_202601", "[Message]", note));
    }

    // ── 读侧：跨片分页与那道拒绝在活库上的形状 ────────────────────────

    [Fact]
    public async Task Paging_across_shards_walks_the_pages_without_counting_the_total()
    {
        Database.Ready();

        await MonthsReady();

        var note = tag("paging");

        await Orm.InsertRange([Log(Jan, note), Log(Feb, note), Log(Mar, note)]).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        var query = () => Orm.Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31))
            .Where(o => o.Message == note)
            .OrderBy(o => o.OccurredAt);

        var first = await query().ToPagedAsync(1, 2, new PagedOptions(CountMethod.SkipTotal), TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Items.Count);
        Assert.Null(first.Total);
        Assert.True(first.HasNext);

        var second = await query().ToPagedAsync(2, 2, new PagedOptions(CountMethod.SkipTotal), TestContext.Current.CancellationToken);

        // 分页长在外层，不在每片里：每片各取两行会得到六行里的"每片前两行"，而那不是这一页。
        Assert.Single(second.Items);
        Assert.Equal(Mar, second.Items[0].OccurredAt);
        Assert.False(second.HasNext);
    }

    [Fact]
    public async Task A_single_shard_count_runs_while_a_cross_shard_one_refuses_with_the_same_words()
    {
        Database.Ready();

        await MonthsReady();

        var note = tag("count");

        await Orm.InsertRange([Log(Jan, note), Log(Feb, note)]).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        var total = await Orm.Select<ShardLog>().SplitTables(Jan, Jan).Where(o => o.Message == note)
            .CountAsync(TestContext.Current.CancellationToken);

        // 收到一片就是一句普通的聚合：这一格在离线只缺驱动，在活库上缺的东西没有了，于是它必须真数得出数。
        Assert.Equal(1, total);

        var raised = await Assert.ThrowsAsync<QuickSqlException>(
            async () => await Orm.Select<ShardLog>()
                .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 2, 28))
                .Where(o => o.Message == note)
                .CountAsync(TestContext.Current.CancellationToken));

        // 那句拒绝不是"没驱动才轮到这里"：活库上它照旧在编译期说，而且消息与离线同一句（跨片聚合明确拒绝那条裁决）。
        Assert.Contains("跨 2 片不做聚合/分组", raised.Message);
        Assert.Contains("CountMethod.SkipTotal", raised.Message);
    }

    // ── raw 裁判 ──────────────────────────────────────────────────────

    /// <summary>某一张片里带这个标签的有几行。<b>这一句只写给 SQL Server</b>：方括号形在另四家各长得不一样，
    /// 而那些家的"行进了哪一张"由上面的剪枝读与 ORM 自己的数得出。表名与列名都是自家路由与契约算出来的，
    /// 不是用户输入，所以内插可接受（值一律走绑定参数）。</summary>
    static int CountIn(string table, string column, string note)
        => Count(Database.Rows(
            $"SELECT COUNT(*) AS n FROM [dbo].[{table}] WHERE {column} = @m",
            ("@m", note)));

    static int CountWhere(string table, string predicate, string note)
        => Count(Database.Rows(
            $"SELECT COUNT(*) AS n FROM [dbo].[{table}] WHERE [Message] = @m AND {predicate}",
            ("@m", note)));

    static int Count(List<Dictionary<string, string>> rows)
        => int.Parse(rows[0]["n"], CultureInfo.InvariantCulture);
}
