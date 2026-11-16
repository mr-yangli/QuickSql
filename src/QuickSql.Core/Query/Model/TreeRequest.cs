using QuickSql.Metadata;

namespace QuickSql.Query.Model;

/// <summary>闭包的取向。三颗口共用一颗内核，差别只在锚点从哪来、往哪一头走。</summary>
public enum TreeDirection
{
    /// <summary>整片：一句 SELECT 取片，锚点 = 片内无父的那些行。</summary>
    Slice,

    /// <summary>子集：锚点主键给定，往子那一头逐层爬。</summary>
    Descendants,

    /// <summary>父级：锚点主键给定，往父那一头逐层爬。</summary>
    Ancestors,
}

/// <summary>树形查询的意图。与 IncludeManyNode 同一档纪律：不改写主查询的列清单，
/// 执行层按它决定发几句、每句的 IN 臂落在哪颗列上，缝合与 Depth/Path 全在内存。
/// 进模型（因此进规范文本）：它是查询形状的一部分，不是执行策略——同一份模型在 19 家
/// 必须交回同一棵闭包，把它藏在引擎上就会让"点没点过树"逃过等价性判据。</summary>
public sealed record TreeRequest
{
    public required TreeDirection Direction { get; init; }

    /// <summary>自引用的父键列（接链那一刻就在契约上认好，执行层不再认第二遍）。</summary>
    public required ColumnContract ParentColumn { get; init; }

    /// <summary>锚点主键值。Slice 档为空（锚点由取片结果算）；其余两档空清单合法——
    /// 与 IncludeMany 的空父集同一纪律：一句 SQL 都不发，直接交回空集。null 锚点在接链处拒。</summary>
    public required IReadOnlyList<object> Anchors { get; init; }

    /// <summary>深度上限，缺省 32。逐层爬的句数上界，也是拦环的第二道。</summary>
    public int MaxDepth { get; init; } = DefaultMaxDepth;

    /// <summary>锚点自身要不要进结果，缺省 true。Slice 档不适用。</summary>
    public bool IncludeSelf { get; init; } = true;

    public const int DefaultMaxDepth = 32;
}
