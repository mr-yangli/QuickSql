using QuickSql.Query.Model;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace QuickSql.Query;

/// <summary>急切加载的扩展方法。Include 与 ThenInclude 作为扩展方法实现，不在 IQuery 接口上
/// （返回类型需要是 IIncludableQuery 才能继续链式 ThenInclude，而接口方法不允许改变返回类型）。
/// 所有方法都转发到 QueryEngine 的内部实现。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
public static class IncludeExtensions
{
    /// <summary>急切加载引用导航属性。通过 LEFT JOIN 将关联实体与主实体一起加载，避免 N+1
    /// 查询问题。返回 IIncludableQuery 以继续链式调用 ThenInclude 加载更深层导航。</summary>
    public static IIncludableQuery<T, TProp> Include<T, TProp>(
        this IQuery<T> query,
        Expression<Func<T, TProp>> navigation)
        where T : class
    {
        var engine = GetEngine<T>(query);
        var node = engine.Include(navigation);
        return new QuickSqlIncludableQuery<T, TProp>(engine.AddInclude(node), node);
    }

    /// <summary>在已有 Include 的基础上继续加载更深层的导航属性。只能在 Include 或另一个
    /// ThenInclude 之后调用。链式的深度由链式对象携带的最后加载节点决定，而不是模型中的顶级
    /// Include 列表。</summary>
    public static IIncludableQuery<T, TProp> ThenInclude<T, TLast, TProp>(
        this IIncludableQuery<T, TLast> query,
        Expression<Func<TLast, TProp>> navigation)
        where T : class
    {
        if (query is not QuickSqlIncludableQuery<T, TLast> fq)
            throw new QuickSqlException(
                $"ThenInclude 只能用在 QuickSql 的 Include/ThenInclude 链式对象上。当前类型是 {query.GetType().Name}。");

        var engine = fq.Engine;
        var parentNode = fq.LastIncludeNode;
        var node = engine.ThenInclude(parentNode, navigation);
        return new QuickSqlIncludableQuery<T, TProp>(engine.AddIncludeTable(node), node);
    }

    static QueryEngine GetEngine<T>(IQuery<T> query) where T : class
        => query is QuickSqlQuery<T> fq
            ? fq.Engine
            : throw new QuickSqlException(
                $"Include/ThenInclude 只能用在 QuickSql 的链式查询上（orm.Select&lt;T&gt;() 返回的对象）。" +
                $"当前查询类型是 {query.GetType().Name}，不是 QuickSql 的内部实现。");

    /// <summary>在已有的集合加载（IncludeMany）上再往下一层挂集合导航：链的深度由链式对象携带的
    /// 最后一颗节点决定，与 ThenInclude 同一套读法。父端是上一层刚缝出来的那一批子实体，所以这一档
    /// 不碰主查询（库端是主查询一条、每层子查询一条）。再往上一层的 IncludeMany 不接在这颗后面——
    /// 那颗永远从根契约起算，要挂回根就重起一段链。</summary>
    public static IIncludableCollectionQuery<T, TProp> ThenIncludeMany<T, TItem, TProp>(
        this IIncludableCollectionQuery<T, TItem> query,
        Expression<Func<TItem, IEnumerable<TProp>>> navigation)
        where TProp : class
    {
        if (query is not QuickSqlIncludableManyQuery<T, TItem> fq)
            throw new QuickSqlException(
                $"ThenIncludeMany 只能用在 QuickSql 的 IncludeMany/ThenIncludeMany 链式对象上。当前类型是 {query.GetType().Name}。");

        var engine = fq.Engine;

        // 这一层的子型那一档同样在接链处建（编译期子型只有这一句有），缝出来的子行自己就进会话。
        var node = engine.ThenIncludeMany<TProp>(fq.LastIncludeManyNode, navigation, static tracker => tracker.For<TProp>());
        return new QuickSqlIncludableManyQuery<T, TProp>(engine, node);
    }

