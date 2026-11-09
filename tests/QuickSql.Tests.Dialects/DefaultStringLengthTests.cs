using QuickSql;
using QuickSql.Generated;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>orm 级「未声明宽度」兜底的离线那一半（<c>QuickSqlOrmOptions.DefaultStringLength</c>）。
///
/// <para>钉的是宽度四态的分界：<b>未声明</b>吃默认；<b>显式宽度</b>赢过默认；<b>显式非正数</b>是
/// "这一列明确不要上限"，同样不吃默认；<b>点名了存储型</b>的列也不吃——那一列的形状已经由声明者
/// 自己钉死，再塞一个契约宽度只会让写侧长度尺子拒掉列装得下的值。</para>
///
/// <para>还钉两件容易在两条注册路径上分叉的事：一是不配这一格时形状与旧行为逐字相同（未声明仍落
/// 大文本）；二是 G1（生成）与 G2（反射）对同一批列给出同一个宽度——收尾在
/// <see cref="EntityContractFactory"/> 那一处只有一份实现，这一颗是对着它量的。全程离线，
/// 碰驱动即红（<see cref="SqlServer.NoDriverFactory"/>）。</para></summary>
public sealed class DefaultStringLengthTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm Orm(int? defaultStringLength = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider,
            ConnectionString = SqlServer.OfflineConnectionString,
            DefaultStringLength = defaultStringLength,
        });

    static QuickSqlOrm Generated(int? defaultStringLength = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider,
            ConnectionString = SqlServer.OfflineConnectionString,
            DefaultStringLength = defaultStringLength,
        }, new GeneratedEntitySchemaRegistry(Provider, QuickSqlGeneratedMetadata.TryGet,
            defaultStringLength: defaultStringLength));

    static ColumnContract Column(IEntitySchemaRegistry schemas, string member)
        => schemas.Contract<WidthProbeRow>().Columns.Single(c => c.MemberName == member);

    [Fact]
    public void Unannotated_strings_take_the_default_while_declarations_win()
    {
        var schemas = Orm(96).Schemas;

        var bare = Column(schemas, nameof(WidthProbeRow.Bare));
        Assert.Equal(96, bare.MaxChars);
        Assert.Equal("NVARCHAR(96)", Provider.Types.GetStoreType(bare));
        Assert.Contains("默认长度 96", bare.Origin.Note);

        // 默认不只是 DDL 的装饰：写侧长度尺子按同一个数拒，97 颗字符在发 SQL 之前就不过。
        Assert.True(bare.FitsChars(new string('x', 96)));
        Assert.False(bare.FitsChars(new string('x', 97)));

        // 显式宽度赢过默认。
        var narrow = Column(schemas, nameof(WidthProbeRow.Narrow));
        Assert.Equal(32, narrow.MaxChars);
        Assert.Equal("NVARCHAR(32)", Provider.Types.GetStoreType(narrow));

        // 显式非正数 = 明确不要上限：不吃默认。
        var unlimited = Column(schemas, nameof(WidthProbeRow.Unlimited));
        Assert.Null(unlimited.MaxChars);
        Assert.Equal("NVARCHAR(MAX)", Provider.Types.GetStoreType(unlimited));

        // 点名了存储型的列也不吃默认。
        var named = Column(schemas, nameof(WidthProbeRow.Named));
        Assert.Null(named.MaxChars);
        Assert.Equal("nvarchar(max)", Provider.Types.GetStoreType(named));
    }

    [Fact]
    public void Without_the_option_the_shape_is_unchanged()
    {
        var bare = Column(Orm().Schemas, nameof(WidthProbeRow.Bare));

        Assert.Null(bare.MaxChars);
        Assert.Equal("NVARCHAR(MAX)", Provider.Types.GetStoreType(bare));
    }

    [Fact]
    public void Generated_and_reflected_paths_agree_on_every_state()
    {
        Assert.True(QuickSqlGeneratedMetadata.TryGet(typeof(WidthProbeRow), out _),
            $"{nameof(WidthProbeRow)} 没有被 G1 覆盖：这一颗会退化成 G2 自己比自己。");

        string[] members =
        [
            nameof(WidthProbeRow.Bare),
            nameof(WidthProbeRow.Narrow),
            nameof(WidthProbeRow.Unlimited),
            nameof(WidthProbeRow.Named),
        ];

        foreach (var member in members)
            Assert.Equal(Column(Orm(96).Schemas, member).MaxChars, Column(Generated(96).Schemas, member).MaxChars);

        // 只比"两侧相等"不够：两条路一起退化也会通过，所以未声明那一格再钉一颗绝对值。
        Assert.Equal(96, Column(Generated(96).Schemas, nameof(WidthProbeRow.Bare)).MaxChars);
    }

    [Fact]
    public void Non_positive_default_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Orm(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Orm(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Generated(0));
    }

    /// <summary>宽度四态在<b>每一家</b>的存储型上各是什么样：契约那一半（96 / 32 / null / null）与
    /// 发射那一半（各家自己的型名）一起钉。乘 4 的两家（GBase 8s、TDengine，n 量字节）与
    /// 无宽度档各不相同的三家（TEXT / CLOB / VARCHAR(65517)）是这张表里真正会分叉的格子。
    /// 点名了存储型的列在全表里原样穿过——那是各家同一条判据（不校验、不翻译），不是这一家的例外。</summary>
    [Theory]
    [InlineData("sqlserver", "NVARCHAR(96)", "NVARCHAR(32)", "NVARCHAR(MAX)")]
    [InlineData("postgresql", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("kingbase", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("opengauss", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("gaussdb", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("highgo", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("gbase8c", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("duckdb", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("mysql", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("tidb", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("oceanbase", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("oracle", "VARCHAR2(96 CHAR)", "VARCHAR2(32 CHAR)", "CLOB")]
    [InlineData("dameng", "VARCHAR2(96 CHAR)", "VARCHAR2(32 CHAR)", "CLOB")]
    [InlineData("shentong", "VARCHAR2(96 CHAR)", "VARCHAR2(32 CHAR)", "CLOB")]
    [InlineData("gbase8s", "VARCHAR(384)", "VARCHAR(128)", "CLOB")]
    [InlineData("tdengine", "VARCHAR(384)", "VARCHAR(128)", "VARCHAR(65517)")]
    [InlineData("xugu", "VARCHAR(96)", "VARCHAR(32)", "CLOB")]
    [InlineData("sqlite", "VARCHAR(96)", "VARCHAR(32)", "TEXT")]
    [InlineData("clickhouse", "String", "String", "String")]
    public void Every_provider_carries_the_default_into_its_store_type(string name, string wide, string narrow, string unbounded)
    {
        var provider = ProviderOf(name);
        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = "Server=offline",
            DefaultStringLength = 96,
        });

        var columns = orm.Schemas.Contract<WidthProbeRow>().Columns;
        ColumnContract Column(string member) => columns.Single(c => c.MemberName == member);

        Assert.Equal(96, Column(nameof(WidthProbeRow.Bare)).MaxChars);
        Assert.Equal(32, Column(nameof(WidthProbeRow.Narrow)).MaxChars);
        Assert.Null(Column(nameof(WidthProbeRow.Unlimited)).MaxChars);
        Assert.Null(Column(nameof(WidthProbeRow.Named)).MaxChars);

        Assert.Equal(wide, provider.Types.GetStoreType(Column(nameof(WidthProbeRow.Bare))));
        Assert.Equal(narrow, provider.Types.GetStoreType(Column(nameof(WidthProbeRow.Narrow))));
        Assert.Equal(unbounded, provider.Types.GetStoreType(Column(nameof(WidthProbeRow.Unlimited))));
        Assert.Equal("nvarchar(max)", provider.Types.GetStoreType(Column(nameof(WidthProbeRow.Named))));
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