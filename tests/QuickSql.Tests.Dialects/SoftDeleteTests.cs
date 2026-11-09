using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Write;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§10.7 约束 3 的那入口子（v0.38 第十七笔）：<c>orm.Behaviors.SoftDelete</c> 把删除换成
/// 「置位那一格写 true + 时间那一格记一颗」的 UPDATE。
///
/// <para><b>三处删除出口都在这一册里判到文本层</b>：链式 <c>orm.Delete&lt;T&gt;()</c>、批量
/// <c>orm.DeleteRange&lt;T&gt;(rows)</c> 各判得到（一条语句只欠连接的是执行，不是文本）；
/// <c>SaveChanges</c> 的摘行判不到，因为那条路上没有"先看一眼再发"的出口 —— 它的见证在集成册
/// （<c>SoftDeletePathTests</c>），那里连库读回"行还在、那一格是 1"。</para>
///
/// <para><b>与 <see cref="FilterTests"/> 的分工</b>：那一册判的是"谓词从哪儿来、贴进哪一段"，
/// 本册判的是"句子的形状换了"。两者叠在一起的那一格（过滤器进 UPDATE 的 <c>WHERE</c>、而且排在调用方
/// 条件之后）在本册只有一条用例，因为它是两只对象<em>共用同一段 WHERE</em> 的接缝，而不是一处新规则。</para>
///
/// <para><b>靶子是 <see cref="Ticket"/> 而不是 <see cref="Order"/></b>：理由与 <see cref="FilterTests"/>
/// 逐字相同 —— <c>Order</c> 的列序被几十份黄金样本钉死，而这里要往 <c>SET</c> 里放的两列（<c>Deleted</c>、
/// <c>DeletedAt</c>）恰好是 <c>Ticket</c> 独有的那两列（<c>Model.cs</c> 里那句"Behaviors 将来要写的就是这两列"
/// 说的就是本册）。<see cref="LooseNote"/> 是接口键那一档"实现类缺列"的现场。</para>
///
/// <para><b>每条用例自己造一只 orm</b>：册子挂在实例上（一个实例一份），所以本册没有跨用例的可变状态，
/// 也就没有"跑序变了结果变了"那一类红。</para></summary>
public sealed class SoftDeleteTests
{
    static readonly DateTime Moment = new(2026, 10, 7, 13, 14, 15, 160);

    static QuickSqlOrm orm() => SqlServer.Orm();

