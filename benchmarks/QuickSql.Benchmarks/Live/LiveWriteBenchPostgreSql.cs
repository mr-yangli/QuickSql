using BenchmarkDotNet.Attributes;
using Dapper;
using FreeSql;
using Npgsql;
using QuickSql.DriverLoading;
using QuickSql.Generated;
using QuickSql.Providers;
using QuickSql.Schema;
using SqlSugar;
using QuickSqlPostgreSqlProvider = QuickSql.Providers.PostgreSql.PostgreSqlProvider;

namespace QuickSql.Benchmarks.Live;

/// <summary>PostgreSQL 写侧五臂对照（1000 行 × 30 列 WideRow，Id SERIAL 各家省 Id 29 列，标识符 quoted 大小写敏感）。
/// QuickSql 两档：Auto 默认（ExecuteBulkAsync，按门槛选 L1 二进制 COPY 或 L3 多值）与显式 L1
/// （WithBulk(CopyChannel) 点名 PostgreSqlBulkCopyChannel BeginBinaryImportAsync）。Dapper 逐行、FreeSql/SqlSugar 默认多值。
/// PG 扩展协议禁多语句合并（Merged=false），多值 VALUES 是单条语句。构建配置不对称同前：FreeSql/SqlSugar Debug 税就地标注。</summary>
[MemoryDiagnoser]
public class LiveWriteBenchPostgreSql
{
    PgArms _arms = null!;
    WideRow[] _rows = null!;

    const string InsertSql = """
        INSERT INTO "BenchWideRow" (
          "Code1", "Code2", "Code3", "Name1", "Name2", "Name3", "Note", "Remark", "Tag1", "Tag2",
          "Qty1", "Qty2", "Qty3", "Seq", "Version", "Money1", "Money2", "Money3",
          "CreatedAt", "UpdatedAt", "SettledAt", "PaidAt", "CancelledAt",
          "Key1", "Key2", "Flag1", "Flag2", "Flag3", "State"
        ) VALUES (
          @Code1, @Code2, @Code3, @Name1, @Name2, @Name3, @Note, @Remark, @Tag1, @Tag2,
          @Qty1, @Qty2, @Qty3, @Seq, @Version, @Money1, @Money2, @Money3,
          @CreatedAt, @UpdatedAt, @SettledAt, @PaidAt, @CancelledAt,
          @Key1, @Key2, @Flag1, @Flag2, @Flag3, @State
        )
        """;

    [GlobalSetup]
    public void Setup()
    {
        _arms = PgArms.Create();
        _rows = PgArms.Rows(1000);
        _arms.ProbeArms();
    }

    [IterationSetup]
    public void Reset() => _arms.TruncateWideTable();

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "PG BulkInsert 1000×30 · QuickSql Auto 默认（ExecuteBulkAsync 按门槛选层，Release）")]
    public async Task<int> BulkInsertQsAuto()
    {
        var report = await _arms.QuickSql.InsertRange(_rows).ExecuteBulkAsync();
        Guard(report.RowCount, "QuickSql-Auto");
        return report.RowCount;
    }

    [Benchmark(Description = "PG BulkInsert 1000×30 · QuickSql 显式 L1 二进制 COPY（CopyChannel 点名，Release）")]
    public async Task<int> BulkInsertQsL1()
    {
        var report = await _arms.QuickSql.InsertRange(_rows).WithBulk(BulkMethod.CopyChannel).ExecuteBulkAsync();
        if (report.Method != BulkMethod.CopyChannel)
            throw new InvalidOperationException($"PG 1000 行场守门没过（QuickSql-L1）：显式点名交回 {report.Method}。{report.Reason}");
        Guard(report.RowCount, "QuickSql-L1");
        return report.RowCount;
    }

    [Benchmark(Description = "PG BulkInsert 1000×30 · Dapper（逐行 29 列 + 一条事务，Release）")]
    public async Task<int> BulkInsertDapper()
    {
        using var conn = _arms.OpenRaw();
        using var tran = conn.BeginTransaction();
        var affected = await conn.ExecuteAsync(InsertSql, _rows, tran);
        tran.Commit();
        Guard(affected, "Dapper");
        return affected;
    }

    [Benchmark(Description = "PG BulkInsert 1000×30 · FreeSql（NuGet Debug 构建·JIT 关优化·时间偏悲观）")]
    public async Task<int> BulkInsertFs()
    {
        var affected = await _arms.FreeSql.Insert(_rows).IgnoreColumns(x => x.Id).ExecuteAffrowsAsync();
        Guard(affected, "FreeSql");
        return affected;
    }

    [Benchmark(Description = "PG BulkInsert 1000×30 · SqlSugar（NuGet Debug 构建·JIT 关优化·时间偏悲观）")]
    public async Task<int> BulkInsertSs()
    {
        var affected = await _arms.Sugar.Insertable(_rows).IgnoreColumns(x => x.Id).ExecuteCommandAsync();
        Guard(affected, "SqlSugar");
        return affected;
    }

    [Benchmark(Description = "PG BulkInsert 1000×30 · SqlSugar Fastest.BulkCopy（Npgsql COPY·与 QuickSql L1 对等·Debug）")]
    public async Task<int> BulkInsertSsFastest()
    {
        var affected = await _arms.Sugar.Fastest<WideRow>().BulkCopyAsync(new System.Collections.Generic.List<WideRow>(_rows));
        Guard(affected, "SqlSugar-Fastest");
        return affected;
    }

    static void Guard(int affected, string arm)
    {
        if (affected != 1000)
            throw new InvalidOperationException($"PG 三方对照守门没过（{arm}）：受影响 {affected} 行，给了 1000 行。");
    }
}

