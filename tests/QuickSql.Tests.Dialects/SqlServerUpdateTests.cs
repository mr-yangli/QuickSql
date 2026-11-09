using System.Data;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Write;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>写侧第二、三条链的离线验证（§5.4 的写侧、§6 示例里那两句 <c>orm.Update&lt;Item&gt;().Set(...)</c>
/// 与 <c>orm.Delete&lt;Cart&gt;().Where(...)</c>、§10.6 的校验落在哪、§10.1 的硬规则）。
/// 与插入链的分工一样：黄金样本钉版式，结构断言钉"凭什么长这样"，全程 <see cref="SqlServer.NoDriverFactory"/>
/// —— 一旦某条用例碰到驱动探针，那是"生成 SQL 的阶段混进了连接依赖"的缺陷信号。</summary>
public sealed class SqlServerUpdateTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    static QuickSqlOrm ormWith(QuickSqlAop aop, IDbProvider? provider = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = provider ?? SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        });

    static IUpdate<Order> Update() => orm().Update<Order>().Set(o => o.CustomerName, "新名字").Where(o => o.Id == 7);

    // ── 版式 ──────────────────────────────────────────────────────────

    [Fact]
    public void A_set_of_two_columns_is_one_T_SQL_statement_over_exactly_those_columns()
    {
        var sql = orm().Update<Order>()
            .Set(o => o.CustomerName, "新名字")
            .Set(o => o.State, OrderState.Paid)
            .Where(o => o.Id == 7)
            .ToSql();

        Golden.Verify("update-two-sets", sql);

        // T-SQL 的 UPDATE 目标不接受别名声明（`UPDATE [t0] ... FROM [表] AS [t0]`），SET 左侧一律裸列名。
        Checks.Has("UPDATE [t0] SET [CustomerName] = @p0, [State] = @p1 FROM [QuickSqlOrder] AS [t0] WHERE [t0].[Id] = @p2", sql);
    }

    [Fact]
    public void Only_the_named_columns_enter_the_statement()
    {
        var sql = orm().Update<Order>()
            .Set(o => o.Note, "只改备注")
            .Where(o => o.Id == 7)
            .ToSql(out var parameters);

        Assert.Equal(["@p0", "@p1"], parameters.Select(p => p.Name).ToArray());

        // SET 那一颗带列出处（引擎亲手把 ColumnContract 交给 ParameterValue），WHERE 那一颗不带 ——
        // 条件的值是读侧同一个归约器生成的，那里只给"直连列"的一侧定尺子。见
        // The_set_value_carries_its_column_while_a_condition_value_does_not。
        Assert.Equal(["Note", null], parameters.Select(p => p.Hint?.MemberName).ToArray());

        // 没点名的列不进语句：这是 UPDATE 与"按实体整行写"的分界，也是这条链不跑整实体校验的理由。
        foreach (var name in new[] { "CustomerName", "CustomerId", "Amount", "State", "CreatedAt", "PaidAt" })
            Checks.Absent($"[{name}]", sql);
    }

    [Fact]
    public void An_expression_value_stays_in_the_database_and_references_the_aliased_row()
    {
        var sql = orm().Update<Order>()
            .Set(o => o.Amount, o => o.Amount + 1m)
            .Where(o => o.Id == 7)
            .ToSql();

        Golden.Verify("update-expression", sql);

        // 读改写在库里做，所以两个并发请求不会互相覆盖 —— 应用侧先读后写的那个版本会。
        Checks.Has("SET [Amount] = [t0].[Amount] + @p0", sql);
    }

    [Fact]
    public void A_physical_delete_names_no_columns_and_carries_only_the_condition_values()
    {
        var sql = orm().Delete<Order>().Where(o => o.CreatedAt < new DateTime(2026, 1, 1)).ToSql(out var parameters);

        Golden.Verify("delete-by-condition", sql);

        Assert.Single(parameters);
        Assert.Equal(DbType.DateTime2, parameters[0].DbType);
        Assert.Null(parameters[0].Hint);

        Checks.Has("DELETE FROM [t0] FROM [QuickSqlOrder] AS [t0] WHERE", sql);
    }

    [Fact]
    public void The_inlined_text_prefixes_unicode_literals_the_same_way_the_select_side_does()
    {
        var log = Update().ToLogSql();

        Golden.Verify("update-log", log);

        // §10.4：SQL Server 的 varchar 会按 ANSI 代码页丢中文，所以内联文本必须带 N 前缀。
        Checks.Has("N'新名字'", log);
        Checks.Absent("@p", log);
    }

    // ── §5.5 单一真相：条件那一半与读侧是同一个归约器 ──────────────────

    [Fact]
    public void Two_Where_calls_fold_into_the_same_conjunction_the_query_side_produces()
    {
        var updated = orm().Update<Order>()
            .Set(o => o.State, OrderState.Shipped)
            .Where(o => o.CustomerId == 42)
            .Where(o => o.Amount > 10m)
            .ToLogSql();

        var selected = orm().Select<Order>()
            .Where(o => o.CustomerId == 42)
            .Where(o => o.Amount > 10m)
            .ToLogSql();

        // 比的是 WHERE 之后的整段子句：两次 Where 折叠成一条 AND 链、括号由同一个发射器决定，
        // 这两件事在写侧只有一份实现，所以它必须与读侧逐字符相同（内联文本，参数名不参与比较）。
        Assert.Equal(After(selected, " WHERE "), After(updated, " WHERE "));
    }

    [Fact]
    public void A_condition_that_needs_a_join_is_refused_on_the_write_side_for_the_same_reason()
    {
        // 写侧没有 FROM 序列可扩（联表 UPDATE 是 SetSource/派生表那一支，§14），所以引用导航属性必须抛，
        // 而不是静默少筛条件 —— 抛点与消息都来自读侧那一个 UntranslatableExpressionException。
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Update<Order>().Set(o => o.State, OrderState.Paid).Where(o => o.Customer!.Name == "甲").ToSql());

        Checks.Has("不是列", failure.Message);
    }

    [Fact]
    public void A_column_read_that_maps_to_a_function_translates_here_too()
    {
        var sql = orm().Delete<Order>().Where(o => o.Note!.Length > 3).ToSql();

        Checks.Has("LEN([t0].[Note])", sql);
    }

    // ── 这条链自己该拦的东西 ─────────────────────────────────────────

    [Fact]
    public void An_update_without_a_where_is_refused_while_an_explicit_full_table_one_is_not()
    {
        var missing = Checks.Raised<QuickSqlException>(
            () => orm().Update<Order>().Set(o => o.State, OrderState.Cancelled).ToSql());

        Checks.Has("没有 WHERE", missing.Message);
        Checks.Has(".Where(o => true)", missing.Message);

        // 三个出口同一句话：ToSql 拦住了不代表 Explain 与执行也拦住（执行口才是唯一要紧的那一个）。
        Checks.Has("没有 WHERE", Checks.Raised<QuickSqlException>(
            () => orm().Update<Order>().Set(o => o.State, OrderState.Cancelled).Explain().ToSql()).Message);
        Checks.Has("没有 WHERE", Checks.Raised<QuickSqlException>(
            () => orm().Update<Order>().Set(o => o.State, OrderState.Cancelled).ExecuteAffrowsAsync().GetAwaiter().GetResult()).Message);

        // 逃生舱要真的能用：显式写 o => true 得到一条语句，而不是换一个地方再抛一次。
        var onPurpose = orm().Update<Order>().Set(o => o.State, OrderState.Cancelled).Where(o => true).ToSql();

        Golden.Verify("update-without-condition", onPurpose);

        Checks.Has("SET [State] = @p0 FROM [QuickSqlOrder] AS [t0] WHERE ", onPurpose);
        Checks.Has("DELETE FROM [t0] FROM [QuickSqlOrder] AS [t0] WHERE ", orm().Delete<Order>().Where(o => true).ToSql());
    }

    [Fact]
    public void A_delete_without_a_where_is_refused_by_every_exit()
    {
        var missing = Checks.Raised<QuickSqlException>(() => orm().Delete<Order>().Explain());

        Checks.Has("没有 WHERE", missing.Message);
        Checks.Has("没有 WHERE", Checks.Raised<QuickSqlException>(() => orm().Delete<Order>().ToSql(out _)).Message);
        Checks.Has("没有 WHERE", Checks.Raised<QuickSqlException>(() => orm().Delete<Order>().ToLogSql()).Message);
    }

    [Fact]
    public void The_identity_column_and_the_unmapped_members_never_reach_SET()
    {
        var identity = Checks.Raised<QuickSqlException>(() => orm().Update<Order>().Set(o => o.Id, 1).Where(o => o.Id == 2).ToSql());

        Checks.Has("自增列 Id 不接受写", identity.Message);

        // [NotMapped] 的成员在列契约之外（§10.2），所以它连"这一列"都算不上。
        var notMapped = Checks.Raised<QuickSqlException>(
            () => orm().Update<LegacyShipment>().Set(s => s.TrackingUrl, "https://x").Where(s => s.ShipmentId == 1).ToSql());

        Checks.Has("没有名为 TrackingUrl 的列", notMapped.Message);

        // 算式放在第一个参数上：那是最容易写反的一格（把条件当列名报），所以要说清两件事各在哪个参数。
        var notAColumn = Checks.Raised<QuickSqlException>(
            () => orm().Update<Order>().Set(o => o.CustomerName!.Length, 3).Where(o => o.Id == 2).ToSql());

        Checks.Has("算式放在第二个参数上", notAColumn.Message);
    }

    [Fact]
    public void A_column_set_twice_is_refused_at_the_second_call()
    {
        // 库里那句 "Column 'X' is specified multiple times" 不带行号也不说哪一次重复，所以在这里拦。
        var duplicate = Checks.Raised<QuickSqlException>(
            () => orm().Update<Order>().Set(o => o.State, OrderState.Paid).Set(o => o.State, OrderState.Shipped).Where(o => o.Id == 1).ToSql());

        Checks.Has("被设置了两次", duplicate.Message);

        // 表达式那一档不算重复设置，它算的是"基于当前值改" —— 同一条链上写两次仍然要拦。
        var twice = Checks.Raised<QuickSqlException>(
            () => orm().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Set(o => o.Amount, o => o.Amount + 2m).Where(o => o.Id == 1).ToSql());

        Checks.Has("被设置了两次", twice.Message);
    }

    [Fact]
    public void A_statement_with_no_columns_to_set_says_so_instead_of_emitting_an_empty_SET()
    {
        var empty = Checks.Raised<QuickSqlException>(() => orm().Update<Order>().Where(o => o.Id == 1).ToSql());

        Checks.Has("一个 SET 列都没有", empty.Message);
    }

    // ── §10.6：校验落在哪一格上 ──────────────────────────────────────

    [Fact]
    public void A_constant_value_is_measured_by_the_same_ruler_as_an_inserted_one()
    {
        var failure = Checks.Raised<ValidationFailedException>(
            () => orm().Update<Order>().Set(o => o.CustomerName, new string('字', 70)).Where(o => o.Id == 1).ToSql());

        var bad = Assert.Single(failure.Failures);

        Assert.Equal("length", bad.Rule);
        Assert.Contains("CustomerName 超出列 CustomerName 的 64 字符", bad.Message, StringComparison.Ordinal);
        Checks.Has("语句还没有生成", failure.Message);

        // 消息与插入链同源：同一格值在两条链上报的是同一句话（§10.6 要的正是"尺子只有一把"）。
        var onInsert = Assert.Single(Assert.Throws<ValidationFailedException>(
            () => orm().Insert(new Order { CustomerName = new string('字', 70), Id = 1 }).ToSql()).Failures);

        Assert.Equal(bad.Message, onInsert.Message);
    }

    [Fact]
    public void The_only_switch_that_lets_an_oversized_value_through_is_still_the_validation_mode()
    {
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);
        var registry = new EntitySchemaRegistry(new ContractResolver(provider), new EntitySchemaOptions(ValidationMode.Off));
        var orm = new QuickSqlOrm(new QuickSqlOrmOptions { Provider = provider, ConnectionString = SqlServer.OfflineConnectionString }, registry);

        Assert.Equal(ValidationMode.Off, registry.Validator<Order>().Mode);

        var sql = orm.Update<Order>().Set(o => o.CustomerName, new string('字', 70)).Where(o => o.Id == 1).ToSql(out var parameters);

        Assert.Equal(70, ((string) parameters[0].Value!).Length);
        Checks.Has("[CustomerName]", sql);
    }

    [Fact]
    public void A_null_for_a_not_null_column_is_refused_while_a_nullable_column_accepts_it()
    {
        // 类型系统已经拦掉了大部分（直接写字面量 null 推不出 TProp），剩下的从这个可空局部变量进来 ——
        // 那一格照样要过 required 那把尺子。
        string? cleared = null;

        var required = Checks.Raised<ValidationFailedException>(
            () => orm().Update<LegacyShipment>().Set(s => s.CarrierCode, cleared).Where(s => s.ShipmentId == 1).ToSql());

        Assert.Equal("required", Assert.Single(required.Failures).Rule);

        string? note = null;

        orm().Update<Order>().Set(o => o.Note, note).Where(o => o.Id == 1).ToSql(out var parameters);

        Assert.Equal(DBNull.Value, parameters[0].Value);
    }

    [Fact]
    public void The_expression_variant_is_not_measured_because_there_is_no_value_to_measure()
    {
        // 值在库里算，应用侧没有任何东西可以量 —— 拦它等于拦"我不相信这条算式"。
        // 长度越界这一类由库的列约束收尾，这是这一档的代价，写在 §14。
        orm().Update<Order>().Set(o => o.CustomerName, o => o.CustomerName + "后缀").Where(o => o.Id == 1).ToSql();
    }

    [Fact]
    public void The_update_chain_runs_no_value_hook_because_it_reads_no_entities()
    {
        var fired = 0;
        var aop = new QuickSqlAop();

        aop.AuditValue = _ => fired++;

        ormWith(aop).Update<Order>().Set(o => o.State, OrderState.Paid).Where(o => o.Id == 1).ToSql();
        ormWith(aop).Delete<Order>().Where(o => o.Id == 1).ToSql();

        // 插入链的取值段会逐格问 AuditValue；这两条链上没有"整行的值"这个东西可读，
        // 所以钩子一次都不该跑。跑一次就是"凭空往库里写了调用方没说的列"。
        // 分界不是"读侧 vs 写侧"，也不是"Update 这个类名"，而是<em>这条链手上有没有整行实体</em>：
        // 有整行的那条（orm.UpdateRange(实体集)）跑，见 The_row_update_chain_runs_the_value_hook_once_per_cell。
        Assert.Equal(0, fired);
    }

    // ── §10.1 的钩子规则：UpdateRange 那条链的取值段 ───────────────────

    /// <summary><see cref="Order"/> 契约 8 列，<c>Id</c> 是自增主键 ⇒ 整行更新写出去 7 列、
    /// 定位用 1 列。审计触发次数与 SET 清单都挂在这个数上，与 <c>SqlServerInsertTests</c> 同一把尺。</summary>
    const int Written = 7;

    static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, 890);

    static Order row(int n) => new()
    {
        Id = n,
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = Moment,
        Note = "旧备注",
    };

    static Order[] rows(int count) => [.. Enumerable.Range(1, count).Select(row)];

    [Fact]
    public void The_row_update_chain_runs_the_value_hook_once_per_cell_across_both_texts()
    {
        var fired = 0;
        var aop = new QuickSqlAop();

        aop.AuditValue = _ => fired++;

        // 与插入链同一条地基（§12.4 第 4 项）：AuditValue 会写实体，一批实体只能算一次列值。
        // 这里读三个出口——两次文本加一次换档后的文本，格子仍然是那一份。
        var bulk = ormWith(aop).UpdateRange(rows(3));

        bulk.Explain();
        bulk.Explain();
        bulk.WithBulk(BulkWriteMethod.SingleRow).Explain();

        Assert.Equal(3 * Written, fired);
    }

    [Fact]
    public void A_value_filled_on_the_row_update_reaches_the_entity_the_parameter_and_the_replay()
    {
        var stamp = new DateTime(2026, 9, 21, 1, 2, 3, 456);
        var aop = new QuickSqlAop();

        // 修改时间这一族的真实写法：调用方没填（Year==1）就补上，填过就不动。
        aop.AuditValue = e =>
        {
            if (e.Column.MemberName == "PaidAt" && e.CurrentValue is null)
                e.SetValue(stamp);
        };

        var order = row(1);
        var plan = ormWith(aop).UpdateRange([order]).Explain();

        Assert.Equal(stamp, order.PaidAt);

        var parameters = plan.Commands[0].Parameters;
        Assert.Equal(stamp, parameters.Single(p => p.Hint?.MemberName == "PaidAt").Value);

        // 回放里看得见这一步，且它排在发射段那几步之前（取值段先跑，§10.1 硬规则 1）。
        var trace = Assert.Single(plan.HookReplay);

        Assert.Equal("AuditValue", trace.HookName);
        Assert.Equal(HookCategory.Rewrite, trace.Category);
        Assert.Equal(0, trace.Step);
        Assert.StartsWith("PaidAt = ", trace.Before);
        Assert.Equal($"PaidAt = {stamp.ToString(System.Globalization.CultureInfo.InvariantCulture)}", trace.After);
    }

    [Fact]
    public void The_two_row_shaped_chains_ask_the_same_columns_and_differ_only_by_kind()
    {
        var update = new List<string>();
        var insert = new List<string>();
        var aopUpdate = new QuickSqlAop();
        var aopInsert = new QuickSqlAop();

        aopUpdate.AuditValue = e => update.Add($"{e.OperationKind}/{e.Column.MemberName}");
        aopInsert.AuditValue = e => insert.Add($"{e.OperationKind}/{e.Column.MemberName}");

        // 两条有整行实体的链各问一遍。种类是唯一能把它们分开的东西（抬头那一句），
        // 而列清单与顺序逐字相同——筛列的判据在两条链上是同一套：主键、库里给值的、并发列都不问。
        ormWith(aopUpdate).UpdateRange([row(1)]).Explain();
        ormWith(aopInsert).Insert(row(1)).ToSql();

        Assert.Equal(Written, update.Count);
        Assert.All(update, entry => Checks.Has("update/", entry));
        Assert.DoesNotContain("update/Id", update);
        Assert.All(insert, entry => Checks.Has("insert/", entry));
        Assert.Equal(insert.Select(name => name.Split('/')[1]), update.Select(name => name.Split('/')[1]));
    }

    // ── §10.1 硬规则 1：改写要能在 Explain() 里回放 ───────────────────

    [Fact]
    public void The_set_value_carries_its_column_while_a_condition_value_does_not()
    {
        var columns = new List<string?>();
        var aop = new QuickSqlAop();

        aop.ParameterBinding = e =>
        {
            columns.Add(e.Column?.MemberName);

            if (e.Column?.MemberName == "CustomerName")
                e.SetValue("租户甲");
        };

        var plan = ormWith(aop).Update<Order>().Set(o => o.CustomerName, "新名字").Where(o => o.Id == 7).Explain();

        // SET 在前、WHERE 在后（T-SQL 的子句顺序），所以参数表里也是这个顺序。
        // 出处只有 SET 那一半有：条件的值是读侧同一个归约器给的，那里只给"直连列"定尺子（§10.3），
        // 而读侧本来就不跑这道钩子，所以这件事到今天才第一次有后果 —— 账挂 §14。
        Assert.Equal(["CustomerName", null], columns);
        Assert.Equal("租户甲", plan.Sql.Parameters[0].Value);
        Assert.Equal(7, plan.Sql.Parameters[1].Value);

        var trace = Assert.Single(plan.HookReplay);

        Assert.Equal("ParameterBinding", trace.HookName);
        Assert.Equal("@p0 = 新名字", trace.Before);
        Assert.Equal("@p0 = 租户甲", trace.After);

        // 值级改写不动文本：版式进黄金样本，值进参数表。
        Assert.Equal(orm().Update<Order>().Set(o => o.CustomerName, "新名字").Where(o => o.Id == 7).ToSql(), plan.Sql.Text);
    }

    [Fact]
    public void An_unsubscribed_hook_leaves_an_empty_replay_on_both_chains()
    {
        Assert.Empty(Update().Explain().HookReplay);
        Assert.Empty(orm().Delete<Order>().Where(o => o.Id == 1).Explain().HookReplay);
        Assert.Null(orm().Aop.ParameterBinding);
    }

    [Fact]
    public void A_rewriting_hook_that_throws_fails_the_statement_before_anything_is_sent()
    {
        var aop = new QuickSqlAop();

        aop.ParameterBinding = _ => throw new InvalidOperationException("参数审查不通过");

        Checks.Raised<InvalidOperationException>(() => ormWith(aop).Update<Order>().Set(o => o.Note, "x").Where(o => o.Id == 1).ToSql());
        Checks.Raised<InvalidOperationException>(() => ormWith(aop).Delete<Order>().Where(o => o.Id == 1).ToSql());
    }

    // ── Reason：现场排障先看的那一句 ─────────────────────────────────

    [Fact]
    public void The_delete_reason_has_to_say_that_rows_really_go_away()
    {
        var reason = orm().Delete<Order>().Where(o => o.Id == 1).Explain().Reason;

        // 这一格自陈的是"这一句发的是 DELETE"，而它必须同时说清另一件事：想让删除置位的人该去哪里开
        //（§10.7 约束 3 的那入口子 v0.38 第十七笔落了，所以这句话的出处从"还没有落地"换成了"在册时会换成 UPDATE"）。
        // 判据按文本而不是按订阅：一个人没开软删时读到"行不在了"才是对的。
        Checks.Has("物理删除", reason);
        Checks.Has("orm.Behaviors.SoftDelete 在册时这一句会被换成 UPDATE 置位", reason);
        Checks.Has("订一颗 CudBuilding", reason);
    }

    [Fact]
    public void The_update_reason_names_the_columns_it_writes()
    {
        var reason = orm().Update<Order>()
            .Set(o => o.CustomerName, "甲")
            .Set(o => o.PaidAt, new DateTime(2026, 1, 1))
            .Where(o => o.Id == 1)
            .Explain()
            .Reason;

        Checks.Has("SET 2 列（CustomerName、PaidAt）", reason);
    }

    // ── 不连库（§3）与执行出口 ───────────────────────────────────────

    [Fact]
    public async Task Building_never_touches_the_driver_while_executing_has_to()
    {
        var update = orm().Update<Order>().Set(o => o.State, OrderState.Paid).Where(o => o.Id == 1);

        _ = update.Explain().ToLogSql();
        _ = update.ToSql();

        await Assert.ThrowsAsync<DriverNotAvailableException>(
            async () => await update.ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        var delete = orm().Delete<Order>().Where(o => o.Id == 1);

        _ = delete.ToLogSql();

        await Assert.ThrowsAsync<DriverNotAvailableException>(
            async () => await delete.ExecuteAffrowsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Both_plans_carry_the_ast_the_dialect_actually_read()
    {
        var plan = Update().Explain();

        Assert.Equal("QuickSqlOrder", plan.Ast.Table.Table.Name);
        Assert.Equal("t0", plan.Ast.Table.Alias);
        Assert.Single(plan.Ast.Sets);
        Assert.Empty(plan.Ast.Returning);

        var deleted = orm().Delete<Order>().Where(o => o.Id == 7).Explain();

        Assert.NotNull(deleted.Ast.Where);
        Assert.Empty(deleted.Ast.Joins);
    }

    // ── 按实体那一行的两个入口（orm.Update(entity) ∶ orm.Delete(entity)） ────

    [Fact]
    public void An_entity_update_writes_the_columns_that_carry_a_value_and_locates_by_the_key()
    {
        // row(1) 的 PaidAt 是 null —— 这一档读到的就是"这一列我没说"，那一格整列不进语句。
        var sql = orm().Update(row(1)).ToSql(out var parameters);

        Golden.Verify("update-by-entity", sql);

        Checks.Has("WHERE [t0].[Id] = @p6", sql);
        Assert.Equal(7, parameters.Count);

        // 键位带列出处：它是按主键定位的那三条路共用的形状（批量链与跟踪器都带），
        // 不带 Hint 的是链式 .Where(...) 那种"调用方自己写的条件"。
        Assert.Equal("Id", parameters[^1].Hint?.MemberName);
        Assert.Equal(["CustomerName", "CustomerId", "Amount", "State", "CreatedAt", "Note", "Id"],
            parameters.Select(p => p.Hint!.MemberName).ToArray());

        Checks.Absent("[PaidAt]", sql);
    }

    [Fact]
    public void A_column_the_entity_left_null_is_not_said_while_the_chain_still_can_say_NULL()
    {
        string? cleared = null;

        // 同一只实体、同一列：按实体那一档不说这一列，链式那一档把它真写成 NULL。
        // 两扇门可以互换的话，"清空备注"这句话就没有任何一种写法是安全的。
        var byEntity = orm().Update(new Order { Id = 3, CustomerName = null }).ToSql();

        Checks.Absent("[CustomerName]", byEntity);

        var byChain = orm().Update<Order>().Set(o => o.CustomerName, cleared).Where(o => o.Id == 3).ToSql();

        Checks.Has("[CustomerName] = @p0", byChain);

        // 而值类型列永远带来值：0 不是"没填"，所以 Amount 每趟都进 SET。
        Checks.Has("[Amount]", byEntity);
    }

    [Fact]
    public void The_entity_and_the_batch_door_ask_the_hook_the_same_columns_in_the_same_order()
    {
        var entityDoor = new List<string>();
        var batchDoor = new List<string>();
        var aopEntity = new QuickSqlAop();
        var aopBatch = new QuickSqlAop();

        aopEntity.AuditValue = e => entityDoor.Add($"{e.OperationKind}/{e.Column.MemberName}");
        aopBatch.AuditValue = e => batchDoor.Add($"{e.OperationKind}/{e.Column.MemberName}");

        // 筛列的判据只有一把：主键、库里给值的列、并发列都不问。PaidAt 是 null 也照样被问过一遍
        // ——钩子先跑、null 判在后，所以"补修改时间"的订阅方填上的那一格进得了 SET。
        ormWith(aopEntity).Update(row(1));
        ormWith(aopBatch).UpdateRange([row(1)]).Explain();

        Assert.Equal(Written, entityDoor.Count);
        Assert.Equal(batchDoor, entityDoor);
    }

    [Fact]
    public void An_audit_filled_value_enters_the_SET_of_an_entity_update_and_replays()
    {
        var stamp = new DateTime(2026, 9, 21, 1, 2, 3, 456);
        var aop = new QuickSqlAop();

        aop.AuditValue = e =>
        {
            if (e.Column.MemberName == "PaidAt" && e.CurrentValue is null)
                e.SetValue(stamp);
        };

        var order = row(1);
        var plan = ormWith(aop).Update(order).Explain();

        Assert.Equal(stamp, order.PaidAt);
        Checks.Has("[PaidAt]", plan.ToSql());
        Assert.Equal(stamp, plan.Sql.Parameters.Single(p => p.Hint?.MemberName == "PaidAt").Value);

        // 取值段那一步排在发射段之前、步号从 0 起，与 UpdateRange 的 Explain() 同一份回放形状。
        var trace = Assert.Single(plan.HookReplay);

        Assert.Equal("AuditValue", trace.HookName);
        Assert.Equal(0, trace.Step);
    }

    [Fact]
    public void A_row_that_said_nothing_at_all_is_refused_because_an_empty_SET_is_not_a_statement()
    {
        // Customer 除主键以外全是可空列：一只只有 Id 的实体等于什么都没说。
        var empty = Checks.Raised<QuickSqlException>(() => orm().Update(new Customer { Id = 7 }).ToSql());

        Checks.Has("没有一列带值", empty.Message);
        Checks.Has("orm.Update<Customer>()", empty.Message);
    }

    [Fact]
    public void A_key_that_was_never_assigned_is_refused_before_anything_is_sent()
    {
        // Id 是自增主键，而 0 是它"还没被库给值"的那个形态：这一句定位不到任何行，
        // 却会安静地报告 0 行。判据与 UpdateRange ∶ 一句 upsert 共用同一份（StatementEmitter.IsUnassignedKey）。
        var unset = Checks.Raised<QuickSqlException>(() => orm().Update(new Order { Note = "改点什么" }).ToSql());

        Checks.Has("没给值的那个默认形态", unset.Message);
        Checks.Has("orm.Insert", unset.Message);

        var deleted = Checks.Raised<QuickSqlException>(() => orm().Delete(new Order()).Explain());

        Checks.Has("没给值的那个默认形态", deleted.Message);
    }

    [Fact]
    public void A_batch_handed_to_a_single_row_entrance_is_pointed_at_the_range_chain()
    {
        // Insert 那一族记过的坑在这里长同一颗：集合递给收实体的门，T 会被推成 List<Order>
        // 而去建一张不存在的表的契约。拦在入口而不是等契约解析层报出别的话。
        var update = Checks.Raised<QuickSqlException>(() => orm().Update(rows(2)));

        Checks.Has("UpdateRange(rows)", update.Message);

        var delete = Checks.Raised<QuickSqlException>(() => orm().Delete(rows(2)));

        Checks.Has("DeleteRange(rows)", delete.Message);

        // 对照面要成立：整批那一档收同一批行是正常工作的，指路指过去不落空。
        Checks.Has("[QuickSqlOrder]", orm().UpdateRange(rows(2)).Explain().ToLogSql());
    }

    [Fact]
    public void A_composite_key_row_is_located_column_by_column_where_the_batch_door_refuses_it()
    {
        var ledger = new Ledger { Book = 1, Line = 2 };

        Golden.Verify("delete-by-entity-composite-key", orm().Delete(ledger).ToSql());

        var sql = orm().Delete(ledger).ToLogSql();

        Checks.Has("[Book] = 1", sql);
        Checks.Has("[Line] = 2", sql);

        _ = orm().Delete(ledger).ToSql(out var keys);

        Assert.Equal(2, keys.Count);
        Assert.Equal(["Book", "Line"], keys.Select(p => p.Hint!.MemberName).ToArray());

        // 两扇门对复合键的答案不同，而各有权衡：批量那句拒的是"IN 清单装不下元组"，
        // 那一句在单行上根本不成立 —— 所以这里支持、那里拒，不是同一件事的两种写法。
        Checks.Has("复合键", Checks.Raised<QuickSqlException>(() => orm().DeleteRange([ledger]).Explain()).Message);
    }

    [Fact]
    public void A_split_entity_is_written_to_the_shard_its_own_key_names()
    {
        var entry = new ShardLog { Id = new Guid("11111111-1111-1111-1111-111111111111"), Level = 2, OccurredAt = Moment };

        var sql = orm().Update(entry).ToSql();

        Golden.Verify("update-by-entity-split-shard", sql);

        // 片的换算抄插入链那份 Resolve：分片键在实体身上，所以这一档不需要 SplitTable。
        Checks.Has("[QuickSqlShardLog_202603]", sql);

        // 而批量那一档今天没有按片分组的账，拒的是同一件事的另一种现场。
        Checks.Has("一批行会散在几张片上", Checks.Raised<QuickSqlException>(() => orm().UpdateRange([entry]).Explain()).Message);
    }

    [Fact]
    public void An_entity_delete_still_buys_the_soft_delete_rewrite_and_the_global_filter()
    {
        var target = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
        });

        target.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () => Moment);
        target.Filters.Add<Ticket>(t => t.TenantId == 3);

        var plan = target.Delete(ticket(7)).Explain();

        Golden.Verify("delete-by-entity-soft", plan.ToSql());

        // 改的是形状而不是命中范围：置位两列由规则给，WHERE 仍是主键 ∧ 过滤器。
        Checks.Has("SET", plan.ToSql());
        Checks.Has("Behaviors.SoftDelete", plan.Reason);
        Checks.Has("全局过滤器", plan.Reason);
        Checks.Has("[t0].[Id] = @p", plan.ToSql());
        Checks.Has("[t0].[TenantId] = @p", plan.ToSql());

        static Ticket ticket(int id) => new() { Id = id, Title = "工单" + id, TenantId = 3 };
    }

    [Fact]
    public void The_entity_door_still_is_the_same_chain_so_a_second_condition_narrows_it()
    {
        // 交回的是 IUpdate<T>：主键已经在那儿，再叠一句 .Where 只是往末尾加 AND，不换门也收窄得了。
        var sql = orm().Update(row(1)).Where(o => o.State != OrderState.Cancelled).ToLogSql();

        Checks.Has("AND [t0].[State] <> 9", sql);

        // 实体没带的那一列可以再点一句；PaidAt 在上面那行 row(1) 上是 null，所以 SET 里没有它。
        var filled = orm().Update(row(1)).Set(o => o.PaidAt, Moment).ToLogSql();

        Checks.Has("[PaidAt] = ", filled);

        // 而实体带着值的那一列再点一次会被"设置了两次"那道守卫拦下：本家答案不是后写的覆盖先写的，
        // 是让调用方说清哪个值算（要么改实体，要么整句改走链式）。混合现场（整只实体 + 一列在库里算）
        // 在这一档不支持，那是链式的形状。
        var twice = Checks.Raised<QuickSqlException>(() => orm().Update(row(1)).Set(o => o.Note, "再改一次").ToSql());

        Checks.Has("被设置了两次", twice.Message);
    }

    static string After(string text, string needle)
    {
        var at = text.IndexOf(needle, StringComparison.Ordinal);

        Assert.True(at >= 0, $"没找到 {needle}：{Environment.NewLine}{text}");

        return text[at..].Trim();
    }
}
