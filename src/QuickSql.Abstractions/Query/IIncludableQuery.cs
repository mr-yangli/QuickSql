namespace QuickSql.Query;

/// <summary>急切加载（Include/ThenInclude）的链式接口。从 Include 或 ThenInclude 返回，
/// 允许继续链式调用 ThenInclude 加载更深层的导航属性。继承 IQuery 的泛型查询口，
/// 所以 Where/OrderBy/ToListAsync 等所有查询方法仍然可用；TLastProp 记录最后加载的导航属性类型，
/// 供 ThenInclude 的编译期类型检查使用。</summary>
public interface IIncludableQuery<T, TLastProp> : IQuery<T>
    where T : class
{
}

/// <summary>集合导航（to-many）急切加载的链式接口。从 IncludeMany 或 ThenIncludeMany 返回，
/// 允许继续 ThenIncludeMany 往下一层挂集合导航、或 ThenInclude 给这一层的子实体挂一根引用导航。
/// 它与 IIncludableQuery 是两家，不是一家的两个名字：那一家是一条 LEFT JOIN 把整只实体带回来（引用导航），
/// 这一家每一层是另发一条 SELECT 再内存缝合（主查询 SQL 一个字符不变）；ThenInclude 两边都收、收的是各自那一档。
/// T 为根实体，TItem 为链上最后一层集合导航的元素类型（记的是元素型而不是 List），供下一颗 lambda 的形参在编译期定下来。
/// 链级 OrderBy 排的永远是父行；要给最后一层子行点序，接 OrderByMany 那颗扩展口——它把序落进这一层那条另发
/// SELECT 的 ORDER BY，主查询一个字符不变。点在哪一层后面就排哪一层，没点的层不排。</summary>
public interface IIncludableCollectionQuery<T, TItem> : IQuery<T>
{
}
