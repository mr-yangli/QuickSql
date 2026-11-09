using System.Data.Common;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.OracleBase;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>达梦（Oracle 血统）的 SQL <b>文本</b>。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与前两族的离线套件同一分工：黄金样本钉版式，结构断言钉"凭什么长这样"。
///
/// <para><b>这一批样本证明的是回归，不是"达梦接受这条 SQL"</b>，而且这一族的这句话比前两家更硬：
/// 授权的活库只有两台 SQL Server（§13.4 的两台实例），本机没有一台 Oracle 血统的实例，所以这些 <c>.sql</c>
/// 是自产基线 —— 一句都没有在真正的解析器上跑过。正式记账在 §13.5 的能力出处台账（<see
/// cref="CapabilityLedgerTests"/> 里这一家逐位标着「离线已钉」或「仅厂商文档」，「真库跑过」那一档一颗都没有），
/// 写在这一段的目的是：读到这一行的红或绿时，不要把它当成对真库行为的断言。</para>
///
/// <para>样本按<b>形状</b>选而不是按链式方法数（§13.3）：只收"与前两族不同形"或"这一族独有"的那几条，
/// 同形的交给 <see cref="DialectParityTests"/> 逐字对读，不再复制一份基线。</para>
///
/// <para>这个类里同时构造<b>血统基类</b>（<see cref="Family"/>）与<b>叶</b>（<see cref="Provider"/>）：
/// 前者量"这一族的公共形状"，后者只该量点名过的那一处覆写（今天是自增写法）。两家的产出逐字相同之处
/// 就是继承，不同之处必须能被这一类的某一条断言指出来。</para></summary>
public sealed class DamengDialectTests
{
    static readonly IDbProvider Provider = Dameng.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>血统基类本身也实例化一次：<b>这一族的"空壳"主张要被量出来</b> —— 叶与基类逐字同形的部分
    /// 才是继承，不同形的部分只许是点名过的那几处（今天点名的一处：自增写法）。
    /// 【v0.37 第三笔】这一族从今天起有<b>三颗真叶子</b>，而"改动者只有达梦一颗"这一格不变：另两颗
    /// （神通、本尊）各自与这颗探针逐字同形，见 <c>ShenTongDialectTests</c> 与 <c>OracleDialectTests</c>。</summary>
    static readonly IDbProvider Family = new OracleFamilyProbe(DbKind.Oracle, SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名。理由与金仓那一份同一条：三族按形状起的名字是同一批，
    /// 不带目录参数就会改写别家的基线（金仓那一次是复跑整套才发现的，见 <c>KingbaseDialectTests</c> 的注释）。</summary>
    const string Leaf = "dameng";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Leaf);

    static QuickSqlOrm orm() => Dameng.Orm();

