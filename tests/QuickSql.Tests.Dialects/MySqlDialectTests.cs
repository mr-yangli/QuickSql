using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.MySqlBase;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>MySQL 血统（第四档）的 SQL <b>文本</b>。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与前三族的离线套件同一分工：黄金样本钉版式，结构断言钉"凭什么长这样"。
///
/// <para><b>这一批样本证明的是回归，不是"MySQL 接受这条 SQL"</b>，而这一族与达梦那一族在这句话上
/// 处境相同：授权的活库只有两台 SQL Server（§13.4），本机没有一台 MySQL 血统的实例，所以这些 <c>.sql</c>
/// 是自产基线 —— 一句都没有在真正的解析器上跑过。正式记账在 §13.5 的能力出处台账（<see
/// cref="CapabilityLedgerTests"/> 里这一家逐位标着「离线已钉」，「真库跑过」那一档一颗都没有），
/// 写在这一段的目的是：读到这一行的红或绿时，不要把它当成对真库行为的断言。
/// 唯一一处比另两族乐观的说法是：清单里 <c>MySqlConnector</c> 是三行「未实测」里唯一公开、MIT 且 AOT 友好的驱动
/// （sqlserver 那一颗同样公开且 MIT，但它已有两台活库、不在"未实测"那三行内 —— 本条上一版写作"四行里唯一"，
/// 交账时按清单自己第一行推翻），所以<em>第一次</em>真库实测大概会落在这一家 —— 那一天这些样本会第一批被复核，而不是被推翻。</para>
///
/// <para>样本按<b>形状</b>选而不是按链式方法数（§13.3）。这一族的目录比另两家小（11 份），原因是它<em>独占</em>
/// 的形状比看起来少：分页与写目标那两格它站在金仓那一边（只差引号字形），谓词那两格它站在 T-SQL/达梦那一边。
/// 于是这一族真正独有的只有：<b>反引号</b>、<b>OFFSET 不能单独出现</b>、<b>LIKE 的转义符换了字符</b>、
/// <b>注释与改精度共用一句 <c>MODIFY COLUMN</c></b>、<b>全文索引不抛</b>、<b>表选项 <c>ENGINE</c>/<c>CHARSET</c></b>、
/// <b>三段式守卫在这一家是三个候选里唯一不看版本的那一形</b>。同形的部分交给 <see cref="DialectParityTests"/> 逐字对读。</para>
///
/// <para>这个类只构造<b>叶</b>（<see cref="Provider"/>），而<em>不是</em>像达梦那一份那样到处用血统基类：
/// 这一族的叶不覆写任何一格，所以"钉叶"就是"钉族"，把基类实例留着只服务最后那一条<em>空壳证据</em>
/// （<see cref="The_leaf_overrides_nothing_so_its_entire_output_is_the_family_s"/>）。达梦反过来是因为它的叶
/// 真的差一行（自增写法），那份差异需要两份产出对照才量得出来。</para></summary>
public sealed class MySqlDialectTests
{
    static readonly IDbProvider Provider = MySql.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>血统基类的可实例化形态。<see cref="MySqlProviderBase"/> 今天是 <c>abstract</c>（没有一种库种
    /// 叫"基类"），而"这一档还没有叶子覆写者"这一主张需要一个基类实例才量得出来。
    /// <b><see cref="DbKind"/> 取 <see cref="DbKind.MySql"/> 而不是达梦那一份的 <c>DbKind.Oracle</c></b>：
    /// 探针要和叶逐字同形才有意义，而 Kind 会进 <c>EmitJoinedSources</c> 的异常文案 —— 换一个库种名就换一份文本，
    /// 那条相等断言会红在它自己的构造上。
    /// <b>【v0.37 第三笔】"达梦那一份的 <c>DbKind.Oracle</c>"这一称呼今天有了另一家</b>：那颗枚举值不再是
    /// 一颗"有枚举值而没有 provider"的悬空值（<c>OracleDialectTests</c> 的本尊叶就取它），
    /// 而上面那句"换一个库种名就换一份文本"因此第一次有一对反向见证——同一颗 Kind 之下探针与真叶子
    /// 逐字同形，分岔不在文本而在 CLR 类型名。</summary>
    static readonly IDbProvider Family = new MySqlFamilyProbe(DbKind.MySql, SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名。理由与金仓、达梦那两份同一条：四族按形状起的名字是同一批，
    /// 不带目录参数就会改写别家的基线。</summary>
    const string Leaf = "mysql";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Leaf);

    static QuickSqlOrm orm() => MySql.Orm();

    /// <summary>带模式的模型：只有 <see cref="A_named_schema_qualifies_the_table_and_keeps_its_case"/> 需要它。
    /// 与 <c>ChangeGuardTests</c>、<c>DamengDialectTests</c> 同一手法（Fluent 覆写而不是新建实体）：G1 只认
    /// <c>[Table]</c>，新加一个实体会把四家的对拍账牵连进来，而这一格要的只是"契约上写了一个模式名"这一个事实。</summary>
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

    // ── 标识符：第四种引号字符，而且不折叠 ────────────────────────────

    [Fact]
    public void Every_identifier_is_back_quoted_and_keeps_the_case_the_model_wrote()
    {
        var sql = orm().Select<Order>().ToSql();

        Verify("select-entity", sql);

        // 四家里只有这一家不用方括号也不用双引号。而"一律加引号"在这里的理由与 T-SQL 同一格而更强：
        // 保留字表最长，而业务表名恰恰就叫 ORDER、GROUP 那一批。
        Checks.Has("FROM `QuickSqlOrder` AS `t0`", sql);
        Checks.Has("SELECT `t0`.`Id`, `t0`.`CustomerName`", sql);
        Checks.Absent("[", sql);
        Checks.Absent("`QUICKSQLORDER`", sql);

        // 双引号一颗都不许出现 —— 这一条不是版式洁癖：<c>sql_mode</c> 没开 <c>ANSI_QUOTES</c> 时（默认就没开），
        // 双引号在这一家是<b>字符串的字面量引号</b>，于是 "t0"."Id" 会被读成两个常量加一个列引用。
        // 反引号在两种模式下都只是标识符引号，这是这一家与 PG 系在同一个 SQL 标准档位上仍然分道的那颗开关。
        Checks.Absent("\"", sql);
    }

    // ── 分页：与金仓同一套尾巴，但差一颗哨兵 ─────────────────────────

