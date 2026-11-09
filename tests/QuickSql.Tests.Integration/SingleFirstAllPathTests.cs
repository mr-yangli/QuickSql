using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>单行出口三颗（<c>First</c>/<c>Single</c>/<c>SingleOrDefault</c>）与全称量词（<c>All</c>）
/// 在<b>真库</b>上的那一半。离线册（<c>FirstSingleAllTests</c>）钉的是发射形状（取数帽 1/2、计数加取反谓词），
/// 这里钉四件只有活库能答的事：
///
/// <para>① <b>空集那格是数据的事</b>：<c>First</c>/<c>Single</c> 抛 <see cref="QuickSqlException"/>，
/// <c>FirstOrDefault</c>/<c>SingleOrDefault</c> 交 <c>default</c> —— 同一条 SQL，四种公开语义。</para>
///
/// <para>② <b>多行判重</b>：<c>Single</c>/<c>SingleOrDefault</c> 在多行上抛；一行时照常交回。
/// 判重帽截在第 2 行（库里第三行再多也无关），行为由行数判定，帽的形状归离线册。</para>
///
/// <para>③ <b>All 的三值语义</b>：谓词为 NULL 的行按"通过"计（<c>NOT(NULL)</c> 不进计数），
/// 与 <c>NOT EXISTS(… WHERE NOT(谓词))</c> 反形一致 —— 若 NULL 被当"不通过"，带 NULL 行的链上
/// All 永远答 false，那不是 SQL 的答案。</para>
///
/// <para>④ <b>空链上的全称是真的</b>：零行满足条件时 All 答 true（全称量词的空真）。</para>
///
/// <para>靶表只归本文件写，行带批次标签，跨场重跑安全。</para></summary>
public sealed class SingleFirstAllPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static CancellationToken ct => TestContext.Current.CancellationToken;

    readonly string Batch = "qsql-sfa-" + Guid.NewGuid().ToString("N")[..8];

    SingleRow row(int score, int? grade = null) => new() { Sku = $"{Batch}/{score}", Score = score, Grade = grade };

    async Task SeedAsync(params SingleRow[] rows)
    {
        Database.Ready();
        Database.EnsureTable(typeof(SingleRow));

        if (rows.Length > 0)
            await Orm.InsertRange(rows).ExecuteAffrowsAsync(ct);
    }

    IQuery<SingleRow> Rows => Orm.Select<SingleRow>().Where(r => r.Sku!.StartsWith($"{Batch}/"));

    /// <summary>①：空集时四口各答各的 —— 抛的两颗与给 default 的两颗发同一条 SQL，
    /// 差异全在读侧行数判定。</summary>
    [Fact]
    public async Task Empty_set_throws_for_first_and_single_but_defaults_for_the_soft_twins()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<QuickSqlException>(() => Rows.FirstAsync(ct));
        await Assert.ThrowsAsync<QuickSqlException>(() => Rows.SingleAsync(ct));

        Assert.Null(await Rows.FirstOrDefaultAsync(ct));
        Assert.Null(await Rows.SingleOrDefaultAsync(ct));
    }

    /// <summary>②：恰一行时 Single 交回那一行；多行时 Single/SingleOrDefault 都抛，
    /// First 照旧取一行（"多条里取一条"不是它的职责，取舍归调用方的 OrderBy）。</summary>
    [Fact]
    public async Task Single_refuses_multiple_rows_while_first_takes_one()
    {
        await SeedAsync(row(1), row(2), row(3));

        var exact = $"{Batch}/2";

        var only = await Orm.Select<SingleRow>()
            .Where(r => r.Sku == exact)
            .SingleAsync(ct);

        Assert.Equal(2, only.Score);

        await Assert.ThrowsAsync<QuickSqlException>(() => Rows.SingleAsync(ct));
        await Assert.ThrowsAsync<QuickSqlException>(() => Rows.SingleOrDefaultAsync(ct));

        var first = await Rows.FirstAsync(ct);

        Assert.Contains(first.Score, new[] { 1, 2, 3 });
    }

    /// <summary>③+④：带 NULL 行的三值语义（<c>Grade &gt; -5</code> 在 5 与 0 上都真、在 NULL 上
    /// 按通过计 —— 全称成立）；<c>Grade &gt; 1</code> 有一行不满足，全称不成立；
    /// 零行链上 All 答真（全称量词的空真）。</summary>
    [Fact]
    public async Task All_treats_null_predicates_as_passing_and_vacuous_chains_as_true()
    {
        await SeedAsync(row(1, grade: null), row(2, grade: 5), row(3, grade: 0));

        // 有一行（Grade=0）不满足 → false。
        Assert.False(await Rows.AllAsync(r => r.Grade! > 1, ct));

        // 全部满足（NULL 行按通过计，5 与 0 都 > -5）→ true。若 NULL 被"不通过"计，这一颗就是 false。
        Assert.True(await Rows.AllAsync(r => r.Grade! > -5, ct));

        // 空真：一条零行的链上（前缀没有匹配），全称量词为真。
        var nowhere = Orm.Select<SingleRow>().Where(r => r.Sku!.StartsWith($"{Batch}/nope/"));

        Assert.True(await nowhere.AllAsync(r => r.Score < 0, ct));
    }
}

/// <summary>单行/全称口的靶实体：Score 是无 NULL 的普通列（First/Single 的取数对象），
/// Grade 可空（All 的三值语义要一颗真的 NULL）。只归 <see cref="SingleFirstAllPathTests"/> 写。</summary>
[Table("QuickSqlSingleRow")]
public class SingleRow
{
    public int Id { get; set; }

    [QuickColumn(Length = 64)]
    public string? Sku { get; set; }

    public int Score { get; set; }

    public int? Grade { get; set; }
}
