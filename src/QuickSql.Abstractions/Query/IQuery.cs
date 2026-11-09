using System.Linq.Expressions;
using QuickSql.Sql;

namespace QuickSql.Query;

/// <summary>分页总数算法。五值乘 provider 能力矩阵决定实际用哪个。</summary>
public enum CountMethod
{
    Auto = 0,
    CountStar = 1,
    /// <summary>COUNT(*) OVER() 一次往返拿全（需要 Count_Over_Window 能力位）。</summary>
    CountOverWindow = 2,
    /// <summary>只取 size 加 1 行判 HasNext，不算总数。</summary>
    SkipTotal = 3,
    /// <summary>分离两条语句但在同一连接同一事务内，避免读写分离把 Count 打到延迟从库。</summary>
    SeparateTransaction = 4,
}

public sealed record PagedOptions(CountMethod CountMethod = CountMethod.Auto, bool StripOrderingForCount = true, string? CacheKey = null);

/// <summary>分页语法本身要求 ORDER BY 的库（T-SQL 的 OFFSET..FETCH、DM 的 ROWNUM），
/// 在查询没给序时怎么办。默认 Throw，因为"补什么序"是语义决定（补主键？补创建时间？），
/// 归约层不知道业务想要哪个，静默补一个就是把不确定的行序伪装成确定的分页结果。
/// 放在模型落位处而不是方言里：方言只负责在策略已经放行时补占位序（ISqlDialect.PagingRequiresOrdering）。</summary>
public enum PagingOrderingPolicy
{
    /// <summary>抛 QuickSqlException，消息里点名这一页需要的序。</summary>
    Throw = 0,

    /// <summary>用实体主键升序补上（无主键则退回 Throw）。给"就是要一个稳定页、序无所谓"的列表接口。</summary>
    AutoOrderPrimaryKey = 1,

    /// <summary>不补序，交给方言写 ORDER BY (SELECT NULL) 占位。数据库不保证行序，翻页可能重复或漏行。</summary>
    Allow = 2,
}

/// <summary>required 加 init 是本仓库的语法基线：值语义，漏字段就是编译错误。</summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Page { get; init; }

    public required int Size { get; init; }

    /// <summary>SkipTotal 档下为 null，前端文案要降级成"下一页"。</summary>
    public required int? Total { get; init; }

    public bool HasNext { get; init; }

    public int TotalPages => Total is { } t && Size > 0 ? (t + Size - 1) / Size : 0;
}

/// <summary>Explain() 的产物，不依赖连接。改写类钩子的每一步都在这里可回放。</summary>
public sealed record QueryPlan(
    Sql.Ast.SelectStatement Ast,
    Sql.DialectSql Sql,
    string ModelText,
    IReadOnlyList<Aop.HookTrace> HookReplay)
{
    /// <summary>内联字面量要问 IValueBinder，所以计划自带 provider：
    /// AST 与 SQL 本来就是某一个方言的产物，带它不算跨层。</summary>
    public required Providers.IDbProvider Provider { get; init; }

    /// <summary>参数内联的可读 SQL。只读 Sql.Text 是不够的：
    /// 里面有 @p0，DBA 拿到没法直接执行。</summary>
    public string ToLogSql() => Sql.ToLogSql(Provider);

    public override string ToString()
        => ModelText + Environment.NewLine + Sql.Text + Environment.NewLine
           + string.Join(Environment.NewLine, Sql.Parameters.Select(p => $"  {p.Name} = {p.Value ?? "NULL"}"));
}

