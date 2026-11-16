using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.Data.Sqlite;
using QuickSql.Benchmarks.Live;

namespace QuickSql.Benchmarks.Live;

/// <summary>读侧四个形状类共用的守门与 SQL（口径：docs/quicksql/plan-cross-orm-bench.md）。
///
/// <para><b>形状怎么选的</b>：离线套件 §12.8 里四家（含 Dapper）都能表达、且表达形状逐臂一致的那几格 ——
/// <c>SelectSimple</c>（8 列整行）、<c>MaterializeWide</c>（30 列整行，含 decimal/Guid/DateTime/枚举的落位）、
/// <c>SelectJoin3</c>（三表联接 + 三列投影，离线片的同形状）、<c>EmptyFixed</c>（空结果固定开销，
/// 树册 scale 档的移植，量"每次执行都要付一次"的那笔）。<b>MetadataLookup 与 ChangeTrackerDetect10k 不进来</b>：
/// 前者是本家的元数据税，别家没有对应物；后者量的变更跟踪只有本家有 —— 没有对照物的形状进了表就是一行无意义的数。</para>
///
/// <para><b>每条链都带 <c>ORDER BY Id</c></b>：抽查守门要按下标取行，无序的行序守不了门；
/// 主键上的 ORDER BY 在 SQLite 里是索引顺扫，五臂交出去的 SQL 工作量逐臂一致。</para>
///
/// <para><b>BDN 一类只许一枚 baseline，所以四个形状各成一类</b>（每类各开一颗装置库，
/// GlobalSetup 的建库种子成本不在测量之内）。<b>派生类不能 sealed</b>：BDN 靠生成子类跑基准
/// （与 Benchmarks.cs 同一条纪律）。<b>SelectSimpleDto 不进本版</b>：离线片已经发现"每次执行装配一次
/// 投影委托"没有指纹缓存（Benchmarks.cs 里 SelectSimpleDto 那段注释），本家这一格的账还没还清 ——
/// 还清之前量它，对照表里那一格量的就是已知欠账而不是形状本身。等那笔落地了再补。</para></summary>
static class LiveRead
{
    public const string SelectOrderSql = """
        select "Id", "CustomerName", "CustomerId", "Amount", "State", "CreatedAt", "PaidAt", "Note"
        from "BenchOrder" order by "Id"
        """;

    public const string EmptyOrderSql = "select \"Id\" from \"BenchOrder\" where \"Id\" < 0";

    public const string JoinSql = """
        select o."Id", c."City", i."Quantity"
        from "BenchOrder" o
        inner join "BenchCustomer" c on o."CustomerId" = c."Id"
        inner join "BenchItem" i on i."OrderId" = o."Id"
        order by o."Id"
        """;

    public static void GuardOrderRows(List<BenchOrder> rows, string arm)
    {
        LiveGuard.Rows(rows, LiveArms.ReadRows, $"SelectSimple · {arm}");
        LiveGuard.That(rows[7].Amount == LiveArms.AmountOf(7), $"SelectSimple · {arm} 的 decimal 落位（row 7 Amount）");
        LiveGuard.That(rows[7].State == BenchState.Shipped, $"SelectSimple · {arm} 的枚举落位（row 7 State）");
        LiveGuard.That(rows[7].CreatedAt == LiveArms.CreatedAtOf(7), $"SelectSimple · {arm} 的 DateTime 落位（row 7 CreatedAt）");
        LiveGuard.That(rows[7].PaidAt == LiveArms.PaidAtOf(7), $"SelectSimple · {arm} 的可空 DateTime 落位（row 7 PaidAt）");
    }

    public static void GuardWideRows(List<WideRow> rows, string arm)
    {
        LiveGuard.Rows(rows, LiveArms.ReadRows, $"MaterializeWide · {arm}");
        LiveGuard.That(rows[9].Money1 == LiveArms.AmountOf(9), $"MaterializeWide · {arm} 的 decimal 落位（row 9 Money1）");
        LiveGuard.That(rows[9].Money2 == 9 + 1.5m, $"MaterializeWide · {arm} 的第二颗 decimal 落位（row 9 Money2）");
        LiveGuard.That(rows[9].Key1 == LiveArms.KeyOf(9), $"MaterializeWide · {arm} 的 Guid 落位（row 9 Key1）");
        LiveGuard.That(rows[9].State == BenchState.Placed, $"MaterializeWide · {arm} 的枚举落位（row 9 State）");
        LiveGuard.That(rows[9].Flag1 == (9 % 2 == 0), $"MaterializeWide · {arm} 的 bool 落位（row 9 Flag1）");
        LiveGuard.That(rows[9].CancelledAt == null, $"MaterializeWide · {arm} 的可空 DateTime null 落位（row 9 CancelledAt）");
    }

