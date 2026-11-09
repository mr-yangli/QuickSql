using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>集合同步（SqlSugar 的 <c>Storageable</c> 同一语义）在真库上的那一半。钉的是四件事：
///
/// <para>① <b>对账不发一句写</b>：<c>ToStorage</c> 之后库的行数与值必须原封不动 —— "先看账再动手"
/// 是这条链与 <c>InsertOrUpdate</c>（对上就改、对不上就插、没有删除桶）的分界。<b>存在性那一句还只端
/// 键列</b>：整行读回只属于删除桶，那一份才是交出去给人看的。</para>
///
/// <para>② <b>键的三档都对得准</b>：默认主键（自增键"没填"的行直进新增桶，不查库）、点名单列
/// （<c>Sku</c> 对账，<c>Id=0</c> 的行照样能进修改桶 —— 点名键不是自增列，"没填"的捷径不生效）、
/// 匿名类型复合键（两颗列一起算一颗键）。</para>
///
/// <para>③ <b>删除桶两档都有现场</b>：带范围（<c>SplitDelete(范围)</c>）只在自己那一片里读与摘 ——
/// 本表里别的批次留下的行不在被摘之列，测试表可以带历史行重跑；不带范围是整表语义（"库里有而
/// 清单里没有"以整表计），那颗测试不用它，整表档由同一颗分支承着。</para>
///
/// <para>④ <b>执行口各管各的桶</b>：部分执行（只增、只改）动不了别的桶；点名键非主键时修改桶
/// 逐行走 <c>Update</c> 链（SET 键位之外可写的列、WHERE 点名键），主键档走批量 —— 两档都按
/// 裁判读数逐格对账。清单空而删除桶开着、重复键、无键可对，三个拒收口各有各的话。</para>
///
/// <para>裁判纪律与全层同一条：终审一律裸 ADO（<see cref="Database.Rows"/>），不用被测的读口自证清白。
/// 这三张靶表只归本文件写，批次标签隔离跨场运行的行；删除桶会摘别的批次的行（见 ③），所以
/// 断言只落在带本批标签的行上，别的批次死活不进断言。</para></summary>
public sealed class StorageablePathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static CancellationToken ct => TestContext.Current.CancellationToken;

    readonly string Batch = "qsql-stor-" + Guid.NewGuid().ToString("N")[..8];

    StorableRow storable(string sku, int quantity, int id = 0) => new() { Id = id, Sku = $"{Batch}/{sku}", Quantity = quantity };

    StorableCompositeRow composite(int keyA, string keyB, int quantity) => new() { KeyA = keyA, KeyB = $"{Batch}/{keyB}", Quantity = quantity };

    static List<Dictionary<string, string>> StorableRows(string where, params (string Name, object? Value)[] args)
        => Database.Rows("SELECT [Id], [Sku], [Quantity] FROM [dbo].[QuickSqlStorableRow] WHERE " + where, args);

    static List<Dictionary<string, string>> CompositeRows(string where, params (string Name, object? Value)[] args)
        => Database.Rows("SELECT [KeyA], [KeyB], [Quantity] FROM [dbo].[QuickSqlStorableComposite] WHERE " + where, args);

    string Like => "[Sku] LIKE @p";

    (string Name, object? Value) Tag => ("@p", Batch + "%");

    /// <summary>①+②：默认主键档 —— 已有行带 <c>Id</c> 进修改桶、自增键没填的行直进新增桶，
    /// 一口气执行后库里逐格对上。</summary>
    [Fact]
    public async Task Default_key_splits_and_executes_by_primary_key()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        var first = await Orm.Insert(storable("keep", 10)).ExecuteIdentityAsync(ct);
        await Orm.Insert(storable("second", 20)).ExecuteAffrowsAsync(ct);

        var result = await Orm.Storageable(new[]
        {
            storable("keep", 99, id: (int) first),
            storable("fresh", 5),
        }).ToStorageAsync(ct);

        var updated = Assert.Single(result.UpdateList);
        Assert.Equal(first, updated.Id);
        Assert.Equal(99, updated.Quantity);
        Assert.Equal($"{Batch}/keep", updated.Sku);

        var inserted = Assert.Single(result.InsertList);
        Assert.Equal($"{Batch}/fresh", inserted.Sku);
        Assert.Empty(result.DeleteList);

        Assert.Equal(2, await result.ExecuteCommandAsync(ct));

        Assert.Equal("99", Assert.Single(StorableRows("[Id] = @id", ("@id", first)))["Quantity"]);
        Assert.Single(StorableRows(Like + " AND [Quantity] = 5", Tag));
    }

    /// <summary>①：对账不动库 —— 桶分得再准，没调执行器库里就还是原样。</summary>
    [Fact]
    public async Task To_storage_alone_writes_nothing()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        var existing = await Orm.Insert(storable("stay", 10)).ExecuteIdentityAsync(ct);

        var result = await Orm.Storageable(new[]
        {
            storable("stay", 77, id: (int) existing),
            storable("ghost", 1),
        }).ToStorageAsync(ct);

        Assert.Single(result.UpdateList);
        Assert.Single(result.InsertList);

        var judge = Assert.Single(StorableRows("[Id] = @id", ("@id", existing)));
        Assert.Equal("10", judge["Quantity"]);
        Assert.DoesNotContain(StorableRows(Like, Tag), r => r["Sku"] == $"{Batch}/ghost");
    }

    /// <summary>①那一读的宽度：存在性对账只端键列（宽列一个都不回来），整行读回只属于删除桶那一句。
    /// 两句都只发读，而桶仍分得准 —— 键投影没把键读丢的证据就在同一次的账上。</summary>
    [Fact]
    public async Task Reconciliation_reads_only_the_key_columns()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        await Orm.Insert(storable("width", 10)).ExecuteAffrowsAsync(ct);

        var sent = new List<string>();
        var aop = new QuickSqlAop();

        aop.Executing = e => sent.Add(e.Sql.Text);

        var result = await Database.OrmWith(aop)
            .Storageable(new[] { storable("width", 11), storable("width-new", 12) })
            .WhereColumns(r => r.Sku!)
            .SplitDelete(r => r.Sku!.StartsWith(Batch))
            .ToStorageAsync(ct);

        // 两句读：对账在前（谓词是 OR 平铺的键等值），删除桶的整片读回在后（scope 落进 WHERE）。
        Assert.Equal(2, sent.Count);
        Assert.All(sent, s => Assert.Contains(Database.CatalogueName("QuickSqlStorableRow"), s, StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain("Quantity", sent[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quantity", sent[1], StringComparison.OrdinalIgnoreCase);

        Assert.Single(result.UpdateList);
        Assert.Single(result.InsertList);
        Assert.Empty(result.DeleteList);
    }

    /// <summary>②点名键档：<c>Id=0</c> 的行按 <c>Sku</c> 对上就进修改桶 —— 点名键不是自增列，
    /// "没填"的捷径不生效；执行走逐行 <c>Update</c> 链（SET 键位之外可写的列、WHERE 点名键）。</summary>
    [Fact]
    public async Task Where_columns_matches_by_sku_and_updates_without_primary_key()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        await Orm.Insert(storable("by-sku", 10)).ExecuteAffrowsAsync(ct);

        var result = await Orm.Storageable(new[]
        {
            storable("by-sku", 42),
            storable("by-sku-new", 7),
        }).WhereColumns(r => r.Sku!).ToStorageAsync(ct);

        Assert.Single(result.UpdateList);
        Assert.Single(result.InsertList);

        Assert.Equal(2, await result.ExecuteCommandAsync(ct));

        Assert.Equal("42", Assert.Single(StorableRows(Like, ("@p", $"{Batch}/by-sku")))["Quantity"]);
        Assert.Single(StorableRows(Like, ("@p", $"{Batch}/by-sku-new")));
    }

    /// <summary>②复合键档：两颗列一起算一颗键，对上的改、对不上的增。</summary>
    [Fact]
    public async Task Composite_where_columns_diff_by_two_columns()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableCompositeRow));

        await Orm.InsertRange(new[]
        {
            composite(1, "a", 10),
            composite(2, "a", 20),
        }).ExecuteAffrowsAsync(ct);

        var result = await Orm.Storageable(new[]
        {
            composite(1, "a", 33),
            composite(2, "b", 44),
            composite(1, "c", 55),
        }).WhereColumns(r => new { r.KeyA, r.KeyB }).ToStorageAsync(ct);

        Assert.Single(result.UpdateList);
        Assert.Equal(2, result.InsertList.Count);
        Assert.Empty(result.DeleteList);

        Assert.Equal(3, await result.ExecuteCommandAsync(ct));

        Assert.Equal("33", Assert.Single(CompositeRows("[KeyA] = 1 AND [KeyB] = @b", ("@b", $"{Batch}/a")))["Quantity"]);
        Assert.Equal("44", Assert.Single(CompositeRows("[KeyA] = 2 AND [KeyB] = @b", ("@b", $"{Batch}/b")))["Quantity"]);
        Assert.Equal("55", Assert.Single(CompositeRows("[KeyA] = 1 AND [KeyB] = @b", ("@b", $"{Batch}/c")))["Quantity"]);
    }

    /// <summary>③删除桶：库里有而清单里没有的那一批被摘走（本表别的批次的行也在被摘之列 ——
    /// 整表同步的本意），断言只落在本批标签上。</summary>
    [Fact]
    public async Task Split_delete_removes_rows_missing_from_the_list()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        await Orm.InsertRange(new[]
        {
            storable("d-1", 1),
            storable("d-2", 2),
            storable("d-3", 3),
        }).ExecuteAffrowsAsync(ct);

        var result = await Orm.Storageable(new[]
        {
            storable("d-1", 50),
            storable("d-2", 60),
        }).WhereColumns(r => r.Sku!).SplitDelete(r => r.Sku!.StartsWith(Batch)).ToStorageAsync(ct);

        var deleted = Assert.Single(result.DeleteList);
        Assert.Equal($"{Batch}/d-3", deleted.Sku);
        Assert.Equal(2, result.UpdateList.Count);
        Assert.Empty(result.InsertList);

        Assert.Equal(3, await result.ExecuteCommandAsync(ct));

        var mine = StorableRows(Like, Tag);
        Assert.Equal(2, mine.Count);
        Assert.DoesNotContain(mine, r => r["Sku"] == $"{Batch}/d-3");
        Assert.Equal("50", mine.Single(r => r["Sku"] == $"{Batch}/d-1")["Quantity"]);
        Assert.Equal("60", mine.Single(r => r["Sku"] == $"{Batch}/d-2")["Quantity"]);
    }

    /// <summary>④部分执行：只执行新增桶，修改桶分毫未动 —— 各执行口只认自己的那一桶。</summary>
    [Fact]
    public async Task Partial_executors_apply_only_their_bucket()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        var existing = await Orm.Insert(storable("partial", 10)).ExecuteIdentityAsync(ct);

        var result = await Orm.Storageable(new[]
        {
            storable("partial", 77, id: (int) existing),
            storable("partial-new", 5),
        }).ToStorageAsync(ct);

        Assert.Equal(1, await result.ExecuteInsertAsync(ct));

        Assert.Equal(1, StorableRows(Like, Tag).Count(r => r["Sku"] == $"{Batch}/partial-new"));
        Assert.Equal("10", Assert.Single(StorableRows("[Id] = @id", ("@id", existing)))["Quantity"]);

        Assert.Equal(1, await result.ExecuteUpdateAsync(ct));
        Assert.Equal("77", Assert.Single(StorableRows("[Id] = @id", ("@id", existing)))["Quantity"]);
    }

    /// <summary>拒收口一：清单空而删除桶开着 = "清空整表"，同步链不为这一句代劳。</summary>
    [Fact]
    public async Task Empty_list_with_delete_tracked_is_refused()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        await Assert.ThrowsAsync<QuickSqlException>(
            () => Orm.Storageable<StorableRow>([]).SplitDelete().ToStorageAsync(ct));
    }

    /// <summary>拒收口二：同一颗键说两次话没法判增改。</summary>
    [Fact]
    public async Task Duplicate_keys_are_refused()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        await Assert.ThrowsAsync<QuickSqlException>(
            () => Orm.Storageable(new[]
            {
                storable("dup", 1),
                storable("dup", 2),
            }).WhereColumns(r => r.Sku!).ToStorageAsync(ct));
    }

    /// <summary>拒收口三：没有主键也不点名，键对不出来。</summary>
    [Fact]
    public async Task No_primary_key_and_no_columns_is_refused()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableKeylessRow));

        await Assert.ThrowsAsync<QuickSqlException>(
            () => Orm.Storageable<StorableKeylessRow>([new StorableKeylessRow { Name = "x", Quantity = 1 }])
                .ToStorageAsync(ct));
    }

    /// <summary>分块档：清单大过单句参数预算时按预算切开逐块对账，桶的边界与行数一格不差
    /// （预算随 provider 声明走：声明了的按声明、没声明的按保守 100 —— 后者这一跑就是三块往上）。
    /// 只验账与行数，250 行整表逐格对账交给 ③⑤ 那两颗小样本。</summary>
    [Fact]
    public async Task Keys_beyond_the_parameter_budget_still_diff_exactly()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StorableRow));

        var seeded = Enumerable.Range(0, 200)
            .Select(i => storable($"c-{i:D3}", i))
            .ToArray();

        await Orm.InsertRange(seeded).ExecuteAffrowsAsync(ct);

        var listed = seeded
            .Select((r, i) => storable($"c-{i:D3}", i + 1000))
            .Concat(Enumerable.Range(0, 50).Select(i => storable($"n-{i:D3}", i)))
            .ToArray();

        var result = await Orm.Storageable(listed).WhereColumns(r => r.Sku!).ToStorageAsync(ct);

        Assert.Equal(200, result.UpdateList.Count);
        Assert.Equal(50, result.InsertList.Count);
        Assert.Empty(result.DeleteList);

        Assert.Equal(250, await result.ExecuteCommandAsync(ct));

        var judge = Database.Rows(
            "SELECT COUNT(*) AS N FROM [dbo].[QuickSqlStorableRow] WHERE [Sku] LIKE @p",
            ("@p", Batch + "%"));

        Assert.Equal(250, int.Parse((string) judge.Single()["N"], CultureInfo.InvariantCulture));
    }
}

