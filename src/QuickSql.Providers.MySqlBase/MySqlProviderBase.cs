using System.Data.Common;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的驱动槽位。MySQL 本尊与 TiDB、OceanBase 两颗分叉叶子共享这一份，叶子今天都零覆写；
/// 构造期不反射：DbProviderFactory 由 Core 的 DriverRegistry 解析后注入，本程序集编译期不认识 MySqlConnector。
/// 各槽位共用同一批实例：Expressions.Register 的覆写必须同时对翻译器和方言生效，
/// 各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</summary>
public abstract class MySqlProviderBase : IDbProvider
{
    readonly MySqlTypes _types = MySqlTypes.Instance;
    readonly MySqlValues _values = MySqlValues.Instance;
    readonly MySqlExpressions _expressions = new();
    readonly MySqlKeys _keys = new();
    readonly MySqlDialect _dialect;

    protected MySqlProviderBase(DbKind kind, DbProviderFactory driverFactory)
    {
        Kind = kind;
        Connectors = new MySqlConnectorFactory(driverFactory);
        CodeFirst = new MySqlCodeFirst(_types);
        _dialect = new MySqlDialect(kind, _types, _keys, _values, _expressions);

        // 与 SqlServerProvider 同一条接线：装载类（MySqlBulkCopy）不在工厂的抽象面上，只能从同一支驱动程序集里
        // 按名字取，所以这里交工厂而不是交名字；构造期仍然一次反射都没有（见 MySqlBulk 的抬头）。
        Bulk = new MySqlBulk(driverFactory);
    }

    public DbKind Kind { get; }

    /// <summary>子类要改的是加或减这一档，而不是另起一份——另起一份就等于把能力继承链作废。</summary>
    public virtual DbCapabilities Capabilities => FamilyCapabilities;

    /// <summary>这一档的位掩码本体：6 位 = 5 位离线文本 + 1 位装载通道（Bulk_Copy 量的是驱动 API，
    /// provider 里那条反射装配未跑过集成册）。加在族档上、三颗叶子一起领——量具只证到 MySQL 本尊那一台，
    /// 另两家的 LOAD DATA LOCAL 是否等价未实测。"少"是这一档的姿态：加减只在方言文本真的分岔时发生，
    /// 这一族到今天没为任何一颗分叉叶子改过掩码。</summary>
    protected static DbCapabilities FamilyCapabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Schemas | DbCapabilities.Guid_Char36 |
        DbCapabilities.Comment_On_Column | DbCapabilities.Alter_Column_Precision | DbCapabilities.Bulk_Copy |
        DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new MySqlSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new MySqlConcurrency();

    /// <summary>MySQL 血统的完整性码表。它填 SQLSTATE，但唯一/非空/外键/检查全挤在 23000 一个值里，
    /// 所以 SqlState 只能当"是不是完整性违例"的门，分档必须看厂商自己的号——号在基类 Data 字典里
    /// （实测键名 Server Error Code，同字典还带 SqlState），判据只站 ADO.NET 基类面。
    /// 先认 SqlState==23000 再认号：那根槽位不是完整性专用的，不点名来源就照号翻，
    /// 等于把别家填进来的号猜成 MySQL 的。1062（重复键）实测于本机；1048、1452/1216/1217、3819
    /// 是厂商文档的号，在这台上还没撞过。约束名不在结构化字段里（写在消息 for key '表.索引名' 半句里），
    /// 交 null 走"按名字回契约查"的路。TiDB/OceanBase 线协议一致、号沿用 MySQL，
    /// 但"沿用"不是判据——各叶子在真库上撞过一次之前，这一格对它们算推断。</summary>
    public virtual IntegrityViolation? ClassifyIntegrityError(DbException error)
    {
        if (!string.Equals(error.SqlState, "23000", StringComparison.OrdinalIgnoreCase))
            return null;

        return error.Data["Server Error Code"]?.ToString() switch
        {
            "1062" => new IntegrityViolation(IntegrityKind.Unique),
            "1048" => new IntegrityViolation(IntegrityKind.NotNull),
            "1452" or "1216" or "1217" => new IntegrityViolation(IntegrityKind.ForeignKey),
            "3819" => new IntegrityViolation(IntegrityKind.Check),
            _ => null,
        };
    }

    public IChangeGuard ChangeGuard { get; } = new MySqlChangeGuard();

    /// <summary>MySQL 血统的批量策略：L1 装载通道（LOAD DATA LOCAL）+ 预算 65535。
    /// 缺 AllowLoadLocalInfile=true 时 Auto 落在 L3 并把那句话写进 Reason（落在 L3 不算降级，
    /// BulkDegraded 那一位不派）；与第三家交回 null 走 100 颗兜底不同，这一档有协议级数字可报。</summary>
    public IBulkStrategy? Bulk { get; }

    public bool CanCreateDatabase => true;

    /// <summary>这一家认的库名键。实测（MySqlConnector 2.6.0）：db、DB、init catalog、InitialCatalog、
    /// initial_catalog、双空格写法一律按「未知选项」抛，只有 Database 与 Initial Catalog 能用——
    /// 列多出来的那颗不会错，但它是一次永远命不中的比。</summary>
    static readonly ConnectionStringCatalog Catalog = new("Database", "Initial Catalog");

    /// <summary>支持 CREATE DATABASE IF NOT EXISTS，一条语句搞定。目标库名从连接串读（Catalog），
    /// 连库时把这些键全部摘掉（不指定库也能连上 MySQL 服务器）。
    /// 读不到库名就走"串里没库名就不建库"那一岔——静默跳过，症状推迟到调用方连那座不存在的库时才现形。</summary>
    public async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        var dbName = Catalog.Read(builder);

        if (dbName is null) return;

        // 两颗键一起摘：驱动自己会同义词归一，留一颗等于把「哪一颗说了算」交给解析顺序
        // （实测 Database=AAA;Initial Catalog=BBB 两颗并存时驱动不抛，取后出现的那颗）。
        Catalog.Clear(builder);

        await using var connection = Connectors.CreateConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = Connectors.CreateCommand(connection);
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{dbName.Replace("`", "``")}`";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
