using System.Data;
using System.Data.Common;
using Dapper;
using FreeSql;
using Microsoft.Data.Sqlite;
using QuickSql.DriverLoading;
using QuickSql.Generated;
using QuickSql.Schema;
using SqlSugar;
using QuickSqlSqliteProvider = QuickSql.Providers.Sqlite.SqliteProvider;

namespace QuickSql.Benchmarks.Live;

/// <summary>四臂对照装置的组装根（口径：docs/quicksql/plan-cross-orm-bench.md，装置形状对齐
/// 树册 §13 —— plan-tree-query.md 那次三方对照的"另一本账"）。
///
/// <para><b>等化器怎么工作的</b>：同一颗 SQLite 临时文件、同一套 DDL、同一段原始 ADO 种子，
/// 于是四臂（QuickSql / Dapper / FreeSql / SqlSugar）交回的差只剩"ORM 自己怎么做"。
/// 三臂共用 <c>Microsoft.Data.Sqlite</c> 这颗驱动；<b>FreeSql 臂不同源</b> ——
/// FreeSql.Provider.Sqlite 底下是 System.Data.SQLite.Core（自带原生 sqlite3），
/// 这是它家用户在 SQLite 上的真实用法，边界记进册子而不是改配置硬凑。</para>
///
/// <para><b>种子值全部走公式</b>（<see cref="AmountOf"/> 等一族）：基准方法里的抽查守门按同一批公式算
/// 期望值，装置改了种子、守门当场红 —— 不靠人肉对齐两处字面量。</para>
///
/// <para><b>类型落位跟 QuickSql 自家 CodeFirst 同一档</b>（SqliteTypes 那张表）：
/// decimal 落 TEXT（定点没有无损的落处）、Guid 落 <c>GUID</c> 声明（本家认这颗名字，
/// System.Data.SQLite 与 Microsoft.Data.Sqlite 都会按它解析文本）、DateTime 落 TEXT、
/// bool/枚举落 INTEGER。抽查守门（<see cref="LiveGuard"/>）替这些落位验真 —— 有哪家把
/// "串位/串型"装成正常数字，行数那一格先红。</para></summary>
public sealed class LiveArms : IDisposable
{
    /// <summary>与离线套件同一档行数（1001）：两片的"每行成本"要能横向读。</summary>
    public const int ReadRows = 1_001;

    public const int WriteRows = 1_000;

    public required string ConnectionString { get; init; }

    public required QuickSqlOrm QuickSql { get; init; }

    public required IFreeSql FreeSql { get; init; }

    public required SqlSugarClient Sugar { get; init; }

    string _file = "";