    static QuickSqlOrm ormWith(QuickSqlAop aop)
        => new(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        });

    /// <summary>两格都注册的 orm（<c>deleted</c> + <c>at</c>），时钟钉成 <see cref="Moment"/>：
    /// 文本与参数因此都是确定值，可以进黄金样本。</summary>
    static QuickSqlOrm soft(QuickSqlAop? aop = null)
    {
        var target = aop is null ? orm() : ormWith(aop);

        target.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () => Moment);

        return target;
    }

    /// <summary>只注册 <c>deleted</c> 那一格。</summary>
    static QuickSqlOrm flagOnly()
    {
        var target = orm();

        target.Behaviors.SoftDelete<Ticket>(t => t.Deleted);

        return target;
    }

    static Ticket ticket(int id) => new() { Id = id, Title = "工单" + id, TenantId = 3 };

    static Ticket[] tickets(int n) => [.. Enumerable.Range(1, n).Select(ticket)];

    // ── 形状：发出去的句子换了 ────────────────────────────────────────

    [Fact]
    public void A_registered_behavior_sends_an_update_while_the_where_stays_the_callers_one()
    {
        var chain = soft().Delete<Ticket>().Where(t => t.Id == 7);

        var sql = chain.ToSql();

        Golden.Verify("soft-delete-update", sql);

        // SET 左侧裸列名、目标带别名声明 —— 与 UPDATE 链同一条版式（T-SQL 的写目标不接受别名声明那一段，
        // 见 SqlServerUpdateTests 里钉住的那句）。参数序先 SET 后 WHERE：那是发射段的既有纪律，不是新规则。
        Checks.Has("UPDATE [t0] SET [Deleted] = @p0, [DeletedAt] = @p1 FROM [QuickSqlTicket] AS [t0] WHERE [t0].[Id] = @p2", sql);
        Checks.Absent("DELETE", sql);
    }

    [Fact]
    public void Without_a_registered_behavior_the_delete_chain_still_sends_a_delete()
    {
        var sql = orm().Delete<Ticket>().Where(t => t.Id == 7).ToSql();

        // 这一条钉的是"没有注册就没有改写"，方向与上面那条相反：默认那一支删的还是行本身，
        // 而本库不会替没开软删的项目悄悄留下带 true 的行。
        Checks.Has("DELETE FROM [t0] FROM [QuickSqlTicket] AS [t0]", sql);
        Checks.Absent("UPDATE", sql);
        Checks.Has("物理删除", orm().Delete<Ticket>().Where(t => t.Id == 7).Explain().Reason);
    }

    [Fact]
    public void The_flag_only_registration_writes_one_column_and_no_timestamp()
    {
        var plan = flagOnly().Delete<Ticket>().Where(t => t.Id == 7).Explain();

        Checks.Has("SET [Deleted] = @p0", plan.Sql.Text);
        Checks.Absent("[DeletedAt]", plan.Sql.Text);

        // 参数只有置位那一颗加调用方那一颗：注册里没给 at，本库就不该往语句里添第三颗参数。
        Assert.Equal(["@p0", "@p1"], plan.Sql.Parameters.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void The_two_written_values_are_parameters_and_not_literals()
    {
        var plan = soft().Delete<Ticket>().Where(t => t.Id == 7).Explain();

        // true 与时间都走参数：它们与调用方 Set(o => o.列, 值) 写进去的东西同级，所以既过了 ParameterBinding
        // 那颗钩子（下面那条用例判的就是这件事），也不必在五族的字面量形状上各写一份。
        Assert.Equal(3, plan.Sql.Parameters.Count);
        Assert.Equal(true, plan.Sql.Parameters[0].Value);
        Assert.Equal(Moment, plan.Sql.Parameters[1].Value);
        Assert.Equal(7, plan.Sql.Parameters[2].Value);
    }

    [Fact]
    public void A_bound_value_hook_reaches_the_two_columns_the_library_itself_writes()
    {
        var seen = new List<string?>();
        var aop = new QuickSqlAop();

        aop.ParameterBinding = e => seen.Add(e.Column?.MemberName);

        var plan = soft(aop).Delete<Ticket>().Where(t => t.Id == 7).Explain();

        // 顺序就是发射段的顺序：库里那两格先落，调用方条件那颗后落。这一条是"改写发生在钩子之前"的判据 ——
        // 如果软删是在 ParameterBinding 之后才添参数，这两格就没人过钩子，而 §10.1 硬规则 1 的回放就缺半截。
        // 第三颗带的是 null 而不是 "Id"：条件值没有列出处，那是 UPDATE 链已经钉过的既有不对称
        //（SqlServerUpdateTests.The_set_value_carries_its_column_while_a_condition_value_does_not），本条只复点。
        Assert.Equal(["Deleted", "DeletedAt", null], seen);
        Assert.NotEmpty(plan.Sql.Parameters);
    }

    [Fact]
    public void A_text_hook_still_runs_on_top_of_the_rewrite_and_says_which_chain_it_came_from()
    {
        var kinds = new List<string>();
        var texts = new List<string>();
        var aop = new QuickSqlAop();

        aop.CudBuilding = e =>
        {
            kinds.Add(e.OperationKind);
            texts.Add(e.Sql.Text);
        };

        var plan = soft(aop).Delete<Ticket>().Where(t => t.Id == 7).Explain();

        // 约束 3 那句"日志与审计看到的是真正发出去的那一句"在 AST 改判之后仍然成立：订阅者拿到的文本
        // 已经是 UPDATE，而 OperationKind 仍说 delete —— 那一个词说的是"调用方要的是删除"，
        // 文本说的是"库里执行的是置位"，两句都是真的，缺一句就会让人读错。
        Assert.Equal(["delete"], kinds);
        Assert.StartsWith("UPDATE ", texts[0], StringComparison.Ordinal);
        Assert.Empty(plan.HookReplay);
    }

    [Fact]
    public void A_subscriber_can_still_rewrite_that_update_and_the_replay_keeps_both_steps()
    {
        var aop = new QuickSqlAop();

        aop.CudBuilding = e => e.Rewritten = e.Sql with { Text = "/* 影子 */ " + e.Sql.Text };

        var plan = soft(aop).Delete<Ticket>().Where(t => t.Id == 7).Explain();

        // 回放里只有一条：软删换的是形状，不进 HookReplay（它不是钩子，而调用方订阅的那一颗改的是文本）。
        // 这一条同时钉住"预置行为不占文本改写的口子"—— 两条机制叠在同一条语句上，各记各的。
        var trace = Assert.Single(plan.HookReplay);

        Assert.Equal("CudBuilding", trace.HookName);
        Assert.Equal(HookCategory.Rewrite, trace.Category);
        Assert.StartsWith("UPDATE [t0] SET", trace.Before, StringComparison.Ordinal);
        Assert.StartsWith("/* 影子 */ UPDATE [t0] SET", trace.After, StringComparison.Ordinal);
    }

    // ── 自述：Reason 的三分岔 ─────────────────────────────────────────

    [Fact]
    public void The_reason_names_the_written_columns_and_stops_claiming_a_physical_delete()
    {
        var reason = soft().Delete<Ticket>().Where(t => t.Id == 7).Explain().Reason;

        Checks.Has("Behaviors.SoftDelete", reason);
        Checks.Has("SET Deleted、DeletedAt", reason);
        Checks.Absent("物理删除", reason);

        // "命中 0 行"那一格必须在这一条里就说清：这一句的 0 行说的是谓词没圈到行，
        // 不是"行已经不在了"，而"已经置位过"绝不该被读成一次并发冲突。
        Checks.Has("谓词没圈到行", reason);
        Checks.Has("不会把「已被删过」报成并发冲突", reason);
    }

    [Fact]
    public void The_reason_still_counts_the_folded_filters_when_the_shape_was_swapped()
    {
        var target = soft();

        target.Filters.Add<Ticket>(t => t.TenantId == 3);

        var plan = target.Delete<Ticket>().Where(t => t.Id == 7).Explain();

        Checks.Has("WHERE [t0].[Id] = @p2 AND [t0].[TenantId] = @p3", plan.Sql.Text);
        Checks.Has("叠着 1 条全局过滤器", plan.Reason);

        // 软删不改 WHERE，所以过滤器的命中范围在改写前后是同一个：这一条钉的是"换形状不换范围"。
        Assert.Equal(4, plan.Sql.Parameters.Count);
    }

    // ── 时钟：一颗属于一次构建 ────────────────────────────────────────

    [Fact]
    public void The_clock_is_read_when_the_statement_is_built_and_only_once_per_chain()
    {
        var fired = 0;
        var target = orm();

        target.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () =>
        {
            fired++;

            return Moment;
        });

        var chain = target.Delete<Ticket>().Where(t => t.Id == 7);

        var first = chain.ToSql();
        var second = chain.ToSql();

        // 逐字相同：同一条链的 Explain 是缓存的，所以 ToSql 两次不会多读一次时钟 ——
        // 而"预览的那一句就是要发出去的那一句"这条链的契约（§5.4）靠这一格兑现到时间戳上。
        Assert.Equal(first, second);
        Assert.Equal(1, fired);

        // 注册期不读时钟：冻在注册上就等于后面所有删除记同一个时刻（与过滤器那句"每次执行重新求值"同一条理由）。
        var registered = orm();

        var read = 0;

        registered.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () =>
        {
            read++;

            return Moment;
        });

        Assert.Equal(0, read);
    }

    // ── 键的形状：接口、具体类、缺列 ──────────────────────────────────

    [Fact]
    public void An_interface_key_covers_every_implementing_entity_and_no_other()
    {
        var target = orm();

        target.Behaviors.SoftDelete<ISoftDelete>(e => e.Deleted);

        Assert.StartsWith("UPDATE ", target.Delete<Ticket>().Where(t => t.Id == 1).ToSql(), StringComparison.Ordinal);

        // Order 不实现那一个接口，所以它一个字都不该变 —— 匹配规则只有一句（键 IsAssignableFrom 实体），
        // 与过滤器那一档同一句，两处不许各判一次。
        Assert.Equal(orm().Delete<Order>().Where(o => o.Id == 1).ToSql(), target.Delete<Order>().Where(o => o.Id == 1).ToSql());
    }

    [Fact]
    public void An_interface_key_fails_at_the_table_that_lacks_the_column_and_names_both_sides()
    {
        var target = orm();

        // 注册期查不出来（接口没有列契约，见 FilterRegistry.Probe 那一段同一个理由），
        // 所以这一条的失败点在第一次真贴上 LooseNote 时 —— 而消息必须把"谁的注册、贴在哪张表"说全。
        target.Behaviors.SoftDelete<ISoftDelete>(e => e.Deleted);

        var failure = Checks.Raised<QuickSqlException>(() => target.Delete<LooseNote>().Where(n => n.Id == 1).ToSql());

        Checks.Has("QuickSqlLooseNote 没有名为 Deleted 的列", failure.Message);
        Checks.Has("接口/基类，注册期查不出来", failure.Message);
    }

    [Fact]
    public void A_concrete_key_that_lacks_the_column_fails_at_registration_not_at_the_first_delete()
    {
        var target = orm();

        // 具体类键有契约可查，所以 §10.7 约束 4 那句"启动期失败"在这一档是兑现的：注册这一句就响。
        var failure = Checks.Raised<QuickSqlException>(
            () => target.Behaviors.SoftDelete<LooseNote>(n => n.Deleted));

        Checks.Has("QuickSqlLooseNote 没有名为 Deleted 的列", failure.Message);
    }

    [Fact]
    public void A_second_registration_for_the_same_entity_is_refused_because_neither_can_win()
    {
        var target = orm();

        target.Behaviors.SoftDelete<Ticket>(t => t.Deleted);

        var failure = Checks.Raised<QuickSqlException>(
            () => target.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () => Moment));

        // 两张册子对同一张表各说一种写法时"谁赢"没有任何依据；而后一次注册看起来像"改了配置"，
        // 于是这一条报在注册那一刻，而不是等到有人读到一句与自己想的不一样的语句。
        Checks.Has("已经注册过一次", failure.Message);
        Checks.Has("换一只 orm 实例", failure.Message);

        // 仍然生效的是第一次那一句（只置位、没有时间列）：这一条钉的是"拒了之后册子没被改坏"。
        Checks.Absent("[DeletedAt]", target.Delete<Ticket>().Where(t => t.Id == 7).ToSql());
    }

    [Fact]
    public void An_expression_instead_of_a_member_read_is_refused_at_registration()
    {
        var target = orm();

        // 这一档回答的是"置位哪一列、时间记在哪一列"，不是"什么条件"，所以取反、算式、导航链都在注册期拒。
        var failure = Checks.Raised<QuickSqlException>(
            () => target.Behaviors.SoftDelete<Ticket>(t => !t.Deleted));

        Checks.Has("要的是一个成员的读法", failure.Message);

        var nested = Checks.Raised<QuickSqlException>(
            () => orm().Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt!.Value));

        Checks.Has("at 那一格", nested.Message);
    }

    [Fact]
    public void A_column_the_library_refuses_to_write_is_refused_as_a_target_too()
    {
        var target = orm();

        // 并发列"只比较不书写"（§8.2）与自增列不接受写这两把尺子，在软删这一档同样量得着：
        // deleted 那一格的形参类型是 bool，所以点不到 InventoryItem 那根字符串并发列，
        // 于是本条量的是自增那一支 —— 把自增列当置位列，就是"往 IDENTITY 里写 true"。
        var failure = Checks.Raised<QuickSqlException>(
            () => target.Behaviors.SoftDelete<IdentityFlag>(t => t.Deleted));

        Checks.Has("不接受写", failure.Message);
    }

    // ── 批量档：一句一 chunk 而一颗时间属于整批 ───────────────────────

    [Fact]
    public void The_bulk_delete_by_keys_becomes_one_update_per_command_sharing_a_single_timestamp()
    {
        var fired = 0;
        var target = orm();

        target.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () =>
        {
            fired++;

            return Moment;
        });

        var plan = target.DeleteRange(tickets(3)).WithBulk(BulkWriteMethod.SingleRow).Explain();

        Assert.Equal(3, plan.Commands.Count);
        Assert.Equal(1, fired);

        foreach (var command in plan.Commands)
        {
            Checks.Has("UPDATE [t0] SET [Deleted] = @p0, [DeletedAt] = @p1", command.Text);
            Checks.Absent("DELETE", command.Text);

            // 每一句的删除时间是同一颗：一次 DeleteRange 是一次删除动作，切成三句只是往返的形状。
            Assert.Equal(Moment, command.Parameters[1].Value);
        }

        Checks.Has("同一批共用一颗时间", plan.Reason);
    }

    [Fact]
    public void The_merged_command_puts_the_two_written_values_before_the_key_list()
    {
        var plan = soft().DeleteRange(tickets(3)).Explain();

        var command = Assert.Single(plan.Commands);

        // 一句 IN 清单换写成一句 UPDATE：SET 的两颗参数在前、键清单的三颗在后（发射段的既有顺序）。
        Checks.Has("WHERE [t0].[Id] IN (", command.Text);
        Assert.Equal(5, command.Parameters.Count);
        Assert.Equal(true, command.Parameters[0].Value);
        Assert.Equal(Moment, command.Parameters[1].Value);
        Assert.Equal([1, 2, 3], command.Parameters.Skip(2).Select(p => p.Value).ToArray());
    }

    [Fact]
    public void A_bulk_delete_still_reports_the_filters_that_narrow_its_key_list()
    {
        var target = soft();

        target.Filters.Add<Ticket>(t => t.TenantId == 3);

        var plan = target.DeleteRange(tickets(2)).Explain();

        Checks.Has("[t0].[TenantId] = @p", plan.Commands[0].Text);
        Checks.Has("叠着 1 条全局过滤器", plan.Reason);
    }

    /// <summary><see cref="A_column_the_library_refuses_to_write_is_refused_as_a_target_too"/> 的靶子：
    /// 置位列<em>长得像</em>那一列而它是自增列（<c>IsIdentity = Yes</c> 是显式声明，绕开"整型主键才自增"那条约定）。
    /// 这一张只在软删这一档用，别的册都不碰它。</summary>
    [Table("QuickSqlIdentityFlag")]
    sealed class IdentityFlag
    {
        public int Id { get; set; }

        [QuickColumn(IsIdentity = TriState.Yes)]
        public bool Deleted { get; set; }
    }
}
