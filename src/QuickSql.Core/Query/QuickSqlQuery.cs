using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Query.Model;
using QuickSql.Sql.Ast;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace QuickSql.Query;

/// <summary>标注文案集中一处：属性参数必须是常量表达式，故不能写成各类自己的 const。
/// 读侧每个落地类型付的是同一笔账：翻译 lambda 要反射读闭包成员，DTO/匿名投影要编译表达式树。</summary>
static class TranslationCost
{
    public const string Message = "翻译 lambda 要反射读闭包成员，DTO/匿名投影要编译表达式树（§5.10 的 G2/G3）。G1 预翻译接管后与本类一起摘掉。";
}

/// <summary>链式对象背后那台引擎的口。关联子查询要在表达式树里把捕获的链式对象求回 QueryEngine，
/// 有了这一格就是一型判断加一次属性读，不必每次按名字反射取 Engine 属性。
/// 公开形状不受影响：本接口是 internal，链式对象对外的成员一个都没多。</summary>
interface IEngineCarrier
{
    QueryEngine Engine { get; }
}

/// <summary>读侧的链式对象。不可变：每个算子返回新对象，所以同一个查询链
/// 可以复用、可以传给别人、不需要 Clone，并发枚举天然安全。
/// 这里没有任何 SQL 文本与连接：QueryEngine 才是干活的地方，本类只做类型化转发。
/// public 是因为 DbSet&lt;T&gt; 要的是同一条链 + 一份会话，继承把"链式方法在跟踪集合上该返回什么类型"
/// 一并复用（db.Orders.Where(...) 得到 IQuery 而仍然跟踪，会话在引擎上、派生时带着走）；
/// 构造口仍是 internal：链式的入口只有 orm.Select&lt;T&gt;() 与 db.Set&lt;T&gt;()。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
public class QuickSqlQuery<T> : IQuery<T>, IEngineCarrier
{
    /// <summary>默认流式批大小：ToAsyncEnumerable 上调用方不给值时用这个——对 SQL Server 它只是通过校验的数。</summary>
    const int DefaultStreamBatchSize = 50;

    internal QuickSqlQuery(QueryEngine engine) => Engine = engine;

    internal QueryEngine Engine { get; }

    QueryEngine IEngineCarrier.Engine => Engine;

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => Engine.StreamAsync<T>(DefaultStreamBatchSize, ct).GetAsyncEnumerator(ct);

    // ── 算子 ──────────────────────────────────────────────────────────

    public IQuery<T> Where(Expression<Func<T, bool>> predicate) => new QuickSqlQuery<T>(Engine.Where(predicate));

    public IQuery<T> WhereIF(bool condition, Expression<Func<T, bool>> predicate)
        => condition ? Where(predicate) : this;

