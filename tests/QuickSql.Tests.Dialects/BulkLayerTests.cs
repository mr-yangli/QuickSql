using QuickSql.Aop;
using QuickSql.Providers;
using QuickSql.TestInfra;
using QuickSql.Write;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§12.1 / §12.2 那条批量阶梯在离线上一问一答的册子：<b>层是谁定的、拿不到那一层时怎么办、
/// 块宽谁说了算、两条执行口各认哪几档、降级在哪一刻留下痕</b>。
///
/// <para>与 <see cref="SqlServerInsertTests"/> 的分工：那一册量的是文本层的形状（版式、分块、参数、钩子），
/// 本册量的是<em>选层</em>这件事本身。两册共用同一套纪律：全程
/// <see cref="SqlServer.NoDriverFactory"/>，一旦有用例把它碰着就是"生成期混进了连接依赖"的缺陷信号；
/// 断言失败的消息里带整条产出的 SQL 或整句 Reason。</para>
///
/// <para><b>装载通道这一支为什么能在离线测</b>：真的那一条是 <c>SqlBulkCopy</c>，只能在驱动程序集里按类型名找，
/// 而本册配的是桩工厂 —— 于是"通道在场"必须靠 <see cref="CopyChannelProvider"/> 那一份替身来现场
/// （与 <see cref="NoBulkKeysProvider"/> 同一个手法）。通道<em>真写进去</em>的那一半证据在集成册，
/// 本册一条都不许连库。</para></summary>
public sealed class BulkLayerTests
{
    /// <summary><see cref="Order"/> 的契约有 8 列，其中 <c>Id</c> 是自增列 ⇒ 每行写 7 列。
    /// 分块公式与装载通道的列数都挂在这个数上（<see cref="SqlServerInsertTests"/> 里同名常量的同一条注释）。</summary>
    const int Written = 7;

    const int Budget = 2100;

    const int RowsPerStatement = Budget * 9 / 10 / Written;