    public static void GuardLineRows(List<Line> rows, string arm)
    {
        LiveGuard.Rows(rows, LiveArms.ReadRows, $"SelectJoin3 · {arm}");
        LiveGuard.That(rows[7].City == LiveArms.CityOf(7), $"SelectJoin3 · {arm} 的联接取值（row 7 City）");
        LiveGuard.That(rows[7].Quantity == 0, $"SelectJoin3 · {arm} 的联接取值（row 7 Quantity）");
        LiveGuard.That(rows[64].City == LiveArms.CityOf(64), $"SelectJoin3 · {arm} 的联接取值（row 64 City）");
    }

    public static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTime? Date(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}

/// <summary>SelectSimple：8 列整行 × 1001 行。</summary>
[MemoryDiagnoser]
public class LiveSelectSimpleBench
{
    LiveArms _arms = null!;

    [GlobalSetup]
    public void Setup() => _arms = LiveArms.Create("read-order");

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "SelectSimple · QuickSql（基准臂）")]
    public async Task<List<BenchOrder>> QuickSql()
    {
        var rows = await _arms.QuickSql.Select<BenchOrder>().OrderBy(o => o.Id).ToListAsync();
        LiveRead.GuardOrderRows(rows, "QuickSql");
        return rows;
    }

    [Benchmark(Description = "SelectSimple · Dapper（业界锚点）")]
    public async Task<List<BenchOrder>> DapperArm()
    {
        using var conn = _arms.OpenRaw();
        var rows = (await conn.QueryAsync<BenchOrder>(LiveRead.SelectOrderSql)).ToList();
        LiveRead.GuardOrderRows(rows, "Dapper");
        return rows;
    }

    [Benchmark(Description = "SelectSimple · FreeSql")]
    public async Task<List<BenchOrder>> FreeSqlArm()
    {
        var rows = await _arms.FreeSql.Select<BenchOrder>().OrderBy(o => o.Id).ToListAsync();
        LiveRead.GuardOrderRows(rows, "FreeSql");
        return rows;
    }

    [Benchmark(Description = "SelectSimple · SqlSugar")]
    public async Task<List<BenchOrder>> SqlSugarArm()
    {
        var rows = await _arms.Sugar.Queryable<BenchOrder>().OrderBy(o => o.Id).ToListAsync();
        LiveRead.GuardOrderRows(rows, "SqlSugar");
        return rows;
    }

    [Benchmark(Description = "SelectSimple · 裸 ADO（地板）")]
    public async Task<List<BenchOrder>> RawAdo()
    {
        var rows = new List<BenchOrder>(LiveArms.ReadRows);

        using var conn = _arms.OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = LiveRead.SelectOrderSql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new BenchOrder
            {
                Id = reader.GetInt32(0),
                CustomerName = LiveRead.Text(reader, 1),
                CustomerId = reader.GetInt32(2),
                Amount = reader.GetDecimal(3),
                State = (BenchState) reader.GetInt32(4),
                CreatedAt = reader.GetDateTime(5),
                PaidAt = LiveRead.Date(reader, 6),
                Note = LiveRead.Text(reader, 7),
            });
        }

        LiveRead.GuardOrderRows(rows, "裸 ADO");
        return rows;
    }
}

/// <summary>MaterializeWide：30 列整行 × 1001 行，落位压力格。</summary>
[MemoryDiagnoser]
public class LiveMaterializeWideBench
{
    LiveArms _arms = null!;