    public IQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> projector)
        => new QuickSqlQuery<TResult>(Engine.Project(projector));

    public IOrderedQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> key)
        => new QuickSqlOrderedQuery<T>(Engine.Order(key, descending: false, append: false));

    public IOrderedQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key)
        => new QuickSqlOrderedQuery<T>(Engine.Order(key, descending: true, append: false));

    /// <summary>按名字那一颗同样只转发：拆分、认列、抛与叠加都在 QueryEngine 里，
    /// 这一层不养第二份口径。</summary>
    public IOrderedQuery<T> OrderBy(string sortSpec, bool descending = false)
        => new QuickSqlOrderedQuery<T>(Engine.Order(sortSpec, descending, append: false));

    public IQuery<T> Skip(int count) => new QuickSqlQuery<T>(Engine.Skip(count));

    public IQuery<T> Take(int count) => new QuickSqlQuery<T>(Engine.Take(count));

    public IQuery<T> Distinct() => new QuickSqlQuery<T>(Engine.Distinct());

    public IQuery<T> AsNoTracking() => new QuickSqlQuery<T>(Engine.NoTracking());

    /// <summary>与 AsNoTracking 同一形状：只松开手里的一样东西，模型一个字都不改
    /// （那里松的是会话，这里松的是全局过滤器），所以派生出来的那一条链仍然带着同一个跟踪器。</summary>
    public IQuery<T> IgnoreQueryFilters() => new QuickSqlQuery<T>(Engine.IgnoreFilters());

    /// <summary>与不带参数那一颗走的是 QueryEngine.IgnoreFilters(Type) 同一个落点，
    /// 只是递进去的键是 typeof(TFilter) 而不是 object——"全部"与"按名"在本层不是两条路，
    /// 差别只在清单里那一颗键能被谁匹配上。</summary>
    public IQuery<T> IgnoreQueryFilter<TFilter>() where TFilter : class
        => new QuickSqlQuery<T>(Engine.IgnoreFilters(typeof(TFilter)));

    public IQuery<T> WithCache(string cacheKey, TimeSpan ttl) => new QuickSqlQuery<T>(Engine.Cache(cacheKey, ttl));

    public IQuery<T> As(string alias) => new QuickSqlQuery<T>(Engine.Alias(alias));

    public IQuery<T> WithSql(string sql, object? args = null) => new QuickSqlQuery<T>(Engine.WithSql(sql, args));

    public IQuery<T> SplitTables(DateTime from, DateTime to) => new QuickSqlQuery<T>(Engine.SplitTables(from, to));

    public IQuery<T> SplitTables(params int[] buckets) => new QuickSqlQuery<T>(Engine.SplitTables(buckets));

    /// <summary>两颗点名角色的口径都在 IQuery 上：这里只是把 ChainChoice 落进模型，
    /// 判"能不能"的那一处是执行时取连接的那个门口，不在链上。</summary>
    public IQuery<T> FromPrimary() => new QuickSqlQuery<T>(Engine.Role(ChainChoice.Primary));

    /// <inheritdoc/>
    public IQuery<T> UnionAll(IQuery<T> other) => SetOps(other, SetOperationKind.UnionAll);

    /// <inheritdoc/>
    public IQuery<T> Union(IQuery<T> other) => SetOps(other, SetOperationKind.Union);

    /// <inheritdoc/>
    public IQuery<T> Intersect(IQuery<T> other) => SetOps(other, SetOperationKind.Intersect);

    /// <inheritdoc/>
    public IQuery<T> Except(IQuery<T> other) => SetOps(other, SetOperationKind.Except);

    /// <summary>集合运算的本体：形状守卫在链上当场把"发了也不会对"的形状拦掉，外层只换 FROM 位——
    /// 谓词、序、分页、投影、聚合都留给合并之后的那条链，它们落在外层就是落在整串集合运算上。</summary>
    QuickSqlQuery<T> SetOps(IQuery<T> other, SetOperationKind operation)
    {
        ArgumentNullException.ThrowIfNull(other);

        var first = Engine.Model;
        var second = other is IEngineCarrier carrier
            ? carrier.Engine.Model
            : throw new QuickSqlException(
                $"集合运算的参数要是一条本库的查询链（IQuery<{typeof(T).Name}>），收到的是 {other.GetType().Name}。");

        // 腿上点过 IgnoreQueryFilters 的当场拒：过滤器名单不进模型，合并之后外层的 Effective()
        // 会把过滤器压回整串——点了名的腿静默失效正是本仓库最不肯做的形状。要免过滤就整条链一起免。
        if (Engine.IgnoredFilters is not null || other is IEngineCarrier otherCarrier && otherCarrier.Engine.IgnoredFilters is not null)
            throw new QuickSqlException(
                "集合运算的腿不接 IgnoreQueryFilters/IgnoreQueryFilter<T>()：全局过滤器落在合并后的整串上，" +
                "腿上点名的免过滤会被外层静默压掉。要免过滤就在合并之前把整条链的口径定好（或合并之后整串点）。");

        EnsureLeg(first);
        EnsureLeg(second);

        // 外层只出"SELECT 列 FROM (两腿)"这一件事：腿自带的谓词与 Distinct 已经编译进各自的分支，
        // 留在模型顶层就是把第一条腿的条件再打到整串上——实测就是"UNION ALL 六行回来三十行"那种对不上账的形状。
        return new QuickSqlQuery<T>(Engine.Derive(first with
        {
            Source = new SourceSetOps(first.Root, first, second, operation),
            Predicates = [],
            Distinct = false,
        }));
    }

    static void EnsureLeg(QueryModel leg)
    {
        if (leg.Plan is not EntityProjection { TableIndex: 0 })
            throw new QuickSqlException(
                "集合运算的腿必须是实体整行投影：外层按腿的列序物化，投影/聚合腿的列清单与实体列对不上。" +
                "要先合并再投影（UnionAll(...).Select(...)），不要把 Select 写进腿里。");

        if (leg.Source is not null || leg.DerivedFrom is not null)
            throw new QuickSqlException(
                "集合运算的腿不接套娃：腿自己已经是一只 UNION（或 WithSql 覆盖源/派生表）时，" +
                "内外两层各自的列对齐是三份形状的事，这一档今天不到。");

        if (leg.Includes.Count > 0 || leg.IncludeManys.Count > 0)
            throw new QuickSqlException(
                "集合运算的腿不接急切加载（Include/IncludeMany）：JOIN 展开列会让两腿的列清单不再同形，" +
                "外层按位置物化就是静默串列。要子实体就合并之后按主查询另发一条。");

        if (leg.Ordering.Count > 0 || leg.Offset is not null || leg.Limit is not null)
            throw new QuickSqlException(
                "集合运算的腿不接序与分页：序与分页只长在外层（合并不去重时「每腿各取 N 行」与" +
                "「合并后取 N 行」是两种语义，本家不替调用方挑）。把 OrderBy/Skip/Take 挪到合并之后。");

        if (leg.GroupBy.Count > 0 || leg.Having is not null)
            throw new QuickSqlException(
                "集合运算的腿不接分组：分组腿的选列是键加聚合，与实体列对不上（同 Include 那一格）。" +
                "要按合并后的行分组就写 UnionAll(...).GroupBy(...) —— 那落在外层，语义正确。");

        if (leg.Root.Table.Split is not null)
            throw new QuickSqlException(
                "集合运算的腿不接分表实体：选片之后那条腿自己已经是一只 UNION，两层合并的语义要单独立账" +
                "（§16.4 的选片机器管第一层）。");
    }

    public IQuery<T> FromReplica() => new QuickSqlQuery<T>(Engine.Role(ChainChoice.Replica));

    public IQuery<T, T2> LeftJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class
        => new QuickSqlJoinQuery<T, T2>(Engine.Join(JoinKind.Left, on));

    public IQuery<T, T2> InnerJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class
        => new QuickSqlJoinQuery<T, T2>(Engine.Join(JoinKind.Inner, on));

    public ISelectGrouping<TKey, T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
        => new QuickSqlSelectGrouping<TKey, T>(Engine.GroupBy(keySelector));

    // ── P1：接口里有、实现还没有，所以直接抛，不给"看起来能用"的假象 ──────────

    public IIncludableCollectionQuery<T, TDetail> IncludeMany<TDetail>(Expression<Func<T, IEnumerable<TDetail>>> navigation)
        where TDetail : class
    {
        // 子型那一档的建档委托在这一句给：编译期子型只在这里有，而档位只有泛型那一颗 For<T>() 能建。
        // 有了它，IncludeMany 缝出来的子行自己就进会话，不再要调用方先摸一句 Set<子型>()。
        var node = Engine.IncludeMany<TDetail>(navigation, static tracker => tracker.For<TDetail>());
        return new QuickSqlIncludableManyQuery<T, TDetail>(Engine.AddIncludeMany(node), node);
    }

    public IQuery<T> AsSplitQuery(int navigateBatchSize = 20)
        => new QuickSqlQuery<T>(Engine.SplitQuery());

    /// <summary>「接口里有、实现还没有」的统一说法。不写成返回源对象：那样调用方看不出它没生效，
    /// 而这些路径的产出恰恰是"导航属性该有值了"。DbSet.Load 用同一个口，因为那是同一种"没实现"。</summary>
    internal static QuickSqlException Deferred(string member, string why)
        => new($"{member}(...) 未实现：{why} 现在静默返回源查询会让调用方以为导航属性已经加载，读出来全是 null。");

    // ── 出口 ──────────────────────────────────────────────────────────

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public string ToSql(out IReadOnlyList<AdoParameter> parameters) => Engine.ToSql(out parameters);

    public string ToLogSql() => Engine.ToLogSql();

    public Task<List<T>> ToListAsync(CancellationToken ct = default) => Engine.ListAsync<T>(ct);

    public Task<T[]> ToArrayAsync(CancellationToken ct = default) => Engine.ArrayAsync<T>(ct);

    public Task<T?> FirstOrDefaultAsync(CancellationToken ct = default) => Engine.FirstAsync<T>(ct);

    public Task<T> FirstAsync(CancellationToken ct = default) => Engine.FirstOrThrowAsync<T>(ct);

    public Task<T> SingleAsync(CancellationToken ct = default) => Engine.SingleAsync<T>(ct);

    public Task<T?> SingleOrDefaultAsync(CancellationToken ct = default) => Engine.SingleOrDefaultAsync<T>(ct);

    public Task<bool> AllAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Engine.AllAsync(predicate, ct);

    public Task<int> CountAsync(CancellationToken ct = default) => Engine.CountAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct = default) => Engine.AnyAsync(ct);

    public Task<long> LongCountAsync(CancellationToken ct = default) => Engine.LongCountAsync(ct);

    public Task<PagedResult<T>> ToPagedAsync(int page, int size, PagedOptions? options = null, CancellationToken ct = default)
        => Engine.PagedAsync<T>(page, size, options, ct);

    public IAsyncEnumerable<T> ToAsyncEnumerable(int batchSize) => Engine.StreamAsync<T>(batchSize, default);

    /// <summary>同步出口是 sync-over-async：内核没有第二套同步执行路径（那会变成两份要各自维护的
    /// 连接与钩子逻辑）。所有 await 都走 ConfigureAwait(false)，所以在没有同步上下文的宿主里不会死锁；
    /// 但它在 WinForms/ASP.NET 经典模型下会占住一个线程，异步可用时请优先用 *Async。</summary>
    public List<T> ToList() => Blocking(() => Engine.ListAsync<T>(default));

    public T[] ToArray() => Blocking(() => Engine.ArrayAsync<T>(default));

    public T? FirstOrDefault() => Blocking(() => Engine.FirstAsync<T>(default));

    public T First() => Blocking(() => Engine.FirstOrThrowAsync<T>(default));

    public T Single() => Blocking(() => Engine.SingleAsync<T>(default));

    public T? SingleOrDefault() => Blocking(() => Engine.SingleOrDefaultAsync<T>(default));

    public bool All(Expression<Func<T, bool>> predicate) => Blocking(() => Engine.AllAsync(predicate, default));

    public int Count() => Blocking(() => Engine.CountAsync(default));

    public bool Any() => Blocking(() => Engine.AnyAsync(default));

    public long LongCount() => Blocking(() => Engine.LongCountAsync(default));

    public bool Contains(T value)
    {
        // 运行期退化路径：表达式树内由 ExpressionTranslator 拦截翻译成 IN 子查询，不到这里。
        // 这里只处理直接调用（如 query.Contains(42)），物化后判等是正确但非最优的实现。
        return ToList().Contains(value);
    }

    // ── 聚合 ──────────────────────────────────────────────────────────

    public TResult Sum<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.SumAsync<TResult>(selector, default));

    public TResult Max<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.MaxAsync<TResult>(selector, default));

    public TResult Min<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.MinAsync<TResult>(selector, default));

    public TResult Average<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.AverageAsync<TResult>(selector, default));

    public Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.SumAsync<TResult>(selector, ct);

    public Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.MaxAsync<TResult>(selector, ct);

    public Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.MinAsync<TResult>(selector, ct);

    public Task<TResult> AverageAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.AverageAsync<TResult>(selector, ct);

    static TRes Blocking<TRes>(Func<Task<TRes>> run) => run().GetAwaiter().GetResult();
}

