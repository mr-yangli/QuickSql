using System.Data.Common;
using System.Reflection;
using QuickSql.Providers;
using QuickSql.Providers.MySqlBase;
using QuickSql.Providers.PostgreSqlBase;
using QuickSql.Providers.SqlServer;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>建库那一步读的是连接串里哪一颗键（<see cref="IDbProvider.EnsureDatabaseExistsAsync"/>）。
///
/// <para>这一格不连库也钉得住，而它值得钉的理由是<em>失败形状</em>：读不到库名走的是「这条串没指名字、
/// 本来就不该建库」那一岔，静默返回。所以一种错读法（同义词少列一颗）产出与一种对读法完全一样的外观，
/// 差别要推迟到调用方真去连那座不存在的库时才现形 —— 报出来的是 <c>Unknown database</c>，
/// 而报错里已经没有「本应建库」这件事了。MySQL 血统这一档就这么炸过一次：它只读 <c>Database</c>，
/// 而本项目的串写的是 <c>Initial Catalog</c>。</para>
///
/// <para>另一格是 <em>裸</em> <see cref="DbConnectionStringBuilder"/> 不做同义词归一（.NET 10 实测：
/// 只有大小写忽略，空格与下划线都算不同键）。这一条决定了候选表必须由 provider 自己列全，
/// 所以本文件把每一家列了哪几颗也一并钉住 —— 新增一颗同义词而忘了列，红在这里，不红在别人的机器上。</para></summary>
public sealed class ConnectionStringCatalogTests
{
    /// <summary>本项目的真实串形（合成值，形状照 <c>appsettings.json</c>，那颗键就是踩坑的那颗）。</summary>
    const string AppStyle = "Server=example;Port=3306;User ID=u;Password=p; Initial Catalog=MedicineNext;Charset=utf8";

    [Fact]
    public void A_table_that_only_lists_Database_misses_Initial_Catalog()
    {
        // 这一条是事故本体的最小复现：同义词少列一颗 → 读不到 → 建库静默跳过，而不是抛。
        var catalog = new ConnectionStringCatalog("Database");

        Assert.Null(catalog.Read(Bare(AppStyle)));
    }

    [Fact]
    public void Listing_the_synonym_recovers_the_name()
    {
        var catalog = new ConnectionStringCatalog("Database", "Initial Catalog");

        Assert.Equal("MedicineNext", catalog.Read(Bare(AppStyle)));
    }

    [Fact]
    public void The_bare_builder_does_not_normalize_keyword_spelling()
    {
        // 钉住"为什么候选表必须一颗颗列"：这三颗在驱动眼里都算库名，在裸 builder 眼里是三个互不相干的键。
        var builder = Bare("Server=example;InitialCatalog=MedicineNext");

        Assert.Null(new ConnectionStringCatalog("Database", "Initial Catalog").Read(builder));
        Assert.Equal("MedicineNext", new ConnectionStringCatalog("InitialCatalog").Read(builder));
    }

    [Fact]
    public void Two_keys_disagreeing_throws_instead_of_choosing()
    {
        var catalog = new ConnectionStringCatalog("Database", "Initial Catalog");

        var failure = Checks.Raised<QuickSqlException>(
            () => catalog.Read(Bare("Server=example;Database=AAA;Initial Catalog=BBB")));

        Assert.Contains("AAA", failure.Message, StringComparison.Ordinal);
        Assert.Contains("BBB", failure.Message, StringComparison.Ordinal);

        // 同值两颗不算矛盾：串是拼接出来的，两颗写着同一个名字是常态。
        Assert.Equal("AAA", catalog.Read(Bare("Server=example;Database=AAA;Initial Catalog=AAA")));
    }

    [Fact]
    public void An_empty_value_is_no_name_at_all()
    {
        var catalog = new ConnectionStringCatalog("Database", "Initial Catalog");

        Assert.Null(catalog.Read(Bare("Server=example;Database=")));
        Assert.Equal("BBB", catalog.Read(Bare("Server=example;Database=;Initial Catalog=BBB")));
    }

    [Fact]
    public void Clear_removes_every_listed_key()
    {
        // 只摘读到的那一颗是不够的：留两颗互相矛盾的键，哪一颗说了算就交给驱动的解析顺序
        // （实测 MySqlConnector 2.6.0 对两颗并存不抛，取后出现的那颗）。
        var catalog = new ConnectionStringCatalog("Database", "Initial Catalog");
        var builder = Bare("Server=example;Database=AAA;Initial Catalog=AAA;Port=3306");

        catalog.Clear(builder);

        Assert.Null(catalog.Read(builder));
        Assert.DoesNotContain("AAA", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_table_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionStringCatalog());
    }

    [Theory]
    [InlineData(typeof(MySqlProviderBase), "Database", "Initial Catalog")]
    [InlineData(typeof(PostgreSqlProviderBase), "Database", "dbname", "db")]
    [InlineData(typeof(SqlServerProvider), "Database", "Initial Catalog", "InitialCatalog")]
    public void Each_provider_lists_the_synonyms_its_driver_accepts(Type providerType, params string[] expected)
    {
        // 这一条把"哪一家列了哪几颗"钉成台账：MySQL 那两颗是实测（MySqlConnector 2.6.0 对 db/init catalog
        // 一律按未知选项抛），PG 与 T-SQL 那几颗按各家文档。改这一格必须连带改驱动那头的证据，不然就红在这里。
        var catalog = DeclaredCatalog(providerType);

        Assert.Equal(expected, Keys(catalog));

        // 而真正要保证的是命中的那一句：每一家都要能从"非正主写法"的串里读出库名。
        foreach (string key in expected)
        {
            var read = catalog.Read(Bare($"Server=example;{key}=TargetDb"));

            Assert.NotNull(read);
            Assert.Equal("TargetDb", read);
        }
    }

    /// <summary>取该类自持的那张同义词表（私有静态字段，与 <c>SqlServerProvider</c> 重构前同一处判据）。</summary>
    static ConnectionStringCatalog DeclaredCatalog(Type providerType)
    {
        var field = providerType
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(u => u.FieldType == typeof(ConnectionStringCatalog));

        Assert.NotNull(field);

        return (ConnectionStringCatalog) field!.GetValue(null)!;
    }

    static string[] Keys(ConnectionStringCatalog catalog) =>
        [
            .. (string[])
                typeof(ConnectionStringCatalog)
                    .GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(catalog)!,
        ];

    static DbConnectionStringBuilder Bare(string connectionString) =>
        new() { ConnectionString = connectionString };
}