/// <summary>PG 五臂组装根。同一颗库、同一张表（raw PG DDL，Id SERIAL，quoted 标识符，UUID/BOOLEAN/TIMESTAMP/NUMERIC）。
/// 连接串从 QUICKSQL_TEST_CONNECTION 或仓库根 quicksql-test.postgres.connection.local.txt 取（本机凭据，不入库）。
/// FreeSql 走 DataType.PostgreSQL、SqlSugar 走 DbType.PostgreSQL。Npgsql 原生映射 Guid→UUID/bool→BOOLEAN/DateTime→TIMESTAMP。</summary>
sealed class PgArms : IDisposable
{
    public const int WriteRows = 1_000;

    const string ProbeSql = """
        INSERT INTO "BenchWideRow" (
          "Code1", "Code2", "Code3", "Name1", "Name2", "Name3", "Note", "Remark", "Tag1", "Tag2",
          "Qty1", "Qty2", "Qty3", "Seq", "Version", "Money1", "Money2", "Money3",
          "CreatedAt", "UpdatedAt", "SettledAt", "PaidAt", "CancelledAt",
          "Key1", "Key2", "Flag1", "Flag2", "Flag3", "State"
        ) VALUES (
          @Code1, @Code2, @Code3, @Name1, @Name2, @Name3, @Note, @Remark, @Tag1, @Tag2,
          @Qty1, @Qty2, @Qty3, @Seq, @Version, @Money1, @Money2, @Money3,
          @CreatedAt, @UpdatedAt, @SettledAt, @PaidAt, @CancelledAt,
          @Key1, @Key2, @Flag1, @Flag2, @Flag3, @State
        )
        """;

    const string WideRowDdl = """
        CREATE TABLE "BenchWideRow" (
          "Id" SERIAL PRIMARY KEY,
          "Code1" TEXT, "Code2" TEXT, "Code3" TEXT,
          "Name1" TEXT NOT NULL, "Name2" TEXT NOT NULL, "Name3" TEXT NOT NULL,
          "Note" TEXT, "Remark" TEXT, "Tag1" TEXT, "Tag2" TEXT,
          "Qty1" INT NOT NULL, "Qty2" INT NOT NULL, "Qty3" INT NOT NULL,
          "Seq" BIGINT NOT NULL, "Version" BIGINT NOT NULL,
          "Money1" NUMERIC(18,4) NOT NULL, "Money2" NUMERIC(18,4) NOT NULL, "Money3" NUMERIC(18,4) NOT NULL,
          "CreatedAt" TIMESTAMP NOT NULL, "UpdatedAt" TIMESTAMP NOT NULL, "SettledAt" TIMESTAMP NOT NULL,
          "PaidAt" TIMESTAMP, "CancelledAt" TIMESTAMP,
          "Key1" UUID NOT NULL, "Key2" UUID NOT NULL,
          "Flag1" BOOLEAN NOT NULL, "Flag2" BOOLEAN NOT NULL, "Flag3" BOOLEAN NOT NULL,
          "State" INT NOT NULL
        );
        """;

