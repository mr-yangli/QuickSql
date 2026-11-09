using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的驱动槽位。金仓/openGauss/GaussDB/瀚高/GBase 8c/PostgreSQL 共享这一份，各自只覆写真正分岔的那几处。
/// 构造期不反射：DbProviderFactory 由 Core 的 DriverRegistry 解析后注入，本程序集编译期不认识 Npgsql 也不认识 Kdbndp。
/// 各槽位共用同一批实例：Expressions.Register 的覆写必须同时对翻译器和方言生效，各 new 一份会出现"注册了却不改行为"的幽灵 bug。
/// 构造里开了两道虚口 CreateCodeFirst/CreateDialect：叶子要换 DDL 或文本层在这一层覆写，
/// 覆写里只许碰静态成员与基类已初始化的字段，共享的 keys/values/expressions 作为参数递过去。</summary>
public abstract class PostgreSqlProviderBase : IDbProvider
{
    readonly PostgreSqlTypes _types = PostgreSqlTypes.Instance;
    readonly PostgreSqlValues _values = PostgreSqlValues.Instance;
    readonly PostgreSqlExpressions _expressions = new();
    readonly PostgreSqlKeys _keys = new();
    readonly PostgreSqlDialect _dialect;

    protected PostgreSqlProviderBase(DbKind kind, DbProviderFactory driverFactory)
    {
        Kind = kind;
        Connectors = new PostgreSqlConnectorFactory(driverFactory);
        // 两道虚口从"直 new"改成覆写点（先例是 Oracle 那一族）：覆写里只许碰静态成员与基类已初始化的字段，
        // 各槽位仍然共用同一批实例。
        CodeFirst = CreateCodeFirst(_types);
        _dialect = CreateDialect(kind, _types, _keys, _values, _expressions);

        // 与 SqlServerProvider / MySqlProviderBase 同一条接线：装载类（导入器）不在工厂的抽象面上，
        // 只能从同一支驱动程序集里按类型名与形状找；构造期仍然一次反射都没有。
        Bulk = new PostgreSqlBulk(driverFactory);
    }

    /// <summary>血统的第一道缝：DDL 缺省交给 PostgreSqlCodeFirst。叶子要换 DDL 文本在这一层覆写，
    /// 而不是在构造里拼一颗新的。types 缺省时就是血统自己那份类型表；参数可被覆写方忽略。</summary>
    protected virtual ICodeFirstDialect CreateCodeFirst(PostgreSqlTypes types) =>
        new PostgreSqlCodeFirst(types);

    /// <summary>血统的第二道缝：文本层缺省交给 PostgreSqlDialect。它存在的理由是方言构造的六个参数里
    /// 有四个是本类私有的共享实例（keys/values/expressions），叶子没有别的入口能拿到它们。</summary>
    protected virtual PostgreSqlDialect CreateDialect(
        DbKind kind,
        PostgreSqlTypes types,
        PostgreSqlKeys keys,
        PostgreSqlValues values,
        PostgreSqlExpressions expressions
    ) => new(kind, types, keys, values, expressions);

    public DbKind Kind { get; }

    /// <summary>子类要改的是加或减这一档，而不是另起一份。</summary>
    public virtual DbCapabilities Capabilities => FamilyCapabilities;

    /// <summary>这一档的位掩码本体。Bulk_Copy 加在族档上、六颗 PG 血统的键一起领——判据是"代码今天真的走到"，
    /// 而反射装配这一格还没有集成册读数（活 PG 实例在场而 L1 路径未跑过，按"已接线、未见证"读）；
    /// 各叶子现场的驱动里到底有没有导入器，是 TryResolve 的当场答案不是掩码的承诺。</summary>
    protected static DbCapabilities FamilyCapabilities =>
        DbCapabilities.Paging_LimitOffset
        | DbCapabilities.Returning
        | DbCapabilities.Upsert_OnConflict
        | DbCapabilities.Cte
        | DbCapabilities.WindowFunctions
        | DbCapabilities.Count_Over_Window
        | DbCapabilities.Boolean_Type
        | DbCapabilities.Guid_Raw16
        | DbCapabilities.Schemas
        | DbCapabilities.CaseSensitive_Identifiers
        | DbCapabilities.Comment_On_Column
        | DbCapabilities.Alter_Column_Precision
        | DbCapabilities.TempTable
        | DbCapabilities.Bulk_Copy
        | DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    /// <summary>血统的类型表缺省是 PostgreSqlTypes 那一份，开成虚口：只换方言不够——表达式翻译里 CAST 走的
    /// 就是 provider.Types.ApplyStorageDefaults，叶子只覆写方言的话 TIMESTAMP(3)/DOUBLE PRECISION
    /// 这类血统类型名会从 CAST 一路漏进叶子产出的 SQL。</summary>
    public virtual ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new PostgreSqlSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new PostgreSqlConcurrency();

