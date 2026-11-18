using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using Dapper;
using FreeSql;
using MySqlConnector;
using QuickSql.DriverLoading;
using QuickSql.Generated;
using QuickSql.Providers;
using QuickSql.Schema;
using SqlSugar;
using QuickSqlMySqlProvider = QuickSql.Providers.MySql.MySqlProvider;

namespace QuickSql.Benchmarks.Live;

/// <summary>MySQL 写侧五臂对照（1000 行 × 30 列 WideRow，Id AUTO_INCREMENT 各家省 Id 29 列）。
/// QuickSql 两档守门各断言走对了层：<b>Auto 默认</b>（1000 行 &lt; 13763 行门槛 → L3 多值 VALUES 一次往返，
/// 29 列每块 2033 行）与<b>显式 L1</b>（WithBulk(CopyChannel) 点名 MySqlBulkCopy LOAD DATA LOCAL，不看门槛）。
/// Dapper 逐行、FreeSql/SqlSugar 默认多值。构建配置不对称同前：FreeSql/SqlSugar 官方 NuGet Debug 构建
/// （JIT 关优化，时间偏悲观），Description 就地标注。装置 ProbeArms 先验五臂各写 3 行 + Guid/decimal 落位读回。</summary>
[MemoryDiagnoser]
public class LiveWriteBenchMySql
{
    MySqlArms _arms = null!;
    WideRow[] _rows = null!;

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

    [GlobalSetup]
    public void Setup()
    {
        _arms = MySqlArms.Create();
        _rows = MySqlArms.Rows(1000);
        _arms.ProbeArms();
    }

    [IterationSetup]
    public void Reset() => _arms.TruncateWideTable();

    [GlobalCleanup]
    public void Cleanup() => _arms.Dispose();

    [Benchmark(Baseline = true, Description = "MySQL BulkInsert 1000×30 · QuickSql Auto 默认（13763 行门槛未到 → L3 多值VALUES·一次往返，Release）")]
    public async Task<int> BulkInsertQsAuto()
    {
        var report = await _arms.QuickSql.InsertRange(_rows).ExecuteBulkAsync();
        if (report.Method != BulkMethod.MultiValue)
            throw new InvalidOperationException($"MySQL 1000 行场守门没过：Auto 交回 {report.Method} 而不是 MultiValue（29 列每块 2033 行，1000 行 = 1 次往返 < 8）。{report.Reason}");
        Guard(report.RowCount, "QuickSql-Auto");
        return report.RowCount;
    }

    [Benchmark(Description = "MySQL BulkInsert 1000×30 · QuickSql 显式 L1 MySqlBulkCopy LOAD DATA（CopyChannel 点名不看门槛，Release）")]
    public async Task<int> BulkInsertQsL1()
    {
        var report = await _arms.QuickSql.InsertRange(_rows).WithBulk(BulkMethod.CopyChannel).ExecuteBulkAsync();
        if (report.Method != BulkMethod.CopyChannel)
            throw new InvalidOperationException($"MySQL 1000 行场守门没过（QuickSql-L1）：显式点名交回 {report.Method}。{report.Reason}");
        Guard(report.RowCount, "QuickSql-L1");
        return report.RowCount;
    }

    [Benchmark(Description = "MySQL BulkInsert 1000×30 · Dapper（逐行 29 列 + 一条事务，Release）")]
    public async Task<int> BulkInsertDapper()
    {
        using var conn = _arms.OpenRaw();
        using var tran = await conn.BeginTransactionAsync();
        var affected = await conn.ExecuteAsync(InsertSql, _rows, tran);
        await tran.CommitAsync();
        Guard(affected, "Dapper");
        return affected;
    }

    [Benchmark(Description = "MySQL BulkInsert 1000×30 · FreeSql（NuGet Debug 构建·JIT 关优化·时间偏悲观）")]
    public async Task<int> BulkInsertFs()
    {
        var affected = await _arms.FreeSql.Insert(_rows).IgnoreColumns(x => x.Id).ExecuteAffrowsAsync();
        Guard(affected, "FreeSql");
        return affected;
    }

