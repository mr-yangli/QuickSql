using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>分组链（§5.3 的 <c>GroupBy/Having</c>）产出的 SQL <b>文本</b>与它那几颗护栏。
/// 全程离线（<see cref="SqlServer.NoDriverFactory"/>），与方言册其余读路径用例同一分工：
/// 这里判的是"我们发出去了什么句子、Clause 落在哪个位置上"，不是"某家库接受不接受"——
/// 后者在集成册（sqlite 那一跑真在那台库上把分组句发出去过）。
///
/// <para><b>四条版式判据各自钉住一件事</b>：分组键同时进选列与 <c>GROUP BY</c>（少了前者是"结果集没有键可读"，
/// 少了后者是"聚合把整表压成一行"）；<c>HAVING</c> 在 <c>GROUP BY</c> 之后、<c>ORDER BY</c> 之前（ clause 位置由
/// <see cref="QuickSql.Core.Sql.DialectBase.EmitSelectBody"/> 定，与链上书写顺序无关）；<c>WHERE</c> 永远在
/// <c>GROUP BY</c> 之前（分组之前的条件属于 <c>WHERE</c>）；聚合的模板取自各家注册表（<c>COUNT({0})</c> 那一族），
/// 所以方言<em>不需要为分组新登记任何东西</em>——这条在下面第三家以后都成对出现。</para>
///
/// <para><b>这里没有覆盖的两件事，别读成覆盖过了</b>：① 分组的两种"写错顺序 / 选错对象"形态（<c>Having</c>
/// 写在 <c>GroupBy</c> 之前、组里选非键列）在类型面上根本不存在 —— <c>Having</c> 只长在
/// <see cref="ISelectGrouping{TKey,TValue}"/> 上，而组里的行是 <c>IGrouping</c>，
/// 实体成员不在它上面。一个运行期用例都写不出来，也因此不需要 <c>ONLY_FULL_GROUP_BY</c> 风格的校验开关
/// （§5.3 原稿那颗）；同理"分组两次"只剩 <c>GroupBy → Select → GroupBy</c> 那一条走得通，见下面的用例；
/// ② 分组后的<em>非 COUNT 一族</em>（<c>SUM/Max/Min/Average</c>）与联表之后的分组仍然没有入口：前者要外层引用
/// 内层的列，而派生行的列契约今天没有形状，后者是"页与组的单位接不上"。下面那两颗护栏钉的是"它当场报错、
/// 而且消息点名缺的是哪一格"。分组后的<em>计数</em>有对应物了（整份分组进派生表内层），判据在
/// <c>DerivedTableTests</c>，真库上那一跑在 <c>samples</c> 的 §10。</para></summary>
public sealed class GroupByTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    // ── 版式 ──────────────────────────────────────────────────────────

    [Fact]
    public void Single_key_group_by_projects_the_key_and_the_aggregate()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Orders = g.Count() })
            .ToSql();

        // g.Key 落回分组键那一颗表达式本身：别名由位置生成（§5.1），所以它与 WHERE 里的写法同一个形状。
        Assert.Equal(
            ["[t0].[CustomerId]", "COUNT(*) AS [Orders]"],
            Checks.Columns(sql));

        Checks.Has("FROM [QuickSqlOrder] AS [t0]", sql);
        Checks.Has("GROUP BY [t0].[CustomerId]", sql);

        // 没有 HAVING 就不该发那个子句；没有序也不该补。
        Checks.Absent("HAVING", sql);
        Checks.Absent("ORDER BY", sql);
    }

    [Fact]
    public void A_group_by_can_project_the_aggregate_alone()
    {
        // 只要每组的计数、不要键：GROUP BY 仍然要在，否则这一句会把全表压成一行（那是另一种查询）。
        var sql = orm().Select<Order>()
            .GroupBy(o => o.CustomerId)
            .Select(g => g.Count())
            .ToSql();

        Assert.Equal(["COUNT(*)"], Checks.Columns(sql));
        Checks.Has("GROUP BY [t0].[CustomerId]", sql);
    }

    [Fact]
    public void Composite_key_emits_every_member_in_declaration_order()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => new { o.State, o.CustomerId })
            .Select(g => new { g.Key.State, g.Key.CustomerId, Units = g.Sum(o => o.Amount) })
            .ToSql();

        // 键的列序 = lambda 里的声明序，而不是成员名字母序，也不是列契约序。
        Checks.Has("GROUP BY [t0].[State], [t0].[CustomerId]", sql);

        Assert.Equal(
            ["[t0].[State]", "[t0].[CustomerId]", "SUM([t0].[Amount]) AS [Units]"],
            Checks.Columns(sql));
    }

    [Fact]
    public void Aggregates_take_their_templates_from_the_provider_registry()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => o.State)
            .Select(g => new
            {
                State = g.Key,
                N = g.Count(),
                Total = g.Sum(o => o.Amount),
                Best = g.Max(o => o.Amount),
                Worst = g.Min(o => o.Amount),
                Mean = g.Average(o => o.Amount),
            })
            .ToSql();

        foreach (var needle in new[] { "COUNT(*)", "SUM([t0].[Amount])", "MAX([t0].[Amount])", "MIN([t0].[Amount])", "AVG([t0].[Amount])" })
            Checks.Has(needle, sql);

        // 聚合的实参是普通列而不是相关子查询：组里的行就是那张表的行，没有第二层 FROM。
        Assert.Equal(1, Checks.Occurrences(sql, "SELECT"));
    }

    [Fact]
    public void Having_lands_between_group_by_and_order_by()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => o.CustomerId)
            .Having(g => g.Count() > 2)
            .OrderByDescending(g => g.Count())
            .Select(g => new { CustomerId = g.Key, Orders = g.Count() })
            .ToSql();

        Checks.Absent("WHERE", sql);
        Checks.Has("HAVING COUNT(*) > @p0", sql);
        Checks.Has("ORDER BY COUNT(*) DESC", sql);

        var groupBy = sql.IndexOf("GROUP BY", StringComparison.Ordinal);
        var having = sql.IndexOf("HAVING", StringComparison.Ordinal);
        var orderBy = sql.IndexOf("ORDER BY", StringComparison.Ordinal);

        Assert.True(groupBy < having && having < orderBy, $"子句位置错了：{sql}");
    }

    [Fact]
    public void A_condition_written_before_the_grouping_stays_in_where()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.Amount > 100m)
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.Amount) })
            .ToSql();

        Checks.Has("WHERE [t0].[Amount] > @p0 GROUP BY [t0].[CustomerId]", sql);
        Checks.Absent("HAVING", sql);
    }

    [Fact]
    public void Several_conditions_on_the_group_are_folded_with_and()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => o.CustomerId)
            .Having(g => g.Count() > 1)
            .Having(g => g.Sum(o => o.Amount) < 500m)
            .Select(g => new { CustomerId = g.Key })
            .ToSql();

        Checks.Has("HAVING COUNT(*) > @p0 AND SUM([t0].[Amount]) < @p1", sql);
    }

    [Fact]
    public void Grouping_keeps_the_paging_clause_after_the_ordering()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => o.CustomerId)
            .OrderByDescending(g => g.Sum(o => o.Amount))
            .Skip(2)
            .Take(5)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.Amount) })
            .ToSql();

        // 页与组同一单位：LIMIT/OFFSET 落在分组之后的结果集上，所以这里 5 是"五组"。
        Checks.Has("ORDER BY SUM([t0].[Amount]) DESC OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY", sql);
    }

    [Fact]
    public void The_key_can_be_ordered_by_and_can_be_a_computed_column()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => o.CreatedAt.Year)
            .OrderBy(g => g.Key)
            .Select(g => new { Year = g.Key, Orders = g.Count() })
            .ToSql();

        // 计算列做键时，GROUP BY 与 ORDER BY 复用的就是那一颗表达式本身（不是别名，也不重算成别的形态）；
        // 各家把 `x.Year` 落成什么函数由注册表定，这里判的是"三处同一个形状"。
        Checks.Has("SELECT DATEPART(year, [t0].[CreatedAt]) AS [Year]", sql);
        Checks.Has("GROUP BY DATEPART(year, [t0].[CreatedAt])", sql);
        Checks.Has("ORDER BY DATEPART(year, [t0].[CreatedAt]) ASC", sql);
    }

    /// <summary>三家的引号各不同，但分组这件事在四份文本里只体现在同一处：
    /// 键进选列 + <c>GROUP BY</c> 那一子句。<b>这一条不是"对拍"</b>（<c>DialectParityTests</c> 那种整句逐字相等
    /// 在这里提不出来，引号与分页尾巴就先不一致），它比的是"去掉引号之后的同一串结构"。</summary>
    [Fact]
    public void The_other_dialects_group_the_same_sentence_with_their_own_quoting()
    {
        static string Grouped(QuickSqlOrm provider)
            => provider.Select<Order>()
                .GroupBy(o => o.CustomerId)
                .Having(g => g.Count() > 1)
                .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.Amount) })
                .ToSql();

        var mySql = Grouped(MySql.Orm());
        var postgres = Grouped(PostgreSql.Orm());
        var sqlite = Grouped(Sqlite.Orm());

        Checks.Has("`t0`.`CustomerId`", mySql);
        Checks.Has("GROUP BY `t0`.`CustomerId`", mySql);
        Checks.Has("HAVING COUNT(*) > @p0", mySql);

        Checks.Has("\"t0\".\"CustomerId\"", postgres);
        Checks.Has("GROUP BY \"t0\".\"CustomerId\"", postgres);
        Checks.Has("HAVING COUNT(*) > @p0", postgres);

        Checks.Has("GROUP BY \"t0\".\"CustomerId\"", sqlite);
        Checks.Has("SUM(\"t0\".\"Amount\")", sqlite);
    }

    /// <summary>分组那条 case 判在集合导航那两条<b>之前</b>（二者在树上都是 <c>Enumerable.Count</c>）。
    /// 这条用例是那个插入位置的守门人：插错了支，这里得到的会是一句把相关子查询当成 <c>COUNT(*)</c> 的
    /// <c>GROUP BY</c> 句子，而分组侧的用例全都不会变红。</summary>
    [Fact]
    public void A_collection_navigation_count_is_still_a_correlated_subquery()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { o.Id, Lines = o.Items!.Count() })
            .ToSql();

        // 关联子查询的表位走 t1000+ 序列（与外层的 t0 撞不上名），键是子端 FK 对父端 PK。
        Checks.Has("(SELECT COUNT(*) FROM [QuickSqlOrderItem] AS [t1000] WHERE [t1000].[OrderId] = [t0].[Id])", sql);
        Checks.Absent("GROUP BY", sql);
    }

    // ── 护栏 ──────────────────────────────────────────────────────────

    [Fact]
    public void Grouping_twice_on_one_chain_is_refused()
    {
        // 「分组之后再分组」在链式类型面上写不出来（ISelectGrouping 没有 GroupBy），
        // 能走通的那一条是分组→选形状→再分组：Select 交回的仍是 IQuery，而模型里那颗 GroupBy 还在。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>()
                .GroupBy(o => o.CustomerId)
                .Select(g => new { CustomerId = g.Key, Orders = g.Count() })
                .GroupBy(x => x.Orders));

        Assert.Contains("只能分组一次", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Grouping_a_projected_query_is_refused_because_the_key_has_no_column_to_land_on()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>().Select(o => new { o.CustomerId }).GroupBy(x => x.CustomerId));

        Assert.Contains("投影之后不能再分组", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordering_written_before_the_grouping_is_refused_not_replaced()
    {
        // 分组之前排的序到分组之后没有对应物（组里的行不再有先后），而静默丢掉它只会得到
        // "我以为我排了序"的那一条句子（§5.8 禁静默退化）。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>().OrderBy(o => o.Amount).GroupBy(o => o.CustomerId));

        Assert.Contains("分组之前排的序", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Grouping_a_paged_query_is_refused_because_pages_and_groups_are_two_units()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>().Take(10).GroupBy(o => o.CustomerId));

        Assert.Contains("派生表", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_group_count_now_wraps_the_grouping_and_leaves_the_refusal_behind()
    {
        // 旧版这一格钉的是"当场抛"：按原模型补一个 COUNT(*) 得到的是"每组的行数"，
        // 一堆看起来完全合理的错数字（§5.12）。现在整份分组进内层、外层只剩 COUNT(*)，
        // 所以这一颗判据换成"它不再拒"：规划与编译全过，停在这册没有驱动的那一层（§3）。
        // 派生表本身的文本判据在 <c>DerivedTableTests</c>（那里说清了文本从哪来）。
        var grouped = orm().Select<Order>()
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Orders = g.Count() });

        var failure = Checks.Raised<DriverNotAvailableException>(
            () => grouped.CountAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Checks.Has("CreateConnection", failure.Message);

        // 分页的总数走的是同一个咽喉，而这一册读不到它：PagedAsync 先取行、后算总数。
        // 真库上那两页的总数在 samples/QuickSql.Sample 的 §10。
    }

    [Fact]
    public void A_composite_key_read_as_a_whole_is_refused()
    {
        // g.Key 整体（那个匿名对象）在库里没有对应物；静默挑第一颗键列会得到"少了一半条件的键"。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>()
                .GroupBy(o => new { o.State, o.CustomerId })
                .Select(g => new { K = g.Key })
                .ToSql());

        Assert.Contains("复合键", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Paging_a_grouped_query_without_ordering_refuses_the_primary_key_fallback()
    {
        // AutoOrderPrimaryKey 补的是主键序，而主键不是分组键 —— 补出来那句在库里不合法。
        var builder = new QuickSql.QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            PagingOrdering = PagingOrderingPolicy.AutoOrderPrimaryKey,
        };

        var failure = Checks.Raised<QuickSqlException>(
            () => new QuickSqlOrm(builder).Select<Order>()
                .GroupBy(o => o.CustomerId)
                .Skip(1)
                .Take(10)
                .Select(g => new { CustomerId = g.Key })
                .ToSql());

        Assert.Contains("补不出序", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_method_over_the_group_names_the_supported_set()
    {
        // 不放行到"为 Enumerable.Any 注册映射"那句通用报错：那里指的地方是错的
        // —— 缺的不是方言模板，是 GROUP BY 语义下没有的对应物。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>().GroupBy(o => o.CustomerId).Select(g => new { N = g.Any() }).ToSql());

        Assert.Contains("分组链上可用的聚合", failure.Message, StringComparison.Ordinal);
    }
}