    /// <summary>在已有集合加载（IncludeMany / ThenIncludeMany）上给这一层的子实体再挂一根引用导航：
    /// IncludeMany(o =&gt; o.Items).ThenInclude(i =&gt; i.Material)。主查询一个字符不变，多出来的只是执行期
    /// 一条 SELECT ... WHERE 目标.主键 IN (这一批子实体的外键值) 再逐只缝回，与 AsSplitQuery 那一档共用
    /// 同一颗加载器，同一根边在两条路上长成同一个答案。集合导航在这颗口当场拒（那是 ThenIncludeMany
    /// 的射程）；返回的还是原来那一家（TItem 不变），所以重复调用是在同一层并列多挂几根引用边。
    /// 往引用导航的第二层今天没出口——只有 Include/ThenInclude 那条 JOIN 链（或 AsSplitQuery）走得到。</summary>
    public static IIncludableCollectionQuery<T, TItem> ThenInclude<T, TItem, TProp>(
        this IIncludableCollectionQuery<T, TItem> query,
        Expression<Func<TItem, TProp>> navigation)
    {
        if (query is not QuickSqlIncludableManyQuery<T, TItem> fq)
            throw new QuickSqlException(
                $"ThenInclude 只能用在 QuickSql 的 IncludeMany/ThenIncludeMany 链式对象上。当前类型是 {query.GetType().Name}。");

        fq.Engine.ThenInclude(fq.LastIncludeManyNode, navigation);

        return fq;
    }

    /// <summary>给链上最后一层集合加载的子行点序：IncludeMany(r =&gt; r.Details).OrderByMany(d =&gt; d.Order)
    /// —— 这一层另发的那条子 SELECT 带上 ORDER BY 出场，缝回父实体的顺序就是库里的顺序。序发射在库里
    /// 而不在内存里（内存排要付一次全量拷贝，"每父取前 N"离开 ORDER BY 根本发不出来），主查询一个字符
    /// 不变；点在哪一层后面就排哪一层，没点的层不排。排序键与链级 OrderBy(string) 共用同一套认列
    /// （属性名 → 列名 → 大小写不敏感唯一命中），认不出当场抛；只收子实体上的直接属性访问，嵌套路径
    /// 当场拒（这一档不发 JOIN，嵌套路径根本排不出去）；再点一次是整串替换，多列子行序今天没有出口。
    /// 泛型 TKey 收表达式键而不是 IComparable：免掉值类型每一次的装箱。</summary>
    public static IIncludableCollectionQuery<T, TItem> OrderByMany<T, TItem, TKey>(
        this IIncludableCollectionQuery<T, TItem> query,
        Expression<Func<TItem, TKey>> key,
        bool descending = false)
    {
        if (query is not QuickSqlIncludableManyQuery<T, TItem> fq)
            throw new QuickSqlException(
                $"OrderByMany 只能用在 QuickSql 的 IncludeMany/ThenIncludeMany 链式对象上。当前类型是 {query.GetType().Name}。");

        fq.Engine.OrderByMany(fq.LastIncludeManyNode, key, descending);

        return fq;
    }
}

/// <summary>IIncludableQuery 的内部实现。携带最后加载的 IncludeNode，供 ThenInclude 继续向深层链式加载。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlIncludableQuery<T, TProp>(QueryEngine engine, IncludeNode lastIncludeNode)
    : QuickSqlQuery<T>(engine), IIncludableQuery<T, TProp>
    where T : class
{
    public IncludeNode LastIncludeNode { get; } = lastIncludeNode;
}

/// <summary>IIncludableCollectionQuery 的内部实现。携带最后一颗 IncludeManyNode，供 ThenIncludeMany
/// 往下一层挂集合、ThenInclude 往旁边挂引用。与 QuickSqlIncludableQuery 的差别只在一颗：这一家不派生
/// 新引擎，因为两档节点都挂在本颗集合节点的 Children 与 References 上，模型的清单里不添条目。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlIncludableManyQuery<T, TItem>(QueryEngine engine, IncludeManyNode lastIncludeManyNode)
    : QuickSqlQuery<T>(engine), IIncludableCollectionQuery<T, TItem>
{
    public IncludeManyNode LastIncludeManyNode { get; } = lastIncludeManyNode;
}
