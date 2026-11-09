using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using QuickSql.Core.Schema;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Write;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§16.4 分表在<b>离线</b>能判的那一半：换算、启动期判据、读侧的选片与拒绝、写侧的按片切块、
/// 跟踪器互斥、按范围建片。全程 <see cref="SqlServer.NoDriverFactory"/> —— 这一格里<em>没有一句 SQL 上线</em>，
/// 哪个用例把桩工厂碰响了就是"元数据或发射层混进了连接依赖"。
///
/// <para>与集成册的分工（<c>SplitTablePathTests</c>）：本册钉的是<b>算出来的名字与发出去的句子</b>，
/// 那一册钉的是<b>库里真有那么一张表、行真的进了那一张</b>。"片名两边算得一样"这件事不能靠本册自证 ——
/// 它自证的是本册自己；所以两册用同一个日期、同一个桶号、同一片清单，集成册在真库上按那份清单逐张问存在。</para>
///
/// <para><b>为什么拒绝比产出更难写</b>：这一档的裁决大半是"不做"（跨片聚合、默认全片扫、空分片键当作今天、
/// 按片改索引名）。一句拒绝的价值在消息里有没有那条走得通的路，所以每个负样本都断言<em>消息点名的那颗出口</em>，
/// 而不只是断言抛了。</para></summary>
public sealed class SplitTableTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    /// <summary>真的那一家 provider，配桩工厂：<see cref="CopyChannelProvider"/> 要在它上面掰"有没有通道"那一格。</summary>
    static IDbProvider Real() => SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm(IDbProvider provider)
        => new(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = SqlServer.OfflineConnectionString,
        });

    static readonly DateTime January = new(2026, 1, 15, 10, 30, 0);

    static ShardLog row(DateTime at, int level = 1, Guid? id = null)
        => new() { Id = id ?? Guid.NewGuid(), OccurredAt = at, Level = level, Message = "甲" };

    // ── 换算：一份实现，三处消费者 ────────────────────────────────────

    [Theory]
    [InlineData(SplitKind.Day, 2026, 1, 15, "BAD_T_{year}{month}{day}", "BAD_T_20260115")]
    [InlineData(SplitKind.Month, 2026, 1, 5, "BAD_T_{year}{month}", "BAD_T_202601")]
    [InlineData(SplitKind.Year, 2026, 11, 30, "BAD_T_{year}", "BAD_T_2026")]
    public void A_row_lands_in_the_period_it_belongs_to(SplitKind kind, int year, int month, int day, string template, string expected)
    {
        var table = SplitRouting.Resolve(new TableInfo(null, template), new SplitFacts(kind, "At", 0), new DateTime(year, month, day));

        Assert.Equal(expected, table.Name);

        // 换算完的那一张是物理表，不再带模板事实：发射口的兜底按"这一格在场与否"判，带着就发不出去。
        Assert.Null(table.Split);
    }

    [Fact]
    public void Week_shard_is_named_by_the_monday_it_starts_on()
    {
        // 2026-01-15 是周四，那一周的周一是 12 日 —— 日档写 {day}，周档拿那一周的起点填它。
        var table = SplitRouting.Resolve(
            new TableInfo(null, "BAD_T_{year}{month}{day}"),
            new SplitFacts(SplitKind.Week, "At", 0),
            new DateTime(2026, 1, 15));

        Assert.Equal("BAD_T_20260112", table.Name);
    }

    [Fact]
    public void Season_shard_lands_on_the_first_month_of_the_quarter()
    {
        var table = SplitRouting.Resolve(
            new TableInfo(null, "BAD_T_{year}{month}"),
            new SplitFacts(SplitKind.Season, "At", 0),
            new DateTime(2026, 8, 20));

        Assert.Equal("BAD_T_202607", table.Name);
    }

    [Fact]
    public void A_range_enumerates_one_shard_per_period_in_ascending_order()
    {
        var shards = SplitRouting.EnumerateTime(
            new TableInfo(null, "BAD_T_{year}{month}"),
            new SplitFacts(SplitKind.Month, "At", 0),
            new DateTime(2026, 1, 31),
            new DateTime(2026, 4, 1));

        Assert.Equal(["BAD_T_202601", "BAD_T_202602", "BAD_T_202603", "BAD_T_202604"], [.. shards.Select(s => s.Name)]);
    }

    [Fact]
    public void The_two_ends_inside_one_period_give_exactly_one_shard()
    {
        var shards = SplitRouting.EnumerateTime(
            new TableInfo(null, "BAD_T_{year}{month}"),
            new SplitFacts(SplitKind.Month, "At", 0),
            January,
            new DateTime(2026, 1, 31, 23, 59, 59));

        Assert.Single(shards);
    }

    [Fact]
    public void A_reversed_range_is_refused_instead_of_becoming_an_empty_list()
    {
        var raised = Checks.Raised<QuickSqlException>(() => SplitRouting.EnumerateTime(
            new TableInfo(null, "BAD_T_{year}{month}"),
            new SplitFacts(SplitKind.Month, "At", 0),
            new DateTime(2026, 3, 1),
            new DateTime(2026, 1, 1)));

        Assert.Contains("起点 2026-03-01 晚于终点 2026-01-01", raised.Message);
    }

    [Fact]
    public void A_range_wider_than_the_shard_cap_names_the_cap_and_the_way_out()
    {
        var raised = Checks.Raised<QuickSqlException>(() => SplitRouting.EnumerateTime(
            new TableInfo(null, "BAD_T_{year}{month}{day}"),
            new SplitFacts(SplitKind.Day, "At", 0),
            new DateTime(2020, 1, 1),
            new DateTime(2026, 1, 1)));

        Assert.Contains($"上限 {SplitRouting.MaxShards}", raised.Message);
        Assert.Contains("缩小范围或改按更粗的档", raised.Message);
    }

    [Fact]
    public void Bucket_list_dedupes_and_keeps_the_order_given()
    {
        var shards = SplitRouting.EnumerateBuckets(
            new TableInfo(null, "BAD_T_{shard}"),
            new SplitFacts(SplitKind.Hash, "TenantId", 8),
            [5, 1, 5, 3]);

        Assert.Equal(["BAD_T_05", "BAD_T_01", "BAD_T_03"], [.. shards.Select(s => s.Name)]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void An_out_of_range_bucket_is_refused_with_the_range_in_the_message(int bucket)
    {
        var raised = Checks.Raised<QuickSqlException>(() => SplitRouting.EnumerateBuckets(
            new TableInfo(null, "BAD_T_{shard}"),
            new SplitFacts(SplitKind.Hash, "TenantId", 8),
            [bucket]));

        Assert.Contains("不在 0..7 之内", raised.Message);
    }

    [Fact]
    public void Hash_routing_of_integers_is_the_value_mod_the_buckets_for_negatives_too()
    {
        // 负数那一支折成无符号再取模（C# 的取模跟着被除数走，会给出负的桶号；long.MinValue 的 Math.Abs 溢出成它自己）。
        Assert.Equal("BAD_T_03", Bucket(-1));
        Assert.Equal("BAD_T_02", Bucket(-2));
        Assert.Equal("BAD_T_00", Bucket(long.MinValue));
        Assert.Equal("BAD_T_01", Bucket(5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tenant-a")]
    [InlineData("租户甲")]
    public void Hash_routing_of_strings_is_a_stable_fnv_and_not_the_runtime_hash(string key)
    {
        var routed = Bucket(key);

        Assert.Equal(routed, Bucket(key));

        // 独立的第二份 FNV-1a：本家的桶号必须等于"照着定义算一遍"的数，而不是等于它自己。
        // 这条断言同时是"不用 GetHashCode"的证据 —— 运行期那个值随进程变，而这里给死了一份算法。
        var expected = "BAD_T_" + (Fnv1A(key) % 4u).ToString("D2");

        Assert.Equal(expected, routed);
    }

    static string Bucket(long value)
        => SplitRouting.Resolve(new TableInfo(null, "BAD_T_{shard}"), new SplitFacts(SplitKind.Hash, "TenantId", 4), value).Name;

    static string Bucket(string value)
        => SplitRouting.Resolve(new TableInfo(null, "BAD_T_{shard}"), new SplitFacts(SplitKind.Hash, "TenantId", 4), value).Name;

    static uint Fnv1A(string text)
    {
        var hash = 2166136261u;

        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;

        return hash;
    }

    [Fact]
    public void An_empty_split_key_is_refused_rather_than_routed_to_today()
    {
        // SqlSugar 那一族在空值上当作今天（读码结论见规格 §16.4 那一格），于是"少写一个日期"的行的账
        // 要到月底统计时才看得见。本家让它在写出去之前就停住。
        var raised = Checks.Raised<QuickSqlException>(() => SplitRouting.Resolve(
            new TableInfo(null, "BAD_T_{year}{month}"), new SplitFacts(SplitKind.Month, "At", 0), null));

        Assert.Contains("分片键为空", raised.Message);

        var hashRaised = Checks.Raised<QuickSqlException>(() => SplitRouting.Resolve(
            new TableInfo(null, "BAD_T_{shard}"), new SplitFacts(SplitKind.Hash, "At", 4), null));

        Assert.Contains("分片键为空", hashRaised.Message);
    }

    [Fact]
    public void A_time_bucket_refuses_a_non_date_key_instead_of_converting_it()
    {
        var raised = Checks.Raised<QuickSqlException>(() => SplitRouting.Resolve(
            new TableInfo(null, "BAD_T_{year}{month}"), new SplitFacts(SplitKind.Month, "At", 0), 42));

        Assert.Contains("要的是日期类型，实际拿到 Int32", raised.Message);
    }

    [Fact]
    public void Date_only_and_DateOffset_are_accepted_by_the_time_buckets()
    {
        Assert.Equal("BAD_T_202601", SplitRouting.Resolve(
            new TableInfo(null, "BAD_T_{year}{month}"), new SplitFacts(SplitKind.Month, "At", 0),
            new DateOnly(2026, 1, 31)).Name);

        Assert.Equal("BAD_T_202601", SplitRouting.Resolve(
            new TableInfo(null, "BAD_T_{year}{month}"), new SplitFacts(SplitKind.Month, "At", 0),
            new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.FromHours(8))).Name);
    }

    // ── 启动期判据：算不出名字就别留着它 ──────────────────────────────

    [Theory]
    [InlineData(typeof(PlainName), "缺 Month 档要求的占位符 {year}")]
    [InlineData(typeof(YearOnlyTemplate), "缺 Month 档要求的占位符 {month}")]
    [InlineData(typeof(TooManyTokens), "用不上的占位符 {day}")]
    [InlineData(typeof(RepeatedToken), "{year} 出现了 2 次")]
    [InlineData(typeof(UnknownToken), "认不出的占位符")]
    [InlineData(typeof(ShardTokenOnTime), "缺 Month 档要求的占位符 {year}")]
    [InlineData(typeof(HashWithPlainName), "缺 Hash 档要求的占位符 {shard}")]
    [InlineData(typeof(TimeTokensOnHash), "用不上的占位符 {year}")]
    public void A_template_that_cannot_be_filled_is_refused_at_contract_build(Type clrType, string phrase)
    {
        // 注解在类型上是编译期写死的，所以每种坏模板各配一颗类型（档跟着类型上的 [SplitTable] 走，
        // 这里不再重复给一遍 —— 给了就可能与那颗类型说的不一致）。这里测的是"启动期那一道门"，
        // 而不是 SplitRouting 自己的单元测试 —— 那一半在上面几则里已经直接调过换算口了。
        var raised = Checks.Raised<QuickSqlException>(() => orm().Schemas.Contract(clrType));

        Assert.Contains("分表名模板", raised.Message);
        Assert.Contains(phrase, raised.Message);
    }

    [Fact]
    public void A_split_key_that_is_not_a_column_is_refused()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Schemas.Contract(typeof(IgnoredSplitKey)));

        Assert.Contains("分片键 Ghost 不是实体 IgnoredSplitKey 的一列", raised.Message);
    }

    [Fact]
    public void An_identity_primary_key_on_a_split_entity_is_refused_and_says_the_way_out()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Schemas.Contract(typeof(IdentitySplitKey)));

        Assert.Contains("跨片唯一性库给不了", raised.Message);
        Assert.Contains("IsIdentity=TriState.No", raised.Message);
    }

    [Fact]
    public void Fewer_than_two_buckets_is_refused_as_not_split_at_all()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Schemas.Contract(typeof(OneBucketSplit)));

        Assert.Contains("少于 2 个桶等于没分", raised.Message);
    }

    [Fact]
    public void The_split_column_name_is_filled_once_by_the_factory_and_frozen_with_the_contract()
    {
        var split = orm().Schemas.Contract<ShardLog>().Table.Split!;

        Assert.Equal(SplitKind.Month, split.Kind);
        Assert.Equal(nameof(ShardLog.OccurredAt), split.MemberName);

        // 声明侧（G1 生成的事实、G2 反射的注解）都留 null，由 EntityContractFactory 一处填上：
        // 两侧一致这件事在 MetadataEquivalenceTests 里比，而"填好之后随契约不可变"在这里比。
        Assert.Equal(nameof(ShardLog.OccurredAt), split.ColumnName);
        Assert.Equal(0, split.Buckets);
        Assert.True(split.IsTime);

        var hashed = orm().Schemas.Contract<TenantRow>().Table.Split!;

        Assert.Equal(4, hashed.Buckets);
        Assert.False(hashed.IsTime);
        Assert.Equal(nameof(TenantRow.TenantId), hashed.ColumnName);
    }

    // ── 发射口：模板不许原样出去 ──────────────────────────────────────

    [Fact]
    public void A_template_reaching_the_emitter_is_refused_there()
    {
        var template = orm().Schemas.Contract<ShardLog>().Table;
        var dialect = Real().Dialect;

        var raised = Checks.Raised<QuickSqlException>(() =>
        {
            EmitTableName(dialect, template);

            return null;
        });

        Assert.Contains("没换算成具体分片就被送去发射", raised.Message);
        Assert.Contains("读侧要先给选片范围", raised.Message);
    }

    /// <summary><see cref="SqlWriter"/> 是 <c>ref struct</c>，装不进 lambda 的捕获，所以那一句要过一颗本地函数。
    /// 这一颗只服务上面那则：它证明的是"兜底在发射口上"，不是又一份用法出口。</summary>
    static void EmitTableName(ISqlDialect dialect, TableInfo table)
    {
        var writer = new SqlWriter(new StringBuilder(), [], dialect.ParameterPrefix);

        dialect.EmitTableName(writer, table);
    }

    // ── 读侧：选片、单片同形、跨片 UNION ALL ──────────────────────────

    [Fact]
    public void Reading_a_split_entity_without_a_shard_range_is_refused()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Select<ShardLog>().ToSql());

        Assert.Contains("没选片就要读", raised.Message);
        Assert.Contains("不给“默认扫全片”的退路", raised.Message);
    }

    [Fact]
    public void SplitTables_on_an_unsplit_entity_is_refused_rather_than_reading_the_whole_table()
    {
        var raised = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)).ToSql());

        Assert.Contains("实体 Order 没有声明分表", raised.Message);
    }

    [Fact]
    public void A_single_shard_read_is_the_same_sentence_as_an_unsplit_entity()
    {
        var sql = orm().Select<ShardLog>().SplitTables(January, January).ToSql();

        Checks.Absent("UNION ALL", sql);
        Checks.Has("FROM [QuickSqlShardLog_202601] AS [t0]", sql);

        // 列序与不分表时同一份（物化按位置读，跨片那一层不能为它另定一套列序）。
        Assert.Equal(
            ["[t0].[Id]", "[t0].[Message]", "[t0].[Level]", "[t0].[OccurredAt]", "[t0].[CustomerId]"],
            Checks.Columns(sql));
    }

    [Fact]
    public void A_range_over_three_months_bends_one_select_per_shard_into_union_all()
    {
        var sql = orm().Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31))
            .Where(o => o.Level > 2)
            .OrderBy(o => o.OccurredAt)
            .Skip(10).Take(10)
            .ToSql();

        Golden.Verify("split-union-three-shards", sql);

        Assert.Equal(2, Checks.Occurrences(sql, "UNION ALL"));
        Assert.Equal(3, Checks.Occurrences(sql, "FROM [QuickSqlShardLog_"));

        // 外层只端一颗 *：片内的选列形状与单片时同一份，物化按位置读的列序因此不必为跨片重定。
        Assert.StartsWith("SELECT * FROM (", sql, StringComparison.Ordinal);

        // 条件落在每一片里（那是剪枝之后的片内过滤），序与分页只长在外层。
        // 参数按文本里出现的先后编号，所以三片各占一颗 WHERE（@p0..@p2）之后，外层分页落在 @p3/@p4。
        Assert.Equal(3, Checks.Occurrences(sql, "[Level] > @"));
        Assert.EndsWith("ORDER BY [t0].[OccurredAt] ASC OFFSET @p3 ROWS FETCH NEXT @p4 ROWS ONLY", sql, StringComparison.Ordinal);

        foreach (var shard in new[] { "202601", "202602", "202603" })
            Checks.Has($"[QuickSqlShardLog_{shard}]", sql);

        // 分支里不许有序与分页：T-SQL 拒绝带 ORDER BY 的派生表，而"每片各取 10 行"根本不是这一页要的东西
        // （SqlSugar 的 Clone() 把 Take/Skip/OrderBy 复制进每一片，正是反面教材）。
        Assert.Equal(3, Checks.Occurrences(sql, "WHERE"));
        Assert.Equal(1, Checks.Occurrences(sql, "OFFSET"));
    }

    [Fact]
    public void The_shard_list_is_a_fact_of_the_model_so_the_last_saying_wins()
    {
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 2, 1);

        Assert.Equal(
            orm().Select<ShardLog>().SplitTables(from, to).ToSql(),
            orm().Select<ShardLog>().SplitTables(from, to).ToSql());

        // 说一次算一次，后一次覆盖前一次：叠加两份会得到第三种没人要的区间。
        var narrowed = orm().Select<ShardLog>().SplitTables(from, to).SplitTables(January, January).ToSql();

        Checks.Has("[QuickSqlShardLog_202601]", narrowed);
        Assert.DoesNotContain("202602", narrowed);
        Checks.Absent("UNION ALL", narrowed);
    }

    [Fact]
    public void Hash_shards_are_read_in_the_bucket_order_given()
    {
        var sql = orm().Select<TenantRow>().SplitTables(3, 0).ToSql();

        Assert.Equal(1, Checks.Occurrences(sql, "UNION ALL"));

        var first = sql.IndexOf("[QuickSqlTenantRow_03]", StringComparison.Ordinal);
        var second = sql.IndexOf("[QuickSqlTenantRow_00]", StringComparison.Ordinal);

        Assert.True(first >= 0 && second > first, $"桶序没有落进分支序：{Environment.NewLine}{sql}");
    }

    [Fact]
    public void A_time_range_on_the_hash_entity_and_a_bucket_list_on_the_time_entity_are_both_refused()
    {
        var raised = Checks.Raised<QuickSqlException>(
            () => orm().Select<TenantRow>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)).ToSql());

        Assert.Contains("按时间范围选片只适用于时间档", raised.Message);

        var other = Checks.Raised<QuickSqlException>(
            () => orm().Select<ShardLog>().SplitTables(1).ToSql());

        Assert.Contains("按桶号选片只适用于 Hash", other.Message);
    }

    // ── 读侧的拒绝：跨片聚合、连接、分页计数 ──────────────────────────

    [Fact]
    public void A_count_over_shards_is_refused_and_points_at_the_way_that_works()
    {
        var raised = Checks.Raised<QuickSqlException>(
            () => orm().Select<ShardLog>()
                .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 1))
                .CountAsync(TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult());

        var message = raised.Message;

        Assert.Contains("跨 3 片不做聚合/分组", message);
        Assert.Contains("CountMethod.SkipTotal", message);

        // 单片不受这一格限制：范围收到一片就是一句普通的聚合，它照样进第 5 层、只缺驱动那件事要用异常来证明 ——
        // 拿 ToSql() 量不到它，聚合的出口在 CountAsync 那一颗，不在查询串的发射口。
        var single = Checks.Raised<DriverNotAvailableException>(
            () => orm().Select<ShardLog>().SplitTables(January, January)
                .CountAsync(TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult());

        Checks.Has("CreateConnection", single.Message);
    }

    [Fact]
    public void Aggregates_and_group_by_over_shards_are_refused_at_build()
    {
        var q = orm().Select<ShardLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 1));

        var sum = Checks.Raised<QuickSqlException>(
            () => q.SumAsync(o => o.Level, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Contains("不做聚合/分组", sum.Message);

        // 分组链没有物化出口（§5.3：整行 + GROUP BY 在哪家都是错句），所以这一则是选完才撞门的。
        var grouped = Checks.Raised<QuickSqlException>(() => orm().Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 1))
            .GroupBy(o => o.Level)
            .Select(g => g.Key)
            .ToSql());

        Assert.Contains("不做聚合/分组", grouped.Message);
    }

    [Fact]
    public void The_count_that_wraps_its_own_inner_query_refuses_with_the_same_words()
    {
        // 派生表那一格里 UNION ALL 活在内层，CompileSplit 打不到外层，所以侧门上钉了同一句拒绝：
        // 放行它的后果是"CountAsync 抛，而 Take(10).CountAsync() 给数"——那是实现顺序决定的形状。
        var raised = Checks.Raised<QuickSqlException>(() => orm().Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 1))
            .OrderBy(o => o.OccurredAt)
            .Take(10)
            .CountAsync(TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult());

        Assert.Contains("跨 3 片不做聚合/分组", raised.Message);
    }

    [Fact]
    public void Joins_and_includes_across_shards_are_refused()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 1))
            .Include(o => o.Customer)
            .ToSql());

        Assert.Contains("不做连接与急切加载", raised.Message);

        var joined = Checks.Raised<QuickSqlException>(() => orm().Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 1))
            .LeftJoin<Customer>((s, c) => s.CustomerId == c.Id)
            .ToSql());

        Assert.Contains("不做连接与急切加载", joined.Message);
    }

    [Fact]
    public void Paging_without_the_total_is_the_shape_that_survives_cross_shard()
    {
        // SkipTotal 那一档多取一行判有无下一页，不数总数，所以跨片成立（§5.12）。
        // 这里量的是它对应的句子形状：两片各一条 SELECT 串起来，尾巴上只挂外层的序与分页。
        var sql = orm().Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 2, 28))
            .OrderBy(o => o.Id)
            .Take(10)
            .ToSql();

        Assert.Equal(1, Checks.Occurrences(sql, "UNION ALL"));
        Checks.Has("FETCH NEXT", sql);
    }

    // ── 写侧：按片切块 ────────────────────────────────────────────────

    [Fact]
    public void A_batch_across_three_months_becomes_one_statement_per_shard()
    {
        var plan = orm().InsertRange([
            row(new DateTime(2026, 1, 10), id: Guid.Empty),
            row(new DateTime(2026, 2, 10)),
            row(new DateTime(2026, 1, 20)),
            row(new DateTime(2026, 3, 10)),
        ]).Explain();

        // 一条 INSERT 只有一个目标表，所以片的张数就是块数（这里的行数远没到预算那道界）。
        Assert.Equal(3, plan.Chunks.Count);

        // 片的先后按各片首行第一次出现的先后，片内保留入参顺序 —— 回填与"哪一行进了哪张表"都靠这一条。
        Assert.Equal(
            ["QuickSqlShardLog_202601", "QuickSqlShardLog_202602", "QuickSqlShardLog_202603"],
            [.. plan.Chunks.Select(c => c.Ast.Table.Table.Name)]);

        Assert.Equal(2, plan.Chunks[0].RowCount);
        Checks.Has("INSERT INTO [QuickSqlShardLog_202601] ", plan.Chunks[0].Sql.Text);
        Checks.Has("INSERT INTO [QuickSqlShardLog_202602] ", plan.Chunks[1].Sql.Text);
        Assert.Equal(1, plan.Chunks[2].RowCount);

        Assert.Contains("分布在 3 张片上", plan.Reason);

        // 一块的行在入参数组里不连续（第一块是第 0 与第 2 行），所以块要自带那份下标表。
        Assert.Equal([0, 2], plan.Chunks[0].RowOrdinals!.ToArray());
        Assert.Equal([1], plan.Chunks[1].RowOrdinals!.ToArray());

        // 分表实体的单行批也带这张下标表：一旦按片分组，"这一块来自入参的哪几行"就是每块的事实，
        // 不给"只有一片时省掉"那条特例 —— 回填路径只认一种形状，特例是它的第三种。
        Assert.Equal([0], orm().Insert(row(January)).Explain().Chunks[0].RowOrdinals!.ToArray());
    }

    [Fact]
    public void A_split_batch_refuses_a_row_whose_key_is_empty()
    {
        // SqlSugar 那一族把空分片键当作今天，于是这一行会静默进本月那张表；本家一条都不写。
        var raised = Checks.Raised<QuickSqlException>(
            () => orm().InsertRange([
                new NullableSplitKey { Id = Guid.NewGuid(), At = January },
                new NullableSplitKey { Id = Guid.NewGuid() },
            ]).Explain());

        Assert.Contains("分片键为空", raised.Message);
    }

    [Fact]
    public void An_unsplit_batch_still_needs_no_ordinal_table_and_names_one_table()
    {
        var plan = orm().InsertRange([new Order { Id = 1 }, new Order { Id = 2 }]).Explain();

        Assert.Single(plan.Chunks);
        Assert.Null(plan.Chunks[0].RowOrdinals);
        Assert.DoesNotContain("张片", plan.Reason);
    }

    [Fact]
    public void Multi_shard_insert_has_no_single_sql_text_and_says_where_to_look()
    {
        var insert = orm().InsertRange([row(January), row(new DateTime(2026, 2, 1))]);

        var raised = Checks.Raised<QuickSqlException>(() => insert.ToSql());

        Assert.Contains("没有单一 SQL 文本", raised.Message);
        Assert.Contains("ToLogSql()", raised.Message);

        // 而"看全部分块"那一句是真给得出的：两片各一段，段首点名各自的目标表。
        Checks.Has("[QuickSqlShardLog_202601]", insert.ToLogSql());
        Checks.Has("[QuickSqlShardLog_202602]", insert.ToLogSql());
    }

    [Fact]
    public void Copy_channel_delivery_for_split_rows_is_one_trip_per_shard()
    {
        var report = orm(CopyChannelProvider.OfferingChannel(Real()))
            .InsertRange([
                row(January), row(new DateTime(2026, 1, 20)),
                row(new DateTime(2026, 2, 10)),
            ])
            .ExplainBulk(BulkMethod.CopyChannel);

        Assert.Equal(BulkMethod.CopyChannel, report.Method);
        Assert.Equal(3, report.RowCount);

        // 一次递交只有一个目标表，所以"几次往返"是逐片取整再相加：这里是一片 2 行 + 一片 1 行 = 2 次。
        Assert.Equal(2, report.RoundTrips);
        Assert.Contains("分 2 张片", report.Reason);
    }

    // ── 写侧：UPDATE / DELETE 选片 ────────────────────────────────────

    [Fact]
    public void An_update_without_a_shard_is_refused_before_the_emitter_sees_the_template()
    {
        var raised = Checks.Raised<QuickSqlException>(
            () => orm().Update<ShardLog>().Set(o => o.Level, 9).Where(o => o.Id == Guid.Empty).ToSql());

        Assert.Contains("UPDATE 要先说打哪一片", raised.Message);
        Assert.Contains("SplitTable(那一片里的一个日期)", raised.Message);
    }

    [Fact]
    public void A_shard_chosen_first_moves_the_target_and_the_column_prefix_together()
    {
        var sql = orm().Update<ShardLog>()
            .SplitTable(January)
            .Set(o => o.Level, 9)
            .Where(o => o.Id == Guid.Empty)
            .ToSql();

        Golden.Verify("split-update-one-shard", sql);

        // T-SQL 的写法是 UPDATE 别名 … FROM 表 AS 别名：换片要连 FROM 那一张一起换，
        // 而 SET 与 WHERE 的前缀仍然指同一份目标（这就是"选片必须放链首"那句话的产出形状）。
        Checks.Has("UPDATE [t0] SET [Level] = @p0 FROM [QuickSqlShardLog_202601] AS [t0]", sql);
        Assert.Equal(0, Checks.Occurrences(sql, "UNION ALL"));
    }

    [Fact]
    public void Choosing_the_shard_after_a_clause_is_refused_and_says_why()
    {
        // 列引用是翻译时绑到当时的目标上的（Oracle 血统那一家自引用名就是表名），事后换片会留下指错前缀的句子。
        var raised = Checks.Raised<QuickSqlException>(() => orm().Update<ShardLog>()
            .Set(o => o.Level, 9)
            .SplitTable(January)
            .Where(o => o.Id == Guid.Empty)
            .ToSql());

        Assert.Contains("要放在链式最前面", raised.Message);
        Assert.Contains("已叠加 1 个 SET/0 个条件", raised.Message);
    }

    [Fact]
    public void The_reason_carries_the_physical_shard_not_the_template()
    {
        var plan = orm().Update<ShardLog>().SplitTable(January).Set(o => o.Level, 9).Where(o => o.Id == Guid.Empty).Explain();

        Assert.Contains("UPDATE QuickSqlShardLog_202601", plan.Reason);
        Assert.DoesNotContain("{year}", plan.Reason);
    }

    [Fact]
    public void A_delete_names_its_shard_the_same_way_and_refuses_without_one()
    {
        var raised = Checks.Raised<QuickSqlException>(
            () => orm().Delete<ShardLog>().Where(o => o.Id == Guid.Empty).ToSql());

        Assert.Contains("DELETE 要先说删哪一片", raised.Message);

        var bucket = orm().Delete<TenantRow>().SplitTable(2).Where(o => o.Id == Guid.Empty).Explain();

        // T-SQL 的删除句同样是"别名在前、目标在 FROM"，所以要钉的是那一张表换了片而别名没换。
        Checks.Has("DELETE FROM [t0] FROM [QuickSqlTenantRow_02] AS [t0]", bucket.ToSql());
        Assert.Contains("DELETE FROM QuickSqlTenantRow_02", bucket.Reason);
        Assert.DoesNotContain("{shard}", bucket.Reason);

        var lateShard = Checks.Raised<QuickSqlException>(() => orm().Delete<ShardLog>()
            .Where(o => o.Id == Guid.Empty)
            .SplitTable(January)
            .ToSql());

        Assert.Contains("要放在链式最前面", lateShard.Message);
    }

    [Fact]
    public void SplitTable_on_an_unsplit_write_chain_is_refused()
    {
        Assert.Contains("没有声明分表",
            Checks.Raised<QuickSqlException>(() => orm().Update<Order>().SplitTable(January)).Message);

        Assert.Contains("没有声明分表",
            Checks.Raised<QuickSqlException>(() => orm().Delete<Order>().SplitTable(January)).Message);
    }

    [Fact]
    public void A_hash_bucket_out_of_range_is_refused_by_the_write_chain_too()
    {
        var raised = Checks.Raised<QuickSqlException>(() => orm().Update<TenantRow>().SplitTable(9));

        Assert.Contains("不在 0..3 之内", raised.Message);
    }

    // ── 跟踪器：分表的写不归会话管 ────────────────────────────────────

    [Fact]
    public void A_split_entity_added_to_a_session_is_refused_before_the_transaction_opens()
    {
        var db = new DbContext(orm());

        db.Add(row(January));

        var raised = Checks.Raised<QuickSqlException>(() => db.SaveChanges());

        Assert.Contains("跟踪器写不出去", raised.Message);
        Assert.Contains("orm.Insert/InsertRange/Update/Delete", raised.Message);
    }

    [Fact]
    public void Reading_a_split_entity_into_a_session_is_allowed_because_it_sends_nothing()
    {
        var db = new DbContext(orm());

        db.Attach(row(January));

        // 判据是"这一行此刻会不会被写出去"，与 AnyDue 同一颗：只读进会话不发改，没必要关掉整个会话的身份映射。
        var report = db.SaveChanges();

        Assert.Empty(report.Operations);
        Assert.Equal(0, report.AffectedRows);
    }

    // ── DDL：按范围建片 ───────────────────────────────────────────────

    [Fact]
    public void The_shard_script_lists_one_create_table_per_shard_and_no_create_index()
    {
        var ddl = orm().SplitTablesScript<ShardLog>(new DateTime(2026, 1, 1), new DateTime(2026, 2, 1));

        Assert.Equal(2, Checks.Occurrences(ddl, "CREATE TABLE"));
        Assert.Contains("[QuickSqlShardLog_202601]", ddl);
        Assert.Contains("[QuickSqlShardLog_202602]", ddl);
        Assert.DoesNotContain("{year}", ddl);

        // 主键随每张片建（那是实体形状的一半），而索引一条都不发。
        Checks.Has("PRIMARY KEY", ddl);

        // 索引名在 PG 与 Oracle 那一族是模式内唯一的：同一个模板的 N 张片各发同一颗名字，到第二张就撞。
        // 这一格钉的是"脚本里确实一条 CREATE INDEX 都没有"，而 SplitTablesResult 会把没建的那几条点名。
        Assert.DoesNotContain("CREATE INDEX", ddl);
        Assert.DoesNotContain("IX_", ddl);
    }

    [Fact]
    public void The_hash_script_lists_exactly_the_buckets_requested()
    {
        var ddl = orm().SplitTablesScript<TenantRow>(1, 3);

        Assert.Equal(2, Checks.Occurrences(ddl, "CREATE TABLE"));
        Assert.Contains("[QuickSqlTenantRow_01]", ddl);
        Assert.Contains("[QuickSqlTenantRow_03]", ddl);
        Assert.DoesNotContain("{shard}", ddl);
        Assert.DoesNotContain("_02", ddl);
    }

    [Fact]
    public void Whole_database_sync_skips_the_template_and_names_it_in_the_result()
    {
        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Real(),
            ConnectionString = SqlServer.OfflineConnectionString,
            SchemaSync = SchemaSyncPolicy.Script,
        });

        _ = orm.Schemas.Contract<ShardLog>();
        _ = orm.Schemas.Contract<Order>();

        var result = orm.SyncStructure();

        // 模板不是一张能建的表；跳过并记名，而不是悄悄建出一张名字里真带 {year} 的表，也不是整库同步直接不可用。
        Assert.Contains("QuickSqlOrder", result.CreatedTables);
        Assert.DoesNotContain("QuickSqlShardLog_{year}{month}", result.CreatedTables);
        Checks.Has("[QuickSqlOrder]", result.Ddl);
        Assert.DoesNotContain("QuickSqlShardLog", result.Ddl);

        var skipped = Assert.Single(result.SkippedSplitTemplates);

        Assert.Contains("QuickSqlShardLog_{year}{month}", skipped);
        Assert.Contains("Month 档", skipped);
        Assert.Contains("OccurredAt", skipped);
    }

    [Fact]
    public void Asking_for_shards_of_an_unsplit_entity_is_refused()
    {
        Assert.Contains("没有声明分表",
            Checks.Raised<QuickSqlException>(() => orm().SplitTablesScript<Order>(January, January)).Message);
    }

    [Fact]
    public void Creating_shards_without_permission_is_refused_before_any_connection()
    {
        var oldEnv = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

            var raised = Checks.Raised<InvalidOperationException>(
                () => orm().CreateSplitTables<ShardLog>(new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)));

            Assert.Contains("ForceSchemaSync=true", raised.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", oldEnv);
        }
    }

    // ── 现场造的负样本实体 ────────────────────────────────────────────

    /// <summary>注解是编译期写死的，所以每种坏模板各配一颗类型。这几颗故意只有表名与档不同：
    /// 判据要证明的是"表名那一格决定抛不抛"，别的差异都不许进来。</summary>
    [Table("BAD_T_PLAIN")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class PlainName
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }
    }

    [Table("BAD_T_{year}")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class YearOnlyTemplate
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }
    }

    [Table("BAD_T_{year}{month}{day}")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class TooManyTokens
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }
    }

    [Table("BAD_T_{year}{year}")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class RepeatedToken
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }
    }

    /// <summary>认不出的占位符要<em>配齐本档要的那两颗</em>才量得到：校验按"缺 → 多 → 认不出"三问的顺序走，
    /// 一颗只有 <c>{hour}</c> 的模板在第一问就红了，那句"认不出的占位符"永远轮不到说。</summary>
    [Table("BAD_T_{year}{month}{hour}")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class UnknownToken
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }
    }

    [Table("BAD_T_{shard}")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class ShardTokenOnTime
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }
    }

    [Table("BAD_T_HASH_PLAIN")]
    [SplitTable(SplitKind.Hash, nameof(At), Buckets = 4)]
    sealed class HashWithPlainName
    {
        public Guid Id { get; set; }

        public int At { get; set; }
    }

    [Table("BAD_T_{year}{month}")]
    [SplitTable(SplitKind.Hash, nameof(At), Buckets = 4)]
    sealed class TimeTokensOnHash
    {
        public Guid Id { get; set; }

        public int At { get; set; }
    }

    [Table("BAD_IGNORED_{year}{month}")]
    [SplitTable(SplitKind.Month, nameof(Ghost))]
    sealed class IgnoredSplitKey
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }

        [DaSchema.NotMapped]
        public DateTime Ghost { get; set; }
    }

    [Table("BAD_IDENTITY_{year}{month}")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class IdentitySplitKey
    {
        /// <summary>约定命中 <c>Id</c> ⇒ 主键 + 自增（整型才自动自增，§10.2 的约定层）—— 正是要拒的那一形。</summary>
        public int Id { get; set; }

        public DateTime At { get; set; }
    }

    [Table("BAD_ONE_BUCKET_{shard}")]
    [SplitTable(SplitKind.Hash, nameof(At), Buckets = 1)]
    sealed class OneBucketSplit
    {
        public Guid Id { get; set; }

        public int At { get; set; }
    }

    /// <summary>分片键是<em>可空</em>的一列：写侧"空分片键当场抛"那一格唯一的现场。
    /// <see cref="ShardLog.OccurredAt"/> 是 <c>DateTime</c>，装箱后永远不为 null，拿不到这一句。</summary>
    [Table("BAD_NULL_{year}{month}")]
    [SplitTable(SplitKind.Month, nameof(At))]
    sealed class NullableSplitKey
    {
        public Guid Id { get; set; }

        public DateTime? At { get; set; }
    }
}