    [Benchmark(Description = "MySQL BulkInsert 1000×30 · SqlSugar（NuGet Debug 构建·JIT 关优化·时间偏悲观）")]
    public async Task<int> BulkInsertSs()
    {
        var affected = await _arms.Sugar.Insertable(_rows).IgnoreColumns(x => x.Id).ExecuteCommandAsync();
        Guard(affected, "SqlSugar");
        return affected;
    }

    [Benchmark(Description = "MySql BulkInsert 1000×30 · SqlSugar Fastest.BulkCopy（CSV路径丢1行实插999·量耗时·Debug）")]
    public async Task<int> BulkInsertSsFastest()
    {
        // SqlSugar MySQL CSV bulk 路径丢 1 行（实插 999，COUNT 实证）——SqlSugar bug。本格只量耗时不验行数，
        // 返回其报数；正确性缺陷已在台账 §7.4 记录，不与 QuickSql L1（1000 行可靠）混读。
        return await _arms.Sugar.Fastest<WideRow>().BulkCopyAsync(new System.Collections.Generic.List<WideRow>(_rows));
    }

    static void Guard(int affected, string arm)
    {
        if (affected != 1000)
            throw new InvalidOperationException($"MySQL 三方对照守门没过（{arm}）：受影响 {affected} 行，给了 1000 行。");
    }
}

/// <summary>MySQL 五臂组装根。同一颗库、同一张表（raw MySQL DDL，Id AUTO_INCREMENT，CHARACTER SET utf8mb4）。
/// 连接串从 QUICKSQL_TEST_CONNECTION 或仓库根 quicksql-test.mysql.connection.local.txt 取（本机凭据，不入库）；
/// EnsureConnectionFlag 补 AllowLoadLocalInfile=true（L1 LOAD DATA LOCAL）与 GuidFormat=char36（Guid 落 CHAR(36)）。
/// FreeSql 走 DataType.MySql、SqlSugar 走 DbType.MySqlConnector。</summary>
sealed class MySqlArms : IDisposable
{
    public const int WriteRows = 1_000;
    public const int MassRows = 20_001;

