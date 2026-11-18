using System.Data.Common;
using BenchmarkDotNet.Attributes;
using Dapper;
using QuickSql.DriverLoading;
using QuickSql.Generated;
using QuickSql.Schema;
using SqlSugar;
using QuickSqlXuguProvider = QuickSql.Providers.Xugu.XuguProvider;

namespace QuickSql.Benchmarks.Live;

/// <summary>虚谷（Xugu）写侧对照（1000 行 × 30 列 WideRow，Id identity 各家省 Id 29 列，标识符折大写）。
/// <b>QuickSql vs Dapper 两臂</b>：FreeSql/SqlSugar 虚谷臂需额外驱动包（SqlSugar.XuguCore.dll 等，本装置未引），暂不入。
/// QuickSql 走 XuguBulk 预算 300（BDN 实测时间拐点，v11.0.0；安全界 2048 实测只作上限出处——
/// 每语句代价随颗数超线性，宽块时间输；仍无装载通道：驱动 XG* 面无装载类），
/// 29 列每块 9 行、1000 行 112 块；兜底 100 时代（334 块）与硬上限 2048 时代（16 块）旧读数备查 plan-cross-orm-bench.md 虚谷注记。
/// 虚谷参数前缀 ':'（非 @），Dapper 裸 SQL 用 :Name1 形。驱动 XuguClient 厂商 4.0.0.8 经 DriverRegistry 运行期解析
/// （drivers/XuguClient.dll，编译期不引用）。表由 QuickSql SyncStructure 建（虚谷类型映射 + identity DDL 由 provider 定）。</summary>
[MemoryDiagnoser]
public class LiveWriteBenchXugu
{
    XuguArms _arms = null!;
    WideRow[] _rows = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arms = XuguArms.Create();
        _rows = XuguArms.Rows(1000);
        _arms.ProbeArms();
    }

    [IterationSetup]
    public void Reset() => _arms.TruncateWideTable();

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "Xugu BulkInsert 1000×30 · QuickSql L3 多值VALUES（XuguBulk 预算300·identity省Id，Release）")]
    public async Task<int> BulkInsertQs()
    {
        var affected = await _arms.QuickSql.InsertRange(_rows).ExecuteAffrowsAsync();
        Guard(affected, "QuickSql");
        return affected;
    }

    [Benchmark(Description = "Xugu BulkInsert 1000×30 · Dapper（逐行 29 列 + 一条事务，Release）")]
    public async Task<int> BulkInsertDapper()
    {
        using var conn = _arms.OpenRaw();
        using var tran = conn.BeginTransaction();
        var affected = await conn.ExecuteAsync(XuguArms.InsertSql, _rows, tran);
        tran.Commit();
        Guard(affected, "Dapper");
        return affected;
    }

    [Benchmark(Description = "Xugu BulkInsert 1000×30 · SqlSugar（NuGet Debug 构建·XuguCoreNew，时间偏悲观）")]
    public async Task<int> BulkInsertSs()
    {
        var affected = await _arms.Sugar.Insertable(_rows).IgnoreColumns(x => x.Id).ExecuteCommandAsync();
        Guard(affected, "SqlSugar");
        return affected;
    }

    [Benchmark(Description = "Xugu BulkInsert 1000×30 · SqlSugar Fastest.BulkCopy（虚谷是否支持待验·Debug）")]
    public async Task<int> BulkInsertSsFastest()
    {
        var affected = await _arms.Sugar.Fastest<WideRow>().BulkCopyAsync(new System.Collections.Generic.List<WideRow>(_rows));
        Guard(affected, "SqlSugar-Fastest");
        return affected;
    }

    static void Guard(int affected, string arm)
    {
        if (affected != 1000)
            throw new InvalidOperationException($"Xugu 对照守门没过（{arm}）：受影响 {affected} 行，给了 1000 行。");
    }
}

/// <summary>虚谷对照组装根（QuickSql + Dapper）。表由 QuickSql SyncStructure 建（先 DROP 残留表再建，免旧表非自增 ID 残留）。
/// 连接取仓库根 quicksql-test.xugu.connection.local.txt（本机凭据不入库）。驱动经 DriverRegistry 解析 XuguClient。</summary>
sealed class XuguArms : IDisposable
{
    public const int WriteRows = 1_000;

    public const string InsertSql = """
        INSERT INTO BENCHWIDEROW (
          CODE1, CODE2, CODE3, NAME1, NAME2, NAME3, NOTE, REMARK, TAG1, TAG2,
          QTY1, QTY2, QTY3, SEQ, VERSION, MONEY1, MONEY2, MONEY3,
          CREATEDAT, UPDATEDAT, SETTLEDAT, PAIDAT, CANCELLEDAT,
          KEY1, KEY2, FLAG1, FLAG2, FLAG3, STATE
        ) VALUES (
          :Code1, :Code2, :Code3, :Name1, :Name2, :Name3, :Note, :Remark, :Tag1, :Tag2,
          :Qty1, :Qty2, :Qty3, :Seq, :Version, :Money1, :Money2, :Money3,
          :CreatedAt, :UpdatedAt, :SettledAt, :PaidAt, :CancelledAt,
          :Key1, :Key2, :Flag1, :Flag2, :Flag3, :State
        )
        """;

