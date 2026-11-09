using QuickSql.Execution;
using QuickSql.Materialization;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§11 逃生舱的离线册：一句<b>手写的 SQL 文本</b>加上一个<b>参数对象</b>，
/// 经过 <see cref="RawSql"/> 折成 <see cref="DialectSql"/>，再由 <see cref="RawRowReader"/> 把行装回 CLR。
///
/// <para><b>这一族口唯一的新逻辑就是「命名」与「对位」两件事</b>，所以这一册也只判这两件：
/// 文本从头到尾一颗不动（不rewrite 占位符、不补引号、不校验语法 —— 那三件事任一做了，用户写的就不再是他写的那一句），
/// 参数名 = 本家前缀 + 属性名；行的那一边按列名对属性名，先原样再忽略大小写。
/// <b>执行本身不在这里测</b>：那三个字符串重载只是把 <c>(sql, args)</c> 折成 <see cref="DialectSql"/> 再交给已有的低层口，
/// AOP、会话连接复用、事务环境、超时、摘前缀全是同一条链，没有第二份实现可测（真库那一半的见证在
/// <c>QuickSql.Sample</c> 的 §9，与分组那一批同格）。</para>
///
/// <para><b>这里判不动的三件事，别读成判过了</b>：① 真驱动对参数名大小写的脾气（离线侧没有假 ADO 栈，§14 记着这一格）；
/// ② <c>byte[]</c> 与流式列（<c>GetBytes</c> 分片读）在真驱动上的往返；③ 每一家前缀的实际文本形状由
/// <see cref="ISqlDialect.ParameterPrefix"/> 那颗声明位负责，本册第 3 条只是把它的读数复述到参数名上，
/// 不是又一份方言见证。</para></summary>
public sealed class RawAdoTests
{
    const string Sql = "SELECT Id, Total FROM T WHERE Id = @id";

    // ── 命名那一半：文本不动，名字 = 前缀 + 属性名 ──

    [Fact]
    public void The_text_is_handed_over_untouched()
    {
        var sql = "  select   [a b] as \"X\"  -- 别动这行\n  from t where a=b ";
        var folded = RawSql.ToDialectSql(sql, null, '@');

        // 原样到最后一个空白：这一口的全部前提是"这句话是你自己写的"。
        Assert.Equal(sql, folded.Text);
        Assert.Empty(folded.Parameters);
    }

    [Fact]
    public void A_parameter_name_is_the_home_prefix_plus_the_property_name_as_written()
    {
        var folded = RawSql.ToDialectSql(Sql, new { id = 7, total = 12.5m }, '@');

        // 名字照属性名的原样大小写（这里是小写 id），而文本里的占位符由 SQL 作者写：
        // 两边必须同形，这一格就是"谁负责哪半"的那条分工。
        Assert.Collection(folded.Parameters,
            first =>
            {
                Assert.Equal("@id", first.Name);
                Assert.Equal((object) 7, first.Value);
            },
            second =>
            {
                Assert.Equal("@total", second.Name);
                Assert.Equal((object) 12.5m, second.Value);
            });
    }

    [Fact]
    public void Every_family_names_its_parameters_with_its_own_prefix()
    {
        // 与生成 SQL 那一条路同一个前缀来源（ISqlDialect.ParameterPrefix），所以这里不是第二份方言表，
        // 而是"逃生舱没有自己的一套命名"这一句的证据。
        Assert.Equal("@Id", Assert.Single(RawSql.ToDialectSql(Sql, new { Id = 1 }, SqlServer.Orm().Provider.Dialect.ParameterPrefix).Parameters).Name);
        Assert.Equal("@Id", Assert.Single(RawSql.ToDialectSql(Sql, new { Id = 1 }, MySql.Orm().Provider.Dialect.ParameterPrefix).Parameters).Name);
        Assert.Equal("@Id", Assert.Single(RawSql.ToDialectSql(Sql, new { Id = 1 }, Sqlite.Orm().Provider.Dialect.ParameterPrefix).Parameters).Name);
        Assert.Equal(":Id", Assert.Single(RawSql.ToDialectSql(Sql, new { Id = 1 }, Oracle.Orm().Provider.Dialect.ParameterPrefix).Parameters).Name);
        Assert.Equal(":Id", Assert.Single(RawSql.ToDialectSql(Sql, new { Id = 1 }, Dameng.Orm().Provider.Dialect.ParameterPrefix).Parameters).Name);
        Assert.Equal(":Id", Assert.Single(RawSql.ToDialectSql(Sql, new { Id = 1 }, Xugu.Orm().Provider.Dialect.ParameterPrefix).Parameters).Name);
    }

