using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的驱动槽位（单层 provider：没有血统基类、没有叶子，没有一颗可覆写的父类）。
/// 构造期不反射：DbProviderFactory 由 Core 的 DriverRegistry 按 BuiltinDrivers 解析后注入；
/// 各槽位共用同一批实例——Expressions.Register 的覆写必须同时对翻译器和方言生效，
/// 各 new 一份就会出现"注册了却不改行为"的幽灵 bug。能力位声明 4 位且逐位有产出对应；
/// 刻意不声明的每一位都要理由：Transactions 取"没有"（驱动 TDengineConnection.BeginDbTransaction
/// 当场 throw NotSupportedException，内核写路径不开事务、逐句按库的 autocommit 直发，
/// 公开 BeginTransaction 路径按位缺位当场抛——严禁静默假事务）；UPDATE/DELETE 在方言那两格整格拒，
/// Upsert_* 因此也没有接（INSERT 同 ts 覆盖确实像天然 upsert，但 3.x 重复时间戳的确切语义
/// ——整行覆盖还是部分列——要活库钉死，重开面记 notes）；Returning/Sequences/Identity_* 是
/// "官方语句形状没读到"（本家没有由库递增的列，主键就是那颗时间戳，由调用方自己给值）；
/// Comment_On_Column 不声明：本家没有列注释这一格（官方 column_definition 文法里没有 COMMENT 项，
/// INS_COLUMNS 目录也没有备注列），而表注释那半能发也能读回（INS_TABLES.table_comment），
/// 成对闭合走 TryEmitTableComment 的返回值说话，不欠一颗位；稳定表（STABLE）面是
/// provider 本地发射（声明位 [TDengineStable]/[TDengineTag] 走 EntityContract.Stable 那颗槽），
/// 不欠能力位；Bulk 交 null，批量从 L3 起；
/// Alter_Column_Precision 保守不声明（ALTER … MODIFY COLUMN 的改宽文档有一句，活库复核再翻）。
/// 整层是厂商文档口径、未实测台账——重开条件只有一条：拿到一台可跑的引擎。</summary>
[QuickSqlProvider(DbKind.TDengine)]
public sealed class TDengineProvider : IDbProvider
{
    /// <summary>内建表里的 key。名字与 DbKind.TDengine 的 ToString().ToLowerInvariant() 一致，
    /// 所以 key 那一颗不需要额外映射表——"名字就是自己"。</summary>
    public const string DriverKey = "tdengine";

    readonly TDengineTypes _types = TDengineTypes.Instance;
    readonly TDengineValues _values = TDengineValues.Instance;
    readonly TDengineExpressions _expressions = new();
    readonly TDengineKeys _keys = new();
    readonly TDengineDialect _dialect;

    public TDengineProvider(DbProviderFactory driverFactory)
    {
        Connectors = new TDengineConnectorFactory(driverFactory);
        CodeFirst = new TDengineCodeFirst(_types, _values);
        _dialect = new TDengineDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.TDengine;

    /// <summary>位掩码本体，三位各自的产出文本见各槽位类。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Boolean_Type | DbCapabilities.Guid_Char36 |
        DbCapabilities.Schemas;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new TDengineSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new TDengineConcurrency();

    public IChangeGuard ChangeGuard { get; } = new TDengineChangeGuard();

    /// <summary>没有批量策略：没有装载通道的读数，批量退回链自然从 L3 起。</summary>
    public IBulkStrategy? Bulk => null;

    /// <summary>CREATE DATABASE 语句在本家存在，但"连接串不带 db 直连再补建"那条锚点路
    /// 没有读数（ws 驱动的连接串 db 缺省空串，能不能空库连接只有引擎答得了）——
    /// 文档口径下显式交 false，重开条件与整层同一条：拿到一台引擎。</summary>
    public bool CanCreateDatabase => false;
}
