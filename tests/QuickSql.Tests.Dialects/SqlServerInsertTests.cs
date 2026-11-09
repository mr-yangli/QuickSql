using System.Data;
using System.Text;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using QuickSql.Write;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>写路径的离线验证（§5.4 的写侧、§12.3 的分块、§12.4 的代价面、§10.1 的钩子规则）。
/// 全程 <see cref="SqlServer.NoDriverFactory"/>：生成一条 INSERT 不需要驱动，一旦某条用例把它碰着，
/// 那就是「SQL 生成期混进了连接依赖」的缺陷信号，而不是替身没配好。
/// 与读侧同一套分工：黄金样本钉版式，结构断言钉"凭什么长这样"。</summary>
public sealed class SqlServerInsertTests
{
    /// <summary><see cref="Order"/> 的契约有 8 列，其中 <c>Id</c> 是自增列 ⇒ 每行写 7 列。
    /// 分块公式、参数个数、审计触发次数都挂在这个数上，所以它是本文件的常量之一，改了会连带一片红。</summary>
    const int Written = 7;

    static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, 890);

    static QuickSqlOrm orm() => SqlServer.Orm();

    /// <summary>§12.3 的整块边界：预算 2100、留十分之一给文本、每行 7 颗参数。</summary>
    const int Budget = 2100;

    const int RowsPerStatement = Budget * 9 / 10 / Written;

    static Order row(int n) => new()
    {
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = Moment,
    };

    static Order[] rows(int count) => [.. Enumerable.Range(1, count).Select(row)];

    static QuickSqlOrm ormWith(QuickSqlAop aop, IDbProvider? provider = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = provider ?? SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        });

    // ── 版式（§5.4 写侧 + §12.4 第 1 项的列合成） ──────────────────────

    [Fact]
    public void A_single_row_is_one_parameterized_statement_over_every_written_column()
    {
        var sql = orm().Insert(row(1)).ToSql();

        Golden.Verify("insert-single", sql);

        // 自增列不进列清单：它的值由库决定，写进去只会得到"填的数被忽略"。
        Assert.Equal(
            [.. orm().Schemas.Contract<Order>().Columns.Where(c => !c.IsIdentity).Select(c => c.ColumnName)],
            ColumnList(sql));

        Checks.Has("INSERT INTO [QuickSqlOrder] ", sql);
        Checks.Absent("[Id]", sql);
    }

    [Fact]
    public void An_identity_value_the_caller_wrote_never_reaches_the_wire()
    {
        var order = row(1);

        order.Id = 4242;

        var sql = orm().Insert(order).ToSql(out var parameters);

        // 被丢掉而不是被报错：填了自增列是常见写法（从 DTO 拷过来的实体上它就在那儿），
        // 而"显式写自增列"是 IDENTITY_INSERT 那条路，P0 未接。
        Assert.Equal(Written, parameters.Count);
        Checks.Absent("4242", sql);
    }

    [Fact]
    public void Every_cell_is_a_parameter_and_its_column_travels_with_it()
    {
        orm().Insert(row(1)).ToSql(out var parameters);

        Assert.Equal(["@p0", "@p1", "@p2", "@p3", "@p4", "@p5", "@p6"], parameters.Select(p => p.Name).ToArray());
        Assert.Equal(["CustomerName", "CustomerId", "Amount", "State", "CreatedAt", "PaidAt", "Note"], parameters.Select(p => p.Hint!.MemberName).ToArray());

        // 驱动不会替我们转枚举（§9.3），所以转换必须在 binder 里发生。
        Assert.Equal(2, parameters[3].Value);
        Assert.Equal(DbType.Int32, parameters[3].DbType);

        // 遗留 datetime 会先把毫秒取整到 1/300 秒（§9.3）。
        Assert.Equal(DbType.DateTime2, parameters[4].DbType);

        // null 也参数化成 NULL，而不是让那一格落 DEFAULT —— 带默认值约束的列上这两者结果不同。
        Assert.Equal(DBNull.Value, parameters[5].Value);
    }

    [Fact]
    public void Several_rows_are_one_statement_with_one_tuple_per_row()
    {
        var sql = orm().InsertRange(rows(3)).ToSql(out var parameters);

        Golden.Verify("insert-three-rows", sql);

        // 多行是一次往返里的一条语句，不是三条：这是 L3 与 L5 的分界（§12.3）。
        Assert.Equal(3 * Written, parameters.Count);
        Assert.Equal(2, Checks.Occurrences(sql, "), ("));

        // 参数按行主序铺开 —— 回填时「回包第 k 行 ↔ 入参第 k 行」就是靠这个顺序成立的（§12.4 第 1 项）。
        Assert.Equal(["客户1", "客户2", "客户3"], new[] { 0, Written, 2 * Written }.Select(i => (string) parameters[i].Value!).ToArray());
    }

    [Fact]
    public void The_key_returning_variant_puts_OUTPUT_between_the_column_list_and_VALUES()
    {
        var insert = orm().InsertRange(rows(3));

        var sql = insert.ExplainKeys().ToSql();

        Golden.Verify("insert-three-rows-with-keys", sql);

        Checks.Has(") OUTPUT inserted.[Id] VALUES (", sql);

        // 一次 OUTPUT 覆盖整条语句的多行结果集，不是一行一个：位置与数量都是 T-SQL 的硬要求。
        Assert.Equal(1, Checks.Occurrences(sql, "inserted.[Id]"));
        Assert.True(sql.IndexOf("OUTPUT", StringComparison.Ordinal) < sql.IndexOf("VALUES", StringComparison.Ordinal));

        // 另一档不带 OUTPUT：两档的差别只在文本，参数表逐格相同，OUTPUT 不占参数位。
        // 比的是两个引擎实例 —— 同一个实例上两档共用同一份取值结果，那样比是句回声。
        var withoutKeys = orm().InsertRange(rows(3)).Explain();

        Checks.Absent("OUTPUT", withoutKeys.ToSql());
        Assert.Equal(3 * Written, withoutKeys.Chunks[0].Sql.ParameterCount);
        Assert.Equal(withoutKeys.Chunks[0].Sql.Parameters, insert.ExplainKeys().Chunks[0].Sql.Parameters);
    }

    [Fact]
    public void A_table_without_an_identity_column_keeps_its_key_in_the_column_list_and_refuses_the_key_exit()
    {
        var token = new SessionToken { Id = Guid.NewGuid(), Value = "abc", ExpiresAt = Moment };

        var sql = orm().Insert(token).ToSql(out var parameters);

        Assert.Equal(["Id", "Value", "ExpiresAt"], ColumnList(sql));

        // Guid 是原生类型，不做文本化（跨库对齐才需要 GuidAsText）。
        Assert.Equal(DbType.Guid, parameters[0].DbType);

        // 没有自增列还带 Returning，产出的就是一条"看着像在回填"的普通 INSERT —— 比报错更难查。
        var failure = Checks.Raised<QuickSqlException>(() => orm().Insert(token).ExplainKeys());

        Checks.Has("没有自增列", failure.Message);
    }

    // ── §12.3 分块 ────────────────────────────────────────────────────

    [Fact]
    public void The_parameter_budget_of_the_provider_divided_by_the_column_count_sets_the_chunk_size()
    {
        Assert.Equal(Budget, SqlServer.Provider(SqlServer.NoDriverFactory.Instance).Bulk!.ParameterBudget);
        Assert.Equal(270, RowsPerStatement);

        var plan = orm().InsertRange(rows(600)).Explain();

        Assert.Equal(3, plan.Chunks.Count);
        Assert.Equal([270, 270, 60], plan.Chunks.Select(c => c.RowCount).ToArray());
        Assert.Equal([0, 270, 540], plan.Chunks.Select(c => c.FirstRow).ToArray());

        // 不重不漏：分块只是把同一批行切开，行数总和必须还是那批行。
        Assert.Equal(600, plan.Chunks.Sum(c => c.RowCount));
        Assert.All(plan.Chunks, chunk => Assert.True(chunk.Sql.ParameterCount <= Budget * 9 / 10, $"这一块用了 {chunk.Sql.ParameterCount} 颗参数，预算的九成是 {Budget * 9 / 10}"));

        // 整块边界：正好一块、多一行就两块。
        Assert.Single(orm().InsertRange(rows(RowsPerStatement)).Explain().Chunks);
        Assert.Equal(2, orm().InsertRange(rows(RowsPerStatement + 1)).Explain().Chunks.Count);

        // 除的是这个实体的列数，不是一个全局常数：Customer 每行 3 列 ⇒ 每块 630 行。
        Assert.Equal(3, orm().Schemas.Contract<Customer>().Columns.Count(c => !c.IsIdentity));
        Assert.Equal(Budget * 9 / 10 / 3, orm().InsertRange(customers(600)).Explain().RowsPerStatement);

        Checks.Has($"每块 {RowsPerStatement} 行（3 块）", plan.Reason);
    }

    static Customer[] customers(int count)
        => [.. Enumerable.Range(1, count).Select(i => new Customer { Name = "客" + i, City = "城", Remark = null })];

    [Fact]
    public void A_split_batch_has_no_single_text_and_says_which_chunk_it_refused_to_merge()
    {
        var plan = orm().InsertRange(rows(600)).Explain();
        var log = plan.ToLogSql();

        Assert.False(plan.IsSingleStatement);

        // 两段各从 @p0 起号的文本拼在一起不是任何一条能执行的语句，所以这里必须拒而不是"给个近似值"。
        var failure = Checks.Raised<QuickSqlException>(() => plan.ToSql());

        Checks.Has("分成 3 块", failure.Message);

        var atTheEngine = Checks.Raised<QuickSqlException>(() => orm().InsertRange(rows(600)).ToSql(out _));

        Checks.Has("没有单一参数表", atTheEngine.Message);

        // 分节注释让"几条语句"这件事在文本上可见 —— 交付给 DBA 的东西不能看着像一条。
        Checks.Has("-- 块 1：第 0~269 行", log);
        Checks.Has("-- 块 3：第 540~599 行", log);

        // 跨块不原子（§12.4 第 8 项）：不写进 Reason 的话，只有出事的那天才会知道要包事务。
        Checks.Has("跨块不是原子的", plan.Reason);
    }

    [Fact]
    public void A_provider_without_bulk_key_retrieval_degrades_only_the_key_exit()
    {
        // SQL Server 上永远走不到这一跳，所以用一个只掰能力位的转发 provider 让它现场可测（§12.4 第 1 项）。
        var offline = NoBulkKeysProvider.Around(SqlServer.Provider(SqlServer.NoDriverFactory.Instance));
        var orm = ormWith(new QuickSqlAop(), offline);

        var keys = orm.InsertRange(rows(3)).ExplainKeys();

        Assert.Equal(3, keys.Chunks.Count);
        Assert.Equal(1, keys.RowsPerStatement);
        Assert.Equal(1, keys.Chunks[1].FirstRow);
        Checks.Has("退到 L5 逐条", keys.Reason);

        // 退化的是"要回填键"这个请求，不是这个库：不回填时仍然是 L3 一条。
        var plain = orm.InsertRange(rows(3)).Explain();

        Assert.Single(plain.Chunks);
        Assert.Equal(RowsPerStatement, plain.RowsPerStatement);
    }

    // ── §10.1 的钩子规则 ─────────────────────────────────────────────

    [Fact]
    public void The_value_hook_runs_once_per_cell_no_matter_how_many_exits_are_read()
    {
        var fired = 0;
        var aop = new QuickSqlAop();

        aop.AuditValue = _ => fired++;

        // §12.4 第 4 项的地基：AuditValue 会写实体，一个插入对象只能算一次列值。
        // 三个出口（两份文本 + 一份计划）都读过的话刷新次数还是 21，批量与逐条才有可比对象。
        var insert = ormWith(aop).InsertRange(rows(3));

        insert.ToSql();
        insert.Explain();
        insert.ExplainKeys();

        Assert.Equal(3 * Written, fired);

        // 逐条路径同样：每行 7 列 ⇒ 7 次。批量与逐条的刷新次数一致，才是"语义相同、只差往返次数"。
        var perRow = 0;
        var each = new QuickSqlAop();

        each.AuditValue = _ => perRow++;

        foreach (var one in rows(3))
            ormWith(each).Insert(one).ToSql();

        Assert.Equal(3 * Written, perRow);
    }

    [Fact]
    public void A_client_supplied_key_is_offered_to_the_value_hook_while_an_identity_is_not()
    {
        var seen = new List<string>();
        var aop = new QuickSqlAop();

        aop.AuditValue = e => seen.Add(e.Column.MemberName);

        // 判据是「这一列是不是被书写出去的列」，不是「是不是主键」：客户端主键（Guid、雪花号那一派）在
        // 插入这一侧就是写出去的列，而"补号"那道钩子必须碰得到它——真库实测（192.168.0.228 / MySQL 8.4.8）
        // 在这颗列上报出 insert/Id，与这里同一份顺序。自增主键的值归库给，问了也没有落点，所以不问。
        ormWith(aop).Insert(new SessionToken { Id = Guid.NewGuid(), Value = "abc", ExpiresAt = Moment }).ToSql();

        Assert.Equal(["Id", "Value", "ExpiresAt"], seen);

        seen.Clear();
        ormWith(aop).Insert(row(1)).ToSql();

        Assert.DoesNotContain("Id", seen);
    }

    [Fact]
    public void A_filled_value_writes_through_to_the_entity_the_parameter_and_the_replay()
    {
        var stamp = new DateTime(2026, 9, 21, 1, 2, 3, 456);
        var aop = new QuickSqlAop();

        // 默认值时间（Year==1）⇒ 调用方没填 ⇒ 补上。这是 CreatedAt 这类审计列的真实写法。
        aop.AuditValue = e =>
        {
            if (e.Column.MemberName == "CreatedAt" && e.CurrentValue is DateTime { Year: 1 })
                e.SetValue(stamp);
        };

        var draft = new Order { CustomerName = "甲", CustomerId = 1, Amount = 2m, State = OrderState.Draft };

        var plan = ormWith(aop).Insert(draft).Explain();

        Assert.Equal(stamp, draft.CreatedAt);
        Assert.Equal(stamp, plan.Chunks[0].Sql.Parameters[4].Value);

        var trace = Assert.Single(plan.HookReplay);

        Assert.Equal("AuditValue", trace.HookName);
        Assert.Equal(HookCategory.Rewrite, trace.Category);
        Assert.StartsWith("CreatedAt = ", trace.Before);
        Assert.Equal($"CreatedAt = {stamp.ToString(System.Globalization.CultureInfo.InvariantCulture)}", trace.After);
    }

    [Fact]
    public void An_unsubscribed_hook_and_a_hook_that_sets_nothing_both_leave_an_empty_replay()
    {
        Assert.Empty(orm().Insert(row(1)).Explain().HookReplay);

        var aop = new QuickSqlAop();

        aop.AuditValue = _ => { };

        Assert.Empty(ormWith(aop).Insert(row(1)).Explain().HookReplay);

        // 硬规则 2「未订阅即零成本」在代码上是 audit is null 的那个分支：不订阅就不构造事件对象。
        // 分配数在这里量不了（要量的是 bench 的写路径行），离线能钉的是"没改东西 ⇒ 回放里没有这一步"。
        Assert.Null(orm().Aop.AuditValue);
    }

    [Fact]
    public void A_rewriting_hook_that_throws_fails_the_insert_instead_of_writing_a_half_filled_row()
    {
        var aop = new QuickSqlAop();

        aop.AuditValue = _ => throw new InvalidOperationException("租户服务不可用");

        // 硬规则 3：改写类钩子的异常直接向上（Aop.Observe 只兜观察类）。吞掉就是写出一批"填充没发生"的行。
        var failure = Checks.Raised<InvalidOperationException>(() => ormWith(aop).Insert(row(1)).ToSql());

        Assert.Equal("租户服务不可用", failure.Message);

        var binding = new QuickSqlAop();

        binding.ParameterBinding = _ => throw new InvalidOperationException("参数审查不通过");

        Checks.Raised<InvalidOperationException>(() => ormWith(binding).Insert(row(1)).ToSql());
    }

    [Fact]
    public void Parameter_binding_rewrites_by_the_column_a_parameter_belongs_to()
    {
        var columns = new List<string?>();
        var aop = new QuickSqlAop();

        aop.ParameterBinding = e =>
        {
            columns.Add(e.Column?.MemberName);

            if (e.Column?.MemberName == "CustomerName")
                e.SetValue("租户甲");
        };

        var plan = ormWith(aop).InsertRange(rows(2)).Explain();

        var parameters = plan.Chunks[0].Sql.Parameters;

        // 每一颗插入参数都带着列出处 —— 按位置配对会在方言多插一颗参数（分页 hint、并发谓词）时把值改到隔壁列上。
        Assert.Equal(2 * Written, columns.Count);
        Assert.All(columns, name => Assert.NotNull(name));

        Assert.Equal("租户甲", parameters[0].Value);
        Assert.Equal("租户甲", parameters[Written].Value);
        Assert.Equal(1, parameters[1].Value);
        Assert.Equal(2, parameters[Written + 1].Value);

        Assert.Equal(2, plan.HookReplay.Count);
        Assert.All(plan.HookReplay, trace => Assert.Equal("ParameterBinding", trace.HookName));
        Assert.Equal("@p0 = 客户1", plan.HookReplay[0].Before);
        Assert.Equal("@p0 = 租户甲", plan.HookReplay[0].After);

        // 值级改写只动参数不动文本，所以改写过的与没改写的版式一致（文本进黄金样本，值进参数表）。
        Assert.Equal(orm().InsertRange(rows(2)).ToSql(), plan.Chunks[0].Sql.Text);
    }

    [Fact]
    public void Each_chunk_gets_its_own_parameter_numbering_after_the_hook()
    {
        var aop = new QuickSqlAop();

        aop.ParameterBinding = _ => { };

        // 钩子按构建次数触发，而构建是每块一次：两块的参数表各自从 @p0 起号，回放里就得看得出是两条语句。
        var plan = ormWith(aop).InsertRange(rows(RowsPerStatement + 1)).Explain();

        Assert.Equal(2, plan.Chunks.Count);
        Assert.Equal("@p0", plan.Chunks[0].Sql.Parameters[0].Name);
        Assert.Equal("@p0", plan.Chunks[1].Sql.Parameters[0].Name);
        Assert.Equal(RowsPerStatement * Written, plan.Chunks[0].Sql.ParameterCount);
        Assert.Equal(Written, plan.Chunks[1].Sql.ParameterCount);
    }

    // ── §10.6 的校验闸门 ─────────────────────────────────────────────

    [Fact]
    public void Contract_rules_reject_the_whole_batch_before_a_single_character_of_sql_exists()
    {
        var rows = new[]
        {
            row(1),
            new Order { CustomerName = new string('字', 70), CustomerId = 2, Amount = 1m, State = OrderState.Placed, CreatedAt = Moment },
            new Order { CustomerName = new string('字', 70), CustomerId = 3, Amount = 1m, State = OrderState.Placed, CreatedAt = Moment },
        };

        var failure = Checks.Raised<ValidationFailedException>(() => orm().InsertRange(rows).ToSql());

        Assert.Equal(2, failure.Failures.Count);
        Assert.All(failure.Failures, bad => Assert.Equal("length", bad.Rule));

        // 整批只报第一句错的话，调用方要跑三轮才知道有几行坏（§10.6）。行号是这里唯一的定位手段。
        Assert.StartsWith("[第 1 行]", failure.Failures[0].Message);
        Assert.StartsWith("[第 2 行]", failure.Failures[1].Message);
        Assert.Contains("CustomerName 超出列 CustomerName 的 64 字符", failure.Failures[1].Message, StringComparison.Ordinal);

        Checks.Has("ValidationMode.ContractOnly", failure.Message);
        Checks.Has("整批先校验后执行", failure.Message);

        // 单行时行号没有意义，消息里点的是成员与列。
        var single = Checks.Raised<ValidationFailedException>(() => orm().Insert(rows[1]).ToSql());

        Checks.Absent("[第", single.Message);
        Assert.StartsWith("CustomerName", Assert.Single(single.Failures).Message);
    }

    [Fact]
    public void The_batch_rejection_summary_merges_rows_that_broke_on_the_same_rule()
    {
        var rows = new[]
        {
            row(1),
            new Order { CustomerName = new string('字', 70), CustomerId = 2, Amount = 1m, State = OrderState.Placed, CreatedAt = Moment },
            new Order { CustomerName = new string('字', 70), CustomerId = 3, Amount = 1m, State = OrderState.Placed, CreatedAt = Moment },
        };

        var failure = Checks.Raised<ValidationFailedException>(() => orm().InsertRange(rows).ToSql());

        // 写路径喂进摘要的仍是一行一条的明细（行号在这里是唯一的定位手段），折出来的是"这一列这一条尺坏了几行"。
        Checks.Has("共 1 组", failure.Message);
        Checks.Has("CustomerName / length：2 项", failure.Message);
        Assert.Equal(2, failure.Failures.Count);
    }

    [Fact]
    public void Not_null_comes_from_the_contract_not_the_annotation_on_the_DataAnnotations_path()
    {
        // LegacyShipment 的契约全来自 DataAnnotations：这一条同时验「写路径吃的是同一份契约」。
        var failure = Checks.Raised<ValidationFailedException>(() => orm().Insert(new LegacyShipment { Weight = 1m }).ToSql());

        var bad = Assert.Single(failure.Failures);

        Assert.Equal("CarrierCode", bad.MemberName);
        Assert.Equal("required", bad.Rule);
        Assert.Contains("carrier_code", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_only_switch_that_lets_an_oversized_value_through_is_the_validation_mode()
    {
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);
        var registry = new EntitySchemaRegistry(new ContractResolver(provider), new EntitySchemaOptions(ValidationMode.Off));
        var orm = new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = provider, ConnectionString = SqlServer.OfflineConnectionString },
            registry);

        Assert.Equal(ValidationMode.Off, registry.Validator<Order>().Mode);

        // 关掉之后放行的是同一条 SQL：拦截与否由校验器决定，写路径没有自己偷偷加第二道尺子（§16.13）。
        var sql = orm.Insert(row(1)).ToSql();

        Checks.Has("[CustomerName]", sql);

        var passed = new Order { CustomerName = new string('字', 70), CustomerId = 1, Amount = 1m, State = OrderState.Placed, CreatedAt = Moment };

        orm.Insert(passed).ToSql(out var values);

        Assert.Equal(70, ((string) values[0].Value!).Length);

        // 分块与校验是两件不相干的事：关掉校验不会顺手改掉每块行数。
        Assert.Equal(RowsPerStatement, orm.InsertRange(rows(600)).Explain().RowsPerStatement);
    }

    [Fact]
    public void A_null_element_and_a_null_argument_are_both_refused_before_the_build()
    {
        var inCollection = Checks.Raised<QuickSqlException>(() => orm().InsertRange(rows(2).Append((Order) null!).ToArray()).ToSql());

        // 行号是入参下标：报成"第 0 行"就把坏行的位置说反了。
        Checks.Has("第 2 行是 null", inCollection.Message);

        Assert.Throws<ArgumentNullException>(() => orm().Insert((Order) null!).ToSql());
    }

    // ── 发射层：DEFAULT 格 ───────────────────────────────────────────

    [Fact]
    public void A_null_cell_in_the_ast_becomes_DEFAULT_and_shrinks_the_parameter_table()
    {
        // 引擎这一版不产 DEFAULT 格（那要走部分列插入，§16 的 IgnoreColumns），所以这里手工构造 AST：
        // 先把多值语句"某一格落库默认值"的版式钉住，接线时不会改到别人。
        var contract = orm().Schemas.Contract<Order>();
        var columns = new[] { contract.Columns[1], contract.Columns[2] };

        var insert = new InsertStatement
        {
            Table = new TableRef(0, "t0", contract.Table, contract),
            Columns = columns,
            Rows = [[new ParameterValue("老王", columns[0]), null]],
        };

        var writer = new SqlWriter(new StringBuilder(), [], '@');

        SqlServer.Provider(SqlServer.NoDriverFactory.Instance).Dialect.Emit(in insert, writer);

        var sql = writer.ToSqlString();

        Golden.Verify("insert-default-cell", sql);

        Checks.Has("VALUES (@p0, DEFAULT)", sql);
        Assert.Single(writer.Parameters);

        // 内联那一档（给 DBA 的文本）里 DEFAULT 必须还是 DEFAULT，不能被写成 NULL。
        Checks.Has("VALUES (N'老王', DEFAULT)", new DialectSql(sql, writer.Parameters).ToLogSql(SqlServer.Provider(SqlServer.NoDriverFactory.Instance)));
    }

    // ── 执行出口与入口形状 ───────────────────────────────────────────

    [Fact]
    public async Task Zero_rows_produce_no_statement_and_never_open_a_connection()
    {
        var plan = orm().InsertRange<Order>([]).Explain();

        Assert.Empty(plan.Chunks);
        Assert.Equal(0, plan.RowCount);
        Checks.Has("0 行：不产生任何语句", plan.Reason);
        Assert.Equal("-- 0 行：不产生任何语句", plan.ToLogSql());
        Checks.Raised<QuickSqlException>(() => plan.ToSql());

        Assert.Equal(0, await orm().InsertRange<Order>([]).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        // 对照组：同样的 orm 插上真要写的行就会碰到驱动探针。少了这一句，上面那条"0 行不连库"
        // 可以靠"任何执行都直接返回 0"骗过去。探针的拒绝经 TryEnsureLoaded 转成下面的类型，
        // 而证据留在消息里 —— 所以这里既钉类型也钉那句理由。
        var touched = await Assert.ThrowsAsync<DriverNotAvailableException>(
            async () => await orm().Insert(row(1)).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Checks.Has("只生成 SQL 的测试不该碰驱动", touched.Message);
    }

    [Fact]
    public void A_batch_exit_that_cannot_honor_its_own_return_type_refuses_instead_of_returning_the_first_key()
    {
        // 多行时静默返回首行的键是最坏的形状：调用方以为拿到的是"这次插入的 id"。
        var multi = Checks.Raised<QuickSqlException>(
            () => orm().InsertRange(rows(2)).ExecuteIdentityAsync().GetAwaiter().GetResult());

        Checks.Has("只用于单行插入（当前 2 行）", multi.Message);

        var noKeys = Checks.Raised<QuickSqlException>(
            () => orm().Insert(new SessionToken { Id = Guid.NewGuid(), Value = "v" }).ExecuteReturnKeysAsync().GetAwaiter().GetResult());

        Checks.Has("没有自增列", noKeys.Message);
    }

    [Fact]
    public void A_collection_of_entities_is_not_an_entity_insert()
    {
        // Insert(T) 与 Insert(IEnumerable<T>) 同族重载传集合时必然选中前者（identity 转换胜过隐式引用转换），
        // 于是 orm.Insert(list) 会把 List<Customer> 当实体去建契约。改名成 InsertRange 是语言规则逼的，
        // 这条断言防的是"顺手补一个便利重载"把那个坑原样填回来。
        var entityEntries = typeof(QuickSqlOrm).GetMethods().Where(m => m.Name == "Insert").ToArray();

        Assert.Single(entityEntries);
        Assert.All(entityEntries, m => Assert.False(
            typeof(IEnumerable<Customer>).IsAssignableFrom(m.GetParameters()[0].ParameterType),
            $"Insert 的实参类型 {m.GetParameters()[0].ParameterType} 能吃下集合：它会与实体重载争同一句 orm.Insert(list)，而赢的那个是实体。"));

        var batch = typeof(QuickSqlOrm).GetMethod("InsertRange")!;

        Assert.Equal(typeof(IEnumerable<>), batch.GetParameters()[0].ParameterType.GetGenericTypeDefinition());

        // 真跑一遍：批量口拿到的是行数，不是"一个 Customer 实体当一行"。
        Assert.Equal(3, orm().InsertRange(customers(3)).Explain().RowCount);
        Assert.Equal(1, orm().Insert(new Customer { Name = "甲" }).Explain().RowCount);
    }

    /// <summary>取 VALUES 之前那个显式列清单。只服务于本文件的列序断言，不是解析器。</summary>
    static string[] ColumnList(string sql)
    {
        var head = sql[..sql.IndexOf(" VALUES ", StringComparison.Ordinal)];
        var open = head.IndexOf('(');

        return [.. head[(open + 1)..head.LastIndexOf(')')].Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim('[', ']'))];
    }
}