    [GlobalSetup]
    public void Setup() => _arms = LiveArms.Create("read-wide");

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "MaterializeWide · QuickSql（基准臂）")]
    public async Task<List<WideRow>> QuickSql()
    {
        var rows = await _arms.QuickSql.Select<WideRow>().OrderBy(o => o.Id).ToListAsync();
        LiveRead.GuardWideRows(rows, "QuickSql");
        return rows;
    }

    [Benchmark(Description = "MaterializeWide · Dapper（业界锚点）")]
    public async Task<List<WideRow>> DapperArm()
    {
        using var conn = _arms.OpenRaw();
        var rows = (await conn.QueryAsync<WideRow>("select * from \"BenchWideRow\" order by Id")).ToList();
        LiveRead.GuardWideRows(rows, "Dapper");
        return rows;
    }

    [Benchmark(Description = "MaterializeWide · FreeSql")]
    public async Task<List<WideRow>> FreeSqlArm()
    {
        var rows = await _arms.FreeSql.Select<WideRow>().OrderBy(o => o.Id).ToListAsync();
        LiveRead.GuardWideRows(rows, "FreeSql");
        return rows;
    }

    [Benchmark(Description = "MaterializeWide · SqlSugar")]
    public async Task<List<WideRow>> SqlSugarArm()
    {
        var rows = await _arms.Sugar.Queryable<WideRow>().OrderBy(o => o.Id).ToListAsync();
        LiveRead.GuardWideRows(rows, "SqlSugar");
        return rows;
    }

    [Benchmark(Description = "MaterializeWide · 裸 ADO（地板）")]
    public async Task<List<WideRow>> RawAdo()
    {
        var rows = new List<WideRow>(LiveArms.ReadRows);

        using var conn = _arms.OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select * from \"BenchWideRow\" order by Id";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new WideRow
            {
                Id = reader.GetInt32(0),
                Code1 = LiveRead.Text(reader, 1),
                Code2 = LiveRead.Text(reader, 2),
                Code3 = LiveRead.Text(reader, 3),
                Name1 = reader.GetString(4),
                Name2 = reader.GetString(5),
                Name3 = reader.GetString(6),
                Note = LiveRead.Text(reader, 7),
                Remark = LiveRead.Text(reader, 8),
                Tag1 = LiveRead.Text(reader, 9),
                Tag2 = LiveRead.Text(reader, 10),
                Qty1 = reader.GetInt32(11),
                Qty2 = reader.GetInt32(12),
                Qty3 = reader.GetInt32(13),
                Seq = reader.GetInt64(14),
                Version = reader.GetInt64(15),
                Money1 = reader.GetDecimal(16),
                Money2 = reader.GetDecimal(17),
                Money3 = reader.GetDecimal(18),
                CreatedAt = reader.GetDateTime(19),
                UpdatedAt = reader.GetDateTime(20),
                SettledAt = reader.GetDateTime(21),
                PaidAt = LiveRead.Date(reader, 22),
                CancelledAt = LiveRead.Date(reader, 23),
                Key1 = reader.GetGuid(24),
                Key2 = reader.GetGuid(25),
                Flag1 = reader.GetBoolean(26),
                Flag2 = reader.GetBoolean(27),
                Flag3 = reader.GetBoolean(28),
                State = (BenchState) reader.GetInt32(29),
            });
        }

        LiveRead.GuardWideRows(rows, "裸 ADO");
        return rows;
    }
}

/// <summary>SelectJoin3：三表联接 + 三列投影 × 1001 行（离线片同形状）。
/// FreeSql/SqlSugar 走两家文档的标准多表形状（<c>Select&lt;T1,T2,T3&gt;()</c> / <c>Queryable&lt;T1,T2,T3&gt;()</c>
/// 起链、三参 lambda 写联接与投影）；投影用构造函数式 —— 位置记录没有无参构造器，
/// MemberInit 写法在表达式树里根本编不过（第一版就是这么红的）。</summary>
[MemoryDiagnoser]
public class LiveSelectJoin3Bench
{
    LiveArms _arms = null!;

