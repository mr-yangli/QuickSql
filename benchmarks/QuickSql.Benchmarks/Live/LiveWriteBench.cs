using BenchmarkDotNet.Attributes;
using Dapper;
using QuickSql.Benchmarks.Live;

namespace QuickSql.Benchmarks.Live;

/// <summary>写侧一格：1000 行 × 30 列批量插入 × 四臂。形状对齐离线套件的 <c>BulkInsertBench</c>
/// （同档行数、同一份 <see cref="WideRow"/> 实体、同一档"值各不相同"的赋值）。
///
/// <para><b>每臂走的是各家用户的默认写法，不是谁的极限</b>：QuickSql <c>InsertRange</c>（默认装配口，
/// 整型 Id 按自增约定省列，29 列）；FreeSql <c>Insert(list)</c>、SqlSugar <c>Insertable(list)</c>、
/// Dapper 逐行参数化 <c>Execute(sql, rows)</c>（Dapper 的批量惯用法：一条显式事务包 1000 句 ——
/// 30 列显式 Id）。谁家在 SQLite 上把多值插到几百行一批、谁家逐行发，就是这一格要量出来的形状。</para>
///
/// <para><b>[IterationSetup] 砸表重建</b>：不重建的话行数跨迭代滚雪球（第 k 次迭代插进一张已有
/// k×1000 行的表），页缓存那笔账会算到跑得晚的臂头上。重建是一次常数开销、在被测量之外。
/// BDN 对 IterationSetup 的惯例性警告在这里不成立的前提就是重建便宜且恒定 —— 两个条件都验过。</para></summary>
[MemoryDiagnoser]
public class LiveWriteBench
{
    LiveArms _arms = null!;

    WideRow[] _rows = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arms = LiveArms.Create("write");

        // 与离线 BulkInsertBench 同一档赋值：值各不相同，免得装箱全命中小整数缓存。
        _rows = new WideRow[LiveArms.WriteRows];

        for (var row = 0; row < _rows.Length; row++)
        {
            _rows[row] = new WideRow
            {
                Id = row,
                Code1 = "c" + row,
                Qty1 = row,
                Money1 = row + 0.5m,
                Key1 = LiveArms.KeyOf(row),
                State = LiveArms.StateOf(row),
            };
        }
    }

    /// <summary>首个迭代之前也砸一遍：GlobalSetup 建的表带着种子数据，那一千行不该算进任何一臂的账。</summary>
    [IterationSetup]
    public void ResetScratch() => _arms.RecreateWideTable();

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "BulkInsert 1000×30 · QuickSql（基准臂，默认装配口 29 列）")]
    public async Task<int> BulkInsertQs()
    {
        var affected = await _arms.QuickSql.InsertRange(_rows).ExecuteAffrowsAsync();
        Guard(affected, "QuickSql");
        return affected;
    }

    [Benchmark(Description = "BulkInsert 1000×30 · Dapper（逐行参数化 + 一条事务）")]
    public async Task<int> BulkInsertDapper()
    {
        using var conn = _arms.OpenRaw();
        using var tran = conn.BeginTransaction();

        var affected = await conn.ExecuteAsync(InsertSql, _rows, tran);
        tran.Commit();

        Guard(affected, "Dapper");
        return affected;
    }

    [Benchmark(Description = "BulkInsert 1000×30 · FreeSql")]
    public async Task<int> BulkInsertFs()
    {
        var affected = await _arms.FreeSql.Insert(_rows).ExecuteAffrowsAsync();
        Guard(affected, "FreeSql");
        return affected;
    }

    [Benchmark(Description = "BulkInsert 1000×30 · SqlSugar")]
    public async Task<int> BulkInsertSs()
    {
        var affected = await _arms.Sugar.Insertable(_rows).ExecuteCommandAsync();
        Guard(affected, "SqlSugar");
        return affected;
    }

    /// <summary>列序与 <see cref="LiveArms.WideRowDdl"/> 一致；Id 显式（另三家不受 QuickSql 那颗
    /// "整型 Id 视作自增"约定管，列差 1/30 属口径注记，见类注释）。</summary>
    const string InsertSql = """
        insert into "BenchWideRow" (
          "Id", "Code1", "Code2", "Code3", "Name1", "Name2", "Name3", "Note", "Remark", "Tag1", "Tag2",
          "Qty1", "Qty2", "Qty3", "Seq", "Version", "Money1", "Money2", "Money3",
          "CreatedAt", "UpdatedAt", "SettledAt", "PaidAt", "CancelledAt",
          "Key1", "Key2", "Flag1", "Flag2", "Flag3", "State"
        ) values (
          @Id, @Code1, @Code2, @Code3, @Name1, @Name2, @Name3, @Note, @Remark, @Tag1, @Tag2,
          @Qty1, @Qty2, @Qty3, @Seq, @Version, @Money1, @Money2, @Money3,
          @CreatedAt, @UpdatedAt, @SettledAt, @PaidAt, @CancelledAt,
          @Key1, @Key2, @Flag1, @Flag2, @Flag3, @State
        )
        """;

    static void Guard(int affected, string arm)
    {
        if (affected != LiveArms.WriteRows)
            throw new InvalidOperationException(
                $"三方对照守门没过（BulkInsert · {arm}）：交回受影响 {affected} 行，实体给了 {LiveArms.WriteRows} 行 —— 写侧少写或多写都会伪装成快，先红再谈数。");
    }
}
