using QuickSql.Providers;
using QuickSql.TestInfra;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>DI 那三颗重载落地之后，<c>QuickSqlOrmBuilder.AfterBuild</c> 补的那一格（v0.38 第十八笔）：
/// <b>按请求装的装配</b> —— 过滤器、审计与日志钩子、行为注册这三处都是每实例一份，而"当前登录者属于哪一家企业"
/// 恰恰是按请求的事实，所以它们只能在实例建好之后装。
///
/// <para><b>为什么这一格不能在应用侧绕</b>：装配要跑在这条流上的第一条语句之前，而宿主拿不到"刚建好、
/// 还没被人用过"的那个时机 —— 谁先解析谁就用上了，晚一步装的过滤器会漏掉前面那几句。所以库必须给这一颗，
/// 而不是让每个宿主自己盖一层 Scoped 注册去猜内核怎么装配。</para>
///
/// <para><b>全程离线</b>：本册判的是"装配跑了几次、跑在哪一只实例上、清单是不是快照"，
/// 一句 SQL 都不上线（过滤器那句只到 <c>ToSql()</c>）。装出来的东西在活库上的行为，归
/// <c>FilterTests</c> 与 <c>SoftDeletePathTests</c> 那些既有判据。</para></summary>
public sealed class DiAssemblyTests
{
    static IServiceCollection register(Action<QuickSqlOrmBuilder> configure)
        => new ServiceCollection().AddQuickSqlOrm(
            DbKind.SqlServer, SqlServer.OfflineConnectionString, SqlServer.NoDriverFactory.Instance, configure);

    static QuickSqlOrm resolve(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<QuickSqlOrm>();
    }

    [Fact]
    public void The_assembly_runs_once_per_scope_on_the_instance_it_was_given()
    {
        var seen = new List<QuickSqlOrm>();

        var orm = resolve(register(b => b.AfterBuild.Add((_, target) => seen.Add(target))));

        // 一次解析跑一次，而跑的就是交回来的那一只（不是另一只新建的）。
        Assert.Single(seen);
        Assert.Same(orm, seen[0]);
    }

    [Fact]
    public void Resolving_the_same_scope_twice_does_not_run_the_assembly_again()
    {
        var runs = 0;

        var services = register(b => b.AfterBuild.Add((_, _) => runs++));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<QuickSqlOrm>();
        var second = scope.ServiceProvider.GetRequiredService<QuickSqlOrm>();

        Assert.Same(first, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_filter_installed_there_is_on_the_very_first_statement_of_that_request()
    {
        var installed = resolve(register(b => b.AfterBuild.Add((_, target) => target.Filters.Add<Ticket>(t => !t.Deleted))));
        var bare = resolve(register(static _ => { }));

        // 这一句是本次装配的全部理由：装完立刻生效，不需要有人在第一跳之前再问一次"装好了吗"。
        // 判据盯的是 <c>WHERE</c> 那一段而不是"[Deleted]"这四个字——那一格在 SELECT 的列清单里本来就出现，
        // 只按四个字判的话，装配整颗不跑也是绿的（探针第一跑正是这么骗过去的）。
        Checks.Has("FROM [QuickSqlTicket] AS [t0] WHERE NOT [t0].[Deleted] = 1", installed.Select<Ticket>().ToSql());
        Assert.DoesNotContain("WHERE", bare.Select<Ticket>().ToSql());
    }

    [Fact]
    public void Several_assemblies_run_in_the_order_they_were_given()
    {
        var order = new List<string>();

        resolve(register(b =>
        {
            b.AfterBuild.Add((_, _) => order.Add("先登记的"));
            b.AfterBuild.Add((_, _) => order.Add("后登记的"));
        }));

        Assert.Equal(new[] { "先登记的", "后登记的" }, order);
    }

    [Fact]
    public void The_list_is_a_snapshot_taken_at_registration_time()
    {
        var runs = 0;
        var late = new List<Action<IServiceProvider, QuickSqlOrm>>();

        late.Add((_, _) => runs++);

        var services = register(b => b.AfterBuild = late);

        // 登记之后往同一份清单里添的那一颗不生效：描述表是登记时的产物，留活引用只会让"什么时候装的"变成猜。
        late.Add((_, _) => runs++);

        resolve(services);

        Assert.Equal(1, runs);
    }

    [Fact]
    public void The_assembly_sees_that_requests_own_services_and_installs_only_there()
    {
        var services = register(b => b.AfterBuild.Add((sp, orm) =>
        {
            // 装配拿得到本请求的服务：这一格是这颗钩子存在的理由 —— "当前登录者是哪一家企业"只有在
            // 这条流上才知道，而装上去的过滤器只属于这一条流。
            if (sp.GetRequiredService<Installer>().Enabled)
                orm.Aop.Executed = _ => { };
        })).AddScoped<Installer>();

        using var provider = services.BuildServiceProvider();

        using var on = provider.CreateScope();
        on.ServiceProvider.GetRequiredService<Installer>().Enabled = true;
        var first = on.ServiceProvider.GetRequiredService<QuickSqlOrm>();

        using var off = provider.CreateScope();
        var second = off.ServiceProvider.GetRequiredService<QuickSqlOrm>();

        Assert.NotNull(first.Aop.Executed);

        // 另一条流上同一颗钩子跑过，而它自己判了"不装"：订阅位挂在实例的 Aop 上，Aop 每请求一份，
        // 所以上一家的回调不会串到这一家。
        Assert.Null(second.Aop.Executed);
    }

    sealed class Installer
    {
        public bool Enabled { get; set; }
    }

    [Fact]
    public void An_assembly_that_registers_the_same_behavior_twice_is_refused_on_the_spot()
    {
        var raised = Checks.Raised<QuickSqlException>(() => resolve(register(b =>
        {
            b.AfterBuild.Add((_, orm) => orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt));
            b.AfterBuild.Add((_, orm) => orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted));
        })));

        // 装配写重了不当"后一次盖掉前一次"处理：抛在第一次真用上那只实例的时候，而不是留下两种说法。
        Assert.Contains("已经注册过一次", raised.Message);
    }
}