    /// <summary>裸 ADO 地板臂与 Dapper 臂共用的开连接口：<b>交回的连接是开着的</b> ——
    /// 裸 ADO 臂直接发命令，Dapper 臂的开事务也需要它；每臂用 <c>using</c> 自管生命周期。</summary>
    public SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>按名字开一颗新库：建表、种子、四臂组装。名字决定临时文件，一个基准类一颗 ——
    /// 各类各库互不踩（写侧的 <see cref="RecreateWideTable"/> 只会砸自己那颗的表）。</summary>
    public static LiveArms Create(string name)
    {
        var file = Path.Combine(Path.GetTempPath(), $"quicksql-live-{name}.db");

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(file + suffix))
                File.Delete(file + suffix);
        }

        var cs = "Data Source=" + file;

        CreateSchema(cs);
        Seed(cs);

        // QuickSql 走完整的运行期驱动解析链（清单 → AssemblyLoadContext → 工厂），
        // 与集成层同一条路、同一条"不许直接 new 工厂"的理由（tests/QuickSql.Tests.Common/Sqlite.cs）。
        // SqliteProvider 两家重名（SqlSugar 也有一颗），本家的那一颗用别名点名。
        var factory = new DriverRegistry().Factory(QuickSqlSqliteProvider.DriverKey);
        var provider = new QuickSqlSqliteProvider(factory);
        var quickSql = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = cs,
        }, new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet));

        var fsql = new FreeSqlBuilder()
            .UseConnectionString(DataType.Sqlite, cs)
            .Build();

        var sugar = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = cs,
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = true,
        });

        // Dapper 对 Guid 属性的物化走 Convert，而 Microsoft.Data.Sqlite 把 TEXT 列的 Guid 交回
        // string，Convert.ChangeType(string→Guid) 不存在 —— 挂一颗 TypeHandler 让它按值的三种
        // 落位解析。这是 Dapper+SQLite 的社区标准配方，不是给谁开小灶；其余三臂都自己转过去了。
        SqlMapper.AddTypeHandler(new LiveGuidHandler());

        // 表名点名一遍：WideRow 的表名（BenchWideRow）与类名不同，不赌两家 DataAnnotations
        // 解析的差异。其余三颗类名即表名，走 [Table] 与这里双重一致。
        sugar.MappingTables.Add(nameof(WideRow), "BenchWideRow");

        return new LiveArms
        {
            ConnectionString = cs,
            QuickSql = quickSql,
            FreeSql = fsql,
            Sugar = sugar,
            _file = file,
        };
    }

    public void Dispose()
    {
        FreeSql.Dispose();
        Sugar.Dispose();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(_file + suffix))
                    File.Delete(_file + suffix);
            }
            catch (IOException)
            {
                // 句柄还没放干净：这颗文件在临时目录里，留着也是操作系统回收，不参与任何读数。
            }
        }
    }

    /// <summary>写侧基准的 <c>[IterationSetup]</c> 用：把写靶表砸了重建，行数才不会跨迭代滚雪球
    /// （1000 行批插进一张已有 10 万行的表，页缓存那笔账会算到谁头上说不清）。</summary>
    public void RecreateWideTable()
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS \"BenchWideRow\";{WideRowDdl}";
        cmd.ExecuteNonQuery();
    }

    // ---- 种子公式：守门与装置共用这一份，两边永远对得上 ----

    public static decimal AmountOf(int row) => row + 0.5m;

    public static string CityOf(int row) => "city" + (row & 63);

    public static DateTime CreatedAtOf(int row) => new DateTime(2026, 1, 1).AddMinutes(row);

    public static Guid KeyOf(int row) => new Guid(row, 0, 0, new byte[8]);

    public static BenchState StateOf(int row) => (BenchState) (row % 4);

    public static DateTime? PaidAtOf(int row) => row % 2 == 0 ? null : CreatedAtOf(row).AddHours(1);

    // ---- DDL 与种子：全部原始 ADO，四家都不参与 ----

    const string OrderDdl = """
        CREATE TABLE "BenchOrder" (
          "Id" INTEGER PRIMARY KEY,
          "CustomerName" TEXT,
          "CustomerId" INTEGER NOT NULL,
          "Amount" TEXT NOT NULL,
          "State" INTEGER NOT NULL,
          "CreatedAt" TEXT NOT NULL,
          "PaidAt" TEXT,
          "Note" TEXT
        );
        """;

    const string CustomerDdl = """
        CREATE TABLE "BenchCustomer" (
          "Id" INTEGER PRIMARY KEY,
          "Name" TEXT,
          "City" TEXT
        );
        """;

    const string ItemDdl = """
        CREATE TABLE "BenchItem" (
          "Id" INTEGER PRIMARY KEY,
          "OrderId" INTEGER NOT NULL,
          "Sku" TEXT,
          "Quantity" INTEGER NOT NULL,
          "Price" TEXT NOT NULL
        );
        """;

    /// <summary>列序与 <see cref="WideRow"/> 属性序逐列一致 —— 裸 ADO 地板臂按序号取值，靠这份一致活着。</summary>
    public const string WideRowDdl = """
        CREATE TABLE "BenchWideRow" (
          "Id" INTEGER PRIMARY KEY,
          "Code1" TEXT,
          "Code2" TEXT,
          "Code3" TEXT,
          "Name1" TEXT NOT NULL,
          "Name2" TEXT NOT NULL,
          "Name3" TEXT NOT NULL,
          "Note" TEXT,
          "Remark" TEXT,
          "Tag1" TEXT,
          "Tag2" TEXT,
          "Qty1" INTEGER NOT NULL,
          "Qty2" INTEGER NOT NULL,
          "Qty3" INTEGER NOT NULL,
          "Seq" INTEGER NOT NULL,
          "Version" INTEGER NOT NULL,
          "Money1" TEXT NOT NULL,
          "Money2" TEXT NOT NULL,
          "Money3" TEXT NOT NULL,
          "CreatedAt" TEXT NOT NULL,
          "UpdatedAt" TEXT NOT NULL,
          "SettledAt" TEXT NOT NULL,
          "PaidAt" TEXT,
          "CancelledAt" TEXT,
          "Key1" GUID NOT NULL,
          "Key2" GUID NOT NULL,
          "Flag1" INTEGER NOT NULL,
          "Flag2" INTEGER NOT NULL,
          "Flag3" INTEGER NOT NULL,
          "State" INTEGER NOT NULL
        );
        """;

    static void CreateSchema(string cs)
    {
        using var db = new SqliteConnection(cs);
        db.Open();

        using var cmd = db.CreateCommand();
        cmd.CommandText = OrderDdl + CustomerDdl + ItemDdl + WideRowDdl;
        cmd.ExecuteNonQuery();
    }

    static void Seed(string cs)
    {
        using var db = new SqliteConnection(cs);
        db.Open();
        using var tran = db.BeginTransaction();

        SeedOrders(db, tran);
        SeedCustomers(db, tran);
        SeedItems(db, tran);
        SeedWideRows(db, tran);

        tran.Commit();
    }

    static void SeedOrders(SqliteConnection db, SqliteTransaction tran)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "INSERT INTO \"BenchOrder\" VALUES (@Id, @CustomerName, @CustomerId, @Amount, @State, @CreatedAt, @PaidAt, @Note)";

        var id = Param(cmd, "@Id");
        var customerName = Param(cmd, "@CustomerName");
        var customerId = Param(cmd, "@CustomerId");
        var amount = Param(cmd, "@Amount");
        var state = Param(cmd, "@State");
        var createdAt = Param(cmd, "@CreatedAt");
        var paidAt = Param(cmd, "@PaidAt");
        var note = Param(cmd, "@Note");

        for (var row = 0; row < ReadRows; row++)
        {
            id.Value = row;
            customerName.Value = "name" + row;
            customerId.Value = row % 97;
            amount.Value = AmountOf(row);
            state.Value = (int) StateOf(row);
            createdAt.Value = CreatedAtOf(row);
            paidAt.Value = (object?) PaidAtOf(row) ?? DBNull.Value;
            note.Value = "note" + row;
            cmd.ExecuteNonQuery();
        }
    }

    static void SeedCustomers(SqliteConnection db, SqliteTransaction tran)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "INSERT INTO \"BenchCustomer\" VALUES (@Id, @Name, @City)";

        var id = Param(cmd, "@Id");
        var name = Param(cmd, "@Name");
        var city = Param(cmd, "@City");

        for (var row = 0; row < ReadRows; row++)
        {
            id.Value = row;
            name.Value = "cust" + row;
            city.Value = CityOf(row);
            cmd.ExecuteNonQuery();
        }
    }

    static void SeedItems(SqliteConnection db, SqliteTransaction tran)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "INSERT INTO \"BenchItem\" VALUES (@Id, @OrderId, @Sku, @Quantity, @Price)";

        var id = Param(cmd, "@Id");
        var orderId = Param(cmd, "@OrderId");
        var sku = Param(cmd, "@Sku");
        var quantity = Param(cmd, "@Quantity");
        var price = Param(cmd, "@Price");

        for (var row = 0; row < ReadRows; row++)
        {
            id.Value = row;
            orderId.Value = row;
            sku.Value = "sku" + row;
            quantity.Value = row % 7;
            price.Value = row + 1.5m;
            cmd.ExecuteNonQuery();
        }
    }

    static void SeedWideRows(SqliteConnection db, SqliteTransaction tran)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = """
            INSERT INTO "BenchWideRow" (
              "Id", "Code1", "Code2", "Code3", "Name1", "Name2", "Name3", "Note", "Remark", "Tag1", "Tag2",
              "Qty1", "Qty2", "Qty3", "Seq", "Version", "Money1", "Money2", "Money3",
              "CreatedAt", "UpdatedAt", "SettledAt", "PaidAt", "CancelledAt",
              "Key1", "Key2", "Flag1", "Flag2", "Flag3", "State"
            ) VALUES (
              @Id, @Code1, @Code2, @Code3, @Name1, @Name2, @Name3, @Note, @Remark, @Tag1, @Tag2,
              @Qty1, @Qty2, @Qty3, @Seq, @Version, @Money1, @Money2, @Money3,
              @CreatedAt, @UpdatedAt, @SettledAt, @PaidAt, @CancelledAt,
              @Key1, @Key2, @Flag1, @Flag2, @Flag3, @State
            )
            """;

        var id = Param(cmd, "@Id");
        var code1 = Param(cmd, "@Code1");
        var code2 = Param(cmd, "@Code2");
        var code3 = Param(cmd, "@Code3");
        var name1 = Param(cmd, "@Name1");
        var name2 = Param(cmd, "@Name2");
        var name3 = Param(cmd, "@Name3");
        var note = Param(cmd, "@Note");
        var remark = Param(cmd, "@Remark");
        var tag1 = Param(cmd, "@Tag1");
        var tag2 = Param(cmd, "@Tag2");
        var qty1 = Param(cmd, "@Qty1");
        var qty2 = Param(cmd, "@Qty2");
        var qty3 = Param(cmd, "@Qty3");
        var seq = Param(cmd, "@Seq");
        var version = Param(cmd, "@Version");
        var money1 = Param(cmd, "@Money1");
        var money2 = Param(cmd, "@Money2");
        var money3 = Param(cmd, "@Money3");
        var createdAt = Param(cmd, "@CreatedAt");
        var updatedAt = Param(cmd, "@UpdatedAt");
        var settledAt = Param(cmd, "@SettledAt");
        var paidAt = Param(cmd, "@PaidAt");
        var cancelledAt = Param(cmd, "@CancelledAt");
        var key1 = Param(cmd, "@Key1");
        var key2 = Param(cmd, "@Key2");
        var flag1 = Param(cmd, "@Flag1");
        var flag2 = Param(cmd, "@Flag2");
        var flag3 = Param(cmd, "@Flag3");
        var state = Param(cmd, "@State");

        // 读侧守门要 1001 行（与离线 MaterializeWide 同档）：写侧 [IterationSetup] 会砸表重建，
        // 所以这里多种的那一行不参与写侧的账。
        for (var row = 0; row < ReadRows; row++)
        {
            id.Value = row;
            code1.Value = "c" + row;
            code2.Value = DBNull.Value;
            code3.Value = DBNull.Value;
            name1.Value = "x";
            name2.Value = "x";
            name3.Value = "x";
            note.Value = DBNull.Value;
            remark.Value = DBNull.Value;
            tag1.Value = DBNull.Value;
            tag2.Value = DBNull.Value;
            qty1.Value = row;
            qty2.Value = row % 5;
            qty3.Value = row % 9;
            seq.Value = (long) row;
            version.Value = (long) row;
            money1.Value = AmountOf(row);
            money2.Value = row + 1.5m;
            money3.Value = row + 2.5m;
            createdAt.Value = CreatedAtOf(row);
            updatedAt.Value = CreatedAtOf(row);
            settledAt.Value = CreatedAtOf(row);
            paidAt.Value = (object?) PaidAtOf(row) ?? DBNull.Value;
            cancelledAt.Value = row % 3 == 0 ? DBNull.Value : CreatedAtOf(row).AddHours(3);
            key1.Value = KeyOf(row);
            key2.Value = new Guid(row + 1_000_000, 0, 0, new byte[8]);
            flag1.Value = row % 2 == 0;
            flag2.Value = row % 3 == 0;
            flag3.Value = row % 7 == 0;
            state.Value = (int) StateOf(row);
            cmd.ExecuteNonQuery();
        }
    }

    static SqliteParameter Param(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}

