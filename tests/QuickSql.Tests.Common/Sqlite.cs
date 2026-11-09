using System.Data.Common;
using QuickSql.DriverLoading;
using QuickSql.Providers.Sqlite;

namespace QuickSql.TestInfra;

/// <summary>SQLite 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Xugu"/>、<see cref="Gbase8s"/>
/// 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是第七档，而它落在"单层"那一串里的第四颗（前三颗是 <see cref="SqlServer"/>、
/// <see cref="Xugu"/>、<see cref="Gbase8s"/>），所以这份组装根也只交回一个 <see cref="SqliteProvider"/>，
/// 没有 <c>MySqlDialectTests</c> 里那枚<em>空壳证据</em>用的血统基类探针 —— 没有第二层，就没有
/// "两层产出相同"这句话可量。<b>它对拍的价值也不在"又多一处对读"</b>：这一族的产出与另六家无一族同形，
/// 所以它不进 <c>DialectParityTests</c>，本册量的是四颗异形格（见
/// <c>QuickSql.Tests.Common.csproj</c> 那一段与 <c>SqliteDialectTests</c>）。</para>
///
/// <para><b>这一档有另三颗单层家都没有的一格：这颗组装根<em>验得出</em>而不只是"挂着就会被误读"</b>。
/// <see cref="Gbase8s"/> 那一份类注释里写着「这里没有 <see cref="SqlServer.Driver"/> 的对应物，
/// 有了就会被误读成这条解析路径验过」，<see cref="Xugu"/> 那一份在同一格上从"没有对应物"改口成
/// "有身份了"（v0.32 第二格开了集成通路）。本家从第一天就有 <see cref="Driver"/>，
/// 而它<em>不许被离线册调用</em>：离线册拿 <see cref="SqlServer.NoDriverFactory"/>，碰驱动就是
/// §3 那条约束的红。只有 <c>tests/QuickSql.Tests.Integration</c>（那里 <c>Microsoft.Data.Sqlite</c>
/// 在引用闭包里）调得动它，出处是 <c>obj/sqlite-probe*/</c> 那批报告里的 A0 系列四颗。</para>
///
/// <para><b>连接配置这一格本家与另两家不同，而不同的那一格要说死</b>：另两家的串里有主机、端口、
/// 账号与口令，所以"没有默认值"是它们的天然防线（<see cref="Xugu.TestConnectionString"/> 那一段写的
/// 就是这个）。本家那一颗串里只有一个文件路径，而 <c>SQLite</c> 在 <c>Open()</c> 那一刻会把文件造出来，
/// 于是<em>任何路径都连得上</em>：默认值不会造成"连不上"的红，只会造成"凭空多一颗文件"。
/// 本家因此保留那道严格闸门 <see cref="TestConnectionString"/>（与另两家逐字同判据：没配就问出来），
/// 另外<em>只给集成层</em>开一条明确的、带名字的退路 <see cref="ScratchConnectionString"/>。
/// <b>那条退路不是"默认值"，因为它要调用方点名才能走到</b>，而调用它的那一处已经在
/// <c>QUICKSQL_TEST_PROVIDER=sqlite</c> 这条显式通路里了。</para></summary>
public static class Sqlite
{
    public static SqliteProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第七家上与前六家享有同一个探针。给了 <paramref name="factory"/>
    /// 却不给连接串时取 <see cref="TestConnectionString"/>，与 <see cref="SqlServer.Orm"/> 同一套三分岔。
    /// <b>本家这里刻意不接 <see cref="ScratchConnectionString"/> 当兜底</b>：那样一来任何一条忘了配置的
    /// 离线用例都会悄悄建出一颗文件库，而 §3 要的是它红。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? (factory is null ? SqlServer.OfflineConnectionString : TestConnectionString),
        });

    /// <summary>走完整的运行期驱动解析链（清单 → 独立 <c>AssemblyLoadContext</c> → 工厂），与
    /// <see cref="SqlServer.Driver"/>、<see cref="Xugu.Driver"/> 同一条路、
    /// 同一条"不许直接 <c>new</c> 工厂"的理由。
    ///
    /// <para><b>本行验的到底是哪三件事</b>：清单里 <c>"sqlite"</c> 那一颗键读得出、程序集名
    /// <c>Microsoft.Data.Sqlite</c> 解析得出、清单里那颗 <c>factoryType</c>
    /// （<c>Microsoft.Data.Sqlite.SqliteFactory</c>）就是它交回的那一颗 —— 第三件是本家独有的：<b>它是这份
    /// 清单上第一颗 <c>factoryType</c> 不是照厂商文档写、而是从活机器上读回来的键</b>，所以这里量的是
    /// "我们写进清单的那颗类名与现场那颗同名"，不是"我们猜对了一颗类名"。
    /// 它仍然<em>不</em>验这一层写的任何一个字（那是集成册那 95 条的活）。</para></summary>
    public static DbProviderFactory Driver()
        => new DriverRegistry().Factory(SqliteProvider.DriverKey);

    /// <summary>集成测试的目标库：<b>没有默认值</b>，与 <see cref="SqlServer.TestConnectionString"/>、
    /// <see cref="Xugu.TestConnectionString"/> 同一条判据。环境变量 <c>QUICKSQL_SQLITE_CONNECTION</c> 优先，
    /// 其次仓库根的 <see cref="LocalConnectionFile"/>。两处都没有就抛。
    ///
    /// <para><b>这一家为什么要单开一颗变量名而不是复用 <c>QUICKSQL_TEST_CONNECTION</c></b>：
    /// 与 <see cref="Xugu.TestConnectionString"/> 那段同一颗理由 —— 各家目标库共用一颗变量名会让
    /// "换一家跑"变成"覆盖另一家的配置"。这里还多一层：本家那一颗串里<em>没有口令</em>，
    /// 所以把它写进那颗本地文件纯粹是为了"目标路径也要显式"，不是为了藏字。</para></summary>
    public static string TestConnectionString
        => ResolveConnectionString() ?? throw new InvalidOperationException(
               $"没有配置 SQLite 集成测试目标库：设环境变量 QUICKSQL_SQLITE_CONNECTION，或在仓库根放 {LocalConnectionFile}" +
               "（第一行非 # 内容即连接串，形如 Data Source=<路径>）。该文件不入库。");

    const string LocalConnectionFile = "quicksql-test.sqlite.connection.local.txt";

    /// <summary>配了就把那一句交回来，没配交回 <c>null</c> 而<em>不是</em>抛。
    ///
    /// <para><b>它存在的唯一理由是集成夹具那一颗分岔</b>：本家的"实例"就是一颗文件，所以"没有配置"在
    /// 另两家的意思是"没有可连的库、用例必须被看见跳掉"，在本家的意思是"还没有那座库" —— 于是夹具
    /// 这一处允许它在临时目录里自建一座（<see cref="ScratchConnectionString"/>），而那一步只有点名
    /// 走到这里的那一处才走得到。<b><see cref="TestConnectionString"/> 那道闸门因此一个字不动</b>：
    /// 别处照旧"没配就问出来"。</para>
    ///
    /// <para><b>离线册不许调这一颗</b>：它绕开的是"没有配置"这一格的报错，而绕开之后<em>只对
    /// <c>QUICKSQL_TEST_PROVIDER=sqlite</c> 那一跑有意义</em>。</para></summary>
    public static string? TryTestConnectionString() => ResolveConnectionString();

    static string? ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_SQLITE_CONNECTION");

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

    /// <summary>给集成夹具自己用的那条<em>自建库</em>通路：在本机临时目录下按名字造一颗文件库。
    /// <b>它存在是因为本家的"实例"就是一颗文件</b>——§9.2 那句"建出来的表与查的表同一张"与 §13.4 那句
    /// "每场自建库"在另两家要一台授权实例才谈得上，在本家只要求一个可写目录。
    /// <b>它<em>不是</em> <see cref="TestConnectionString"/> 的兜底</b>（上面那段写着为什么），
    /// 只有点名调用它的那一处才走得到，而那一处已经在 <c>QUICKSQL_TEST_PROVIDER=sqlite</c> 这条显式通路里。
    /// 路径落在 <see cref="Path.GetTempPath"/> 而不是仓库里：本层的产物不该出现在
    /// <c>git status</c> 的任何一档（<c>bin/</c> 与 <c>obj/</c> 之外没有可藏的目录）。</summary>
    public static string ScratchConnectionString(string name)
        => "Data Source=" + Path.Combine(Path.GetTempPath(), "quicksql-sqlite-" + name + ".db");
}
