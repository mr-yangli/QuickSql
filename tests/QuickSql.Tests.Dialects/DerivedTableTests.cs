using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>派生表（§5.12 那一格）与 <c>WithSql</c>（§11 的表源那一格）产出的 SQL <b>文本</b>。
/// 与方言册其余读路径用例同一分工：这里判的是"我们发出去了什么句子"，不是"某家库接受不接受"——
/// 后者在真库上（<c>samples/QuickSql.Sample</c> 的 §10 就是拿真 SQLite 把这几句发出去过）。
///
/// <para><b>文本从哪来</b>：这一册没有假的 ADO 栈（§14 立着的原因），所以拿不到"执行期那条语句"。
/// 派生表的判据走的是同一条咽喉的<em>子查询口</em>：<c>Where(c =&gt; grouped.Count() &gt; 2)</c> 里的
/// <c>Count()</c> 用的就是 <c>AggregateModel → Compile</c>，那条内层与 <c>CountAsync</c> 要发的内层是同一份模型，
/// 于是文本判据不落空，而别名改名（<c>RemapSubqueryAliases</c>）顺带被验到 —— 派生表嵌进外层时内层与
/// 外层别名必须<em>同时</em>换成 t1000，只换一边得到的是"无法绑定多部分标识符"，那是这里最贵的一种红。</para>
///
/// <para><b>接住的与没接住的，说清楚</b>：外层只发 <c>COUNT(*)/COUNT_BIG(*)</c> 这一族（COUNT/LongCount/Any 与
/// 分页总数），因为外层要引用内层的列（<c>SUM(t0.Total)</c>）就得先给派生行造一份列契约，而那一步今天没有形状。
/// 所以下面既有用例钉"分组后的计数发得出派生表"，也有用例钉"分组后的 SUM 仍然当场拒、而且消息点名缺的是哪一格"。
/// 那句拒词指向的替代口就是本文件后半的 <c>WithSql</c> 与 §11 的原生 SQL 口。</para></summary>
public sealed class DerivedTableTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    /// <summary>分组链：一条已经选了形状、可以直接 <c>Count()</c> 的分组查询。</summary>
    static IQuery<int> Grouped(QuickSqlOrm owner) => owner.Select<Order>().GroupBy(o => o.CustomerId).Select(g => g.Count());

    /// <summary>带页的链。默认的 <c>PagingOrdering</c> 要求先有序（§9.4），所以这里必须写 OrderBy。</summary>
    static IQuery<Order> Paged(QuickSqlOrm owner) => owner.Select<Order>().OrderBy(o => o.Id).Skip(4).Take(3);

    static IQuery<OrderState> DistinctStates(QuickSqlOrm owner) => owner.Select<Order>().Select(o => o.State).Distinct();

    // ── 派生表三形 ────────────────────────────────────────────────────

    [Fact]
    public void The_group_count_wraps_the_grouping_and_leaves_group_by_inside()
    {
        var grouped = Grouped(orm());

        var sql = orm().Select<Customer>().Where(c => grouped.Count() > 2).ToSql();

        // 外层只剩 COUNT(*)：分组一句原样进括号，GROUP BY 一颗不留在外层。
        Checks.Has("(SELECT COUNT(*) FROM (SELECT COUNT(*) FROM [QuickSqlOrder] AS [t1000] GROUP BY [t1000].[CustomerId]) AS [t1000])", sql);

        // 派生表必须有别名（T-SQL 与 MySQL 都要求），而它引用的列与它自己的表位是同一套别名。
        Checks.Has(") AS [t1000])", sql);
        Checks.Absent("GROUP BY [t0]", sql);
    }

    [Fact]
    public void An_ordering_that_exists_only_to_pick_rows_is_stripped_from_the_derived_table()
    {
        // T-SQL 拒绝"带 ORDER BY 而没有 OFFSET 的派生表"，而 count 不关心序：剥掉它是既合法又省的一刀。
        var withOrder = orm().Select<Order>()
            .GroupBy(o => o.CustomerId)
            .OrderBy(g => g.Count())
            .Select(g => g.Count());

        Assert.DoesNotContain("ORDER BY", orm().Select<Customer>().Where(c => withOrder.Count() > 2).ToSql());

        // 而它没丢：同一条链自己端出去时序还在，剥的是"这一次执行不关心它"。
        Checks.Has("ORDER BY COUNT(*) ASC", withOrder.ToSql());
    }

    [Fact]
    public void A_paged_count_keeps_the_page_inside_because_that_is_what_is_being_counted()
    {
        var paged = Paged(orm());

        var sql = orm().Select<Customer>().Where(c => paged.Count() > 2).ToSql();

        Checks.Has("(SELECT COUNT(*) FROM (SELECT", sql);
        Checks.Has("OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY) AS [t1000])", sql);

        // 内层有序时序<em>不</em>剥：它是这一页的内容的一部分，摘掉就数了另一些行。
        Checks.Has("ORDER BY [t1000].[Id] ASC OFFSET", sql);
    }

    [Fact]
    public void The_distinct_count_counts_the_deduplicated_rows_not_the_table()
    {
        var distinct = DistinctStates(orm());

        var sql = orm().Select<Customer>().Where(c => distinct.Count() > 2).ToSql();

        // 与 §5.12 那个事故同一个形状：以前这一格要么拒要么"按去重前的行数算"，现在是内层自己带 DISTINCT。
        Checks.Has("FROM (SELECT DISTINCT [t1000].[State] FROM [QuickSqlOrder] AS [t1000]) AS [t1000]", sql);
    }

    [Fact]
    public void The_count_of_a_grouped_query_no_longer_refuses_and_reaches_the_driver()
    {
        // 旧版这一格是 QuickSqlException（"分组之后的聚合要派生表"）。现在它该撞在第 5 层：
        // 规划与编译全过，停在这册没有驱动（§3 的 ToSql 不连库反过来用）。
        var failure = Checks.Raised<DriverNotAvailableException>(
            () => Grouped(orm()).CountAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Checks.Has("CreateConnection", failure.Message);
    }

    // ── 计数一族的两颗新公共口（v0.38 第十九笔）────────────────────────

    [Fact]
    public void The_two_new_exits_of_the_count_family_reach_the_driver_on_the_same_throat()
    {
        // <c>AnyAsync</c>/<c>LongCountAsync</c> 在引擎里早就有，缺的一直只是 <c>IQuery</c> 上那两颗口。
        // 接上之后走的是同一条咽喉，所以这两颗钉的是"新口没有另起一条路"：规划与编译全过，停在第 5 层没有驱动。
        // <b>文本判据在这里落不了</b>：它们是出口不是链，<c>ToSql()</c> 拿不到"执行期那一条句子"。
        // <c>Any</c> 发出去的形状（「<c>SELECT 1 … 取第一行</c>」的存在性探针，不再是 <c>COUNT(*)</c>）由
        // <c>AnyProbeTests</c> 离线钉文本、由集成册 <c>ReadPathTests</c> 在真库上钉行为。
        // <c>LongCount</c> 仍是 <c>COUNT(*)</c>（它要的就是总数），同样由集成册在真库上判。
        var any = Checks.Raised<DriverNotAvailableException>(
            () => Grouped(orm()).AnyAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Checks.Has("CreateConnection", any.Message);

        var longCount = Checks.Raised<DriverNotAvailableException>(
            () => Grouped(orm()).LongCountAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Checks.Has("CreateConnection", longCount.Message);
    }

    [Fact]
    public void The_synchronous_half_of_those_two_exits_unwraps_instead_of_hiding_the_layer()
    {
        // 同步口走的是同一个 Blocking（GetAwaiter().GetResult()），所以异常本体原样透出来 ——
        // 判成 DriverNotAvailable 而不是 TargetInvocationException/AggregateException 那一类包装壳。
        var any = Checks.Raised<DriverNotAvailableException>(() => Grouped(orm()).Any());

        Checks.Has("CreateConnection", any.Message);

        var longCount = Checks.Raised<DriverNotAvailableException>(() => Grouped(orm()).LongCount());

        Checks.Has("CreateConnection", longCount.Message);
    }

    // ── 还没接住的那一档 ──────────────────────────────────────────────

    [Fact]
    public void A_non_count_aggregate_over_a_derived_table_refuses_and_names_the_missing_shape()
    {
        // 外层要引用内层的列，就得先有派生行的列契约（列名与类型只在内层的投影计划里）。
        // 这一格拒的是"缺哪一步"，不是"不支持" —— 消息本身给出替代口。
        var failure = Checks.Raised<QuickSqlException>(
            () => Grouped(orm()).SumAsync(x => x, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Checks.Has("派生行的列契约", failure.Message);
        Checks.Has("只接 COUNT 一族", failure.Message);
        Checks.Has("orm.Ado.QueryAsync", failure.Message);
    }

    [Fact]
    public void A_non_count_aggregate_over_a_paged_query_refuses_through_the_same_throat()
    {
        // 分组、DISTINCT、带页三种形状在 AggregateModel 里是同一个判据（NeedsDerivedTable），
        // 所以它们的非 COUNT 一族拒的是同一句话 —— 这一颗钉的是"带页也走这里，没被漏掉"。
        var failure = Checks.Raised<QuickSqlException>(
            () => Paged(orm()).SumAsync(o => o.Amount, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Checks.Has("派生行的列契约", failure.Message);
    }

    // ── 各家版式 ──────────────────────────────────────────────────────

    [Fact]
    public void Every_family_wraps_the_page_it_emits_and_aliases_the_derived_table()
    {
        // 判据不是"各家都写 OFFSET"，而是"各家把自己那套分页原样搬进内层，外面套一个 COUNT(*)"：
        // 分页写法在 <c>EmitPaging</c> 一处定，派生表这一层不该改它。
        foreach (var (name, needle) in new[]
                 {
                     ("sqlserver", "OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY) AS [t1000]"),
                     ("mysql", "LIMIT @p0 OFFSET @p1) AS `t1000`"),
                     ("sqlite", "LIMIT @p0 OFFSET @p1) AS \"t1000\""),
                     ("oracle", "\"QUICKSQL$ROWNUM\" > :p2) \"T1000\""),
                     ("dameng", "\"QUICKSQL$ROWNUM\" > :p2) \"T1000\""),
                     ("xugu", "LIMIT :p0 OFFSET :p1) \"T1000\""),
                     ("gbase8s", "FIRST ? SKIP ?"),
                 })
        {
            var owner = Family(name);
            var paged = Paged(owner);
            var sql = owner.Select<Customer>().Where(c => paged.Count() > 2).ToSql();

            Checks.Has("(SELECT COUNT(*) FROM (SELECT", sql);
            Checks.Has(needle, sql);
        }
    }

    [Fact]
    public void The_derived_alias_and_the_inner_references_are_remapped_together()
    {
        // 内层与外层同名（都叫 t0）时，靠库的最近绑定不会错，但人读不出哪一个是谁 ——
        // 而<em>只改一边</em>才是事故：FROM (…) 挂 [t1000] 而里面的列还写 [t0] 是句非法 SQL。
        var grouped = Grouped(orm());

        var sql = orm().Select<Customer>().Where(c => grouped.Count() > 2).ToSql();

        Assert.Equal(3, Checks.Occurrences(sql, "t1000"));

        // 派生表那一段里没有残留的 [t0]（外层自己的表位除外）。
        var inner = sql[sql.IndexOf("(SELECT COUNT(*) FROM (", StringComparison.Ordinal)..];
        Checks.Absent("[t0]", inner[..inner.IndexOf(") AS [t1000]", StringComparison.Ordinal)]);
    }

    // ── WithSql：表源那一格 ───────────────────────────────────────────

    [Fact]
    public void WithSql_replaces_the_root_table_position_and_the_outer_layer_is_still_library_written()
    {
        var query = orm().Select<Order>()
            .WithSql("SELECT * FROM QuickSqlOrder WHERE Amount >= @min", new { min = 10m })
            .Where(o => o.Amount > 20);

        var sql = query.ToSql(out var parameters);

        // 里面那一段一颗不动（连表名都不加引号），外面这一层按列契约选列、按链上的谓词加条件。
        Checks.Has("FROM (SELECT * FROM QuickSqlOrder WHERE Amount >= @min) AS [t0] WHERE [t0].[Amount] > @p1", sql);

        // 原生那颗粒先登记，所以生成的编号从它后面接着走：@p0 不存在，两处不可能撞名。
        Assert.Equal(["@min", "@p1"], [.. parameters.Select(p => p.Name)]);
    }

    [Fact]
    public void WithSql_folds_argument_names_with_the_home_prefix()
    {
        var oracle = Family("oracle");

        var sql = oracle.Select<Order>()
            .WithSql("SELECT * FROM QUICKSQLORDER WHERE AMOUNT >= :min", new { min = 10m })
            .Where(o => o.Amount > 20)
            .ToSql();

        // 前缀是本家的事：文本里写 :min，参数集合里也是 :min，而外层生成的那颗跟着同一个前缀。
        Checks.Has("FROM (SELECT * FROM QUICKSQLORDER WHERE AMOUNT >= :min) \"T0\" WHERE \"T0\".\"AMOUNT\" > :p1", sql);
    }

    [Fact]
    public void WithSql_refuses_the_positional_family_because_there_is_no_name_to_fold_into()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => Family("gbase8s").Select<Order>().WithSql("SELECT * FROM t_order WHERE amount >= ?", new { min = 10m }));

        Checks.Has("位置式", failure.Message);
    }

    [Fact]
    public void WithSql_refuses_a_parameter_name_from_the_generated_number_space()
    {
        // 生成 SQL 的参数名是"前缀 + p + 集合长度"，撞上去的症状是两处绑定抢同一个名字、值串到隔壁列。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>().WithSql("SELECT * FROM t WHERE a = @p0", new { p0 = 1 }));

        Checks.Has("号段", failure.Message);
        Checks.Has("@p0", failure.Message);
    }

    [Fact]
    public void An_alias_written_after_WithSql_lands_on_the_native_source_too()
    {
        // 覆盖源里存的 TableRef 是 WithSql 那一刻抓住的旧别名实例。不改写它，FROM 挂 [o]
        // 而外层谓词引用 [t0]（或反过来），得到的是"无法绑定多部分标识符"。
        var sql = orm().Select<Order>()
            .WithSql("SELECT * FROM QuickSqlOrder")
            .As("o")
            .Where(o => o.Amount > 1)
            .ToSql();

        Checks.Has("FROM (SELECT * FROM QuickSqlOrder) AS [o] WHERE [o].[Amount] > @p0", sql);
    }

    [Fact]
    public void The_native_source_is_visible_in_the_model_text()
    {
        // §10.1 硬规则 1：一条"看起来不像库写的"语句要在 Explain 里就判得出来。
        var text = orm().Select<Order>()
            .WithSql("SELECT * FROM QuickSqlOrder WHERE Amount >= @min", new { min = 10m })
            .Explain()
            .ModelText;

        Checks.Has("SOURCE RAW[SELECT * FROM QuickSqlOrder WHERE Amount >= @min]{@min}", text);
    }

    [Fact]
    public void A_count_over_a_native_source_remaps_its_alias_as_well()
    {
        // 内层是用户自己的文本（没有可改名的列引用），但别名<em>要</em>跟着改：
        // 外层那句的 WHERE 已经换成 [t1000]，表源挂的名字若还停在 [t0] 就是句非法 SQL。
        var source = orm().Select<Order>()
            .WithSql("SELECT * FROM QuickSqlOrder")
            .Where(o => o.Amount > 20);

        var sql = orm().Select<Customer>().Where(c => source.Count() > 2).ToSql();

        Checks.Has("FROM (SELECT * FROM QuickSqlOrder) AS [t1000] WHERE [t1000].[Amount] > @p0", sql);
    }

    static QuickSqlOrm Family(string name) => name switch
    {
        "sqlserver" => SqlServer.Orm(),
        "mysql" => MySql.Orm(),
        "sqlite" => Sqlite.Orm(),
        "oracle" => Oracle.Orm(),
        "dameng" => Dameng.Orm(),
        "xugu" => Xugu.Orm(),
        "gbase8s" => Gbase8s.Orm(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "方言册没有这一家的组装根。"),
    };
}
