using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Query.Model;

/// <summary>查询的唯一真相。链式与 LINQ 两条路径都只能往这里追加，
/// 谁都不许自己生成 SQL 文本。不可变：每次派生新实例，所以复用与并发枚举天然安全。</summary>
public sealed record QueryModel
{
    public required IReadOnlyList<TableRef> Tables { get; init; }

    /// <summary>FROM 位的覆盖口。null 说的是"照旧：根表位就是那张实体表"；非 null 时编译器把它原样端出去
    /// （今天两种：WithSql 交回的一段用户自己的 SELECT，与派生表折出来的表源）。这一格里有东西，
    /// 模型规范文本就必须有对应的一行，否则等价性判据会漏掉一整类差异（打印在 QueryModelPrinter）。</summary>
    public TableSource? Source { get; init; }

    /// <summary>派生表入口：整份模型先编译成内层 SELECT，再作为外层唯一的表位。
    /// 与 Source 的分工是谁写内层：这一格的内层由库自己规划（键、聚合、页都在内层，外层只剩一个
    /// COUNT(*)），Source 那一格的内层是用户写的文本。两者不会同时出现在一份模型上。</summary>
    public QueryModel? DerivedFrom { get; init; }

    /// <summary>分表实体的已选片清单：每一项都是把占位符换算完的物理表，顺序就是 UNION ALL 的分支顺序
    /// （时间档按周期升序，哈希档按给出的桶序）。null 说的是这一份模型还没选片——分表实体带着 null
    /// 走到编译口会当场抛，本家不给"默认全片扫"的静默大范围。与 Source 同一条纪律：
    /// 这一格里有东西，模型文本就必须有对应的一行。</summary>
    public IReadOnlyList<TableInfo>? Shards { get; init; }

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];

    /// <summary>AND 折叠前的原样集合——保留它才能做到「链式与 LINQ 产出同一个模型」的可判定比较。</summary>
    public IReadOnlyList<SqlExpression> Predicates { get; init; } = [];

    public IReadOnlyList<OrderExpression> Ordering { get; init; } = [];

    public required ProjectionPlan Plan { get; init; }

    public IReadOnlyList<SqlExpression> GroupBy { get; init; } = [];

    public SqlExpression? Having { get; init; }

    public int? Offset { get; init; }

    public int? Limit { get; init; }

    public bool Distinct { get; init; }

    public string? CacheKey { get; init; }

    public TimeSpan? CacheTtl { get; init; }

    /// <summary>急切加载的导航属性树。空列表表示没有 Include。编译器按深度优先遍历生成
    /// LEFT JOIN 与扩展列，物化器按同一顺序读取并设置导航属性。</summary>
    public IReadOnlyList<IncludeNode> Includes { get; init; } = [];

    /// <summary>集合导航的批量加载意图。不改写主查询 SQL，执行层在主查询完成后
    /// 发第二条查询拿子实体，内存缝合回父实体的集合属性。</summary>
    public IReadOnlyList<IncludeManyNode> IncludeManys { get; init; } = [];

    /// <summary>拆分查询标志。为 true 时引用导航不走 LEFT JOIN，
    /// 而是主查询之后单独发查询再内存缝合。不进规范文本：与跟不跟踪一样是执行策略而非查询形状。</summary>
    public bool SplitQuery { get; init; }

    /// <summary>链上对角色的显式表态：FromPrimary() / FromReplica() 落在这里，缺省 Auto 才是交给
    /// 数据源路由判。它不进规范文本，与 SplitQuery 同一条理由：这一格改的是这句发在哪条连接上，
    /// 一个字都不改 SQL，进文本会把"点没点过 FromPrimary"变成等价性判据的例外。集群名不在这格里：
    /// 它不是链上的决定，而是每张表的声明，由 DataSourceRouting.ClusterOf 从表位算出来。</summary>
    public ChainChoice Role { get; init; } = ChainChoice.Auto;

    /// <summary>归约规则：聚合语义不含序，所以 Count/Sum/Any 要把 ORDER BY 剥掉；
    /// 但带 Skip/Take 的形态必须保留（OFFSET 在没有 ORDER BY 时不保证行序，T-SQL 那一族还直接报错）。
    /// 剥不剥由模型决定，不由方言决定。</summary>
    public bool StripsOrdering => !Plan.UsesOrdering && Limit is null;

    public TableRef Root => Tables[0];

    /// <summary>等价性测试的判据：两个模型的规范文本必须逐字符相等。</summary>
    public string StableKey => QueryModelPrinter.Print(this);

    public QueryModel AddPredicate(SqlExpression predicate)
        => this with { Predicates = [.. Predicates, predicate] };

    public QueryModel AddOrderings(IReadOnlyList<OrderExpression> orders)
        => this with { Ordering = [.. Ordering, .. orders] };

    public QueryModel AddJoin(JoinClause join, TableRef table)
        => this with { Joins = [.. Joins, join], Tables = [.. Tables, table] };
}

/// <summary>投影计划决定物化形状，因此它属于模型而不是方言层。</summary>
public abstract record ProjectionPlan
{
    /// <summary>投影本身是否依赖 ORDER BY（实体/DTO/标量投影依赖，聚合不依赖）。</summary>
    public virtual bool UsesOrdering => true;
}

public sealed record EntityProjection(int TableIndex) : ProjectionPlan;

public sealed record ValueProjection(SqlExpression Node, Type ResultType) : ProjectionPlan;

/// <summary>DTO / 匿名类型投影。Slots 的顺序即结果集列序。</summary>
public sealed record ObjectProjection(Type ResultType, IReadOnlyList<ProjectionSlot> Slots, bool ViaConstructor) : ProjectionPlan;

public sealed record ProjectionSlot(string Name, SqlExpression Node, Type MemberType);

public sealed record AggregateProjection(AggregateKind Kind, SqlExpression Node, Type ResultType, bool Distinct) : ProjectionPlan
{
    public override bool UsesOrdering => false;
}

public enum AggregateKind { Count, LongCount, Sum, Average, Min, Max, Any, All }
