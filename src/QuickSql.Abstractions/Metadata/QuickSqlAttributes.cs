namespace QuickSql.Metadata;

/// <summary>注解必须能区分"没写"和"写了 false"，否则优先级链无法判定。
/// bool 表达不了这件事，所以自研属性一律用三态。</summary>
public enum TriState
{
    Unset = 0,
    Yes = 1,
    No = 2,
}

/// <summary>自研强属性层。名字、类型名、排除、主键、必填、长度、备注这些系统注解
/// （Table、Column、NotMapped、Key、Required、StringLength/MaxLength、Description/Display）
/// 接得住的语义一律不在这里重复；这一层只收 DataAnnotations 表达不了的语义：
/// QuickColumn 的尺寸与三态、Index 的复合索引、以及分表/集群/并发/级联那四颗。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class QuickColumnAttribute : Attribute
{
    /// <summary>字符串列的长度（应为多少个字符）。与 DataAnnotations 的 StringLength/MaxLength
    /// 同格、但优先级更高（自研层赢过注解层）。</summary>
    public int Length { get; set; }

    public int Precision { get; set; }

    public int Scale { get; set; }

    /// <summary>三态："没写""写了 nullable""写了 not-null"。bool 表达不了这件事，
    /// 优先级链无法判定；Required 只有"非空"一个方向，补不上另一半。</summary>
    public TriState IsNullable { get; set; } = TriState.Unset;

    public TriState IsPrimary { get; set; } = TriState.Unset;

    /// <summary>No = 有列但不是数据库自增（客户端主键、序列由 IKeyStrategy 决定）。
    /// 这一格没有 DataAnnotations 等价注解——Key 只管"谁是键"，管不到"值谁给"。</summary>
    public TriState IsIdentity { get; set; } = TriState.Unset;
}

/// <summary>复合索引，形状与 EF Core 的 Index 同一版：点成员名、不给索引名——
/// 名字由约定生成（IX_表_列1_列2，EntityContractFactory 那一份实现）。
/// Clustered 与 Descending 是 DataAnnotations 里没有的存储档，留给 provider 扩展用。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class IndexAttribute(params string[] memberNames) : Attribute
{
    public string[] MemberNames { get; } = memberNames;

    public bool IsUnique { get; set; }

    public bool Clustered { get; set; }

    public bool Descending { get; set; }
}

/// <summary>把这一张逻辑表声明成分表：系统的 Table(Name)（或类型名）此时给的是带占位符的模板，
/// 真正的表名按 kind 从 field 那一列的值算出来（占位符清单与各自的适用档在 SplitRouting）。
/// 这一格不落 Fluent 等价写法：分表最不能有的是"两条路径算出两种表名"；要按运行期上下文改表名
/// （影子表、租户表那一类）走的是 TableRemap 钩子那格。field 必须是一列（被 NotMapped 排除掉就当场抛），
/// 时间档要日期类型，Hash 档接受整数与字符串。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SplitTableAttribute(SplitKind kind, string field) : Attribute
{
    public SplitKind Kind { get; } = kind;

    public string Field { get; } = field;

    /// <summary>仅 Hash 档需要，其余档忽略它。少于 2 个桶等于没分。</summary>
    public int Buckets { get; set; }
}

/// <summary>把这一张逻辑表声明成"住在某座具名集群里"（读写分离与分库共用这一颗名字）。
/// 注解里只有集群名、没有连接串：连接串归 QuickSqlOrmOptions 那三颗（ConnectionString 是缺省集群的
/// 主库、Replicas 是它的从库、Clusters 是其余具名集群），所以这一颗注解可以进版本库而那条串不能。
/// 不落 Fluent，因为"两条路径算出两种归属"在分库上是把语句发到另一座库里去，比表名算错更贵；
/// 没写这颗注解的实体全都在 main，而 main 就是那条唯一的连接串。
/// cluster 空或全空白当场抛（启动期）；显式写 main 与不写这一颗同义，而 Clusters 那一列里不许
/// 再出现 main——那一座的名字由 ConnectionString 占着。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DataSourceAttribute(string cluster) : Attribute
{
    public string Cluster { get; } = cluster;
}

/// <summary>乐观并发的比较列（DataAnnotations 里没有等价的注解，所以落在自研属性层）。
/// 默认的整型族是"只比较、不书写"：这一列不进 UPDATE 的 SET、不进 INSERT 的列清单
/// （ORM 每次更新发 <c>Version = Version + 1</c>），但进 UPDATE/DELETE 的 WHERE（比较物化时的旧值）。
/// "不书写"不等于"不参与脏判定"：往这一列里写值是有人绕开了 ORM 的 +1，必须现形，
/// 所以脏判定照比，比出差异就在发射前整句拒掉（剔掉那一列才是错的——那等于把调用方写进去的值
/// 静默丢弃，而静默丢弃查不出来）。AppSetsValue = true 是应用给值族（ConcurrencyStamp 一派）：
/// 那一列进 SET 与 INSERT 列清单（写出去的是实体当前值），WHERE 仍比快照旧值，不自动加一——
/// 新值由应用在保存前生成好。rowversion / 值归库给的那一档支持已移除。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConcurrencyTokenAttribute : Attribute
{
    /// <summary>默认构造：整型自增那一族（ORM 在 SET 里发 Version = Version + 1）。</summary>
    public ConcurrencyTokenAttribute() => AppSetsValue = false;

    /// <summary>应用给值那一族：新值由应用在保存前生成，列进 SET 与列清单，WHERE 仍比快照。</summary>
    public ConcurrencyTokenAttribute(bool appSetsValue) => AppSetsValue = appSetsValue;

    /// <summary>这一并发列的新值由应用给（写出去），而不是由 ORM 的 SET 自增。</summary>
    public bool AppSetsValue { get; }
}

/// <summary>关掉级联：这一颗实体（打在类上）或这一根集合导航（打在属性上）在 Add/Remove
/// 顺导航图带人时不出边，子实体要自己 Add/Remove。
/// 级联默认是开的，所以这一颗只有退出用途，没有开启用途：它给的是"这张表的子行不归父行管"
/// 那一类（多对多的中间表、由别的流程维护的从属行、以及库里根本不该被父行带着走的集合）。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class NoCascadeAttribute : Attribute;
