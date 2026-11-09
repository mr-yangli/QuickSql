using QuickSql.Metadata;
using QuickSql.Tracking;

namespace QuickSql.Query.Model;

/// <summary>集合导航（to-many）的加载意图。与 IncludeNode 不同：不改写主查询 SQL，而是在主查询
/// 执行完后发第二条查询拿子实体，再在内存里缝回父实体。FK 发现走约定（子实体上「父类型名Id」）
/// 或子实体上指向父类型的 RelationshipContract。一层之下可以再挂一层（ThenIncludeMany，见
/// Children），同一层旁边还可以挂引用导航（ThenInclude，见 References）。</summary>
public sealed class IncludeManyNode
{
    /// <summary>父实体上的集合属性名（如 "Items"）。</summary>
    public required string NavigationMemberName { get; init; }

    /// <summary>子实体的 CLR 类型。</summary>
    public required Type ChildType { get; init; }

    /// <summary>子实体上的 FK 列名（指向父实体 PK）。顺序与父实体 PK 列对齐。</summary>
    public required IReadOnlyList<string> ChildFkColumnNames { get; init; }

    /// <summary>子型那一档的建档委托（带编译期子型，建档不必反射）。IncludeMany 登记子型走
    /// TrackAll，它按运行期类型找档位，而档位只有泛型 For 口能建——子型档位没建就是那句
    /// 「内核的不变量被破」；建出的档与 ChildType 对不上时当场响，不留静默。</summary>
    internal Func<ChangeTracker, ITypeTracker>? ChildTrackerFactory { get; init; }

    /// <summary>子节点（ThenIncludeMany 链）：挂在本节点子型上的再一层集合导航。与
    /// IncludeNode.Children 同一棵树形态，区别在每一层各发一条 SELECT，父端对象是上一层刚缝出来的
    /// 那一批；Children 不进主查询，主查询 SQL 一个字符不变。</summary>
    public List<IncludeManyNode> Children { get; } = [];

    /// <summary>挂在本节点子型上的引用导航（ThenInclude）。形状与 AsSplitQuery 那一档的引用加载
    /// 一样：执行层按 WHERE 目标.PK IN（这一批子实体的 FK 值）另发一条 SELECT 再逐只缝回，主查询
    /// SQL 同样一个字符不变。与 Children 分两格是因为父端不同：那一格是子实体的子实体（往下走一层），
    /// 这一格是子实体手里的另一只实体（往旁边走一根）。</summary>
    public List<IncludeNode> References { get; } = [];

    /// <summary>OrderByMany 落在本层子行上的序。执行层把它灌进这一层子 SELECT 的 OrderBy 槽——
    /// 发射在库里，不在内存里：缝完再排要在应用进程里付一次全量拷贝，"每父取前 N"那一族句子离开
    /// ORDER BY 根本发不出来。空清单 = 没点过这颗口，那一档子 SELECT 逐字节同形（方言对空序不发
    /// ORDER BY）；每层各拿各的清单，一颗口不隔层生效。</summary>
    public List<IncludeManyOrdering> Orderings { get; } = [];
}

/// <summary>子行序的一档：存的是接链那一刻就在子实体契约上认好的列（OrderByMany 的认列与链级
/// OrderBy(string) 共用同一颗咽喉，认不出当场抛），而不是表达式本身——报错响在接链那一刻，执行层
/// 每次执行现造 AST 时拿现成的列直发，不必每跑一次重认一遍名字。Descending 是降序位：收的是表达式，
/// 方向没有第二种来源，只能由这一颗形参给。</summary>
public sealed record IncludeManyOrdering(ColumnContract Column, bool Descending);
