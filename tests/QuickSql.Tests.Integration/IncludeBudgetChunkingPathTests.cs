using QuickSql.Aop;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§12.3 分块公式的<b>读侧那一半</b>：补载子查询（IncludeMany / 拆分 Include / <c>DbSet.Load</c>，
/// 共用 <c>QueryEngine.QueryInBatchesAsync</c> 一颗咽喉）的 IN 值按参数预算分块。
///
/// <para><b>钉的是什么</b>：这一格落地之前，"父集合有多大"直接决定单条 IN 的参数颗数 ——
/// SQL Server 的参数上限是 2100、Oracle 的 IN 列表上限是 1000 项，超限的失败形状是
/// <em>整条子查询在驱动侧报错</em>，而不是慢。写侧（批量 INSERT、合并 UPDATE）早就按
/// <c>StatementEmitter.RowsPerStatement</c> 分块并有低预算钉，读侧四条 IN 是漏网的那一族；
/// 现在同一颗公式管到读侧，本册用同一把量具（<c>LowBudgetProvider</c>）逼它现形。</para>
///
/// <para><b>为什么预算掰到 3</b>：公式是 <c>预算 × 9 / 10 / 每行参数颗数</c>，单键 IN 每行一颗，
/// 3 的预算给每块 2 键 —— 3 只父正好拆成 2 + 1 两块，块数与每块的参数颗数都是确定量。
/// 对照腿走默认预算（本库不报数，落 <c>ConservativeParameterBudget</c> 的 100 → 每块 90），
/// 同一批父只发一条子查询：证明"分块由预算驱动"，不是无条件多付往返。</para></summary>
public sealed class IncludeBudgetChunkingPathTests
{
    static CancellationToken ct => TestContext.Current.CancellationToken;

    // Batch 每用例一颗（隔离理由见 OrderingPathTests 头注）。Label/Bin 列只有 nvarchar(16)，
    // 所以进列的是那 8 位十六进制短标签，判据要的是"这一批行是本次的"，不是标签的长度。
    readonly string Batch = "quicksql-inbc-" + Guid.NewGuid().ToString("N")[..8];

    string slate(string name) => $"{Batch[14..]}/{name}";

    /// <summary>三只架、四只位（R1 一只、R2 两只、R3 一只），逐只写库后交回它们的 Id。</summary>
    async Task<List<int>> SeedRacks()
    {
        EnsureTable(typeof(Rack));
        EnsureTable(typeof(ShelfSlot));

        using var db = new DbContext(Database.Orm);

        var racks = new[]
        {
            new Rack { Label = slate("R1"), Slots = [new ShelfSlot { Bin = slate("S11") }] },
            new Rack { Label = slate("R2"), Slots = [new ShelfSlot { Bin = slate("S21") }, new ShelfSlot { Bin = slate("S22") }] },
            new Rack { Label = slate("R3"), Slots = [new ShelfSlot { Bin = slate("S31") }] },
        };

        foreach (var rack in racks)
            db.Add(rack);

        await db.SaveChangesAsync(ct);

        return [.. racks.Select(r => r.Id)];
    }

    [Fact]
    public async Task IncludeMany_over_a_tight_budget_chunks_the_child_select_and_stitches_every_parent()
    {
        Database.Ready();

        var ids = await SeedRacks();

        // 紧预算腿：预算 3 → 每块 2 键 → 3 只父拆成 2 + 1 两块子查询。
        var tightSent = new List<(string Text, int Parameters)>();
        var tightOrm = Database.OrmWith(
            new QuickSqlAop { Executing = e => tightSent.Add((e.Sql.Text, e.Sql.Parameters.Count)) },
            provider: LowBudgetProvider.BudgetOnly(Database.Provider, 3));

        var viaTight = await tightOrm.Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .Where(r => ids.Contains(r.Id))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        // 判据一：主查询一条 + 子查询两条 —— 分块真的发生了（这一格落地之前是单条 IN 直发，
        // 父集合超过预算时整条在驱动侧失败）。两条子句都是 IN 形，参数颗数逐块是 2 与 1。
        Assert.Equal(3, tightSent.Count);
        Assert.Contains(" IN ", tightSent[1].Text, StringComparison.Ordinal);
        Assert.Contains(" IN ", tightSent[2].Text, StringComparison.Ordinal);
        Assert.Equal(2, tightSent[1].Parameters);
        Assert.Equal(1, tightSent[2].Parameters);

        // 判据二：缝合不打折 —— 每只父都拿到自己的那串子行，一块都不丢、不串。
        AssertStitched(viaTight);

        // 对照腿：默认预算（不报数的库走 100 的保守值 → 每块 90），同一批父只发一条子查询，
        // 缝合结果与紧预算腿逐格一致 —— 分块是预算逼出来的，不是无条件多付往返。
        var wideSent = new List<(string Text, int Parameters)>();
        var viaWide = await Database.OrmWith(
                new QuickSqlAop { Executing = e => wideSent.Add((e.Sql.Text, e.Sql.Parameters.Count)) })
            .Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .Where(r => ids.Contains(r.Id))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        Assert.Equal(2, wideSent.Count);
        Assert.Equal(3, wideSent[1].Parameters);
        AssertStitched(viaWide);
    }

    /// <summary>三只架按 Id 序各拿到自己的位：R1 一只、R2 两只、R3 一只，标签逐格对上。
    /// R2 那两只两侧都按字典序排过再比：子查询没点序，行序是库给的，判据要的是"都在、不串"。</summary>
    void AssertStitched(List<Rack> racks)
    {
        Assert.Equal(3, racks.Count);
        Assert.Equal(slate("S11"), Assert.Single(racks[0].Slots!).Bin);
        Assert.Equal([slate("S21"), slate("S22")], racks[1].Slots!.Select(s => s.Bin).OrderBy(b => b, StringComparer.Ordinal).ToArray());
        Assert.Equal(slate("S31"), Assert.Single(racks[2].Slots!).Bin);
    }

    static void EnsureTable(Type clrType) => Database.EnsureTable(clrType);
}
