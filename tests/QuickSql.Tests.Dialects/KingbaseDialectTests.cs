using System.Data;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>金仓（PG 系血统）的 SQL <b>文本</b>。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与 T-SQL 那一族的离线套件同一分工：黄金样本钉版式，结构断言钉"凭什么长这样"。
///
/// <para><b>这些样本证明的是回归，不是"金仓接受这条 SQL"</b>。本机没有任何一台金仓/PG（授权的活库只有
/// 两台 SQL Server），所以这一批 <c>.sql</c> 是自产基线：它们会抓住方言层的每一次意外改动，
/// 但一句都没有在真正的解析器上跑过。那句话的正式记账在 §13.5 的能力出处台账，那里逐位标着「未实测」——
/// 写在这一段的目的是：读到这一行的红或绿时，不要把它当成对真库行为的断言。</para>
///
/// <para>样本按<b>形状</b>选而不是按链式方法数（§13.3）：只收"与 T-SQL 那一族不同形"的那几条，
/// 同形的交给 <see cref="DialectParityTests"/> 逐字对读，不再复制一份基线。</para></summary>
public sealed class KingbaseDialectTests
{
    static readonly IDbProvider Provider = Kingbase.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名。<b>这一层不能省</b>：两族按形状起的名字是同一批（<c>select-entity</c>、
    /// <c>functions</c>、<c>ddl-order</c>…），第一次生成时没带它就等于把 <c>golden/sqlserver/</c> 里八份
    /// T-SQL 基线改写成了金仓文本 —— 复跑整套离线套件才发现，已按 git 还原。所以这个类里只有
    /// <c>Verify</c> 这一个写样本的入口。</summary>
    const string Family = "kingbase";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Family);

    static QuickSqlOrm orm() => Kingbase.Orm();

    /// <summary>只有一用例需要改选项：<c>Skip/Take</c> 无 <c>OrderBy</c> 在共用层默认是抛（§9.4），
    /// 而这一家的 <c>OFFSET</c> 本来就合法，所以要看它长什么样必须把策略放开。</summary>
    static QuickSqlOrm orm(PagingOrderingPolicy policy)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider,
            ConnectionString = SqlServer.OfflineConnectionString,
            PagingOrdering = policy,
        });

    static Order row(int n) => new()
    {
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, 890),
    };

    // ── 标识符与 SELECT 骨架 ──────────────────────────────────────────

    [Fact]
    public void Every_identifier_is_double_quoted_because_the_family_folds_unquoted_names_to_lower_case()
    {
        var sql = orm().Select<Order>().ToSql();

        Verify("select-entity", sql);

        // 不带引号的 QuickSqlOrder 会被折成 quicksqlorder，与建表时那句 CREATE TABLE "QuickSqlOrder" 不是同一张表。
        // 所以这一家的策略是全部加引号（大小写敏感），而不是"只在需要时加"——后者在混合大小写的现场必红。
        Checks.Has("FROM \"QuickSqlOrder\" AS \"t0\"", sql);
        Checks.Has("SELECT \"t0\".\"Id\", \"t0\".\"CustomerName\"", sql);
        Checks.Absent("[", sql);
    }

    // ── 分页：§9.4 那三套实现里的第一套 ──────────────────────────────

    [Fact]
    public void Limit_and_offset_are_two_independent_clauses()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.CreatedAt).Skip(10).Take(5).ToSql(out var parameters);

        Verify("paging-order-skip-take", sql);

        Assert.Equal(["@p0", "@p1"], parameters.Select(p => p.Name).ToArray());

        // 两段的先后是这一家的写法（LIMIT 在 OFFSET 之前），而参数序号跟着发射序走 ——
        // 所以 @p0 是 LIMIT 那一颗。这一点值不核对的话，把两个子句写反仍是一句能过 golden 的 SQL。
        Assert.Equal([5, 10], parameters.Select(p => (int) p.Value!).ToArray());
        Checks.Has("ORDER BY \"t0\".\"CreatedAt\" ASC LIMIT @p0 OFFSET @p1", sql);
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWS ONLY", sql);
    }

    [Fact]
    public void Take_without_skip_emits_no_offset_clause_at_all()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Take(5).ToSql(out var parameters);

        Verify("paging-take-only", sql);

        // T-SQL 那一族在这里必须写 OFFSET 0（FETCH 不接受单独的），这一家省略子句就是省略，
        // 不多一颗常量参数。两侧的参数颗数因此本来就不同——那是方言的事实，不是某一边的多余物。
        Assert.Single(parameters);
        Checks.Has("ORDER BY \"t0\".\"Id\" ASC LIMIT @p0", sql);
        Checks.Absent("OFFSET", sql);
    }

    [Fact]
    public void Paging_needs_no_ordering_so_no_placeholder_order_by_is_invented()
    {
        var sql = orm(PagingOrderingPolicy.Allow).Select<Order>().Skip(10).Take(5).ToSql();

        Verify("paging-without-ordering", sql);

        // 「LIMIT 不要求 ORDER BY」在这一家是事实，所以共用层那颗占位序永远不触发。
        // 反过来说：结果不稳定在这一家并没有变成"更安全"，只是不再需要被伪造一句 ORDER BY (SELECT NULL)。
        Checks.Absent("ORDER BY", sql);
        Checks.Has("LIMIT @p0 OFFSET @p1", sql);
    }

    // ── 谓词形状 ─────────────────────────────────────────────────────

    [Fact]
    public void An_empty_set_is_a_bare_false_because_where_wants_a_boolean()
    {
        var none = Array.Empty<int>();

        var sql = orm().Select<Order>().Where(o => none.Contains(o.Id)).ToSql();

        Verify("where-false-empty-set", sql);

        // 1 = 0 在这一家也执行得动（整数比较即布尔），所以写错了不会报错——正是"两家都过、只有一家对"
        // 的那类形状，只能由方言自己给（§10.5）。
        Checks.Has("WHERE false", sql);
        Checks.Absent("1 = 0", sql);
    }

    [Fact]
    public void A_bool_operand_uses_IS_TRUE_rather_than_comparing_to_a_bit()
    {
        var paid = true;

        var sql = orm().Select<Order>().Where(o => paid && o.Amount > 1m).ToSql(out var parameters);

        Verify("where-boolean-flag", sql);

        // 语义与 T-SQL 的 = 1 相同，但写法不能沿用：这一家 @p0 是 boolean，而 = 1 那一侧是 integer，
        // 直接类型错误。可空布尔上 IS TRUE 与 IS FALSE 都匹配不到 NULL，所以换的是写法不是语义。
        Assert.Equal(2, parameters.Count);
        Checks.Has("@p0 IS TRUE AND \"t0\".\"Amount\" > @p1", sql);
        Checks.Absent("@p0 = 1", sql);
    }

    // ── 主键回填：RETURNING 落在句尾 ─────────────────────────────────

    [Fact]
    public void The_key_clause_is_a_trailing_returning_instead_of_a_pre_VALUES_output()
    {
        var sql = orm().Insert(row(1)).ExplainKeys().ToSql();

        Verify("insert-with-keys", sql);

        Checks.Has("VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6) RETURNING \"Id\"", sql);
        Assert.True(sql.IndexOf("VALUES", StringComparison.Ordinal) < sql.IndexOf("RETURNING", StringComparison.Ordinal));
        Checks.Absent("OUTPUT", sql);
        Checks.Absent("inserted.", sql);
    }

    [Fact]
    public void An_update_with_key_retrieval_gets_exactly_one_space_before_returning()
    {
        // 这一条是 v0.22 的回归样本，而且是"接第二个方言族才现形"的那种：T-SQL 把 UPDATE 整个覆写了，
        // 所以共用层 UPDATE 尾巴上少一颗空格永远量不到它，产出会是 `…WHERE @p0 = 1RETURNING "Id"`——
        // 两家的解析器都给语法错，但报错的位置离真正的改动点隔着一个文件。
        var contract = orm().Schemas.Contract<Order>();
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));
        var id = contract.Columns.Single(c => c.MemberName == nameof(Order.Id));

        var sql = EmitUpdate(new UpdateStatement
        {
            Table = new TableRef(0, "t0", contract.Table, contract),
            Sets = [new SetClause(amount, new ParameterValue(1m, amount))],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(new TableRef(0, "t0", contract.Table, contract), id), new ParameterValue(3, id)),
            Returning = [id],
        }).Text;

        Verify("update-with-returning", sql);

        Checks.Has("\"t0\".\"Id\" = @p1 RETURNING \"Id\"", sql);
    }

    // ── §12.4 第 2 项的变更守卫：这一家有 IS DISTINCT FROM ────────────

    [Fact]
    public void A_not_null_column_still_needs_only_the_inequality()
    {
        var merged = Merge(nameof(Order.Amount));

        Verify("update-merged-not-null", merged.Text);

        Checks.Has("WHERE \"t0\".\"Id\" = \"t1\".\"Id\" AND \"t0\".\"Amount\" <> \"t1\".\"Amount\"", merged.Text);
        Checks.Absent("IS DISTINCT FROM", merged.Text);
    }

    [Fact]
    public void A_nullable_column_needs_one_clause_because_the_family_has_distinct_from()
    {
        var merged = Merge(nameof(Order.PaidAt));

        Verify("update-merged-nullable", merged.Text);

        // T-SQL 那一份要三段（不等 + 只表侧空 + 只行集侧空），这里一段就够，而且"两侧同空算没变"
        // 是这一家的原生语义。守卫变短不是风格：三段 OR 在库里走不动索引，一段 IS DISTINCT FROM 走得动。
        Checks.Has("WHERE \"t0\".\"Id\" = \"t1\".\"Id\" AND \"t0\".\"PaidAt\" IS DISTINCT FROM \"t1\".\"PaidAt\"", merged.Text);
        Checks.Absent(" OR ", merged.Text);
        Checks.Absent("IS NULL", merged.Text);
    }

    [Fact]
    public void The_two_guards_of_a_merged_update_are_parenthesised_as_one_OR_group()
    {
        // 两列守卫：一条 IS DISTINCT FROM 与一条 <> 由 OR 连接，整组挂在一个 AND 下面。
        // 括号是这一条的钉子：<c>AND a <> b IS DISTINCT FROM c</c> 这种没括号的写法，
        // 在这一家会被读成 <c>(a <> b) IS DISTINCT FROM c</c>（见下面那条 precedence 用例），语义整个换掉。
        var merged = Merge(nameof(Order.PaidAt), nameof(Order.Amount));

        Checks.Has("\"t0\".\"PaidAt\" IS DISTINCT FROM \"t1\".\"PaidAt\" OR \"t0\".\"Amount\" <> \"t1\".\"Amount\"", merged.Text);
        Checks.Has("= \"t1\".\"Id\" AND (\"t0\".\"PaidAt\"", merged.Text);
        Assert.Equal(1, Checks.Occurrences(merged.Text, " OR "));
    }

    [Fact]
    public void IS_distinct_from_binds_looser_than_a_comparison_so_the_nearer_side_gets_parenthesised()
    {
        // 这一条钉的是 DialectBase.Precedence 表里那一格的<em>方向</em>，不是今天任何一条链的产物：
        // PG 手册 4.1.6 的 Table 4.2 把 <c>&lt; &gt; = &lt;= &gt;= &lt;&gt;</c> 排在 <c>IS ISNULL NOTNULL</c> 之上，
        // 文法里 <c>distinct_expr</c> 的两个操作数只吃到 <c>a_expr_compare</c> 那一层，
        // 于是 <c>x = a IS DISTINCT FROM b</c> 读作 <c>x = ...</c> 还是 <c>(x = a) IS ...</c> 由比较符先绑。
        // v0.22 最初把这格写反了（当成 IS 更紧），反方向的产物就是这条断言现在拒绝的那种文本。
        // 今天没有一条链式入口产得出这棵树 —— 它是那张表的钉子，不是某个功能的回归样本。
        var order = orm().Schemas.Contract<Order>();
        var stored = new TableRef(0, "t0", order.Table, order);
        var paidAt = order.Columns.Single(c => c.MemberName == nameof(Order.PaidAt));
        var createdAt = order.Columns.Single(c => c.MemberName == nameof(Order.CreatedAt));
        var flag = order.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        var ast = new DeleteStatement
        {
            Table = stored,
            Where = new SqlBinaryExpression(
                BinaryOperator.Equal,
                new ColumnExpression(stored, flag),
                new SqlBinaryExpression(
                    BinaryOperator.IsDistinctFrom, new ColumnExpression(stored, paidAt), new ColumnExpression(stored, createdAt))),
        };

        var sql = EmitDelete(ast).Text;

        // 内层更松 ⇒ 必须加括号，否则发出去的是另一棵树。
        Checks.Has("WHERE \"t0\".\"CustomerName\" = (\"t0\".\"PaidAt\" IS DISTINCT FROM \"t0\".\"CreatedAt\")", sql);

        // 反方向那一档不需要括号：比较符绑得紧，右侧直接写就是同一棵树。
        var tighter = ast with
        {
            Where = new SqlBinaryExpression(
                BinaryOperator.IsDistinctFrom,
                new ColumnExpression(stored, paidAt),
                new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, createdAt), new ColumnExpression(stored, paidAt))),
        };

        Checks.Has("WHERE \"t0\".\"PaidAt\" IS DISTINCT FROM \"t0\".\"CreatedAt\" = \"t0\".\"PaidAt\"", EmitDelete(tighter).Text);
    }

    // ── 写侧的 FROM / USING：连接条件要挪进 WHERE ────────────────────

    [Fact]
    public void A_merged_update_reads_the_session_rows_through_a_comma_separated_from()
    {
        var merged = Merge(nameof(Order.Amount), nameof(Order.PaidAt));

        Verify("update-merged-two-columns", merged.Text);

        // 键的等值条件原本在 ON 上，这一家的 UPDATE … FROM 没有 ON 可放，所以它落在 WHERE 的最前面。
        // INNER 才挪得动：外连接的条件挪进 WHERE 会把未匹配行整个筛掉，那是改语义而不是改版式。
        Checks.Has("FROM (VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)) AS \"t1\" (\"Id\", \"Amount\", \"PaidAt\")", merged.Text);
        Checks.Has("WHERE \"t0\".\"Id\" = \"t1\".\"Id\" AND ", merged.Text);
        Checks.Absent("INNER JOIN", merged.Text);
        Checks.Absent(" ON ", merged.Text);
    }

    [Fact]
    public void A_delete_with_joined_sources_uses_USING_and_folds_every_condition_into_where()
    {
        var order = orm().Schemas.Contract<Order>();
        var customer = orm().Schemas.Contract<Customer>();
        var item = orm().Schemas.Contract<OrderItem>();

        var stored = new TableRef(0, "t0", order.Table, order);
        var parents = new TableRef(1, "t1", customer.Table);
        var details = new TableRef(2, "t2", item.Table);

        var id = order.Columns.Single(c => c.MemberName == nameof(Order.Id));
        var customerId = order.Columns.Single(c => c.MemberName == nameof(Order.CustomerId));
        var state = order.Columns.Single(c => c.MemberName == nameof(Order.State));
        var parentId = customer.Columns.Single(c => c.MemberName == nameof(Customer.Id));
        var parentOrderId = item.Columns.Single(c => c.MemberName == nameof(OrderItem.OrderId));

        var ast = new DeleteStatement
        {
            Table = stored,
            Joins =
            [
                Join(parents, [parentId], new SqlBinaryExpression(
                    BinaryOperator.Equal, new ColumnExpression(stored, customerId), new ColumnExpression(parents, parentId))),
                Join(details, [parentOrderId], new SqlBinaryExpression(
                    BinaryOperator.Equal, new ColumnExpression(stored, id), new ColumnExpression(details, parentOrderId))),
            ],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, state), new ParameterValue(2, state)),
        };

        var sql = EmitDelete(ast).Text;

        Verify("delete-using", sql);

        // 两个表源：第一份带 USING，往后一律逗号并列（这一家的 USING 只吃表源清单）。
        // T-SQL 的那一份是 INNER JOIN … ON 连着写，两家在这里逐字不同形，所以这一条必须是金仓的样本。
        Checks.Has("DELETE FROM \"QuickSqlOrder\" AS \"t0\" USING", sql);
        Checks.Has(") AS \"t1\" (\"Id\"), (VALUES (@p1)) AS \"t2\" (\"OrderId\")", sql);
        Checks.Has("WHERE \"t0\".\"CustomerId\" = \"t1\".\"Id\" AND \"t0\".\"Id\" = \"t2\".\"OrderId\" AND \"t0\".\"State\" = @p2", sql);
        Checks.Absent("INNER JOIN", sql);
    }

    [Fact]
    public void An_outer_join_on_the_write_side_is_refused_rather_than_silently_folded()
    {
        // 把 LEFT 的条件折进 WHERE 会得到一条执行得动、行数不对的语句：那正是 §12.2 拒绝的"静默降级"。
        // 抛而不是"照 INNER 折"：这一格没有第二条可用的路（LEFT 在这一家的 UPDATE … FROM 里没有形状），
        // 调用方要看到的是"为什么不行"，不是"不行"。
        var failure = Checks.Raised<QuickSqlException>(() => Merge([nameof(Order.Amount)], kind: JoinKind.Left));

        Checks.Has("不接受 LEFT", failure.Message);
        Checks.Has("语义就变了", failure.Message);

        var missing = Checks.Raised<QuickSqlException>(() => Merge([nameof(Order.Amount)], onCondition: false));

        Checks.Has("缺少 ON 条件", missing.Message);
    }

    // ── 函数模板：同一条链，两家的模板表 ─────────────────────────────

    [Fact]
    public void The_same_chain_renders_this_family_s_function_templates()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026 && Math.Round(o.Amount, 2) > 1m)
            .ToSql();

        Verify("functions", sql);

        // LENGTH 而不是 LEN：两者对尾部空格的算法不同（LEN 不计、LENGTH 计），这一格差的是语义不只是名字。
        Checks.Has("LENGTH(\"t0\".\"CustomerName\") > @p0", sql);
        Checks.Has("EXTRACT(year FROM \"t0\".\"CreatedAt\") = @p1", sql);

        // ROUND 单参数那一档在 PG 14 之前只吃 numeric，金仓/openGauss 的内核更早，所以按最老的内核写。
        Checks.Has("ROUND(CAST(\"t0\".\"Amount\" AS numeric), @p2)", sql);
    }

    [Fact]
    public void String_reads_take_the_native_from_for_forms_and_the_one_based_offset()
    {
        var sql = orm().Select<Order>()
            .Select(o => new
            {
                Cut = o.CustomerName!.Substring(1, 2),
                Tail = o.CustomerName!.Substring(1),
                At = o.CustomerName!.IndexOf("x"),
                Joined = o.CustomerName + " " + o.Note,
                Blank = string.IsNullOrWhiteSpace(o.Note),
            })
            .ToSql();

        Verify("string-functions", sql);

        // 0 基与 1 基的换算（+ 1）写在模板里，所以两侧同一份 C# 形状；而单参数 Substring 在这一家有原生写法，
        // T-SQL 要伪造一个"够长的长度"——这就是"模板按家登记、共用层只发符号"的全部理由。
        Checks.Has("SUBSTRING(\"t0\".\"CustomerName\" FROM @p0 + 1 FOR @p1)", sql);
        Checks.Has("SUBSTRING(\"t0\".\"CustomerName\" FROM @p2 + 1)", sql);
        Checks.Has("(STRPOS(\"t0\".\"CustomerName\", @p3) - 1)", sql);
        // 三元的 + 折成两层 CONCAT（与 T-SQL 同一套折叠规则，§5.8）：模板按元数登记，直落三参会被静默丢掉。
        Checks.Has("CONCAT(CONCAT(\"t0\".\"CustomerName\", @p4), \"t0\".\"Note\")", sql);
        Checks.Has("(\"t0\".\"Note\" IS NULL OR BTRIM(\"t0\".\"Note\") = '')", sql);
    }

    [Fact]
    public void Date_arithmetic_is_interval_multiplication_and_greatest_is_native()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Tomorrow = o.CreatedAt.AddDays(1), Ceiling = Math.Max(o.Amount, 1m) })
            .ToSql();

        Verify("date-and-number", sql);

        // 这一家没有 DATEADD：加 n 个单位是"乘出来的 interval"，所以乘数带括号——{1} 若是表达式，
        // 少了括号就成了 a - b * interval，优先级把减法整个算进乘数里。
        Checks.Has("(\"t0\".\"CreatedAt\" + (@p0) * INTERVAL '1 day')", sql);

        // T-SQL 那一档 GREATEST 要用 CASE 展开，这里是原生函数：两家的这条样本不同形，对拍读不出"一样"。
        Checks.Has("GREATEST(\"t0\".\"Amount\", @p1)", sql);
    }

    // ── CodeFirst：这一家的类型名与自增写法 ─────────────────────────

    [Fact]
    public void Create_table_emits_this_family_s_type_names_and_identity_clause()
    {
        var contract = orm().Schemas.Contract<Order>();
        var writer = NewWriter();

        Provider.CodeFirst.EmitCreateTable(writer, contract);

        var sql = writer.ToSqlString();

        Verify("ddl-order", sql);

        Checks.Has("CREATE TABLE \"public\".\"QuickSqlOrder\"", sql);
        Checks.Has("\"Id\" INTEGER GENERATED BY DEFAULT AS IDENTITY NOT NULL", sql);
        Checks.Has("\"CustomerName\" VARCHAR(64) NULL", sql);
        Checks.Has("\"Amount\" NUMERIC(18,4) NOT NULL", sql);
        Checks.Has("\"CreatedAt\" TIMESTAMP(3) NOT NULL", sql);
        Checks.Has("\"State\" INTEGER NOT NULL", sql);
        Checks.Has("\"Note\" TEXT NULL", sql);

        // 遗留的 IDENTITY(1,1)/NVARCHAR/DATETIME2 一律不出现：这一家没有这些名字，
        // 写出来是整批建表失败而不是一个可以照着改的报错。
        Checks.Absent("IDENTITY(1,1)", sql);
        Checks.Absent("NVARCHAR", sql);
        Checks.Absent("DATETIME2", sql);
    }

    [Fact]
    public void Column_changes_and_comments_are_this_family_s_shapes()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        // 三条各自一句、各自一个书写器：塞进同一个书写器会得到 NOT NULLALTER TABLE 这种连体文本，
        // 而这一家本来就要它们分开（下面那两处 " ; " 是 provider 自己写的，不是这里拼的）。
        var sql = string.Join(" ; ",
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, note)),
            Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, amount)),
            Statement(w => Assert.True(Provider.CodeFirst.TryEmitColumnComment(w, contract.Table, name))));

        Verify("ddl-alter-and-comment", sql);

        // 精度那一档必须是两句：ALTER COLUMN … TYPE 不碰可空性（T-SQL 的 ALTER COLUMN 会顺带写回），
        // 漏掉第二句就是"改了精度"顺带改了约束——而约束是 NOT NULL 时它本该在历史 NULL 行上失败。
        Checks.Has("ALTER TABLE \"public\".\"QuickSqlOrder\" ADD COLUMN \"Note\" TEXT NULL", sql);
        Checks.Has("ALTER COLUMN \"Amount\" TYPE NUMERIC(18,4) ; ALTER TABLE \"public\".\"QuickSqlOrder\" ALTER COLUMN \"Amount\" SET NOT NULL", sql);

        // 列注释是原生语法，不需要 sp_addextendedproperty 那种存储过程调用；文本不带 N 前缀。
        Checks.Has("COMMENT ON COLUMN \"public\".\"QuickSqlOrder\".\"CustomerName\" IS ''", sql);
        Checks.Absent("sp_addextendedproperty", sql);
        Checks.Absent("N'", sql);
    }

    // ── 内联字面量（ToLogSql 那一档）─────────────────────────────────

    [Fact]
    public void Inlined_text_carries_no_N_prefix()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        var insert = new InsertStatement
        {
            Table = new TableRef(0, "t0", contract.Table, contract),
            Columns = [name, note],
            Rows = [[new ParameterValue("客户甲", name), new ParameterValue("备注", note)]],
        };

        var (text, parameters) = EmitInsert(in insert);

        var log = new DialectSql(text, parameters).ToLogSql(Provider);

        // 这一家的字符串默认就是 UTF-8（库字符集），没有"窄字面量装不下中文"这件事，
        // 所以 N 前缀不能沿用：它在这里是语法错，不是无害的冗余（§10.4 的跨库那一半）。
        Checks.Has("VALUES ('客户甲', '备注')", log);
        Checks.Absent("N'", log);
    }

    [Fact]
    public void Binary_refuses_to_inline_at_all_because_its_text_form_depends_on_a_session_setting()
    {
        var contract = orm().Schemas.Contract<InventoryItem>();
        var sku = contract.Columns.Single(c => c.MemberName == nameof(InventoryItem.Sku));
        var version = contract.Columns.Single(c => c.MemberName == nameof(InventoryItem.RowVersion));

        var insert = new InsertStatement
        {
            Table = new TableRef(0, "t0", contract.Table, contract),
            Columns = [sku, version],
            Rows = [[new ParameterValue("货1", sku), new ParameterValue(new byte[] { 1, 15 }, version)]],
        };

        var (text, parameters) = EmitInsert(in insert);

        var log = new DialectSql(text, parameters).ToLogSql(Provider);

        // bytea 的文本形态取决于 bytea_output（hex / escape 两档），而这份文本没有那台库的会话设置，
        // 所以宁可不内联、把 @p 留着：给一份"看着能执行、换台库就错"的文本比不给更坏。
        Assert.Equal(2, parameters.Count);
        Checks.Has("@p1", log);
        Checks.Has("'货1'", log);
    }

    /// <summary>浮点字面量的三档，与 T-SQL 侧那条同名形状的用例各钉一家
    /// （<c>SqlServerValueTests.A_floating_point_literal_keeps_the_exponent_form_and_refuses_the_non_finite_ones</c>）。
    /// 两家的这一格原本<em>同一份错</em>：指数式也补 <c>.0</c>，于是 <c>1E-08</c> 变成 <c>1E-08.0</c> ——
    /// 那是句语法错误，而它在离线侧唯一的显形方式就是把产物本身钉住（§13.5：这一族没有活库）。</summary>
    [Fact]
    public void The_floating_point_literal_keeps_the_exponent_form_and_refuses_the_non_finite_ones()
    {
        // 纯整数形式要补：`3` 在这一家会被当成 integer 常量，real/double 列上要绕一次隐式转换。
        Assert.True(Provider.Values.TryInlineLiteral(3d, null, out var whole));
        Assert.Equal("3.0", whole);

        Assert.True(Provider.Values.TryInlineLiteral(2.5f, null, out var fraction));
        Assert.Equal("2.5", fraction);

        Assert.True(Provider.Values.TryInlineLiteral(1e-08, null, out var tiny));
        Assert.Equal("1E-08", tiny);

        // 这一家的非有限值是 'NaN'::double precision 那种带引号再 cast 的写法，裸 NaN 会被解析成列名；
        // 而 -∞ 打头那颗 '-' 是合法的浮点首字符 —— 只判首字符的写法会补出 '-∞.0'。
        Assert.False(Provider.Values.TryInlineLiteral(double.NaN, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.PositiveInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.NegativeInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(float.NegativeInfinity, null, out _));

        var log = new DialectSql(
            "WHERE \"t0\".\"Rate\" = @p0 AND \"t0\".\"Scale\" = @p1",
            [new AdoParameter("@p0", 1e-08, DbType.Double, 0), new AdoParameter("@p1", double.NaN, DbType.Double, 0)]).ToLogSql(Provider);

        Checks.Has("\"t0\".\"Rate\" = 1E-08", log);
        Checks.Has("\"t0\".\"Scale\" = @p1", log);
    }

    // ── 全局过滤器（§10.7）：注入没有方言分支，被贴进去的那一句有 ──────

    [Fact]
    public void A_global_filter_negates_the_bool_column_with_IS_TRUE_because_that_is_what_a_predicate_is_here()
    {
        var fresh = orm();

        fresh.Filters.Add<Ticket>(t => !t.Deleted);

        var sql = fresh.Select<Ticket>().ToSql();

        Verify("filter-soft-delete", sql);

        // 注册、匹配、贴进 WHERE/ON 那几步全在共用层（<c>FilterRegistry</c>），三族的产出除字形外同形 ——
        // 那一半由 <see cref="DialectParityTests"/> 逐字对读。这一家唯一不同的是<em>被贴进去的那一句</em>：
        // bool 列在这一家本来就是 boolean，所以守卫是 IS TRUE 而不是 = 1（见
        // <c>A_bool_operand_uses_IS_TRUE_rather_than_comparing_to_a_bit</c>），取反之后是 NOT … IS TRUE。
        // <c>NOT</c> 在这一家的优先级表里排在 <c>IS</c> 之下，所以不加括号也是"整个条件取反"，
        // 不会读成 <c>(NOT "Deleted") IS TRUE</c> —— 那句话只能靠产物本身钉住，离线侧没有第二种判法。
        Checks.Has("WHERE NOT \"t0\".\"Deleted\" IS TRUE", sql);
        Checks.Absent("= 1", sql);
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    static SqlWriter NewWriter() => new(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }

    static DialectSql EmitUpdate(UpdateStatement ast)
    {
        var writer = NewWriter();

        Provider.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    static DialectSql EmitDelete(DeleteStatement ast)
    {
        var writer = NewWriter();

        Provider.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    static DialectSql EmitInsert(in InsertStatement ast)
    {
        var writer = NewWriter();

        Provider.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    static JoinClause Join(TableRef side, IReadOnlyList<ColumnContract> columns, SqlExpression on)
        => new(JoinKind.Inner, new SourceRows(side, columns, [[new ParameterValue(1, columns[0])]]), on);

    /// <summary>手搭一份合并语句，形状与 <c>EntityTracker.WriteMergedAsync</c> 同构（T-SQL 那份在
    /// <see cref="ChangeGuardTests"/>）。值在这里是常量，为的是让文本能被黄金样本钉住；
    /// <paramref name="kind"/> 与 <paramref name="onCondition"/> 两个旋钮只服务那两条拒绝用例 ——
    /// 正常写法永远是 INNER 带 ON。</summary>
    static DialectSql Merge(string[] members, JoinKind kind = JoinKind.Inner, bool onCondition = true)
    {
        var contract = Kingbase.Orm().Schemas.Contract<Order>();
        var stored = new TableRef(0, "t0", contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var keys = contract.PrimaryKey;
        ColumnContract[] sets = [.. members.Select(m => contract.Columns.Single(c => c.MemberName == m))];
        ColumnContract[] columns = [.. keys, .. sets];

        var rows = new IReadOnlyList<SqlExpression>[2];

        for (var row = 0; row < rows.Length; row++)
        {
            var cells = new SqlExpression[columns.Length];

            for (var i = 0; i < cells.Length; i++)
                cells[i] = new ParameterValue(i == 0 ? 7 + row : (object?) ("值" + i), columns[i]);

            rows[row] = cells;
        }

        SqlExpression? on = null;

        if (onCondition)
            foreach (var key in keys)
            {
                var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, key), new ColumnExpression(side, key));

                on = on is null ? equal : new SqlBinaryExpression(BinaryOperator.And, on, equal);
            }

        SqlExpression[] differences = [.. sets.Select(column => Provider.ChangeGuard.BuildDifference(
            column, new ColumnExpression(stored, column), new ColumnExpression(side, column)))];

        SqlExpression where = differences[^1];

        for (var i = differences.Length - 2; i >= 0; i--)
            where = new SqlBinaryExpression(BinaryOperator.Or, differences[i], where);

        return EmitUpdate(new UpdateStatement
        {
            Table = stored,
            Sets = [.. sets.Select(column => new SetClause(column, new ColumnExpression(side, column)))],
            Where = where,
            Joins = [new JoinClause(kind, new SourceRows(side, columns, rows), on)],
        });
    }

    DialectSql Merge(string member) => Merge([member]);

    DialectSql Merge(string first, string second) => Merge([first, second]);
}