/// <summary>四臂共用的正确性守门（树册"节点数递归数出来"的翻译版）：<b>每臂每次执行先交对行数与抽查值，
/// 再谈时间与字节</b>。没有它，"哪家把 enum 读成了 string、哪家把表查串了"会安静地变成一个看起来正常的
/// 快 20% —— 对照研究里最贵的就是那种数字。</summary>
static class LiveGuard
{
    public static void Rows<T>(List<T> rows, int expected, string what)
    {
        if (rows.Count != expected)
            throw new InvalidOperationException(
                $"三方对照守门没过（{what}）：交回 {rows.Count} 行，装置种了 {expected} 行 —— 读串表或读串行数，别把这次的数字记进册子。");
    }

    public static void That(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException($"三方对照守门没过（{what}）：值与种子公式对不上 —— 多半是哪家把 TEXT/GUID 落位读歪了。");
    }
}

/// <summary>Dapper 臂的 Guid 解析（TEXT/原样/BLOB 三种落位都收）—— 挂载理由见 <see cref="LiveArms"/>。</summary>
sealed class LiveGuidHandler : SqlMapper.TypeHandler<Guid>
{
    public override Guid Parse(object value) => value switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        byte[] b => new Guid(b),
        _ => throw new InvalidCastException($"Dapper 臂收到落位外的 Guid 形状：{value.GetType().Name}"),
    };

    public override void SetValue(IDbDataParameter parameter, Guid value) => parameter.Value = value;
}