/// <summary>读侧契约。不实现 IQueryable：链式是唯一入口，
/// from-join-let 查询语法因此不可用，联表只走 LeftJoin/InnerJoin。
/// 留在契约里的只有表达式形态的 lambda 与 IAsyncEnumerable，
/// 两者都在共享框架内，所以本包零外部依赖、Domain 层单引这一包即可。没有 Clone()。</summary>
public interface IQuery<T> : IAsyncEnumerable<T>
{
    IOrderedQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> key);

    IOrderedQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key);

    /// <summary>按名字排序——给"排序键是运行期才知道的一段文本"的调用方（页面表头点了哪一列、
    /// 导出任务挑的排序字段、查询串上整串过来的 "Id,Name desc"）。它不是 ORDER BY 片段的入口：
    /// 那颗串只拿去认列（先属性名、再列名、最后大小写不敏感的唯一命中），
    /// 认出来发的是契约里那一颗列加方言引号，认不出当场抛。没有"原样拼进去"的退路，所以这一格
    /// 不需要黑名单：这一条路上"用户文本进 SQL"没有对应的代码路径——从外部来的排序键写错时症状
    /// 是页面顺序不对，没人会为此报修，所以它只能响、不能猜。
    /// 形状：逗号分颗，每一颗是名字或名字加 asc/desc（大小写不敏感），空颗跳过；descending
    /// 给的是串里没点方向的那些颗，写了方向的以串为准；认不出方向词的那一段整颗按列名去认，
    /// 所以 "desc" 单独一颗指的是名叫 desc 的那一列。
    /// 排多颗可以一次给一串，也可以在 ThenBy(string) 上逐颗追加，两种写法发的列序都是书写序。
    /// 只落在单表链这一档：联表链上"一个名字属于哪张表"要先有别名规则，那是另一格。</summary>
    IOrderedQuery<T> OrderBy(string sortSpec, bool descending = false);

    IQuery<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>条件成立才贴这一条谓词：给"筛选条件是可选的"那一类调用方（检索表单里用户没填的那一栏）。
    /// condition 为假时返回自己，一个字节的模型都不改，所以它与"根本没写过这一句"逐字同形
    /// （模型文本、发出去的 SQL 与参数编号都不变）——连腿里的 lambda 都不翻译，
    /// 闭包成员求值那一步根本不会发生。它不是开关而是省一段 if：真档与 Where 同一颗咽喉，
    /// 翻译失败照样在编译期之前响；多条可选条件就是连着写几句，AND 的顺序 = 书写的顺序。</summary>
    IQuery<T> WhereIF(bool condition, Expression<Func<T, bool>> predicate);

    IQuery<T> AsNoTracking();

    /// <summary>这一条链上不套用 orm.Filters 上注册的全局过滤器。
    /// 它是读侧唯一的旁路口，而且不是万能的：注册时写了 mandatory 的那一条在这里直接抛——
    /// 多租户隔离不能靠"约定大家都记得加条件"，而一个能被随手关掉的隔离条件就是那种约定。
    /// 只落在单表链这一档：联表链上没有这个口子（联表之后想跳过过滤器只能回到单表链重起一条）。
    /// 它自己在 QueryModel 里不留痕迹——反过来说，戴过滤器那条链的 Explain 模型文本里会多出那几颗谓词
    /// （要的正是这件事能被看见），而这一条链的模型文本与"这个 orm 实例从没注册过过滤器"逐字节相等。
    /// 它与 IgnoreQueryFilter 差一个 s，不是手误：这一颗的字面照抄 EF Core 的同名口、复数留着，
    /// 按名那一颗是本库自造的口、单数；两颗是同一件事的两种射程。</summary>
    IQuery<T> IgnoreQueryFilters();

    /// <summary>这一条链上只跳过注册键正是 TFilter 的那几条全局过滤器，其余照旧贴。
    /// 参数吃的就是 orm.Filters.Add 的那颗键（实体，或它实现的接口/基类），
    /// 判据是键的同一性而不是继承关系：注册在 ISoftDelete 上的那一条要点 ISoftDelete 才跳得掉，
    /// 点名实体碰不到它——反过来，点名一个接口也碰不到注册在实现类上的那些条。
    /// 这一档窄是有理由的：一族式的点名会让"我只想关软删那一条"静默地少掉一个租户条件，
    /// 而那正是 mandatory 要拦的失败形态，拦不住的地方就不该提供口子。
    /// 它与 IgnoreQueryFilters 是同一颗口的两种射程，可以叠着写；"没点名的也跳"在这里没有对应物，
    /// 要说"全部"就写那一句。mandatory 照旧生效：被点名的那一条注册时写了 mandatory 就在这里抛，
    /// 没被点名的不受影响；同样只落在单表链这一档。</summary>
    IQuery<T> IgnoreQueryFilter<TFilter>() where TFilter : class;

    /// <summary>把另一条同型的查询并进来（UNION ALL，不去重）：两腿各自完整发一句
    /// SELECT，整串占一个表位（与分表读侧同一种形状）。序与分页只长在外层——腿里点了
    /// OrderBy/Skip/Take 当场拒（"每腿各取 N 行"与"合并后取 N 行"是两种语义，本家不替调用方挑，
    /// 也不照另一家 ORM 那样把分页悄悄复制进每一腿）；急切加载与分表腿同样当场拒
    /// （前者的列清单会变，后者选片之后自己已经是一只 UNION）。
    /// 全局过滤器落在合并后的整串上（结果与按腿过滤一致）；
    /// 腿上点名的 IgnoreQueryFilters 当场拒——免过滤的口径要在整条链上定，不给"点了名却
    /// 被外层压掉"的暗门。</summary>
    IQuery<T> UnionAll(IQuery<T> other);

    /// <summary>同 UnionAll，关键字换 UNION：整句去重。两腿重叠的行只留一份——
    /// 去重发生在合并之后、外层谓词与序之前，与 SQL 的子句次序一致。</summary>
    IQuery<T> Union(IQuery<T> other);

    /// <summary>同 UnionAll 的链路与腿守卫，关键字换 INTERSECT：整串交集——
    /// 两腿都出现的行（各腿先去重、再求交）。序与分页同样只长在外层。</summary>
    IQuery<T> Intersect(IQuery<T> other);

    /// <summary>同 UnionAll 的链路与腿守卫，关键字换 EXCEPT：整串差集——
    /// 第一腿有、第二腿没有的行（各腿先去重、再求差）。四种集合运算里唯一一颗有方言分岔：
    /// Oracle 血统（含达梦/神通）发 MINUS，语义同格。</summary>
    IQuery<T> Except(IQuery<T> other);

    IQuery<T> AsSplitQuery(int navigateBatchSize = 20);

    IQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> projector);

    /// <summary>急切加载集合导航属性（to-many）。与 Include 那一家分得很开，理由是形状不同：
    /// Include 是一条 LEFT JOIN 把整只引用实体随主查询带回来，而把同一写法用在集合边上会
    /// 按明细行数复制每一行主表——页码、行数与聚合一起改语义。
    /// 所以这一颗不动主查询 SQL：主查询执行完再发一条按"外键 IN 父端主键集合"的 SELECT，
    /// 在内存里缝回父实体的集合属性；父端为空时那条子查询整个跳过（不发空 IN）。
    /// 返回集合档链式接口，可以再往下一层挂 ThenIncludeMany，下一层的父端就是上一层刚缝出来的那一批。
    /// 外键发现按约定三档：子实体上指向父类型的引用导航、集合边上点名的 ForeignKey、列名"父类型名加 Id"。
    /// 子行的序接 OrderByMany 那颗扩展口（排在 ThenIncludeMany 之前就是给前一层点序），发射在这一层
    /// 子 SELECT 的 ORDER BY 上、主查询与不发序时逐字节同形。TDetail 要 class：它就是子实体，
    /// 集合导航的元素型永远是实体。</summary>
    IIncludableCollectionQuery<T, TDetail> IncludeMany<TDetail>(Expression<Func<T, IEnumerable<TDetail>>> navigation)
        where TDetail : class;

    /// <summary>显式别名，取代按形参名找表。</summary>
    IQuery<T> As(string alias);

    /// <summary>根表位的 FROM 换成一整段自己写的 SELECT：
    /// 外面这一层仍然由库端——Where/OrderBy/Select 照契约出列，内层那一段一个字不动，
    /// 两边合成同一个 DialectSql，所以日志、AOP、连接复用、超时与链式同一条链。
    /// args 的口径与 orm.Ado 的字符串口同一份实现：参数名 = 本家前缀 + 属性名，
    /// 文本里的占位符要写成同形的那一颗；位置式那一家当场拒，参数名占了生成号段（@p0 那一形）也当场拒。
    /// 内层必须交回契约里那些列名（SELECT * 那形天然成立）。
    /// 只落在单表链这一档：联表链上没有这一颗口。</summary>
    IQuery<T> WithSql(string sql, object? args = null);

    /// <summary>分表实体的按时间范围选片：起与止各自下整到本档的周期（月档就是月初），
    /// 中间逐周期展开成这一次要读的片清单。链上后面的 Where/OrderBy/Skip/Take 都在合并之后才落，
    /// 所以序与分页是整段区间的事、不是每片各页。
    /// 只有一片时不包 UNION ALL 那一层：FROM 直接换成那张物理表，句子与不分表的实体逐字同形。
    /// 没有"不给范围就读全部片"的退路：现存的分片有几张只有库知道，而一句静默的 UNION 三百张日表
    /// 是要压在页面上的事故、不是优化。跨片上的聚合/分组/连接一律当场拒（CountAsync 也是聚合），
    /// 分页只要"有没有下一页"时传 CountMethod.SkipTotal。
    /// 在不分表的实体上调它直接抛；只落在单表链这一档。</summary>
    IQuery<T> SplitTables(DateTime from, DateTime to);

    /// <summary>分表实体的按桶号选片：给哪几个桶就读哪几张表，
    /// 重复桶号去掉，越界的桶号抛。与时间档同一句纪律——不给"全部桶"的顺口感，桶数在实体上写着，
    /// 真要全量就自己按桶批过去。哈希取模用的不是 GetHashCode（那个随进程变，会把同一行在两次运行里
    /// 送到两张表），口径见 SplitKind.Hash。</summary>
    IQuery<T> SplitTables(params int[] buckets);

    /// <summary>这一条链点名落在主库上。它与"三格自动强制主库"是同一颗开关的
    /// 两面：事务内、同一条执行流上这座集群写过、这座集群一个从库都没配——那三格不用点名也走主库，
    /// 这一颗是给"我知道我从库上此刻读不到我要的那一行"的调用方用的（刚切完复制、批处理尾巴上的核对读）。
    /// 它不改 SQL 文本：点的只是连接，所以 Explain 的模型文本与不点的那一条逐字相等，
    /// 而"这句实际走了哪条"在 CommandLogEvent.Route 那一格里看得见（那才是它的验收面）。
    /// 只落在单表链这一档。</summary>
    IQuery<T> FromPrimary();

    /// <summary>这一条链点名落在从库上。它是三格强制主库的显式反命，因此有两处
    /// 当场抛而不是静默改道：在这条事务里点它（事务内的读必须落在被这条事务独占的那条连接上，
    /// 换一条去读说的就不是这个事务里的数据）、以及这座集群根本没配从库（"你要从库而这里没有"落不了地，
    /// 把它悄悄收成主库就是"看起来能用"那一类，而那条路的症状是容量与延迟，不是报错）。
    /// 没有权重、没有延迟探测：从库按配置给出的顺序轮询，
    /// 而复制延迟本家测不到（没有承接它的 SPI 槽位），所以这一颗要读"够新的"数据时请改用
    /// FromPrimary。</summary>
    IQuery<T> FromReplica();

    IQuery<T> WithCache(string cacheKey, TimeSpan ttl);

    IQuery<T> Distinct();

    IQuery<T> Skip(int count);

    IQuery<T> Take(int count);

    /// <summary>联表的 arity 递增形态。on 的形参按 FROM 序列位置解析，允许截断尾部形参。</summary>
    IQuery<T, T2> LeftJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class;

    IQuery<T, T2> InnerJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class;

    QueryPlan Explain();

    string ToSql();

    string ToSql(out IReadOnlyList<Sql.AdoParameter> parameters);

    string ToLogSql();

    Task<List<T>> ToListAsync(CancellationToken ct = default);

    /// <summary>ToListAsync 的数组形态：同一颗咽喉——同一条模型、同一句 SQL、
    /// 同一处跟踪与缓存落点、同样只发一条语句，换的只是交回来的 CLR 形状。它不省一趟、反而多一次
    /// 整段拷贝：物化那一路落的本来就是 List，缓存里存的也是那一只，要"少一次拷贝"的是 ToListAsync；
    /// 这一颗值的是交出去之后改不动——长度定死，而且拿到的不是缓存里那一只，
    /// 调用方 Add/Clear 不会顺着改到下一次命中交回来的结果。
    /// 同步孪生口是 ToArray。
    /// 只落在单表链这一档：联表链上那几颗带投影的物化没有对应物，要数组就 Select 回到单表链再调这一颗。</summary>
    Task<T[]> ToArrayAsync(CancellationToken ct = default);

    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);

    /// <summary>单行出口的"没有行算错误"档：FirstOrDefaultAsync 的孪生，差别只在空集——
    /// 那一颗给 default，这一颗抛。执行档同一条：句子上限 1 行，
    /// 所以多行的链上它也不会把整表搬回来。只落在单表链这一档。</summary>
    Task<T> FirstAsync(CancellationToken ct = default);

    /// <summary>单行出口的"恰一行"档：句子上限 2 行（判重档）—— 0 行与多行都是错误、抛。
    /// 多行那一条抛的是"链条算错了"而不是"取第一行"：
    /// 要"多条里取一条"请先 OrderBy 加 FirstAsync。
    /// "没有行给 default"的那一档是 SingleOrDefaultAsync。</summary>
    Task<T> SingleAsync(CancellationToken ct = default);

    /// <summary>SingleAsync 的"没有行给 default"档：0 行交回 default，
    /// 多行照旧抛（"恰一行"的判据不因缺省而放宽——多行永远是链条算错的信号）。</summary>
    Task<T?> SingleOrDefaultAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>存在性出口，计数一族的第三颗。发的是"SELECT 1 取第一行"的存在性探针：
    /// COUNT 在大表上是全量计数，而存在性只要库交出第一行就能收口。它与 CountAsync 同一条
    /// 咽喉，所以分组、Distinct、带 Skip/Take 的链上判的是同一批行；那条链带页时"有没有"
    /// 问的是那一页有没有，不是整表。</summary>
    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>全称量词：链上每一行都满足谓词才是 true。
    /// 发的句子是数出"不满足"的行——谓词取反并入 WHERE 再计数，计数为 0 即全称成立
    /// （谓词为 NULL 的行按"通过"计，与三值逻辑一致）。
    /// 它与 CountAsync 走同一条聚合咽喉，分组、Distinct、带 Skip/Take 的
    /// 链上判的是同一批行。</summary>
    Task<bool> AllAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);

    /// <summary>计数一族的第四颗，与 CountAsync 同一个咽喉、同一档派生表口径。
    /// 它不改变发出去的句子：库里那一列本来就是整数，所以这一颗与 Count 发同一句
    /// COUNT(*)，换的只是读回来的 CLR 形状（long）。存在的理由只有一条——
    /// 行数过了 int 范围时，CountAsync 那一颗读不回来。</summary>
    Task<long> LongCountAsync(CancellationToken ct = default);

    Task<PagedResult<T>> ToPagedAsync(int page, int size, PagedOptions? options = null, CancellationToken ct = default);

    IAsyncEnumerable<T> ToAsyncEnumerable(int batchSize);

    List<T> ToList();

    /// <summary>ToArrayAsync 的同步口，与 ToList 同一档 sync-over-async
    /// （异步可用时优先 ToArrayAsync）。它不是"少付一次拷贝"的那条路：走的仍是同一条
    /// Rows 加末尾那一次拷贝，比 ToList 多的那一趟整段拷贝与异步那颗逐字相同。</summary>
    T[] ToArray();

    T? FirstOrDefault();

    /// <summary>FirstAsync 的同步口，与 ToList 同一档 sync-over-async
    /// （异步可用时优先 FirstAsync）。</summary>
    T First();

    /// <summary>SingleAsync 的同步口，同一档 sync-over-async。</summary>
    T Single();

    /// <summary>SingleOrDefaultAsync 的同步口，同一档 sync-over-async。</summary>
    T? SingleOrDefault();

    int Count();

    /// <summary>AnyAsync 的同步口，与 Count 同一档 sync-over-async
    /// （异步可用时优先 AnyAsync）。</summary>
    bool Any();

    /// <summary>LongCountAsync 的同步口，同一档 sync-over-async。</summary>
    long LongCount();

    /// <summary>AllAsync 的同步口，同一档 sync-over-async。</summary>
    bool All(Expression<Func<T, bool>> predicate);

    /// <summary>判断子查询结果集是否包含指定值。在表达式树内翻译为 target IN (SELECT ...)；
    /// 在表达式树外（运行期直接调用）退化为 FirstOrDefault 判空。</summary>
    bool Contains(T value);

    // ── 分组 ──────────────────────────────────────────────────────────

    /// <summary>按 keySelector 那一列（或那几列）分组，交出分组链。
    /// 返回的不是普通查询：分组之后能选的只有分组键与聚合值，而这个限制由类型系统免费代劳——
    /// 形参是 IGrouping，它除了 Key 与元素序列上的聚合方法之外
    /// 一个实体成员都没有，所以"分组后 Select 里偷偷放非聚合列"那一类事故在这里根本写不出来，
    /// 不必再开一颗运行期校验开关。</summary>
    ISelectGrouping<TKey, T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector);

    // ── 聚合 ──────────────────────────────────────────────────────────

    /// <summary>对指定列求和。表达式树内翻译为标量子查询。</summary>
    TResult Sum<TResult>(Expression<Func<T, TResult>> selector);

    /// <summary>对指定列求最大值。</summary>
    TResult Max<TResult>(Expression<Func<T, TResult>> selector);

    /// <summary>对指定列求最小值。</summary>
    TResult Min<TResult>(Expression<Func<T, TResult>> selector);

    /// <summary>对指定列求平均值。</summary>
    TResult Average<TResult>(Expression<Func<T, TResult>> selector);

    Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);

    Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);

    Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);

    Task<TResult> AverageAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);
}

