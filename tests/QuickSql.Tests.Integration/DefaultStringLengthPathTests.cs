using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>缺省宽度的活库那一半（PostgreSQL / 虚谷）：配 <c>DefaultStringLength</c> → CodeFirst 建表 →
/// 从目录读回<b>真实列型</b> → 再用裸 ADO 撞一次列宽，证明"默认长度"不是只活在契约里的一颗数。
///
/// <para>三件事分开判：① 未声明那一列在目录里真的带上了 96；② 显式宽度（32）与点名存储型（text）各自赢；
/// ③ 显式无宽度那一列既没被默认收窄（目录里是 text），也真的装得下 200 颗——"不吃默认"在库里也是真的。
/// 反方向的 97 颗走裸 ADO，避开我们写侧那把 <c>FitsChars</c> 尺子，撞的是库自己的列宽。</para>
///
/// <para>只对 PG 与虚谷开：其余三家在本轮没有这条活库通路，当场 skip 而不是假装绿。</para></summary>
public sealed class DefaultStringLengthPathTests
{
    static CancellationToken ct => TestContext.Current.CancellationToken;

    static QuickSqlOrm Orm(int? defaultStringLength) => new(new QuickSqlOrmOptions
    {
        Provider = Database.Orm.Provider,
        ConnectionString = Database.ConnectionString,
        DefaultStringLength = defaultStringLength,
        SchemaSync = SchemaSyncPolicy.Execute,
        ForceSchemaSync = true,
    });

