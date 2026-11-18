using BenchmarkDotNet.Attributes;
using Dapper;
using FreeSql;
using Microsoft.Data.SqlClient;
using QuickSql.DriverLoading;
using QuickSql.Generated;
using QuickSql.Providers;
using QuickSql.Schema;
using SqlSugar;
using QuickSqlSqlServerProvider = QuickSql.Providers.SqlServer.SqlServerProvider;

namespace QuickSql.Benchmarks.Live;

/// <summary>SQL Server 写侧四臂对照（与 SQLite 的 <see cref="LiveWriteBench"/> 同形：1000 行 × 30 列 WideRow，
/// 各家默认写法，一条事务）。<b>Id 走 IDENTITY</b>：QuickSql 默认整型 Id 自增约定省 Id 列（29 列），
/// FreeSql/SqlSugar 默认自增同形，Dapper 显式 INSERT 29 列（不带 Id）——四臂都让库给号，列齐平。
/// 与 SQLite 那片（显式 Id 30 列）口径不同：SQL Server 真 IDENTITY 列显式插值要 SET IDENTITY_INSERT，
/// 让库给号是各家默认且免开关的形状。构建配置不对称同 SQLite 片：FreeSql/SqlSugar 官方 NuGet 是 Debug 构建
/// （JIT 关优化，时间偏悲观），Dapper/QuickSql Release，Description 就地标注。</summary>
[MemoryDiagnoser]
public class LiveWriteBenchSqlServer
{
    SqlServerArms _arms = null!;
    WideRow[] _rows = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arms = SqlServerArms.Create();
        _rows = new WideRow[SqlServerArms.WriteRows];
        for (var row = 0; row < _rows.Length; row++)
            _rows[row] = new WideRow
            {
                Id = row,
                Code1 = "c" + row,
                Qty1 = row,
                Money1 = row + 0.5m,
                Key1 = LiveArms.KeyOf(row),
                Key2 = new Guid(row + 1_000_000, 0, 0, new byte[8]),
                CreatedAt = LiveArms.CreatedAtOf(row),
                UpdatedAt = LiveArms.CreatedAtOf(row),
                SettledAt = LiveArms.CreatedAtOf(row),
                PaidAt = LiveArms.PaidAtOf(row),
                State = LiveArms.StateOf(row),
            };
    }

    [IterationSetup]
    public void Reset() => _arms.TruncateWideTable();

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "SqlServer BulkInsert 1000×30 · QuickSql L1 SqlBulkCopy（ExecuteBulkAsync Auto，Release）")]
    public async Task<int> BulkInsertQsL1()
    {
        var report = await _arms.QuickSql.InsertRange(_rows).ExecuteBulkAsync();
        Guard(report.RowCount, "QuickSql-L1");
        return report.RowCount;
    }

    [Benchmark(Description = "SqlServer BulkInsert 1000×30 · QuickSql L3 多值VALUES（ExecuteAffrowsAsync，Release）")]
    public async Task<int> BulkInsertQsL3()
    {
        var affected = await _arms.QuickSql.InsertRange(_rows).ExecuteAffrowsAsync();
        Guard(affected, "QuickSql-L3");
        return affected;
    }

    [Benchmark(Description = "SqlServer BulkInsert 1000×30 · Dapper（逐行 29 列 + 一条事务，Release）")]
    public async Task<int> BulkInsertDapper()
    {
        using var conn = _arms.OpenRaw();
        using var tran = conn.BeginTransaction();
        var affected = await conn.ExecuteAsync(InsertSql, _rows, tran);
        tran.Commit();
        Guard(affected, "Dapper");
        return affected;
    }

    [Benchmark(Description = "SqlServer BulkInsert 1000×30 · FreeSql（NuGet Debug 构建·JIT 关优化·时间偏悲观）")]
    public async Task<int> BulkInsertFs()
    {
        var affected = await _arms.FreeSql.Insert(_rows).IgnoreColumns(x => x.Id).ExecuteAffrowsAsync();
        Guard(affected, "FreeSql");
        return affected;
    }

    [Benchmark(Description = "SqlServer BulkInsert 1000×30 · SqlSugar 普通Insertable（NuGet Debug·时间偏悲观）")]
    public async Task<int> BulkInsertSs()
    {
        var affected = await _arms.Sugar.Insertable(_rows).IgnoreColumns(x => x.Id).ExecuteCommandAsync();
        Guard(affected, "SqlSugar");
        return affected;
    }

    [Benchmark(Description = "SqlServer BulkInsert 1000×30 · SqlSugar Fastest.BulkCopy（SqlBulkCopy·与 QuickSql L1 对等·Debug）")]
    public async Task<int> BulkInsertSsFastest()
    {
        var affected = await _arms.Sugar.Fastest<WideRow>().BulkCopyAsync(new System.Collections.Generic.List<WideRow>(_rows));
        Guard(affected, "SqlSugar-Fastest");
        return affected;
    }

    const string InsertSql = """
        INSERT INTO BenchWideRow (
          Code1, Code2, Code3, Name1, Name2, Name3, Note, Remark, Tag1, Tag2,
          Qty1, Qty2, Qty3, Seq, Version, Money1, Money2, Money3,
          CreatedAt, UpdatedAt, SettledAt, PaidAt, CancelledAt,
          Key1, Key2, Flag1, Flag2, Flag3, State
        ) VALUES (
          @Code1, @Code2, @Code3, @Name1, @Name2, @Name3, @Note, @Remark, @Tag1, @Tag2,
          @Qty1, @Qty2, @Qty3, @Seq, @Version, @Money1, @Money2, @Money3,
          @CreatedAt, @UpdatedAt, @SettledAt, @PaidAt, @CancelledAt,
          @Key1, @Key2, @Flag1, @Flag2, @Flag3, @State
        )
        """;

    static void Guard(int affected, string arm)
    {
        if (affected != SqlServerArms.WriteRows)
            throw new InvalidOperationException($"SqlServer 三方对照守门没过（{arm}）：受影响 {affected} 行，给了 {SqlServerArms.WriteRows} 行。");
    }
}