    public required string ConnectionString { get; init; }
    public required QuickSqlOrm QuickSql { get; init; }
    public required IFreeSql FreeSql { get; init; }
    public required SqlSugarClient Sugar { get; init; }

    public NpgsqlConnection OpenRaw()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public static WideRow[] Rows(int count)
    {
        var rows = new WideRow[count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = new WideRow
            {
                Id = i,
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

    public static PgArms Create()
    {
        var cs = ResolveConnectionString() ?? throw new InvalidOperationException(
            "没有 PG 连接：设 QUICKSQL_TEST_CONNECTION 或在仓库根放 quicksql-test.postgres.connection.local.txt。");

        using (var conn = new NpgsqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS \"BenchWideRow\";" + WideRowDdl;
            cmd.ExecuteNonQuery();
        }

        var provider = new QuickSqlPostgreSqlProvider(new DriverRegistry().Factory("postgresql"));
        var quickSql = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = cs,
        }, new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet));

        var fsql = new FreeSqlBuilder().UseConnectionString(DataType.PostgreSQL, cs).Build();

        var sugar = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = cs,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true,
            MoreSettings = new ConnMoreSettings { PgSqlIsAutoToLower = false },
        });
        sugar.MappingTables.Add("WideRow", "BenchWideRow");

        return new PgArms { ConnectionString = cs, QuickSql = quickSql, FreeSql = fsql, Sugar = sugar };
    }

    /// <summary>装置探针：五臂各写 3 行验受影响行数，再读回探针行验 Guid(UUID)/decimal(NUMERIC) 落位，最后清表。</summary>
    public void ProbeArms()
    {
        var rows = Rows(3);
        GuardProbe(QuickSql.InsertRange(rows).ExecuteBulkAsync().GetAwaiter().GetResult().RowCount, "QuickSql");
        GuardProbe(QuickSql.InsertRange(rows).WithBulk(BulkMethod.CopyChannel).ExecuteBulkAsync().GetAwaiter().GetResult().RowCount, "QuickSql-L1");
        GuardProbe(FreeSql.Insert(rows).IgnoreColumns(x => x.Id).ExecuteAffrowsAsync().GetAwaiter().GetResult(), "FreeSql");
        GuardProbe(Sugar.Insertable(rows).IgnoreColumns(x => x.Id).ExecuteCommandAsync().GetAwaiter().GetResult(), "SqlSugar");
        using (var cnn = OpenRaw())
            GuardProbe(cnn.Execute(ProbeSql, rows), "Dapper");

        using (var conn = OpenRaw())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"Key1\", \"Money1\" FROM \"BenchWideRow\" WHERE \"Qty1\" = 2 ORDER BY \"Id\" DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            LiveGuard.That(reader.Read(), "PG 对照装置探针：读不回探针行");
            var key = reader.GetGuid(0);
            LiveGuard.That(key == LiveArms.KeyOf(2), "PG 对照装置探针：Key1 Guid(UUID) 落位（读到 " + key + "）");
            LiveGuard.That(reader.GetDecimal(1) == 2.5m, "PG 对照装置探针：Money1 decimal(NUMERIC) 标度（应 2.5）");
        }
        TruncateWideTable();
    }

    static void GuardProbe(int affected, string arm)
    {
        if (affected != 3)
            throw new InvalidOperationException($"PG 对照装置探针没过（{arm}）：受影响 {affected} 行，给了 3 行。");
    }

    public void TruncateWideTable()
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE \"BenchWideRow\" RESTART IDENTITY";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        FreeSql.Dispose();
        Sugar.Dispose();
        try
        {
            using var conn = new NpgsqlConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS \"BenchWideRow\"";
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
            var file = Path.Combine(dir.FullName, "quicksql-test.postgres.connection.local.txt");
            if (!File.Exists(file)) continue;
            var line = File.ReadLines(file).FirstOrDefault(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l));
            if (line is not null) return line.Trim();
        }
        return null;
    }
}