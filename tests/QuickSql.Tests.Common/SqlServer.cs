using System.Data.Common;
using QuickSql.DriverLoading;
using QuickSql.Providers.SqlServer;

namespace QuickSql.TestInfra;

/// <summary>组装根（§12.7 F：provider 由调用方直接构造并交进来，内核不做 <c>Type.GetType(string)</c>）。
/// 测试项目就是这里的"调用方"，所以这份组装代码本身就是 DI 层该做的事的样例。</summary>
public static class SqlServer
{
    public static SqlServerProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>组装根入口。<paramref name="factory"/> 为 null 时用 <see cref="NoDriverFactory"/> ——
    /// 只有不碰连接的测试（生成 SQL）可以这么用，一旦有代码试图连库就会立刻变红。
    /// 要改 <c>PagingOrdering</c>/<c>Aop</c> 等选项的测试请直接 new <see cref="QuickSql.QuickSqlOrm"/>：
    /// 这里不预留一套"转发所有选项"的参数表，那只会变成第二份要各自维护的构造器。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(Options(factory, connectionString));

    static QuickSql.QuickSqlOrmOptions Options(DbProviderFactory? factory, string? connectionString)
        => new()
        {
            Provider = Provider(factory ?? NoDriverFactory.Instance),
            // 没给 factory = 只产 SQL、不碰连接，占位串就够；给了 factory 就是要连库，
            // 这时没配置必须问出来，而不是退回某个"肯定连得上"的库。
            ConnectionString = connectionString ?? (factory is null ? OfflineConnectionString : TestConnectionString),
        };

    /// <summary>集成测试的目标库：<b>没有默认值</b>。环境变量 <c>QUICKSQL_TEST_CONNECTION</c> 优先，
    /// 其次仓库根的 <see cref="LocalConnectionFile"/>（本机凭据，不入库）。两处都没有就抛，
    /// 消息本身就是配置说明 —— 集成用例因此是"带着理由 skip"，不是"连了个没人要求的库"。</summary>
    public static string TestConnectionString
        => ResolveConnectionString() ?? throw new InvalidOperationException(
               $"没有配置集成测试目标库：设环境变量 QUICKSQL_TEST_CONNECTION，或在仓库根放 {LocalConnectionFile}" +
               "（第一行非 # 内容即连接串）。该文件含本机凭据，不要提交。");

    /// <summary>给"只生成 SQL、绝不连库"的测试用的占位串。<b>故意不是真配置</b>：
    /// 这类测试哪天因为占位串被换成真串而开始依赖活库，就说明它写错了项目（该去 Integration）。</summary>
    public const string OfflineConnectionString = "Server=offline;Database=never-connected;Integrated Security=True";

    const string LocalConnectionFile = "quicksql-test.connection.local.txt";

    static string? ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_TEST_CONNECTION");

        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, LocalConnectionFile);

            if (!File.Exists(file)) continue;

            var line = File.ReadLines(file).FirstOrDefault(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l));

            return line?.Trim();
        }

        return null;
    }

    /// <summary>走完整的运行期驱动解析链（清单 → <see cref="AssemblyLoadContext"/> → 工厂），
    /// §1.2 原则 3 与 §9.5 在 P0 的第一次真跑就是这一句。<b>不是</b> <c>new SqlClientFactory()</c>：
    /// 那样会把要验的东西绕过去。</summary>
    public static DbProviderFactory Driver()
        => new DriverRegistry().Factory(SqlServerProvider.DriverKey);

    /// <summary>注入已经在进程里的工厂（§9.5 的 AOT 干净路径）：调用方自己引用驱动包，
    /// 把 <c>DbProviderFactory</c> 交进来，编译期仍然没有驱动引用。</summary>
    public static DbProviderFactory Injected(DbProviderFactory factory)
    {
        var registry = new DriverRegistry();

        registry.Register(SqlServerProvider.DriverKey, factory);

        return registry.Factory(SqlServerProvider.DriverKey);
    }

    /// <summary>"生成 SQL 不需要驱动"的可执行断言（§3）。每个成员都抛：<b>只要有一个黄金样本测试变红
    /// 说驱动被调用了，那就是这条设计约束被破坏了</b>，而不是"替身没配好"。
    /// 所以它不是 mock，是一根探针。</summary>
    public sealed class NoDriverFactory : DbProviderFactory
    {
        public static NoDriverFactory Instance { get; } = new();

        static Exception Touched(string member)
            => new InvalidOperationException(
                $"{member} 被调用了：只生成 SQL 的测试不该碰驱动（§3 的「ToSql() 不连库」）。" +
                "如果这条调用是必要的，说明读路径上混进了连接依赖，要按缺陷处理。");

        public override DbConnection CreateConnection() => throw Touched(nameof(CreateConnection));

        public override DbCommand CreateCommand() => throw Touched(nameof(CreateCommand));

        public override DbParameter CreateParameter() => throw Touched(nameof(CreateParameter));

        public override DbDataAdapter CreateDataAdapter() => throw Touched(nameof(CreateDataAdapter));

        public override DbCommandBuilder CreateCommandBuilder() => throw Touched(nameof(CreateCommandBuilder));

        public override DbConnectionStringBuilder CreateConnectionStringBuilder() => throw Touched(nameof(CreateConnectionStringBuilder));
    }
}