public interface IOrderedQuery<T> : IQuery<T>
{
    IOrderedQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> key);

    IOrderedQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> key);

    /// <summary>字符串形态的第二颗，与 IQuery.OrderBy(string) 共用同一处拆分与同一处认列
    /// （同一份三档、同一处抛），差别只在它是把串里那些颗追加到已有的序后面。列序 = 调用序，
    /// 这一点与 lambda 那一条同一个口径。</summary>
    IOrderedQuery<T> ThenBy(string sortSpec, bool descending = false);
}

/// <summary>分组链。
/// 它刻意没有 Where、也没有任何物化出口：分组之前的条件属于 IQuery，
/// 分组之后的条件属于 Having；而"分组了却还没选"这条链上的形状（整行加 GROUP BY）
/// 在任何一家都是错句，所以这里连 Explain() 都不给——出口一律在 Select 之后，
/// 拿到普通查询就是普通的 ToListAsync/ToPagedAsync/Explain。</summary>
public interface ISelectGrouping<TKey, TValue>
{
    /// <summary>筛分组。lambda 的形参是 IGrouping，
    /// 于是能写的只有 Key 与聚合方法这一族——谓词落进 HAVING 而不是 WHERE，
    /// 这一点由"它在 GroupBy 之后"决定，不由书写顺序决定。多条 Having 以 AND 叠加。</summary>
    ISelectGrouping<TKey, TValue> Having(Expression<Func<IGrouping<TKey, TValue>, bool>> predicate);

