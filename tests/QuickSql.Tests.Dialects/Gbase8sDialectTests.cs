using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.Gbase8s;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>GBase 8s（Informix 血统，第六档）的 SQL <b>文本</b>。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与前五族的离线套件同一分工：黄金样本钉版式，结构断言钉"凭什么长这样"。
///
/// <para><b>这一批样本证明的是回归，不是"GBase 8s 接受这条 SQL"</b>，而这一家把这句话推得比前五族更远一档：
/// 它不是"有文档而未实测"，而是<b>连文档都不是本厂商的</b>。§13.4 授权的实例里没有这一族，清单里
/// <c>gbase8s</c> 那一行从未解析成功过，而本层每一格的出处都是 IBM Informix 手册——"Informix 的即
/// GBase 8s 的"是一条<em>借来的前提</em>（§9.1 那一行只说同源，同源到哪一层本次没有材料）。
/// 所以这里没有一格能标【文档·厂商】，全部标【文档·IBM】，那一句"未实测"在这一家要读成
/// <b>"未实测，而且量它的那把尺子本身未验"</b>。逐格的档位差异写在 <see cref="Gbase8sProvider"/> 类注释末段，
/// 本文件的每一条 <c>Checks</c> 都只钉<em>我们发出去了什么</em>，不钉库会答什么。</para>
///
/// <para>样本按<b>形状</b>选而不是按链式方法数（§13.3）。这一族的目录（9 份）介于虚谷（6 份）与 MySQL 血统
/// （11 份）之间，而它比虚谷那一份多的三格<em>不是</em>因为差异更多，是因为这一家<em>没有可借的同形邻居</em>：
/// 虚谷有五条用例的形状与第三家逐字相同，于是可以只写 <c>Assert.Equal</c> 不生基线；这一家在寻址之外
/// 全异形（分页在句首、参数不带名字、写侧没有子句），一句 <c>Assert.Equal</c> 都提不出来，
/// 只能每一份都落一处磁盘。<b>"对读量不到的那一格要由基线量"就是这一族的目录理由</b>。</para>
///
/// <para>这个类只构造<em>一层</em> provider：<see cref="Gbase8sProvider"/> 直接实现 <see cref="IDbProvider"/>，
/// 没有血统基类（§9.1 的清单里挂在 Informix 底下的库种只有这一颗），所以也就没有
/// <c>MySqlDialectTests</c> 里那枚<em>空壳证据</em>用的基类探针。类注释第二段那句"尺子本身未验"在本文件里
/// 有一个具体形状：<b>下面没有任何一条用例断"这一家的库会接受"</b>，包括那条 <c>Assert.Equal</c>
/// —— 它与 <see cref="Kingbase"/> 对读的是<em>两颗策略值</em>，不是两份产出。</para></summary>
public sealed class Gbase8sDialectTests
{
    static readonly IDbProvider Provider = Gbase8s.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名。理由与前五家同一条：不带目录参数就会改写别家的基线。</summary>
    const string Leaf = "gbase8s";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Leaf);

    static QuickSqlOrm orm() => Gbase8s.Orm();

    /// <summary>PG 血统那一颗。<b>本文件只有一条用例以它为判据</b>（下面那格 <c>Assert.Equal</c>），
    /// 而且判据是<em>策略值</em>不是文本：这一家在寻址上与它逐字同形，在寻址之外与它处处异形，
    /// 所以拿它的 <c>ToSql()</c> 对读会得到一句一半真一半假的断言。</summary>
    static QuickSqlOrm kingbase() => Kingbase.Orm();

    /// <summary>带模式的模型：只有 <see cref="A_named_schema_qualifies_the_table_as_a_owner"/> 需要它。
    /// 与 <c>ChangeGuardTests</c>、<c>XuguDialectTests</c> 同一手法（Fluent 覆写而不是新建实体）。</summary>
    static QuickSqlOrm ormWithSchema()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().ToTable("QuickSqlOrder", "app");

        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));
    }

    static Order row(int n) => new()
    {
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, 890),
    };

    // ── 标识符：第五份字形，而它是本仓库第一颗"与另一族逐字相同"的策略 ──

    [Fact]
    public void The_policy_is_the_first_one_identical_to_another_family_s_while_the_provenance_is_not()
    {
        // 这一条量的是本家最反直觉的一格：一颗策略值第一次在两族各立一份出处。双引号 + 区分大小写 +
        // 不折 + 混合大小写才加引号，五格参数一字不差，而 PG 系那一颗的来路是"厂商文档写明折小写所以恒加引号"，
        // 本家那一颗的来路是"SELECT 语法页上'未定界的标识符折成小写'那一句"。
        // <b>两条独立观察落到同一份形状，这一格因此是"取值是事实而不是偏好"在本仓库的第一份见证</b>
        // （另四颗预设每一颗都只有一条来路）。
        Assert.Equal(IdentifierPolicy.PostgreSQL, IdentifierPolicy.Gbase8s);

        var sql = orm().Select<Order>().ToSql();

        Verify("select-entity", sql);

        // 不折，所以"建的时候叫什么、发的时候就是什么"——引号把这一格钉死，而这一家忘加引号时代价与
        // PG 系同一条而更大（库里存的是 quicksqlorder，我们发的是 "QuickSqlOrder"，指的不是同一张表）。
        Checks.Has("FROM \"QuickSqlOrder\" \"t0\"", sql);
        Checks.Has("SELECT \"t0\".\"Id\", \"t0\".\"CustomerName\"", sql);
        Checks.Absent("[", sql);
        Checks.Absent("`", sql);
        Checks.Absent("\"QUICKSQLORDER\"", sql);
        Checks.Absent("\"quicksqlorder\"", sql);

        // 别名不带 AS，所以这里与 PG 血统<em>不</em>逐字同形（那一家在 FROM 与别名之间多三颗字符 " AS "）。
        // 两串文本的差恰好就是那一颗关键字，而这里刻意<em>不做</em>归一化对读：抹掉那三颗就抹掉了
        // 本家在寻址之外那三处异形里的一处。"同一颗策略值"与"同一份产出"是两件事，这两句一起钉住那道缝。
        Checks.Absent(" AS \"t0\"", sql);
        Checks.Has(" AS \"t0\"", kingbase().Select<Order>().ToSql());
    }

    // ── 分页：§9.4 那句"三套"变四套的那一票 ──────────────────────────

    [Fact]
    public void Paging_is_a_qualifier_at_the_head_of_the_sentence_not_a_tail_clause()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var parameters);

        Verify("paging-first-and-skip", sql);

        // 第四种落点：它在句子里面（不像 ROWNUM 那样包一句），又不在句尾（不像另三套各占一条尾巴）。
        // 共用层为前三套留的两个槽位都接不住它，所以本仓库为它新立了第三口（EmitPagingQualifier）。
        Checks.Has("SELECT FIRST ? SKIP ? \"t0\".\"Id\"", sql);
        Checks.Has("ORDER BY \"t0\".\"Id\" ASC", sql);
        Assert.Equal([5, 10], parameters.Select(p => (int) p.Value!).ToArray());

        // 另三套的形状一个都不许漏进来——这一句在另五家是常规，在本家是第一颗"没有任何一套可借"的：
        // 它不是"选了 LIMIT 而不是 OFFSET…FETCH"，是这两条尾巴它都没有。
        Checks.Absent("LIMIT", sql);
        Checks.Absent("OFFSET :p", sql);
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWNUM", sql);
    }

    [Fact]
    public void Skip_without_first_sends_no_sentinel_because_absence_is_already_the_unbounded_form()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql(out var parameters);

        Verify("paging-skip-only", sql);

        // 这一格是 §9.4 之外<em>真正新长出来的那一格</em>：另五家里"光写偏移"的两家都要补一颗"没有上限"的数
        // （MySQL 血统 2^64−1、虚谷 2147483647），而本家的 FIRST 缺省即无上限，所以补任何一颗都是多发一颗参数。
        // 分布从"2 家补 ∶ 3 家不需要"长成 2 ∶ 4，而本家那一票的来路第五家各不同条
        // （PG 系是"语法允许光写 OFFSET"，T-SQL 是"必须有 FETCH 那一颗行数"）。
        Assert.Single(parameters);
        Assert.Equal(30, parameters[0].Value);
        Checks.Has("SELECT SKIP ? ", sql);
        Checks.Absent("FIRST", sql);
        Checks.Absent("2147483647", sql);
        Checks.Absent("18446744073709551615", sql);
    }

    [Fact]
    public void The_head_qualifier_is_the_only_place_where_parameter_numbers_come_out_in_text_order()
    {
        var paid = 7;

        var sql = orm().Select<Order>().Where(o => o.CustomerId == paid).OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var parameters);

        // 位置形占位符让"文本第几颗"与"表里第几颗"必须一致，而本家是唯一一处两者<em>恰好</em>同向的形状：
        // 限定词长在整句之最前，所以 FIRST/SKIP 那两颗比投影与谓词都先登记。
        // <b>这一条不是设计出来又验证过的，是槽位选在句首顺手保住的</b>——把它写成用例的理由是：
        // 下一版若把分页改回尾巴形（或新增一家尾巴形 + 位置形），这一句会红，而那正是它该红的时候。
        Assert.Equal(3, parameters.Count);
        Assert.Equal(5, parameters[0].Value);
        Assert.Equal(10, parameters[1].Value);
        Assert.Equal(7, parameters[2].Value);
        Checks.Has("WHERE \"t0\".\"CustomerId\" = ?", sql);
        Assert.Equal(3, Checks.Occurrences(sql, "?"));
    }

    // ── 参数：六家里第一颗名字不进文本的 ──────────────────────────────

    [Fact]
    public void A_placeholder_is_one_character_and_its_name_stays_inside_the_parameter_table()
    {
        var sql = orm().Select<Order>().Where(o => o.Amount > 1m && o.CustomerId != 7).ToSql(out var parameters);

        // 另五家的占位符在文本里带名字（@p0 / :p0 / $n），本家那一格只有一个字符。
        // 名字仍然登记（内核侧的编号、hint 的载体、ToLogSql 找参数的键），只是<em>不进文本</em>。
        Assert.Equal(2, parameters.Count);

        // 名字带着前缀，而那颗前缀在文本里<em>不出现</em>（文本里只有一颗裸 `?`）。
        // 于是摘前缀那颗开关（DriverAcceptsPrefixedParameterNames）在本家是惰性的：它管的名字这一家本来就不要，
        // 而这里把它写成正面断言，是因为 "?p0" 这一串在本家<em>只</em>活在内核侧——现场若在驱动里找它会找不到。
        Assert.Equal("?p0", parameters[0].Name);
        Assert.False(Provider.Dialect.DriverAcceptsPrefixedParameterNames);
        Checks.Has("\"t0\".\"Amount\" > ? AND \"t0\".\"CustomerId\" <> ?", sql);
        Checks.Absent("?p0", sql);
        Checks.Absent("@p0", sql);
        Checks.Absent(":p0", sql);
        Checks.Absent("$1", sql);
    }

    [Fact]
    public void ToLogSql_stays_identity_here_because_there_is_no_name_to_find()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        var insert = new InsertStatement
        {
            Table = new TableRef(0, "t0", contract.Table, contract),
            Columns = [name, note],
            Rows = [[new ParameterValue("客户甲", name), new ParameterValue("C:\\temp", note)]],
        };

        var emitted = EmitInsert(in insert);
        var log = emitted.ToLogSql(Provider);

        // <b>恒等，而且是整串恒等而不只是"占位符留着"</b>：那颗替换按 前缀+字母 的形状找占位符，
        // 而本家文本里是裸 <c>?</c>、后面不跟名字，于是一处都找不到。
        // 这里要说清断的是哪一层：Gbase8sValues <em>答得出</em>字符串的字面量
        // （下面第二条把它钉住），答不出的是"替换层问不到它"——binder 与替换层在本家第一次不一致，
        // 而那一条正是 Gbase8sValues 类注释第二条"参数内联给 DBA 看这一档发不出带值的文本"的实证。
        Assert.Equal(2, emitted.Parameters.Count);
        Assert.Equal(emitted.Text, log);
        Checks.Has("VALUES (?, ?)", log);
        Assert.True(Provider.Values.TryInlineLiteral("客户甲", null, out var inlined));
        Assert.Equal("'客户甲'", inlined);
        Assert.True(Provider.Values.TryInlineLiteral(@"C:\temp", null, out var path));
        Assert.Equal(@"'C:\temp'", path);
        Checks.Has("INSERT INTO \"QuickSqlOrder\"", log);
    }

    // ── 写目标与写侧：与达梦/虚谷同边，而这一家的理由是"没有那个子句" ──

    [Fact]
    public void The_write_target_qualifies_itself_with_the_table_name_and_keeps_its_case()
    {
        var update = orm().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql();

        // 目标不起别名 ⇒ 在 SET 与 WHERE 里用自己的表名引用自己（第三家同形，来路三条各不同：
        // Oracle 血统靠文档、虚谷靠产物、本家靠"手册的 UPDATE 那一页没出现过目标关联名"）。
        Checks.Has("UPDATE \"QuickSqlOrder\" SET \"Amount\" = \"QuickSqlOrder\".\"Amount\" + ? WHERE \"QuickSqlOrder\".\"Id\" = ?", update);
        Checks.Absent("AS \"QuickSqlOrder\"", update);
        Checks.Absent("\"QuickSqlOrder\" \"QuickSqlOrder\"", update);
    }

    [Fact]
    public void A_delete_shares_the_one_slot_that_the_update_uses()
    {
        var delete = orm().Delete<Order>().Where(o => o.Id == 7).ToSql();

        // EmitDeleteTarget 默认转给 EmitUpdateTarget，所以这一条与上面那条一起红才算那一口动了。
        Checks.Has("DELETE FROM \"QuickSqlOrder\" WHERE \"QuickSqlOrder\".\"Id\" = ?", delete);
        Checks.Absent("USING", delete);
    }

    [Fact]
    public void The_sixth_family_says_no_to_joined_writes_for_the_fourth_kind_of_reason()
    {
        // 四家长到五家（§12.5 的合并档在这一格只剩 T-SQL 与 PG 血统两族发得出），而本家改的是<em>缺法的种数</em>：
        // Oracle 血统"目标不能起别名"、MySQL 血统"能起别名但没有那个子句"、虚谷"两样都没被证过"、
        // 本家"压根没有多表更新的子句，却有 WHERE EXISTS 那条等价路"。
        Assert.False(Provider.Dialect.SupportsJoinedWrites);

        Assert.True(SqlServer.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.True(Kingbase.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.False(Dameng.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.False(MySql.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.False(Xugu.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
    }

    [Fact]
    public void A_hand_built_merged_update_names_the_equivalent_path_it_does_have()
    {
        var failure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount)));

        Checks.Has("不接受目标表之外的表源", failure.Message);
        Checks.Has("SupportsJoinedWrites", failure.Message);

        // 与前两家各反向一档：MySQL 血统那一条不许出现 MERGE INTO（它有而形状不对），虚谷那一条点了 MERGE INTO，
        // 本家点的是 UPDATE … WHERE EXISTS —— 那是这一家<em>唯一</em>能到同一目的地的形状，
        // 而共用层不发它。"抛"的文案里写这一句，是因为现场读到这条错的人下一步就问"那这一家怎么写"。
        Checks.Has("WHERE EXISTS", failure.Message);
        Checks.Absent("MERGE INTO", failure.Message);
    }

    [Fact]
    public void A_delete_with_joined_sources_refuses_on_the_same_gate_as_the_update()
    {
        var order = orm().Schemas.Contract<Order>();
        var item = orm().Schemas.Contract<OrderItem>();

        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(order.Table), order.Table, order);
        var details = new TableRef(1, "t1", item.Table);

        var id = order.Columns.Single(c => c.MemberName == nameof(Order.Id));
        var parentOrderId = item.Columns.Single(c => c.MemberName == nameof(OrderItem.OrderId));

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

        var failure = Checks.Raised<QuickSqlException>(() => EmitDelete(ast).Text);

        Checks.Has("不接受目标表之外的表源", failure.Message);
        Assert.Equal(1, Checks.Occurrences(failure.Message, "1 个"));
    }

    // ── 变更守卫与并发列：三段式的第四种"为什么" ──────────────────────

    [Fact]
    public void A_not_null_column_still_needs_only_the_inequality()
    {
        var sql = GuardText(nameof(Order.Amount));

        // 六家在这一格是五家同形（只有金仓写 IS DISTINCT FROM 那一支），本家与其中三家逐字相同而
        // 与达梦/虚谷差一处折大小写——所以这一句的"同形"读起来只到形状一层。
        Checks.Has("\"QuickSqlOrder\".\"Amount\" <> \"t1\".\"Amount\"", sql);
    }

    [Fact]
    public void A_nullable_column_takes_the_three_way_shape_as_the_fourth_distinct_reason()
    {
        var sql = GuardText(nameof(Order.PaidAt));

        Verify("update-guard-three-way", sql);

        // 三段式在这一家是<em>第五次</em>落地（调用点 5 ∶ 1），而本家的理由与前四次都不同条：
        // 前四家至少各有一条正面理由（布尔不是一等值 / 候选都要按版本发 / 两档出处都没有那一形），
        // 本家只有<em>反面</em>——既没有 IS DISTINCT FROM 也没有 <=>，而"布尔是不是一等值"这一问本次压根没问过。
        Checks.Has("\"QuickSqlOrder\".\"PaidAt\" <> \"t1\".\"PaidAt\" OR (\"QuickSqlOrder\".\"PaidAt\" IS NULL AND \"t1\".\"PaidAt\" IS NOT NULL) OR (\"QuickSqlOrder\".\"PaidAt\" IS NOT NULL AND \"t1\".\"PaidAt\" IS NULL)", sql);
        Checks.Absent("IS DISTINCT FROM", sql);
        Checks.Absent("<=>", sql);
        Assert.Equal(2, Checks.Occurrences(sql, " OR "));
    }

    [Fact]
    public void The_blacklist_here_is_reachable_by_the_default_mapping_and_is_a_limit_not_a_guardrail()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        // 无宽度字符串在这一家落 CLOB（不是 TEXT：这一家的 VARCHAR 上限是服务端配置而不是常数，
        // 所以"没说过宽度"只能由无上限那一档接住）。于是本家是"这一根 string 进不进得了守卫"
        // 那一问的第二家答案关着它——上一家关它的是 Oracle 血统，而那一家有 TEXT 这一档、这一家把 TEXT 当别名收。
        Assert.Equal("CLOB", Provider.Types.GetStoreType(note));
        Assert.False(Provider.ChangeGuard.Compares(note));
        Assert.True(Provider.ChangeGuard.Compares(contract.Columns.Single(c => c.MemberName == nameof(Order.Amount))));

        // 三颗黑名单：clob / blob / byte。前两颗是本家默认路径产得出来的（string 无宽度、byte[]），
        // 第三颗只有人手写 [Column(TypeName = "byte")] 才走到。
        Assert.False(Provider.ChangeGuard.Compares(note with { ClrType = typeof(byte[]), StoreTypeName = null }));
        Assert.False(Provider.ChangeGuard.Compares(note with { StoreTypeName = "BLOB" }));
        Assert.False(Provider.ChangeGuard.Compares(note with { StoreTypeName = "BYTE" }));
        Assert.True(Provider.ChangeGuard.Compares(note with { StoreTypeName = "VARCHAR(256)" }));

        // 带限定词与带空格都要落到基名上：DECIMAL(18,4) 带括号，"CLOB 128" 那种手写带法带的是空格。
        Assert.True(Provider.ChangeGuard.Compares(note with { StoreTypeName = "DECIMAL(18,4) NULL" }));

        // 生产后果今天仍然是零，而且是<em>第六家</em>如此：Mergeable 先问 SupportsJoinedWrites
        // （EntityTracker.cs:860），逐列问 Compares 排在它后面（:869）。
        Assert.False(Provider.Dialect.SupportsJoinedWrites);
    }

    [Fact]
    public void The_concurrency_predicate_is_a_plain_equality_because_this_family_has_no_server_side_counter()
    {
        var sql = ConcurrencyText(nameof(Order.Amount));

        // 模式取 TimestampColumn 而值由应用给：本家是"库给值"那一档在这一格<em>整族消失</em>的第一家
        // —— 既没有 rowversion 那种自动列，也没有 ON UPDATE CURRENT_TIMESTAMP 那种列尾子句【文档·IBM】。
        // 于是分布从 1 家库给值 ∶ 4 家应用给值 长成 0 ∶ 5 的一种新读法（T-SQL 那一家的 rowversion
        // 不再是任何一族的默认，而本家是把这一问的前件拿掉了）。
        Assert.Equal(ConcurrencyMode.TimestampColumn, Provider.Concurrency.Mode);
        Checks.Has("\"Amount\" = ?", sql);
        Checks.Absent("ROWVERSION", sql);
        Checks.Absent("IS NULL", sql);

        // 可空那一档与前五家同一条：`列 = ?` 在 NULL 上是 UNKNOWN，所以并发列在这里必须落在非空列上
        // 或者由应用给值——共用层那一条"NULL 折成 IS NULL"的形状属于守卫，不属于并发谓词。
        Assert.DoesNotContain(" OR ", sql, StringComparison.Ordinal);
    }

    // ── 主键回填：两档回填各缺一半，而缺的是绑定的方向而不是语法 ──────

    [Fact]
    public void The_key_comes_back_on_a_session_function_appended_to_the_same_command()
    {
        var plan = orm().Insert(row(1)).ExplainKeys();
        var sql = plan.ToSql();

        Verify("insert-with-key-readback", sql);

        // 这一家把"库里刚给了我什么"放在一颗会话函数里，而不是放在语句的回包里——所以回填句与 INSERT
        // 必须在同一条命令文本上（会话级函数只在此刻问得着刚插的那一颗）。
        // <b>而"一条命令放两句"在本家收不收没有任何一档材料证过</b>，这一句因此是本文件里形状最硬、
        // 而前提最薄的一条；它带的位是 SupportsPostStatementKeyRead，不带的位是 MultiStatement。
        Checks.Has(" ; SELECT DBINFO('sqlca.sqlerrd1') FROM systables WHERE tabid = 1", sql);
        Checks.Absent("RETURNING", sql);
        Checks.Absent("OUTPUT", sql);
        Checks.Absent("SCOPE_IDENTITY", sql);
        Checks.Has("退到 L5 逐条", plan.Reason);
        Checks.Has("语句后回读", plan.Reason);
    }

    [Fact]
    public void A_sixty_four_bit_key_reads_the_other_session_function_because_the_first_slot_is_thirty_two_bits()
    {
        var token = orm().Schemas.Contract<SessionToken>();
        var wide = token.Columns.Single(c => c.MemberName == nameof(SessionToken.Id)) with { ClrType = typeof(long) };

        var sql = Statement(w => Provider.Keys.EmitPostStatementKeyRead(w, wide));

        // sqlca.sqlerrd1 那一槽就是 Informix 的 sqlca 结构里那个 long：32 位。
        // 拿它读 BIGSERIAL 生成的 64 位序列值不会报错，只会<em>静默丢高位</em>——
        // 本层唯一一处"两式都合法而选错不红"的格，所以判据按 CLR 型分两支，而不是按型名。
        Checks.Has("DBINFO('serial8')", sql);
        Checks.Absent("sqlerrd1", sql);

        var narrow = Statement(w => Provider.Keys.EmitPostStatementKeyRead(w,
            orm().Schemas.Contract<Order>().Columns.Single(c => c.MemberName == nameof(Order.Id))));

        Checks.Has("DBINFO('sqlca.sqlerrd1')", narrow);
    }

    [Fact]
    public void A_multi_row_insert_with_keys_degrades_to_one_row_per_statement_each_reading_back_its_own_key()
    {
        var plan = orm().InsertRange([row(1), row(2)]).ExplainKeys();

        Assert.Equal(2, plan.Chunks.Count);

        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            var chunk = plan.Chunks[i];

            Assert.Equal(1, chunk.RowCount);
            Assert.Equal(i, chunk.FirstRow);
            Checks.Has(" ; SELECT DBINFO('sqlca.sqlerrd1')", chunk.Sql.Text);
        }
    }

    [Fact]
    public void Asking_for_a_key_through_returning_refuses_and_names_the_binding_slot_that_is_missing()
    {
        var contract = orm().Schemas.Contract<Order>();
        var key = contract.PrimaryKey[0];

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.Keys.EmitReturning(w, [key])));

        // 这一家<em>有</em> RETURNING 这个词【文档·IBM】，只在 ESQL/SPL 里、且必须配 INTO 宿主变量。
        // 所以文案不许说"这一家没有 RETURNING"——说了，现场就会去抄那一形并以为自己找到了路。
        Checks.Has("INTO", failure.Message);
        Checks.Has("ESQL", failure.Message);
        Checks.Has("方向位", failure.Message);
        Checks.Has("SupportsPostStatementKeyRead", failure.Message);
    }

    // ── 函数表：每一颗模板都要过"实参各出现一次"那条本家特有的自查 ──

    [Fact]
    public void Date_arithmetic_takes_the_fourth_form_and_the_operator_comes_before_the_unit()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Tomorrow = o.CreatedAt.AddDays(1), Next = o.CreatedAt.AddMonths(2), Ceiling = Math.Abs(o.Amount) })
            .ToSql();

        Verify("date-arithmetic", sql);

        // UNITS 是本家那一派（另五家分成 DATEADD / INTERVAL / NUMTODSINTERVAL 三派，这里是第四派，
        // 而它是四派里唯一一颗"算子在前、单位在词尾"的）。按月那一颗换 ADD_MONTHS，与 Oracle 血统同名而同格不同因。
        Checks.Has("(\"t0\".\"CreatedAt\" + ? UNITS DAY)", sql);
        Checks.Has("ADD_MONTHS(\"t0\".\"CreatedAt\", ?)", sql);
        Checks.Has("ABS(\"t0\".\"Amount\")", sql);
        Checks.Absent("DATEADD", sql);
        Checks.Absent("NUMTODSINTERVAL", sql);
        Checks.Absent("INTERVAL '", sql);
    }

    [Fact]
    public void A_template_that_uses_one_argument_twice_cannot_be_registered_here()
    {
        var query = orm().Select<Order>();

        // str.blank 在另五家是 ({0} IS NULL OR TRIM({0}) = '')——同一颗实参用两次。
        // 带名字的方言里那是免费的（按名回填），带 ? 的方言里它就是"文本两颗、表里一颗"，值绑到隔壁列。
        // 所以本家<em>不注册</em>它，这一句交 UntranslatableExpressionException。
        // <b>这一格是"能力缺在形状而不在库"在本家的第一个实例</b>：库里 TRIM 什么都有，缺的是 SPI 里
        // 那颗"按文本序把同一颗参数登记两次"的口子（§14）。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => query.Where(o => string.IsNullOrWhiteSpace(o.Note)).ToSql());

        Checks.Has("str.blank", failure.Message);

        // 消息里点的是<em>键</em>而不是 CLR 那一句方法名，所以现场要自己多走一步：
        // "str.blank/1 未登记" ⇐ `string.IsNullOrWhiteSpace(x)`。这一步在另五家不需要（那一臂发得出文本），
        // 而它正是这一格该记 §14 的理由之一：能力缺口的<em>可诊断性</em>也算缺口的一部分。

        // 同一颗判据的第二格：IndexOf 也不注册，而它缺的不是实参次序而是<em>函数本身</em>——
        // INSTR 不是 Informix 的内建函数（那是 Oracle 血统那一支），MATCHES 找得到匹配而给不出位置。
        Assert.False(Provider.Expressions.TryGetFunction("str.indexOf", out _));
    }

    [Fact]
    public void String_and_aggregate_functions_share_the_three_family_shape_and_concat_uses_the_operator()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Length > 3 && (o.CustomerName + o.Note).Length > 4)
            .ToSql();

        Verify("string-functions", sql);

        Checks.Has("LENGTH(\"t0\".\"CustomerName\") > ?", sql);

        // || 与 Oracle 血统、PG 血统同形，而这一格在六家里是三派（MySQL 血统报 CONCAT 是另一派）；
        // ConcatOperator 在 src/ 里零消费者，所以这一句钉的是模板 Fn.Concat/2 的产出，不是那颗字段。
        Checks.Has("||", sql);
        Checks.Absent("CONCAT(", sql);

        // 与 PG 血统同一条尾巴（SUBSTR 三参形 + 0 基→1 基的换算）而与前两家不同。
        Assert.Equal("||", Provider.Expressions.ConcatOperator);
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);
        Assert.False(Provider.Expressions.SupportsRegex);
    }

    [Fact]
    public void Like_escapes_with_a_backslash_because_the_shared_layer_always_names_the_escape_character()
    {
        var sql = orm().Select<Order>().Where(o => o.CustomerName!.Contains("50%_off")).ToSql(out var parameters);

        Assert.Equal("%50\\%\\_off%", parameters[0].Value);
        Checks.Has("LIKE ? ESCAPE '\\'", sql);
    }

    [Fact]
    public void The_current_timestamp_is_a_keyword_with_a_field_range_not_a_function()
    {
        // 读模板而不是读产出：`DateTime.Now` 在表达式树里是一颗无输入的常量式成员访问，
        // 翻译器今天把它折成参数而不是折成 date.now/0（六家同一格，所以这一颗在离线侧从来没有产出见证）。
        // 于是本家的这一行只能钉"注册的形状对不对"，钉不了"它发出去长什么样"。
        Assert.True(Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.Now, 0), out var now));

        // CURRENT 不带括号，而共用层会把它拼进算术里 ⇒ 模板自己加括号的理由写在 Gbase8sExpressions 那一行；
        // 带 YEAR TO SECOND 是因为不带字段范围的 CURRENT 折到 DATETIME 那一族上是另一档写法。
        Checks.Has("(CURRENT YEAR TO SECOND)", now!.Template);
        Checks.Absent("SYSDATE", now.Template);
        Checks.Absent("GETDATE", now.Template);

        // 另一颗会话级取值函数有出处而形状不对（交回整数秒而不是时刻值），所以整臂不注册。
        Assert.False(Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.UtcNow, 0), out _));
    }

    // ── CodeFirst：自增在型名里、约束名在括号后、注释那一格发不出去 ──

    [Fact]
    public void Create_table_puts_the_identity_in_the_type_name_and_the_constraint_name_after_the_key()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Verify("ddl-order", sql);

        Checks.Has("CREATE TABLE \"QuickSqlOrder\" (", sql);

        // SERIAL 是<em>型名</em>不是列尾子句 ⇒ 换名这一步在 DDL 层（Gbase8sCodeFirst.SerialFor）而不是类型层，
        // 理由是共用层先定型后判自增；那一格本次由这份样本第一次生成时抓到（它当时写的是 "Id" INTEGER NOT NULL），
        // 改判与它带出的那条 SPI 读数一起记 §13.5。
        Checks.Has("\"Id\" SERIAL NOT NULL", sql);

        // 64 位那一档同一条换名、另一颗型名（BIGINT → BIGSERIAL），而起点/步长那一格仍然无处发。
        var contract = orm().Schemas.Contract<Order>();

        Checks.Has("\"Id\" BIGSERIAL NOT NULL", Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table,
            contract.Columns.Single(c => c.MemberName == nameof(Order.Id)) with { ClrType = typeof(long), StoreTypeName = null })));
        Checks.Has("\"CustomerName\" VARCHAR(256) NULL", sql);
        Checks.Has("\"Amount\" DECIMAL(18,4) NOT NULL", sql);
        Checks.Has("\"State\" INTEGER NOT NULL", sql);
        Checks.Has("\"CreatedAt\" DATETIME YEAR TO FRACTION(3) NOT NULL", sql);
        Checks.Has("\"Note\" CLOB NULL", sql);

        // 词序与另五家<em>相反</em>：CONSTRAINT 跟在括号后面。只差一个词序，而那一个词序在库里是语法错。
        Checks.Has("PRIMARY KEY (\"Id\") CONSTRAINT \"PK_QuickSqlOrder\"", sql);
        Checks.Absent("CONSTRAINT \"PK_QuickSqlOrder\" PRIMARY KEY", sql);

        // 另五家的自增写法与它们的型名不许漏进来。
        Checks.Absent("IDENTITY", sql);
        Checks.Absent("AUTO_INCREMENT", sql);
        Checks.Absent("GENERATED", sql);
        Checks.Absent("NVARCHAR", sql);
        Checks.Absent("TEXT", sql);
    }

    [Fact]
    public void The_declared_length_is_multiplied_into_the_ddl_because_the_column_measures_bytes()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        // 六份预设里第一颗恒 Byte 的量纲（分布从「四 Char ∶ 一分档」变成 四 ∶ 一 ∶ 一）。
        // 更要紧的半格：在另五家那把 ×4 尺子只影响 FitsBytes 的松紧，在本家它<em>写进建表文本</em>。
        var stored = Provider.Types.ApplyStorageDefaults(name);

        Assert.Equal(LengthSemantics.Byte, Provider.Types.GetLengthSemantics(stored));
        Assert.Equal(LengthSemantics.Byte, Provider.Types.GetLengthSemantics(note));
        Assert.Equal(256, stored.MaxBytes);
        Assert.False(stored.Unicode);

        // 闸门与 DDL 必须同源：64 字符 ⇒ VARCHAR(256)，而 MaxBytes 也是 256（同一个数，差 4 倍就是 §10.4 立起来要挡的第一号错）。
        Assert.Equal("VARCHAR(256)", Provider.Types.GetStoreType(name));
        Assert.True(stored.FitsBytes(new string('界', 64)));

        // 超过宽度那一档不写一颗数：这一家的 VARCHAR 上限由服务端配置（ONCONFIG 的 SQL_VARCHAR）决定，
        // 所以"再宽一点"在这一家不是一句 DDL 而是一次现场赌注。不写比写错一档诚实，代价记 §14。
        Assert.Equal("CLOB", Provider.Types.GetStoreType(note with { MaxChars = null, StoreTypeName = null }));
        Assert.Null(Provider.Types.ApplyStorageDefaults(note).MaxBytes);
    }

    [Fact]
    public void Two_names_the_library_gives_and_this_provider_still_picks_the_ansi_one()
    {
        var token = orm().Schemas.Contract<SessionToken>();
        var order = orm().Schemas.Contract<Order>();

        // Guid：与达梦、MySQL 血统同一答案，而这一家<em>连取号函数那一族都没有</em>【文档·IBM】——
        // 所以"取文本那一支"在这里不是取舍而是唯一，Guid_Raw16 那一颗是"另一支候选压根不存在"的第四种情形。
        Assert.Equal("CHAR(36)", Provider.Types.GetStoreType(token.Columns.Single(c => c.MemberName == nameof(SessionToken.Id))));

        // FLOAT 在这一家是 8 字节那一颗，4 字节的叫 SMALLFLOAT——与另五家"FLOAT 跟 double、REAL 才 4 字节"错开一格。
        // 这一格是照抄型名会得到<em>错宽度</em>的唯一一处：不报错，只每行多 4 字节而没人发现。
        Assert.Equal("FLOAT", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.Amount)) with { ClrType = typeof(double), StoreTypeName = null }));
        Assert.Equal("SMALLFLOAT", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.Amount)) with { ClrType = typeof(float), StoreTypeName = null }));
        // 两颗 ANSI 名（BIGINT / BIGSERIAL）与两颗 Informix 别名（int8 / serial8）在这一家是同义字，本文件取 ANSI；
        // 而"自增把 INTEGER 换成 SERIAL"那一步在 DDL 侧（Gbase8sCodeFirst.SerialFor），<b>不在这里</b>——
        // 理由是共用层先定型后判自增，类型层读不到那颗结论（那一格整颗记 §13.5 的改判台账）。
        Assert.Equal("BIGINT", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.Id)) with { ClrType = typeof(long), StoreTypeName = null }));

        // 库里 BOOLEAN 那一型在两档版本里读法不一致（'T'/'F' 与 TRUE/FALSE 两案），所以 CLR bool 落 INTEGER：
        // 三条判据合成一条"位只跟存储型走"，而它是 v0.35 第一笔那条规则第一次由新落地的家<em>主动套用</em>。
        Assert.Equal("INTEGER", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.State))));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Boolean_Type));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Guid_Raw16));
    }

    [Fact]
    public void A_column_type_this_family_cannot_name_is_refused_without_borrowing_another_family_s_reason()
    {
        var order = orm().Schemas.Contract<Order>();
        var amount = order.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        // 同一个 CLR 型、三种不可表达，而三家的缺法三条各不同（虚谷：库有而驱动不认；MySQL 血统：那一型存会话偏移；
        // 本家：库里压根没有第三型）。所以本家的文案<em>不许</em>替另两家说话——把它们的理由抄过来，
        // 现场就会顺着那条路去查驱动或去建带时区的那一型，而两条尽头都没有东西。
        // 这一格是"抄来源时连它的理由一起抄"那条反例的正面防守。
        var failure = Checks.Raised<QuickSqlException>(() => Provider.Types.GetStoreType(amount with { ClrType = typeof(DateTimeOffset), StoreTypeName = null }));

        Checks.Has("压根没有第三型", failure.Message);

        // 与虚谷那一条<em>反向</em>钉：那一家的文案里出现了 "TIMESTAMP WITH TIME ZONE"（库有那一型而驱动不认），
        // 本家不许出现它——出现了，现场就会去建那一型并以为自己找到了退路。
        Checks.Absent("WITH TIME ZONE", failure.Message);
        Checks.Has("DateTimeOffset", failure.Message);

        var unregistered = Checks.Raised<QuickSqlException>(() => Provider.Types.GetStoreType(amount with { ClrType = typeof(Uri), StoreTypeName = null }));

        Checks.Has("未登记 CLR 类型", unregistered.Message);
        Checks.Has("Uri", unregistered.Message);
    }

    [Fact]
    public void A_named_schema_qualifies_the_table_as_an_owner_and_does_not_fold_it()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema().Schemas.Contract<Order>()));

        Verify("ddl-qualified", sql);

        // 限定符出来了，而<em>没有被折</em>——这一格与虚谷/达梦那两家反向（那两家发 "APP"）：
        // 本家不折，所以模型里写 app、发出去还是 "app"。而这一家在库里读到的所有者名是小写，
        // 于是"不折"在这里<em>恰好</em>对，靠的是"我们本来就按小写写"这一条巧合而不是靠折叠。
        Checks.Has("CREATE TABLE \"app\".\"QuickSqlOrder\" (", sql);
        Checks.Absent("\"APP\"", sql);
        Checks.Absent("\"informix\".", sql);

        // "模式"在这一家是<em>所有者</em>而不是一棵独立的命名空间树，所以这一句限定的是"谁的表"。
        // 与 §9.2 那句"给应用建一个模式"的语义差整格记 §14（本家发不出别家那种 CREATE SCHEMA）。
    }

    [Fact]
    public void Add_column_and_modify_column_both_wrap_their_definition_in_parentheses()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        var add = Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, note));
        var modify = Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, amount));

        Verify("ddl-alter", add + " ; " + modify);

        Checks.Has("ALTER TABLE \"QuickSqlOrder\" ADD (\"Note\" CLOB NULL)", add);
        Checks.Has("ALTER TABLE \"QuickSqlOrder\" MODIFY (\"Amount\" DECIMAL(18,4) NOT NULL)", modify);

        // 括号不是装饰：它是这一句能一次加多列的那个位置的容器，而共用层一次只给一列，
        // 所以本家发出的永远是 ADD (一个元素)。那一格形状上的多余要说出来，否则读代码的人会以为漏了什么。
        Checks.Absent("ADD COLUMN", add);
        Checks.Absent("ALTER COLUMN", modify);
        Checks.Absent("MODIFY COLUMN", modify);

        // 改列这一句把可空性一起重写一遍（MODIFY 要的是<em>完整列定义</em>）——契约里没写的格子会被这一句定下来，
        // 与虚谷那一家记过的同一类风险并排。所以 NOT NULL 必须出现在括号里，而不是留给库去默认。
    }

    [Fact]
    public void A_column_comment_has_nowhere_to_go_and_the_intersection_loses_its_first_bit_here()
    {
        var contract = orm().Schemas.Contract<Order>();
        var commented = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName)) with { Comment = "客户名" };

        var writer = NewWriter();

        // 这一家返回 false，而这是本仓库第一次：§9.2 那条"注释走独立一句"在五家是四家原生 + 一家存储过程，
        // 本家是<em>清单里压根没有 COMMENT ON 那一条</em>（列注释在 syscomments 那张目录表里，不在语句里）。
        // 返回 false 之后走 Core 那条"跳过并 WARN"的退路，而后果写在 CapabilityLedgerTests：
        // 六家交集从三位掉到两位，前七次"接一家不缩小"的实证链到这里第一次真掉。
        Assert.False(Provider.CodeFirst.TryEmitColumnComment(writer, contract.Table, commented));
        Assert.Equal(string.Empty, writer.ToSqlString());

        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Comment_On_Column));

        // 刻意不把注释文本塞进建表语句：那一形在另五家是列内的 COMMENT '…' 子句，本家没有，
        // 造一条进去会得到一句库拒的建表——比不写注释严重得多（§1.2 原则 4）。
        var ddl = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithComment().Schemas.Contract<Order>()));

        Checks.Absent("COMMENT", ddl);
    }

    [Fact]
    public void An_index_omits_the_default_direction_and_a_full_text_one_is_refused()
    {
        var contract = orm().Schemas.Contract<Order>();

        var plain = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("IX_QuickSqlOrder_Note", IndexKind.NonClustered, [nameof(Order.Note)]) { ColumnNames = ["Note"] }));

        // 只写 DESC、不写 ASC【文档·IBM】：缺省那一支本来就是升序，所以少写不改语义；
        // 而万一这一家压根不认 ASC 那颗关键字，多写的那一颗会让整句红。两案不对称，取少写的一边。
        Checks.Has("CREATE INDEX \"IX_QuickSqlOrder_Note\" ON \"QuickSqlOrder\" ( \"Note\" )", plain);
        Checks.Absent("ASC", plain);

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("FT_QuickSqlOrder_Note", IndexKind.FullText, [nameof(Order.Note)]) { ColumnNames = ["Note"] })));

        Checks.Has("FULLTEXT", failure.Message);

        // 这一家多出来的那一支（EXCLUDE 排除索引）在契约的 IndexKind 里没有对应那颗，与虚谷那句 BITMAP 同一格。
        Assert.DoesNotContain("EXCLUDE", Enum.GetNames<IndexKind>());
    }

    [Fact]
    public void A_rowversion_column_is_refused_at_the_ddl_emission_point_on_an_absence_reading()
    {
        var contract = orm().Schemas.Contract<InventoryItem>();
        var version = contract.Columns.Single(c => c.MemberName == nameof(InventoryItem.RowVersion));

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitCreateTable(w, contract)));

        // 两边都拒，而来路差两档：虚谷是"建得成、会自增、读不回"三格实测之后裁的，
        // 本家是按<em>缺席读</em>——这一族的类型清单里没有那一型。判据同一条：不建出一列看着能用的废列（§5.8）。
        // 这一句要留着，否则下一版会以为本家也有那三格读数：这里没有"建得成"那一半，收不收那一串词本次未问。
        Checks.Has("[ConcurrencyToken]", failure.Message);
        Checks.Has("缺席读", failure.Message);

        Assert.Throws<QuickSqlException>(() =>
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, version)));

        // 闸门只点名那一颗：人写的别的名字照旧原样穿过。
        var echo = Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, version with { StoreTypeName = "DECIMAL(9,2)" }));

        Checks.Has("\"RowVersion\" DECIMAL(9,2) NULL", echo);

        // 名字原样穿过而<em>不折</em>：这一家的"不折"在 DDL 侧第一次让人写下的名字与我们发出去的名字逐字相同，
        // 而折大写的两家在同一格会把 RowVersion 打成 ROWVERSION——同一颗闸门，两种可读性。
    }

    // ── 内联字面量 ────────────────────────────────────────────────────

    [Fact]
    public void The_bool_and_the_timestamp_inline_in_the_shape_the_column_type_takes()
    {
        Assert.True(Provider.Values.TryInlineLiteral(true, null, out var flag));
        Assert.Equal("1", flag);

        Assert.True(Provider.Values.TryInlineLiteral(false, null, out var unset));
        Assert.Equal("0", unset);

        // 与列型同边（bool→INTEGER、谓词→= 1），所以内联发 1/0 而不是 TRUE/FALSE。
        // 库里 BOOLEAN 在两档版本里读法不一致那一条，是这一格真正的来路（Gbase8sTypes 第一条）。
        Assert.NotEqual("TRUE", flag, StringComparer.Ordinal);

        Assert.True(Provider.Values.TryInlineLiteral(new DateTime(2026, 3, 4, 5, 6, 7, 890), null, out var moment));
        Assert.Equal("'2026-03-04 05:06:07.890'", moment);

        Assert.True(Provider.Values.TryInlineLiteral(3d, null, out var whole));
        Assert.Equal("3.0", whole);

        Assert.True(Provider.Values.TryInlineLiteral(1e-08, null, out var tiny));
        Assert.Equal("1E-08", tiny);

        Assert.False(Provider.Values.TryInlineLiteral(double.NaN, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.PositiveInfinity, null, out _));

        Assert.False(Provider.Values.TryInlineLiteral(new byte[] { 1, 15 }, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), null, out _));

        Assert.True(Provider.Values.TryInlineLiteral(Guid.Empty, null, out var id));
        Assert.Equal("'00000000-0000-0000-0000-000000000000'", id);
    }

    // ── 槽位本身：这一档声明了什么、没声明什么 ────────────────────────

    [Fact]
    public void The_family_declares_five_bits_with_the_new_one_being_the_wiring_bit()
    {
        var declared = Enum.GetValues<DbCapabilities>()
            .Where(static bit => bit != DbCapabilities.None && Provider.Capabilities.HasFlag(bit))
            .ToArray();

        // 原 4 位依旧是六家（现八本）里"库种事实"最少的一档（对 5 / 6 / 7 / 15 各家），而这条读法要修正一次：
        // 它少的那两颗里有一颗是"发了文本而枚举没位"（第四种分页形状），所以那颗 4 不是"这一族只会四件事"，
        // 是"这一族有四件事我们能拿代码指认"。§14 那一格由此立起来：位掩码从今天起不能读成"这家的分页能力的完整读数"。
        // 【本笔（ClickHouse 重开·笔一）】第 5 位是 Transactions：内检索引位（内核按位分岔），不进
        // "库种事实比多少"那一串 —— 4＋1 两颗一起数完这一格的账。
        Assert.Equal(5, declared.Length);
        Assert.Contains(DbCapabilities.Schemas, declared);
        Assert.Contains(DbCapabilities.Alter_Column_Precision, declared);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, declared);
        Assert.Contains(DbCapabilities.Guid_Char36, declared);
        Assert.Contains(DbCapabilities.Transactions, declared);

        // 三分页位一位都不报：本家的形状不在那三颗里，而枚举里没有第四颗。
        Assert.DoesNotContain(DbCapabilities.Paging_LimitOffset, declared);
        Assert.DoesNotContain(DbCapabilities.Paging_RowNum, declared);
        Assert.DoesNotContain(DbCapabilities.Paging_OffsetFetch, declared);

        // 交集在这一家掉一位（Comment_On_Column），这是那串"接一家不缩小"的实证链第一次真掉。
        Assert.DoesNotContain(DbCapabilities.Comment_On_Column, declared);
        Assert.DoesNotContain(DbCapabilities.Returning, declared);
        Assert.DoesNotContain(DbCapabilities.MultiStatement, declared);
        Assert.DoesNotContain(DbCapabilities.Identity_ExplicitInsert, declared);
    }

    [Fact]
    public void The_bulk_slot_is_the_third_one_to_answer_null_so_the_conservative_default_has_three_customers()
    {
        Assert.Null(Provider.Bulk);

        var plan = orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        // 没有能举证的参数预算数字 ⇒ 交回 null，分块器走那颗 100 的保守兜底。
        // 本家的来路与达梦、虚谷同档而多一句：手册里"数组绑定/批量装载"那一族的名字读到过，
        // 而没读到可报的<em>颗数上限</em>——IBulkStrategy.ParameterBudget 要的正是一个颗数。
        Assert.Equal(100, plan.ParameterBudget);
        Assert.Equal(7, plan.ColumnsPerRow);
        Assert.Equal(12, plan.RowsPerStatement);
        Assert.Equal(50, plan.Chunks.Count);
        Assert.Equal(600, plan.Chunks.Sum(static c => c.RowCount));

        var damengPlan = Dameng.Orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        Assert.Equal(damengPlan.Chunks.Count, plan.Chunks.Count);
        Assert.Equal(damengPlan.ParameterBudget, plan.ParameterBudget);
    }

    [Fact]
    public void The_identity_toggle_is_false_here_because_there_is_no_switch_to_open()
    {
        Assert.False(Provider.Keys.SupportsExplicitIdentityInsert);
        Assert.Null(Provider.Keys.IdentityInsertToggle(NewWriter(), new TableInfo(null, "QuickSqlOrder"), true));

        // 六家第五位 false，而本家那一票的含义与另四家不同条：那四家说的是"库里要一颗开关而我们没接"，
        // 本家的 SERIAL 压根不禁止直接写值【文档·IBM】⇒ 这件事在这一家不需要能力。
        // <b>这是本仓库第一颗"false 而理由是它用不上"的位</b>，而真正的缺口在下一格：
        // 写了显式值之后序列会不会跟着挪，本次没有读数（§14）。
        Assert.False(Provider.Keys.SupportsBulkKeyRetrieval);
        Assert.True(Provider.Keys.SupportsPostStatementKeyRead);
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    static QuickSqlOrm ormWithComment()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.CustomerName).HasComment("客户名");

        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));
    }

    static SqlWriter NewWriter(IDbProvider? provider = null)
        => new(new StringBuilder(), [], (provider ?? Provider).Dialect.ParameterPrefix);

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

    /// <summary>并发谓词的文本，与 <see cref="GuardText"/> 同一手法挂在一条最小 UPDATE 上发射
    /// （共用层在 <c>SaveChanges</c> 里拼的那一条也是这个形状，区别只在它带着主键）。
    /// 收到的实参是<em>列</em>而不是值：那颗 <c>12m</c> 是占位的原值，它出现在参数表里而不是文本里，
    /// 这一格正是本家与另五家共同的主张——<c>BuildPredicate</c> 交回的是 <c>列 = 参数</c>，不是 <c>列 = 字面量</c>。</summary>
    static string ConcurrencyText(string member)
    {
        var contract = orm().Schemas.Contract<Order>();
        var column = contract.Columns.Single(c => c.MemberName == member);

        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(amount, new ParameterValue(1m, amount))],
            Where = Provider.Concurrency.BuildPredicate(new ColumnExpression(stored, column), column, 12m),
        };

        return EmitUpdate(ast).Text;
    }

    /// <summary>守卫的文本，与前五家同一手法：这一族没有 UPDATE … FROM，合并档发不出去，
    /// 而守卫仍然要有文本见证 ⇒ 挂在一条<em>不带表源</em>的最小 UPDATE 上发射。
    ///
    /// <para>两侧的别名按 <c>EntityTracker</c> 的真实构造取：表侧走 <c>WriteTargetAlias</c>（本家是表名本身，
    /// 而且<em>不折大小写</em>，所以这段文本里读得出"本家与达梦/虚谷在这一格差一次折叠"）。
    /// 那一半"这条语句是不是 SaveChanges 会发的那一条"在本家与前四家同样读不出——它不是。</para></summary>
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

    /// <summary>手搭一份合并 UPDATE，形状与 <c>EntityTracker.WriteMergedAsync</c> 同构（五族各有一份）。
    /// <b>在这一家它唯一的用途是"抛得准不准"</b>：发射必然在 <c>EmitJoinedSources</c> 那一句上红。</summary>
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
