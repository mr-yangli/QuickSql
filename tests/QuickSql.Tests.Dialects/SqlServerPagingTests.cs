using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>分页（§9.4）。T-SQL 的 <c>OFFSET..FETCH</c> 有两个必须写死的性质：
/// 它<b>强制要求 ORDER BY</b>，而且 <c>FETCH</c> 只能挂在 <c>OFFSET</c> 之后 ——
/// 缺省偏移要落成常量 <c>0</c> 而不是整个子句省略。</summary>
public sealed class SqlServerPagingTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    static QuickSqlOrm orm(PagingOrderingPolicy policy)
        => new(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            PagingOrdering = policy,
        });

    [Fact]
    public void Ordered_skip_take_emits_offset_fetch_with_two_parameters()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.CreatedAt).Skip(20).Take(10).ToSql(out var parameters);

        Golden.Verify("paging-order-skip-take", sql);

        Checks.Has("ORDER BY [t0].[CreatedAt] ASC OFFSET ", sql);
        Checks.Has(" ROWS FETCH NEXT ", sql);
        Checks.Has(" ROWS ONLY", sql);

        // 行列数走参数而不是插值文本：这是"方言发射不拼字符串"（§3）在产出上的可执行版本。
        Assert.Equal(2, parameters.Count);
        Assert.Equal("@p0", parameters[0].Name);
        Assert.Equal(20, parameters[0].Value);
        Assert.Equal("@p1", parameters[1].Name);
        Assert.Equal(10, parameters[1].Value);
    }

    [Fact]
    public void Take_without_skip_still_writes_the_offset_clause_because_fetch_needs_it()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Take(5).ToSql(out var parameters);

        Golden.Verify("paging-take-only", sql);

        // 省略 OFFSET 会得到 "ORDER BY ... FETCH NEXT" —— T-SQL 直接语法错。
        Checks.Has("OFFSET 0 ROWS FETCH NEXT @p0 ROWS ONLY", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void Skip_without_take_emits_offset_only()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql();

        Golden.Verify("paging-skip-only", sql);

        Checks.Has("OFFSET @p0 ROWS", sql);
        Checks.Absent("FETCH NEXT", sql);
    }

    [Fact]
    public void Paging_ordering_is_position_independent_in_the_chain()
    {
        var ordered = orm().Select<Order>().OrderBy(o => o.Amount).Skip(2).Take(4);
        var paged = orm().Select<Order>().Skip(2).Take(4).OrderBy(o => o.Amount);

        // Skip/Take 不在落位时检查序，所以两种书写顺序必须产出同一条 SQL（§9.4）。
        Assert.Equal(ordered.ToSql(), paged.ToSql());
    }

    [Fact]
    public void Paging_without_ordering_throws_by_default()
    {
        var failure = Checks.Raised<QuickSqlException>(() => orm().Select<Order>().Skip(10).Take(10).ToSql());

        // 消息要同时说清"缺什么"和"怎么放开"，否则用户只会去翻源码找那个开关。
        Checks.Has("OFFSET", failure.Message);
        Checks.Has("PagingOrdering", failure.Message);
        Checks.Has("§9.4", failure.Message);
    }

    [Fact]
    public void AutoOrderPrimaryKey_picks_the_contract_primary_key()
    {
        var sql = orm(PagingOrderingPolicy.AutoOrderPrimaryKey).Select<Order>().Skip(10).Take(10).ToSql();

        Golden.Verify("paging-auto-primary-key", sql);

        Checks.Has("ORDER BY [t0].[Id] ASC OFFSET @p0", sql);
    }

    [Fact]
    public void Allow_lets_the_dialect_write_a_placeholder_ordering()
    {
        var sql = orm(PagingOrderingPolicy.Allow).Select<Order>().Skip(10).Take(10).ToSql();

        Golden.Verify("paging-placeholder-ordering", sql);

        // 占位序不是"没序"：没有它这段文本在 T-SQL 里根本执行不了，有了它行序仍由库决定。
        Checks.Has("ORDER BY (SELECT NULL) OFFSET @p0", sql);
    }

    [Fact]
    public void Ordering_from_the_entity_survives_paging()
    {
        // 名字里的另一半（"但聚合会剥掉"）不在这里：聚合没有离线 SQL 出口，`Count()` 是直接执行的，
        // 所以剥序只能由集成用例 `ReadPathTests.Count_strips_the_ordering_a_page_needs` 真库见证。
        // §13.9 原要求"每个 provider 的黄金样本里各出现一次"因此达不到，缺口记在 §5.12。
        var query = orm().Select<Order>().OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Skip(5).Take(5);

        Golden.Verify("paging-two-key-ordering", query.ToSql());

        Checks.Has("ORDER BY [t0].[CreatedAt] ASC, [t0].[Id] ASC OFFSET", query.ToSql());
    }

    [Fact]
    public void A_filtered_list_page_with_a_descending_first_key_is_one_statement()
    {
        // 真实列表页的形状：筛一个状态、按时间倒序、翻页。两键的升降序各写一遍才看得出
        // OrderByDescending/ThenBy 是分别落位的，而不是一整个 ORDER BY 共享一个方向。
        var sql = orm().Select<Order>()
            .Where(o => o.State == OrderState.Paid)
            .OrderByDescending(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .Skip(20)
            .Take(10)
            .ToSql();

        Golden.Verify("paging-filtered-desc-two-key", sql);

        Checks.Has("WHERE [t0].[State] = @p0 ORDER BY [t0].[CreatedAt] DESC, [t0].[Id] ASC OFFSET", sql);
    }

    [Fact]
    public void Explicit_alias_survives_the_paging_placeholder()
    {
        var sql = orm(PagingOrderingPolicy.Allow).Select<Order>().As("o").Take(3).ToSql();

        Golden.Verify("paging-alias-placeholder", sql);

        Checks.Has("FROM [QuickSqlOrder] AS [o]", sql);
        Checks.Has("OFFSET 0 ROWS FETCH NEXT @p0", sql);
    }

    // ── int 上限那一档：偏移与"多取一行"都不许溢成负数 ─────────────
    //
    // 证人是一颗真库的红：消费侧那一族仓储的"取全部"缺省就是 pageSize = int.MaxValue，
    // 而旧那一版把 <c>(page - 1) * size</c> 与 <c>size + 1</c> 都按 int 算 ——
    // 于是发出去的句子是 <c>LIMIT -2147483648</c>，库以 <c>2201W: LIMIT must not be negative</c> 收尾，
    // 症状是"一次可授权角色列表 500"。放行那一半（页 1 不再多取一行）只能在活库上读，
    // 归 <c>PagingOverflowPathTests</c>；这里钉的是"溢之前先响"那一半。

    [Fact]
    public async Task An_offset_past_the_int_ceiling_refuses_instead_of_sending_a_negative_offset()
    {
        // 页 3、每页 int.MaxValue 行：偏移是 4294967294，落在 int 之外。
        // 旧那一版算成 -2（乘出去的负数），库会给一句"OFFSET must not be negative"。
        var error = await Assert.ThrowsAsync<QuickSql.QuickSqlException>(() =>
            orm().Select<Order>().ToPagedAsync(3, int.MaxValue, null, TestContext.Current.CancellationToken));

        Checks.Has("超出 int 上限", error.Message);
        Checks.Has("4294967294", error.Message);
    }

    [Fact]
    public async Task The_last_representable_offset_still_passes_the_guard()
    {
        // 反向那一半：判据是"&gt; int.MaxValue"而不是"&gt;= 某个好看的数"。页 2、每页
        // int.MaxValue/2+1 的偏移正好落在 int 上限内 ⇒ 不许在闸口被拦。
        // 这一台是 NoDriverFactory，往下走必然红在驱动上 —— 所以要判的是"红在哪一颗"：
        // 拦在闸口才是 QuickSqlException，红在别处才算放行。
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            orm().Select<Order>().ToPagedAsync(2, int.MaxValue / 2 + 1, null, TestContext.Current.CancellationToken));

        Assert.IsNotType<QuickSql.QuickSqlException>(error);
    }
}
