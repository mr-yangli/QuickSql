using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>链式合并（<c>UnionAll</c>/<c>Union</c>）在<b>真库</b>上的那一半。离线册
/// （<c>SetOperationEmissionTests</c>）钉的是发射形状，这里钉的是三件只有活库能答的事：
///
/// <para>① <b>合并后的行集逐行对得上</b>：两腿并起来就是两段条件的并集（不去重档行数 = 两腿行数之和；
/// 去重档重叠行只留一份）—— 裁判按批次标签点总数与逐行量值，不用被测的读口自证清白（全层同一条纪律）。
/// <b>腿必须自带批次标签</b>：腿的谓词活在腿里（第一版探针没带批次、只按 Bucket 过滤，历史场的行全并了进来
/// —— 那不是合并的缺陷，是夹具的），这一格顺带钉住"腿的谓词只活在腿里"：外层不再背第一条腿的条件。</para>
///
/// <para>② <b>外层算子落在整串上</b>：合并之后再 <c>Where</c>（外层谓词 over 整串）、<c>OrderBy+Take</c>
/// （合并后取前 N，不是每腿各取 N）、<c>CountAsync</c>（合并后的行数）—— 三个形状两腿各喂一份重叠数据，
/// 读数对不上就是"序/谓词漏进腿里"那一类缺陷。</para>
///
/// <para>③ <b>拒收口有真现场</b>：带分页的腿、套娃的腿，在链上当场抛 —— 这两句在任何一家都一样，
/// 所以五家各跑一遍同一组断言。</para>
///
/// <para>靶表只归本文件写，行带批次标签，跨场重跑安全。</para></summary>
public sealed class UnionPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static CancellationToken ct => TestContext.Current.CancellationToken;

    readonly string Batch = "qsql-union-" + Guid.NewGuid().ToString("N")[..8];

    UnionRow row(int bucket, int quantity) => new() { Sku = $"{Batch}/b{bucket}-{quantity}", Bucket = bucket, Quantity = quantity };

    async Task SeedAsync()
    {
        Database.Ready();
        Database.EnsureTable(typeof(UnionRow));

        // bucket 1 三行（10/20/30），bucket 2 三行（5/15/25）—— 两腿重叠段（15~25）留给去重档与外层谓词用。
        await Orm.InsertRange(new[]
        {
            row(1, 10), row(1, 20), row(1, 30),
            row(2, 5), row(2, 15), row(2, 25),
        }).ExecuteAffrowsAsync(ct);
    }

    // 腿自带批次标签：隔离跨场运行的行，也钉住"腿的谓词只活在腿里"那一格。
    IQuery<UnionRow> Leg1 => Orm.Select<UnionRow>().Where(r => r.Sku!.StartsWith($"{Batch}/b1"));

    IQuery<UnionRow> Leg2 => Orm.Select<UnionRow>().Where(r => r.Sku!.StartsWith($"{Batch}/b2"));

    IQuery<UnionRow> WholeBatch => Orm.Select<UnionRow>().Where(r => r.Sku!.StartsWith(Batch));

    [Fact]
    public async Task Union_all_brings_both_legs_exactly()
    {
        await SeedAsync();

        var rows = await Leg1.UnionAll(Leg2).ToListAsync(ct);

        Assert.Equal(6, rows.Count);
        Assert.Equal(105, rows.Sum(r => r.Quantity));

        var judge = Database.Rows(
            "SELECT COUNT(*) AS N FROM [dbo].[QuickSqlUnionRow] WHERE [Sku] LIKE @p",
            ("@p", Batch + "%"));

        Assert.Equal(6, int.Parse((string) judge.Single()["N"], CultureInfo.InvariantCulture));
    }

    /// <summary>外层谓词落在整串上：合并之后再 Where，命中的是并集里过谓词的那几行。</summary>
    [Fact]
    public async Task Where_after_the_union_filters_the_combined_rows()
    {
        await SeedAsync();

        var rows = await Leg1.UnionAll(Leg2).Where(r => r.Quantity > 12).ToListAsync(ct);

        Assert.Equal(new[] { 15, 20, 25, 30 }, rows.Select(r => r.Quantity).OrderBy(q => q));
    }

    /// <summary>去重档：重叠的三行（20/15/25 两腿都选中）整句只留一份 ——
    /// 9 行并起来去成 6 行，判据取行数与量值集合。</summary>
    [Fact]
    public async Task Union_deduplicates_the_combined_rows()
    {
        await SeedAsync();

        var heavy = WholeBatch.Where(r => r.Quantity >= 15);   // 20, 30, 15, 25 —— 4 行
        var light = WholeBatch.Where(r => r.Quantity <= 25);   // 10, 20, 5, 15, 25 —— 5 行

        var both = await heavy.UnionAll(light).ToListAsync(ct);
        var deduped = await heavy.Union(light).ToListAsync(ct);

        Assert.Equal(9, both.Count);
        Assert.Equal(6, deduped.Count);
        Assert.Equal(new[] { 5, 10, 15, 20, 25, 30 }, deduped.Select(r => r.Quantity).OrderBy(q => q));
    }

    /// <summary>合并后的行数与"合并后取前 N"：Count 打在整串外面；Take 拿的是全并集按序的前两行
    /// （5 与 10），不是每腿各取两行（那会是 4 行）。</summary>
    [Fact]
    public async Task Count_and_take_apply_to_the_combined_set()
    {
        await SeedAsync();

        var union = Leg1.UnionAll(Leg2);

        Assert.Equal(6, await union.CountAsync(ct));

        var firstTwo = await union.OrderBy(r => r.Quantity).Take(2).ToListAsync(ct);

        Assert.Equal(new[] { 5, 10 }, firstTwo.Select(r => r.Quantity));
    }

    /// <summary>交集与差集（2026-11-01 补的两口）在真库上对账：交集取两腿重叠的行（15~25 那一段），
    /// 差集取"第一腿有、第二腿没有"的行（30 —— ≥15 而不在 ≤25 里）。裁判口径与合并档同一条：
    /// 行数与量值集合，不用被测的读口自证清白。两腿同一张表、同一实体投影，整行相等即行相等 ——
    /// 这也是 INTERSECT/EXCEPT 的 SQL 语义（各腿去重后求交/求差）。</summary>
    [Fact]
    public async Task Intersect_and_except_answer_on_the_overlapping_rows()
    {
        await SeedAsync();

        var heavy = WholeBatch.Where(r => r.Quantity >= 15);   // 20, 30, 15, 25 —— 4 行
        var light = WholeBatch.Where(r => r.Quantity <= 25);   // 10, 20, 5, 15, 25 —— 5 行

        var both = await heavy.Intersect(light).ToListAsync(ct);
        var difference = await heavy.Except(light).ToListAsync(ct);

        Assert.Equal(new[] { 15, 20, 25 }, both.Select(r => r.Quantity).OrderBy(q => q));
        Assert.Equal(new[] { 30 }, difference.Select(r => r.Quantity).OrderBy(q => q));
    }

    /// <summary>拒收口在真库现场各跑一遍：带分页的腿与套娃的腿，链上当场抛 —— 两句在任何一家一样。</summary>
    [Fact]
    public async Task Legs_with_paging_and_nested_unions_are_refused()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<QuickSqlException>(
            () => Leg1.Take(2).UnionAll(Leg2).ToListAsync(ct));

        await Assert.ThrowsAsync<QuickSqlException>(
            () => Leg1.UnionAll(Leg2).UnionAll(Leg1).ToListAsync(ct));
    }

    /// <summary>拒收口的第三颗（评审轮补）：腿上点过 IgnoreQueryFilters 的当场拒 —— 过滤器名单不进模型，
    /// 合并之后外层会把过滤器压回整串，点了名的腿静默失效正是本仓库最不肯做的形状。</summary>
    [Fact]
    public async Task Legs_with_ignored_filters_are_refused()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<QuickSqlException>(
            () => Orm.Select<UnionRow>().Where(r => r.Sku!.StartsWith(Batch)).IgnoreQueryFilters()
                .UnionAll(Leg2).ToListAsync(ct));
    }
}

/// <summary>合并链的靶实体：Bucket 是腿的分界（每腿一行谓词），Quantity 带重叠段留给去重档。
/// 只归 <see cref="UnionPathTests"/> 写。</summary>
[Table("QuickSqlUnionRow")]
public class UnionRow
{
    public int Id { get; set; }

    [QuickColumn(Length = 64)]
    public string? Sku { get; set; }

    public int Bucket { get; set; }

    public int Quantity { get; set; }
}