    /// <summary>先摘掉上一跑留下的同名表：映射改过之后（TEXT → CLOB）旧表形状会挡住新断言，
    /// 而结构同步不保证能把 TEXT 原地改写成 CLOB。只摘这一张自己建的表。</summary>
    static async Task DropAsync(QuickSqlOrm orm, TableInfo table, CancellationToken ct)
    {
        await using var connection = orm.Provider.Connectors.CreateConnection(Database.ConnectionString);

        await connection.OpenAsync(ct);

        if (await orm.Provider.Schema.ReadTableAsync(connection, table, ct) is null)
            return;

        using var drop = connection.CreateCommand();

        drop.CommandText = $"DROP TABLE \"{Database.CatalogueName(table.Name)}\"";
        await drop.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task The_default_reaches_the_catalog_and_binds_the_real_column()
    {
        Database.Ready();
        Assert.SkipUnless(Database.Family is Database.Target.PostgreSql or Database.Target.Xugu,
            $"默认长度的活库档只有 PG 与虚谷两台靶子（当前 {Database.Family}）。");

        var orm = Orm(96);
        var contract = orm.Schemas.Contract(typeof(LiveWidthRow));

        await DropAsync(orm, contract.Table, ct);
        await orm.SyncStructureAsync(ct);

        await using var connection = orm.Provider.Connectors.CreateConnection(Database.ConnectionString);

        await connection.OpenAsync(ct);

        var table = await orm.Provider.Schema.ReadTableAsync(connection, contract.Table, ct);

        Assert.NotNull(table);

        SchemaColumn Column(string member) => table!.Columns.Single(c => c.Name == Database.CatalogueName(member));

        // 目录里读回来的原文（各家自己的拼法）：PG 落小写 varchar(n)/text，虚谷折成大写、无宽度落 CLOB
        // （TEXT 只是 VARCHAR 的同义字，吃不穿行 64K），而点名 TypeName=text 的那一列仍原样穿过。默认 96 只进第一列。
        var (width96, width32, unbounded, namedText) = Database.Family switch
        {
            Database.Target.PostgreSql => ("varchar(96)", "varchar(32)", "text", "text"),
            Database.Target.Xugu => ("VARCHAR(96)", "VARCHAR(32)", "CLOB", "TEXT"),
            _ => throw new InvalidOperationException($"没有为 {Database.Family} 登记目录原文"),
        };

        var bare = Column(nameof(LiveWidthRow.Bare));
        Assert.Equal(width96, bare.StoreType);
        Assert.Equal(96, bare.MaxChars!.Value);

        var narrow = Column(nameof(LiveWidthRow.Narrow));
        Assert.Equal(width32, narrow.StoreType);
        Assert.Equal(32, narrow.MaxChars!.Value);

        var unlimited = Column(nameof(LiveWidthRow.Unlimited));
        Assert.Equal(unbounded, unlimited.StoreType);
        Assert.Null(unlimited.MaxChars);

        var named = Column(nameof(LiveWidthRow.Named));
        Assert.Equal(namedText, named.StoreType);
        Assert.Null(named.MaxChars);

        // 裸 ADO 撞列宽：96 颗进得去，97 颗由库自己拒（写侧那把尺子在这条路上不参与）。
        var live = Database.CatalogueName("QuickSqlWidthLive");
        var bareColumn = Database.CatalogueName(nameof(LiveWidthRow.Bare));
        var wideColumn = Database.CatalogueName(nameof(LiveWidthRow.Unlimited));

        Database.Execute($"INSERT INTO \"{live}\" (\"{bareColumn}\") VALUES (@v)", ("@v", new string('x', 96)));

        // 97 颗必须被库自己拒。两家的失败形状不一样：PG 交 DbException（22001），
        // 虚谷驱动把同一件事包成裸 Exception（E34501 → sqlexecute err E17111 + 列名）。
        var rejected = Record.Exception(() =>
            Database.Execute($"INSERT INTO \"{live}\" (\"{bareColumn}\") VALUES (@v)", ("@v", new string('x', 97))));

        Assert.NotNull(rejected);
        Assert.True(rejected is DbException || rejected.ToString().Contains("E17111", StringComparison.OrdinalIgnoreCase),
            $"97 颗没有在库侧被拒：{rejected.GetType().Name} / {rejected.Message}");

        // 显式无宽度那一列：200 颗照样进得去。
        Database.Execute($"INSERT INTO \"{live}\" (\"{wideColumn}\") VALUES (@v)", ("@v", new string('x', 200)));
    }

    /// <summary>对照组：同一颗实体形状、同一台 orm，只是不配默认长度——目录里必须是 text，
    /// 200 颗也必须进得去。没有这一颗，上面那颗测的是"库本来就很宽"也说不定。</summary>
    [Fact]
    public async Task Without_the_option_the_column_stays_unbounded()
    {
        Database.Ready();
        Assert.SkipUnless(Database.Family is Database.Target.PostgreSql or Database.Target.Xugu,
            $"默认长度的活库档只有 PG 与虚谷两台靶子（当前 {Database.Family}）。");

        var orm = Orm(null);
        var contract = orm.Schemas.Contract(typeof(LiveWidthUnboundedRow));

        await DropAsync(orm, contract.Table, ct);
        await orm.SyncStructureAsync(ct);

        await using var connection = orm.Provider.Connectors.CreateConnection(Database.ConnectionString);

        await connection.OpenAsync(ct);

        var table = await orm.Provider.Schema.ReadTableAsync(connection, contract.Table, ct);

        Assert.NotNull(table);

        var bare = table!.Columns.Single(c => c.Name == Database.CatalogueName(nameof(LiveWidthUnboundedRow.Bare)));

        Assert.Equal(Database.Family == Database.Target.PostgreSql ? "text" : "CLOB", bare.StoreType);
        Assert.Null(bare.MaxChars);

        var live = Database.CatalogueName("QuickSqlWidthLiveUnbounded");
        var bareColumn = Database.CatalogueName(nameof(LiveWidthUnboundedRow.Bare));

        Database.Execute($"INSERT INTO \"{live}\" (\"{bareColumn}\") VALUES (@v)", ("@v", new string('x', 200)));
    }
}

/// <summary>活库靶实体：四态各一列。表名与列名都经 <see cref="Database.CatalogueName"/> 折算后再拼裸 SQL。</summary>
[Table("QuickSqlWidthLive")]
public class LiveWidthRow
{
    public int Id { get; set; }

    public string? Bare { get; set; }

    [QuickColumn(Length = 32)]
    public string? Narrow { get; set; }

    [QuickColumn(Length = -1)]
    public string? Unlimited { get; set; }

    [Column(TypeName = "text")]
    public string? Named { get; set; }
}
/// <summary>对照组的靶实体：只有一列未声明宽度的字符串，不配默认长度时该落 provider 大文本。</summary>
[Table("QuickSqlWidthLiveUnbounded")]
public class LiveWidthUnboundedRow
{
    public int Id { get; set; }

    public string? Bare { get; set; }
}
