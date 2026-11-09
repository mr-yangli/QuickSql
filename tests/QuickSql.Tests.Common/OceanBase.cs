using System.Data.Common;
using QuickSql.DriverLoading;
using QuickSql.Providers.OceanBase;

namespace QuickSql.TestInfra;

/// <summary>OceanBase（MySQL 模）这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/>、<see cref="TiDb"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 MySQL 血统的第三颗叶子，存在的理由与 <see cref="TiDb"/> 那一份<em>同一条而多一格</em>：
/// 清单里那颗 <c>oceanbase</c> 键与 <c>mysql</c>／<c>tidb</c> 两颗共用同一份 <c>MySqlConnector</c> 程序集名
/// （清单第一次三键一 assembly），多出来的那一格是本家独有的：双模库的"模式在连接上决定"今天没有任何程序入口能问出来
/// （§14 那格参数名单的第二个实例）——这里造的每一颗 provider 都只在主张"假设这条连接在 MySQL 模"。</para>
///
/// <para><b>【2026-11-09 现场】</b>用户提供的一台 OceanBase（<c>192.168.0.228:2881</c>，MySQL 模，
/// SYS 租户、用户 root）走的是 <see cref="MySql"/> 那一条配置通路：环境变量 <c>QUICKSQL_OCEANBASE_CONNECTION</c>
/// 优先，其次仓库根那颗 <see cref="LocalConnectionFile"/>（`*.connection.local.txt` 已被 .gitignore 挡住）。
/// 连接串形与 <see cref="MySql"/> 同一式（<c>Server=…;Port=…;Database=…;User ID=root@SYS;Password=…</c>），
/// <b>租户名写在用户那一格</b>（<c>root@SYS</c>）——厂商文档的 <c>user@tenant#cluster</c> 那一形里的
/// <c>#cluster</c> 只在走 OBProxy 时需要，2881 是 OBServer 直连口，escalation 不在这里。这一点要真跑一次才算数。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 数到第六家仍然不需要第六种假配置。</para></summary>
public static class OceanBase
{
    public static OceanBaseProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在这一家上与前面几家享有同一个探针。给了 <paramref name="factory"/>
    /// 却不给连接串时取 <see cref="TestConnectionString"/>，与 <see cref="MySql.Orm"/> 同一套三分岔
    /// （没配置就问出来，不退回某个"肯定连得上"的库）。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? (factory is null ? SqlServer.OfflineConnectionString : TestConnectionString),
        });

    /// <summary>走完整的运行期驱动解析链（清单 → 独立 <c>AssemblyLoadContext</c> → 工厂），与
    /// <see cref="SqlServer.Driver"/>、<see cref="Xugu.Driver"/>、<see cref="PostgreSql.Driver"/>、
    /// <see cref="MySql.Driver"/> 同一条路。清单里 <c>"oceanbase"</c> 那一行与 <c>mysql</c> 那一行
    /// 共用 <c>MySqlConnector</c>，也共用"不写 <c>factoryType</c>、靠扫导出类型兜底"那条路径。</summary>
    public static DbProviderFactory Driver()
        => new DriverRegistry().Factory(OceanBaseProvider.DriverKey);

    /// <summary>集成测试的第六台目标库：<b>没有默认值</b>，与 <see cref="SqlServer.TestConnectionString"/>、
    /// <see cref="Xugu.TestConnectionString"/>、<see cref="PostgreSql.TestConnectionString"/>、
    /// <see cref="MySql.TestConnectionString"/> 同一条判据。环境变量 <c>QUICKSQL_OCEANBASE_CONNECTION</c> 优先，
    /// 其次仓库根的 <see cref="LocalConnectionFile"/>。两处都没有就抛，消息本身就是配置说明。
    ///
    /// <para><b>为什么这一颗文件必须是 <c>Server=…;…</c> 那一形</b>：与 <see cref="MySql"/> 同一条理由 ——
    /// <c>MySqlConnector</c> 的连接串构建器不吃 <c>mysql://</c> 那一形 URI。</para>
    ///
    /// <para><b>租户写在用户那一格</b>：SYS 租户的 root 写成 <c>User ID=root@SYS</c>（大小写不敏感，
    /// 厂商客户端常用 <c>root@sys</c>）。直连 2881 时不需要 <c>#cluster</c>；若现场只有 OBProxy，
    /// 改端口到 2883 并把连接串写成 <c>root@SYS#&lt;cluster&gt;</c>，这条通路本身不用改。</para></summary>
    public static string TestConnectionString
        => ResolveConnectionString() ?? throw new InvalidOperationException(
               $"没有配置 OceanBase 集成测试目标库：设环境变量 QUICKSQL_OCEANBASE_CONNECTION，或在仓库根放 {LocalConnectionFile}" +
               "（第一行非 # 内容即连接串，形如 Server=192.168.0.228;Port=2881;Database=…;User ID=root@SYS;Password=…）。" +
               "该驱动的连接串解析器不吃 mysql:// 那一形 URI（与 MySql 那一颗同一条实测），所以这里不收 URI。该文件含本机凭据，不要提交。");

    const string LocalConnectionFile = "quicksql-test.oceanbase.connection.local.txt";

    static string? ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_OCEANBASE_CONNECTION");

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
}