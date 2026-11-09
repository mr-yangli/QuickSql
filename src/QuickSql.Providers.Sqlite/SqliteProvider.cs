using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的驱动槽位。这一族没有血统基类也没有叶子：单层 provider、类 sealed、
/// 十一颗子 SPI 各一份实现；构造期不反射，DbProviderFactory 由 Core 的 DriverRegistry 按内建表
/// 解析后注入，本程序集编译期不认识 Microsoft.Data.Sqlite（restore 即得、SqliteFactory 实名可写）。
/// 各槽位共用同一批实例：Expressions.Register 的覆写必须同时对翻译器和方言生效，
/// 各 new 一份就会出现"注册了却不改行为"的幽灵 bug。能力位声明五位，每一位后面跟的是活库
/// 当场交回的东西；刻意不声明的各档逐条给理由（库没有对象，或代码没有发射口）。
/// 最后一句要说在本家：provider 不报引擎版本门槛——RETURNING/DROP COLUMN 要 3.35+、
/// IS DISTINCT FROM 要 3.39+，而这一族的部署可以差得很远（单文件内嵌、编译开关、是否带 ICU），
/// 换一台老引擎时这些文本会当场报语法错而 provider 不会预告。</summary>
[QuickSqlProvider(DbKind.Sqlite)]
public sealed class SqliteProvider : IDbProvider
{
    /// <summary>内建表里的 key：assembly 写 Microsoft.Data.Sqlite、factoryType 写
    /// Microsoft.Data.Sqlite.SqliteFactory——后一颗名字是现场读到的实名，本行第一次把它写进内建表。
    /// 名字与 DbKind.Sqlite 的 ToString().ToLowerInvariant() 一致，所以 key 不需要额外映射表。</summary>
    public const string DriverKey = "sqlite";

    readonly SqliteTypes _types = SqliteTypes.Instance;
    readonly SqliteValues _values = SqliteValues.Instance;
    readonly SqliteExpressions _expressions = new();
    readonly SqliteKeys _keys = new();
    readonly SqliteDialect _dialect;

    public SqliteProvider(DbProviderFactory driverFactory)
    {
        Connectors = new SqliteConnectorFactory(driverFactory);
        CodeFirst = new SqliteCodeFirst(_types);
        _dialect = new SqliteDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.Sqlite;

    /// <summary>位掩码本体，五位；不声明的各档逐条理由在类注释里。
    /// 它不是 virtual 而是一句直给：这一族今天没有叶子，而"不留一颗没人覆写的虚函数"这条纪律已经立过四次。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_LimitOffset
        | DbCapabilities.Returning
        | DbCapabilities.Identity_ExplicitInsert
        | DbCapabilities.Schemas
        | DbCapabilities.MultiStatement
        | DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new SqliteSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new SqliteConcurrency();

    /// <summary>SQLite 的完整性判据，也是首批四家里唯一一颗读文案的：这一家在 ADO.NET 的基类面上
    /// 什么都给不出来（SqlState 是 null、Data 是空字典、ErrorCode 是 HRESULT），真正那两颗号
    /// （SqliteErrorCode 19 与 SqliteExtendedErrorCode 2067）只挂在驱动专有成员上，而"加载可以反射、
    /// 调用不行"够不着。所以只认 "UNIQUE constraint failed" 这一句已实测的固定文案，其余三档
    /// （NOT NULL / FOREIGN KEY / CHECK）交回 null——文案比号更脆，一次只押一句已实测的；
    /// 好处是这一句带着列名清单，是四家里唯一不需要回契约就能报出坏在哪几列的形态。
    /// 用 Contains 而不是 StartsWith：整句开头是 "SQLite Error 19:" 那颗号的壳，
    /// "是不是唯一违例"这件事只由那半句决定。</summary>
    public IntegrityViolation? ClassifyIntegrityError(DbException error) =>
        error.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            ? new IntegrityViolation(IntegrityKind.Unique)
            : null;

    public IChangeGuard ChangeGuard { get; } = new SqliteChangeGuard();

    /// <summary>没有批量策略，与 XuguProvider、Gbase8sProvider 同档同因：那颗驱动给不出一个可报的
    /// 高速通道（没有装载类形状的 API，它的高速面是"一条事务包住一批语句"，那是执行编排不是装载通道），
    /// 也给不出一个可报的参数预算。Merged 档不接：驱动"看起来"收得下分号批，但"看起来收得下"
    /// 与"一条批被它逐句执行、行数按句回报"之间差的是一场没跑过的读数——集成册补一场多语句批即可当场改判。</summary>
    public IBulkStrategy? Bulk => null;

    /// <summary>SQLite 打开连接即创建文件，所以支持自动建库。</summary>
    public bool CanCreateDatabase => true;

    /// <summary>打开再关闭一次连接：文件不存在时驱动会自动创建，已存在时是空操作。
    /// 不建表、不写任何内容——那是 SchemaSyncer 的职责。</summary>
    public async Task EnsureDatabaseExistsAsync(
        string connectionString,
        CancellationToken ct = default
    )
    {
        await using var connection = Connectors.CreateConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
    }
}
