using System.Linq.Expressions;
using QuickSql.Query;
using QuickSql.Query.Model;
using QuickSql.TestInfra;
using QuickSql.Translation;
using Xunit;
using QuickSql.Sql.Ast;

namespace QuickSql.Tests.Dialects;

/// <summary>§5.8 的护栏：翻译不了就抛，并且消息要说清"改哪里"。
/// 每一条都断言消息关键字，因为"抛了"本身不是交付物 —— 半年后一条 <c>"unsupported"</c> 什么都说不出。</summary>
public sealed class GuardrailTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    [Fact]
    public void Filtering_after_a_projection_is_refused()
    {
        var projected = orm().Select<Order>().Select(o => new { o.Id, Money = o.Amount });

        var failure = Checks.Raised<QuickSqlException>(() => projected.Where(x => x.Money > 1m).ToSql());

        Checks.Has("投影之后不能再加 WHERE", failure.Message);
    }

    [Fact]
    public void Writing_a_condition_after_paging_emits_the_sql_clause_order_not_the_chain_order()
    {
        // §16.15 已决（2026-09-21，用户裁决「先排序，后分页；排序/分页之后再筛条件不管」）：不加闸门、不改写、不包派生表。
        // 这条用例是那个决定的落地形态 —— 它原来叫 `Filtering_after_paging_should_be_refused`，按 skip 记账等一个不会来的修法。
        // 现在钉的是两件事：① 谓词恒在 `WHERE`、序恒在 `ORDER BY`、帽恒在 `OFFSET`，子句顺序由 SQL 的语法决定而不是由链式书写顺序决定；
        // ② 把「筛→序→页」这个正常写法（§5.11 的示例）整个反着写，产出的 SQL 一模一样 —— 证明书写先后没有进模型，
        // 于是"倒着写会得到另一种语义"这个前提本来就不成立，也就不需要闸门去挡。
        var normal = orm().Select<Order>()
            .Where(o => o.Amount > 10m).OrderBy(o => o.CreatedAt).Skip(10)
            .ToSql();

        var reversed = orm().Select<Order>()
            .OrderBy(o => o.CreatedAt).Skip(10).Where(o => o.Amount > 10m)
            .ToSql();

        Checks.Has("WHERE [t0].[Amount] > @p0 ORDER BY [t0].[CreatedAt] ASC OFFSET @p1 ROWS", reversed);

        Assert.Equal(normal, reversed);
    }

    [Fact]
    public void Ordering_after_a_projection_is_refused()
    {
        var projected = orm().Select<Order>().Select(o => o.Amount);

        var failure = Checks.Raised<QuickSqlException>(() => projected.OrderBy(x => x).ToSql());

        Checks.Has("投影之后不能再排序", failure.Message);
    }

    [Fact]
    public void Naming_the_alias_after_conditions_are_stacked_is_refused()
    {
        var filtered = orm().Select<Order>().Where(o => o.State == OrderState.Placed);

        var failure = Checks.Raised<QuickSqlException>(() => filtered.As("ord").ToSql());

        // 反过来的顺序会写出 FROM [QuickSqlOrder] AS [ord] 配 WHERE [t0].[State]：库报"无法绑定多部分标识符"。
        Checks.Has("As(\"ord\")", failure.Message);
        Checks.Has("最前面", failure.Message);
    }

    [Fact]
    public void A_reference_navigation_used_whole_as_a_value_is_refused_with_a_read_suggestion()
    {
        // 引用导航在谓词/投影里<em>读它的列</em>那一档现在由 §5.2 的自动联表接住（见证在
        // <see cref="NavigationAutoJoinTests"/>），这一条留的是还没接住的那一档：整根导航当一个值。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>().Where(o => (object)o.Customer! == null).ToSql());

        Checks.Has("t0.Customer", failure.Message);
        Checks.Has("再往下写一格", failure.Message);
    }

    [Fact]
    public void A_column_member_without_a_sql_shape_is_refused_rather_than_dropped()
    {
        // 回归：认不出来的成员读法曾经退化成"把宿主列原样发出去"，于是 Kind 变成了 CreatedAt ——
        // 语法全对、结果全错。静默少一层函数比报错严重得多。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>().Where(o => o.CreatedAt.Kind == DateTimeKind.Utc).ToSql());

        Checks.Has("DateTime.Kind", failure.Message);
    }

    [Fact]
    public void The_date_part_reads_that_do_map_emit_provider_templates()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CreatedAt.Year == 2026 && o.PaidAt!.Value.Month > 3 && o.CustomerName!.Length > 2)
            .ToSql();

        Golden.Verify("members-date-and-length", sql);

        Checks.Has("DATEPART(year, [t0].[CreatedAt]) = @p0", sql);
        Checks.Has("DATEPART(month, [t0].[PaidAt]) > @p1", sql);
        Checks.Has("LEN([t0].[CustomerName]) > @p2", sql);
    }

    [Fact]
    public void An_unregistered_method_names_the_exact_register_call_that_would_fix_it()
    {
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>().Where(o => o.CustomerName!.PadRight(8) == "x").ToSql());

        Checks.Has("System.String.PadRight", failure.Message);
        Checks.Has("Expressions.Register", failure.Message);
    }

    [Fact]
    public void Aggregating_over_a_navigation_collection_produces_a_correlated_subquery()
    {
        var sql = orm().Select<Order>().Where(o => o.Items!.Any(i => i.Quantity > 2)).ToSql();

        Checks.Has("EXISTS", sql);
        Checks.Has("SELECT 1", sql);
    }

    [Fact]
    public void Count_over_a_navigation_collection_produces_a_scalar_subquery()
    {
        var sql = orm().Select<Order>().Where(o => o.Items!.Count() > 5).ToSql();

        Checks.Has("COUNT(*)", sql);
    }

    [Fact]
    public void Count_on_a_captured_IQuery_produces_a_scalar_subquery()
    {
        var activeOrders = orm().Select<Order>().Where(o => o.State == OrderState.Placed);

        var sql = orm().Select<Customer>().Where(c => activeOrders.Count() > 5).ToSql();

        Checks.Has("SELECT COUNT(*)", sql);
        Checks.Has("FROM", sql);
    }

    [Fact]
    public void Contains_on_a_captured_IQuery_produces_an_IN_subquery()
    {
        var activeCustomerIds = orm().Select<Order>()
            .Where(o => o.State == OrderState.Placed)
            .Select(o => o.CustomerId);

        var sql = orm().Select<Customer>().Where(c => activeCustomerIds.Contains(c.Id)).ToSql();

        Checks.Has("IN", sql);
        Checks.Has("SELECT", sql);
    }

    [Fact]
    public void Sum_on_a_captured_IQuery_produces_a_scalar_subquery()
    {
        var orderAmounts = orm().Select<Order>().Where(o => o.State == OrderState.Paid);

        var sql = orm().Select<Customer>().Where(c => orderAmounts.Sum(o => o.Amount) > 1000).ToSql();

        Checks.Has("SUM", sql);
        Checks.Has("SELECT", sql);
    }

    [Fact]
    public void Max_on_a_captured_IQuery_produces_a_scalar_subquery()
    {
        var orderAmounts = orm().Select<Order>();

        var sql = orm().Select<Customer>().Where(c => orderAmounts.Max(o => o.Amount) > 500).ToSql();

        Checks.Has("MAX", sql);
    }

    [Fact]
    public void Min_on_a_captured_IQuery_produces_a_scalar_subquery()
    {
        var orderAmounts = orm().Select<Order>();

        var sql = orm().Select<Customer>().Where(c => orderAmounts.Min(o => o.Amount) < 10).ToSql();

        Checks.Has("MIN", sql);
    }

    [Fact]
    public void Average_on_a_captured_IQuery_produces_a_scalar_subquery()
    {
        var orderAmounts = orm().Select<Order>();

        var sql = orm().Select<Customer>().Where(c => orderAmounts.Average(o => o.Amount) > 100).ToSql();

        Checks.Has("AVG", sql);
    }

    [Fact]
    public void A_lambda_with_more_parameters_than_from_positions_is_refused()
    {
        // 链式入口下多写一个形参编译不过（Select/Where 的实参类型就是 Expression<Func<T,…>>），
        // 所以这道守卫防的是 Scope 这个公开接缝的调用方 —— 它同时挡住了 <c>(x, index)</c> 这类
        // "第二个形参是行号而不是表"的形态：绑上去会得到一个能编译、结果错列的模型。
        var model = OrderOnly();

        Expression<Func<Order, int, bool>> mistyped = (o, index) => index > 1;

        var failure = Checks.Raised<UntranslatableExpressionException>(() => Scope.Create(model, mistyped));

        Checks.Has("2 个形参", failure.Message);
        Checks.Has("只有 1 张表", failure.Message);
    }

    [Fact]
    public void A_lambda_whose_parameter_type_does_not_match_the_position_is_refused()
    {
        // 形参按位置绑表（§5.1），所以类型对不上就是形状对不上：这里拿 Order 的表位配 Customer 的形参。
        var model = OrderOnly();

        Expression<Func<Customer, bool>> mistyped = c => c.City == "上海";

        var failure = Checks.Raised<UntranslatableExpressionException>(() => Scope.Create(model, mistyped));

        Checks.Has("第 1 个形参是 Customer", failure.Message);
        Checks.Has("表位 t0 的实体是 Order", failure.Message);
    }

        /// <summary>一个只有 <c>t0</c> 的根模型 —— 两道 <see cref="Scope"/> 守卫都要的是「FROM 里一张表」。</summary>
    static QueryModel OrderOnly()
    {
        var contract = SqlServer.Orm().Schemas.Contract<Order>();

        return new QueryModel { Tables = [new TableRef(0, "t0", contract.Table, contract)], Plan = new EntityProjection(0) };
    }

    [Fact]
    public void P1_features_throw_instead_of_looking_supported()
    {
        var query = orm().Select<Order>();

        // AsSplitQuery 已实现（§5.7 拆分查询），不再抛 —— 验证它安静返回一条新链。
        var split = query.AsSplitQuery();
        Assert.NotNull(split);
    }

    [Fact]
    public async Task A_value_aggregate_over_a_paged_query_refuses_while_the_count_family_goes_through()
    {
        var query = orm().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(10);

        // 带页的聚合现在分两档：COUNT 一族整份模型进内层、外层只发 COUNT(*)，所以它不再拒；
        // 而 SUM 要的是"外层引用内层的某一列"，那一步缺派生行的列契约（§5.12 的 P1 那一格）。
        // 这里传的 token 而不是 default：查询真挂住时 xunit 要能取消它并把这条报成失败而不是拖死整轮。
        var failure = await Assert.ThrowsAsync<QuickSqlException>(() => query.SumAsync(o => o.Amount, TestContext.Current.CancellationToken));

        Checks.Has("派生行的列契约", failure.Message);
        Checks.Has("orm.Ado", failure.Message);
    }

    [Fact]
    public void Sql_generation_never_touches_the_driver_but_execution_does()
    {
        var query = orm().Select<Order>().Where(o => o.Id > 1).OrderBy(o => o.Id).Take(2);

        // ToSql/ToLogSql/Explain 全部跑完，而探针工厂一次都没被调用（§3 的「ToSql() 不连库」）。
        Checks.Has("OFFSET", query.ToSql());
        Checks.Has("FETCH NEXT", query.ToLogSql());
        Assert.NotNull(query.Explain().ModelText);

        // 而真执行必须报"驱动没就位"—— 它把失败点钉在第 5 层，而不是前面四层里的任何一层。
        var failure = Checks.Raised<DriverNotAvailableException>(() => query.ToList().Count);

        Checks.Has("CreateConnection", failure.Message);
        Checks.Has("drivers/", failure.Message);
    }
}
