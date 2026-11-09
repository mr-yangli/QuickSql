using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>SELECT 版式（§3 的第 2→3 层）。断言分两类：黄金样本管"整条文本长什么样"，
/// 结构断言管"凭什么长这样"—— 列序来自契约、导航属性不成列、别名由位置生成。
/// 只有黄金样本的话，一次把别名生成规则改掉会让二十个样本同时变红而说不出改了哪条规则。</summary>
public sealed class SqlServerSelectTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    [Fact]
    public void Entity_projection_emits_contract_columns_in_declaration_order()
    {
        var sql = orm().Select<Order>().ToSql();

        Golden.Verify("select-entity", sql);

        // 列序 = 契约列序（§10.3）：物化按位置读，所以这里断言的是顺序而不是"包含"。
        string[] expected =
        [
            "[t0].[Id]", "[t0].[CustomerName]", "[t0].[CustomerId]", "[t0].[Amount]",
            "[t0].[State]", "[t0].[CreatedAt]", "[t0].[PaidAt]", "[t0].[Note]",
        ];

        Assert.Equal(expected, Checks.Columns(sql));

        // 导航属性不可映射（元数据层跳过），漏跳就会生成一个库里根本不存在的列。
        Checks.Absent(".[Customer]", sql);
        Checks.Absent(".[Items]", sql);
    }

    [Fact]
    public void Table_name_and_alias_come_from_the_contract_and_the_position()
    {
        var sql = orm().Select<Order>().ToSql();

        // 别名由位置生成（§5.1），所以 C# 里那个 <c>o</c> 根本不影响产出。
        Checks.Has("FROM [QuickSqlOrder] AS [t0]", sql);
        Checks.Has("SELECT [t0].[Id]", sql);
    }

    [Fact]
    public void Several_predicates_fold_left_to_right_into_one_flat_and_chain()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.State == OrderState.Paid)
            .Where(o => o.Amount > 100m)
            .Where(o => o.PaidAt != null)
            .ToSql();

        Golden.Verify("select-three-where", sql);

        Assert.Equal(2, Checks.Occurrences(sql, " AND "));

        // 同一个条件写成三条链式 Where 还是一个 && lambda，产出文本相同但模型不同：
        // 模型留的是「折叠前的原样集合」（两条谓词 vs 一条 AND 节点），所以 §13.2 的判据必须落在 SQL 文本上，
        // 而 StableKey 比的是"同一串算子的两份写法是否归约到同一个模型"（§13.2 的跨 arity 等价就是它的应用场合）。
        // 把这两个判据混为一谈，等价性测试会假红一片。
        var viaLambda = orm().Select<Order>()
            .Where(o => o.State == OrderState.Paid && o.Amount > 100m && o.PaidAt != null)
            .ToSql();

        Assert.Equal(sql, viaLambda);
    }

    [Fact]
    public void Mixed_and_or_get_parenthesis_only_where_nesting_differs()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerId == 1 && (o.Amount > 10m || o.State == OrderState.Draft))
            .ToSql();

        Golden.Verify("select-and-or", sql);

        Checks.Has("AND ([t0].[Amount] > @p1 OR [t0].[State] = @p2)", sql);

        // 同种连接词不套括号（翻译器已折成扁平右倾链）：多套一层括号在语义上是噪音，
        // 在黄金样本上则会让"只是多加了一个条件"看起来像版式变更。
        Checks.Absent(" AND (", orm().Select<Order>().Where(o => o.State == OrderState.Paid && o.Amount > 100m).ToSql());
    }

    [Fact]
    public void Null_comparison_becomes_is_null_rather_than_equal_to_null()
    {
        var query = orm().Select<Order>();

        Checks.Has("WHERE [t0].[PaidAt] IS NULL", query.Where(o => o.PaidAt == null).ToSql());
        Checks.Has("WHERE [t0].[PaidAt] IS NOT NULL", query.Where(o => o.PaidAt != null).ToSql());

        // C# 里 == null 与 IsNullOrEmpty 不是一回事，但 SQL 侧都必须两分支齐全，否则 NULL 行会漏掉。
        var empty = query.Where(o => string.IsNullOrEmpty(o.Note)).ToSql();

        Checks.Has("[t0].[Note] IS NULL", empty);
        Checks.Has("OR [t0].[Note] = @p0", empty);
        Checks.Has("OR [t0].[Note] = N''", query.Where(o => string.IsNullOrEmpty(o.Note)).ToLogSql());
    }

    [Fact]
    public void Value_projection_emits_exactly_one_column()
    {
        var sql = orm().Select<Order>().Select(o => o.Amount).ToSql();

        Golden.Verify("select-value", sql);

        Assert.Equal(["[t0].[Amount]"], Checks.Columns(sql));
    }

    [Fact]
    public void Anonymous_projection_names_every_slot()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { o.Id, Money = o.Amount * 2m, Who = o.CustomerName!.ToUpper() })
            .ToSql();

        Golden.Verify("select-anonymous", sql);

        // 直接列不补 AS（列序才是真相，多打别名只会把样本搞脏），计算列才必须有名。
        Checks.Has("[t0].[Id], ", sql);
        Checks.Has("AS [Money]", sql);
        Checks.Has("AS [Who]", sql);
    }

    [Fact]
    public void Constructor_dto_projection_keeps_argument_order_as_column_order()
    {
        var sql = orm().Select<Order>()
            .Select(o => new OrderSummary(o.Id, o.CustomerName, o.Amount))
            .ToSql();

        Golden.Verify("select-dto", sql);

        // 构造器形态没有属性名可反射，参数顺序就是物化顺序（§10.3 的位置读法在这里最脆）。
        Assert.Equal(["[t0].[Id]", "[t0].[CustomerName]", "[t0].[Amount]"], Checks.Columns(sql));
    }

    [Fact]
    public void Distinct_and_custom_alias_land_in_the_right_slots()
    {
        var query = orm().Select<Order>().As("ord");

        Checks.Has("FROM [QuickSqlOrder] AS [ord]", query.ToSql());

        var sql = orm().Select<Order>().As("ord").Where(o => o.State == OrderState.Placed).Distinct().ToSql();

        Golden.Verify("select-alias-distinct", sql);

        // 别名在最前面时才安全：谓词持有的是当时的表位实例（见 QueryEngine.Alias 的守卫）。
        Checks.Has("WHERE [ord].[State] = @p0", sql);
        Checks.Absent("[t0]", sql);
    }

    [Fact]
    public void Explain_carries_the_model_text_without_touching_the_driver()
    {
        var plan = orm().Select<Order>().Where(o => o.Id > 7).OrderBy(o => o.CreatedAt).Take(3).Explain();

        Checks.Has("PLAN Entity(t0)", plan.ModelText);
        Checks.Has("ORDER t0.CreatedAt ASC", plan.ModelText);
        Assert.Equal(DbKind.SqlServer, plan.Provider.Kind);
        Assert.Empty(plan.HookReplay);

        // 参数值留在参数里，模型文本里没有：StableKey 是"两份写法是否等价"的判据（§13.2），带值就没法比了。
        Checks.Absent("@p", plan.ModelText);
        Assert.Equal(7, plan.Sql.Parameters[0].Value);
    }
}
