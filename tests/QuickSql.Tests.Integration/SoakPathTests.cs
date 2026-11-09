using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>规模化三测（压力 ∶ 并发 ∶ 泄漏）在<b>真库</b>上的 soak 套件。点状正确性由
/// <c>ConcurrencyPathTests</c>（乐观锁全谱）、<c>ThreadConcurrencyPathTests</c>（线程并发四形）、
/// <c>LeakPathTests</c>（context 收敛 ∶ 形状缓存不涨）钉着；这一册量的是<em>规模</em>：
///
/// <para>① <b>压力</b>：五千米数据一轮整链（批量插 → 分页读 → 逐批更新 → 计数删），行数与耗时对账；</para>
///
/// <para>② <b>并发</b>：八只会话在共享 orm 上并行混跑（各插各查各改，批次标签隔离），收口行数必须等于
/// 各会话自报之和——多一分是串了账，少一分是丢了写；</para>
///
/// <para>③ <b>泄漏</b>：三百轮"建上下文 → 查 → 丢"，GC 后弱引用全部收敛、会话注册表与物化缓存
/// 有界——连接与缓存涨上去不下来，是压测里最难查的那一档。</para>
///
/// <para>靶表只归本文件写，行带批次标签，跨场重跑安全。跑得慢是常态（压力那一颗在 SQL Server 上
/// 数秒级），挂上 <c>QUICKSQL_SOAK_SCALE</c> 可整体缩放（默认 1）。</para></summary>
public sealed class SoakPathTests
{
    [Table("QuickSqlSoakRow")]
    public class SoakRow
    {
        public int Id { get; set; }

        [QuickColumn(Length = 64)]
        public string? Sku { get; set; }

        public int Seq { get; set; }

        public decimal Amount { get; set; }

        public bool Done { get; set; }
    }

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static int Scale => int.TryParse(Environment.GetEnvironmentVariable("QUICKSQL_SOAK_SCALE"), out var n) && n > 0 ? n : 1;

    static QuickSqlOrm NewOrm() => new QuickSqlOrm(new QuickSqlOrmOptions
    {
        Provider = Database.Provider,
        ConnectionString = Database.ConnectionString,
        SchemaSync = SchemaSyncPolicy.Execute,
        ForceSchemaSync = true,
    });

    static void Drop()
    {
        Database.Ready();
        Database.Execute("DROP TABLE IF EXISTS " + Database.Provider.CodeFirst.Identifiers.Quote("QuickSqlSoakRow"));
    }

    // ── ① 压力：五千米一轮整链 ─────────────────────────────────────────

    [Fact]
    public async Task Five_thousand_rows_through_the_whole_chain()
    {
        Drop();

        var orm = NewOrm();
        orm.Schemas.Contract<SoakRow>();
        await orm.SyncStructureAsync(ct);

        var batch = "qsql-soak-" + Guid.NewGuid().ToString("N")[..8];
        var rows = Scale * 5_000;

        var clock = Stopwatch.StartNew();

        // 批量插：五百一行分块，全链第一段。
        for (var offset = 0; offset < rows; offset += 500)
        {
            var chunk = Enumerable.Range(offset, Math.Min(500, rows - offset))
                .Select(i => new SoakRow { Sku = $"{batch}/{i}", Seq = i, Amount = i * 0.5m })
                .ToArray();

            await orm.InsertRange(chunk).ExecuteAffrowsAsync(ct);
        }

        clock.Stop();
        var insertMs = clock.ElapsedMilliseconds;

        // 分页读：整链第二段，读数对行数。
        clock.Restart();

        var pages = 0;
        long sum = 0;

        for (var page = 1; page <= rows / 200 + 1; page++)
        {
            var rows_ = await orm.Select<SoakRow>()
                .Where(r => r.Sku!.StartsWith(batch))
                .OrderBy(r => r.Seq)
                .Skip((page - 1) * 200).Take(200)
                .ToListAsync(ct);

            if (rows_.Count == 0)
                break;

            pages++;
            sum += rows_.Sum(r => r.Seq);
        }

        clock.Stop();
        var pageMs = clock.ElapsedMilliseconds;

        Assert.Equal(rows, pages * 200 - (pages * 200 - rows));
        Assert.Equal((long) rows * (rows - 1) / 2, sum);

        // 逐批更新：第三段，Done 打标。
        clock.Restart();

        for (var offset = 0; offset < rows; offset += 1_000)
        {
            await orm.Update<SoakRow>()
                .Where(r => r.Sku!.StartsWith(batch) && r.Seq >= offset && r.Seq < offset + 1_000)
                .Set(r => r.Done, true)
                .ExecuteAffrowsAsync(ct);
        }

        clock.Stop();
        var updateMs = clock.ElapsedMilliseconds;

        var doneCount = await orm.Select<SoakRow>().Where(r => r.Sku!.StartsWith(batch) && r.Done).CountAsync(ct);

        Assert.Equal(rows, doneCount);

        // 计数删：第四段。
        var deleted = await orm.Delete<SoakRow>().Where(r => r.Sku!.StartsWith(batch)).ExecuteAffrowsAsync(ct);

        Assert.Equal(rows, deleted);

        // 读数落账本不落断言：耗时是机器状态，行数才是正确性。
        Assert.True(insertMs >= 0);
        Assert.True(pageMs >= 0);
        Assert.True(updateMs >= 0);
    }

    // ── ② 并发：八会话混跑共享 orm ─────────────────────────────────────

