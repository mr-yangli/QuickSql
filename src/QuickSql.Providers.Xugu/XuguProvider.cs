using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的驱动槽位。单层 provider：没有血统基类也没有叶子。构造期不反射——DbProviderFactory 由 Core 的
/// DriverRegistry 按内建表解析后注入，本程序集编译期不认识 XuguClient；程序集名从未在一台机器上解析成功、
/// 连"官方发布名"都只有一档出处，所以内建表不写 factoryType，走"扫导出类型里第一个 DbProviderFactory 子类"的兜底
/// （厂商参考页至少写有 XGProviderFactory.Instance 这个工厂类名）。
/// 各槽位共用同一批实例：Expressions.Register 的覆写必须同时对翻译器和方言生效，各 new 一份会出现"注册了却不改行为"的幽灵 bug。
/// 能力位 7 位，每一位都指得出产出文本。刻意不声明的位各有理由：Boolean_Type——位只跟存储型走，
/// bool 落 TINYINT 期间永不声明（库虽有原生 BOOLEAN）；Identity_ExplicitInsert——有 IDENTITY_MODE 会话参数但
/// "改会话"的形状 SPI 接不住，Core 也没有调用路径；Upsert_MergeInto——语法在（产物发过）而 src 一行都没发过；
/// Json_Type——类型页有那一档而 Json 泛型没有发射口；其余位没有按位分派的发射口。MultiStatement/MultiResultSets
/// 不声明：多语句批没有产物演示过，建表尾部的分号串联只能走脚本路径。</summary>
[QuickSqlProvider(DbKind.Xugu)]
public sealed class XuguProvider : IDbProvider
{
    /// <summary>内建表里的 key；assembly 写 XuguClient（从未解析成功过）。名字与 DbKind.Xugu 的小写形一致，不需要额外映射表。</summary>
    public const string DriverKey = "xugu";

    readonly XuguTypes _types = XuguTypes.Instance;
    readonly XuguValues _values = XuguValues.Instance;
    readonly XuguExpressions _expressions = new();
    readonly XuguKeys _keys = new();
    readonly XuguDialect _dialect;

    public XuguProvider(DbProviderFactory driverFactory)
    {
        Connectors = new XuguConnectorFactory(driverFactory);
        CodeFirst = new XuguCodeFirst(_types);
        _dialect = new XuguDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.Xugu;

    /// <summary>位掩码本体，各位的产出文本见类注释（Transactions 是记账位：驱动侧本就走 DbTransaction 事务面）。
    /// 不是 virtual 而是一句直给：这一族没有叶子，不留一颗没人覆写的虚函数。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Returning | DbCapabilities.Schemas |
        DbCapabilities.Guid_Char36 | DbCapabilities.CaseSensitive_Identifiers |
        DbCapabilities.Comment_On_Column | DbCapabilities.Alter_Column_Precision |
        DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new XuguSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new XuguConcurrency();

    public IChangeGuard ChangeGuard { get; } = new XuguChangeGuard();

    /// <summary>没有批量策略：文档没给参数个数上限，64K 那一档是行的预算不是参数的颗数，两样都换不成一个可报的
    /// RowsPerStatement——所以报出去的是"没有高速通道"而不是"有但没接"，批量退回链自然从 L3 起。
    /// 重开条件：先有能读出"行数口径"的装载 API。</summary>
    public IBulkStrategy? Bulk => null;

    /// <summary>虚谷的建库模型与接口默认值不同（需要厂商工具或 DBA 操作），显式交 false。</summary>
    public bool CanCreateDatabase => false;
}