    [Fact]
    public void Limit_and_offset_are_two_tail_clauses_and_the_row_count_comes_first()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var parameters);

        // 刻意不生成基线：这一句与 golden/kingbase/paging-order-skip-take.sql 只差引号字形（同一套尾巴），
        // 按 §13.3 那句话它属于"同形交给对拍"的那一批。下面那两条 Has/Absent 量的是这一家自己的两件事：
        // 子句序（LIMIT 在前，所以 @p0 是 5）与"没有上限时的那颗哨兵不许漏到这一形里"。
        Assert.Equal([5, 10], parameters.Select(p => (int) p.Value!).ToArray());
        Checks.Has("ORDER BY `t0`.`Id` ASC LIMIT @p0 OFFSET @p1", sql);
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWNUM", sql);
        Checks.Absent("18446744073709551615", sql);

        // 无 ORDER BY 的分页也不钉：那一格这一家用的是 DialectBase.PagingRequiresOrdering 的默认值，
        // 也就是"本家族没有写过这一格" —— 金仓那一份样本已经替四家量过那个默认值了。
    }

    [Fact]
    public void Offset_cannot_stand_alone_so_the_missing_limit_becomes_a_written_out_sentinel()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql(out var parameters);

        Verify("paging-skip-only", sql);

        // 与金仓恰好相反：那一家 OFFSET 可以单独出现（它的 paging-without-ordering 样本就以 OFFSET 收尾），
        // 这一家语法上 LIMIT 必须在前。四家里唯一一处"方言自己造一个常量补语法"就是这一格。
        Assert.Single(parameters);
        Assert.Equal(30, parameters[0].Value);
        Checks.Has("LIMIT 18446744073709551615 OFFSET @p0", sql);

        // 哨兵写成文本而不是参数：它不是一格数据，是"没有上限"的另一种写法。多一颗参数会当场显形在这里，
        // 而它真正的代价在别处 —— 参数预算（§12.3 的分块公式）量的是真值颗数，混进来会少算一行。
        Checks.Absent("@p1", sql);
    }

    // ── 谓词形状：这一家把两处都推回 T-SQL 那一形 ─────────────────────

    [Fact]
    public void The_shared_predicate_text_needs_no_override_because_true_and_false_are_just_1_and_0()
    {
        var none = Array.Empty<int>();

        var empty = orm().Select<Order>().Where(o => none.Contains(o.Id)).ToSql();

        // 四家在这一格的分布从"两形"变成 3∶1：T-SQL、达梦、这一家都用基类的 1 = 0，只有金仓必须写 false
        // （那一家 WHERE 要的是布尔值，而 false 在那儿是一等值）。这一家确实有 TRUE/FALSE 字面量，
        // 但它们是 1/0 的<em>同义词</em>而不是另一种型，所以覆写它得不到任何东西。
        Checks.Has("WHERE 1 = 0", empty);
        Checks.Absent("WHERE false", empty);

        var paid = true;

        var flag = orm().Select<Order>().Where(o => paid && o.Amount > 1m).ToSql(out var parameters);

        Assert.Equal(2, parameters.Count);

        // 与金仓那一族是<em>反向</em>的两件事，而对拍读不出谁对：那一家 @p0 是 boolean，= 1 那一侧是 integer，
        // 直接类型错误；这一家列是 TINYINT(1)，IS TRUE 存在但把 2 判真。不覆写 EmitBooleanEquality 的理由
        // 在 MySqlDialect 类注释第五条：这一族自己写进去的值只可能是 1/0，而 §10.5 那次事故（可空列上 = 0
        // 匹配不到 NULL）换成 IS FALSE 一样匹配不到 —— 换写法换不掉语义。
        Checks.Has("@p0 = 1 AND `t0`.`Amount` > @p1", flag);
        Checks.Absent("IS TRUE", flag);
    }

    // ── 写目标：站在金仓那一边（第二种形状，不是第四种）────────────────

    [Fact]
    public void The_write_target_takes_an_alias_so_the_ansi_form_of_the_shared_layer_is_legal_here()
    {
        var update = orm().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql();

        // 四家在写目标这一格是 2∶2：T-SQL 把别名挪进第二个 FROM，达梦的别名只能是表名本身，
        // 金仓与这一家都吃共用层那句 ANSI 写法（WriteTargetAlias 交回 t0、EmitUpdateTarget 不覆写）。
        // 所以这里<em>不生成基线</em>，交给 DialectParityTests 的 金仓↔这一家 那一格逐字对读。
        Checks.Has("UPDATE `QuickSqlOrder` AS `t0` SET `Amount` = `t0`.`Amount` + @p0 WHERE `t0`.`Id` = @p1", update);
        Checks.Absent("`QUICKSQLORDER`", update);

        var delete = orm().Delete<Order>().Where(o => o.Id == 7).ToSql();

        Checks.Has("DELETE FROM `QuickSqlOrder` AS `t0` WHERE `t0`.`Id` = @p0", delete);
    }

    [Fact]
    public void A_global_filter_prefixes_the_alias_because_the_write_target_has_one_here()
    {
        var fresh = orm();

        fresh.Filters.Add<Ticket>(t => !t.Deleted);

        var sql = fresh.Delete<Ticket>().Where(t => t.Id == 7).ToSql();

        // 这一条是达梦那一份 filter-delete-self-qualified 的<em>反向</em>样本：那里 §10.7 的注入必须接上
        // "别名就是表名"这一格，这里必须接上"别名就是 t0"。两家用的是同一只 TableRef，
        // 所以这一条红的形状与那一条一样是"前缀换了"，只是换的方向相反。
        Checks.Has("DELETE FROM `QuickSqlTicket` AS `t0` WHERE `t0`.`Id` = @p0 AND NOT `t0`.`Deleted` = 1", sql);
        Checks.Absent("`QUICKSQLTICKET`.", sql);
    }

    // ── 主键回填：v0.34 起这一家接上了"语句后回读"那第二个口子 ─────────
    //
    // 这一节从前是一条"抛"的用例（v0.30 立），钉的是"两个口子里这一家只有一个、且那个没有槽位"。
    // v0.34 第一笔把 §14 那格 <c>IKeyStrategy</c> 的第二个口子建起来了，这一家从今天起走
    // "逐条 + 同句命令回读"；而它的凭据与第五家不同：<b>本机没有授权 MySQL 实例</b>，
    // 这一形的全部依据是离线文本 + 厂商文档（逐条出处写在 MySqlKeys.SupportsPostStatementKeyRead 注释里），
    // 所以这里的断言钉的是"我们发的是哪一形"，真库对账那一栏在 §13.5 仍然空着。

    [Fact]
    public void Asking_for_the_generated_key_appends_a_post_statement_read_in_the_same_command()
    {
        // §12.4 第 1 项的 L3→L5 在这一家从今天起走完全程：退到逐条之后，每一块的句尾接一条回读句，
        // 键在同一句命令里带回。"同一条命令"是形状的全部理由：LAST_INSERT_ID() 是会话级函数，
        // 拆开两次执行就出了会话保证（而 INSERT 一次一行的形状恰好也绕开了锁模式那一格——
        // 连续性推断根本不需要发生，代价与理由都明写在 Reason 里）。
        var plan = orm().Insert(row(1)).ExplainKeys();
        var sql = plan.ToSql();

        Checks.Has(" ; SELECT LAST_INSERT_ID()", sql);
        Checks.Has("VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)", sql);
        Assert.EndsWith("SELECT LAST_INSERT_ID()", sql, StringComparison.Ordinal);

        // 这一家从来发不出 OUTPUT/RETURNING 当结果集（旧用例钉的就是那一句"抛"），反向钉它仍不出现。
        Checks.Absent("RETURNING", sql);
        Checks.Absent("OUTPUT", sql);

        Checks.Has("退到 L5 逐条", plan.Reason);
        Checks.Has("语句后回读", plan.Reason);

        // 反面那一样：不要键的 INSERT 正常发得出，而且不带回读句。它的文本与另三家只差引号，
        // 所以不成基线（那一份逐字对读在 DialectParityTests 的 INSERT 那一格里，四家现在同链各跑一遍）。
        var plain = orm().Insert(row(1)).ToSql();

        Checks.Absent("RETURNING", plain);
        Checks.Absent("OUTPUT", plain);
        Checks.Absent("LAST_INSERT_ID", plain);
    }

    // ── §12.5 的合并档：第二家把闸门放在拼树之前 ──────────────────────

    [Fact]
    public void The_dialect_says_up_front_that_a_write_statement_cannot_carry_another_table()
    {
        // 这一格从"三家独一份"变成 2∶2（T-SQL 的 UPDATE … FROM、金仓的 UPDATE … FROM / USING 为 true；
        // 达梦与这一家为 false）。翻案的不是判据而是那一格的<em>重数</em>：
        // SaveChanges 的整批退回从此不是两家孤例，而"退回去对不对"仍由 §12.4 第 2 项那句语义等价撑着。
        Assert.False(Provider.Dialect.SupportsJoinedWrites);

        Assert.True(SqlServer.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.True(Kingbase.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.False(Dameng.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
    }

    [Fact]
    public void A_hand_built_merged_update_names_both_missing_shapes_instead_of_dropping_the_join()
    {
        // 日常路径到不了这里（SupportsJoinedWrites 为 false 时 EntityTracker.Mergeable 第一道就退桶），
        // 手工构造 AST 才量得到发射层那一句：悄悄丢掉表源的合并 UPDATE 会把同一个值写给整张表。
        var failure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount)));

        Checks.Has("不接受目标表之外的表源", failure.Message);
        Checks.Has("SupportsJoinedWrites", failure.Message);

        // 与达梦那一条的文案<em>不同</em>是故意的：那一族要点名的是 MERGE INTO（它有那一形，只是共用层发不出），
        // 这一家没有可指认的替代形状，所以它点名的是"没有 UPDATE … FROM 也没有带列名清单的行集"。
        Checks.Absent("MERGE INTO", failure.Message);
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

        Checks.Has("不接受目标表之外的表源", failure.Message);

        // 两半共用一口（EmitDeleteTarget 默认转给 EmitUpdateTarget，而 JOIN 那一段共用 EmitJoinedSources），
        // 所以这一条与上面那条一起红才算那一口动了 —— 与达梦那一份同一条纪律。
        Assert.Equal(1, Checks.Occurrences(failure.Message, "1 个"));
    }

    // ── §12.4 第 2 项的变更守卫：三段式，而理由与另两家不是一条 ────────

    [Fact]
    public void A_not_null_column_still_needs_only_the_inequality()
        => Checks.Has("`t0`.`Amount` <> `t1`.`Amount`", GuardText(nameof(Order.Amount)));

    [Fact]
    public void A_nullable_column_takes_the_three_way_shape_because_it_is_the_only_candidate_needing_no_version()
    {
        var sql = GuardText(nameof(Order.PaidAt));

        Verify("update-guard-three-way", sql);

        // 这一家<em>有</em> IS DISTINCT FROM（8.0.29 起）也<em>有</em> <=>（NULL 安全的等号），按形状它本可以走
        // 金仓那一档一句。取三段的两条理由：加一颗 <=> 运算符是接口契约（不在 provider 单方权限内），
        // 而 IS DISTINCT FROM 要按版本发、SPI 里没有"库的版本"那一格。<b>三段式是这一家三个候选里唯一不看版本
        // 就发得出的形状</b>（金仓那一句式同样不看版本，所以这句判据只在候选集内成立，不是四家之间的判据）。
        // 于是守卫文本的形状在四家分成 3∶1（三家三段、金仓一家一句），而金仓那一家是"知道自己新到能用一句"的那一个。
        Checks.Has("`t0`.`PaidAt` <> `t1`.`PaidAt` OR (`t0`.`PaidAt` IS NULL AND `t1`.`PaidAt` IS NOT NULL) OR (`t0`.`PaidAt` IS NOT NULL AND `t1`.`PaidAt` IS NULL)", sql);
        Checks.Absent("IS DISTINCT FROM", sql);
        Checks.Absent("<=>", sql);

        // 括号不许少：AND 与 OR 混在一句里时，守卫的几段必须由括号成一个组（共用层的优先级表负责这一格）。
        Assert.Equal(2, Checks.Occurrences(sql, " OR "));
    }

    [Fact]
    public void An_unbounded_string_is_comparable_here_so_the_lob_split_now_reads_three_to_one()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        // §9.3：无长度的字符串在这一家落 TEXT（LOB 家族最大的一档），而 TEXT 能进比较式。
        // 于是"这一根 string 进不进得了合并档"在四家从 1∶2 变成 1∶3（只有 Oracle 血统那一家因为守卫关着），
        // 而它退回的<em>生产</em>原因是厂商明文限制之外的那颗方言闸门（LOB 在那一家连等号都不接受，
        // 但 SupportsJoinedWrites 已经先把整桶挡下了，见 MySqlChangeGuard 类注释第二段与本文件那条 3∶1 的注释）。
        Assert.Equal("TEXT", Provider.Types.GetStoreType(note));
        Assert.True(Provider.ChangeGuard.Compares(note));
        Assert.True(Provider.ChangeGuard.Compares(contract.Columns.Single(c => c.MemberName == nameof(Order.Amount))));

        // 唯一的否定样本：json。它今天产不出来（类型映射里没有 CLR→JSON 的那一格，src/ 里 Json<T> 是 0 处），
        // 所以这一颗是<em>护栏</em>而不是限制 —— 与达梦关掉四型 LOB 那一格对照着读：同一个方法关的东西完全不同
        // （一边问不出答案、一边厂商明文），而两家今天的生产后果都是零：那两家的合并档由方言闸门先挡掉了。
        Assert.False(Provider.ChangeGuard.Compares(note with { StoreTypeName = "json" }));
        Assert.True(Provider.ChangeGuard.Compares(note with { StoreTypeName = "longtext" }));

        // 带限定词的写法要落到基名上：这一家的 information_schema 把那一型报成 json 而 <c>column_type</c> 才带宽度，
        // 而 <c>BaseType</c> 吃的是 <see cref="ColumnContract.StoreTypeName"/>，所以要按同一口切掉括号 ——
        // 不切的话 "json(4)" 不在黑名单里，那一列会<em>进得了</em>合并档，而它在库里连等号都没有。
        var paidAt = contract.Columns.Single(c => c.MemberName == nameof(Order.PaidAt));

        Assert.False(Provider.ChangeGuard.Compares(paidAt with { StoreTypeName = "json(4)" }));
    }

    [Fact]
    public void A_collation_can_make_the_inequality_answer_no_which_is_not_this_family_s_private_problem()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        // 守卫的文本里没有 COLLATE 那一颗，所以 <> 走的是列上的排序规则；默认那几颗 utf8mb4_…_ci
        // 既不区分大小写也不区分重音 —— "库里 ABC、新值 abc"会被判成没变而把这一行挡在 UPDATE 之外。
        // 这一条钉不住那个行为（它要靠活库），它钉的是"我们<em>没有</em>在这里偷偷补一颗 BINARY"：
        // 强制二进制比较会把"这一列本来就不区分大小写"的现场语义改掉，那是判据而不是笔误，归 §14 的跨库差异表。
        var sql = GuardText(nameof(Order.CustomerName));

        Assert.True(Provider.ChangeGuard.Compares(name));
        Checks.Absent("COLLATE", sql);
        Checks.Absent("BINARY(", sql);
    }

    // ── 函数模板：同一条链，四家的模板表 ─────────────────────────────

    [Fact]
    public void The_same_chain_renders_this_family_s_function_templates()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026 && Math.Round(o.Amount, 2) > 1m)
            .ToSql();

        Verify("functions", sql);

        // LENGTH 在这一家数的是<em>字节</em>（utf8mb4 下一个汉字 3），而契约那一格写的是字符数（§10.4），
        // 所以取 CHAR_LENGTH。这一格是四家里唯一一个"名字像标准写法而量纲错"的陷阱：写 LENGTH 不报错、
        // 不红、只在中文上少一批行。
        Checks.Has("CHAR_LENGTH(`t0`.`CustomerName`) > @p0", sql);
        Checks.Has("EXTRACT(year FROM `t0`.`CreatedAt`) = @p1", sql);

        // 没有金仓那三层 CAST(… AS numeric)：那一格是"9.x 内核上 ROUND 只吃 numeric"逼出来的，
        // 这一家的 FLOAT/DOUBLE/DECIMAL 都直接吃。抄过来会让这一族的每条 ROUND 多三条 cast。
        Checks.Has("ROUND(`t0`.`Amount`, @p2)", sql);
        Checks.Absent("DATEPART", sql);
        Checks.Absent("CAST(", sql);
    }

    [Fact]
    public void String_reads_share_the_standard_form_with_kingbase_and_swap_locate_s_arguments()
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

        // SUBSTRING … FROM … FOR … 与金仓逐字同形（这一家同时认逗号那一形），取标准形的代价写在
        // MySqlExpressions 里：那一支能吃多复杂的表达式没有一台库能证，所以它记 §13.5 而不是记"已实测"。
        Checks.Has("SUBSTRING(`t0`.`CustomerName` FROM @p0 + 1 FOR @p1)", sql);
        Checks.Has("SUBSTRING(`t0`.`CustomerName` FROM @p2 + 1)", sql);

        // LOCATE 的实参顺序跟的是 STRPOS（子串在前）而不是 CHARINDEX（串在前），所以模板里 {0}/{1} 要交换。
        // 这一格是"函数名相同就以为形状相同"的那一类：写反了不报错，只是永远返回 0 → IndexOf 恒 -1。
        Checks.Has("(LOCATE(@p3, `t0`.`CustomerName`) - 1)", sql);

        // <b>三元的 + 折成两层 CONCAT，而每一颗实参各带一层 IFNULL</b>——这一句在 v0.37 之前写的是
        // <c>CONCAT(CONCAT(…))</c> 那一无包装的形，与金仓那一族逐字同形。同形这件事本来就是<em>错的</em>：
        // 本家的 CONCAT 传染 NULL【实测·P-07 ∶ P-08，出处 obj/logs/v37-my-recon-3.log】，
        // 而折成 CONCAT 的全部理由（§5.8 那句"C# 把 null 当空串"）在这一家需要额外一层包装才成立。
        // 包装后的形状与第七家那颗 <c>COALESCE … || …</c> 同一处置，模板里那两层嵌套则是共用层
        // <c>FoldConcatenation</c> 的二元折叠（<c>KingbaseDialectTests</c> 钉的是同一条规则的另一家读数）。
        Checks.Has("CONCAT(IFNULL(CONCAT(IFNULL(`t0`.`CustomerName`, ''), IFNULL(@p4, '')), ''), IFNULL(`t0`.`Note`, ''))", sql);

        // Blank 那一格三家三形，这一家与金仓同形（达梦不能写 = ''，因为它的 '' 就是 NULL）。
        Checks.Has("(`t0`.`Note` IS NULL OR TRIM(`t0`.`Note`) = '')", sql);
    }

    [Fact]
    public void Date_arithmetic_is_a_named_date_add_because_an_interval_cannot_be_multiplied()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Tomorrow = o.CreatedAt.AddDays(1), Ceiling = Math.Max(o.Amount, 1m) })
            .ToSql();

        Verify("date-and-number", sql);

        // 金仓那一族是 x + (n) * INTERVAL '1 day'（间隔能乘数），这一家不能乘，所以六个单位各写一颗 DATE_ADD。
        // 括号包着乘数那一条理由两家共用：{1} 若是表达式，没有它就把减法整个算进间隔里。
        Checks.Has("DATE_ADD(`t0`.`CreatedAt`, INTERVAL (@p0) DAY)", sql);
        Checks.Absent("INTERVAL '1 day'", sql);
        Checks.Absent("NUMTODSINTERVAL", sql);

        // GREATEST 原生（与金仓、达梦同形；T-SQL 要 CASE 展开）。NULL 传染那一档这一家与 T-SQL 同边、
        // 与金仓分岔 —— 那是对拍读不出来的语义差，写在 MySqlExpressions 类注释与 §14 的跨库差异表里。
        Checks.Has("GREATEST(`t0`.`Amount`, @p1)", sql);
    }

    [Fact]
    public void Like_escapes_with_a_tilde_because_a_backslash_is_the_default_string_escape()
    {
        var query = orm().Select<Order>();

        var sql = query.Where(o => o.CustomerName!.Contains("50%_off")).ToSql(out var parameters);

        Verify("where-like", sql);

        // 四家里唯一一处"转义符本身换字符"。共用层把那颗字符<em>原样</em>夹进引号里（DialectBase 的
        // ESCAPE '<字符>'），而反斜杠在这一家默认模式下是字符串里的转义符 —— 于是 ESCAPE '\' 是一条未闭合的串。
        // 改成 '//' 要动 IExpressionRules 的形状（那颗是 char 不是 string），那是接口契约。
        Checks.Has("LIKE @p0 ESCAPE '~'", sql);
        Assert.Equal("%50~%~_off%", parameters[0].Value);

        // 四家对读这一格时参数值本来就各家不同（各按各的转义符），那是正常结果而不是缺陷：
        // 代价只落在"跨库比对 LIKE 参数"这一件人工动作上，写在 MySqlExpressions 的类注释里。
        Checks.Absent("\\", sql);
    }

    // ── CodeFirst：类型名、表选项与那一颗自增 ─────────────────────────

    [Fact]
    public void Create_table_carries_the_engine_and_charset_because_the_default_is_not_a_promised_one()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Verify("ddl-order", sql);

        // 模式名一个都不写（与达梦同判：这一家没有 dbo/public 那种人人可进的公共模式）。
        // 但同一件事的含义不同：那一族裸名说的是"当前用户的模式"，这里说的是连接串带来的那个库 ——
        // "建在哪一库里"由驱动参数决定，那是 §14 那颗"连接串参数改变方言假设"缺口的第二个实例。
        Checks.Has("CREATE TABLE `QuickSqlOrder` (", sql);
        Checks.Absent("`APP`.", sql);
        Checks.Absent("`DBO`.", sql);
        Checks.Absent("`PUBLIC`.", sql);

        // 类型名逐列：量纲写在列上（VARCHAR(n) 的 n 是字符数）、无长度字符串落 TEXT、时刻带 3 位小数秒、
        // 枚举是 INT（这一家也有原生 ENUM 型，但那是"每库一套取值清单"的路子，跨库更难迁）。
        Checks.Has("`Id` INT AUTO_INCREMENT NOT NULL", sql);
        Checks.Has("`CustomerName` VARCHAR(64) NULL", sql);
        Checks.Has("`State` INT NOT NULL", sql);
        Checks.Has("`Amount` DECIMAL(18,4) NOT NULL", sql);
        Checks.Has("`CreatedAt` DATETIME(3) NOT NULL", sql);
        Checks.Has("`Note` TEXT NULL", sql);
        Checks.Has("CONSTRAINT `PK_QuickSqlOrder` PRIMARY KEY (`Id`)", sql);

        // 尾部那一段不是版式而是语义：ENGINE 决定这张表有没有事务（整个 SaveChanges 依赖它，
        // 而默认引擎在有些部署里是 MyISAM），CHARSET 决定 VARCHAR(n) 里的 n 能不能装下中文 ——
        // 后者与 T-SQL 那一家默认 NVARCHAR 是同一条判断的两半。刻意不写 COLLATE：那是现场策略。
        Checks.Has(") ENGINE = InnoDB DEFAULT CHARSET = utf8mb4", sql);

        // 另三族的自增写法与 T-SQL 的存储布局开关一律不许漏进来。
        Checks.Absent("IDENTITY", sql);
        Checks.Absent("GENERATED BY DEFAULT", sql);
        Checks.Absent("NVARCHAR", sql);
        Checks.Absent("DATETIME2", sql);
        Checks.Absent("CLUSTERED", sql);
    }

    [Fact]
    public void A_named_schema_qualifies_the_table_and_keeps_its_case()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema().Schemas.Contract<Order>()));

        Verify("ddl-qualified", sql);

        // 这一份样本要写的只有一句：限定符出来了，而它<em>没有</em>被折叠 —— 与达梦那一份 ddl-qualified
        // 正相反（那一家模型里写 app、发出去必须是 "APP"）。这一家的敏感开关在文件系统上
        // （lower_case_table_names 建库时定死），所以既不能赌区分也不能赌不区分，只能原样带引号发出去。
        Checks.Has("CREATE TABLE `app`.`QuickSqlOrder` (", sql);
        Checks.Absent("`APP`", sql);
    }

    [Fact]
    public void Column_changes_are_one_statement_and_the_comment_is_part_of_the_definition()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));
        var commented = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName)) with { Comment = "客户名" };

        var sql = string.Join(" ; ",
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, note)),
            Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, amount)),
            Statement(w => Assert.True(Provider.CodeFirst.TryEmitColumnComment(w, contract.Table, commented))));

        Verify("ddl-alter-and-comment", sql);

        Checks.Has("ALTER TABLE `QuickSqlOrder` ADD COLUMN `Note` TEXT NULL", sql);

        // 改精度一句说完，而且没有金仓那条 " ; " 串联的第二句（那一家 ALTER COLUMN … TYPE 不碰可空性）。
        // MODIFY COLUMN 吃的是"这一列的完整定义"：型、可空性、自增三格必须一起重写，漏哪一格就等于顺手改了它
        // —— 而 AUTO_INCREMENT 那一格漏掉时，主键列上库直接报 1075。
        Checks.Has("ALTER TABLE `QuickSqlOrder` MODIFY COLUMN `Amount` DECIMAL(18,4) NOT NULL", sql);
        Checks.Absent("ALTER COLUMN", sql);

        // 注释<em>不是独立语句</em>：这一家没有 COMMENT ON COLUMN，也没有 T-SQL 那种扩展属性存储过程。
        // 所以这一格里注释跟着列定义走，而代价（顺手重写整列）写在 MySqlCodeFirst 的类注释第四条。
        Checks.Has("ALTER TABLE `QuickSqlOrder` MODIFY COLUMN `CustomerName` VARCHAR(64) NULL COMMENT '客户名'", sql);
        Checks.Absent("COMMENT ON COLUMN", sql);
        Checks.Absent("sp_addextendedproperty", sql);
        Checks.Absent("N'", sql);
    }

    [Fact]
    public void Changing_precision_and_writing_a_comment_share_one_definition_so_neither_forgets_a_slot()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var commented = name with { Comment = "客户名" };

        var precision = Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, commented));

        // 这一条钉的是 MySqlCodeFirst 里那一个私有 Definition(column, includeComment) 的全部理由：
        // 注释与改精度共用一份列定义出口，否则"改精度把注释抹掉"与"加注释把型改回去"会各犯一次。
        // 前一家不会红在这一格（那两件事在那一家是两句独立语句），所以它是第四家给共用层添的第一份见证。
        Checks.Has("MODIFY COLUMN `CustomerName` VARCHAR(64) NULL COMMENT '客户名'", precision);

        // 反方向那一半：契约没写注释时不发 COMMENT 子句 —— 但库里已有的那一处会被这一句抹掉，
        // 那是已知代价（§14），不是这里的断言。
        var bare = Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, name));

        Checks.Absent("COMMENT", bare);
    }

    /// <summary>注释字面量里的 <b>反斜杠</b>（v0.39 第二笔）。这一家缺省把 <c>\</c> 当转义符，而注释是
    /// 契约文本直接排版进字面量执行的两处发射点（表注与列注）—— 原 <c>AppendStringLiteral</c> 只翻倍单引号，
    /// <c>C:\temp</c> 会静默换形，而<b>尾随反斜杠</b>把闭合引号转义掉，字符串无处闭合，ALTER 当场语法错。
    /// 所以这一族的两处发射点带 <c>backslashIsEscape</c> 档：<c>'</c> 与 <c>\</c> 各自翻倍
    /// （引号翻倍两种 sql_mode 下都写法都合法）。另四家不管这一档——它们没有反斜杠转义这一说。</summary>
    [Fact]
    public void Comment_literals_escape_the_backslash_because_contract_text_lands_inlined()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        // 尾随反斜杠是这次的问题点：不翻倍时它转义掉闭合引号，一句 ALTER 语法错。
        var column = Statement(w => Assert.True(Provider.CodeFirst.TryEmitColumnComment(
            w, contract.Table, name with { Comment = @"C:\temp\尾\" })));
        var table = Statement(w => Assert.True(Provider.CodeFirst.TryEmitTableComment(
            w, contract.Table, @"C:\path \'_mix'")));

        // \ → \\ ，' → '' ，双双翻倍后字符串在原文尽头闭合，注释逐字读得回去。
        Checks.Has(@"MODIFY COLUMN `CustomerName` VARCHAR(64) NULL COMMENT 'C:\\temp\\尾\\'", column);
        Checks.Has(@"ALTER TABLE `QuickSqlOrder` COMMENT = 'C:\\path \\''_mix'''", table);
    }

    [Fact]
    public void A_full_text_index_is_emitted_because_it_is_native_here_and_the_parser_is_the_open_half()
    {
        var contract = orm().Schemas.Contract<Order>();

        var sql = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("FT_QuickSqlOrder_Note", IndexKind.FullText, [nameof(Order.Note)]) { ColumnNames = ["Note"] }));

        Verify("ddl-fulltext-index", sql);

        // 四家里第一次有人把它<em>建成一句真全文索引</em>：另三家各有各的全文（域索引、tsvector、Oracle Text）而都在契约之外，
        // 所以金仓与达梦两族在这一格抛。T-SQL 也不抛，但它发的是被降格的普通索引（见 <c>SqlServerCodeFirst.EmitCreateIndex</c>）——
        // 本条上一版写作"第一次有人不抛"，交账时按四家各自的 <c>EmitCreateIndex</c> 重数推翻：不抛的有两家，
        // 把 <c>FULLTEXT</c> 两个字真写进 SQL 的只有一家。
        // 但这一家<em>不报错</em>恰恰是更该写清的那一种：默认解析器按空格与标点切词，中文整段进一个 token，
        // 索引建好了而 MATCH … AGAINST 查不出中文 —— 要 WITH PARSER ngram，而索引类型在 IndexContract 里没有那一格。
        Checks.Has("CREATE FULLTEXT INDEX `FT_QuickSqlOrder_Note` ON `QuickSqlOrder` ( `Note` )", sql);
        Checks.Absent("WITH PARSER", sql);
        Checks.Absent("ASC", sql);

        // 方向在全文索引上没有意义（那一类按相关度排序），所以这里不写方向，而不是写一颗被忽略的方向。
        // 普通索引那边<em>写</em>方向，而 5.7 解析 DESC 但忽略它 —— 那一格要版本判断，SPI 里没有（§14）。
        var plain = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("IX_QuickSqlOrder_Note", IndexKind.NonClustered, [nameof(Order.Note)]) { ColumnNames = ["Note"] }));

        Checks.Has("CREATE INDEX `IX_QuickSqlOrder_Note` ON `QuickSqlOrder` ( `Note` ASC )", plain);
    }

    // ── 内联字面量（ToLogSql 那一档）─────────────────────────────────

    [Fact]
    public void Text_never_inlines_here_because_one_reading_of_the_string_depends_on_a_session_switch()
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

        // 值本身带反斜杠（Windows 路径是这一类值最常见的样子），所以下面判的是"这一整颗参数留回了占位符"。
        var emitted = EmitInsert(in insert);
        var log = emitted.ToLogSql(Provider);

        // 四家里第一家把字符串内联整个关掉。共用的 AppendStringLiteral 只做标准那一半（翻倍单引号），
        // 而默认模式下反斜杠也是转义符 —— 于是 C:\temp 这类值会在"日志里的 SQL"那一档静默换形。
        // 两种 sql_mode 下没有一种统一的写法，而猜错的这一半是改值不是报错。
        Assert.Equal(2, emitted.Parameters.Count);
        Checks.Has("@p0", log);
        Checks.Has("@p1", log);
        Checks.Absent("客户甲", log);
        Checks.Absent(@"C:\temp", log);

        // 代价可数而且只在一条路上：TryInlineLiteral 在 src/ 里唯一的消费者是 SqlLogging，
        // 也就是说 ToLogSql() 少了一档人眼可读的字面量 —— 执行侧一个字节都没变（执行走参数，从来不走内联）。
        // 与金仓拒 bytea 同一档判据（"给一份看着能执行、换台库就错的文本比不给更坏"），而这里多拒一型。
        Checks.Has("INSERT INTO `QuickSqlOrder`", log);
    }

    [Fact]
    public void The_literals_this_family_does_inline_are_written_the_way_a_client_reparses_them()
    {
        Assert.True(Provider.Values.TryInlineLiteral(true, null, out var flag));
        Assert.Equal("TRUE", flag);

        Assert.True(Provider.Values.TryInlineLiteral(false, null, out var unset));
        Assert.Equal("FALSE", unset);

        // 布尔字面量与谓词侧的 = 1 不矛盾：那一档说的是"库里那一列怎么比"（TINYINT(1) 装得下 2），
        // 这一档说的是"日志里的 TRUE 抄进客户端要能认"。金仓同形，T-SQL 写 1/0。
        Assert.True(Provider.Values.TryInlineLiteral(new DateTime(2026, 3, 4, 5, 6, 7, 890), null, out var moment));
        Assert.Equal("'2026-03-04 05:06:07.890'", moment);

        // 分隔符是空格而不是 ISO 的 T：这一家确实也认 T，而"认不认"取决于解析器版本，
        // 日志里的 SQL 是给人复制到客户端里跑的，所以取文档给的规范形。
        Assert.DoesNotContain("T05:06", moment, StringComparison.Ordinal);

        // byte[] 也拒，但理由与金仓那一族<em>不是</em>同一格：那里是同一个字面量有两种读法，
        // 这里是内联长度不受参数预算管、只受 max_allowed_packet 管。
        Assert.False(Provider.Values.TryInlineLiteral(new byte[] { 1, 15 }, null, out _));

        // 而 Guid 可以：'D' 形只含十六进制与短横，不含反斜杠，所以那条唯一的理由在这里不成立。
        // "能不能内联"要按形状判而不是按族判 —— 这是第四家给这一格添的第四份见证。
        Assert.True(Provider.Values.TryInlineLiteral(Guid.Empty, null, out var id));
        Assert.Equal("'00000000-0000-0000-0000-000000000000'", id);
    }

    [Fact]
    public void A_floating_point_literal_keeps_the_dot_that_the_first_two_families_learned_to_add()
    {
        // 与达梦同名那条用例的<em>反向</em>断言：这一家抄的是 SqlServerValues 那一份 2026-09-22
        // 真库实测<em>之后</em>的形状（1E-08.0 是语法错，而修复前后的两版在另两族各撞出来一次，§13.5）。
        // 这一家的 FLOAT/DOUBLE 列上 3 与 3.0 是不是同一档隐式转换<em>未实测</em> —— 取"与另两族一致"
        // 而不是"这一家更松"，因为后者需要一台库来证。
        Assert.True(Provider.Values.TryInlineLiteral(3d, null, out var whole));
        Assert.Equal("3.0", whole);

        Assert.True(Provider.Values.TryInlineLiteral(2.5f, null, out var fraction));
        Assert.Equal("2.5", fraction);

        Assert.True(Provider.Values.TryInlineLiteral(1e-08, null, out var tiny));
        Assert.Equal("1E-08", tiny);

        Assert.False(Provider.Values.TryInlineLiteral(double.NaN, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.PositiveInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.NegativeInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(float.NegativeInfinity, null, out _));
    }

    // ── 类型映射里那两处"宁缺不猜" ───────────────────────────────────

    [Fact]
    public void A_column_type_this_family_cannot_represent_is_refused_rather_than_narrowed()
    {
        var contract = orm().Schemas.Contract<Order>();
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        // §9.3 原稿给 DateTimeOffset 写的退路是"降级 DateTime UTC + 列标记，读取端还原"，而那两半今天都不存在。
        // 悄悄按本地时间写进去是把 +08:00 变成 08:00 再读回 00:00 的那种静默改值（§5.8 同一条纪律），所以抛。
        var offset = amount with { ClrType = typeof(DateTimeOffset), StoreTypeName = null };

        var failure = Checks.Raised<QuickSqlException>(() => Provider.Types.GetStoreType(offset));

        Checks.Has("DateTimeOffset", failure.Message);
        Checks.Has("降级", failure.Message);

        // 未登记的 CLR 型同样抛，而它点名的正是那一条退路（显式给 [Column(TypeName=…)]）—— 与上面那颗
        // 只是消息里少了"没有那一型"的说法，所以两句各判一次：抛出这一行为本身要钉住两次。
        var unregistered = amount with { ClrType = typeof(Uri), StoreTypeName = null };

        var unknown = Checks.Raised<QuickSqlException>(() => Provider.Types.GetStoreType(unregistered));

        Checks.Has("未登记 CLR 类型", unknown.Message);
        Checks.Has("Uri", unknown.Message);
    }

    [Fact]
    public void A_bool_column_is_TINYINT_1_which_is_why_the_shared_equality_predicate_stays()
    {
        var contract = orm().Schemas.Contract<Ticket>();
        var deleted = contract.Columns.Single(c => c.MemberName == nameof(Ticket.Deleted));

        // 类型名与谓词形状是同一件事的两半：这一家把 BOOL 起名为 TINYINT(1)，而那一型装得下 2，
        // 所以"这一列有布尔型"（Boolean_Type 那位不声明）与"谓词写 = 1"（EmitBooleanEquality 不覆写）
        // 是同一个库种事实的两次落地。缺了这一条，那两处就会被人各自"顺手改对"。
        Assert.Equal("TINYINT(1)", Provider.Types.GetStoreType(deleted));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Boolean_Type));

        // 窄整数一律往宽里放（与另两族同一条纪律），而这里不用 UNSIGNED：那会把
        // "读回来该配哪种 CLR 型"变成 provider 的事。
        var token = orm().Schemas.Contract<SessionToken>();

        Assert.Equal("CHAR(36)", Provider.Types.GetStoreType(token.Columns.Single(c => c.MemberName == nameof(SessionToken.Id))));
    }

    // ── 槽位本身：这一档声明了什么、没声明什么 ────────────────────────

    [Fact]
    public void The_family_declares_seven_bits_and_the_sixth_is_a_channel_the_seventh_is_the_wiring_bit()
    {
        var declared = Enum.GetValues<DbCapabilities>()
            .Where(static bit => bit != DbCapabilities.None && Provider.Capabilities.HasFlag(bit))
            .ToArray();

        // 原文（v0.35 起这本账的形状）："5 位、全部有离线文本，是四档里最少的一份掩码，也是第二家『仅厂商文档』
        // 那位数为 0 的档（第一家是达梦那一档 6 = 6 + 0）"。两格今天一起改判，判据一个字没动：位数涨到 6
        // （那笔在血统基类挂上 <c>Bulk_Copy</c>，tidb／oceanbase 同源继承而一位不改写），于是"全部有离线文本"
        // 那半句作废——第 6 颗不是一段 SQL 而是一条装载通道，它在 §13.5 里落"仅厂商文档"那一档（驱动 API 有
        // 对照量具的真库读数、provider 那条反射装配还没跑过集成册），本家因此从零那一侧走到 sqlite 那本旁边（各 1 位）；
        // "最少"那一格也不再是本家，它由 gbase8s 那本 4 位独占。<b>那不是能力强，是敢声明的少</b>——
        // 逐位判据写在 MySqlProviderBase 的类注释里，出处记在 §13.5。
        // 【本笔（ClickHouse 重开·笔一）】第 7 颗 Transactions 同样不是库种事实：内核写路径按位分岔的接线位，
        // 族档领位、三叶同领；"仅厂商文档"档对它也不适用（它的账在 TransactionCapabilityTests）。
        Assert.Equal(7, declared.Length);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, declared);
        Assert.Contains(DbCapabilities.Guid_Char36, declared);
        Assert.Contains(DbCapabilities.Schemas, declared);
        Assert.Contains(DbCapabilities.Comment_On_Column, declared);
        Assert.Contains(DbCapabilities.Alter_Column_Precision, declared);
        Assert.Contains(DbCapabilities.Transactions, declared);

        // 第 6 颗与另五颗不同档的那一格在这里可指：另五颗每一位都有一段被黄金样本或本套件钉住的文本，
        // 这一颗可指的是 <c>MySqlBulk.ResolveCopyChannel</c> 那颗反射口——它在离线册上返回 null
        // （这里没有真驱动可解析），而那不等于位该收回：位判据是"代码今天走到那一条"，走到的是那段反射装配本身。
        Assert.Contains(DbCapabilities.Bulk_Copy, declared);
        Assert.Null(Provider.Bulk!.ResolveCopyChannel());

        // 第一家不声明 CaseSensitive_Identifiers 的档：引号照加（防保留字）与"解析器判等区分大小写"
        // 是两件事，而这一家只有前者成立。下面那两句是它的对照：另两档都声明了它，
        // 于是 §13.1 那句"两个离线家族共有四位"从今天起是三位（那一格在台账用例里翻案）。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.CaseSensitive_Identifiers));
        Assert.True(Kingbase.Provider(SqlServer.NoDriverFactory.Instance).Capabilities.HasFlag(DbCapabilities.CaseSensitive_Identifiers));
        Assert.True(Dameng.Provider(SqlServer.NoDriverFactory.Instance).Capabilities.HasFlag(DbCapabilities.CaseSensitive_Identifiers));

        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Returning));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Upsert_OnConflict));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiStatement));
    }

    [Fact]
    public void The_bulk_budget_is_a_protocol_number_and_its_cost_is_visible_in_the_chunk_count()
    {
        // 与第三家相反的选择：那一族交回 null（库侧与驱动侧各有一个上限，没有活库能告诉哪一个先生效），
        // 这一族有一个协议级的数字可报（预处理语句的占位符计数只有两字节宽）。
        var plan = orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        Assert.NotNull(Provider.Bulk);
        Assert.Equal(65_535, plan.ParameterBudget);
        Assert.Equal(7, plan.ColumnsPerRow);
        Assert.Equal(8425, plan.RowsPerStatement);
        Assert.Single(plan.Chunks);
        Assert.Equal(600, plan.Chunks.Sum(static c => c.RowCount));
        Checks.Has("参数预算 65535", plan.Reason);

        // 65535 与金仓那一份 32767 都在同一份 RowsPerStatement 公式下工作（§13.1 那条主张的第四份见证）。
        // 8425 与 4212 差一倍而块数都是 1 —— 那正是"600 行 × 7 列 = 4200 颗"低于两家预算的结果，
        // 不是两家共用一个数。这一句在下面那对断言里有可数的形态，而 DialectParityTests 里再核一次。
        var kingbase = Kingbase.Orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        Assert.Single(kingbase.Chunks);
        Assert.True(plan.RowsPerStatement > kingbase.RowsPerStatement);
        Assert.True(plan.ParameterBudget > kingbase.ParameterBudget);
    }

    [Fact]
    public void The_two_slots_nobody_reads_yet_answer_for_this_family()
    {
        // ConcatOperator 与 SupportsRegex 在 src/ 里零消费者（四家同一格），所以它们今天只是台账上的一句话。
        // 这一条钉不住产出，钉的是"这一家给的答案是哪一头"：写 || 在这一家是逻辑 OR（默认 sql_mode 不折成
        // 字符串拼接），于是将来接它的那个人手里必须已经写着函数名而不是运算符。
        Assert.Equal("CONCAT", Provider.Expressions.ConcatOperator);
        Assert.NotEqual("||", Provider.Expressions.ConcatOperator, StringComparer.Ordinal);
        Assert.False(Provider.Expressions.SupportsRegex);

        // 转义符是这一家<em>有</em>消费者的一格（翻译器与 DialectBase 各读一次），所以它落在样本上：
        // 见上面那条 where-like。这两颗位放在一起，是因为"零消费者的槽位"与"有消费者的槽位"要分开结算。
        Assert.Equal('~', Provider.Expressions.LikeEscapeCharacter);
    }

    [Fact]
    public void The_leaf_overrides_nothing_so_its_entire_output_is_the_family_s()
    {
        var order = orm().Schemas.Contract<Order>();

        var leaf = Statement(w => Provider.CodeFirst.EmitCreateTable(w, order));
        var family = Statement(w => Family.CodeFirst.EmitCreateTable(w, order));

        // 这一条是 §9.1 那句"MySQL 本尊 / MariaDB / TiDB 共享一份"的落地形态，也是这一版的空壳证据：
        // 叶与基类<em>逐字</em>相同（达梦那一份的对应用例差一行 IDENTITY，因为那一格真的分岔了）。
        // 所以这一家没有 ddl-order-mysql 那样的第二份基线 —— 两份产出相同，多一份文件只会多一处要同步的地方。
        Assert.Equal(family, leaf);
        Assert.Equal(Family.Dialect.ParameterPrefix, Provider.Dialect.ParameterPrefix);
        Assert.Equal(Family.Capabilities, Provider.Capabilities);

        // 连拒绝的文案也一样（Kind 相同才有这一句）：那三颗将来才覆写的差异（MariaDB 的 RETURNING、
        // TiDB 的 AUTO_ID_CACHE 与表选项）都不在这里，它们各自要换的是策略对象或表选项那一段，
        // 而那颗表选项今天是 const —— 等真有叶子那天再虚化，不留一颗没人覆写的虚函数。
        // 【v0.37 第五笔：这一族"将来覆写者"名单里 MariaDB 那格作废——本仓库裁不立 MariaDB、DbKind.MariaDb 已撤（见 §14）；
        //  剩 TiDB 一颗已落但逐格裁"不覆写"。】
        Assert.Equal(
            Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount), Family)).Message,
            Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount))).Message);
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>血统基类的可实例化形态（与 <c>DamengDialectTests</c> 里那一份 <c>OracleFamilyProbe</c> 同一种
    /// 探针）。它存在的唯一理由是上面那条空壳证据：这一族的叶今天零覆写，所以<em>基类实例就是产出本体</em>。</summary>
    sealed class MySqlFamilyProbe(DbKind kind, DbProviderFactory factory) : MySqlProviderBase(kind, factory)
    {
    }

    static SqlWriter NewWriter(IDbProvider? provider = null)
        => new(new StringBuilder(), [], (provider ?? Provider).Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }

    // ISqlDialect 的 Emit 是按语句类型各一记重载（in Select/Insert/Update/Delete），没有"收一份 SqlStatement"
    // 的那一口 —— 所以这里每种语句各一个发射器，而不是一个共用的 Emit(...)。
    static DialectSql EmitUpdate(UpdateStatement ast, IDbProvider? provider = null)
    {
        var writer = NewWriter(provider);

        (provider ?? Provider).Dialect.Emit(in ast, writer);

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

    /// <summary>守卫的文本。<b>与达梦那一份同一个处境</b>：这一家没有 UPDATE … FROM，合并档发不出去
    /// （上面那两条拒绝用例量的就是这件事），而守卫仍然要有文本见证 —— 于是挂在一条<em>不带表源</em>的
    /// 最小 UPDATE 上发射，那颗 SET 与守卫无关，它存在的理由只是"UPDATE 必须有一句 SET"。
    ///
    /// <para>与达梦<em>不同</em>的一处：这里两侧的别名都是 t0（<c>WriteTargetAlias</c> 交回的是共用层那个默认值），
    /// 所以这段文本读得出的是守卫的形状，读不出目标别名 —— 后者由 <see cref="The_write_target_takes_an_alias_so_the_ansi_form_of_the_shared_layer_is_legal_here"/> 钉。</para></summary>
    static string GuardText(string member, IDbProvider? provider = null)
    {
        var owner = provider ?? Provider;
        var contract = orm().Schemas.Contract<Order>();
        var column = contract.Columns.Single(c => c.MemberName == member);

        var stored = new TableRef(0, owner.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(amount, new ParameterValue(1m, amount))],
            Where = owner.ChangeGuard.BuildDifference(column,
                new ColumnExpression(stored, column), new ColumnExpression(side, column)),
        };

        return EmitUpdate(ast, owner).Text;
    }

    /// <summary>手搭一份合并 UPDATE，形状与 <c>EntityTracker.WriteMergedAsync</c> 同构（T-SQL 那份在
    /// <see cref="ChangeGuardTests"/>，金仓那份在 <see cref="KingbaseDialectTests"/>，达梦那份在
    /// <see cref="DamengDialectTests"/>）。<b>在这一家它唯一的用途是"抛得准不准"</b>：发射必然在
    /// <c>EmitJoinedSources</c> 那一句上红 —— 所以它不需要键与行集值都真实，只要表源在就够了。</summary>
    static DialectSql MergedUpdate(string member, IDbProvider? provider = null)
    {
        var owner = provider ?? Provider;
        var contract = orm().Schemas.Contract<Order>();
        var stored = new TableRef(0, owner.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var key = contract.PrimaryKey[0];
        var set = contract.Columns.Single(c => c.MemberName == member);
        ColumnContract[] columns = [key, set];

        var rows = new IReadOnlyList<SqlExpression>[2];

        for (var row = 0; row < rows.Length; row++)
            rows[row] = [new ParameterValue(7 + row, key), new ParameterValue("值", set)];

        var on = new SqlBinaryExpression(BinaryOperator.Equal,
            new ColumnExpression(stored, key), new ColumnExpression(side, key));

        var difference = owner.ChangeGuard.BuildDifference(set,
            new ColumnExpression(stored, set), new ColumnExpression(side, set));

        return EmitUpdate(new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(set, new ColumnExpression(side, set))],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, key), new ParameterValue(7, key)),
            Joins = [new JoinClause(JoinKind.Inner, new SourceRows(side, columns, rows), on)],
        }, owner);
    }
}