    public required string ConnectionString { get; init; }
    public required DbProviderFactory Factory { get; init; }
    public required QuickSqlOrm QuickSql { get; init; }
    public required SqlSugarClient Sugar { get; init; }

    public DbConnection OpenRaw()
    {
        var conn = Factory.CreateConnection()!;
        conn.ConnectionString = ConnectionString;
        conn.Open();
        return conn;
    }

    public static WideRow[] Rows(int count)
    {
        var rows = new WideRow[count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = new WideRow
            {
                Id = i + 1,
                Code1 = "c" + i,
                Qty1 = i,
                Money1 = i + 0.5m,
                Key1 = LiveArms.KeyOf(i),
                Key2 = new Guid(i + 1_000_000, 0, 0, new byte[8]),
                CreatedAt = LiveArms.CreatedAtOf(i),
                UpdatedAt = LiveArms.CreatedAtOf(i),
                SettledAt = LiveArms.CreatedAtOf(i),
                PaidAt = LiveArms.PaidAtOf(i),
                State = LiveArms.StateOf(i),
            };
        return rows;
    }

    public static XuguArms Create()
    {
        var cs = ResolveConnectionString() ?? throw new InvalidOperationException(
            "没有虚谷连接：在仓库根放 quicksql-test.xugu.connection.local.txt。");

        var factory = new DriverRegistry().Factory(QuickSqlXuguProvider.DriverKey);
        var provider = new QuickSqlXuguProvider(factory);
        // 先清残留表（前次跑可能留下非自增 ID 的旧表，ForceSchemaSync 不改列 identity），再 SyncStructure 建全新 identity 表。
        try
        {
            using var drop = factory.CreateConnection()!;
            drop.ConnectionString = cs;
            drop.Open();
            using var cmd = drop.CreateCommand();
            cmd.CommandText = "DROP TABLE BENCHWIDEROW";
            cmd.ExecuteNonQuery();
        }
        catch { /* 表不存在则忽略 */ }

        var quickSql = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = cs,
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        }, new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet));
        _ = quickSql.Select<WideRow>();
        quickSql.SyncStructureAsync().GetAwaiter().GetResult();

        // SqlSugar 虚谷：文档（donet5.com typeId=2640）要求注册 Xugu 驱动程序集到 InstanceFactory.CustomAssemblies，
        // 绕过 SqlSugarCore 按名探测。包 SqlSugar.XuguCoreNew 交付的程序集名实为 SqlSugar.XuguCore.dll，按名加载即可。
        InstanceFactory.CustomAssemblies = new[] { System.Reflection.Assembly.Load("SqlSugar.XuguCore") };
        var sugar = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = cs,
            DbType = DbType.Xugu,
            IsAutoCloseConnection = true,
        });
        sugar.MappingTables.Add("WideRow", "BENCHWIDEROW");

        return new XuguArms { ConnectionString = cs, Factory = factory, QuickSql = quickSql, Sugar = sugar };
    }

    /// <summary>装置探针：QuickSql + Dapper 各写 3 行（identity 自动给号，无跨臂 Id 冲突），读回验 Guid(CHAR36)/decimal 落位，清表。</summary>
    public void ProbeArms()
    {
        var rows = Rows(3);
        GuardProbe(QuickSql.InsertRange(rows).ExecuteAffrowsAsync().GetAwaiter().GetResult(), "QuickSql");
        using (var cnn = OpenRaw())
            GuardProbe(cnn.Execute(InsertSql, rows), "Dapper");

        using (var conn = OpenRaw())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT KEY1, MONEY1 FROM BENCHWIDEROW WHERE QTY1 = 2 ORDER BY ID DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            LiveGuard.That(reader.Read(), "Xugu 对照装置探针：读不回探针行");
            var key = reader.GetString(0);
            LiveGuard.That(Guid.Parse(key) == LiveArms.KeyOf(2), "Xugu 对照装置探针：Key1 Guid(CHAR36) 落位（读到 " + key + "）");
            LiveGuard.That(reader.GetDecimal(1) == 2.5m, "Xugu 对照装置探针：Money1 decimal 标度（应 2.5）");
        }
        TruncateWideTable();
    }

    static void GuardProbe(int affected, string arm)
    {
        if (affected != 3)
            throw new InvalidOperationException($"Xugu 对照装置探针没过（{arm}）：受影响 {affected} 行，给了 3 行。");
    }

    public void TruncateWideTable()
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE BENCHWIDEROW";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Sugar.Dispose();
        try
        {
            using var conn = OpenRaw();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE BENCHWIDEROW";
            cmd.ExecuteNonQuery();
        }
        catch { /* scratch 清理失败不掩主体 */ }
    }

    static string? ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "quicksql-test.xugu.connection.local.txt");
            if (!File.Exists(file)) continue;
            var line = File.ReadLines(file).FirstOrDefault(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l));
            if (line is not null) return line.Trim();
        }
        return null;
    }
}