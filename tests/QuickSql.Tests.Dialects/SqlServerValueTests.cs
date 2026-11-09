using System.Data;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>值与文本的落点（§4 的 binder 面 + §10.4 的字符集面）。
/// 这一组全部在"没有驱动"的前提下跑：绑定与内联是 provider 的职责，不是驱动的职责。</summary>
public sealed class SqlServerValueTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    [Fact]
    public void Strings_bind_as_unicode_with_the_argument_length()
    {
        var query = orm().Select<Order>().Where(o => o.CustomerName == "老王");

        query.ToSql(out var parameters);

        Assert.Equal(DbType.String, parameters[0].DbType);
        Assert.Equal(2, parameters[0].Size);

        // Size 按实参给而不是按契约的 nvarchar(64) 给：SqlParameter 在 Size 小于内容时是截断，不是报错。
        var longText = string.Concat(Enumerable.Repeat('字', 200));

        orm().Select<Order>().Where(o => o.CustomerName == longText).ToSql(out var wide);

        Assert.Equal(200, wide[0].Size);
    }

    [Fact]
    public void Enums_reach_the_wire_as_the_underlying_int()
    {
        var sql = orm().Select<Order>().Where(o => o.State == OrderState.Paid).ToSql(out var parameters);

        Golden.Verify("where-enum", sql);

        // §9.3：枚举存 INT，而且驱动不会替我们转 —— 绑成枚举实例会按名字比较，读回来全是空结果。
        Assert.Equal(2, parameters[0].Value);
        Assert.Equal(DbType.Int32, parameters[0].DbType);
    }

    [Fact]
    public void ToLogSql_inlines_the_types_whose_literal_form_is_unambiguous()
    {
        var moment = new DateTime(2026, 3, 4, 5, 6, 7, 890);

        var log = orm().Select<Order>()
            .Where(o => o.CustomerName == "老王" && o.CreatedAt > moment && o.Amount >= 12.5m && o.State == OrderState.Shipped && o.PaidAt == null)
            .ToLogSql();

        // N 前缀：没有它，字串在解析期就被降级成排序规则的代码页（§10.4 的第一行实测）。
        Checks.Has("[t0].[CustomerName] = N'老王'", log);
        Checks.Has("> '2026-03-04T05:06:07.890'", log);
        Checks.Has(">= 12.5", log);
        Checks.Has("[t0].[State] = 3", log);
        Checks.Has("[t0].[PaidAt] IS NULL", log);
        Checks.Absent("@p", log);
    }

    [Fact]
    public void ToLogSql_keeps_the_placeholder_including_its_prefix_when_the_binder_refuses()
    {
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

        var sql = new DialectSql(
            "WHERE [t0].[Span] = @p0 AND [t0].[Count] = @p1",
            [new AdoParameter("@p0", TimeSpan.FromMinutes(3), DbType.Object, 0), new AdoParameter("@p1", 7, DbType.Int32, 0)]);

        var log = sql.ToLogSql(provider);

        // 回归：非内联值曾经被写成裸参数名（丢掉 @），产出的根本不是一条能执行的 SQL。
        Checks.Has("[t0].[Span] = @p0", log);
        Checks.Has("[t0].[Count] = 7", log);

        // binder 明确说自己不行 —— 上面那条"保留占位符"的分支才有依据，不是字符串匹配碰运气。
        Assert.False(provider.Values.TryInlineLiteral(TimeSpan.FromMinutes(3), null, out _));
    }

    /// <summary>浮点字面量的三档，一格都不许靠记性（金仓侧的同一条在
    /// <c>KingbaseDialectTests.The_floating_point_literal_keeps_the_exponent_form_and_refuses_the_non_finite_ones</c>，
    /// 达梦侧的在 <c>DamengDialectTests.A_floating_point_literal_is_written_as_is_and_only_the_non_finite_ones_are_refused</c>：
    /// 三处同源同错，所以三家各自钉一次 —— 而第三家那一条的<em>形状</em>与前两家不同，那一格本身就是发现）。</summary>
    [Fact]
    public void A_floating_point_literal_keeps_the_exponent_form_and_refuses_the_non_finite_ones()
    {
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

        // 纯整数形式要补：不补的话 `3` 在 float 列上是 int 常量隐式转，小数位这一档读不出来。
        Assert.True(provider.Values.TryInlineLiteral(3d, null, out var whole));
        Assert.Equal("3.0", whole);

        Assert.True(provider.Values.TryInlineLiteral(2.5f, null, out var fraction));
        Assert.Equal("2.5", fraction);

        // 指数式不许补：`1E-08` 本身就是合法的 float 常量，补成 `1E-08.0` 是语法错误（真库实测，见 binder 注释）。
        Assert.True(provider.Values.TryInlineLiteral(1e-08, null, out var tiny));
        Assert.Equal("1E-08", tiny);

        // 非有限值压根没有 T-SQL 字面量（TRY_CAST('NaN' AS float) 为 NULL），所以拒。
        // -∞ 是这一档真正的钉子：它打头那颗 `-` 是合法的浮点首字符，只判首字符的写法会把它补成 `-∞.0`。
        Assert.False(provider.Values.TryInlineLiteral(double.NaN, null, out _));
        Assert.False(provider.Values.TryInlineLiteral(double.PositiveInfinity, null, out _));
        Assert.False(provider.Values.TryInlineLiteral(double.NegativeInfinity, null, out _));
        Assert.False(provider.Values.TryInlineLiteral(float.NegativeInfinity, null, out _));

        var log = new DialectSql(
            "WHERE [t0].[Rate] = @p0 AND [t0].[Scale] = @p1",
            [new AdoParameter("@p0", 1e-08, DbType.Double, 0), new AdoParameter("@p1", double.NaN, DbType.Double, 0)]).ToLogSql(provider);

        Checks.Has("[t0].[Rate] = 1E-08", log);
        Checks.Has("[t0].[Scale] = @p1", log);
    }

    [Fact]
    public void Like_patterns_escape_the_argument_before_adding_wildcards()
    {
        var query = orm().Select<Order>();

        Golden.Verify("where-like", query.Where(o => o.CustomerName!.Contains("50%_off")).ToSql());

        var contains = query.Where(o => o.CustomerName!.Contains("50%_off")).ToSql(out var middle);

        Checks.Has("LIKE @p0 ESCAPE '\\'", contains);

        // 两侧的通配符是翻译器加的，必须活过转义；用户给的那一段里 % 和 _ 才是字面量。
        // 转义顺序错了会静默少行（Contains 退化成全等匹配），这是本文件最值钱的一条断言。
        Assert.Equal("%50\\%\\_off%", middle[0].Value);

        query.Where(o => o.CustomerName!.EndsWith("50%_off")).ToSql(out var ends);
        query.Where(o => o.CustomerName!.StartsWith("50%_off")).ToSql(out var starts);

        // 通配符落在哪一侧由算子决定：EndsWith 在前、StartsWith 在后。两条断言写成同一个值
        // 就等于没测（这一版曾经把它们都写成后缀通配符，绿得很安静）。
        Assert.Equal("%50\\%\\_off", ends[0].Value);
        Assert.Equal("50\\%\\_off%", starts[0].Value);
    }

    [Fact]
    public void Closure_collections_become_in_lists_and_an_empty_one_becomes_the_dialect_false_predicate()
    {
        var ids = new[] { 1, 2, 3 };

        var sql = orm().Select<Order>().Where(o => ids.Contains(o.Id)).ToSql(out var parameters);

        Golden.Verify("where-in", sql);

        Checks.Has("[t0].[Id] IN (@p0, @p1, @p2)", sql);
        Assert.Equal(3, parameters.Count);

        // 黄金样本回归：IN 自己就是谓词，Booleanized 的形状表漏掉它就会写成 `IN (…) = 1`，
        // 那是一条只有真连库才会暴露的语法错 —— 而这里连红都不会红。
        Checks.Absent(") = 1", sql);

        var none = Array.Empty<int>();

        var alwaysFalse = orm().Select<Order>().Where(o => none.Contains(o.Id)).ToSql();

        // 1 = 0 由方言给：DM 系的恒假写法不同，硬编码在这里就成了方言泄漏（§3 第 4 层）。
        Checks.Has("WHERE 1 = 0", alwaysFalse);
        Checks.Absent("1 = 0 = 1", alwaysFalse);
    }

    /// <summary>枚举数组的 Contains 与 <c>int[]</c> 那条<b>不是同一个形状</b>：在 .NET 10 上它编成 3 元的
    /// <c>MemoryExtensions.Contains</c>，末尾多带一个值为 null 的 <c>IEqualityComparer&lt;T&gt;</c> 实参。
    /// 集成套件里那条管"跑不跑得通"，这条管"多出来的一元是不是被当没看见"（§13.1）。</summary>
    [Fact]
    public void Enum_arrays_become_in_lists_of_their_int_storage()
    {
        var states = new[] { OrderState.Placed, OrderState.Shipped };

        var sql = orm().Select<Order>().Where(o => states.Contains(o.State)).ToSql(out var parameters);

        Checks.Has("[t0].[State] IN (@p0, @p1)", sql);
        Checks.Absent(") = 1", sql);

        Assert.Equal(new object[] { 1, 3 }, parameters.Select(p => p.Value!).ToArray());
        Assert.All(parameters, p => Assert.Equal(DbType.Int32, p.DbType));
    }

    /// <summary>只认编译期传下来的那个 null 比较器。真给了自定义比较器还照默认语义发射，
    /// 就是把用户显式指定的相等规则偷偷换成另一套 —— 宁缺毋滥（§5.8）。</summary>
    [Fact]
    public void An_explicit_element_comparer_is_refused_instead_of_silently_dropped()
    {
        var states = new[] { OrderState.Placed };

        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>().Where(o => states.Contains(o.State, new ByUnderlying())).ToSql());

        Checks.Has("默认比较器", failure.Message);
    }

    /// <summary>只为触发上一条守卫而存在的比较器：它的存在本身就是那条实参"不能被当没看见"的理由。</summary>
    sealed class ByUnderlying : IEqualityComparer<OrderState>
    {
        public bool Equals(OrderState x, OrderState y) => x == y;

        public int GetHashCode(OrderState state) => (int) state;
    }

    /// <summary>绑定的精度口径（§9.3）。<c>DbType.DateTime</c> 上线的是遗留 <c>datetime</c>，
    /// 会先把毫秒取整到 1/300 秒才落进 <c>datetime2(3)</c> 那一列，而产出的 SQL 文本一模一样 ——
    /// 所以这一条只能钉 DbType 本身；等值查询真跑一遍在集成套件的 <c>Timestamp_milliseconds…</c>。</summary>
    [Fact]
    public void Timestamps_bind_as_datetime2_not_the_legacy_datetime()
    {
        var moment = new DateTime(2026, 3, 4, 5, 6, 7, 456);

        orm().Select<Order>().Where(o => o.PaidAt == moment).ToSql(out var parameters);

        Assert.Equal(DbType.DateTime2, parameters[0].DbType);
    }

    [Fact]
    public void String_addition_becomes_concat_because_t_sql_null_poisons_the_plus()
    {
        var sql = orm().Select<Order>().Select(o => new { Full = o.CustomerName + "/" + o.Note }).ToSql();

        // C# 把 null 当空串，T-SQL 的 + 会让整个表达式变 NULL：不折成 CONCAT 就是静默错结果（§5.8）。
        Checks.Has("CONCAT(CONCAT(", sql);
        Checks.Absent(" + ", sql);
    }

    [Fact]
    public void Conditionals_and_coalesce_keep_both_branches()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Label = o.State == OrderState.Cancelled ? "已取消" : o.CustomerName, Fallback = o.Note ?? "-" })
            .ToSql();

        Golden.Verify("projection-case", sql);

        Checks.Has("CASE WHEN", sql);
        Checks.Has(" ELSE ", sql);
        Checks.Has("COALESCE([t0].[Note], @p", sql);
    }

    [Fact]
    public void Registered_functions_render_their_templates_with_arity_checked()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026 && Math.Round(o.Amount, 2) > 1m)
            .ToSql();

        Golden.Verify("functions", sql);

        // LEN 而不是 LENGTH；DATEPART(year) 而不是 EXTRACT —— 模板表按家登记，方言不许猜。
        Checks.Has("LEN([t0].[CustomerName]) > @p0", sql);
        Checks.Has("DATEPART(year, [t0].[CreatedAt]) = @p1", sql);
        Checks.Has("ROUND([t0].[Amount], @p2) > @p3", sql);
    }

    [Fact]
    public void A_method_call_with_a_receiver_counts_it_in_the_arity_key()
    {
        // 注册键要含接收者：`a.Replace(b, c)` 递进模板的是三个实参。登记成 /2 与 /1 的那两格永远查不到，
        // 于是这两条链在 v0.22 之前抛的是 UntranslatableExpressionException —— 接第二个方言族才撞见（§13.5）。
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Replace("甲", "乙").IndexOf("乙") >= 0)
            .ToSql();

        Golden.Verify("string-replace-and-indexof", sql);

        // CHARINDEX 的参数是反的（子串在前），而 -1 把"找不到给 0"换回 C# 的"找不到给 -1"。
        Checks.Has("REPLACE([t0].[CustomerName], @p0, @p1)", sql);
        Checks.Has("(CHARINDEX(@p2, REPLACE([t0].[CustomerName], @p0, @p1)) - 1) >= @p3", sql);
    }
}
