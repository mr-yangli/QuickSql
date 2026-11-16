using BenchmarkDotNet.Attributes;
using QuickSql.Benchmarks.Live;

namespace QuickSql.Benchmarks.Live;

/// <summary>SQL 构建三臂对照（Dapper 没有构建器，没有对照物的臂不进表 —— 与读侧同一纪律）。
/// 形状对齐离线套件的 <c>SqlBuildBench</c>：100 步 Where → 渲染出文本，外加一步 Where 的截距。
///
/// <para><b>口径差必须先说</b>：三家的 ToSql 交回的不是同一种文本 —— QuickSql 发参数占位（@p0 那一族），
/// FreeSql/SqlSugar 的渲染会把值内联进文本。所以这一格量的是"各家把一条链变成文本要花多少 CPU"，
/// <b>不是</b>"同一段工作的三种实现"，文本大小本身就在各家那一格里。册子里照此解读。</para>
///
/// <para>链在每次迭代里从零搭起（离线片同形状）：量的是"链构建 + 渲染"全程，不是"第二次读缓存"。</para></summary>
[MemoryDiagnoser]
public class LiveSqlBuildBench
{
    LiveArms _arms = null!;

    [GlobalSetup]
    public void Setup() => _arms = LiveArms.Create("build");

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "SqlBuild100 · QuickSql（基准臂）")]
    public string SqlBuild100Qs()
    {
        var query = _arms.QuickSql.Select<BenchOrder>();

        for (var i = 0; i < 100; i++)
            query = query.Where(o => o.Id > i);

        return query.ToSql();
    }

    [Benchmark(Description = "SqlBuild100 · FreeSql")]
    public string SqlBuild100Fs()
    {
        var query = _arms.FreeSql.Select<BenchOrder>();

        for (var i = 0; i < 100; i++)
            query = query.Where(o => o.Id > i);

        return query.ToSql();
    }

    [Benchmark(Description = "SqlBuild100 · SqlSugar")]
    public string SqlBuild100Ss()
    {
        var query = _arms.Sugar.Queryable<BenchOrder>();

        for (var i = 0; i < 100; i++)
            query = query.Where(o => o.Id > i);

        return query.ToSqlString();
    }

    [Benchmark(Description = "SqlBuild1（截距）· QuickSql")]
    public string SqlBuild1Qs()
    {
        var query = _arms.QuickSql.Select<BenchOrder>().Where(o => o.Id > 0);

        return query.ToSql();
    }

    [Benchmark(Description = "SqlBuild1（截距）· FreeSql")]
    public string SqlBuild1Fs()
    {
        var query = _arms.FreeSql.Select<BenchOrder>().Where(o => o.Id > 0);

        return query.ToSql();
    }

    [Benchmark(Description = "SqlBuild1（截距）· SqlSugar")]
    public string SqlBuild1Ss()
    {
        var query = _arms.Sugar.Queryable<BenchOrder>().Where(o => o.Id > 0);

        return query.ToSqlString();
    }
}
