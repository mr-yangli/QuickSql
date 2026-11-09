using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s 的驱动槽位。单层 provider：Informix 血统底下只有它一颗，"没有可覆写的父类"
/// 不是选择而是没有对象。构造期不反射：DbProviderFactory 由 Core 的 DriverRegistry 注入；
/// 程序集名 GBase.DataDB 只来自内建表一行、从未解析成功，连工厂类名字都没读到——
/// 这条驱动路径第一次有键是"纯按内建表存在"（解析不开是恒真，直到拿到驱动包）。
/// 能力位 4 位，六家最少，且是第一颗"分页有产出而一位都报不出"的：句首限定词 SELECT FIRST SKIP
/// 在三分页枚举位里没有对应那颗，报成任何一颗现有的都是撒谎（共用层不按那三位门控行为，今天代价为零，
/// 但位掩码从此不能被读成"分页能力的完整读数"）。其余：Schemas 对目录读侧的 owner 闸（"模式"在本家是所有者），
/// Alter_Column_Precision 对 MODIFY (…) 那句，CaseSensitive 对标识符策略，Guid_Char36 对 CHAR(36)
/// （没有原生 UUID 也没有取号函数，这一位是唯一答案）。
/// 刻意不声明的：Returning（回填抛）；Boolean_Type（位只跟存储型走，bool 落 INTEGER）；
/// Identity_ExplicitInsert——SERIAL 写显式值默认允许，false 说的是"不需要开关"（SPI 只有开关那一格）；
/// MultiStatement——多语句批没有任何材料证明可行，与语句后回读那颗开关两处必须一起改；
/// Guid_Raw16——另一支候选压根不存在。整层正确性挂在一颗未验证的等价关系上（"Informix 的即 GBase 8s 的"）：
/// 重开条件是拿到一份 GBase 8s 手册，或一台库。</summary>
[QuickSqlProvider(DbKind.Gbase8s)]
public sealed class Gbase8sProvider : IDbProvider
{
    /// <summary>内建表里的 key；assembly 写 GBase.DataDB（从未解析成功过）。名字与 DbKind.Gbase8s 的小写形一致，不需要额外映射表。</summary>
    public const string DriverKey = "gbase8s";

    readonly Gbase8sTypes _types = Gbase8sTypes.Instance;
    readonly Gbase8sValues _values = Gbase8sValues.Instance;
    readonly Gbase8sExpressions _expressions = new();
    readonly Gbase8sKeys _keys = new();
    readonly Gbase8sDialect _dialect;

    public Gbase8sProvider(DbProviderFactory driverFactory)
    {
        Connectors = new Gbase8sConnectorFactory(driverFactory);
        CodeFirst = new Gbase8sCodeFirst(_types);
        _dialect = new Gbase8sDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.Gbase8s;

    /// <summary>位掩码本体（产出文本见类注释）。不是 virtual：这一族没有叶子，不留没人覆写的虚函数。
    /// 4 是六家最少，但别读成"只会四件事"——有一颗是"发了文本而枚举没位"。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Schemas | DbCapabilities.Alter_Column_Precision |
        DbCapabilities.CaseSensitive_Identifiers | DbCapabilities.Guid_Char36 |
        DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new Gbase8sSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new Gbase8sConcurrency();

    public IChangeGuard ChangeGuard { get; } = new Gbase8sChangeGuard();

    /// <summary>没有批量策略：文档面读到过装载一族的"名字"（Informix 的 ESO 那一支）而托管那面连一颗
    /// 可反射的类型名都没有，也没有可报的颗数上限——报出去的是"没有高速通道"，批量退回链自然从 L3 起。
    /// 重开条件排在"改回厂商程序集名"那一步之后（程序集本身从未解析成功过）。</summary>
    public IBulkStrategy? Bulk => null;

    /// <summary>GBase 8s（Informix 血统）的建库要走 ontape/dbaccess 等管理工具，
    /// 不是 SQL 语句能做的事，显式交 false。</summary>
    public bool CanCreateDatabase => false;
}
