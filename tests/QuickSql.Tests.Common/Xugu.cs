using System.Data.Common;
using QuickSql.DriverLoading;
using QuickSql.Providers.Xugu;

namespace QuickSql.TestInfra;

/// <summary>虚谷这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是第五档，而它立项那两版所依据的前提到本版已经换掉了，<em>原文照抄在这里不删</em>：
/// 「本机没有任何一台虚谷实例（§13.4 授权的只有两台 SQL Server），清单里那一行的程序集名 <c>XuguClient</c>
/// 也从未被解析成功过」—— 前一句 v0.31 翻（授权清单添了第三台，正是一台虚谷），后一句 v0.32 翻
/// （经 <c>DriverRegistry</c> 解析成功，六格摆法里五格各连上一次，出处 <c>obj/logs/v32-matrix-*-open.log</c>）。
/// <b>而"这里没有 <see cref="SqlServer.Driver"/> 的对应物"那一格也在同一版翻了</b>：原句是
/// "有了它就会被误读成这条解析路径验过"，中间一版换成的理由是"验它的是探针、不是这一层"，而 v0.32 第二格
/// 把集成层开了一条虚谷通路（<c>tests/QuickSql.Tests.Integration/Database.cs</c> 的家族缝），
/// 走的就是<em>这一层</em>产出的文本，所以这一颗组装根现在有身份了。<b>"有身份"要说清是哪一格的身份</b>：
/// 它验的是"清单里那一行能解析出工厂、工厂能连上"，<em>不</em>验"这一家已适配" —— 后者仍然要三句
/// （驱动名解析过但现场是不是交付入库的那一颗没核；本族文本自本版起有真库见证；连上过 ≠ 本族每一条文本跑过），
/// 跑到哪一格、哪几条红在 ~~<c>IKeyStrategy</c> 缺"语句之后回读"那一颗口子上~~（v0.34 第一笔把那颗口子
/// 建起来了，虚谷是两家 declared true 里唯一带真库出处的一家；这一句从"缺口子"改成"口子已在了，
/// 欠的只剩把集成层的整族跳过拆回去"），见 §13.5 与 §14 的本版账。</para>
///
/// <para><b>与另三家离线家族不同的一处：这一档没有"基类 + 叶子"两层，只有这一层</b>（§9.1 那份清单里没有第二种
/// 挂在虚谷底下的库种），所以这份组装根也只交回一个 <see cref="XuguProvider"/>，
/// 而没有 <c>MySqlDialectTests</c> 里那枚<em>空壳证据</em>用的血统基类探针 —— 没有第二层，就没有"两层产出相同"
/// 这句话可量。发布物那一侧同档的是 <c>QuickSql.Providers.SqlServer</c>，它也没有基类层。</para>
///
/// <para>连接串占位符仍然复用 <see cref="SqlServer.OfflineConnectionString">SqlServer 的那一份</see>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第五家不需要第五种假配置。</para></summary>
public static class Xugu
{
    public static XuguProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第五家上与前四家享有同一个探针。给了 <paramref name="factory"/>
    /// 却不给连接串时取 <see cref="TestConnectionString"/>，与 <see cref="SqlServer.Orm"/> 同一套三分岔
    /// （没配置就问出来，不退回某个"肯定连得上"的库）。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? (factory is null ? SqlServer.OfflineConnectionString : TestConnectionString),
        });

    /// <summary>走完整的运行期驱动解析链（内建表 <c>BuiltinDrivers</c> → 独立 <c>AssemblyLoadContext</c> → 工厂），与
    /// <see cref="SqlServer.Driver"/> 同一条路、同一条"不许直接 <c>new</c> 工厂"的理由：
    /// 那样会把要验的东西绕过去。这里验的东西具体是三件事，缺一件这一句就不算绿：
    /// 内建表里 <c>"xugu"</c> 那一行读得出、程序集名 <c>XuguClient</c> 解析得出、
    /// 交回来的工厂 <c>CreateConnection()</c> 连得上（<c>src/QuickSql.Providers.Xugu/Driver/</c> 是 v0.31 入库的
    /// 厂商产物，由 tests 的 csproj <c>Link</c> 进输出目录的 <c>drivers/</c>，探测名就是内建表那颗程序集名）。
    /// 出处：<c>obj/logs/v32-matrix-*-open.log</c>（六格摆法）与 <c>obj/logs/v32-recon-meta-2.log</c> 头三行
    /// （那一跑的 <c>FACTORY=</c> 一行印的就是这里交回的工厂类型）。</summary>
    public static DbProviderFactory Driver()
        => new DriverRegistry().Factory(XuguProvider.DriverKey);

    /// <summary>集成测试的第三台目标库：<b>同样没有默认值</b>。环境变量 <c>QUICKSQL_XUGU_CONNECTION</c> 优先，
    /// 其次仓库根的 <see cref="LocalConnectionFile"/>（第一行非 <c>#</c> 内容即连接串，含本机凭据、不入库，
    /// 已进 <c>.gitignore</c>）。两处都没有就抛，消息本身就是配置说明。
    ///
    /// <para><b>为什么不用 <see cref="SqlServer.TestConnectionString"/> 那颗变量名</b>：两台目标库共用
    /// <c>QUICKSQL_TEST_CONNECTION</c> 会让"换一台跑"变成"覆盖另一台的配置"，而 §13.4 要的是
    /// "同一批用例在两台/三台上各跑一遍"这种可并排的账 —— 串开的代价只是多一个名字，共用的代价是账对不上。
    /// <b>还有一层：这一家连的就是本仓库交付的那份厂商产物</b>（<c>Driver/</c> 目录），所以这一颗串配上了
    /// 也只说明"这台实例可达"，不说明"这台实例上跑的就是我们写的第五族"—— 后者由上面那颗
    /// <see cref="Driver"/> 与集成层那条家族缝一起证。</para></summary>
    public static string TestConnectionString
        => ResolveConnectionString() ?? throw new InvalidOperationException(
               $"没有配置虚谷集成测试目标库：设环境变量 QUICKSQL_XUGU_CONNECTION，或在仓库根放 {LocalConnectionFile}" +
               "（第一行非 # 内容即连接串）。该文件含本机凭据，不要提交。");

    const string LocalConnectionFile = "quicksql-test.xugu.connection.local.txt";

    static string? ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_XUGU_CONNECTION");

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
