using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary><c>IValueBinder.NormalizeValue</c> 与 <c>CreateParameter(...).Value</c> 的逐字对拍，十九家逐家过。
/// 接口契约写明"归一口径必须与 CreateParameter 的 .Value 逐字一致"——L1 装载通道直取前者、文本层走后者，
/// 两扇门若分叉，同一条插入在两层会写出不同的值，而那种错绿得最安静。默认接口方法退回后者时这一格天然绿，
/// 它真正钉的是<b>覆写者</b>（SqlServer/PG/MySQL 血统的廉价 Normalize、Oracle 血统与后续接通道那天的每一家）：
/// 值矩阵含 null/DBNull/枚举（int 底与 byte 底）/可空/二进制/时刻三形，hint 路再喂同一颗列契约
/// （PG 血统的时刻换形这类 hint 依赖的分岔也需要两门同形）。全程离线，碰驱动即红。</summary>
public sealed class NormalizeValueParityTests
{
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("kingbase")]
    [InlineData("opengauss")]
    [InlineData("gaussdb")]
    [InlineData("highgo")]
    [InlineData("gbase8c")]
    [InlineData("duckdb")]
    [InlineData("mysql")]
    [InlineData("tidb")]
    [InlineData("oceanbase")]
    [InlineData("oracle")]
    [InlineData("dameng")]
    [InlineData("shentong")]
    [InlineData("gbase8s")]
    [InlineData("tdengine")]
    [InlineData("xugu")]
    [InlineData("sqlite")]
    [InlineData("clickhouse")]
    public void NormalizeValue_matches_CreateParameter_value_on_every_provider(string name)
    {
        var provider = ProviderOf(name);
        var binder = provider.Values;

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = SqlServer.OfflineConnectionString,
        });

        var columns = orm.Schemas.Contract<Order>().Columns;

        foreach (var value in Values())
            Assert.Equal(binder.CreateParameter(string.Empty, value, null).Value, binder.NormalizeValue(value, null));

        // hint 路：同一颗契约喂两扇门。字符串/时刻/枚举各挑一列，null 与实值各走一遍。
        (object? Value, ColumnContract Hint)[] hinted =
        [
            ("文本", columns.Single(c => c.MemberName == nameof(Order.CustomerName))),
            (null, columns.Single(c => c.MemberName == nameof(Order.CustomerName))),
            (new DateTime(2026, 3, 4, 5, 6, 7), columns.Single(c => c.MemberName == nameof(Order.CreatedAt))),
            (null, columns.Single(c => c.MemberName == nameof(Order.CreatedAt))),
            (OrderState.Paid, columns.Single(c => c.MemberName == nameof(Order.State))),
        ];

        foreach (var (value, hint) in hinted)
            Assert.Equal(binder.CreateParameter(string.Empty, value, hint).Value, binder.NormalizeValue(value, hint));
    }

    static object?[] Values() =>
    [
        null,
        DBNull.Value,
        42,
        42L,
        (short)7,
        (byte)3,
        3.5f,
        3.5d,
        12.34m,
        "文本",
        true,
        new Guid(1, 2, 3, new byte[8]),
        new DateTime(2026, 3, 4, 5, 6, 7, 890),
        new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(8)),
        TimeSpan.FromMinutes(90),
        new byte[] { 0xCA, 0xFE },
        OrderState.Paid,
        ByteEnum.Second,
        (int?)null,
        (int?)5,
    ];

    enum ByteEnum : byte
    {
        First,
        Second,
    }

    static IDbProvider ProviderOf(string name) => name switch
    {
        "sqlserver" => SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
        "postgresql" => PostgreSql.Provider(SqlServer.NoDriverFactory.Instance),
        "kingbase" => Kingbase.Provider(SqlServer.NoDriverFactory.Instance),
        "opengauss" => OpenGauss.Provider(SqlServer.NoDriverFactory.Instance),
        "gaussdb" => GaussDb.Provider(SqlServer.NoDriverFactory.Instance),
        "highgo" => Highgo.Provider(SqlServer.NoDriverFactory.Instance),
        "gbase8c" => Gbase8c.Provider(SqlServer.NoDriverFactory.Instance),
        "duckdb" => DuckDb.Provider(SqlServer.NoDriverFactory.Instance),
        "mysql" => MySql.Provider(SqlServer.NoDriverFactory.Instance),
        "tidb" => TiDb.Provider(SqlServer.NoDriverFactory.Instance),
        "oceanbase" => OceanBase.Provider(SqlServer.NoDriverFactory.Instance),
        "oracle" => Oracle.Provider(SqlServer.NoDriverFactory.Instance),
        "dameng" => Dameng.Provider(SqlServer.NoDriverFactory.Instance),
        "shentong" => ShenTong.Provider(SqlServer.NoDriverFactory.Instance),
        "gbase8s" => Gbase8s.Provider(SqlServer.NoDriverFactory.Instance),
        "tdengine" => TDengine.Provider(SqlServer.NoDriverFactory.Instance),
        "xugu" => Xugu.Provider(SqlServer.NoDriverFactory.Instance),
        "sqlite" => Sqlite.Provider(SqlServer.NoDriverFactory.Instance),
        "clickhouse" => ClickHouse.Provider(SqlServer.NoDriverFactory.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "没有这一家的离线组装根"),
    };
}