using System.Data.Common;
using QuickSql.DriverLoading;
using QuickSql.Providers.MySql;

namespace QuickSql.TestInfra;

/// <summary>MySQL 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/> 同形：
/// <b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是第四档，存在的理由与金仓、达梦那两份<em>同一条</em>：本机没有任何一台 MySQL 血统的实例
/// （§13.4 授权的只有两台 SQL Server），清单里那一行的程序集名 <c>MySqlConnector</c> 也从未被解析成功过
/// （§13.5 台账里这一行整行「未实测」），所以这里<em>没有</em> <see cref="SqlServer.Driver"/>
/// 的对应物 —— 有了它就会被误读成"这条解析路径验过"。这一家与另两家不一样的地方只有一处：
/// 清单里那一行是三行「未实测」里唯一公开、MIT 且 AOT 友好的驱动（sqlserver 那一颗同样公开且 MIT，
/// 但它不在"未实测"这三行里 —— 本条上一版写作"四行里唯一"，交账时按清单自己第一行推翻），
/// 所以它也是第一次真库实测最可能落的那一档；
/// 那一天要补的是连接串解析与真库用例，不是在这里先猜默认值。</para>
///
/// <para>连接串占位符仍然复用 <see cref="SqlServer.OfflineConnectionString"/>（SqlServer 的那一份）：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第四家不需要第四种假配置。</para>
///
/// <para><b>【2026-09-29 改判 —— 原文照抄在前，改判在后】</b>上面两段里三句今天都不成立了：
/// ① "本机没有任何一台 MySQL 血统的实例"——用户提供了一台 <c>192.168.0.228:3306/TestDB</c>，
/// 而 <c>SELECT VERSION()</c> 交 <c>8.4.8</c>、<c>@@version_comment</c> 交
/// <c>MySQL Community Server - GPL</c>【实测·S-01 ∶ S-02，<c>obj/logs/v37-my-recon-1.log</c>】；
/// ② "<c>MySqlConnector</c> 从未被解析成功过"——清单里 <c>"mysql"</c> 那一行经 <c>DriverRegistry</c>
/// 解析出了工厂（<see cref="Driver"/> 现在有了，与 <see cref="PostgreSql"/> 走过的是同一道改判）；
/// ③ "那一天要补的是连接串解析"——那一天到了，而它确实<em>先</em>撞在解析上：这一颗驱动的连接串构建器
/// <b>不吃 URI 形</b>（<c>MySqlConnectionStringBuilder("mysql://…")</c> 报
/// <c>Format of the initialization string</c>），与 <see cref="PostgreSql"/> 那一族"URI 直接就是合法连接串"
/// 正相反，所以 <see cref="LocalConnectionFile"/> 里写的是 <c>Server=…;Port=…;…</c> 那一形。
///
/// 这一颗组装根今天验的仍是两件事（"清单里那一行能解析出工厂、工厂能连上"），而<em>不</em>验"这一家已适配"
/// —— 后者由集成层那条家族缝与集成册的逐条读数证。</para></summary>
public static class MySql
{
    public static MySqlProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第四家上与前三家享有同一个探针。给了 <paramref name="factory"/>
    /// 却不给连接串时取 <see cref="TestConnectionString"/>，与 <see cref="PostgreSql.Orm"/> 同一套三分岔
    /// （没配置就问出来，不退回某个"肯定连得上"的库）。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? (factory is null ? SqlServer.OfflineConnectionString : TestConnectionString),
        });

    /// <summary>走完整的运行期驱动解析链（清单 → 独立 <c>AssemblyLoadContext</c> → 工厂），与
    /// <see cref="SqlServer.Driver"/>、<see cref="Xugu.Driver"/>、
    /// <see cref="PostgreSql.Driver"/> 同一条路、同一条"不许直接 <c>new</c> 工厂"的理由。
    ///
    /// <para><b>本行验的三件事</b>：清单里 <c>"mysql"</c> 那一颗键读得出、程序集名 <c>MySqlConnector</c>
    /// 解析得出、交回来的工厂 <c>CreateConnection()</c> 连得上。清单那一行刻意<em>不</em>写
    /// <c>factoryType</c>（<c>MySqlProvider.DriverKey</c> 的注释写的就是这个省略），于是走
    /// "扫导出类型里第一个 <c>DbProviderFactory</c> 子类"那条兜底 —— 本行是那条兜底在本仓库
    /// <b>第一次真跑通</b>：前三家各有 <c>factoryType</c> 或厂商产物，只有这一颗是靠扫描命中的。</para></summary>
    public static DbProviderFactory Driver()
        => new DriverRegistry().Factory(MySqlProvider.DriverKey);

    /// <summary>集成测试的第五台目标库：<b>没有默认值</b>，与 <see cref="SqlServer.TestConnectionString"/>、
    /// <see cref="Xugu.TestConnectionString"/>、<see cref="PostgreSql.TestConnectionString"/> 同一条判据。
    /// 环境变量 <c>QUICKSQL_MYSQL_CONNECTION</c> 优先，其次仓库根的 <see cref="LocalConnectionFile"/>。
    /// 两处都没有就抛，消息本身就是配置说明。
    ///
    /// <para><b>为什么这一颗文件必须是 <c>Server=…;…</c> 那一形</b>：见类注释改判第 ③ 条那一颗实测。
    /// <see cref="PostgreSql.TestConnectionString"/> 那一段允许直接贴 URI，是因为 Npgsql 的构建器认那一形，
    /// 而本家不认 —— 两处同源而不同形，所以那颗<em>不能照抄</em>。</para></summary>
    public static string TestConnectionString
        => ResolveConnectionString() ?? throw new InvalidOperationException(
               $"没有配置 MySQL 集成测试目标库：设环境变量 QUICKSQL_MYSQL_CONNECTION，或在仓库根放 {LocalConnectionFile}" +
               "（第一行非 # 内容即连接串，形如 Server=…;Port=…;Database=…;User ID=…;Password=…）。" +
               "该驱动的连接串解析器不吃 mysql:// 那一形 URI（实测见类注释），所以这里不收 URI。该文件含本机凭据，不要提交。");

    const string LocalConnectionFile = "quicksql-test.mysql.connection.local.txt";

    static string? ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_MYSQL_CONNECTION");

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