    static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, 890);

    /// <summary>真的那一家 provider，配桩工厂：它报 <c>Bulk_Copy</c> 的能力位，但<em>取不到</em>通道 ——
    /// 桩工厂所在的 assembly 里没有装载类。这一格本身就是阶梯的判据之一，见下面第一则。</summary>
    static IDbProvider Real() => SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm(IDbProvider? provider = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = provider ?? Real(),
            ConnectionString = SqlServer.OfflineConnectionString,
        });

    static QuickSqlOrm ormWith(QuickSqlAop aop, IDbProvider? provider = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = provider ?? Real(),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        });

    static Order row(int n) => new()
    {
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = Moment,
    };

    static Order[] rows(int count) => [.. Enumerable.Range(1, count).Select(row)];

    // ── 层是谁定的 ────────────────────────────────────────────────────

    [Fact]
    public void The_real_channel_is_absent_when_the_driver_assembly_has_no_bulk_class()
    {
        // 这一格同时钉三件事，而它们都是 §12.7 那句"通道靠反射找"的直接后果：
        // 构造 provider 那一段一次类型查找都没有（不然桩工厂的探针早响了）、取不到通道交回的是 null
        // 而不是异常（阶梯因此从 L3 起，不是整条写路径红掉）。
        var bulk = Real().Bulk!;

        Assert.Null(bulk.ResolveCopyChannel());
        Assert.Equal(Budget, bulk.ParameterBudget);
    }

    [Fact]
    public void Auto_takes_the_copy_channel_the_moment_a_provider_offers_one()
    {
        var insert = orm(CopyChannelProvider.OfferingChannel(Real())).InsertRange(rows(3));

        var report = insert.ExplainBulk();

        Assert.Equal(BulkMethod.Auto, report.Requested);
        Assert.Equal(BulkMethod.CopyChannel, report.Method);
        Assert.False(report.Degraded);
        Assert.Equal(3, report.RowCount);
        Assert.Equal(1, report.RoundTrips);
        Assert.Equal(3, report.ChunkSize);
        Checks.Has("不发 SQL 文本、不走参数", report.Reason);
        Checks.Has("一次递交", report.Reason);

        // 层是"哪一条出口"自己的事：同一份请求在文本口那边仍然是一条 INSERT，
        // 换句话说 Explain() 永远不会因为通道在场而交不出文本。
        Checks.Has("INSERT INTO [QuickSqlOrder] ", insert.Explain().Chunks[0].Sql.Text);
    }

    [Fact]
    public void Auto_taking_the_text_layer_because_no_channel_exists_is_not_a_degradation()
    {
        var report = orm().InsertRange(rows(3)).ExplainBulk();

        Assert.Equal(BulkMethod.MultiValue, report.Method);
        Assert.False(report.Degraded);

        // §12.2 那条裁决：Auto 没承诺过任何一层，取到阶梯上第一个满足条件的层就是它的定义。
        // 把它算成降级会让每一次 SQL Server 上的 Auto 都留痕，而"降级可订阅"从此没有信噪比。
        Checks.Absent("退回", report.Reason);
    }

    [Fact]
    public void A_requested_layer_that_is_not_there_degrades_and_names_who_refused()
    {
        var report = orm().InsertRange(rows(3)).WithBulk(BulkMethod.CopyChannel).ExplainBulk();

        // Requested 与 Method 分开记，正是 §12.2 要留痕的那一件事：这一格里它们不等。
        Assert.Equal(BulkMethod.CopyChannel, report.Requested);
        Assert.Equal(BulkMethod.MultiValue, report.Method);
        Assert.True(report.Degraded);
        Checks.Has("没有可用的装载通道", report.Reason);
        Checks.Has("→ 退回 L3 多值语句。", report.Reason);

        // 退回之后仍然要能读出"那一条文本语句长什么样"：数字由同一条公式给，不是另编一句。
        Checks.Has($"每块 {RowsPerStatement} 行", report.Reason);
    }

    [Fact]
    public void A_missing_channel_makes_Never_throw_instead_of_writing_row_by_row()
    {
        var refused = Checks.Raised<QuickSqlException>(
            () => orm().InsertRange(rows(3)).WithBulk(BulkMethod.CopyChannel, FallbackPolicy.Never).ExplainBulk());

        // 这句异常是给 CI 与上线前压测的：§12.2 那句"唯一能防住静默逐条的手段"。
        Checks.Has("没有可用的装载通道", refused.Message);
        Checks.Has("FallbackPolicy.Never 不允许退回文本层", refused.Message);

        // Never 只在"拿不到"时咬人：通道在场时它与 DegradeAndLog 交回同一个产物。
        Assert.Equal(BulkMethod.CopyChannel,
            orm(CopyChannelProvider.OfferingChannel(Real())).InsertRange(rows(3))
                .WithBulk(BulkMethod.CopyChannel, FallbackPolicy.Never).ExplainBulk().Method);
    }

    [Fact]
    public void A_column_the_channel_will_not_take_sends_the_whole_batch_back_not_just_that_column()
    {
        var provider = CopyChannelProvider.Rejecting(Real(), "State");
        var insert = orm(provider).Insert(row(1)).WithBulk(BulkMethod.CopyChannel);

        var report = insert.ExplainBulk();

        Assert.True(report.Degraded);
        Assert.Equal(BulkMethod.MultiValue, report.Method);

        // 拒绝的话里必须点名是哪一列、CLR 侧是什么型：现场只拿得到这一句。
        Checks.Has("State（CLR ", report.Reason);
        Checks.Has("不在 SqlServer 装载通道收下的类型里", report.Reason);

        // 退回的是<em>整批</em>：那一列的值仍然进列清单。省掉一列等于那一列不进库，那是丢写，
        // 而且是一种绿着发生的丢写 —— 与 IChangeGuard.Compares 那句"返回 false 时整批退回"同一个道理。
        var plan = insert.Explain();

        Checks.Has("[State]", plan.Chunks[0].Sql.Text);
        Assert.Equal(Written, plan.Chunks[0].Sql.Parameters.Count);

        // 同一条判据在 Never 那一档变成异常，而不是"少一列的绿"。
        var refused = Checks.Raised<QuickSqlException>(
            () => orm(provider).Insert(row(1)).WithBulk(BulkMethod.CopyChannel, FallbackPolicy.Never).ExplainBulk());

        Checks.Has("不在 SqlServer 装载通道收下的类型里", refused.Message);
    }

    [Fact]
    public void Zero_rows_pick_no_layer_even_when_a_channel_is_on_offer()
    {
        // 判序在这里是有意义的：0 行那一格写在取通道<em>之前</em>，
        // 否则一次空批会把通道找出来、再交出一句"0 行走装载通道、一次递交"。
        var report = orm(CopyChannelProvider.OfferingChannel(Real()))
            .InsertRange<Order>([]).WithBulk(BulkMethod.CopyChannel).ExplainBulk();

        Assert.Equal(BulkMethod.MultiValue, report.Method);
        Assert.False(report.Degraded);
        Assert.Equal(0, report.RowCount);
        Assert.Equal(0, report.RoundTrips);
        Assert.Equal(0, report.ChunkSize);
        Checks.Has("0 行：一层都不必选", report.Reason);
    }

    [Fact]
    public void The_layer_decision_builds_no_text_and_therefore_fires_no_rewrite_hook()
    {
        var cud = 0;
        var binding = 0;
        var aop = new QuickSqlAop();

        aop.CudBuilding = _ => cud++;
        aop.ParameterBinding = _ => binding++;

        var insert = ormWith(aop, CopyChannelProvider.OfferingChannel(Real())).InsertRange(rows(3)).WithBulk(BulkMethod.CopyChannel);

        insert.ExplainBulk();

        Assert.Equal(0, cud);
        Assert.Equal(0, binding);

        // 探针自己得是有效的：建文本时这两位确实会响。少了这一段，上面那两个 0 可以是"钩子根本没接线"。
        insert.Explain();

        Assert.True(cud > 0, "CudBuilding 没被建文本触发过，那上面那条 0 不说明任何事");
        Assert.True(binding > 0, "ParameterBinding 没被建文本触发过，那上面那条 0 不说明任何事");
    }

    // ── 块宽谁说了算 ──────────────────────────────────────────────────

    [Fact]
    public void A_requested_block_width_pushes_the_chunk_but_never_lifts_it_past_the_budget()
    {
        var capped = orm().InsertRange(rows(5)).WithBulk(BulkMethod.MultiValue, rowsPerRoundTrip: 2);

        var report = capped.ExplainBulk();
        var plan = capped.Explain();

        Assert.Equal(2, report.ChunkSize);
        Assert.Equal(3, report.RoundTrips);

        // 两份产出各说各的读者，但同一件事：这个 2 是请求压的，不是预算算的。
        Checks.Has("每块行数是请求压下来的", report.Reason);
        Checks.Has($"块宽 2 是请求压下来的（预算允许 {RowsPerStatement} 行）", plan.Reason);
        Assert.Equal(2, plan.RowsPerStatement);
        Assert.Equal(3, plan.Chunks.Count);

        // 只压不抬：抬过预算的语句会被驱动直接拒，而 §12.3 立项要防的正是"换宽表就出事"。
        var lifted = orm().InsertRange(rows(600)).WithBulk(BulkMethod.MultiValue, rowsPerRoundTrip: 5_000).ExplainBulk();

        Assert.Equal(RowsPerStatement, lifted.ChunkSize);
        Assert.Equal(3, lifted.RoundTrips);
        Checks.Absent("请求压下来", lifted.Reason);
    }

    [Fact]
    public void A_block_width_below_one_is_refused_before_it_can_make_an_empty_chunk()
    {
        var zero = Checks.Raised<QuickSqlException>(
            () => orm().Insert(row(1)).WithBulk(BulkMethod.MultiValue, rowsPerRoundTrip: 0));

        Checks.Has("rowsPerRoundTrip=0", zero.Message);
        Checks.Has("每块至少 1 行", zero.Message);

        // 想要阶梯最底那一档就说出来，而不是用 0 行一块的语句去逼近它。
        Checks.Has(nameof(BulkMethod.SingleRow), zero.Message);

        var negative = Checks.Raised<QuickSqlException>(
            () => orm().Insert(row(1)).WithBulk(BulkMethod.MultiValue, rowsPerRoundTrip: -1));

        Checks.Has("rowsPerRoundTrip=-1", negative.Message);
    }

    [Fact]
    public void Single_row_is_a_block_width_of_one_not_a_second_implementation()
    {
        var insert = orm().InsertRange(rows(3)).WithBulk(BulkMethod.SingleRow);

        var report = insert.ExplainBulk();

        Assert.Equal(BulkMethod.SingleRow, report.Method);
        Assert.False(report.Degraded);
        Assert.Equal(1, report.ChunkSize);
        Assert.Equal(3, report.RoundTrips);
        Checks.Has("按请求逐条", report.Reason);

        // 分岔点在 Build 之下、不在它旁边：同一份代码，块宽 1 ⇒ 一行一块 ⇒ 一次改写一次往返。
        var plan = insert.Explain();

        Assert.Equal(1, plan.RowsPerStatement);
        Assert.Equal(3, plan.Chunks.Count);
        Assert.Equal(1, plan.Chunks[2].RowCount);
        Assert.Equal(2, plan.Chunks[2].FirstRow);
        Checks.Has("块宽 1 是按请求来的", plan.Reason);
        Checks.Absent("退到 L5", plan.Reason);
    }

    [Fact]
    public void A_block_width_on_the_copy_layer_is_rows_per_delivery()
    {
        // 装载通道那一档没有参数预算那道界，所以 rowsPerRoundTrip 在那里说的就是"一次递交几行"
        // （IInsert.WithBulk 抬头里那一格）。不压时一次递交。
        var insert = orm(CopyChannelProvider.OfferingChannel(Real())).InsertRange(rows(5)).WithBulk(BulkMethod.CopyChannel, rowsPerRoundTrip: 2);

        var report = insert.ExplainBulk();

        Assert.Equal(BulkMethod.CopyChannel, report.Method);
        Assert.Equal(2, report.ChunkSize);
        Assert.Equal(3, report.RoundTrips);
        Checks.Has("分 3 次递交、每次 2 行", report.Reason);

        Assert.Equal(1, orm(CopyChannelProvider.OfferingChannel(Real())).InsertRange(rows(5))
            .WithBulk(BulkMethod.CopyChannel).ExplainBulk().RoundTrips);
    }

    [Fact]
    public void The_layer_report_and_the_text_plan_never_disagree_on_the_numbers()
    {
        // 这一格是本册最值钱的一条：ExplainBulk 与 Explain 共用 Decide/Build 那同一个出口取数，
        // 所以"离线看到的层"与"执行走的那一层"之间没有第二套算术可漂移。三档各量一遍。
        foreach (var variant in new[]
                 {
                     (Method: BulkMethod.MultiValue, Cap: (int?)null, Chunk: RowsPerStatement),
                     (Method: BulkMethod.MultiValue, Cap: (int?)2, Chunk: 2),
                     (Method: BulkMethod.SingleRow, Cap: (int?)null, Chunk: 1),
                 })
        {
            var insert = orm().InsertRange(rows(5)).WithBulk(variant.Method, rowsPerRoundTrip: variant.Cap);

            var report = insert.ExplainBulk();
            var plan = insert.Explain();

            Assert.Equal(variant.Method, report.Method);
            Assert.Equal(variant.Chunk, report.ChunkSize);
            Assert.Equal(plan.RowsPerStatement, report.ChunkSize);
            Assert.Equal(plan.Chunks.Count, report.RoundTrips);
        }
    }

    // ── 两条执行口各认哪几档 ──────────────────────────────────────────

    [Fact]
    public void The_row_count_exit_refuses_the_layers_it_cannot_report()
    {
        // 返回一颗 int 的出口装不下"走了哪一层、几次往返、为什么"，所以指向装载通道的请求在这里当场拒。
        var copy = Checks.Raised<QuickSqlException>(
            () => orm().InsertRange(rows(3)).WithBulk(BulkMethod.CopyChannel).ExecuteAffrowsAsync().GetAwaiter().GetResult());

        Checks.Has("ExecuteAffrowsAsync 走不了 CopyChannel", copy.Message);
        Checks.Has("ExecuteBulkAsync", copy.Message);

        var auto = Checks.Raised<QuickSqlException>(
            () => orm().InsertRange(rows(3)).WithBulk(BulkMethod.Auto).ExecuteAffrowsAsync().GetAwaiter().GetResult());

        Checks.Has("走不了 Auto", auto.Message);

        // 文本层那两档过得了这道门：撞上的不是这句拒，是驱动探针。
        // 结构上的另一半收益在这里可见 —— 这条出口不可能走到 CopyAsync，所以它的签名上不欠修剪税。
        var text = Checks.Raised<DriverNotAvailableException>(
            () => orm().InsertRange(rows(3)).WithBulk(BulkMethod.MultiValue).ExecuteAffrowsAsync().GetAwaiter().GetResult());

        Checks.Has("只生成 SQL 的测试不该碰驱动", text.Message);
        Checks.Absent("走不了", text.Message);

        var each = Checks.Raised<DriverNotAvailableException>(
            () => orm().InsertRange(rows(3)).WithBulk(BulkMethod.SingleRow).ExecuteAffrowsAsync().GetAwaiter().GetResult());

        Checks.Absent("走不了", each.Message);
    }

    [Fact]
    public void A_backfill_exit_stays_on_the_text_layer_even_when_a_channel_is_offered()
    {
        // 装载通道给不了回填（§12.1 第三行那句"不能回填自增主键"），所以那条阶梯对它不存在：
        // 请求了 CopyChannel 也一样，OUTPUT 一句不少、块宽仍按预算算。
        var insert = orm(CopyChannelProvider.OfferingChannel(Real())).InsertRange(rows(3)).WithBulk(BulkMethod.CopyChannel);

        var keys = insert.ExplainKeys();

        Checks.Has(") OUTPUT inserted.[Id] VALUES (", keys.Chunks[0].Sql.Text);
        Checks.Has("L3 多值语句", keys.Reason);
        Checks.Absent("装载通道", keys.Reason);
        Assert.Equal(RowsPerStatement, keys.RowsPerStatement);

        // 拒的是回填口借道，不是请求本身被丢掉：同一条请求在批量口那边确实看到了 L1。
        Assert.Equal(BulkMethod.CopyChannel, insert.ExplainBulk().Method);

        // 而请求压下来的块宽跟着<em>实际走的那一层</em>：回填走文本层，压宽就在文本层生效。
        Assert.Equal(2, orm(CopyChannelProvider.OfferingChannel(Real())).InsertRange(rows(3))
            .WithBulk(BulkMethod.CopyChannel, rowsPerRoundTrip: 2).ExplainKeys().RowsPerStatement);

        // 执行口那边同样不借道：它撞的是探针，不是"这一档在这儿不可用"那句拒。
        var touched = Checks.Raised<DriverNotAvailableException>(
            () => insert.ExecuteReturnKeysAsync().GetAwaiter().GetResult());

        Checks.Has("只生成 SQL 的测试不该碰驱动", touched.Message);
        Checks.Absent("走不了", touched.Message);
    }

    // ── 降级留痕（§10.1 的 BulkDegraded 那一位） ───────────────────────

    [Fact]
    public void A_degradation_leaves_its_trace_before_the_write_starts()
    {
        var seen = new List<BulkDegradedEvent>();
        var aop = new QuickSqlAop();

        aop.BulkDegraded = seen.Add;

        var insert = ormWith(aop).InsertRange(rows(3)).WithBulk(BulkMethod.CopyChannel);
        var report = insert.ExplainBulk();

        // 离线册连不上库：写那一步必然撞驱动探针。这里要的正是"探针响了，而痕在它响之前就已经留下"——
        // 降级之后那句写还可能失败，而"它退到哪一层"恰恰是排查那一半要用的信息。
        var touched = Checks.Raised<DriverNotAvailableException>(
            () => insert.ExecuteBulkAsync().GetAwaiter().GetResult());

        Checks.Has("只生成 SQL 的测试不该碰驱动", touched.Message);

        Assert.Single(seen);
        Assert.Equal(BulkMethod.CopyChannel, seen[0].Requested);
        Assert.Equal(BulkMethod.MultiValue, seen[0].Method);
        Assert.Equal(3, seen[0].RowCount);

        // 同一个 Reason 的两个读者（一个给人看、一个是订阅方要打的字段）不许分头编。
        Assert.Equal(report.Reason, seen[0].Reason);
    }

    [Fact]
    public void Auto_a_silent_policy_and_a_honored_request_all_leave_no_trace()
    {
        // Auto 取到文本层不算降级（§12.2 那条裁决）：它没承诺过任何一层。
        Assert.Empty(Traces(new QuickSqlAop(), BulkMethod.Auto, FallbackPolicy.DegradeAndLog, null));

        // 真降级但选了 DegradeSilently：不声张就是它的定义（缓存回填那一类纯优化路径）。
        Assert.Empty(Traces(new QuickSqlAop(), BulkMethod.CopyChannel, FallbackPolicy.DegradeSilently, null));

        // 通道在场而请求被满足：没有降级这件事，也就没有痕。
        Assert.Empty(Traces(new QuickSqlAop(), BulkMethod.CopyChannel, FallbackPolicy.DegradeAndLog, CopyChannelProvider.OfferingChannel(Real())));

        // 对照组：同一条请求、同一条出口，换成 DegradeAndLog 就有一痕（上面三条不是"根本没派发"）。
        Assert.Single(Traces(new QuickSqlAop(), BulkMethod.CopyChannel, FallbackPolicy.DegradeAndLog, null));
    }

    /// <summary>跑一次会降级的批量执行，交回留下的痕。<b>只吞 <see cref="DriverNotAvailableException"/></b>：
    /// 离线册上这一句必然撞桩工厂的探针，而本格量的是痕不是写；别的异常一律让它红。</summary>
    static List<BulkDegradedEvent> Traces(QuickSqlAop aop, BulkMethod method, FallbackPolicy policy, IDbProvider? provider)
    {
        var seen = new List<BulkDegradedEvent>();

        aop.BulkDegraded = seen.Add;

        try
        {
            ormWith(aop, provider).InsertRange(rows(3)).WithBulk(method, policy).ExecuteBulkAsync().GetAwaiter().GetResult();
        }
        catch (DriverNotAvailableException)
        {
        }

        return seen;
    }
}
