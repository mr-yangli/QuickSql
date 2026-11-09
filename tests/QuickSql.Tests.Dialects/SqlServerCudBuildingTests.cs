using QuickSql.Aop;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Write;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary><c>CudBuilding</c>（§10.1 改写类里"整句文本级"的那一档）的离线验证。
///
/// <para>v0.18 之前这个槽位是<b>只有字段、没有发射点</b>的东西 —— 订阅了不会有任何反应，那正是 §10.1
/// 点名的"本仓库拒绝的那种形状"。今天它有了发射点（<c>StatementEmitter.ApplyCud</c>），
/// 四条发射路径：插入链、UPDATE 链、DELETE 链，以及跟踪器 <c>SaveChanges</c> 里的两个发射口。
/// <b>前三条在离线全部可判，所以判在这里；最后一条要连接，归集成层</b>（<c>TrackingPathTests</c>）。</para>
///
/// <para>顺序是被钉住的第二件事：<c>AST → 文本 → CudBuilding → ParameterBinding</c>。文本级改写排在参数级之前，
/// 是因为并发谓词那类"结构性追加"必须在 AST 里合取（<c>UPDATE ... OUTPUT ... WHERE</c> 与
/// <c>... WHERE ... RETURNING</c> 两家的收尾位置不同，文本级追加必在一家写出语法错），
/// 而排在它之后的参数钩子因此看到的必然是<em>最终那条语句</em>的参数表。</para>
///
/// <para>全程 <see cref="SqlServer.NoDriverFactory"/>：改写一条语句不需要驱动。</para></summary>
public sealed class SqlServerCudBuildingTests
{
    /// <summary>没人订阅时 DELETE 链产出的那一份文本 —— 本文件要的是"改写前后各是什么"，
    /// 版式本身归 <see cref="SqlServerUpdateTests"/> 与黄金样本钉，所以这里现取而不是抄一份常量。</summary>
    static string Baseline => orm().Delete<Order>().Where(o => o.Id == 1).ToSql();

    static QuickSqlOrm orm() => SqlServer.Orm();

