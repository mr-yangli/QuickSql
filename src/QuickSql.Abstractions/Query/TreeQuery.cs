namespace QuickSql.Query;

/// <summary>树形查询的一行：实体 + 它在闭包里的位置。扁形状是树形状的同一份数据的另一种交法，
/// 两者由同一次缝合产出（先建森林再前序摊平），所以"行与节点对不上"这一类缺陷没有存在的空间。</summary>
public sealed record TreeRow<T>
{
    public required T Value { get; init; }

    /// <summary>本行的树键（实体主键值）。</summary>
    public required object Key { get; init; }

    /// <summary>父键值。null = 库里这一行没有父；非 null 但父不在本次闭包里 = 孤儿，按根交回。</summary>
    public object? ParentKey { get; init; }

    /// <summary>输出森林里的层级，根 = 0。Descendants 的根是锚点（IncludeSelf(false) 时是锚点的直接子），
    /// Ancestors 的根是最上层祖先（所以面包屑序 = Depth 升序），ToTree 的根是片内无父的那些行。</summary>
    public required int Depth { get; init; }

    /// <summary>从输出森林的根到本行的键串，'/' 连接（如 "42/57/913"）。在内存里拼，不走库端字符串函数，
    /// 所以 19 家逐字节同形。它是展示与定位用的，不承诺字典序等于树序（键是数字时 "10" 排在 "2" 前）。</summary>
    public required string Path { get; init; }
}

/// <summary>树形查询的一个节点。Children 的顺序 = 取数顺序（ToTree 那一档就是 SQL 的 ORDER BY，
/// 逐层爬那一档是每层句子的序），不在内存里重排。</summary>
public sealed class TreeNode<T>
{
    readonly List<TreeNode<T>> _children = [];

    public required T Value { get; init; }

    public required object Key { get; init; }

    public object? ParentKey { get; init; }

    /// <summary>见 <see cref="TreeRow{T}.Depth"/>。缝合时落位，之后只读。</summary>
    public int Depth { get; internal set; }

    /// <summary>见 <see cref="TreeRow{T}.Path"/>。</summary>
    public string Path { get; internal set; } = string.Empty;

    /// <summary>本次闭包里没有父可挂（库里无父，或父被片界定/过滤器切在外面，或父在环上）。</summary>
    public bool IsRoot { get; internal set; }

    public IReadOnlyList<TreeNode<T>> Children => _children;

    internal void AddChild(TreeNode<T> child) => _children.Add(child);
}

/// <summary>树形查询的链：三颗口（Descendants / Ancestors / ToTree）里前两颗交回它。
/// 这一层只收"闭包怎么取"的两颗旋钮，不收 Where/OrderBy/Select——闭包行上再筛一刀会把链砍断，
/// 交回的是"看着像树"的残段，所以那些口在这一档没有出口（要筛就在接树之前筛，那一句会落到每一层句子上）。</summary>
public interface ITreeQuery<T>
{
    /// <summary>深度上限，缺省 32。它同时是拦环的第二道（第一道是已访问键集）：
    /// 库里成环时句子数与行数都有界，不依赖任何库端的 MAXRECURSION / CYCLE。</summary>
    ITreeQuery<T> Depth(int max);

    /// <summary>锚点自身要不要出现在结果里，缺省 true。摘掉时按方向分两读：子集档锚点的直接子升为根
    /// （Depth = 0）；父级档锚点被摘掉，链还是那条链、根仍是最上层祖先。ToTree 那一档没有锚点，这颗不适用。</summary>
    ITreeQuery<T> IncludeSelf(bool include = true);

    /// <summary>扁闭包，前序（根 → 子）。</summary>
    Task<List<TreeRow<T>>> ToTreeRowsAsync(CancellationToken ct = default);

    /// <summary>嵌成森林。与 ToTreeRowsAsync 同一次缝合的两种交法。</summary>
    Task<List<TreeNode<T>>> ToTreeAsync(CancellationToken ct = default);

    List<TreeRow<T>> ToTreeRows();

    List<TreeNode<T>> ToTree();

    /// <summary>这一档的 SQL 出口只给"第一句"：ToTree 给那一句取片，Descendants/Ancestors 给锚点那一句。
    /// 逐层爬的后续句子不在这颗口里——闭包是多句，一颗 string 装不下，假装装得下就是诊断口骗人。
    /// 要看全部句子走 Explain() 的模型段与执行期的 AOP 播报。</summary>
    string ToSql();

    QueryPlan Explain();
}
