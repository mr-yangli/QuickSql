namespace QuickSql.DriverLoading;

/// <summary>驱动默认表：程序集名与工厂类型名的唯一来源。每颗 key 与 DbKind 的 ToString().ToLowerInvariant()
/// 逐字相同，所以不需要映射表。provider 侧走编译期登记，驱动侧在运行期按程序集名装载
/// （DriverRegistry 那条 Assembly.Load），provider 程序集编译期不引用任何驱动。</summary>
public static class BuiltinDrivers
{
    static readonly DriverRef[] _refs =
    [
        // factoryType 照厂商文档写全名：这一颗有两台活库实测过，全名不是猜的。
        new("sqlserver", "Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlClientFactory"),

        // PG 本尊：Npgsql 是这一族线协议自己实现的驱动，名字不借不称。刻意不写 factoryType——
        // 省略它走「扫导出类型里第一个 DbProviderFactory 子类」兜底；已在真库解析成功并跑完整本集成册。
        new("postgresql", "Npgsql", null),

        // 金仓 V8：两颗名字都按厂商文档写、未实测。factoryType 照文档写全名，而现场装不上时
        // 更稳的姿势是把 DLL 丢进 drivers/ 让它走兜底扫描——猜错全名会把本来能成的解析变成"找不到类型"。
        new("kingbase", "Kdbndp", "Kdbndp.KdbndpFactory"),

        // openGauss：官方分发的是 Npgsql 的 fork，托管程序集名没有厂商读数，不猜名字（达梦行立过的纪律）。
        // 从未解析成功过、刻意不写 factoryType；现场真装上那天，第一件事是按厂商读数改回它自己的程序集名。
        new("opengauss", "Npgsql", null),

        // GaussDB：它自己分发的托管驱动叫什么，本仓库没有任何可引读数，本行不替它引申。
        // 处境与 openGauss 同：未解析、未实测、无本机实例。
        new("gaussdb", "Npgsql", null),

        // 瀚高 HighGo：线协议就是 PG 的，Npgsql 是血统协议自己的驱动，不借邻家的臂。未解析、未实测、无本机实例。
        new("highgo", "Npgsql", null),

        // GBase 8c：与瀚高共享同一颗来路原词（厂商文档「兼容 PG」）；自己分发的托管驱动叫什么没有可引读数，不猜名字。未解析、未实测。
        new("gbase8c", "Npgsql", null),

        // Oracle：assembly 名的来路是本仓库自家注释里的一句话，现场不能拿它当厂商读数用。
        // 刻意不写 factoryType，且它连一颗有出处的工厂类名都没有。NuGet 侧 Oracle.ManagedDataAccess.Core
        // 实测程序集名与本行逐字相同。未解析、未实测、无本机实例。
        new("oracle", "Oracle.ManagedDataAccess", null),

        // 达梦：只有 assembly 名按厂商文档写，从未解析成功过；刻意不写 factoryType——按文档猜全名
        // 反而会把本来能成的解析变成"找不到类型"。NuGet 的 DmProvider 2.0.0 在 net10.0 上是实测死路
        // （包内只有 lib/Dm.dll，程序集名对不上），正解是厂商 .NET 版驱动丢进 <输出目录>/drivers/。
        new("dameng", "DmProvider", null),

        // 神通 Oscar：assembly 名的来路是厂商文档口径的一个「兼容」词，且它连"是不是程序集名"都只有那一列支持。
        // 未解析、刻意不写 factoryType、未实测、无本机实例。
        new("shentong", "Oscar", null),

        // MySQL 血统：assembly 写 MySqlConnector（社区驱动；官方分发的是 MySQL.Data，两条不同源）。
        // 已解析成功并跑完整本集成册，是全表第一颗靠"扫导出类型"兜底真跑通的键；现场读到的是
        // MySQL Community Server 8.4.8，与厂商那份 MySQL.Data 的差别没有任何读数。
        new("mysql", "MySqlConnector", null),

        // TiDB：驱动按协议族装、库种按方言假设装，解开的那一格由 provider 自己站死。mysql 行的实测
        // 推不到本行——同一份 assembly 在两台库种之间可迁移这件事只证到了 mysql 自己那一台。未实测。
        new("tidb", "MySqlConnector", null),

        // OceanBase（MySQL 模）：这一家是双模、模式在连接上决定，本行 assembly 只在 MySQL 模成立——
        // Oracle 模该走的是另一颗今天不存在的键（缺口记档）。mysql 行的实测推不到这一家自己的 MySQL 模实现。未实测。
        new("oceanbase", "MySqlConnector", null),

        // 虚谷：assembly 与厂商文档给的驱动包名逐字同名已证，工厂解析到 XuguClient.XGProviderFactory
        // （实测是 public static 字段且就是工厂本身），六格摆法五格走到"打开+只读查询成功"。
        // 4.0.0.8 是纯托管的一颗，不需要原生同伴；连接串里 User ID 不再被认（要 USER 或 UID），
        // Server / Port / Database / Password / ssl_str 照旧——4.0 的 ValidateRequiredKeys 收窄了键名。
        // 口令只走环境变量，不落脚本与日志。
        new("xugu", "XuguClient", null),

        // GBase 8s：GBase.DataDB 的全部来路是规格矩阵一行第四列；没有厂商文档、没有 shipped 适配器、
        // 没有活库，也没有可借的臂。provider 层每格标【文档·IBM】而不是【文档·厂商】——
        // "Informix 的即 GBase 8s 的"是一颗借来的前提。未解析、刻意不写 factoryType、未实测。
        new("gbase8s", "GBase.DataDB", null),

        // SQLite：factoryType 是从活机器上读回来的（restore 到手 FullName = Microsoft.Data.Sqlite，
        // 导出类型里恰好一颗 DbProviderFactory 子类）。也是第一颗不需要任何现场摆法的非 T-SQL 行：
        // 不要凭据、一个文件就是一座库——这是访问面的差别而不是纪律的差别，不给任何一家开先例。
        new("sqlite", "Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.SqliteFactory"),

        // DuckDB：内嵌单文件引擎这一档的第二颗键——"库"就是连接串里那颗文件，不要凭据、
        // 也没有"建库"这条路。assembly 名按厂商文档口径写（DuckDB.NET.Data，官方 ADO.NET 驱动），
        // factoryType 刻意不写走兜底扫描；此名从未在任何机器上解析成功过，整行未实测。
        new("duckdb", "DuckDB.NET.Data", null),

        // ClickHouse：单层 provider（与 Sqlite/DuckDb 同档，无血统基类、无叶子），内建表第一次收一个
        // "客户端/服务器引擎"档的【文档口径行】。assembly 名按厂商文档口径写（ClickHouse.Client），
        // factoryType 刻意不写走兜底扫描；此名从未在任何机器上解析成功过，整行未实测。
        new("clickhouse", "ClickHouse.Client", null),

        // TDengine：assembly 名是 TDengine 而不是包名 TDengine.Connector——官方源仓的
        // src/TDengine.csproj 没设 AssemblyName，SDK 默认取项目文件名（这一格是本行最贵的坑）。
        // TDengineFactory 没有静态 Instance 字段，factoryType 刻意不写走兜底扫描
        //（程序集里 DbProviderFactory 子类恰好一颗）；连接串 protocol=websocket（纯托管，
        // taosAdapter 默认 6041）不落 taos.dll，native 档才要装客户端库。整行未实测。
        new("tdengine", "TDengine", null),
    ];

    public static IReadOnlyList<DriverRef> Drivers => _refs;

    public static DriverRef? Find(string key)
    {
        foreach (var reference in _refs)
            if (string.Equals(reference.Key, key, StringComparison.OrdinalIgnoreCase))
                return reference;

        return null;
    }
}
