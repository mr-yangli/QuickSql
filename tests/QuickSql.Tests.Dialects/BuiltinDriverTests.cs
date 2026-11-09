using QuickSql.DriverLoading;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§9.5 的另一头：<b>现场什么都没配</b>时驱动从哪来。
///
/// <para>名字来源只有一处 —— 内建默认表 <see cref="BuiltinDrivers"/>。曾经这里还有一条清单
/// （<c>QuickSqlDrivers.json</c>）优先于内建表，而"清单没复制到输出目录"是<em>静默</em>的，
/// 于是一个只报库种与串的宿主在启动期报出来的是"驱动不存在"，真实原因却是一份看不见的文件。
/// 那一层已整层删除，内建表从"退路"升成唯一来源，零配置就是「<see cref="DbKind"/> + 连接串」装配起来。
/// 这几件事都要能离线钉住，所以本文件不连库、也不要任何一份真驱动。</para></summary>
public sealed class BuiltinDriverTests
{
    /// <summary>一个谁也不会去配的键，只属于最后那条"内建表里也没有"的用例。</summary>
    const string UnlistedKey = "nosuchprovider";

    [Fact]
    public void Every_db_kind_has_a_builtin_row()
    {
        // 这颗断言钉的是覆盖面：新增一颗 DbKind 而忘了配内建行，就会在只报库种与串的宿主上变成启动期报错。
        // key 的取法照 <see cref="DbKind"/> 名的小写（无映射表）。
        foreach (var kind in Enum.GetValues<DbKind>())
        {
            var reference = BuiltinDrivers.Find(kind.ToString().ToLowerInvariant());

            Assert.NotNull(reference);
            Assert.False(string.IsNullOrEmpty(reference!.AssemblyName), $"{kind} 的内建行没有程序集名");
        }

        Assert.Equal(Enum.GetValues<DbKind>().Length, BuiltinDrivers.Drivers.Count);
    }

    [Fact]
    public void A_default_registry_still_reaches_the_assembly_load_step()
    {
        // 内建表接住"什么都没配"这一档之后要一路走到装载那一步，于是报错说的是"这颗程序集装不上"，
        // 而不是"表里没有这一行"——两句话各有各的修法。金仓驱动在本仓库的机器上从来没有装上过，
        // 所以这里量到的是装载失败（Kdbndp 找不到），正是"名字来源已解析、只差那颗 DLL"那一格。
        var registry = new DriverRegistry();

        var failure = Record.Exception(() => registry.Factory("kingbase"));

        Assert.IsType<DriverNotAvailableException>(failure);
        Checks.Has("Kdbndp", failure!.Message);
        Checks.Has("内建默认", failure.Message);
    }

    [Fact]
    public void A_key_the_builtin_table_does_not_know_points_at_the_two_remedies()
    {
        // 内建表里没有这一行时，消息要能把两条出路原样写出来（把 DLL 丢进 drivers/ ∶ 用 Register 注入工厂），
        // 而不是只说"不存在"。清单删除之后，这两条就是现场仅剩的覆盖手段。
        var registry = new DriverRegistry();

        var failure = Record.Exception(() => registry.Factory(UnlistedKey));

        Assert.IsType<DriverNotAvailableException>(failure);
        Checks.Has(UnlistedKey, failure!.Message);
        Checks.Has("drivers/", failure.Message);
        Checks.Has("Register", failure.Message);
    }
}
