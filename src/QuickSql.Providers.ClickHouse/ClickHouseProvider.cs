using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的驱动槽位（单层 provider：没有血统基类、没有叶子，没有一颗可覆写的父类）。
/// 构造期不反射：DbProviderFactory 由 Core 的 DriverRegistry 按 BuiltinDrivers 解析后注入；
/// 各槽位共用同一批实例——Expressions.Register 的覆写必须同时对翻译器和方言生效，
/// 各 new 一份就会出现"注册了却不改行为"的幽灵 bug。能力位声明 7 位且逐位有产出对应；
/// 刻意不声明的每一位都要理由：Transactions 取"没有"（内核写路径不开事务、逐句按库的 autocommit 直发，
/// 公开 BeginTransaction 路径按位缺位当场抛——严禁静默假事务）；Returning/Upsert_*/Sequences 是
/// "官方语句形状没读到"，宁可当场红也不发没钉过的一句；Bulk 交 null，批量从 L3 起；其余位没有按位分派的发射口。
/// 整层是厂商文档口径、未实测台账——重开条件只有一条：拿到一台可跑的引擎。</summary>
[QuickSqlProvider(DbKind.ClickHouse)]
public sealed class ClickHouseProvider : IDbProvider
{
    /// <summary>内建表里的 key。名字与 DbKind.ClickHouse 的 ToString().ToLowerInvariant() 一致，
    /// 所以 key 那一颗不需要额外映射表——"名字就是自己"。</summary>
    public const string DriverKey = "clickhouse";

    readonly ClickHouseTypes _types = ClickHouseTypes.Instance;
    readonly ClickHouseValues _values = ClickHouseValues.Instance;
    readonly ClickHouseExpressions _expressions = new();
    readonly ClickHouseKeys _keys = new();
    readonly ClickHouseDialect _dialect;

    public ClickHouseProvider(DbProviderFactory driverFactory)
    {
        Connectors = new ClickHouseConnectorFactory(driverFactory);
        CodeFirst = new ClickHouseCodeFirst(_types);
        _dialect = new ClickHouseDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.ClickHouse;

    /// <summary>位掩码本体，七位各自的产出文本见各槽位类。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Boolean_Type | DbCapabilities.Guid_Raw16 |
        DbCapabilities.Schemas | DbCapabilities.Alter_Column_Precision | DbCapabilities.CaseSensitive_Identifiers |
        DbCapabilities.Comment_On_Column;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new ClickHouseSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new ClickHouseConcurrency();

    public IChangeGuard ChangeGuard { get; } = new ClickHouseChangeGuard();

    /// <summary>没有批量策略：没有装载通道的读数，批量退回链自然从 L3 起。</summary>
    public IBulkStrategy? Bulk => null;

    /// <summary>本家的"建库"要走 CREATE DATABASE 语句本身，而连接串里那颗 database 当场就要在场——
    /// 没有"连一个不带库名的锚点库再补建"的路可走，所以显式交 false。EnsureDatabaseExistsAsync 不覆写：
    /// 谁绕过这一颗 false 来问，都在接口默认的 NotSupportedException 上停。</summary>
    public bool CanCreateDatabase => false;
}