    [Fact]
    public void The_executor_supplies_the_home_prefix_so_callers_never_type_it()
    {
        // 调用方只写属性名，前缀由执行器从本家方言取 —— 于是一句 SQL 换库时改的是列名方言，不是参数名写法。
        Assert.Equal("@total", Assert.Single(SqlServer.Orm().Ado.ToRawSql("SELECT 1 WHERE a = @total", new { total = 1 }).Parameters).Name);
        Assert.Equal(":total", Assert.Single(Xugu.Orm().Ado.ToRawSql("SELECT 1 WHERE a = :total", new { total = 1 }).Parameters).Name);
    }

    [Fact]
    public void A_positional_family_refuses_the_argument_object_instead_of_inventing_names()
    {
        // GBase 8s 的占位符是 ?，名字根本不进文本。按属性名编号会造出驱动收不到的名字，
        // 症状是"参数没绑上"那种看不出原因的错 —— 所以这一家当场拒，并把低层口指出去。
        var failure = Checks.Raised<QuickSqlException>(
            () => Gbase8s.Orm().Ado.ToRawSql("SELECT 1 WHERE a = ?", new { a = 1 }));

        Assert.Contains("位置式", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancellation_token_in_the_argument_slot_is_refused()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => RawSql.ToDialectSql(Sql, default(CancellationToken), '@'));

        Assert.Contains("ct", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancellation_token_hiding_inside_a_property_is_refused_too()
    {
        // 这颗比上一条更阴：参数对象照样折得出来，只是绑出去一颗两属性的盒子，
        // 而库那边收到的是一句缺参数的语句。
        var failure = Checks.Raised<QuickSqlException>(
            () => RawSql.ToDialectSql(Sql, new { id = 1, token = default(CancellationToken) }, '@'));

        Assert.Contains("CancellationToken", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_argument_object_without_a_single_public_property_is_refused()
    {
        var failure = Checks.Raised<QuickSqlException>(() => RawSql.ToDialectSql(Sql, new OnlyFields(), '@'));

        Assert.Contains("公共实例属性", failure.Message, StringComparison.Ordinal);
    }

    // ── 对位那一半：标量直读第 0 列，其余按名字装配 ──

    [Fact]
    public void A_scalar_shape_reads_the_first_column()
    {
        using var numbers = new RawReader(("AnyName", typeof(int), new[] { 7 }, null));

        Assert.Equal(7, First<int>(numbers));

        using var money = new RawReader(("AnyName", typeof(decimal), new[] { 12.5m }, null));

        Assert.Equal(12.5m, First<decimal>(money));

        using var text = new RawReader(("AnyName", typeof(string), new[] { "报表" }, null));

        Assert.Equal("报表", First<string>(text));
    }

    [Fact]
    public void A_scalar_shape_comes_through_the_same_converter_as_the_chained_exits()
    {
        // 枚举存 INT、Guid 在有的库里是 BINARY(16)、DateOnly 由 DateTime 折出来 ——
        // 这三件事在原生口与链式口必须是同一份代码，否则"某一列的读法"就有两处可改。
        using var states = new RawReader(("AnyName", typeof(int), new[] { 2 }, null));

        Assert.Equal(OrderState.Paid, First<OrderState>(states));

        using var dates = new RawReader(("AnyName", typeof(DateTime), new[] { new DateTime(2026, 3, 4) }, null));

        Assert.Equal(new DateOnly(2026, 3, 4), First<DateOnly>(dates));

        using var nullable = new RawReader(("AnyName", typeof(int), new[] { 0 }, new[] { true }));

        Assert.Null(First<int?>(nullable));
    }

    [Fact]
    public void An_object_row_is_assembled_by_name_not_by_position()
    {
        // 列序与属性序相反：按位置装配在这里会读出"Total 是 3、Orders 是 12.5"那种静默错值。
        using var reader = new RawReader(
            ("Total", typeof(decimal), new[] { 600m }, null),
            ("Orders", typeof(int), new[] { 3 }, null),
            ("CustomerId", typeof(int), new[] { 5 }, null));

        var row = First<Report>(reader);

        Assert.Equal(5, row.CustomerId);
        Assert.Equal(3, row.Orders);
        Assert.Equal(600m, row.Total);
        Assert.Null(row.Label);
    }

    [Fact]
    public void Names_match_case_insensitively_when_the_database_folds_them()
    {
        // 达梦一类把不带引号的标识符折成大写，而属性名只有一份写法。
        // 原样匹配排在忽略大小写之前：两句都对得上时先认原样的那一颗，不去动"哪一列"的归属。
        using var reader = new RawReader(
            ("CUSTOMERID", typeof(int), new[] { 5 }, null),
            ("ORDERS", typeof(int), new[] { 3 }, null));

        var row = First<Report>(reader);

        Assert.Equal(5, row.CustomerId);
        Assert.Equal(3, row.Orders);
    }

    [Fact]
    public void Unmatched_columns_are_ignored_and_unsupplied_properties_keep_their_default()
    {
        // 手写 SQL 常带辅助列与临时 join 列；少选一列则是那句 SQL 自己的形状。
        // 两格都不该抛，因为抛出去的症状是"报表跑不起来"，而这里没有任何东西是错的。
        using var reader = new RawReader(
            ("CustomerId", typeof(int), new[] { 5 }, null),
            ("RunId", typeof(int), new[] { 99 }, null));

        var row = First<Report>(reader);

        Assert.Equal(5, row.CustomerId);
        Assert.Equal(0, row.Orders);
        Assert.Equal(0m, row.Total);
    }

    [Fact]
    public void A_result_set_with_not_a_single_matching_column_is_refused()
    {
        // 一颗属性都没对上时，交回的是整行默认值的对象 —— 那读起来像「查到了三行空壳」而不是「列名对不上」。
        using var reader = new RawReader(
            ("Cust_Id", typeof(int), new[] { 5 }, null),
            ("Cnt", typeof(int), new[] { 3 }, null));

        var failure = Checks.Raised<QuickSqlException>(() => First<Report>(reader));

        Assert.Contains("Cust_Id", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CustomerId", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_is_cached_by_type_while_the_column_binding_belongs_to_one_execution()
    {
        // 缓存按型存的是属性清单与读写口，列序号必须留在每次执行自己的那张表里：
        // 同一个 Report 被两句列序不同的 SQL 用是常态用法，共享序号会让第二句读到第一句的对位。
        var first = RawRowReader.Create<Report>();
        var second = RawRowReader.Create<Report>();

        using (var reader = new RawReader(
                   ("Orders", typeof(int), new[] { 3 }, null),
                   ("CustomerId", typeof(int), new[] { 5 }, null)))
        {
            Assert.True(reader.Read());

            var row = first(reader);

            Assert.Equal(5, row.CustomerId);
            Assert.Equal(3, row.Orders);
        }

        using (var reader = new RawReader(
                   ("Total", typeof(decimal), new[] { 600m }, null),
                   ("Orders", typeof(int), new[] { 7 }, null),
                   ("CustomerId", typeof(int), new[] { 9 }, null)))
        {
            Assert.True(reader.Read());

            var row = second(reader);

            Assert.Equal(9, row.CustomerId);
            Assert.Equal(7, row.Orders);
            Assert.Equal(600m, row.Total);
        }
    }

    [Fact]
    public void The_binding_is_asked_once_and_the_following_rows_reuse_the_ordinals()
    {
        // 三行只问一遍列名：这是"名字对位按场次而不是按行"那一格的可数证据。
        // 两颗列 × 三行 ⇒ GetName 恰好两颗，多出来的每一次问都是把每行的开销写进报表出口。
        using var reader = new RawReader(
            ("CustomerId", typeof(int), new[] { 5, 6, 7 }, null),
            ("Orders", typeof(int), new[] { 3, 2, 1 }, null));

        var row = RawRowReader.Create<Report>();
        var seen = new List<int>();

        while (reader.Read())
        {
            var current = row(reader);

            seen.Add(current.CustomerId);
            seen.Add(current.Orders);
        }

        Assert.Equal(2, reader.NameAsks);
        Assert.Equal(new[] { 5, 3, 6, 2, 7, 1 }, seen);
    }

    [Fact]
    public void A_shape_without_a_parameterless_constructor_is_refused_and_told_where_to_go()
    {
        var record = Checks.Raised<QuickSqlException>(() => RawRowReader.Create<Positional>());

        Assert.Contains("无参构造", record.Message, StringComparison.Ordinal);

        var computed = Checks.Raised<QuickSqlException>(() => RawRowReader.Create<NoWrite>());

        Assert.Contains("公共可写属性", computed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_struct_that_is_not_a_column_shape_is_refused_rather_than_converted()
    {
        // 值类型不等于标量：Pair 有两个字段，把它当"直读第 0 列"会在 Convert.ChangeType 里
        // 抛出一句和列名、类型都无关的话。
        var failure = Checks.Raised<QuickSqlException>(() => RawRowReader.Create<Pair>());

        Assert.Contains("既不是可直读第 0 列的标量", failure.Message, StringComparison.Ordinal);
    }

    static T First<T>(RawReader reader)
    {
        var row = RawRowReader.Create<T>();

        Assert.True(reader.Read());

        return row(reader);
    }

    public sealed class OnlyFields
    {
        public int Id;
    }

    public sealed class Report
    {
        public int CustomerId { get; set; }

        public int Orders { get; set; }

        public decimal Total { get; set; }

        public string? Label { get; set; }
    }

    public sealed record Positional(int CustomerId, int Orders);

    public sealed class NoWrite
    {
        public int CustomerId => 1;
    }

    public struct Pair
    {
        public int Left { get; set; }

        public int Right { get; set; }
    }
}
