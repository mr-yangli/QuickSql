using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>联表（§5.2）。三件事必须由这一组测试钉住：别名由位置生成、形参名不参与解析、
/// 以及「投影之后就再也回不去列契约」这条护栏真的挡住了。</summary>
public sealed class SqlServerJoinTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    [Fact]
    public void Left_join_emits_a_second_table_position_and_a_bare_on()
    {
        var sql = orm().Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Select((o, c) => new { o.Id, c.Name })
            .ToSql();

        Golden.Verify("join-left-two", sql);

        Checks.Has("LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
    }

    [Fact]
    public void Inner_join_and_two_parameter_where_fold_into_one_predicate()
    {
        var sql = orm().Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => c.City == "上海" && o.Amount > 10m)
            .Select((o, c) => new { Order = o.Id, Customer = c.Name })
            .ToSql();

        Golden.Verify("join-inner-where", sql);

        Checks.Has("INNER JOIN [QuickSqlCustomer] AS [t1]", sql);
        Checks.Has("WHERE [t1].[City] = @p0 AND [t0].[Amount] > @p1", sql);

        // 两个槽位都是直接列，所以既不补 AS 也不带匿名投影的槽位名：物化看的是列序，不是列名（§10.3）。
        // 这条断言防的是"给直接列打别名"——它不会改变结果，但会让每条黄金样本都长一倍。
        Checks.Has("SELECT [t0].[Id], [t1].[Name] FROM", sql);
    }

    [Fact]
    public void Inner_join_of_two_tables_projects_one_column_from_each()
    {
        // 最薄的联表投影：没有 WHERE、没有排序，只验「INNER 关键字 + ON 落在哪 + 列序按投影槽位」。
        // 与 join-left-two 成对读，两条文本只差 LEFT/INNER 一个词。
        var sql = orm().Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Select((o, c) => new { o.Id, c.Name })
            .ToSql();

        Golden.Verify("join-inner-two", sql);

        Checks.Has("INNER JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
    }

    [Fact]
    public void Parameter_names_do_not_appear_anywhere_in_the_output()
    {
        // FreeSql 的一条硬教训：lambda 形参名参与找表，重命名重构就会改 SQL（§5.1）。
        var left = orm().Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Select((o, c) => new { o.Id, c.Name });

        var right = orm().Select<Order>()
            .InnerJoin<Customer>((x, y) => x.CustomerId == y.Id)
            .Select((x, y) => new { x.Id, y.Name });

        Assert.Equal(left.ToSql(), right.ToSql());
    }

    [Fact]
    public void A_self_join_takes_two_positions_of_the_same_table()
    {
        // §5.2 的"同名实体多次 join"。两张 [QuickSqlOrder] 靠位置区分（t0/t1），不靠形参名也不靠显式别名：
        // 别名只是给人看的，位置才是真相（§5.1）。
        var sql = orm().Select<Order>()
            .LeftJoin<Order>((o, p) => o.Id == p.Id)
            .Select((o, p) => new { o.Id, Other = p.Amount })
            .ToSql();

        Golden.Verify("join-self", sql);

        Checks.Has("FROM [QuickSqlOrder] AS [t0]", sql);
        Checks.Has("LEFT JOIN [QuickSqlOrder] AS [t1] ON [t0].[Id] = [t1].[Id]", sql);
        Assert.Equal(["[t0].[Id]", "[t1].[Amount]"], Checks.Columns(sql));
    }

    [Fact]
    public void Three_table_chain_numbers_tables_by_position()
    {
        var sql = orm().Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .LeftJoin<OrderItem>((o, c, i) => i.OrderId == o.Id)
            .Where((o, c, i) => c.Name != null)
            .Select((o, c, i) => new { o.Id, c.Name, i.Sku })
            .ToSql();

        Golden.Verify("join-three", sql);

        Checks.Has("INNER JOIN [QuickSqlCustomer] AS [t1]", sql);
        Checks.Has("LEFT JOIN [QuickSqlOrderItem] AS [t2]", sql);
        Assert.Equal(["[t0].[Id]", "[t1].[Name]", "[t2].[Sku]"], Checks.Columns(sql));
    }

    [Fact]
    public void The_same_entity_can_be_joined_twice_and_the_two_positions_stay_distinct()
    {
        // 表位按 FROM 位置编号，所以两张 [QuickSqlCustomer] 是 t1 与 t2，靠位置而不是靠"这是同一个实体"区分。
        // 与 join-self 成对：那边是根表自己再进一次，这边是右表进两次。
        var sql = orm().Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .InnerJoin<Customer>((o, c, r) => o.Id == r.Id)
            .Select((o, c, r) => new { Customer = c.Name, Referrer = r.City })
            .ToSql();

        Checks.Has("INNER JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
        Checks.Has("INNER JOIN [QuickSqlCustomer] AS [t2] ON [t0].[Id] = [t2].[Id]", sql);
        Assert.Equal(["[t1].[Name]", "[t2].[City]"], Checks.Columns(sql));
    }

    [Fact]
    public void Joins_do_not_repeat_the_root_table_in_from()
    {
        var sql = orm().Select<Order>()
            .InnerJoin<OrderItem>((o, i) => i.OrderId == o.Id)
            .Select((o, i) => new { o.Id, i.Sku })
            .ToSql();

        // 「join 不重复写主表」是沿用 FreeSql 的写法约定；主表进两次 FROM 会让结果集整片翻倍。
        Assert.Equal(1, Checks.Occurrences(sql, "[QuickSqlOrder] AS"));
        Assert.Equal(1, Checks.Occurrences(sql, "FROM "));
    }

    [Fact]
    public void Ordering_a_join_query_can_use_both_tables()
    {
        var sql = orm().Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .OrderBy((o, c) => c.Name)
            .ThenByDescending((o, c) => o.CreatedAt)
            .Select((o, c) => new { o.Id, c.Name })
            .ToSql();

        Golden.Verify("join-ordered", sql);

        Checks.Has("ORDER BY [t1].[Name] ASC, [t0].[CreatedAt] DESC", sql);
    }

    [Fact]
    public void Projecting_from_a_join_and_then_filtering_is_refused()
    {
        // §5.8：投影之后的列已经离开列契约，再筛就只能猜列，猜错是静默少行而不是报错。
        var projected = orm().Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Select((o, c) => new { o.Id, Name = c.Name });

        var failure = Checks.Raised<QuickSqlException>(() => projected.Where(x => x.Name != null).ToSql());

        Checks.Has("投影之后不能再加 WHERE", failure.Message);
    }

    [Fact]
    public void Joining_a_collection_navigation_is_refused()
    {
        // T2 : class 对接口同样成立，所以"把集合导航当表联"编译得过，只能靠运行时报（§5.7 的两次查询才是它的归宿）。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>().LeftJoin<IEnumerable<OrderItem>>((o, items) => true).ToSql());

        Checks.Has("集合导航", failure.Message);
    }

    [Fact]
    public void A_second_join_uses_the_tables_already_in_from()
    {
        var sql = orm().Select<OrderItem>()
            .InnerJoin<Order>((i, o) => i.OrderId == o.Id)
            .InnerJoin<Customer>((i, o, c) => o.CustomerId == c.Id)
            .Select((i, o, c) => new { i.Sku, o.Amount, c.City })
            .ToSql();

        Golden.Verify("join-from-detail", sql);

        // 起始表是明细表时，位置编号仍然从 t0 开始递增：谁先出现谁就是 t0，与实体名字无关。
        Assert.Equal(["[t0].[Sku]", "[t1].[Amount]", "[t2].[City]"], Checks.Columns(sql));
    }

    [Fact]
    public void A_condition_after_the_left_join_stays_in_where()
    {
        // 「左联之后再筛」不能退化成正则联表：把条件挪进 ON 会改变补不补 NULL 行的语义（§5.6）。
        var sql = orm().Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => c.City == "上海")
            .Select((o, c) => new { o.Id, c.City })
            .ToSql();

        Checks.Has("LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
        Checks.Has("WHERE [t1].[City] = @p0", sql);
    }

    [Fact]
    public void Paging_a_join_still_needs_its_order_by()
    {
        // §9.4：联表之后分页与单表分页是同一道出口检查，形状改写不该让它绕过去。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>()
                .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
                .Skip(10).Take(5)
                .ToSql());

        Checks.Has("没有 ORDER BY", failure.Message);
    }

    [Fact]
    public void Testing_a_whole_row_for_null_is_refused()
    {
        // §16.8：整行当值用两个语义都不猜。链式是即时翻译，所以抛点在 Where 这一步，不必走到 ToSql。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>()
                .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
                .Where((o, c) => c != null));

        Checks.Has("整行不能当值用", failure.Message);
        Checks.Has("无法翻译表达式：c", failure.Message);
        // v0.36 第一笔（§16.8 毛病 1+3）：句号直拼的模板新形，与毛病 3 的两条实测可写形态。
        Checks.Has("无法翻译表达式：c。整行不能当值用", failure.Message);
        Checks.Has("成员位置只能放列", failure.Message);
        Checks.Absent("可改写为", failure.Message);
    }

    [Fact]
    public void An_unmapped_method_call_points_at_registration_instead_of_a_phantom_port()
    {
        // 毛病 4 的第三处（ExpressionTranslator 的未映射方法调用那支）：此前全仓库零触发用例，
        // 这条消息的文案一直没被量过一次。今天它指向注册口与真实的 ToList()/ToListAsync()，
        // 两条幻影口（ToEnumerable / AllowClientEvaluation）都不许再出现。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>()
                .Where(o => o.Id.ToString() == "1")
                .ToSql());

        Checks.Has("注册映射", failure.Message);
        Checks.Has("FunctionMapping", failure.Message);
        Checks.Has("落到内存再调这个方法", failure.Message);
        Checks.Absent("ToEnumerable", failure.Message);
        Checks.Absent("可改写为", failure.Message);
    }

    [Fact]
    public void Projecting_a_whole_row_is_refused_as_well()
    {
        // 与上一条同形不同口：整行当投影也在组合那一刻被拒（投影槽位要的是一个 SqlExpression，
        // 而"整行"在列契约里没有对应物）。链式侧两个形状都落在翻译层，因为没有 LINQ 的透明标识符
        // 会把 new { o, c } 原样带回模型、推迟到物化口才发现。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>()
                .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
                .Select((o, c) => new { o, c }));

        Checks.Has("整行不能当值用", failure.Message);

        // 报错里出现的是用户自己写的形参名，且是投影实参列表里第一个撞上的那个（§5.1：链式不做位置化改写）。
        Checks.Has("无法翻译表达式：o", failure.Message);
        // v0.36 第一笔：话术给出的两条改路都是实测过的可写形态（这一条用例的合法对照面就在下面两条里）。
        Checks.Has("只选一张表", failure.Message);
        Checks.Absent("交给 Select(o => new", failure.Message);
    }

    [Fact]
    public void Projecting_one_tables_whole_row_out_of_a_join_is_the_allowed_side_of_that_line()
    {
        // §16.8 那条线的另一侧：被拒的是"整行当值用"，"投影 = 某一张表的整行"成立 —— 多表只用来筛，
        // 结果集仍是 Order。选中的形参本身就是唯一答案，所以没有 §5.2 ③ 说的"多表态没有默认投影形状"问题。
        var sql = orm().Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => c.City != null)
            .Select((o, c) => o)
            .ToSql();

        Golden.Verify("join-project-left-entity", sql);

        Assert.Equal(["[t0].[Id]", "[t0].[CustomerName]", "[t0].[CustomerId]", "[t0].[Amount]", "[t0].[State]", "[t0].[CreatedAt]", "[t0].[PaidAt]", "[t0].[Note]"], Checks.Columns(sql));

        // 右表留在 FROM 与 WHERE 里，但一列都不投影 —— "join 只用于筛"是这条形状的用途所在。
        Checks.Has("LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
        Checks.Absent("[t1].", sql[..sql.IndexOf(" FROM ", StringComparison.Ordinal)]);
    }

    [Fact]
    public void The_chain_has_no_way_to_desc_a_first_join_key_not_even_through_the_projection()
    {
        // §16.14 的事实依据另一半：绕道"先投影、再对投影列降序"也被拒 ——
        // 所以联表 arity 上 `ORDER BY <列> DESC` 打头的序在链式侧根本写不出来，不是"绕一下就行"。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>()
                .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
                .Select((o, c) => new { o.Id, Name = c.Name })
                .OrderByDescending(x => x.Name)
                .ToSql());

        Checks.Has("投影之后不能再排序", failure.Message);
    }

    /// <summary>§13.2 ① 跨 arity 等价 —— v0.6 把等价性测试的对象从"链式 vs LINQ"换成这个之后，它是唯一的见证。
    /// 判据落在规范模型文本而不是 SQL：同一串算子在一、二、三个表位上归约出的模型，剔掉新增表位自带的那几行之后
    /// 必须逐字节相等。谓词与排序**刻意只碰 `t0`** —— 一旦碰了 `t1`，剔除规则会连 `WHERE` 一起剔掉，断言就成了回声。</summary>
    [Fact]
    public void The_same_operators_at_two_and_three_positions_differ_only_by_the_added_table_positions()
    {
        var one = orm().Select<Order>()
            .Where(o => o.State == OrderState.Paid)
            .Skip(5).Take(3)
            .OrderBy(o => o.CreatedAt)
            .Explain().ModelText;

        var two = orm().Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => o.State == OrderState.Paid)
            .Skip(5).Take(3)
            .OrderBy((o, c) => o.CreatedAt)
            .Explain().ModelText;

        var three = orm().Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .InnerJoin<OrderItem>((o, c, i) => i.OrderId == o.Id)
            .Where((o, c, i) => o.State == OrderState.Paid)
            .Skip(5).Take(3)
            .OrderBy((o, c, i) => o.CreatedAt)
            .Explain().ModelText;

        // 先确认"确实多长了行"，否则上面的剔除若把整串剔空也会通过。
        Assert.True(two.Length > one.Length, $"两表模型比单表的还短，剔除规则有问题：{Environment.NewLine}{two}");
        Assert.True(three.Length > two.Length, $"三表模型比两表的还短：{Environment.NewLine}{three}");

        Assert.Equal(Trim(one), Trim(WithoutPositions(two, "t1")));
        Assert.Equal(Trim(one), Trim(WithoutPositions(three, "t1", "t2")));
    }

    /// <summary>剔掉提到给定表位的整行：新增一个表位会带来一行 `TABLE tN = …` 与一到两行 `JOIN … ON …`。</summary>
    static string WithoutPositions(string model, params string[] positions)
        => string.Join("\n", model.Split('\n').Where(line => !positions.Any(p => line.Contains(p, StringComparison.Ordinal))));

    static string Trim(string model) => model.Trim('\n', '\r', ' ');
}