/// <summary>IOrderedQuery 的落地。有序与无序是同一个引擎，类型区别只为让
/// ThenBy 出现在能接它的对象上，不额外携带状态。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlOrderedQuery<T>(QueryEngine engine) : QuickSqlQuery<T>(engine), IOrderedQuery<T>
{
    public IOrderedQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> key)
        => new QuickSqlOrderedQuery<T>(Engine.Order(key, descending: false, append: true));

    public IOrderedQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> key)
        => new QuickSqlOrderedQuery<T>(Engine.Order(key, descending: true, append: true));

    public IOrderedQuery<T> ThenBy(string sortSpec, bool descending = false)
        => new QuickSqlOrderedQuery<T>(Engine.Order(sortSpec, descending, append: true));
}

/// <summary>分组链。这一层没有任何物化出口，这是刻意的：分组而未选的形状（整行列 +
/// GROUP BY）在任何一家都是错句，所以它连 Explain() 都不给，出口在 Select 交回的 IQuery 上。
/// 只做类型化转发，模型派生在 QueryEngine。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
class QuickSqlSelectGrouping<TKey, TValue>(QueryEngine engine) : ISelectGrouping<TKey, TValue>
{
    internal QueryEngine Engine { get; } = engine;

    public ISelectGrouping<TKey, TValue> Having(Expression<Func<IGrouping<TKey, TValue>, bool>> predicate)
        => new QuickSqlSelectGrouping<TKey, TValue>(Engine.Having(predicate));

