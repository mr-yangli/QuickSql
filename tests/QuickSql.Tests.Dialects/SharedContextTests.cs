using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§7 那句 <c>orm.CreateSharedContext&lt;AppDbContext&gt;()</c>（per-async-flow 的共享会话）的离线判据。
/// 这一册问的全是<b>槽位</b>而不是 SQL：一只会话在一条执行流上被取几次、什么情况下是同一只、什么情况下各拿各的，
/// 而 <see cref="SqlServer.NoDriverFactory"/> 保证了一句 SQL 也没上线（§3）。
///
/// <b>每一条用例都用一只自己的 <see cref="QuickSqlOrm"/>。</b>这不是排版习惯而是隔离手段：槽位挂在实例上
/// （一颗 <c>AsyncLocal</c> 字段而非静态），于是「这一条用例同步写进去的值会不会留给下一条」这个问题在实例隔离之后
/// 根本不存在 —— 反过来，若哪天有人把槽位改成 static，第三条与第六条会当场红：那时隔壁那只 orm
/// 与兄弟分支都会读到上一行留下的那只会话。
///
/// 判不到的那一半归集成层：共享会话在真库上的事务归属（它拿着 <see cref="QuickSqlOrm"/> 走的就是 <c>SaveChanges</c>
/// 那条既有路径，§6 (3)），本册只在内存里建实例，证不了这一点。</summary>
public sealed class SharedContextTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    // ── 同一只会话：取两次 / 两种类型 / 两只 orm ──────────────────────

    [Fact]
    public void The_same_flow_takes_the_same_context_every_time()
    {
        var root = orm();

        var first = root.CreateSharedContext<RequestSession>();
        var second = root.CreateSharedContext<RequestSession>();

        Assert.Same(first, second);

        // "同一只"要说准：共享的不只是对象身份，而是那一本账 —— 第一只里 Add 的行，第二只看得见。
        first.Add(new Order { Id = 7, CustomerName = "甲" });

        Assert.Same(first.ChangeTracker, second.ChangeTracker);
        Assert.Equal(1, second.ChangeTracker.Count);
    }

    [Fact]
    public void Each_context_type_gets_its_own_slot_on_the_same_flow()
    {
        var root = orm();

        var request = root.CreateSharedContext<RequestSession>();
        var report = root.CreateSharedContext<ReportSession>();

        Assert.NotSame(request, report);
        Assert.Same(report, root.CreateSharedContext<ReportSession>());

        // 链头换过一次之后老那一格不能丢：ReportSession 是第二个节点，往后找要还能摸到第一个。
        Assert.Same(request, root.CreateSharedContext<RequestSession>());
    }

    [Fact]
    public void Two_orm_instances_on_one_flow_do_not_share_a_context()
    {
        var left = orm();
        var right = orm();

        var fromLeft = left.CreateSharedContext<RequestSession>();
        var fromRight = right.CreateSharedContext<RequestSession>();

        Assert.NotSame(fromLeft, fromRight);
        Assert.Same(fromLeft, left.CreateSharedContext<RequestSession>());

        // 挂在实例上的另一层含义：会话手里那颗 Orm 就是取到它的那一只，不会串到隔壁那套库。
        Assert.Same(left, fromLeft.Orm);
        Assert.Same(right, fromRight.Orm);
    }

    // ── AsyncLocal 的那条方向箭头：谁能看见谁 ────────────────────────

    [Fact]
    public async Task A_context_built_after_an_await_stays_inside_its_own_branch()
    {
        var root = orm();

        // 子任务里建的那只属于子任务那一帧（只有同步写入才回到调用方的帧，v0.12 探针、§6）：
        // 父帧之后自己取，取到的是另一只，而父帧这一只在它自己那条流上是稳定的。
        var inner = await Task.Run(() => root.CreateSharedContext<RequestSession>());

        var outer = root.CreateSharedContext<RequestSession>();

        Assert.NotSame(inner, outer);
        Assert.Same(outer, root.CreateSharedContext<RequestSession>());

        // 两本账互不相干：父帧里 Add 的那一行不会出现在子帧那只里，反之亦然。
        outer.Add(new Order { Id = 7, CustomerName = "甲" });

        Assert.Equal(1, outer.ChangeTracker.Count);
        Assert.Equal(0, inner.ChangeTracker.Count);
    }

    [Fact]
    public async Task A_context_built_by_the_parent_is_the_one_every_branch_sees()
    {
        var root = orm();

        var parent = root.CreateSharedContext<RequestSession>();

        var branches = await Task.WhenAll(
            Task.Run(() => root.CreateSharedContext<RequestSession>()),
            Task.Run(() => root.CreateSharedContext<RequestSession>()));

        // §7 那句"不做 DI 注册也能跨方法传递"的正面：父帧先建，扇出去的每一条分支摸到的都是同一只。
        // 也正因为如此，并发分支在改同一本账 —— 这一格今天不设闸门（要闸门是新公开语义，归 §14 P1 余项），
        // 所以这里钉的是行为，不是保护。
        Assert.All(branches, branch => Assert.Same(parent, branch));
    }

    [Fact]
    public async Task Sibling_branches_that_each_build_one_do_not_see_each_other()
    {
        var root = orm();

        var branches = await Task.WhenAll(
            Task.Run(() => root.CreateSharedContext<RequestSession>()),
            Task.Run(() => root.CreateSharedContext<RequestSession>()));

        Assert.NotSame(branches[0], branches[1]);

        // 两条分支各自建完之后，父帧那一格还是空的 —— 它一取就是第三只，而不是捡走其中一条的。
        var parent = root.CreateSharedContext<RequestSession>();

        Assert.NotSame(parent, branches[0]);
        Assert.NotSame(parent, branches[1]);
    }

    [Fact]
    public void A_fresh_execution_flow_starts_with_no_shared_context()
    {
        var root = orm();

        var parent = root.CreateSharedContext<RequestSession>();

        RequestSession? onNewFlow = null;

        // 新线程 + 抑制 ExecutionContext 流动 = 一条与当前这一帧无关的流（"另一个请求"的离线等价物）。
        // 它必须建自己的那一只，而不是捡父帧那一只 —— §7 那句「不会跨请求泄漏」钉在这里。
        Thread worker;

        using (ExecutionContext.SuppressFlow())
        {
            worker = new Thread(() => onNewFlow = root.CreateSharedContext<RequestSession>());
            worker.Start();
        }

        worker.Join();

        Assert.NotNull(onNewFlow);
        Assert.NotSame(parent, onNewFlow);
    }

    // ── 建不出来时不占位 ─────────────────────────────────────────────

    [Fact]
    public void A_context_shape_that_cannot_be_constructed_leaves_the_slot_empty()
    {
        var root = orm();

        // 派生会话要有 public Ctor(QuickSqlOrm)；Activator 找不到时抛 MissingMethodException（消息点名类型，§7）。
        Checks.Raised<MissingMethodException>(() => root.CreateSharedContext<NeedsMoreThanTheOrm>());

        // 挂链表发生在构造之后，所以那次失败没留下任何一格：隔壁那一只照常建、照常稳定。
        var request = root.CreateSharedContext<RequestSession>();

        Assert.Same(request, root.CreateSharedContext<RequestSession>());
    }

    public sealed class RequestSession(QuickSqlOrm orm) : DbContext(orm)
    {
        public DbSet<Order> Orders => Set<Order>();
    }

    public sealed class ReportSession(QuickSqlOrm orm) : DbContext(orm);

    /// <summary>负样本：构造器多要一颗 <c>string</c>，而这条口子只会按 <c>(QuickSqlOrm)</c> 找。</summary>
    public sealed class NeedsMoreThanTheOrm(QuickSqlOrm orm, string tag) : DbContext(orm)
    {
        public string Tag => tag;
    }
}
