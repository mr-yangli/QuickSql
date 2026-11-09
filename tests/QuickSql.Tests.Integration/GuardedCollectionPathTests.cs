using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary><c>IncludeMany</c> 的缝合打在<b>写入口受保护</b>的集合导航上（§5.7，ABP/DDD 的标配形状
/// <c>public List&lt;TChild&gt; Certs { get; protected set; } = [];</c>）。
///
/// <para><b>旧形状在这一形上抛的是「实体 X 上没有名为 Y 的集合导航属性」</b>——成员明明在场，
/// 真正的原因只是缝合那一步只认公共写口。如今的裁决记在 <c>MemberAccessors.BindCollection</c>：
/// 属性类型自己收得到追加口，就地往 getter 交回的活串里<em>追加</em>，全程不碰写口；
/// 公共写口那一臂一字不易，所以这两格同时也在钉「孪生对等」——受保护的那串与 <c>OpenShop</c>
/// 那串读回来的内容必须一模一样。</para>
///
/// <para><b>用独立 Orm 而不是会话读回</b>：与 <c>CascadePathTests</c> 的链式那颗同法 ——
/// 缝合的每刻读数只属于这一次读取，而 <c>IncludeMany</c> 往会话登记子型不建档那一格是已记名的缺口
/// （上面那格在此只判"挂上没挂上"，替会话侧把变量关掉）。靶子三颗父型共用同一张表：
/// 写进去的店由 <see cref="GuardedShop"/> 写，读回来的两格分别用 <see cref="GuardedShop"/> 与
/// <see cref="SeededShop"/> —— 同一批行，两副 CLR 形状。</para></summary>
public sealed class GuardedCollectionPathTests
{
    // Batch 每用例一颗（隔离押在标签上而不是用例序上，理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-guarded-" + Guid.NewGuid().ToString("N")[..8];

    string slate(string name) => $"{Batch[16..]}/{name}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static DbContext Session() => new(Database.Orm);

    /// <summary>三颗父型同表，物理靶子只有一张店表加一张证件表；<c>EnsureTable</c> 幂等，
    /// 后到的同表型直接过。</summary>
    static void EnsureTables()
    {
        Database.EnsureTable(typeof(GuardedShop));
        Database.EnsureTable(typeof(ShopCert));
    }

    /// <summary>主判据：一只店带三张证写进库，独立 Orm 用 <c>IncludeMany</c> 读回
    /// <c>protected set</c> 那一形 —— 旧形状在这一句上直接抛 QuickSqlException（用户在现场
    /// 用文件库复现过的那次就是这个抛点），如今必须缝得上，而且与公共写口孪生逐值相同。</summary>
    [Fact]
    public async Task IncludeMany_stitches_children_into_a_protected_setter_collection()
    {
        Database.Ready();
        EnsureTables();

        var shopId = await StoreShopAsync();

        var reloaded = Assert.Single(await Database.OrmWith()
            .Select<GuardedShop>()
            .IncludeMany(s => s.Certs!)
            .Where(s => s.Id == shopId)
            .ToListAsync(ct));

        // 恰好三颗：每颗捞到的子行在一次缝合里挂一次（挂两遍在这里会读成 6）。
        // 期望序是 <c>StringComparer.Ordinal</c> 的序：「一」U+4E00 <「三」U+4E09 <「二」U+4E8C。
        Assert.Equal(3, reloaded.Certs.Count);
        Assert.Equal(
            [slate("证一"), slate("证三"), slate("证二")],
            reloaded.Certs.Select(c => c.No).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>同一批行、孪生对照：公共写口的 <see cref="OpenShop"/> 读同一家店，两串必须逐值相等 ——
    /// 受保护那一臂不是"另发明一套缝合"，它挂上去的东西与公共写口那一臂本来就挂得上的东西一模一样。</summary>
    [Fact]
    public async Task The_public_setter_twin_reads_the_same_graph_back()
    {
        Database.Ready();
        EnsureTables();

        var shopId = await StoreShopAsync();

        var guarded = Assert.Single(await Database.OrmWith()
            .Select<GuardedShop>().IncludeMany(s => s.Certs!).Where(s => s.Id == shopId).ToListAsync(ct));

        var open = Assert.Single(await Database.OrmWith()
            .Select<OpenShop>().IncludeMany(s => s.Certs!).Where(s => s.Id == shopId).ToListAsync(ct));

        Assert.NotNull(open.Certs);
        Assert.Equal(
            open.Certs!.Select(c => c.No).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            guarded.Certs.Select(c => c.No).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>「集合位已经装着值」那一格。<b>语义裁决（就这一种，写死在这里）</b>：就地追加 ——
    /// 活串不清空、预置的那颗必须活着，而这一次捞到的每颗子行<em>恰好挂一次</em>（重复灌会读成 7：
    /// 3×2+1；整串覆盖会读成 3，预置那颗没了）。两个错形被这一个读数同时区分开。</summary>
    [Fact]
    public async Task A_live_collection_holding_values_keeps_them_and_takes_each_child_once()
    {
        Database.Ready();
        EnsureTables();

        var shopId = await StoreShopAsync();

        // SeededShop 与 GuardedShop 同表，只差初始化器里预置的那一颗（Id = -1，库里不会有这种键）。
        var seeded = Assert.Single(await Database.OrmWith()
            .Select<SeededShop>()
            .IncludeMany(s => s.Certs!)
            .Where(s => s.Id == shopId)
            .ToListAsync(ct));

        Assert.Equal(4, seeded.Certs.Count);
        Assert.Equal("预置的一颗", Assert.Single(seeded.Certs, c => c.Id == -1).No);
        Assert.Equal(
            [slate("证一"), slate("证三"), slate("证二")],
            seeded.Certs.Where(c => c.Id != -1).Select(c => c.No).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>写侧与上一族的链式用例同法：会话 <c>Add</c> 整图落库（级联读集合走的是公共 getter，
    /// 这一格不需要也不该替写侧作保）。返回店的键给各用例自己那一行过滤。</summary>
    async Task<int> StoreShopAsync()
    {
        var db = Session();
        var shop = new GuardedShop { Id = 0, Name = slate("受保护的店") };

        shop.Certs.Add(new ShopCert { No = slate("证一") });
        shop.Certs.Add(new ShopCert { No = slate("证二") });
        shop.Certs.Add(new ShopCert { No = slate("证三") });

        db.Add(shop);

        await db.SaveChangesAsync(ct);

        return shop.Id;
    }
}
