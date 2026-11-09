using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>全局过滤器贴在 <c>Include</c> 的那张左联表上时，落点是那颗节点的 <c>ON</c>，不是 <c>WHERE</c>
/// （§10.7 那条「左联右表的过滤器进 ON」判据的第二个落脚处）。
///
/// <para><b>为什么显式 join 与 Include 要分两处钉</b>：两条边进的是同一份 <c>WHERE</c>/<c>ON</c> 折叠，
/// 但它们在模型里的<em>来路</em>不同 —— 显式 join 在 <see cref="QueryModel.Joins"/> 里占一格，
/// 注入口按 <c>i &lt;= Joins.Count</c> 就认得出；Include 的 LEFT JOIN 是编译期照
/// <see cref="IncludeNode.OnCondition"/> 现造的，模型里没有对应项。所以「按 <c>Joins</c> 找左联」的那一岔
/// 对 Include 的表位<em>永远判不中</em>，过滤器就会掉进 <c>WHERE</c>，而这一格掉下去有两种事故形态，
/// 且都不报错：页面上的行凭空少掉（未匹配的补 NULL 行被那条过滤器一起筛光，左联静默退化成内联），
/// 以及 <c>Count</c> 那一句把联表省掉之后留下一句引用不到任何表位的 <c>[t1].[IsDeleted]</c>
/// （聚合计划不是 <c>EntityProjection</c>，<c>UsesIncludeJoins</c> 就不响）。</para>
///
/// <para><b>这一组不覆盖的东西</b>：<c>SplitQuery</c> 那条路上 Include 根本不成联表（子实体由第二条查询自己
/// 做根表位，它自己那一趟过滤器贴在 <c>WHERE</c> 上是对的），归 §13.4 的集成套件；<c>IncludeMany</c>
/// 不改写主查询 SQL，天然在这一格之外。<b>二级 <c>ThenInclude</c> 的折叠也没有离线靶子</b>：测试模型里
/// 唯一那条两级的链（<c>ReportTotal → ReportRow → Customer</c>）跨两座集群，编译器在成句之前就把它拒了。
/// 递归那一段因此只由应用侧现场覆盖（药师列表的两颗顶级 Include 走的是同一份折叠代码，但不是那一级）。</para></summary>
public sealed class IncludeFilterPlacementTests
{
    /// <summary>在册一条 <see cref="Customer"/> 过滤器的 orm —— 靶子是 Include 的目标表，
    /// 而根表 <see cref="Order"/> 上不注册任何东西，这样 <c>WHERE</c> 里出现的每一颗 <c>[t1]</c> 都是罪证。</summary>
    static QuickSqlOrm filtered()
    {
        var fresh = SqlServer.Orm();

        fresh.Filters.Add<Customer>(c => c.City != null);

        return fresh;
    }

    [Fact]
    public void The_filter_on_an_included_table_lands_in_the_on_clause_of_that_join()
    {
        var sql = filtered().Select<Order>().Include(o => o.Customer).ToSql();

        Golden.Verify("filter-include-on-clause", sql);

        Checks.Has("LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] AND [t1].[City] IS NOT NULL", sql);
    }

    [Fact]
    public void The_included_table_leaves_the_where_clause_alone()
    {
        var sql = filtered().Select<Order>().Where(o => o.Amount > 10m).Include(o => o.Customer).ToSql();

        // 这一句钉的是 Count 那一条事故的<em>前提</em>：分页要发的第二句把 Include 的联表省掉，
        // 只要 WHERE 里一颗 [t1] 都没有，那一句就还能发（库报"列不存在"是这条不变量被破的唯一回响）。
        // 真句子的形态在应用侧现场验（离线这只 orm 没有驱动，Count() 一步都走不到 SQL）。
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

        Checks.Absent("[t1]", where);
    }

    [Fact]
    public void The_callers_condition_stays_first_and_the_filter_does_not_move_it()
    {
        var sql = filtered().Select<Order>().Where(o => o.Amount > 10m).Include(o => o.Customer).ToSql();

        // 折叠方向与 §10.7 那条「用户条件 → 注册序」一致，只是过滤器换了一段落脚。
        Checks.Has("WHERE [t0].[Amount] > @p0", sql);
        Checks.Has("ON [t0].[CustomerId] = [t1].[Id] AND [t1].[City] IS NOT NULL", sql);
    }

    [Fact]
    public void Two_registrations_on_one_included_table_stack_inside_the_same_on_clause()
    {
        var target = SqlServer.Orm();

        target.Filters.Add<Customer>(c => c.City != null).Add<Customer>(c => c.Name != "黑名单");

        var sql = target.Select<Order>().Include(o => o.Customer).ToSql();

        // 两条都要叠在同一份已有 ON 上，而不是后一条把前一条顶掉（同一张右表上读原文会丢前一条）。
        // 参数号从 @p0 起：`City != null` 是 IS NOT NULL，一个占位都不占，所以唯一那颗参数是 Name。
        Checks.Has("ON [t0].[CustomerId] = [t1].[Id] AND [t1].[City] IS NOT NULL AND [t1].[Name] <> @p0", sql);
    }

    [Fact]
    public void Executing_the_same_chain_twice_folds_the_filter_once()
    {
        var query = filtered().Select<Order>().Include(o => o.Customer);

        // 折进的是节点副本。就地改写节点的话，链上的那一份会被第二次执行再叠一遍，
        // 而叠出来的句子结果不变、只在日志里越来越长 —— 那是最难查的一类回归。
        Assert.Equal(query.ToSql(), query.ToSql());
    }

    [Fact]
    public void A_filter_on_the_root_table_still_goes_into_the_where()
    {
        var target = SqlServer.Orm();

        target.Filters.Add<Order>(o => o.Amount > 1m);

        var sql = target.Select<Order>().Include(o => o.Customer).ToSql();

        // 根表没有「补 NULL 行」那一档，进 WHERE 与进 ON 等价，而 §10.7 定的是进 WHERE。
        // 这一条是防"把 Include 那一岔写成所有右表"时顺手把根表也搬走。
        Checks.Has("LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] WHERE [t0].[Amount] > @p0", sql);
    }

    [Fact]
    public void Ignoring_the_key_by_name_reaches_an_included_table_too()
    {
        var sql = filtered().Select<Order>().Include(o => o.Customer).IgnoreQueryFilters().ToSql();

        // 点名跳过之后那一段 ON 都不该存在 —— 跳过的是贴进去的那一条，不是整颗联表。
        // 判据写成"谓词那一段没有"而不是"[t1] 没有"：Include 的列本来就选进了 SELECT 清单。
        Checks.Has("LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
        Checks.Absent("IS NOT NULL", sql);
    }
}
