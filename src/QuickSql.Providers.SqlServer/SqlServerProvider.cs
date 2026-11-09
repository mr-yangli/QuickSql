using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.SqlServer;

/// <summary>SQL Server 的驱动槽位。构造期不反射：DbProviderFactory 由 Core 的 DriverRegistry 按
/// 内建表解析后注入，本程序集编译期不认识 Microsoft.Data.SqlClient。七个槽位共用同一批实例：
/// Expressions.Register 覆写必须同时对翻译器和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</summary>
[QuickSqlProvider(DbKind.SqlServer)]
public sealed class SqlServerProvider : IDbProvider
{
    /// <summary>内建表里的 key。assembly 通常是 Microsoft.Data.SqlClient。</summary>
    public const string DriverKey = "sqlserver";

    readonly SqlServerTypes _types = SqlServerTypes.Instance;
    readonly SqlServerValues _values = SqlServerValues.Instance;
    readonly SqlServerExpressions _expressions = new();
    readonly SqlServerKeys _keys = new();
    readonly SqlServerDialect _dialect;

    public SqlServerProvider(DbProviderFactory driverFactory)
    {
        Connectors = new SqlServerConnectorFactory(driverFactory);
        CodeFirst = new SqlServerCodeFirst(_types);
        _dialect = new SqlServerDialect(_types, _keys, _values, _expressions);

        // 批量策略要认识工厂住的那个程序集：装载类（SqlBulkCopy）不在 DbProviderFactory 的抽象面上，
        // 只能从同一支驱动程序集里按名字取——现场同时装两家时，跟着连接所属的那一家走才是唯一可能对的答案。
        // 这里交的是工厂而不是程序集名，所以构造期仍然一次反射都没有。
        Bulk = new SqlServerBulk(driverFactory);
    }

    public DbKind Kind => DbKind.SqlServer;

    /// <summary>有 OUTPUT、有 MERGE、有原生 BIT 与 uniqueidentifier；没有 SKIP LOCKED
    /// （T-SQL 只有 READPAST 提示，语义不等价），也没有 Truncate_Cascade。别为了让代码跑得过去而往上加位。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_OffsetFetch
        | DbCapabilities.Returning
        | DbCapabilities.Upsert_MergeInto
        | DbCapabilities.Boolean_Type
        | DbCapabilities.Guid_Raw16
        | DbCapabilities.Identity_ExplicitInsert
        | DbCapabilities.Schemas
        | DbCapabilities.Cte
        | DbCapabilities.WindowFunctions
        | DbCapabilities.Bulk_Copy
        | DbCapabilities.MultiStatement
        | DbCapabilities.MultiResultSets
        | DbCapabilities.Count_Over_Window
        | DbCapabilities.Comment_On_Column
        | DbCapabilities.Alter_Column_Precision
        | DbCapabilities.TempTable
        | DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new SqlServerSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new SqlServerConcurrency();

    /// <summary>SQL Server 的完整性码表。这一家的 SqlState 是空的（SqlException.SqlState 放的是
    /// ERROR_STATE() 而不是 SQLSTATE，实测直接 null），号在基类的 Data 字典里
    /// （HelpLink.EvtID / HelpLink.EvtSrc = MSSQLServer），所以"通用层读 SqlState 就通吃"在这一家不成立，
    /// 但仍然不需要反射调驱动成员。547 故意不分类：那一颗号同时覆盖外键冲突、检查约束与命名唯一约束，
    /// 分开只能读文案，而误分类比不分类坏——把外键冲突说成"重复提交"，用户会照着提示再点一次
    /// 而库里根本没有那一行，所以 547 交回 null（驱动原样出洞）。</summary>
    public IntegrityViolation? ClassifyIntegrityError(System.Data.Common.DbException error)
    {
        // EvtSrc 那一格不是装饰：这根字典槽位是 ADO.NET 给"帮助链接"用的，别家驱动也可能填，
        // 所以按它点名的来源收口，认错了家就什么都别翻。
        if (
            !string.Equals(
                error.Data["HelpLink.EvtSrc"] as string,
                "MSSQLServer",
                StringComparison.Ordinal
            )
        )
            return null;

        return error.Data["HelpLink.EvtID"] switch
        {
            "2601" or "2627" => new IntegrityViolation(IntegrityKind.Unique),
            "515" => new IntegrityViolation(IntegrityKind.NotNull),
            _ => null,
        };
    }

    public IChangeGuard ChangeGuard { get; } = new SqlServerChangeGuard();

    /// <summary>L1 有通道，2100 的参数预算从第一天就生效。通道是懒出来的：只产 SQL 的离线路径不会碰那次类型查找。</summary>
    public IBulkStrategy? Bulk { get; }

    public bool CanCreateDatabase => true;

    /// <summary>这一家认的库名键，三颗都列：SqlClient 认 Database、Initial Catalog 与不带空格的同一种写法，
    /// 而裸 DbConnectionStringBuilder 不做同义词归一（实测只按字面键比，仅忽略大小写），
    /// 候选表少列一颗就静默少读一颗。</summary>
    static readonly ConnectionStringCatalog Catalog = new(
        "Database",
        "Initial Catalog",
        "InitialCatalog"
    );

    /// <summary>连到 master 库，查 sys.databases；不存在则 CREATE DATABASE。
    /// 目标库名按 Catalog 读，读不到就按原样跳过建库。</summary>
    public async Task EnsureDatabaseExistsAsync(
        string connectionString,
        CancellationToken ct = default
    )
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        var catalog = Catalog.Read(builder);

        if (catalog is null)
            return;

        // 几颗同义词一起摘、改完只留一份：留两颗互相矛盾的键等于把"哪一颗说了算"交给解析顺序。
        Catalog.Clear(builder);
        builder["Initial Catalog"] = "master";

        await using var connection = Connectors.CreateConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var checkCmd = Connectors.CreateCommand(connection);
        checkCmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        var p = checkCmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = catalog;
        checkCmd.Parameters.Add(p);
        var exists =
            Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        if (exists)
            return;

        await using var createCmd = Connectors.CreateCommand(connection);
        createCmd.CommandText = $"CREATE DATABASE [{catalog.Replace("]", "]]")}]";
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
