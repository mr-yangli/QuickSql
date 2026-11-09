using QuickSql;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>行号四颗的钉子（<c>QuickFx.RowNumber</c>，投影形状 —— SqlSugar 的 <c>SqlFunc.RowNumber</c> 那一族的同门，
/// 签名按本库口径重立：序键必填、方向用 bool、分堆另起一档）。
///
/// <para>判据三半，与 <see cref="QuickFxTests"/> 同一分工：① <b>金标</b>钉"整条文本长什么样"；
/// ② <b>十八家叶子</b>各答一次这一颗真正的主张 —— <em>共用层出文本，方言只出自己的引号</em>：
/// OVER 里那一段的列字形与本家普通 <c>ORDER BY</c> 给出的字形逐字相同，所以这里不抄十八份字面量，
/// 而是拿各家的普通排序当尺子（抄十八份基线只会多一处要同步的地方，而它量不到的还是"字形是不是同一份"）；
/// ③ <b>落点清单</b>逐格钉：行号只当列选出来，落在别的格子里当场拒——谓词位（WHERE ∶ HAVING ∶ 连接条件 ∶
/// 全局过滤器）∶ <c>GROUP BY</c> 的键（单列与复合两条）∶ 聚合的参数位（顶层与分组链两处）∶ 写侧 SET 的值位 ∶
/// DISTINCT 同框（两个书写顺序各判一处）∶ 分组链的投影位 ∶ 分表跨片 ∶ OVER 里套 OVER；
/// 外层 <c>ORDER BY</c> 里带 OVER 各家都认，那一格<em>不收</em>这一问（另立一颗"允许"钉，与上面那些"拒"是同一判据的两面）；
/// 再加两档实参判据（常量序键 ∶ 方向那颗读不出值）与树外调用那颗指路钉。</para>
///
/// <para>实体复用 <see cref="QuickFxTests"/> 文件自带的 <c>Schedule</c>：<c>Id</c> 与 <c>BeginDate</c>
/// 两列正好当序键与分堆键，而行号不发日期、也不碰列契约，另起一张表没有理由。</para></summary>
public sealed class RowNumberTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    const string OrderingKeyword = "ORDER BY ";

    const string Ascending = " ASC";

    // ---------- 四种形状各钉一条 ----------

    [Fact]
    public void RowNumber_over_the_whole_set_numbers_by_one_column()
    {
        var sql = orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(s.Id) })
            .ToSql();

        Golden.Verify("fx-rown-order", sql);

        Checks.Has("ROW_NUMBER() OVER (ORDER BY [t0].[Id] ASC) AS [Rn]", sql);
    }

    [Fact]
    public void RowNumber_can_number_downwards()
    {
        var sql = orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(s.BeginDate, descending: true) })
            .ToSql();

        Checks.Has("ROW_NUMBER() OVER (ORDER BY [t0].[BeginDate] DESC) AS [Rn]", sql);
    }

    [Fact]
    public void RowNumber_partitions_before_it_orders()
    {
        var sql = orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(s.BeginDate, s.Id) })
            .ToSql();

        Golden.Verify("fx-rown-partition", sql);

        // 分堆与排序的先后由重载的位置定：partitionKey 在前、orderKey 在后，OVER 里也是这个语序。
        Checks.Has("ROW_NUMBER() OVER (PARTITION BY [t0].[BeginDate] ORDER BY [t0].[Id] ASC) AS [Rn]", sql);
    }

    [Fact]
    public void A_partitioned_row_number_can_go_downwards_too()
    {
        var sql = orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(s.BeginDate, s.Id, true) })
            .ToSql();

        Checks.Has("ROW_NUMBER() OVER (PARTITION BY [t0].[BeginDate] ORDER BY [t0].[Id] DESC) AS [Rn]", sql);
    }

    // ---------- 它就是一个普通投影槽位 ----------

    [Fact]
    public void The_row_number_is_one_projection_slot_among_the_columns()
    {
        // 行号排在选列第一位时，物化按列序读的就是它 —— 别名那一格不能吞、也不能自己挪位置（§10.3）。
        var sql = orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(s.Id), s.BeginDate })
            .ToSql();

        Assert.Equal(["ROW_NUMBER() OVER (ORDER BY [t0].[Id] ASC) AS [Rn]", "[t0].[BeginDate]"], Checks.Columns(sql));

        // 序键那颗没有偷偷参数化：OVER 里挂的是列，整条句子一颗参数都没有。
        Checks.Absent("@p0", sql);
    }

    [Fact]
    public void A_bare_row_number_projection_is_a_value_projection()
    {
        // 标量出口那一形：没有成员名可当别名，发出去的只有 OVER 那一段本体。
        var sql = orm().Select<Schedule>()
            .Select(s => QuickFx.RowNumber(s.Id))
            .ToSql();

        Checks.Has("SELECT ROW_NUMBER() OVER (ORDER BY [t0].[Id] ASC) FROM", sql);
    }

    [Fact]
    public void RowNumber_composes_with_filter_ordering_and_paging()
    {
        // 行号进选列，外层自己的 WHERE/ORDER BY/分页照长：OVER 与句尾那一段是两件事，一处不动另一处。
        var sql = orm().Select<Schedule>()
            .Where(s => s.Id > 0)
            .OrderByDescending(s => s.BeginDate)
            .Skip(10).Take(5)
            .Select(s => new { Rn = QuickFx.RowNumber(s.BeginDate, s.Id) })
            .ToSql();

        Checks.Has("ROW_NUMBER() OVER (PARTITION BY [t0].[BeginDate] ORDER BY [t0].[Id] ASC) AS [Rn]", sql);
        Checks.Has("WHERE [t0].[Id] > @p0", sql);
        Checks.Has("ORDER BY [t0].[BeginDate] DESC", sql);
        Checks.Has("OFFSET", sql);
    }

    [Fact]
    public void The_outer_ordering_is_not_gated_because_every_family_reads_it_there()
    {
        // 外层 ORDER BY 里带 OVER 是各家都认的一格（窗口值算完才排，正是 ORDER BY 的位置），所以那一格没有闸门。
        // 这一颗钉的是"允许"，与下面三颗钉的"拒"是同一判据的两面。
        var sql = orm().Select<Schedule>()
            .OrderBy(s => QuickFx.RowNumber(s.Id))
            .ToSql();

        Checks.Has("ORDER BY ROW_NUMBER() OVER (ORDER BY [t0].[Id] ASC) ASC", sql);
    }

    // ---------- 十八家叶子：OVER 里的字形 = 本家普通排序的字形 ----------

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    [InlineData("xugu")]
    [InlineData("gbase8s")]
    [InlineData("clickhouse")]
    [InlineData("postgresql")]
    [InlineData("kingbase")]
    [InlineData("duckdb")]
    [InlineData("gbase8c")]
    [InlineData("gaussdb")]
    [InlineData("highgo")]
    [InlineData("opengauss")]
    [InlineData("mysql")]
    [InlineData("tidb")]
    [InlineData("oceanbase")]
    [InlineData("dameng")]
    [InlineData("shentong")]
    [InlineData("oracle")]
    public void The_over_clause_carries_each_leaf_s_own_identifier_text(string family)
    {
        var leaf = For(family);

        // 尺子取自本家：同一列拿去问一条普通 ORDER BY 得到什么字形，OVER 里就必须是什么字形。
        // 三档各钉一条，方向词由共用层出（ASC/DESC 十八家同一份），列名由本家出。
        var idOrdering = OrderingById(leaf);
        var dayOrdering = OrderingByBeginDate(leaf);
        var id = idOrdering[..^Ascending.Length];
        var day = dayOrdering[..^Ascending.Length];

        Checks.Has($"ROW_NUMBER() OVER (ORDER BY {idOrdering})",
            leaf.Select<Schedule>().Select(s => new { Rn = QuickFx.RowNumber(s.Id) }).ToSql());

        Checks.Has($"ROW_NUMBER() OVER (ORDER BY {id} DESC)",
            leaf.Select<Schedule>().Select(s => new { Rn = QuickFx.RowNumber(s.Id, true) }).ToSql());

        Checks.Has($"ROW_NUMBER() OVER (PARTITION BY {day} ORDER BY {idOrdering})",
            leaf.Select<Schedule>().Select(s => new { Rn = QuickFx.RowNumber(s.BeginDate, s.Id) }).ToSql());
    }

    /// <summary>本家把那一列写成什么样：借普通排序问，取 <c>ORDER BY</c> 到句尾那一段。
    /// 借尺子而不抄基线的理由与 <see cref="QuickFxTests"/> 的口径等价钉同一条 —— 要证的不是"文本长这样"，
    /// 而是"行号没有引入第二套字形"。</summary>
    static string OrderingById(QuickSqlOrm orm) => Tail(orm.Select<Schedule>().OrderBy(s => s.Id).ToSql());

    static string OrderingByBeginDate(QuickSqlOrm orm) => Tail(orm.Select<Schedule>().OrderBy(s => s.BeginDate).ToSql());

    static string Tail(string sql)
    {
        var at = sql.IndexOf(OrderingKeyword, StringComparison.Ordinal);

        Assert.True(at >= 0, $"那一条普通排序里没有 ORDER BY：{sql}");

        return sql[(at + OrderingKeyword.Length)..].Trim();
    }

    /// <summary>按名取一家的离线组装根（与 <see cref="QuickFxTests"/> 同一张十八家名册）。
    /// 默认分支响而不静默少测：以后添一家而 InlineData 忘了登，或登了名而这里没有探针，都是当场红。</summary>
    static QuickSqlOrm For(string family) => family switch
    {
        "sqlserver" => SqlServer.Orm(),
        "sqlite" => Sqlite.Orm(),
        "xugu" => Xugu.Orm(),
        "gbase8s" => Gbase8s.Orm(),
        "clickhouse" => ClickHouse.Orm(),

        "postgresql" => PostgreSql.Orm(),
        "kingbase" => Kingbase.Orm(),
        "duckdb" => DuckDb.Orm(),
        "gbase8c" => Gbase8c.Orm(),
        "gaussdb" => GaussDb.Orm(),
        "highgo" => Highgo.Orm(),
        "opengauss" => OpenGauss.Orm(),

        "mysql" => MySql.Orm(),
        "tidb" => TiDb.Orm(),
        "oceanbase" => OceanBase.Orm(),

        "dameng" => Dameng.Orm(),
        "shentong" => ShenTong.Orm(),
        "oracle" => Oracle.Orm(),
        _ => throw new NotSupportedException($"行号册里没有 {family} 这一家的探针。"),
    };

    // ---------- 九族金标：整条文本按族立账 ----------

    /// <summary>与 <see cref="QuickFxTests.The_predicate_text_is_byte_pinned_per_golden_family"/> 同一分工：
    /// 金标只立在金标在案的族（自有方言五家 ∶ 血统代表金仓/达梦/MySQL ∶ 自带金标的虚谷），
    /// 没有金标目录的九颗叶子走各家 DialectTests 的"同形交给逐字对读"，而它们的<em>字形</em>
    /// 已由上面那颗十八家叶子钉逐族量过。</summary>
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    [InlineData("xugu")]
    [InlineData("gbase8s")]
    [InlineData("clickhouse")]
    [InlineData("kingbase")]
    [InlineData("dameng")]
    [InlineData("mysql")]
    [InlineData("duckdb")]
    public void The_projection_text_is_byte_pinned_per_golden_family(string family)
    {
        var leaf = For(family);

        Golden.Verify("fx-rown-order",
            leaf.Select<Schedule>().Select(s => new { Rn = QuickFx.RowNumber(s.Id) }).ToSql(), family);

        Golden.Verify("fx-rown-partition",
            leaf.Select<Schedule>().Select(s => new { Rn = QuickFx.RowNumber(s.BeginDate, s.Id) }).ToSql(), family);
    }

    // ---------- 三处位置闸门，加两档实参判据 ----------

    [Fact]
    public void A_row_number_used_as_a_filter_is_refused_at_the_call_site()
    {
        // 库的规矩：OVER 的值要等全部行算完才有，WHERE 那一格不收。抛在组合时，栈里是用户写下的那一行。
        var ex = Raised(() => orm().Select<Schedule>()
            .Where(s => QuickFx.RowNumber(s.Id) > 3)
            .ToSql());

        Assert.Contains("行号的落点", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithSql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_number_used_as_a_group_key_is_refused()
    {
        var ex = Raised(() => orm().Select<Schedule>().GroupBy(s => QuickFx.RowNumber(s.Id)));

        Assert.Contains("GROUP BY 的键", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_number_inside_the_over_clause_is_refused()
    {
        // 两级序号今天只能分两句（而内层那一句只能走 WithSql / 原生 SQL）。
        var ex = Raised(() => orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(QuickFx.RowNumber(s.Id)) }));

        Assert.Contains("OVER 里再套 OVER", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_constant_order_key_is_refused_because_it_numbers_nothing()
    {
        // 常量序键发出去是一句"能执行"的 SQL，给的是任意序 —— 那种句子绿在测试里、错在报表上。
        var ex = Raised(() => orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(1) }));

        Assert.Contains("序键要落在查询行上", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direction_reading_from_the_row_names_the_overload_that_was_meant()
    {
        // 拿一颗 bool 表达式当第二颗实参写，重载决议把它送进方向档，所以这句拒绝要说出具名实参那一条出路。
        var ex = Raised(() => orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(s.Id, s.Id > 0) }));

        Assert.Contains("具名实参", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_number_inside_an_aggregate_is_refused()
    {
        // SUM(ROW_NUMBER() OVER (…))：窗口值要等全部行算完才有，而聚合问的就是"算完之前那些行"。
        var ex = Raised(() => orm().Select<Schedule>().Sum(s => QuickFx.RowNumber(s.Id)));

        Assert.Contains("顶层聚合的参数位", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_number_inside_a_grouped_aggregate_is_refused()
    {
        // 分组链上那一条（g.Sum(o => …)）与顶层那一条是两个落点，消息各给各的位置名，否则红了分不出是谁。
        var ex = Raised(() => orm().Select<Schedule>()
            .GroupBy(s => s.BeginDate)
            .Having(g => g.Sum(s => QuickFx.RowNumber(s.Id)) > 0));

        Assert.Contains("分组聚合的参数位", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_number_inside_a_composite_group_key_is_refused()
    {
        // 复合键走的是另一条 Visit（逐成员翻），那一条也要过同一把尺子。
        var ex = Raised(() => orm().Select<Schedule>()
            .GroupBy(s => new { Rn = QuickFx.RowNumber(s.Id), s.BeginDate }));

        Assert.Contains("GROUP BY 的键", ex.Message, StringComparison.Ordinal);
    }

    // ---------- 审查落地批：写侧与两种组合的又四格 ----------

    [Fact]
    public void A_row_number_in_a_SET_value_is_refused()
    {
        // 读侧那几格有 WithSql 那条出路，写侧这一格没有：它给的是隔壁那条写链。
        // 靶子取 ShardLog.Level（一颗能写的 int 列）——Schedule 的整型列只有主键，那一格先被写侧的列规则拦下。
        var ex = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Update<ShardLog>().Set(s => s.Level, s => QuickFx.RowNumber(s.OccurredAt)));

        Assert.Contains("SET 的值位", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UpdateRange", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_number_and_Distinct_are_refused_whichever_way_they_are_written()
    {
        // 两个书写顺序是两个不同的判点：先 Distinct 的在 Project 上看见标记，先 Select 的在 Distinct 上看见计划。
        var declaredFirst = Checks.Raised<QuickSqlException>(
            () => orm().Select<Schedule>().Distinct().Select(s => new { Rn = QuickFx.RowNumber(s.Id) }));

        var projectedFirst = Checks.Raised<QuickSqlException>(
            () => orm().Select<Schedule>().Select(s => new { Rn = QuickFx.RowNumber(s.Id) }).Distinct());

        Assert.Contains("DISTINCT 与行号不同框", declaredFirst.Message, StringComparison.Ordinal);
        Assert.Equal(declaredFirst.Message, projectedFirst.Message);
    }

    [Fact]
    public void A_row_number_on_the_grouped_chain_is_refused()
    {
        // 分组链的投影位：OVER 的键要进 GROUP BY 还是写成组级，本库不猜——猜错的那半是一句能执行的错句。
        var ex = Checks.Raised<QuickSqlException>(
            () => orm().Select<Schedule>().GroupBy(s => s.BeginDate).Select(g => new { Rn = QuickFx.RowNumber(g.Key) }));

        Assert.Contains("分组链的投影里不落行号", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_untranslatable_direction_argument_still_gets_the_row_number_pointer()
    {
        // 审查抓的那格：行号原先在"逐颗 Visit 实参"之后才分派，于是第二颗实参里一颗本家不认的表达式
        // （GetHashCode）抢先把消息换成"为 Int32.GetHashCode 注册映射"——用户看到的是隔壁那一格，
        // 而重载决议走错档这件事一次都没被说出来。入口提到 Call 最上面之后，这句拿的是行号自己的指路。
        var ex = Raised(() => orm().Select<Schedule>()
            .Select(s => new { Rn = QuickFx.RowNumber(s.Id, s.GetHashCode() > 0) }));

        Assert.Contains("具名实参", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("注册映射", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_number_over_shards_is_refused_because_every_shard_would_restart_at_one()
    {
        // 跨片那句是每片一条 SELECT 串起来的 UNION ALL，而选列长在每片那一句里 —— 行号因此在每片各数各的。
        var ex = Checks.Raised<QuickSqlException>(
            () => orm().Select<ShardLog>()
                .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 1))
                .Select(s => new { Rn = QuickFx.RowNumber(s.OccurredAt) })
                .ToSql());

        Assert.Contains("跨 3 片不行号", ex.Message, StringComparison.Ordinal);
        Assert.Contains("每片各自从 1 数起", ex.Message, StringComparison.Ordinal);

        // 单片不受这一格限制：范围收到一片就是一句普通的 OVER。
        var single = orm().Select<ShardLog>()
            .SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31))
            .Select(s => new { Rn = QuickFx.RowNumber(s.OccurredAt) })
            .ToSql();

        Checks.Has("ROW_NUMBER() OVER (ORDER BY [t0].[OccurredAt] ASC)", single);
    }

    [Fact]
    public void Calling_outside_an_expression_throws_the_pointer_message()
    {
        // 四颗方法体都没有实现；真被调到（先物化再在内存里"顺手取个号"）就要响得指路。
        var ex = Assert.Throws<NotSupportedException>(() => QuickFx.RowNumber(1));

        Assert.Contains("只活在查询表达式树里", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ToList()", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>取回异常本体（与 <see cref="Checks.Raised{T}"/> 同一个理由：这一族护栏的价值一半在消息说没说清该改哪里）。</summary>
    static UntranslatableExpressionException Raised(Func<object?> evaluate) => Checks.Raised<UntranslatableExpressionException>(evaluate);
}