    public IOrderedSelectGrouping<TKey, TValue> OrderBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new QuickSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: false, append: false));

    public IOrderedSelectGrouping<TKey, TValue> OrderByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new QuickSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: true, append: false));

    public ISelectGrouping<TKey, TValue> Skip(int count) => new QuickSqlSelectGrouping<TKey, TValue>(Engine.Skip(count));

    public ISelectGrouping<TKey, TValue> Take(int count) => new QuickSqlSelectGrouping<TKey, TValue>(Engine.Take(count));

    public IQuery<TResult> Select<TResult>(Expression<Func<IGrouping<TKey, TValue>, TResult>> projector)
        => new QuickSqlQuery<TResult>(Engine.Project(projector));
}

/// <summary>分组链的有序形态，理由与 QuickSqlOrderedQuery 同一条：类型区别只为让
/// ThenBy 出现在能接它的对象上（OrderBy 之后再 OrderBy 是替换而不是追加，那一颗口不在这层暴露）。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlOrderedSelectGrouping<TKey, TValue>(QueryEngine engine)
    : QuickSqlSelectGrouping<TKey, TValue>(engine), IOrderedSelectGrouping<TKey, TValue>
{
    public IOrderedSelectGrouping<TKey, TValue> ThenBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new QuickSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: false, append: true));

    public IOrderedSelectGrouping<TKey, TValue> ThenByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new QuickSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: true, append: true));
}