/// <summary>SQL Server 四臂组装根。同一颗库、同一张表（raw T-SQL DDL，Id IDENTITY），四臂共用。
/// 连接串从 QUICKSQL_TEST_CONNECTION 或仓库根 quicksql-test.connection.local.txt 取（本机凭据，不入库）。</summary>
sealed class SqlServerArms : IDisposable
{
    public const int WriteRows = 1_000;

    public required string ConnectionString { get; init; }
    public required QuickSqlOrm QuickSql { get; init; }
    public required IFreeSql FreeSql { get; init; }
    public required SqlSugarClient Sugar { get; init; }

    public SqlConnection OpenRaw()
    {
        var conn = new SqlConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public static SqlServerArms Create()
    {
        var cs = ResolveConnectionString() ?? throw new InvalidOperationException(
            "没有 SqlServer 连接：设 QUICKSQL_TEST_CONNECTION 或在仓库根放 quicksql-test.connection.local.txt。");

        using (var conn = new SqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "IF OBJECT_ID('BenchWideRow','U') IS NOT NULL DROP TABLE BenchWideRow;" + WideRowDdl;
            cmd.ExecuteNonQuery();
        }

        var factory = new DriverRegistry().Factory(QuickSqlSqlServerProvider.DriverKey);
        var provider = new QuickSqlSqlServerProvider(factory);
        var quickSql = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = cs,
        }, new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet));

        var fsql = new FreeSqlBuilder().UseConnectionString(DataType.SqlServer, cs).Build();

        var sugar = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = cs,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
        });
        sugar.MappingTables.Add(nameof(WideRow), "BenchWideRow");

        return new SqlServerArms { ConnectionString = cs, QuickSql = quickSql, FreeSql = fsql, Sugar = sugar };
    }

    public void TruncateWideTable()
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE BenchWideRow";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        FreeSql.Dispose();
        Sugar.Dispose();
        try
        {
            using var conn = new SqlConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "IF OBJECT_ID('BenchWideRow','U') IS NOT NULL DROP TABLE BenchWideRow";
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
            var file = Path.Combine(dir.FullName, "quicksql-test.connection.local.txt");
            if (!File.Exists(file)) continue;
            var line = File.ReadLines(file).FirstOrDefault(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l));
            if (line is not null) return line.Trim();
        }
        return null;
    }

    const string WideRowDdl = """
        CREATE TABLE BenchWideRow (
          Id INT IDENTITY(1,1) PRIMARY KEY,
          Code1 NVARCHAR(MAX), Code2 NVARCHAR(MAX), Code3 NVARCHAR(MAX),
          Name1 NVARCHAR(MAX) NOT NULL, Name2 NVARCHAR(MAX) NOT NULL, Name3 NVARCHAR(MAX) NOT NULL,
          Note NVARCHAR(MAX), Remark NVARCHAR(MAX), Tag1 NVARCHAR(MAX), Tag2 NVARCHAR(MAX),
          Qty1 INT NOT NULL, Qty2 INT NOT NULL, Qty3 INT NOT NULL,
          Seq BIGINT NOT NULL, Version BIGINT NOT NULL,
          Money1 DECIMAL(18,4) NOT NULL, Money2 DECIMAL(18,4) NOT NULL, Money3 DECIMAL(18,4) NOT NULL,
          CreatedAt DATETIME2 NOT NULL, UpdatedAt DATETIME2 NOT NULL, SettledAt DATETIME2 NOT NULL,
          PaidAt DATETIME2, CancelledAt DATETIME2,
          Key1 UNIQUEIDENTIFIER NOT NULL, Key2 UNIQUEIDENTIFIER NOT NULL,
          Flag1 BIT NOT NULL, Flag2 BIT NOT NULL, Flag3 BIT NOT NULL,
          State INT NOT NULL
        );
        """;
}