/// <summary>同步链的靶实体（默认键档）：约定命中 <c>Id</c> ⇒ 主键 + 自增，与 <see cref="StorableRow"/>
/// 的对账捷径（自增键"没填"直进新增桶）配套。三张靶表只归 <see cref="StorageablePathTests"/> 写。</summary>
[Table("QuickSqlStorableRow")]
public class StorableRow
{
    public int Id { get; set; }

    [QuickColumn(Length = 48)]
    public string? Sku { get; set; }

    public int Quantity { get; set; }
}

/// <summary>同步链的靶实体（复合键档）：没有主键也没有自增 —— 键完全由 <c>WhereColumns</c> 点名，
/// 两颗列一起算一颗。整型 + 字符串的搭配顺带钉住"两侧同一把规范化尺子"那一格
/// （SQLite 把整数交成长的那一族，不过尺子对账就是全错）。</summary>
[Table("QuickSqlStorableComposite")]
public class StorableCompositeRow
{
    public int KeyA { get; set; }

    [QuickColumn(Length = 48)]
    public string? KeyB { get; set; }

    public int Quantity { get; set; }
}

/// <summary>同步链的拒收口靶实体：没有主键 —— "没有键可对"那一格的话要有现场说得出。</summary>
[Table("QuickSqlStorableKeyless")]
public class StorableKeylessRow
{
    [QuickColumn(Length = 24)]
    public string? Name { get; set; }

    public int Quantity { get; set; }
}