    const string ProbeSql = """
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

    const string WideRowDdl = """
        CREATE TABLE BenchWideRow (
          Id INT AUTO_INCREMENT PRIMARY KEY,
          Code1 VARCHAR(200) NULL, Code2 VARCHAR(200) NULL, Code3 VARCHAR(200) NULL,
          Name1 VARCHAR(200) NOT NULL, Name2 VARCHAR(200) NOT NULL, Name3 VARCHAR(200) NOT NULL,
          Note VARCHAR(200) NULL, Remark VARCHAR(200) NULL, Tag1 VARCHAR(200) NULL, Tag2 VARCHAR(200) NULL,
          Qty1 INT NOT NULL, Qty2 INT NOT NULL, Qty3 INT NOT NULL,
          Seq BIGINT NOT NULL, Version BIGINT NOT NULL,
          Money1 DECIMAL(18,4) NOT NULL, Money2 DECIMAL(18,4) NOT NULL, Money3 DECIMAL(18,4) NOT NULL,
          CreatedAt DATETIME(3) NOT NULL, UpdatedAt DATETIME(3) NOT NULL, SettledAt DATETIME(3) NOT NULL,
          PaidAt DATETIME(3) NULL, CancelledAt DATETIME(3) NULL,
          Key1 CHAR(36) NOT NULL, Key2 CHAR(36) NOT NULL,
          Flag1 TINYINT(1) NOT NULL, Flag2 TINYINT(1) NOT NULL, Flag3 TINYINT(1) NOT NULL,
          State INT NOT NULL
        ) CHARACTER SET utf8mb4;
        """;

    public required string ConnectionString { get; init; }
    public required QuickSqlOrm QuickSql { get; init; }
    public required IFreeSql FreeSql { get; init; }
    public required SqlSugarClient Sugar { get; init; }

    public MySqlConnection OpenRaw()
    {
        var conn = new MySqlConnection(ConnectionString);
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

    public static MySqlArms Create()
    {
        var cs = ResolveConnectionString() ?? throw new InvalidOperationException(
            "没有 MySQL 连接：设 QUICKSQL_TEST_CONNECTION 或在仓库根放 quicksql-test.mysql.connection.local.txt。");
        cs = EnsureConnectionFlag(cs, "AllowLoadLocalInfile", "true");
        cs = EnsureConnectionFlag(cs, "GuidFormat", "char36");

        using (var conn = new MySqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS BenchWideRow;" + WideRowDdl;
            cmd.ExecuteNonQuery();
        }

        var provider = new QuickSqlMySqlProvider(new DriverRegistry().Factory("mysql"));
        var quickSql = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = cs,
        }, new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet));

        var fsql = new FreeSqlBuilder().UseConnectionString(DataType.MySql, cs).Build();

        var sugar = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = cs,
            DbType = DbType.MySqlConnector,
            IsAutoCloseConnection = true,
        });
        sugar.MappingTables.Add("WideRow", "BenchWideRow");

        return new MySqlArms { ConnectionString = cs, QuickSql = quickSql, FreeSql = fsql, Sugar = sugar };
    }

    /// <summary>装置探针：五臂各写 3 行验受影响行数，再读回探针行验 Guid/decimal 落位（CHAR(36)/DECIMAL 标度），
    /// 最后清表。任何一臂写不进或落位歪都在 GlobalSetup 当场红，不带病进基准。</summary>
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
            cmd.CommandText = "SELECT Key1, Money1 FROM BenchWideRow WHERE Qty1 = 2 ORDER BY Id DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            LiveGuard.That(reader.Read(), "MySQL 对照装置探针：读不回探针行");
            // GuidFormat=char36 下 CHAR(36) 列被 MySqlConnector 当 Guid 类型，GetString 会 Guid→String 抛 InvalidCast，
            // 故按 Guid 读（这正是验 Guid 落位的本意）。
            var key = reader.GetGuid(0);
            LiveGuard.That(key == LiveArms.KeyOf(2), "MySQL 对照装置探针：Key1 Guid 落位（读到 " + key + "）");
            LiveGuard.That(reader.GetDecimal(1) == 2.5m, "MySQL 对照装置探针：Money1 decimal 标度（应 2.5）");
        }
        TruncateWideTable();
    }

    static void GuardProbe(int affected, string arm)
    {
        if (affected != 3)
            throw new InvalidOperationException($"MySQL 对照装置探针没过（{arm}）：受影响 {affected} 行，给了 3 行。");
    }

    public void TruncateWideTable()
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE BenchWideRow";
        cmd.ExecuteNonQuery();
    }

    public int CountWideTable()
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM BenchWideRow";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        FreeSql.Dispose();
        Sugar.Dispose();
        try
        {
            using var conn = new MySqlConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS BenchWideRow";
            cmd.ExecuteNonQuery();
        }
        catch { /* scratch 清理失败不掩主体 */ }
    }

    static string EnsureConnectionFlag(string cs, string key, string value)
    {
        var replaced = Regex.Replace(cs, "(?i)(^|;)" + key + "\\s*=\\s*[^;]*", "$1" + key + "=" + value);
        return replaced != cs ? replaced : cs.TrimEnd(';', ' ') + ";" + key + "=" + value;
    }

    static string? ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "quicksql-test.mysql.connection.local.txt");
            if (!File.Exists(file)) continue;
            var line = File.ReadLines(file).FirstOrDefault(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l));
            if (line is not null) return line.Trim();
        }
        return null;
    }
}