/// <summary>联表之后的两表查询。类型参数集合就是「已进 FROM 的表」，所以引用没联过的表在
/// 编译期就过不去，而不是等到数据库报"无法绑定多部分标识符"。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
class QuickSqlJoinQuery<T1, T2> : IQuery<T1, T2>
{
    public QuickSqlJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate)
        => new QuickSqlJoinQuery<T1, T2>(Engine.Where(predicate));

    public IQuery<T1, T2> WhereIF(bool condition, Expression<Func<T1, T2, bool>> predicate)
        => condition ? Where(predicate) : this;

    public IQuery<T1, T2> Skip(int count) => new QuickSqlJoinQuery<T1, T2>(Engine.Skip(count));

    public IQuery<T1, T2> Take(int count) => new QuickSqlJoinQuery<T1, T2>(Engine.Take(count));

    public IOrderedJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> key)
        => new QuickSqlOrderedJoinQuery<T1, T2>(Engine.Order(key, descending: false, append: false));

    public IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, TResult>> projector)
        => new QuickSqlQuery<TResult>(Engine.Project(projector));

    public IQuery<T1, T2, T3> LeftJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class
        => new QuickSqlJoinQuery<T1, T2, T3>(Engine.Join(JoinKind.Left, on));

    public IQuery<T1, T2, T3> InnerJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class
        => new QuickSqlJoinQuery<T1, T2, T3>(Engine.Join(JoinKind.Inner, on));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public string ToLogSql() => Engine.ToLogSql();

    public Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default)
        => new QuickSqlQuery<TResult>(Engine.Project(projector)).ToListAsync(ct);

    public Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default)
        => new QuickSqlQuery<TResult>(Engine.Project(projector)).ToPagedAsync(page, size, options, ct);
}