    [GlobalSetup]
    public void Setup() => _arms = LiveArms.Create("read-join");

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "SelectJoin3 · QuickSql（基准臂）")]
    public async Task<List<Line>> QuickSql()
    {
        var rows = await _arms.QuickSql.Select<BenchOrder>()
            .InnerJoin<BenchCustomer>((o, c) => o.CustomerId == c.Id)
            .InnerJoin<BenchItem>((o, c, i) => i.OrderId == o.Id)
            .OrderBy((o, c, i) => o.Id)
            .ToListAsync((o, c, i) => new Line(o.Id, c.City, i.Quantity));

        LiveRead.GuardLineRows(rows, "QuickSql");
        return rows;
    }

    [Benchmark(Description = "SelectJoin3 · Dapper（业界锚点）")]
    public async Task<List<Line>> DapperArm()
    {
        using var conn = _arms.OpenRaw();
        var rows = (await conn.QueryAsync<Line>(LiveRead.JoinSql)).ToList();
        LiveRead.GuardLineRows(rows, "Dapper");
        return rows;
    }

    [Benchmark(Description = "SelectJoin3 · FreeSql")]
    public async Task<List<Line>> FreeSqlArm()
    {
        var rows = await _arms.FreeSql.Select<BenchOrder, BenchCustomer, BenchItem>()
            .InnerJoin((o, c, i) => o.CustomerId == c.Id)
            .InnerJoin((o, c, i) => i.OrderId == o.Id)
            .OrderBy((o, c, i) => o.Id)
            .ToListAsync((o, c, i) => new Line(o.Id, c.City, i.Quantity));

        LiveRead.GuardLineRows(rows, "FreeSql");
        return rows;
    }

    [Benchmark(Description = "SelectJoin3 · SqlSugar")]
    public async Task<List<Line>> SqlSugarArm()
    {
        var rows = await _arms.Sugar.Queryable<BenchOrder>()
            .InnerJoin<BenchCustomer>((o, c) => o.CustomerId == c.Id)
            .InnerJoin<BenchItem>((o, c, i) => i.OrderId == o.Id)
            .OrderBy((o, c, i) => o.Id)
            .Select((o, c, i) => new Line(o.Id, c.City, i.Quantity))
            .ToListAsync();

        LiveRead.GuardLineRows(rows, "SqlSugar");
        return rows;
    }

    [Benchmark(Description = "SelectJoin3 · 裸 ADO（地板）")]
    public async Task<List<Line>> RawAdo()
    {
        var rows = new List<Line>(LiveArms.ReadRows);

        using var conn = _arms.OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = LiveRead.JoinSql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new Line(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));

        LiveRead.GuardLineRows(rows, "裸 ADO");
        return rows;
    }
}

/// <summary>EmptyFixed：空结果的每次执行固定开销（树册 scale 档的移植）。
/// 用 <c>WHERE Id &lt; 0</c> 打在同一张 BenchOrder 上，同表同列数，只差结果为空。</summary>
[MemoryDiagnoser]
public class LiveEmptyFixedBench
{
    LiveArms _arms = null!;

    [GlobalSetup]
    public void Setup() => _arms = LiveArms.Create("read-empty");

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "EmptyFixed · QuickSql（基准臂）")]
    public async Task<List<BenchOrder>> QuickSql()
    {
        var rows = await _arms.QuickSql.Select<BenchOrder>().Where(o => o.Id < 0).ToListAsync();
        LiveGuard.Rows(rows, 0, "EmptyFixed · QuickSql（空表交回了行）");
        return rows;
    }

    [Benchmark(Description = "EmptyFixed · Dapper（业界锚点）")]
    public async Task<List<BenchOrder>> DapperArm()
    {
        using var conn = _arms.OpenRaw();
        var rows = (await conn.QueryAsync<BenchOrder>(LiveRead.EmptyOrderSql)).ToList();
        LiveGuard.Rows(rows, 0, "EmptyFixed · Dapper（空表交回了行）");
        return rows;
    }

    [Benchmark(Description = "EmptyFixed · FreeSql")]
    public async Task<List<BenchOrder>> FreeSqlArm()
    {
        var rows = await _arms.FreeSql.Select<BenchOrder>().Where(o => o.Id < 0).ToListAsync();
        LiveGuard.Rows(rows, 0, "EmptyFixed · FreeSql（空表交回了行）");
        return rows;
    }

    [Benchmark(Description = "EmptyFixed · SqlSugar")]
    public async Task<List<BenchOrder>> SqlSugarArm()
    {
        var rows = await _arms.Sugar.Queryable<BenchOrder>().Where(o => o.Id < 0).ToListAsync();
        LiveGuard.Rows(rows, 0, "EmptyFixed · SqlSugar（空表交回了行）");
        return rows;
    }

    [Benchmark(Description = "EmptyFixed · 裸 ADO（地板）")]
    public async Task<List<BenchOrder>> RawAdo()
    {
        var rows = new List<BenchOrder>();

        using var conn = _arms.OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = LiveRead.EmptyOrderSql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new BenchOrder { Id = reader.GetInt32(0) });

        LiveGuard.Rows(rows, 0, "EmptyFixed · 裸 ADO（空表交回了行）");
        return rows;
    }
}