    IOrderedSelectGrouping<TKey, TValue> OrderBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);

    IOrderedSelectGrouping<TKey, TValue> OrderByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);

    /// <summary>分组链上的分页：页与组同一单位（ORDER BY 加 OFFSET/FETCH 落在分组之后的结果集上）。
    /// 序的缺省策略照旧；但"无序时补主键"在分组上没有对应物（主键不在分组键里），会当场报错而不是补一条错句。</summary>
    ISelectGrouping<TKey, TValue> Skip(int count);

    ISelectGrouping<TKey, TValue> Take(int count);

    /// <summary>选出来就是最终形状：DTO / 匿名类型 / 一颗标量。
    /// 投影列只能读 Key（复合键读 Key.成员）与聚合，其余在 IGrouping
    /// 上根本不存在，所以编译期就过不去。</summary>
    IQuery<TResult> Select<TResult>(Expression<Func<IGrouping<TKey, TValue>, TResult>> projector);
}

/// <summary>分组链的有序形态。与 IOrderedQuery 同一个理由：类型区别只为让 ThenBy
/// 出现在能接它的对象上，OrderBy 之后再 OrderBy 会替换前一串，所以那一颗口不在这层暴露。</summary>
public interface IOrderedSelectGrouping<TKey, TValue> : ISelectGrouping<TKey, TValue>
{
    IOrderedSelectGrouping<TKey, TValue> ThenBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);

    IOrderedSelectGrouping<TKey, TValue> ThenByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);
}