    /// <summary>带模式的模型：只有 <see cref="Schemas_bit_has_a_sample_that_qualifies_a_table"/> 需要它。
    /// 与 <c>ChangeGuardTests</c> 同一手法（Fluent 覆写而不是新建实体）：G1 只认 <c>[Table]</c>，
    /// 新加一个实体会把三家的对拍账牵连进来，而这一格要的只是"契约上写了一个模式名"这一个事实。</summary>
    static QuickSqlOrm ormWithSchema()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().ToTable("QuickSqlOrder", "app");

        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Family, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Family, model)));
    }

    /// <summary>只有一用例需要改选项：<c>Skip/Take</c> 无 <c>OrderBy</c> 在共用层默认是抛，
    /// 而这一族的 <c>ROWNUM</c> 本来就不要求序（要的是确定性，不是语法），所以要看它长什么样得先放开策略。</summary>
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

    // ── 标识符：引号之外还要折叠 ──────────────────────────────────────

    [Fact]
    public void Every_identifier_is_double_quoted_and_folded_to_upper_case()
    {
        var sql = orm().Select<Order>().ToSql();

        Verify("select-entity", sql);

        // 与前两家同一格的理由、第三种的形状：不带引号的 QuickSqlOrder 在库里存的是 QUICKSQLORDER，
        // 而金仓那一族折的是小写 —— 所以折叠方向本身是方言知识，写在 IdentifierPolicy 里而不是调用方。
        Checks.Has("FROM \"QUICKSQLORDER\" \"T0\"", sql);
        Checks.Has("SELECT \"T0\".\"ID\", \"T0\".\"CUSTOMERNAME\"", sql);
        Checks.Absent("[", sql);
        Checks.Absent("\"QuickSqlOrder\"", sql);
    }

    [Fact]
    public void A_table_alias_is_written_without_the_as_keyword()
    {
        var sql = orm().Select<Order>().ToSql();

        // 这一家的表别名是"表名后面直接跟名字"。发出 AS 在这一族是语法错（列别名才用 AS），
        // 而它不会因为写错而少一列 —— 只会整句被拒，所以钉在文本上而不是钉在能力位上。
        Checks.Has("FROM \"QUICKSQLORDER\" \"T0\"", sql);
        Checks.Absent(" AS \"T0\"", sql);
    }

    // ── 分页：§9.4 那三套实现里的第三套（包装而不是尾巴）──────────────

    [Fact]
    public void Take_only_wraps_the_select_in_one_rownum_layer_and_adds_no_columns()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Take(5).ToSql(out var parameters);

        Verify("paging-take-only", sql);

        // 只有一层包装，而且外层是 SELECT *：没有中间那层造出来的伪列，所以这一档不多带列。
        Assert.Single(parameters);
        Checks.Has(") WHERE ROWNUM <= :p0", sql);
        Checks.Absent("QUICKSQL$ROWNUM", sql);
        Checks.Absent("LIMIT", sql);
        Checks.Absent("FETCH", sql);
    }

    [Fact]
    public void Skip_needs_a_second_layer_because_rownum_is_numbered_before_the_predicate()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var parameters);

        Verify("paging-order-skip-take", sql);

        // 直接写 WHERE ROWNUM > 10 永远为空：编号在谓词里，第 1 行拿到 1 时比较不成立就不继续编号。
        // 中间那层把伪列落成一颗真列，外层才有东西可比 —— 这一格是这一族独有的形状，前两族都没有。
        Checks.Has("SELECT \"QUICKSQL$PAGE\".*, ROWNUM \"QUICKSQL$ROWNUM\" FROM (", sql);
        Checks.Has(") \"QUICKSQL$PAGE\" WHERE ROWNUM <= (:p0 + :p1)) WHERE \"QUICKSQL$ROWNUM\" > :p2", sql);

        // 上界留在 SQL 里（:p0 + :p1）而不是在 CLR 里加好：加完就不是参数了，
        // 而这一档的调用方是分页 —— 同一条语句 repeated，共享游标要的是文本不变。
        Assert.Equal([10, 5, 10], parameters.Select(p => (int) p.Value!).ToArray());
        Assert.Equal([":p0", ":p1", ":p2"], parameters.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Paging_needs_no_ordering_clause_because_rownum_is_not_a_window()
    {
        var sql = orm(PagingOrderingPolicy.Allow).Select<Order>().Skip(10).Take(5).ToSql();

        Verify("paging-without-ordering", sql);

        // 与前两家同一档的事实（ROWNUM 不要求 ORDER BY 是语法层的事实），但语义那一层更硬：
        // 没有序的 ROWNUM 分页在每一页之间可以任意重排，这一族不会替你补那句 ORDER BY (SELECT NULL)。
        Checks.Absent("ORDER BY", sql);
        Checks.Has("ROWNUM <= (:p0 + :p1)", sql);
    }

    // ── 谓词形状 ─────────────────────────────────────────────────────

    [Fact]
    public void An_empty_set_is_a_constant_false_because_the_family_has_no_boolean_value()
    {
        var none = Array.Empty<int>();

        var sql = orm().Select<Order>().Where(o => none.Contains(o.Id)).ToSql();

        Verify("where-false-empty-set", sql);

        // 与金仓那一族同一条判据、不同的文本：`WHERE false` 在这里没有 boolean 常量可以写
        // （§9.3 那一行"没有原生布尔"），而 1 = 0 与 T-SQL 同形 —— 三族在这里是 1=0 / false / 1=0。
        Checks.Has("WHERE 1 = 0", sql);
        Checks.Absent("WHERE false", sql);
    }

    [Fact]
    public void A_bool_operand_compares_to_a_number_because_there_is_no_boolean_type()
    {
        var paid = true;

        var sql = orm().Select<Order>().Where(o => paid && o.Amount > 1m).ToSql(out var parameters);

        Verify("where-boolean-flag", sql);

        // 金仓那一族这里必须是 IS TRUE（@p0 是 boolean，= 1 那一侧是 integer，直接类型错误）。
        // 这一族反过来：NUMBER(1) 列上 = 1 才是正常写法，IS TRUE 不存在。同一格语义、两家反向，
        // 而对拍读不出谁对 —— 只有 provider 自己知道（§10.5 的那一档）。
        Assert.Equal(2, parameters.Count);
        Checks.Has(":p0 = 1 AND \"T0\".\"AMOUNT\" > :p1", sql);
        Checks.Absent("IS TRUE", sql);
    }

    // ── 写目标：不能起别名，所以它用自己的表名引用自己 ────────────────

    [Fact]
    public void An_update_qualified_its_own_columns_with_the_table_name_because_there_is_no_target_alias()
    {
        var sql = orm().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql();

        Verify("update-self-qualified", sql);

        // 这一格是 §4 那句"目标别名由方言在建 AST 之前定下来"（WriteTargetAlias）唯一能被文本量出来的形状：
        // T-SQL 那一家前缀是 [t0]，金仓是 "t0"，这里只能是表名本身 —— 而起别名那一半被 EmitUpdateTarget 丢掉了。
        Checks.Has("UPDATE \"QUICKSQLORDER\" SET \"AMOUNT\" = \"QUICKSQLORDER\".\"AMOUNT\" + :p0 WHERE \"QUICKSQLORDER\".\"ID\" = :p1", sql);
        Checks.Absent("\"QUICKSQLORDER\" \"T0\"", sql);
    }

    [Fact]
    public void A_delete_carries_the_same_self_reference_because_its_target_takes_no_alias_either()
    {
        var sql = orm().Delete<Order>().Where(o => o.Id == 7).ToSql();

        Verify("delete-self-qualified", sql);

        // DELETE 与 UPDATE 共用一口（EmitDeleteTarget 默认转给 EmitUpdateTarget），所以这里的
        // "两半成对"是同一句 WriteTargetAlias 的第二次兑现。少掉这一条的话，"共用一口"只是注释里的说法。
        Checks.Has("DELETE FROM \"QUICKSQLORDER\" WHERE \"QUICKSQLORDER\".\"ID\" = :p0", sql);
        Checks.Absent("USING", sql);
    }

    [Fact]
    public void A_global_filter_on_the_write_target_is_qualified_with_the_table_name_not_an_alias()
    {
        var fresh = orm();

        fresh.Filters.Add<Ticket>(t => !t.Deleted);

        var sql = fresh.Delete<Ticket>().Where(t => t.Id == 7).ToSql();

        Verify("filter-delete-self-qualified", sql);

        // 上面那两条讲的是"没注册过滤器时"的写目标，这一条讲的是<em>注册之后它还是不是同一只表位</em>：
        // §10.7 的注入必须接进那条语句自己的 <see cref="TableRef"/>（别名由 WriteTargetAlias 定），
        // 而不是内核另造一只 t0。这里前缀一旦变成 "T0".，产物在库里是"无法绑定多部分标识符"
        // —— 一处只在真连库时才暴露的漏法，所以拿"不该出现"来钉（这一家没有活库，只能这样钉）。
        Checks.Has("DELETE FROM \"QUICKSQLTICKET\" WHERE \"QUICKSQLTICKET\".\"ID\" = :p0 AND NOT \"QUICKSQLTICKET\".\"DELETED\" = 1", sql);
        Checks.Absent("\"T0\".", sql);
    }

    // ── 主键回填：这一家没有能发出来的那一格 ──────────────────────────

    [Fact]
    public void Asking_for_the_generated_key_refuses_rather_than_emitting_sql_that_cannot_carry_it()
    {
        // §12.4 第 1 项的 L3→L5 只解决"一句插几行"，不解决"键落在哪里"。这一族两个口子都到不了：
        // RETURNING … INTO 要 OUT 绑定（AdoParameter 没有方向位）；语句后回读那第二个口子 v0.34 建起来了
        // （§14），但这一族的回读表达式（IDENTITYCURRVAL 那一档）没有任何授权实例问过 ——  declared false。
        // 【v0.34 原文订】本行原句是"语句后回读要 IKeyStrategy 上第二个方法"，那句在当天已经不再是要点。
        // 所以这里必须抛 —— 悄悄发一条不带回填的 INSERT 会得到"插进去 3 行，SaveChanges 报 3，键全丢"。
        var failure = Checks.Raised<QuickSqlException>(() => orm().Insert(row(1)).ExplainKeys());

        Checks.Has("没有可作为结果集发出的", failure.Message);
        Checks.Has("AdoParameter 没有方向位", failure.Message);

        // 反面那一样：不要键的 INSERT 正常发得出，而且不带任何回填痕迹。
        var plain = orm().Insert(row(1)).ToSql();

        Verify("insert-plain", plain);
        Checks.Absent("RETURNING", plain);
        Checks.Absent("OUTPUT", plain);
    }

    // ── §12.5 的合并档：这一族没有 UPDATE … FROM，所以闸门在拼树之前 ──

    [Fact]
    public void The_dialect_says_up_front_that_a_write_statement_cannot_carry_another_table()
    {
        Assert.False(Provider.Dialect.SupportsJoinedWrites);

        // 这一格从 v0.23 的"1∶2"变成了 v0.30 的"<b>2∶2</b>"：另两族仍是 true（T-SQL 的 UPDATE … FROM、
        // 金仓的 UPDATE … FROM / USING），而 MySQL 血统那一族与这一族同边 —— 它既没有 UPDATE … FROM，
        // 也没有共用层要的那种"带列名清单的行集"。它<em>有</em>多表 UPDATE，但那是另一条语法，
        // 与"行集 + 主键对齐"的形状不同源，折不动（见 MySqlDialect 的 EmitJoinedSources 那一段）。
        // 这一格是 SaveChanges 那一道闸门的依据，所以它不许被"为了让合并档跑起来"改成 true。
        Assert.True(SqlServer.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.True(Kingbase.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.False(MySql.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
    }

    [Fact]
    public void A_hand_built_merged_update_names_the_only_alternative_instead_of_dropping_the_join()
    {
        // 日常路径到不了这里（SupportsJoinedWrites 为 false 时 EntityTracker.Mergeable 第一道就退桶），
        // 手工构造 AST 才量得到发射层那一句：悄悄丢掉表源的合并 UPDATE 会把同一个值写给整张表。
        var failure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount)));

        Checks.Has("MERGE INTO", failure.Message);
        Checks.Has("SupportsJoinedWrites", failure.Message);
        Checks.Has("一句一实体", failure.Message);
    }

    [Fact]
    public void A_delete_with_joined_sources_refuses_on_the_same_gate_as_the_update()
    {
        var order = orm().Schemas.Contract<Order>();
        var item = orm().Schemas.Contract<OrderItem>();

        var stored = new TableRef(0, "t0", order.Table, order);
        var details = new TableRef(1, "t1", item.Table);

        var id = order.Columns.Single(c => c.MemberName == nameof(Order.Id));
        var parentOrderId = item.Columns.Single(c => c.MemberName == nameof(OrderItem.OrderId));

        // 连接条件写全：这一条用例要证的是"带着行集就抛"，所以不能让"缺 ON"成为它红的理由。
        var ast = new DeleteStatement
        {
            Table = stored,
            Joins =
            [
                new JoinClause(JoinKind.Inner, new SourceRows(details, [parentOrderId], [[new ParameterValue(1, parentOrderId)]]),
                    new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, id), new ColumnExpression(details, parentOrderId))),
            ],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, id), new ParameterValue(7, id)),
        };

        // 发射要留在 lambda 里面：写在外面就是"断言之前先抛"，那条红会是异常而不是这条用例。
        var failure = Checks.Raised<QuickSqlException>(() => EmitDelete(ast).Text);

        Checks.Has("不接受额外的表源", failure.Message);
    }

    // ── §12.4 第 2 项的变更守卫：三段式回来了，而 LOB 比不了 ───────────

    [Fact]
    public void A_not_null_column_still_needs_only_the_inequality()
        => Checks.Has("\"QUICKSQLORDER\".\"AMOUNT\" <> \"T1\".\"AMOUNT\"", GuardText(nameof(Order.Amount)));

    [Fact]
    public void A_nullable_column_goes_back_to_the_three_way_or_because_there_is_no_distinct_from()
    {
        var sql = GuardText(nameof(Order.PaidAt));

        Verify("update-guard-three-way", sql);

        // 金仓那一家一句 IS DISTINCT FROM 就够；这里三段的形状与 T-SQL 同一份（ChangeGuardShapes），
        // 因为限制同源：布尔表达式不是一等值，(a IS NULL) <> (b IS NULL) 那句近似式过不去。
        // 第三家落回第一家的形状，而不是新发明第三种 —— 这一格是"守卫那三段的形状是共用层的事、
        // 用不用它是库种的事"这条拆分的见证（拆之前它是 SqlServerChangeGuard 的私有方法）。
        Checks.Has("\"QUICKSQLORDER\".\"PAIDAT\" <> \"T1\".\"PAIDAT\" OR (\"QUICKSQLORDER\".\"PAIDAT\" IS NULL AND \"T1\".\"PAIDAT\" IS NOT NULL) OR (\"QUICKSQLORDER\".\"PAIDAT\" IS NOT NULL AND \"T1\".\"PAIDAT\" IS NULL)", sql);
        Checks.Absent("IS DISTINCT FROM", sql);
    }

    [Fact]
    public void A_lob_column_is_refused_by_the_gate_before_any_shape_is_built()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        // 无长度的字符串在这一族落 CLOB（§9.3），而 LOB 不能出现在等号两侧 —— 那是这一族的规则，
        // 不是实现缺口。所以拒的地方是 Compares（守卫的闸门），而不是 BuildDifference（守卫的形状）。
        Assert.Equal("CLOB", Family.Types.GetStoreType(note));
        Assert.False(Provider.ChangeGuard.Compares(note));
        Assert.True(Provider.ChangeGuard.Compares(contract.Columns.Single(c => c.MemberName == nameof(Order.Amount))));

        // BuildDifference 刻意不抛：它只在合并档被调用，而那一档在 Mergeable 的第一道闸门就进不来
        // （SupportsJoinedWrites 为 false）。在这里再抛一次是同一句话讲两遍，还会让"闸门在前"这条
        // 顺序断言失效 —— §12.5 要的是"一个盖不住的列都不会被问到"，问到就说明闸门漏了。
        var shape = Provider.ChangeGuard.BuildDifference(note,
            new ColumnExpression(new TableRef(0, "QUICKSQLORDER", contract.Table, contract), note),
            new ColumnExpression(new TableRef(1, "t1", contract.Table), note));

        Assert.IsType<SqlBinaryExpression>(shape);

        // 宁可整批退回逐条，也不换 CHECKSUM/哈希近似：撞上的那一行会被判成"没变"，新值就被静默丢掉。
        // 判据是逐列的：同一张表上其余各列不受牵连（与 ChangeGuardTests 那两条同一个形状）。
        foreach (var member in new[] { nameof(Order.Id), nameof(Order.State), nameof(Order.CreatedAt), nameof(Order.PaidAt) })
            Assert.True(Provider.ChangeGuard.Compares(contract.Columns.Single(c => c.MemberName == member)));
    }

    // ── 函数模板：同一条链，三家的模板表 ─────────────────────────────

    [Fact]
    public void The_same_chain_renders_this_family_s_function_templates()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026 && Math.Round(o.Amount, 2) > 1m)
            .ToSql();

        Verify("functions", sql);

        Checks.Has("LENGTH(\"T0\".\"CUSTOMERNAME\") > :p0", sql);
        Checks.Has("EXTRACT(year FROM \"T0\".\"CREATEDAT\") = :p1", sql);

        // 没有金仓那三层 CAST(… AS numeric)：那一格是"9.2 内核上 ROUND 只吃 numeric"逼出来的，
        // 而这一族的 NUMBER 不分档 —— 抄过来会得到三条多一 cast 的语句。
        Checks.Has("ROUND(\"T0\".\"AMOUNT\", :p2)", sql);
        Checks.Absent("DATEPART", sql);
        Checks.Absent("CAST(", sql);
    }

    [Fact]
    public void Blank_is_one_is_null_test_because_the_empty_string_and_null_are_the_same_value()
    {
        var sql = orm().Select<Order>()
            .Select(o => new
            {
                Cut = o.CustomerName!.Substring(1, 2),
                At = o.CustomerName!.IndexOf("x"),
                Blank = string.IsNullOrWhiteSpace(o.Note),
            })
            .ToSql();

        Verify("string-functions", sql);

        // 另三家发 (x IS NULL OR TRIM(x) = '')，这一族发 (TRIM(x) IS NULL)：加那段 = '' 不是多余而是错 ——
        // '' 就是 NULL，那次比较两侧同为 NULL 得到 UNKNOWN，"空格串算不算空"这一格会永远不成立，
        // 不报错、不红、只是少一批行。这是第三家给这一槽位带来的第三种模板，而 v0.30 的第四家
        // 站回金仓那一边（它没有"'' 就是 NULL"这一条），所以三形仍是三形而不是四形。
        Checks.Has("(TRIM(\"T0\".\"NOTE\") IS NULL)", sql);
        Checks.Absent("= ''", sql);

        // SUBSTR 与 T-SQL 同形（金仓用 SQL 标准的 SUBSTRING … FROM … FOR …）；INSTR 与 STRPOS 同形。
        Checks.Has("SUBSTR(\"T0\".\"CUSTOMERNAME\", :p0 + 1, :p1)", sql);
        Checks.Has("(INSTR(\"T0\".\"CUSTOMERNAME\", :p2) - 1)", sql);
    }

    [Fact]
    public void Date_arithmetic_is_a_named_interval_function_not_a_multiplication()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Tomorrow = o.CreatedAt.AddDays(1), Ceiling = Math.Max(o.Amount, 1m) })
            .ToSql();

        Verify("date-and-number", sql);

        // TIMESTAMP + NUMBER 在这一族是类型错误（DATE + NUMBER 才是天数），而契约上"这列是 DATE
        // 还是 TIMESTAMP"发射层不知道 —— NUMTODSINTERVAL 是选谁都不能错的那个写法。
        Checks.Has("(\"T0\".\"CREATEDAT\" + NUMTODSINTERVAL(:p0, 'DAY'))", sql);
        Checks.Absent("INTERVAL '1 day'", sql);

        // GREATEST 原生（与金仓同形；T-SQL 要 CASE 展开）。
        Checks.Has("GREATEST(\"T0\".\"AMOUNT\", :p1)", sql);
    }

    // ── CodeFirst：这一家的类型名、默认模式与自增 ─────────────────────

    [Fact]
    public void Create_table_qualifies_nothing_when_the_contract_names_no_schema()
    {
        var sql = Statement(w => Family.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Verify("ddl-order", sql);

        // T-SQL 补 dbo、PG 系补 public，这一家<em>什么都不写</em>：那一家没有"所有人都能进"的公共模式，
        // 裸名说的是"连接所带的那个模式"。补一个假名字会得到一句 schema does not exist。
        Checks.Has("CREATE TABLE \"QUICKSQLORDER\" (", sql);
        Checks.Absent("\"APP\".", sql);
        Checks.Absent("\"DBO\".", sql);
        Checks.Absent("\"PUBLIC\".", sql);

        // 类型名逐列：量纲写在列上（VARCHAR2(n CHAR)）、无长度字符串落 CLOB、时间戳带精度、枚举是 NUMBER(10)。
        Checks.Has("\"ID\" NUMBER(10) GENERATED BY DEFAULT AS IDENTITY NOT NULL", sql);
        Checks.Has("\"CUSTOMERNAME\" VARCHAR2(64 CHAR) NULL", sql);
        Checks.Has("\"STATE\" NUMBER(10) NOT NULL", sql);
        Checks.Has("\"AMOUNT\" NUMBER(18,4) NOT NULL", sql);
        Checks.Has("\"CREATEDAT\" TIMESTAMP(3) NOT NULL", sql);
        Checks.Has("\"NOTE\" CLOB NULL", sql);

        // 主键约束没有 CLUSTERED：那一颗是 T-SQL 的存储布局开关。
        Checks.Has("CONSTRAINT \"PK_QUICKSQLORDER\" PRIMARY KEY (\"ID\")", sql);
        Checks.Absent("CLUSTERED", sql);
        Checks.Absent("NVARCHAR", sql);
        Checks.Absent("DATETIME2", sql);
    }

    [Fact]
    public void A_named_schema_qualifies_the_table_and_folds_with_it()
    {
        var sql = Statement(w => Family.CodeFirst.EmitCreateTable(w, ormWithSchema().Schemas.Contract<Order>()));

        Verify("ddl-qualified", sql);

        // 这一份样本要写的只有一句：限定符出来了，而且模式名与表名走的是同一个归一口 ——
        // 模型里写的是小写 app，出来的必须是 "APP"，否则 §9.2 那句"建出来的表与查的表是同一张"
        // 会在模式这一层破掉。它单独成一份基线而不是并进 ddl-order：这一家的<em>默认</em>形状是
        // "什么都不写"，所以"写了模式"是这一族的第二种形状，而不是第一种的一个参数。
        Checks.Has("CREATE TABLE \"APP\".\"QUICKSQLORDER\" (", sql);
    }

    [Fact]
    public void The_leaf_changes_only_the_identity_clause_of_that_whole_table()
    {
        var contract = orm().Schemas.Contract<Order>();

        var leaf = Statement(w => Provider.CodeFirst.EmitCreateTable(w, contract));
        var family = Statement(w => Family.CodeFirst.EmitCreateTable(w, contract));

        Verify("ddl-order-dameng", leaf);

        // §9.1 记的那一格：达梦的自增长成像 SQL Server（它的语法表大量取自那一家），
        // 而"叶与基类只差这一行"本身就是这一版的空壳证据 —— 其余部分逐字同形由下面这一句钉住。
        Checks.Has("\"ID\" NUMBER(10) IDENTITY(1,1) NOT NULL", leaf);
        Assert.Equal(family.Replace("GENERATED BY DEFAULT AS IDENTITY", "IDENTITY(1,1)"), leaf);
    }

    [Fact]
    public void Column_changes_and_comments_are_this_family_s_shapes()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        var codeFirst = Family.CodeFirst;

        var sql = string.Join(" ; ",
            Statement(w => codeFirst.EmitAddColumn(w, contract.Table, note)),
            Statement(w => codeFirst.EmitAlterColumnPrecision(w, contract.Table, amount)),
            Statement(w => Assert.True(codeFirst.TryEmitColumnComment(w, contract.Table, name))));

        Verify("ddl-alter-and-comment", sql);

        // 加列的括号是语法的一部分（它同时也是"一次加多列"的那个写法），漏掉它在 Oracle 上对某些子句就是错。
        Checks.Has("ALTER TABLE \"QUICKSQLORDER\" ADD (\"NOTE\" CLOB NULL)", sql);

        // 改精度一句说完：MODIFY 把类型与可空性一起改，所以这里没有金仓那条 " ; " 串联的第二句。
        // 整列定义重写而不是只写类型 —— MODIFY 会按新定义重建那一列，漏写可空性等于把它交回库的默认。
        Checks.Has("ALTER TABLE \"QUICKSQLORDER\" MODIFY (\"AMOUNT\" NUMBER(18,4) NOT NULL)", sql);
        Checks.Absent("ALTER COLUMN", sql);

        // 列注释是原生语法，与金仓同形、与 T-SQL 的 sp_addextendedproperty 分岔；文本不带 N 前缀。
        Checks.Has("COMMENT ON COLUMN \"QUICKSQLORDER\".\"CUSTOMERNAME\" IS ''", sql);
        Checks.Absent("sp_addextendedproperty", sql);
        Checks.Absent("N'", sql);
    }

    [Fact]
    public void A_full_text_index_is_refused_rather_than_emitted_as_an_ordinary_btree()
    {
        var contract = orm().Schemas.Contract<Order>();

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Family.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("FT_QuickSqlOrder_Note", IndexKind.FullText, [nameof(Order.Note)]) { ColumnNames = ["Note"] })));

        // 那一家的全文是域索引（CTXSYS.CONTEXT）+ 一套 CONTAINS 谓词，而且那是独立安装的组件。
        // 悄悄发一个普通索引会得到"建好了但查不了全文"——比不建更坏（§9.2 那句"宁可报错"）。
        Checks.Has("Oracle Text", failure.Message);
    }

    // ── 内联字面量（ToLogSql 那一档）─────────────────────────────────

    [Fact]
    public void Inlined_text_carries_no_N_prefix_and_dates_become_an_explicit_format_call()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var createdAt = contract.Columns.Single(c => c.MemberName == nameof(Order.CreatedAt));

        var insert = new InsertStatement
        {
            Table = new TableRef(0, "t0", contract.Table, contract),
            Columns = [name, note, createdAt],
            Rows = [[new ParameterValue("客户甲", name), new ParameterValue("备注", note), new ParameterValue(new DateTime(2026, 3, 4, 5, 6, 7, 890), createdAt)]],
        };

        var log = EmitInsert(in insert).ToLogSql(Provider);

        // N'…' 在这一族是 NVARCHAR2 的字面量，而映射发的是 VARCHAR2(n CHAR) —— 加前缀等于给一次比较
        // 塞进一次隐式转换。与金仓同结论、不同理由（那一家是"前缀会被当成不认识的语法"）。
        Checks.Has("'客户甲'", log);
        Checks.Absent("N'", log);

        // 日期时间必须写成函数调用：ANSI 那种 TIMESTAMP '…' 字面量吃会话的 NLS_TIMESTAMP_FORMAT，
        // 格式串写死在语句里，会话说什么都不影响它读出什么。
        Checks.Has("TO_TIMESTAMP('2026-03-04 05:06:07.890','YYYY-MM-DD HH24:MI:SS.FF3')", log);
    }

    [Fact]
    public void Binary_inlines_here_because_only_one_reading_of_the_text_exists()
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

        var log = EmitInsert(in insert).ToLogSql(Provider);

        // 金仓那一族拒内联的理由是 bytea_output（同一个字面量在两个会话设置下读出两个值），
        // 而 HEXTORAW 的入参只有十六进制这一种解释 —— 会话里没有能改它含义的开关。
        // 这就是"能不能内联"要按形状判而不是按族判的第三份见证。
        Checks.Has("HEXTORAW('010F')", log);
        Checks.Has("'货1'", log);
    }

    [Fact]
    public void A_floating_point_literal_is_written_as_is_and_only_the_non_finite_ones_are_refused()
    {
        // 与前两家同名那条用例的<em>反向</em>断言：T-SQL 与金仓要给纯整数形式补一个小数点
        // （3 在 real/double8 列上是整数常量，等值比较要走隐式转换），而这一族的 NUMBER 不分整数与浮点，
        // 3 与 3.0 是同一个值的两种写法。在这里补点只会让黄金样本比出一处没有语义变化的改动。
        Assert.True(Provider.Values.TryInlineLiteral(3d, null, out var whole));
        Assert.Equal("3", whole);

        Assert.True(Provider.Values.TryInlineLiteral(2.5f, null, out var fraction));
        Assert.Equal("2.5", fraction);

        // 指数式原样留着：两族那条"不许给指数式补点"的判据在这里没有对象，而字符集校验仍然挡得住 NaN。
        Assert.True(Provider.Values.TryInlineLiteral(1e-08, null, out var tiny));
        Assert.Equal("1E-08", tiny);

        // 非有限值要拒：这一族能写的只有 TO_BINARY_DOUBLE('NaN', …) 那一档带引号的形状，
        // 而它的含义取决于会话的 NLS_NUMERIC_CHARACTERS —— 与金仓拒 bytea 同一条判据（§10.4 的会话那一半）。
        Assert.False(Provider.Values.TryInlineLiteral(double.NaN, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.PositiveInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.NegativeInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(float.NegativeInfinity, null, out _));
    }

    // ── 槽位本身：这一族声明了什么、没声明什么 ────────────────────────

    [Fact]
    public void The_provider_declares_no_bulk_strategy_so_the_chunker_uses_its_conservative_fallback()
    {
        // 没有活库 ⇒ 没有能举证的参数预算数字。另三家各有一颗指得出的出处：驱动侧的 2100、
        // PG 协议 int16 的 32767、MySQL 预处理语句的 65535 —— 后两颗同样没在真库上证过（§13.5），
        // 但它们的<em>上限本身</em>写在那一族的协议里；这一族指不到这样一颗数，所以选 null。
        // 报大了得到真库一句"参数过多"，报小了只是多几次往返 —— 成本差一个量级。
        Assert.Null(Provider.Bulk);

        var plan = orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        // 600 行 × 7 列（Id 是自增列不进清单）按共用层那颗 100 的预算切：900/7 → 每块 12 行 → 50 块。
        // 同一批行在金仓那一档是一块（32767），在 T-SQL 是三块（2100），在 MySQL 也是一块（65535）——
        // 这四份数字都在 DialectParityTests 里逐家对读，写死在这里的是"兜底分支有真实消费者、
        // 且它给出的是这几个数"。<b>只有这一族是 50 块</b>那一格今天仍然成立，但成立的方式变了：
        // 原来四家里三家块数各不同（3/1/50），现在 1 出现两次，可数的判据落到预算与每语句行数上。
        //
        // <para><b>v0.31 把上面那句"仍然成立"推翻了</b>（原文按 §13.1 的规矩留着）：第五族虚谷也没有可举证的
        // 预算数字，于是兜底分支从今天起有<b>两个</b>消费者，"只有这一族是 50 块"不再是这一家的属性而是一格的
        // 形状。这一格翻得不只是那句主张 —— 它翻的是<em>判据本身</em>：v0.23 写这一条时钉的是"孤例所以看得见"，
        // 五家之后可钉的只有"两家同数"，而两家的来路不同（Oracle 血统说"驱动有数组绑定而这一族没有"、
        // 虚谷说"两档出处都给不出一个颗数"）。同一颗 null 在两处是两条不同的理由，这一点由
        // <c>XuguDialectTests.The_bulk_slot_is_the_second_one_to_answer_null…</c> 那份对读守住。</para>
        Assert.Equal(100, plan.ParameterBudget);
        Assert.Equal(7, plan.ColumnsPerRow);
        Assert.Equal(12, plan.RowsPerStatement);
        Assert.Equal(50, plan.Chunks.Count);
        Assert.Equal(600, plan.Chunks.Sum(static c => c.RowCount));
        Checks.Has("参数预算 100", plan.Reason);
        Checks.Has("跨块不是原子的", plan.Reason);
    }

    [Fact]
    public void The_family_guid_is_text_because_there_is_no_native_uuid_type()
    {
        var contract = orm().Schemas.Contract<SessionToken>();
        var id = contract.Columns.Single(c => c.MemberName == nameof(SessionToken.Id));

        // Guid_Char36 这一位从 v0.10 立项起一直没有 provider 真的声明过（金仓那一族有原生 uuid），
        // 到这里才第一次有代码走到它 —— 所以这一格不只是类型映射，也是能力位的一条实证。
        Assert.Equal("CHAR(36)", Family.Types.GetStoreType(id));
        Assert.True(Provider.Capabilities.HasFlag(DbCapabilities.Guid_Char36));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Boolean_Type));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Returning));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Upsert_MergeInto));
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>血统基类的可实例化形态：它今天是 <c>abstract</c>（没有一种库种叫"基类"），
    /// 而这一族的"叶与基类同形"这条主张需要一个基类实例才量得出来。金仓那一家没有这个探针，
    /// 因为它的叶零覆写、主张是"完全同形"，直接对读两家的产出就够（<see cref="DialectParityTests"/>）。
    /// <b>【v0.37 第三笔】上面那句"没有一种库种叫基类"到今天仍然对</b>（这颗类照旧是 <c>abstract</c>，
    /// 本笔没有为它加实例入口），但同一段里那句隐含的自辩——"探针之所以存在，是因为这一族的基类没有住户"——
    /// 从本笔起只在这一家成立：<c>OracleDialectTests</c> 里那一颗同写法探针（<c>OracleLeafProbe</c>，
    /// <c>Kind</c> 也同取 <see cref="DbKind.Oracle"/>）与本尊叶子如今<b>除了 CLR 类型名与清单键以外
    /// 没有任何可观察差别</b>，连拒绝文案都不分家。于是"探针"这一档在本仓库第一次有了两种身份：
    /// 这一家它是基类的替身，另两家它是与真叶子重影的替身。本格不删原文也不改判据（替身照旧要留），
    /// 只把"为什么留"那句换成它今天的实情。</summary>
    sealed class OracleFamilyProbe(DbKind kind, DbProviderFactory factory) : OracleProviderBase(kind, factory)
    {
    }

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

    /// <summary>守卫的文本。<b>这一族的 UPDATE … FROM 不存在，所以合并档发不出去</b>（上面那两条拒绝用例
    /// 量的就是这件事），而守卫仍然要有文本见证 —— 于是把它挂在一条<em>不带表源</em>的最小 UPDATE 上发射：
    /// 那颗 SET 与守卫无关，它存在的理由只是"UPDATE 必须有一句 SET"。
    ///
    /// <para>两侧的<em>名字</em>按 <c>EntityTracker</c> 的真实构造取：表侧走 <c>WriteTargetAlias</c>
    /// （这一族是表名本身），行集侧仍是 <c>t1</c>。所以这段文本里能读出的东西只有守卫的形状与前缀的来路，
    /// 而"这条语句是不是 SaveChanges 会发的那一条"——它不是，那一档在这一族永远不会被走到。</para></summary>
    static string GuardText(string member)
    {
        var contract = orm().Schemas.Contract<Order>();
        var column = contract.Columns.Single(c => c.MemberName == member);

        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(amount, new ParameterValue(1m, amount))],
            Where = Provider.ChangeGuard.BuildDifference(column,
                new ColumnExpression(stored, column), new ColumnExpression(side, column)),
        };

        return EmitUpdate(ast).Text;
    }

    /// <summary>手搭一份合并 UPDATE，形状与 <c>EntityTracker.WriteMergedAsync</c> 同构（T-SQL 那份在
    /// <see cref="ChangeGuardTests"/>，金仓那份在 <see cref="KingbaseDialectTests"/>）。
    /// <b>在这一族它唯一的用途是"抛得准不准"</b>：前两族拿它量文本，这里发射必然在
    /// <c>EmitJoinedSources</c> 那一句上红 —— 所以它不需要键、行集值都真实，只要表源在就够了。</summary>
    static DialectSql MergedUpdate(string member)
    {
        var contract = orm().Schemas.Contract<Order>();
        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var key = contract.PrimaryKey[0];
        var set = contract.Columns.Single(c => c.MemberName == member);
        ColumnContract[] columns = [key, set];

        var rows = new IReadOnlyList<SqlExpression>[2];

        for (var row = 0; row < rows.Length; row++)
            rows[row] = [new ParameterValue(7 + row, key), new ParameterValue("值", set)];

        var on = new SqlBinaryExpression(BinaryOperator.Equal,
            new ColumnExpression(stored, key), new ColumnExpression(side, key));

        var difference = Provider.ChangeGuard.BuildDifference(set,
            new ColumnExpression(stored, set), new ColumnExpression(side, set));

        return EmitUpdate(new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(set, new ColumnExpression(side, set))],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, key), new ParameterValue(7, key)),
            Joins = [new JoinClause(JoinKind.Inner, new SourceRows(side, columns, rows), on)],
        });
    }
}