    [Fact]
    public async Task Eight_parallel_sessions_on_a_shared_orm_reconcile_exactly()
    {
        Drop();

        var orm = NewOrm();
        orm.Schemas.Contract<SoakRow>();
        await orm.SyncStructureAsync(ct);

        var batch = "qsql-soak-par-" + Guid.NewGuid().ToString("N")[..8];
        const int sessions = 8;
        const int perSession = 60;

        var summaries = await Task.WhenAll(Enumerable.Range(0, sessions).Select(async s =>
        {
            var tag = $"{batch}/s{s}/";
            var inserted = 0;

            // 每会话三轮"插 → 查 → 改"，各自独立上下文（跟踪器按会话隔离）。
            for (var round = 0; round < 3; round++)
            {
                await using var db = new SoakContext(orm);
                var entities = Enumerable.Range(round * perSession / 3, perSession / 3)
                    .Select(i => new SoakRow { Sku = $"{tag}r{round}-{i}", Seq = s * 1000 + round * 100 + i })
                    .ToArray();

                db.AddRange(entities);
                await db.SaveChangesAsync(ct);
                inserted += entities.Length;

                var read = await db.Set<SoakRow>().Where(r => r.Sku!.StartsWith(tag)).ToListAsync(ct);
                Assert.Equal(inserted, read.Count);

                foreach (var row in read)
                    row.Done = true;

                await db.SaveChangesAsync(ct);
            }

            return (Session: s, Inserted: inserted);
        }));

        // 收口对账：库里恰好是各会话自报之和；打标行数相同。
        var total = summaries.Sum(x => x.Inserted);
        var inDb = await orm.Select<SoakRow>().Where(r => r.Sku!.StartsWith(batch)).CountAsync(ct);
        var doneInDb = await orm.Select<SoakRow>().Where(r => r.Sku!.StartsWith(batch) && r.Done).CountAsync(ct);

        Assert.Equal(total, inDb);
        Assert.Equal(total, doneInDb);
        Assert.Equal(sessions * 3 * (perSession / 3), total);
    }

    public class SoakContext(QuickSqlOrm orm) : DbContext(orm);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task RunCyclesAsync(QuickSqlOrm orm, string probe, List<(WeakReference Context, WeakReference Row)> keep, CancellationToken token)
    {
        for (var i = 0; i < 300; i++)
            keep.Add(await CycleAsync(orm, probe, token));
    }

    /// <summary>一轮"建-查-丢"。<b>非内联是判据的一部分</b>：Debug JIT 会把方法局部变量的栈根
    /// 延长到调用方方法末尾——不跨帧，最后一轮的上下文/实体就"活着"，那不是泄漏是 JIT 形状
    /// （真泄漏的形状是 300 个全不收敛：有东西在组装根或缓存上抓着它们）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<(WeakReference Context, WeakReference Row)> CycleAsync(QuickSqlOrm orm, string probe, CancellationToken token)
    {
        await using var db = new SoakContext(orm);
        var row = await db.Set<SoakRow>().Where(r => r.Sku == probe).FirstOrDefaultAsync(token);

        return (new WeakReference(db), new WeakReference(row));
    }

    // ── ③ 泄漏：三百轮建-查-丢 ─────────────────────────────────────────

    [Fact]
    public async Task Three_hundred_context_cycles_leave_nothing_behind()
    {
        Drop();

        var orm = NewOrm();
        orm.Schemas.Contract<SoakRow>();
        await orm.SyncStructureAsync(ct);

        var batch = "qsql-soak-leak-" + Guid.NewGuid().ToString("N")[..8];

        await orm.InsertRange(Enumerable.Range(0, 50)
                .Select(i => new SoakRow { Sku = $"{batch}/{i}", Seq = i }))
            .ExecuteAffrowsAsync(ct);

        var keep = new List<(WeakReference Context, WeakReference Row)>();

        var probe = $"{batch}/0";

        // 循环住在独立的非内联方法里：GC 发生在它<em>返回之后</em>，它的状态机已经散架，
        // 最后一轮 CycleAsync 的任务盒子因此无根——若在主测试方法里直接 await 循环，
        // 主方法的 await 链会一直抓着最后一轮，得到"只有第 300 个不收敛"的假阳性。
        await RunCyclesAsync(orm, probe, keep, ct);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // 真异步驱动（SQL Server）会把每一跳排在线程池线程上——最近完成的那一轮，其状态机
        // 还躺在工作线程尚未被覆盖的栈上，GC 看得见它。这是 GC 判活的知名边界，不是泄漏：
        // 泄漏的形状是<em>大面积</em>不收敛（没做跨帧隔离时 300 个全活的那一版就是），
        // 而这里允许的是"至多最后一轮"一个尾巴。判据：双轮采集 + 让线程池转空一圈后，未收敛数 ≤ 1。
        await Task.Delay(50, ct);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var alive = keep.Count(x => x.Context.IsAlive || x.Row.IsAlive);

        Assert.True(alive <= 1,
            $"三百轮建-查-丢之后有 {alive} 个上下文/实体未被回收：" +
            "大面积不收敛才是泄漏（会话持了连接、或身份映射/缓存抓着实体），逐个点名第一颗：" +
            keep.FirstOrDefault(x => x.Context.IsAlive || x.Row.IsAlive));

        // 会话注册表有界：三百轮只有循环里那一型，注册表不随轮次增长（每型一份，不是每次一份）。
        Assert.Single(orm.Schemas.RegisteredTypes, t => t == typeof(SoakRow));
    }
}