    static QuickSqlOrm ormWith(QuickSqlAop aop)
        => new(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        });

    static IDelete<Order> Delete(QuickSqlOrm orm) => orm.Delete<Order>().Where(o => o.Id == 1);

    static IUpdate<Order> Update(QuickSqlOrm orm) => orm.Update<Order>().Set(o => o.Note, "改写之前的原文").Where(o => o.Id == 1);

    static IInsert<Order> Insert(QuickSqlOrm orm) => orm.Insert(new Order { CustomerName = "甲", CustomerId = 3, Amount = 1m });

    /// <summary>影子表：这一档的典型形状 —— 整句换掉，参数一颗不动。</summary>
    static string Shadowed(string text)
        => text.Replace("[QuickSqlOrder]", "[QuickSqlOrderShadow]", StringComparison.Ordinal);

    static void Shadow(CudBuildingEvent e)
        => e.Rewritten = e.Sql with { Text = Shadowed(e.Sql.Text) };

    // ── 硬规则 1：改写要在 Explain() 里回放 ───────────────────────────

    [Fact]
    public void A_subscriber_replaces_the_text_and_the_replay_shows_both_sides()
    {
        var aop = new QuickSqlAop();

        aop.CudBuilding = Shadow;

        var plan = ormWith(aop).Delete<Order>().Where(o => o.Id == 1).Explain();

        Checks.Has("[QuickSqlOrderShadow]", plan.Sql.Text);

        var trace = Assert.Single(plan.HookReplay);

        Assert.Equal(0, trace.Step);
        Assert.Equal("CudBuilding", trace.HookName);
        Assert.Equal(HookCategory.Rewrite, trace.Category);
        Assert.Equal(Baseline, trace.Before);
        Assert.Equal(plan.Sql.Text, trace.After);

        // 文本级改写不碰参数表：那一格归 ParameterBinding，两者各有各的回放（§10.1 的分类学就是这个意思）。
        Assert.Single(plan.Sql.Parameters);
        Assert.Equal(1, plan.Sql.Parameters[0].Value);
        Assert.Equal(plan.Sql.Text, Delete(ormWith(aop)).ToSql());
    }

    [Fact]
    public void A_subscriber_that_only_looks_produces_no_replay_row()
    {
        var seen = 0;
        var aop = new QuickSqlAop();

        aop.CudBuilding = e =>
        {
            seen++;
        };

        var plan = ormWith(aop).Delete<Order>().Where(o => o.Id == 1).Explain();

        // 「看过但不改」不写 Rewritten，于是既不产生回放记录、也不让 Reason 换分支。
        // 这一格要钉住的原因：回放列表一旦按"订阅数"而不是按"改写数"生成，日志里就会出现没发生过的变更。
        Assert.Equal(1, seen);
        Assert.Empty(plan.HookReplay);
        Assert.Equal(Baseline, plan.Sql.Text);
    }

    [Fact]
    public void Two_subscribers_chain_in_registration_order_and_the_second_sees_the_firsts_text()
    {
        var aop = new QuickSqlAop();
        var incoming = new List<string>();

        aop.CudBuilding = e => e.Rewritten = e.Sql with { Text = "/* 租户甲 */ " + e.Sql.Text };
        aop.CudBuilding += e =>
        {
            incoming.Add(e.Sql.Text);

            Shadow(e);
        };

        var plan = ormWith(aop).Delete<Order>().Where(o => o.Id == 1).Explain();

        Assert.Equal([$"/* 租户甲 */ {Baseline}"], incoming.ToArray());
        Assert.Equal($"/* 租户甲 */ {Shadowed(Baseline)}", plan.Sql.Text);

        Assert.Equal(2, plan.HookReplay.Count);
        Assert.Equal(new[] { 0, 1 }, plan.HookReplay.Select(t => t.Step).ToArray());
        Assert.Equal(Baseline, plan.HookReplay[0].Before);
        Assert.Equal(plan.HookReplay[1].Before, plan.HookReplay[0].After);
        Assert.Equal(plan.Sql.Text, plan.HookReplay[1].After);
    }

    // ── 硬规则 2：未订阅即零成本 ─────────────────────────────────────

    [Fact]
    public void An_unsubscribed_hook_leaves_all_three_chains_byte_identical_and_the_replay_empty()
    {
        var aop = new QuickSqlAop();
        var quiet = ormWith(aop);

        Assert.Null(quiet.Aop.CudBuilding);
        Assert.False(quiet.Aop.HasCudRewriters);

        Assert.Equal(Insert(orm()).Explain().ToSql(), Insert(quiet).Explain().ToSql());
        Assert.Equal(Update(orm()).ToSql(), Update(quiet).ToSql());
        Assert.Equal(Delete(orm()).ToSql(), Delete(quiet).ToSql());

        Assert.Empty(Insert(quiet).Explain().HookReplay);
        Assert.Empty(Update(quiet).Explain().HookReplay);
        Assert.Empty(Delete(quiet).Explain().HookReplay);
    }

    // ── 三条链共用一个发射点 ─────────────────────────────────────────

    [Fact]
    public void Each_chain_fires_the_hook_with_the_word_the_execution_tag_uses()
    {
        var kinds = new List<string>();
        var aop = new QuickSqlAop();

        aop.CudBuilding = e => kinds.Add(e.OperationKind);

        Insert(ormWith(aop)).Explain();
        Update(ormWith(aop)).ToSql();
        Delete(ormWith(aop)).ToSql();

        // 同一个词表（insert / update / delete）：钩子按它分派"哪一类语句要加全局注释"，
        // 而日志与 Explain 里问的也是同一件事，两套词就会在钩子里对不上。
        Assert.Equal(["insert", "update", "delete"], kinds.ToArray());
    }

    [Fact]
    public void Every_chain_hands_the_hook_a_text_that_the_same_call_then_executes()
    {
        var aop = new QuickSqlAop();

        aop.CudBuilding = Shadow;
        aop.CudBuilding += e => e.Rewritten = e.Sql with { Text = e.Sql.Text + " /* tail */" };

        var orm = ormWith(aop);

        // 两个出口同一份文本：ToSql 里改过一次、执行时又生成一次的话，看的那条和跑的那条就不是同一条。
        Assert.Equal(Delete(orm).ToSql(), Delete(orm).Explain().Sql.Text);
        Assert.Equal(Update(orm).ToLogSql(), Update(orm).Explain().ToLogSql());

        Checks.Has("[QuickSqlOrderShadow]", Insert(orm).Explain().ToSql());
        Checks.Has("/* tail */", Insert(orm).Explain().ToSql());
    }

    // ── 顺序：AST → 文本 → CudBuilding → ParameterBinding ────────────

    [Fact]
    public void The_parameter_hook_sees_the_parameters_of_the_rewritten_statement()
    {
        var aop = new QuickSqlAop();

        aop.CudBuilding = Shadow;
        aop.ParameterBinding = e =>
        {
            if (e.Column?.MemberName == "Note")
                e.SetValue("改写之后再说一遍");
        };

        var plan = ormWith(aop).Update<Order>().Set(o => o.Note, "改写之前的原文").Where(o => o.Id == 1).Explain();

        Assert.Equal(["CudBuilding", "ParameterBinding"], plan.HookReplay.Select(t => t.HookName).ToArray());
        Assert.Equal([0, 1], plan.HookReplay.Select(t => t.Step).ToArray());
        Assert.Equal("改写之后再说一遍", plan.Sql.Parameters[0].Value);
        Checks.Has("[QuickSqlOrderShadow]", plan.Sql.Text);

        // 反序的话这里会是"参数被改了、但那颗参数不在最终文本里"——两档各写一半，谁也说不清最终执行什么。
        Assert.Equal("ParameterBinding", plan.HookReplay[1].HookName);
    }

    // ── 硬规则 3：改写类的异常不吞 ───────────────────────────────────

    [Fact]
    public void A_rewriting_hook_that_throws_fails_every_chain_before_anything_is_sent()
    {
        var aop = new QuickSqlAop();

        aop.CudBuilding = _ => throw new InvalidOperationException("影子表不在这个库里");
        var orm = ormWith(aop);

        // 观察类的异常进 Error 不中断，改写类反过来必须中断：它没改成果，而语句已经不该被执行了。
        Checks.Raised<InvalidOperationException>(() => Insert(orm).Explain());
        Checks.Raised<InvalidOperationException>(() => Update(orm).ToSql());
        Checks.Raised<InvalidOperationException>(() => Delete(orm).ToSql());
    }

    // ── Reason 跟着文本走 ────────────────────────────────────────────

    [Fact]
    public void The_reason_stops_claiming_a_physical_delete_once_the_text_is_something_else()
    {
        var aop = new QuickSqlAop();

        aop.CudBuilding = Shadow;

        var reason = ormWith(aop).Delete<Order>().Where(o => o.Id == 1).Explain().Reason;

        // 没改写时那句「物理删除 + §10.7」还在（SqlServerUpdateTests 钉着它），改写后它必须换分支：
        // 按订阅数而不是按产出判的话，"只读不写"的订阅者会把这句真话也一起撤掉。
        Checks.Has("已被 CudBuilding 改写", reason);
        Checks.Absent("物理删除", reason);
        Checks.Has("HookReplay", reason);

        Assert.Contains("SET 1 列", ormWith(aop).Update<Order>().Set(o => o.Note, "x").Where(o => o.Id == 1).Explain().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrewritten_delete_still_gets_the_sentence_that_matches_its_text()
    {
        var reason = ormWith(new QuickSqlAop()).Delete<Order>().Where(o => o.Id == 1).Explain().Reason;

        Checks.Has("物理删除", reason);
        Checks.Absent("已被 CudBuilding 改写", reason);
    }
}