/// <summary>联表后的多表查询。类型参数集合就是"已进 FROM 的表"，编译期挡住引用未 join 的表。</summary>
public interface IQuery<T1, T2>
{
    IQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate);

    /// <summary>条件成立才贴这一条谓词，为假时返回自己；口径同单表链的 WhereIF。</summary>
    IQuery<T1, T2> WhereIF(bool condition, Expression<Func<T1, T2, bool>> predicate);

    IQuery<T1, T2, T3> LeftJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class;

    IQuery<T1, T2, T3> InnerJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class;

    IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, TResult>> projector);

    IOrderedJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> key);

    IQuery<T1, T2> Skip(int count);

    IQuery<T1, T2> Take(int count);

    QueryPlan Explain();

    string ToSql();

    string ToLogSql();

    Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default);

    Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default);
}

public interface IQuery<T1, T2, T3>
{
    IQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate);

    /// <summary>条件成立才贴这一条谓词，为假时返回自己；口径同单表链的 WhereIF。</summary>
    IQuery<T1, T2, T3> WhereIF(bool condition, Expression<Func<T1, T2, T3, bool>> predicate);

    IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, T3, TResult>> projector);

    IOrderedJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key);

    IQuery<T1, T2, T3> Skip(int count);

    IQuery<T1, T2, T3> Take(int count);

    QueryPlan Explain();

    string ToSql();

    string ToLogSql();

    Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default);
}

public interface IOrderedJoinQuery<T1, T2>
{
    IQuery<T1, T2> ThenBy<TKey>(Expression<Func<T1, T2, TKey>> key);

    IQuery<T1, T2> ThenByDescending<TKey>(Expression<Func<T1, T2, TKey>> key);

    QueryPlan Explain();

    string ToSql();

    Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default);
}

public interface IOrderedJoinQuery<T1, T2, T3>
{
    IQuery<T1, T2, T3> ThenBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key);

    IQuery<T1, T2, T3> ThenByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> key);

    QueryPlan Explain();

    string ToSql();

    Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, CancellationToken ct = default);
}
