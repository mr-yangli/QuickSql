using BenchmarkDotNet.Attributes;
using QuickSql.Providers;

namespace QuickSql.Benchmarks.Live;

/// <summary>PG 写侧 5 万行规模对照（<see cref="LiveWriteBenchPostgreSql"/> 的放大版，复用 PgArms）。
/// 1000 行时 Auto 在门槛下选 L3 多值；5 万行远超门槛，Auto 应选 L1 二进制 COPY——本格量量大后各臂的缩放。
/// Dapper 逐行 5 万次远程往返是线性慢（分钟级），不入本格（其 1000 行已量过 5.5 s）。
/// 构建配置不对称同前：FreeSql/SqlSugar Debug 税就地标注。</summary>
[MemoryDiagnoser]
public class LiveWriteBenchPostgreSqlMass
{
    PgArms _arms = null!;
    WideRow[] _rows = null!;

    const int MassRows = 50_000;

    [GlobalSetup]
    public void Setup()
    {
        _arms = PgArms.Create();
        _rows = PgArms.Rows(MassRows);
    }

    [IterationSetup]
    public void Reset() => _arms.TruncateWideTable();

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "PG 5万行 · QuickSql Auto（ExecuteBulkAsync 门槛上选 L1 COPY，Release）")]
    public async Task<int> BulkInsertQsAuto()
    {
        var report = await _arms.QuickSql.InsertRange(_rows).ExecuteBulkAsync();
        Guard(report.RowCount, "QuickSql-Auto");
        return report.RowCount;
    }

    [Benchmark(Description = "PG 5万行 · QuickSql 显式 L1 二进制 COPY（CopyChannel，Release）")]
    public async Task<int> BulkInsertQsL1()
    {
        var report = await _arms.QuickSql.InsertRange(_rows).WithBulk(BulkMethod.CopyChannel).ExecuteBulkAsync();
        Guard(report.RowCount, "QuickSql-L1");
        return report.RowCount;
    }

    [Benchmark(Description = "PG 5万行 · QuickSql L3 多值VALUES（ExecuteAffrowsAsync，Release）")]
    public async Task<int> BulkInsertQsL3()
    {
        var affected = await _arms.QuickSql.InsertRange(_rows).ExecuteAffrowsAsync();
        Guard(affected, "QuickSql-L3");
        return affected;
    }

    [Benchmark(Description = "PG 5万行 · FreeSql（NuGet Debug 构建·JIT 关优化·时间偏悲观）")]
    public async Task<int> BulkInsertFs()
    {
        var affected = await _arms.FreeSql.Insert(_rows).IgnoreColumns(x => x.Id).ExecuteAffrowsAsync();
        Guard(affected, "FreeSql");
        return affected;
    }

    [Benchmark(Description = "PG 5万行 · SqlSugar（NuGet Debug 构建·JIT 关优化·时间偏悲观）")]
    public async Task<int> BulkInsertSs()
    {
        var affected = await _arms.Sugar.Insertable(_rows).IgnoreColumns(x => x.Id).ExecuteCommandAsync();
        Guard(affected, "SqlSugar");
        return affected;
    }

    static void Guard(int affected, string arm)
    {
        if (affected != MassRows)
            throw new InvalidOperationException($"PG 5万行守门没过（{arm}）：受影响 {affected} 行，给了 {MassRows} 行。");
    }
}