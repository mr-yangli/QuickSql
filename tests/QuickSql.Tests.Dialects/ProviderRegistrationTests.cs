using QuickSql.Providers;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§4 一行注册的登记面。这一册不测方言文本，测的是"生成器有没有跑起来 + 表能不能读"：
/// <see cref="ProviderRegistry"/> 里应当有 18 颗 <see cref="DbKind"/>，与 §5 那张 provider 总表
/// 一颗不差；<see cref="QuickSqlOrm.Create(DbKind,string,System.Data.Common.DbProviderFactory)"/>
/// 出来的 <see cref="QuickSqlOrm"/> 应当带对的 <see cref="IDbProvider.Kind"/>。</summary>
/// <remarks>触发链是 <c>QuickSql.Tests.Common.csproj</c> 把 <c>QuickSql.Generators</c> 以
/// <c>OutputItemType="Analyzer"</c> 挂进去、并引用全部叶 provider 工程，所以本册拿到的登记表
/// 由 <c>QuickSql.Tests.Common</c> 那一份 <c>[ModuleInitializer]</c> 一次性铺好。测试代码里
/// 必须先摸一次 <c>QuickSql.TestInfra.SqlServer</c>（同汇编类型），module initializer 才会跑。
/// 原句"15 颗 provider 工程"记的是 Sqlite 落笔之前的那一份引用面；<b>"第十六颗走同一份触发链、
/// provider 工程是普通引用"这一句在本册里不是可省的字</b>——若哪天 <c>Sqlite</c> 从 <c>Common</c>
/// 里被摘出去，本册第一次跑就会红在"Registered 少一颗"这一格而不是悄悄少测一家。第十七颗
/// <see cref="DbKind.DuckDb"/> 与 Sqlite 同档（v0.39 第一笔）：<c>QuickSql.Providers.DuckDb</c>
/// 亦是普通引用，登记由同一份构造链铺好。第十八颗 <see cref="DbKind.ClickHouse"/>
///（v0.39 第二笔）同一条触发链（普通引用），登记同铺。</remarks>
public class ProviderRegistrationTests
{
    /// <summary>把 <c>QuickSql.Tests.Common</c> 那一份程序集摸热：读同汇编的一颗静态属性即可，
    /// CLR 的模块初始化契约保证"取静态字段/属性"这一族动作之前会跑一次 <c>ModuleInitializer</c>。
    /// <b>只写 <c>typeof(SqlServer)</c> 不够</b>——类型句柄不触发 initializer，本册第一次跑就踩过这个坑。
    /// 少了这一句，<see cref="ProviderRegistry.Registered"/> 会是空集，测试红的其实是"初始化没跑"，
    /// 不是"表没铺"。</summary>
    static void TouchCommonAssemblyModuleInitializer() => _ = SqlServer.NoDriverFactory.Instance;

    [Fact]
    public void Registry_HasAllEighteenKinds()
    {
        TouchCommonAssemblyModuleInitializer();

        var expected = new[]
        {
            DbKind.SqlServer, DbKind.MySql, DbKind.Oracle, DbKind.PostgreSql,
            DbKind.Kingbase, DbKind.OpenGauss, DbKind.GaussDb, DbKind.Highgo,
            DbKind.Dameng, DbKind.ShenTong, DbKind.Xugu, DbKind.Gbase8s,
            DbKind.Gbase8c, DbKind.TiDb, DbKind.OceanBase, DbKind.Sqlite,
            DbKind.DuckDb, DbKind.ClickHouse, DbKind.TDengine,
        };

        foreach (var kind in expected)
            Assert.True(ProviderRegistry.Contains(kind), $"DbKind.{kind} 未登记（生成器没跑或 provider 没引用）");

        Assert.Equal(19, expected.Length);
        Assert.Equal(19, ProviderRegistry.Registered.Distinct().Count());
    }

    [Fact]
    public void Create_ReturnsProviderForKind()
    {
        TouchCommonAssemblyModuleInitializer();

        var orm = QuickSqlOrm.Create(DbKind.MySql, "Server=offline;Database=never;Integrated Security=True",
            SqlServer.NoDriverFactory.Instance);

        Assert.Equal(DbKind.MySql, orm.Provider.Kind);
        Assert.Equal("mysql", ProviderRegistry.DriverKeyOf(DbKind.MySql));
    }

    [Fact]
    public void Create_ThrowsForUnknownKind_WhenKindNotRegistered()
    {
        TouchCommonAssemblyModuleInitializer();

        // 用一颗不存在的整数当 DbKind 走"未登记"那一支——今天值域是 0..17（连续枚举），
        // 99 落在这个范围之外，等价于"注册表里没这颗"，抛的是 InvalidOperationException 而不是 DriverNotAvailable。
        var unregistered = (DbKind) 99;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            QuickSqlOrm.Create(unregistered, "x", SqlServer.NoDriverFactory.Instance));
        Assert.Contains("未在 ProviderRegistry 中登记", ex.Message);
    }
}
