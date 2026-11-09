using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using QuickSql.Metadata;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>线程并发（<b>多任务</b>）的四颗钉子 —— 与 <c>ConcurrencyPathTests</c> 那一份分工要说清：
/// 那里钉的是<b>乐观并发</b>（rowversion 谓词、0 行的两种成因），全在单线程里发生；这里钉的是
/// <b>调度并发</b>：多个 Task 同时打一只 <see cref="QuickSqlOrm"/>。不变量两条：
/// <b>共用 Orm、各自会话，必须精确正确</b>；<b>共用一只会话，必须被拒成一句听得懂的话</b> ——
/// "偶尔炸、偶尔静默串数据"两头都不许。
///
/// <para><b>本层第一次实测（2026-10-30）抓到两格，当天由 <c>SessionGate</c> 收口</b>（EF Core 的
/// <c>ConcurrencyDetector</c> 同一条语义，§14 P1 那颗"要不要守卫"的裁决）：同一会话的并发读曾在
/// <c>ChangeTracker.DetectChanges</c> 炸随机 NRE（6/10 炸、4/10 静默）；同一会话的并发写曾 10/10
/// 静默双倍落库（2 行变 4 行，全程无异常）。②③两颗钉的就是拒绝本身与"恰好两行"的对账。</para>
///
/// <para><b>判据取行数不取时间</b>（与 <c>MaterializationAllocationTests</c> 只取字节同一条口径）：
/// 总数对不上是静默丢写，比抛异常危险一个量级，所以终审是裁判连接上的裸 ADO <see cref="Database.Rows"/>
/// 按批次标签点总数 —— 不用 ORM 的读口自证清白（"冲突一律由会话之外的人制造"那一条纪律，原样搬到这一层）。</para>
///
/// <para><b>并发由测试自己造，不靠测试框架</b>：本层 <c>xunit.runner.json</c> 关了集合并行（那是给读别人
/// 正在改的行防死的），这里的扇出全部 <c>Task.WhenAll</c> 显式写出来，工人几颗、循环几遍都在用例里看得见。
/// <b>起点用 <see cref="Barrier"/> 对齐</b>：把两个 Task 直接交给 <c>WhenAll</c> 探出来的"并发"经常是
/// 串行 —— 被等的那条在等待开始前就同步跑完了（第一版探针在两家上的假象）。
/// 工人数按家族收口：SQLite 是文件库、写本身串行化，虚谷那台授权实例不是压测机 —— 这两家四颗工人，
/// 其余十六颗；<b>收的是压强，不是断言</b>：两档钉的是同一条不变量。</para>
///
/// <para><b>这一层的语义包含"重复跑"</b>：竞态测试只跑一次约等于没测，验收口径是本文件在每一家上循环
/// 重跑若干遍仍全绿（脚本化的遍数记在跑法说明里，不进代码）。</para></summary>
public sealed class ThreadConcurrencyPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static CancellationToken ct => TestContext.Current.CancellationToken;

    // 批次标签每颗用例一颗（同 ConcurrencyPathTests 的理由：static 会共篇，重编译可翻红）
    readonly string Batch = "qsql-tconc-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>文件库与虚谷实例收四颗：SQLite 的写是库级串行（busy 等待是排队不是缺陷），
    /// 虚谷那台实例另有别的账要算。其余十六颗。两档都远超单线程，竞态窗口足够开着。</summary>
    static int Workers => Database.Family is Database.Target.Sqlite or Database.Target.Xugu ? 4 : 16;

    const int PerWorker = 20;

    /// <summary>① 共用一只 <see cref="QuickSqlOrm"/>、每工每轮一只自己的会话：插入走 Orm 根的写口，
    /// 回读走会话的读口，终审在裁判连接上按批次点总数。<b>断言是精确行数</b> —— 少一行是丢写，多一行是重写。</summary>
    [Fact]
    public async Task Parallel_sessions_on_a_shared_orm_write_exactly_the_expected_rows()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StressProbeRow));

        var workers = Workers;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(async w =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                var sku = $"{Batch}/{w:D2}/{i:D3}";
                var quantity = w * 1000 + i;

                await Orm.Insert(new StressProbeRow { Sku = sku, Quantity = quantity, Worker = w })
                    .ExecuteAffrowsAsync(ct);

                await using var session = new DbContext(Orm);
                var loaded = await session.Set<StressProbeRow>().Where(r => r.Sku == sku).ToListAsync(ct);
                var row = Assert.Single(loaded);
                Assert.Equal(quantity, row.Quantity);
            }
        }));

        var judge = Database.Rows(
            "SELECT COUNT(*) AS N FROM [dbo].[QuickSqlStressProbe] WHERE [Sku] LIKE @p",
            ("@p", Batch + "%"));

        Assert.Equal(workers * PerWorker,
            int.Parse((string) judge.Single()["N"], CultureInfo.InvariantCulture));
    }

    /// <summary>② 一只会话被两个任务同时读：<b>必须被拒成一句听得懂的话</b>。守卫（<c>SessionGate</c>，
    /// 2026-10-30 落地，EF Core 的 <c>ConcurrencyDetector</c> 同一条语义）在 <c>ListAsync</c> 入口抓闸，
    /// 第二个进场的操作收 <see cref="QuickSqlException"/> —— 换掉的是落地前那颗随机 NRE
    /// （<c>ChangeTracker.DetectChanges</c>，本机实测 6/10 炸、4/10 静默）。
    /// <b>起点用 <see cref="Barrier"/> 对齐</b>：把两个 Task 直接交给 <c>WhenAll</c> 探出来的"并发"经常是
    /// 串行 —— 被等的那条在等待开始前就同步跑完了（第一版探针在两家上的假象）。</summary>
    [Fact]
    public async Task Two_tasks_reading_one_context_are_refused()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StressProbeRow));

        var sku = Batch + "/shared-read";
        await Orm.Insert(new StressProbeRow { Sku = sku, Quantity = 77, Worker = 0 }).ExecuteAffrowsAsync(ct);

        await using var session = new DbContext(Orm);

        const int threads = 2;
        using var barrier = new Barrier(threads);

        var readers = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await session.Set<StressProbeRow>().Where(r => r.Sku == sku).ToListAsync(ct);
        }));

        await Assert.ThrowsAsync<QuickSqlException>(() => Task.WhenAll(readers));
    }

    /// <summary>③ 同一只会话上的<b>并发保存</b>：第二个 <c>SaveChangesAsync</c> 进场被拒，而先到的那个
    /// 把它看见的整批新增都落库 —— 跟踪器里两行 Added，赢的那一遍刷两行，<b>恰好两行</b>，
    /// 落地前"2 行变 4 行"的静默双倍落库（本机实测 10/10）就此换成一记明确的拒绝。
    /// <b>两颗 Add 顺序执行</b>：Add 本身也是跟踪器的写、不在闸内（守卫拦的是"操作"，Add 是登记），
    /// 并发 Add 不在本次裁决范围。</summary>
    [Fact]
    public async Task Two_tasks_saving_one_context_are_refused_and_exactly_both_rows_land()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StressProbeRow));

        await using var session = new DbContext(Orm);

        session.Add(new StressProbeRow { Sku = $"{Batch}/shared-write/0", Quantity = 0, Worker = 0 });
        session.Add(new StressProbeRow { Sku = $"{Batch}/shared-write/1", Quantity = 1, Worker = 1 });

        const int threads = 2;
        using var barrier = new Barrier(threads);

        var savers = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await session.SaveChangesAsync(ct);
        }));

        await Assert.ThrowsAsync<QuickSqlException>(() => Task.WhenAll(savers));

        var judge = Database.Rows(
            "SELECT COUNT(*) AS N FROM [dbo].[QuickSqlStressProbe] WHERE [Sku] LIKE @p",
            ("@p", Batch + "/shared-write/%"));

        Assert.Equal(threads, int.Parse((string) judge.Single()["N"], CultureInfo.InvariantCulture));
    }

    /// <summary>③ 十六线程同时首撞同一颗读取委托形状：<see cref="Materializer"/> 的惰性首建在竞态下
    /// 委托可能被建多颗（<c>ConcurrentDictionary.GetOrAdd</c> 的工厂不保证唯一），<b>产物却必须一致</b>。
    /// <c>Barrier</c> 把起点对齐到同一帧 —— 不对齐的话"首次"多半已经被最快的那个线程独占，竞态窗口等于没开。</summary>
    [Fact]
    public async Task Threads_hitting_the_same_reader_shape_for_the_first_time_agree()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StressProbeFirstRow));

        var seedSku = Batch + "/first";
        await Orm.Insert(new StressProbeFirstRow { Sku = seedSku }).ExecuteAffrowsAsync(ct);

        const int threads = 16;
        using var barrier = new Barrier(threads);

        var results = await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            await using var session = new DbContext(Orm);
            return await session.Set<StressProbeFirstRow>().Where(r => r.Sku == seedSku).ToListAsync(ct);
        })));

        Assert.All(results, rows => Assert.Single(rows));
    }
}