/// <summary>三表查询。链式在这里封顶：再往上是 4 个类型参数的同构复制，收益递减，
/// 四表以上的联表本版本没有入口（补齐属公开签名变更，待决）。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
class QuickSqlJoinQuery<T1, T2, T3> : IQuery<T1, T2, T3>
{
    public QuickSqlJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate)
        => new QuickSqlJoinQuery<T1, T2, T3>(Engine.Where(predicate));

    public IQuery<T1, T2, T3> WhereIF(bool condition, Expression<Func<T1, T2, T3, bool>> predicate)
        => condition ? Where(predicate) : this;

    public IQuery<T1, T2, T3> Skip(int count) => new QuickSqlJoinQuery<T1, T2, T3>(Engine.Skip(count));

    public IQuery<T1, T2, T3> Take(int count) => new QuickSqlJoinQuery<T1, T2, T3>(Engine.Take(count));

    public IOrderedJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key)
        => new QuickSqlOrderedJoinQuery<T1, T2, T3>(Engine.Order(key, descending: false, append: false));

    public IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, T3, TResult>> projector)
        => new QuickSqlQuery<TResult>(Engine.Project(projector));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public string ToLogSql() => Engine.ToLogSql();

    public Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default)
        => new QuickSqlQuery<TResult>(Engine.Project(projector)).ToPagedAsync(page, size, options, ct);
}

/// <summary>两表查询的有序形态。IOrderedJoinQuery 不继承 IQuery&lt;T1,T2&gt;，
/// 所以联表之后 OrderBy 就只剩排序与物化：想再筛条件要在 OrderBy 之前。留着这层窄化，但别把它记成
/// 正确性闸门——实测谓词写在 OrderBy 前后产出的是同一条 SQL（谓词恒进 WHERE、序恒进 ORDER BY，
/// 书写顺序进不了模型），它挡住的只是"写法顺序与读法不一致"。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlOrderedJoinQuery<T1, T2> : IOrderedJoinQuery<T1, T2>
{
    public QuickSqlOrderedJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2> ThenBy<TKey>(Expression<Func<T1, T2, TKey>> key)
        => new QuickSqlJoinQuery<T1, T2>(Engine.Order(key, descending: false, append: true));

    public IQuery<T1, T2> ThenByDescending<TKey>(Expression<Func<T1, T2, TKey>> key)
        => new QuickSqlJoinQuery<T1, T2>(Engine.Order(key, descending: true, append: true));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default)
        => new QuickSqlQuery<TResult>(Engine.Project(projector)).ToListAsync(ct);
}

/// <summary>三表查询的有序形态，理由同两表那颗。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlOrderedJoinQuery<T1, T2, T3> : IOrderedJoinQuery<T1, T2, T3>
{
    public QuickSqlOrderedJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2, T3> ThenBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key)
        => new QuickSqlJoinQuery<T1, T2, T3>(Engine.Order(key, descending: false, append: true));

    public IQuery<T1, T2, T3> ThenByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> key)
        => new QuickSqlJoinQuery<T1, T2, T3>(Engine.Order(key, descending: true, append: true));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, CancellationToken ct = default)
        => new QuickSqlQuery<TResult>(Engine.Project(projector)).ToListAsync(ct);
}