    /// <summary>PG 血统的完整性码表。每一个数都取自一台可达 PG 上的实测：pg_temp 夹具五种违例各撞一次，
    /// 取值全程只碰 DbException 基类面（SqlState 与 Data["ConstraintName"]），一个驱动类型都没引用。
    /// 三条实测口径：非空那一档库不给约束名（本来就没有命名约束，给的是列名与表名）；
    /// 外键的插入侧与删除侧同报 23503，分岔只在文案；DETAIL 在默认连接串上被驱动整段涂掉
    /// （"Specify 'Include Error Detail'"）——"从文案里取键列"在生产连接串上根本没有材料。
    /// 各叶子跟着这一份走依据的是自己声明继承本家，不等于验过；真库跑错就在叶子上覆写这一格（所以是 virtual）。</summary>
    public virtual IntegrityViolation? ClassifyIntegrityError(DbException error) =>
        error.SqlState switch
        {
            "23505" => new IntegrityViolation(IntegrityKind.Unique, PgConstraintName(error)),
            "23502" => new IntegrityViolation(IntegrityKind.NotNull),
            // 23001 是 ON DELETE RESTRICT 那一档的外键违例（restrict_violation），与 23503 同一处置。
            "23503" or "23001" => new IntegrityViolation(
                IntegrityKind.ForeignKey,
                PgConstraintName(error)
            ),
            "23514" => new IntegrityViolation(IntegrityKind.Check, PgConstraintName(error)),
            _ => null,
        };

    /// <summary>PG 把约束名放在 ADO.NET 的 Data 字典里（实测键名就是 ConstraintName），
    /// 所以这一格不需要驱动类型。非空那一档没有这个键，取到 null 是正常形状而不是失败。</summary>
    static string? PgConstraintName(DbException error) => error.Data["ConstraintName"] as string;

    public IChangeGuard ChangeGuard { get; } = new PostgreSqlChangeGuard();

    /// <summary>PG 血统的批量策略：L1 二进制 COPY 逐行推入面（反射绑定）。位与通道必须分开读：
    /// 位说的是这份代码走到了，叶子现场装的那支驱动里有没有装载类由 TryResolve 当场答——
    /// 借臂的叶子装的是没有导入器的 fork 时整家退 L3，那一种现场这颗位报的是"代码可达而这一支驱动没有"。
    /// 预算从第一天就要生效，否则分块公式会按 T-SQL 的 2100 去切一条这一家收得下的语句（L1 不吃预算，管的是退回文本层之后）。</summary>
    public IBulkStrategy? Bulk { get; }

    /// <summary>血统的值原样保留 true，开成虚口：叶子哪天要改"建库"的表现（如内嵌单文件无服务端的那一档），
    /// 覆写点在这里。</summary>
    public virtual bool CanCreateDatabase => true;

    /// <summary>这一家认的库名键。按 Npgsql 文档列 Database/dbname/db 三颗——没有驱动可测，
    /// 只列到文档写到的那一格，别当成实测读数。</summary>
    static readonly ConnectionStringCatalog Catalog = new("Database", "dbname", "db");

    /// <summary>PG 系没有 CREATE DATABASE IF NOT EXISTS，先查 pg_database 再建：连到 postgres 库
    /// （每台服务器都有的默认库）执行检查与建库语句。目标库名从连接串按同义词读（Catalog），
    /// 与 MySQL 血统那格同一个坑：读不到就走"没库名、不建库"那一岔是静默的，候选键要一颗颗列全。</summary>
    public virtual async Task EnsureDatabaseExistsAsync(
        string connectionString,
        CancellationToken ct = default
    )
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        var dbName = Catalog.Read(builder);

        if (dbName is null)
            return;

        // 先把候选键全摘干净再写锚点库：只覆盖 Database 一颗的话，串里会同时留着
        // Database=postgres 与 dbname=原库，谁说了算交给驱动的解析顺序。
        Catalog.Clear(builder);

        builder["Database"] = "postgres";

        await using var connection = Connectors.CreateConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var checkCmd = Connectors.CreateCommand(connection);
        checkCmd.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @name";
        var p = checkCmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = dbName;
        checkCmd.Parameters.Add(p);
        var exists =
            Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        if (exists)
            return;

        await using var createCmd = Connectors.CreateCommand(connection);
        createCmd.CommandText = $"CREATE DATABASE \"{dbName.Replace("\"", "\"\"")}\"";
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
