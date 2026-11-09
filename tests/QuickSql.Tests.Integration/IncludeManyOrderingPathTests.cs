using QuickSql.Aop;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>集合加载的子行排序口（<c>OrderByMany</c>，§5.7）判在真库上。
///
/// <para><b>为什么这一半只能在库里看</b>：离线那册（<c>IncludeManyChainTests</c> 的读数桩一族）能钉
/// 子句文本里确实带了 <c>ORDER BY</c>，但"缝回父实体的集合属性时<em>顺序就是库给的顺序</em>"这一句
/// 只有库能作证 —— 明细 scrambled 地写进去、按序地读回来，中间那一趟是驱动交回的行序，
/// 内存缝合只按行出现先后逐只 <c>Attach</c>，不重排。</para>
///
/// <para><b>靶子用货架那三层</b>（架 → 位 → 钩，与级联那一家同一批实体）：第一层点 <c>Bin</c> 升序、
/// 第二层点 <c>Name</c> 降序，一颗口隔一层生效或静默吞层那两种失败形态在这一格里都读得出来。
/// 名字都取 ASCII（<c>slate("B1")</c> 一族，同前缀只剩尾号）：汉字排序撞各家 collation 的脾气
/// （<c>CollationTests</c> 那一册判的就是这件事），而这一格要判的是"序进没进库"，不是哪家怎么排汉字。</para></summary>
public sealed class IncludeManyOrderingPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static DbContext Session() => new(Orm);

    // Batch 每用例一颗（隔离理由见 OrderingPathTests 头注）。Bin/Name 列只有 nvarchar(16)，
    // 所以进列的是那 8 位十六进制短标签，判据要的是"这一批行是本次的"，不是标签的长度。
    readonly string Batch = "quicksql-manyo-" + Guid.NewGuid().ToString("N")[..8];

    string slate(string name) => $"{Batch[15..]}/{name}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    /// <summary>三只乱序的位（B3 先写、B1 第二、B2 垫底），B1 手里两只乱序的钩（H2 先写、H1 后写）。</summary>
    Rack ScrambledRack() => new()
    {
        Id = 0,
        Label = slate("序架"),
        Slots =
        [
            new ShelfSlot { Bin = slate("B3") },
            new ShelfSlot { Bin = slate("B1"), Hooks = [new SlotHook { Name = slate("H2") }, new SlotHook { Name = slate("H1") }] },
            new ShelfSlot { Bin = slate("B2") },
        ],
    };

    [Fact]
    public async Task Scrambled_children_come_back_in_the_order_the_child_select_ordered()
    {
        Database.Ready();

        EnsureTable(typeof(Rack));
        EnsureTable(typeof(ShelfSlot));

        var db = Session();
        var rack = ScrambledRack();

        db.Add(rack);
        await db.SaveChangesAsync(ct);

        var rackId = rack.Id;
        var sent = new List<string>();
        var aop = new QuickSqlAop { Executing = e => sent.Add(e.Sql.Text) };

        var shelf = Assert.Single(await Database.OrmWith(aop)
            .Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .OrderByMany(s => s.Bin)
            .Where(r => r.Id == rackId)
            .ToListAsync(ct));

        // 判据一：缝回来的顺序 = ORDER BY 的顺序，而不是写入顺序（B1、B2、B3 ≠ 写序 B3、B1、B2）。
        Assert.Equal([slate("B1"), slate("B2"), slate("B3")], shelf.Slots!.Select(s => s.Bin).ToArray());

        // 判据二：序发射在子句里，不在内存里 —— 父句没有 ORDER BY，子句带着。
        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain("ORDER BY", sent[0]);
        Assert.Contains("ORDER BY", sent[1]);

        // 方向位：再点一次是整串替换，降序就是把那一串翻过来。
        sent.Clear();

        var back = Assert.Single(await Database.OrmWith(aop)
            .Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .OrderByMany(s => s.Bin, descending: true)
            .Where(r => r.Id == rackId)
            .ToListAsync(ct));

        Assert.Contains("DESC", sent[1]);
        Assert.Equal([slate("B3"), slate("B2"), slate("B1")], back.Slots!.Select(s => s.Bin).ToArray());
    }

    [Fact]
    public async Task Each_layer_of_a_nested_chain_carries_its_own_child_ordering()
    {
        Database.Ready();

        EnsureTable(typeof(Rack));
        EnsureTable(typeof(ShelfSlot));
        EnsureTable(typeof(SlotHook));

        var db = Session();
        var rack = ScrambledRack();

        db.Add(rack);
        await db.SaveChangesAsync(ct);

        var rackId = rack.Id;
        var sent = new List<string>();
        var aop = new QuickSqlAop { Executing = e => sent.Add(e.Sql.Text) };

        var shelf = Assert.Single(await Database.OrmWith(aop)
            .Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .OrderByMany(s => s.Bin)
            .ThenIncludeMany(s => s.Hooks!)
            .OrderByMany(h => h.Name, descending: true)
            .Where(r => r.Id == rackId)
            .ToListAsync(ct));

        // 三句：主查询无 ORDER BY、位一句升序、钩一句降序 —— 一颗口只点它书写位置那一层。
        Assert.Equal(3, sent.Count);
        Assert.DoesNotContain("ORDER BY", sent[0]);
        Assert.Contains("ORDER BY", sent[1]);
        Assert.DoesNotContain("DESC", sent[1]);
        Assert.Contains("DESC", sent[2]);

        Assert.Equal([slate("B1"), slate("B2"), slate("B3")], shelf.Slots!.Select(s => s.Bin).ToArray());

        var first = Assert.Single(shelf.Slots!, s => s.Bin == slate("B1"));
        Assert.Equal([slate("H2"), slate("H1")], first.Hooks!.Select(h => h.Name).ToArray());

        // 没点的层不排：同一条链去掉两颗 OrderByMany，行还是那三批行（集合相等），而序不承诺。
        var plain = Assert.Single(await Database.Orm
            .Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .ThenIncludeMany(s => s.Hooks!)
            .Where(r => r.Id == rackId)
            .ToListAsync(ct));

        Assert.Equal(3, plain.Slots!.Count);
        Assert.Equal(2, Assert.Single(plain.Slots!, s => s.Bin == slate("B1")).Hooks!.Count);
    }

    static void EnsureTable(Type clrType) => Database.EnsureTable(clrType);
}
