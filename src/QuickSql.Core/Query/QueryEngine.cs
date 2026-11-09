using QuickSql.Aop;
using QuickSql.Execution;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Query.Filters;
using QuickSql.Query.Model;
using QuickSql.Sql;
using QuickSql.Tracking;
using QuickSql.Translation;
using QuickSql.Write;
using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using QuickSql.Sql.Ast;

namespace QuickSql.Query;

/// <summary>读侧唯一的算子落点。算子只在这里实现一次：链式对象每个方法都只是类型化的转发，
/// 于是「同一条查询在不同 arity 上产出的模型一致」不是两边各写一遍再测相等，而是只有一个实现。
/// 这里也不产生任何 SQL 文本：出口在 Build，它只读 Model。</summary>
[RequiresUnreferencedCode("lambda 归约与闭包常量求值要反射读成员。")]
[RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
sealed class QueryEngine
{
    readonly string[] _tables;

    /// <summary>这条链要去的那座集群 = 根表位的声明。读"根"而不是读"全部表位"是有配套的：
    /// 跨集群的连接与急切加载在编译口就拒掉了，所以能走到执行的句子只可能说一座集群的名字。
    /// 集合导航的第二条查询不受这一格约束：它是一条独立的 SELECT，根表位是子实体自己，
    /// 于是"父在 A 库、子在 B 库、内存里缝起来"天然成立——那正是分库要的东西，不是漏洞。</summary>
    readonly string _cluster;

    /// <summary>会话的跟踪器，null 表示这一条链不跟踪（orm.Select&lt;T&gt;() 与 AsNoTracking() 之后都是 null）。
    /// 跟不跟踪挂在引擎上而不是模型里，于是链式与 LINQ 产出的模型文本逐字节相等（模型里没有跟不跟踪这件事）。
    /// 它跟着 Derive 走，所以 Where/OrderBy/Skip 派生出来的每一段都还是同一个会话的那份身份映射。</summary>
    readonly ChangeTracker? _tracker;

    /// <summary>IgnoreQueryFilters() 的落点。它跟着 Derive 走，所以"这一条链不戴全局过滤器"
    /// 是一句说一次就全程有效的话；它不进模型：模型是查询的形状，戴不戴过滤器是这一次执行的决定。
    /// 形状是一颗可空清单而不是一颗 bool：清单里是被点名跳过的那些注册键（"全部"那一句递进来的是唯一一颗
    /// object），null 是"没点过名"。null 那一档是热路径：没点过名的链一次都不进 AssertIgnorable，
    /// 也不为这一格多付一次分配。</summary>
    readonly IReadOnlyList<Type>? _ignoredFilters;

    /// <summary>分组链的翻译侧形状。它不进模型：SQL 侧的 GROUP BY 表达式已经在模型里，
    /// 这里只留"g.Key 落回哪颗表达式、g.Sum 的 o 绑到哪张表"这一件翻译时才要用的事。
    /// 它跟着 Derive 走：分组之后的 Having/OrderBy/Select 都是派生出来的新引擎，
    /// 而它们的 lambda 形参是 IGrouping，没有这一格就绑不出作用域。</summary>
    readonly GroupingShape? _grouping;

    /// <summary>这条链上已经按引用导航自动补进 FROM 的那几张表。补出来的那条 LEFT JOIN 本身已经在模型的
    /// Joins 里（查询形状，进规范文本），这一份留的只是"宿主那一格的这根边已经联过"这条翻译时才要用的对照，
    /// 为的是下一颗 lambda 再读同一条导航时复用同一个表位。它是只读清单，不是可变登记表：
    /// q.Where(导航1) 与 q.Where(导航2) 两个分叉各自只比自己那一条——共享一只可变字典的话，
    /// 第二个分叉会看见第一颗 lambda 补的那张表位并复用之，而它自己那份 FROM 里没有那张表，
    /// 得到的是一句引用了不存在表位的错句。所以每颗 lambda 一份现场（Scout），落地时才延长这一份。</summary>
    readonly IReadOnlyList<NavigationJoin> _navigation;

    /// <summary>会话级模型覆写给的注册表。null = 没覆写，一切契约照旧走组装根那一份——
    /// 没覆写的上下文与裸链一颗字节都不多付。非 null 时取契约、自动联表造表位、IncludeMany 的
    /// 子型解析三处都改问这一份，三个口共用同一张"覆写在哪"的判据，不猜。</summary>
    internal Schema.IEntitySchemaRegistry? Schemas { get; }

    /// <summary>会话的那颗并发闸。null 就是根链——根链没有会话，不进闸。
    /// 闸口收在七个公开执行口上（List/Array/Capped/Aggregate/Any/Paged/Stream），
    /// 内部路径（私有 Rows、QueryInBatchesAsync、TotalCount）都不再进闸，
    /// 所以闸内嵌套不存在——拆分 include 的子读与分页的 count 都在同一个被闸住的操作里发生。</summary>
    readonly SessionGate? _gate;

    public QueryEngine(QuickSqlOrm orm, QueryModel model, ChangeTracker? tracker = null,
        IReadOnlyList<Type>? ignoredFilters = null, GroupingShape? grouping = null,
        IReadOnlyList<NavigationJoin>? navigation = null, SessionGate? gate = null,
        Schema.IEntitySchemaRegistry? schemas = null)
    {
        Orm = orm;
        Model = model;
        _tracker = tracker;
        _ignoredFilters = ignoredFilters;
        _grouping = grouping;
        _navigation = navigation ?? [];
        _gate = gate;
        Schemas = schemas;
        _tables = [.. model.Tables.Select(t => t.Table.Name)];
        _cluster = DataSourceRouting.ClusterOf(model.Root.Table);
    }

    public QuickSqlOrm Orm { get; }

    public QueryModel Model { get; }

    /// <summary>会话的并发闸（可为 null——根链没有会话）。DbSet.Load 那条补载路
    /// 不经七个执行口（它直接走 QueryInBatchesAsync），所以由 DbSet 在 LoadAsync 入口自行进闸；
    /// 内部其余路径一律不摸这一格，闸内无嵌套。</summary>
    internal SessionGate? Gate => _gate;

    /// <summary>这条链上被点名跳过的过滤器清单（null = 没点过名）。合并链的形状守卫读它：
    /// 过滤器名单不进模型，落在腿上的 IgnoreQueryFilters 在合并之后会被外层过滤器静默压掉——
    /// 守卫探到它就拒，不给"点了名却不生效"的暗门。</summary>
    internal IReadOnlyList<Type>? IgnoredFilters => _ignoredFilters;

    public QueryEngine Derive(QueryModel next) => new(Orm, next, _tracker, _ignoredFilters, _grouping, _navigation, _gate, Schemas);

    /// <summary>分组那一步专用：模型与形状同时换。不写成 Derive(next) with grouping——
    /// 形状在引擎上而不在模型上，派生一次只会把刚立起来的分组丢掉。</summary>
    QueryEngine Derive(QueryModel next, GroupingShape grouping) => new(Orm, next, _tracker, _ignoredFilters, grouping, _navigation, _gate, Schemas);

    /// <summary>这一颗 lambda 的翻译现场：一份只读的已联清单 + 从进来的表位数往后长的位置。
    /// QueryModel 是不可变的，所以补出来的联表只能欠着，由算子决定它落进哪一份新模型——
    /// 这就是它每颗 lambda 新建一颗、而不是挂在引擎上的理由。没有 to-one 边的那一张表不建现场（返回 null）：
    /// 单表链上唯一的宿主就是那张表，它的契约里没有引用导航，就没有任何一次 Resolve 问得出结果——
    /// 而这一颗是每算子一份的，这笔钱只由真用得着的人付。两张表以上照旧建：联进来的任何一张都可能是宿主。</summary>
    NavigationJoinScout? Scout(QueryModel model)
        => model.Tables is [var only] && only.Contract?.Relationships is { Count: 0 }
            ? null
            : new NavigationJoinScout(_navigation, model.Tables.Count, TableRefs, model);

    /// <summary>造表位的委托：没覆写时用组装根那只按实例缓存过的；有覆写时每引擎现包一颗
    /// （覆写链是少数派，那颗缓存税由用得上的人付）。</summary>
    internal Func<Type, int, TableRef> TableRefs => Schemas is null
        ? Orm.TableRefFactory
        : _contextTableRefs ??= (clrType, index) => Orm.NewTableRef(clrType, index, Schemas);

    Func<Type, int, TableRef>? _contextTableRefs;


    /// <summary>把这一颗 lambda 欠下的自动联表落地，并把它记进链级那份清单。
    /// 两处要一起改：只落模型则下一颗 lambda 认不出"已经联过"而联第二张；只记清单则这一颗的 ON
    /// 根本没进 FROM，发出去的句子引用一张不存在的表。没有欠账时走 Derive，一条数组拷贝都不付。</summary>
    QueryEngine Advanced(NavigationJoinScout? scout, QueryModel model, GroupingShape? grouping = null)
        => scout is { HasPending: true }
            ? new(Orm, scout.Flush(model), _tracker, _ignoredFilters, grouping ?? _grouping, [.. _navigation, .. scout.Pending], _gate, Schemas)
            : grouping is { } shape ? Derive(model, shape) : Derive(model);

    // ── 算子 ──────────────────────────────────────────────────────────

    public QueryEngine Where(LambdaExpression predicate)
    {
        Filterable();

        var scout = Scout(Model);
        var node = Orm.Expressions.Predicate(predicate, Scope.Create(Model, predicate, scout));

        return Advanced(scout, Model.AddPredicate(node));
    }

    public QueryEngine Project(LambdaExpression projector)
    {
        var scout = Scout(Model);
        var plan = Orm.Projections.Translate(projector, Binding(projector, scout));

        // 分组链与 DISTINCT 两格各拦一句：它们都是"发得出去、绿得动、而答案是错的"那一型，不是翻译不出来。
        // 判在翻译之后：形状先照读侧同一条尺子翻，翻完才知道选列里有没有 OVER。
        if (Model.GroupBy.Count > 0 && WindowGuard.HasRowNumber(plan))
            throw RowNumberOnGroupedChain();

        if (Model.Distinct && WindowGuard.HasRowNumber(plan))
            throw DistinctWithRowNumber();

        return Advanced(scout, Model with { Plan = plan });
    }

    /// <summary>分组链的投影位不落行号。OVER 的键要么本身是组级表达式、要么进 GROUP BY，
    /// 而这两半在本库今天都没有判据可分（发出去得到的是"能执行、数的范围却不对"的那一句），所以整格停住。</summary>
    static QuickSqlException RowNumberOnGroupedChain() => new(
        "分组链的投影里不落行号：OVER 的键要么写成组级表达式、要么进 GROUP BY，而这一格本库不替你猜要哪一半——" +
        "猜错的那一半是一句能执行、数的范围却不对的句子（§5.8）。" +
        "组内编号今天走 WithSql 或原生 SQL（文档站 14 篇）；组与组之间排号请用分组之后的 OrderBy。");

    /// <summary>DISTINCT 与行号同框的拒绝：行号把每一行都变成唯一行，DISTINCT 因此在库里静默变成空操作
    /// 且一句报错也不会有。两个书写顺序（先 Distinct 后 Select ∶ 先 Select 后 Distinct）
    /// 拿到的是同一句话，所以这一颗被 Project 与 Distinct 各调一次。</summary>
    static QuickSqlException DistinctWithRowNumber() => new(
        "DISTINCT 与行号不同框：行号把每一行都变成唯一行，DISTINCT 因此在库里静默变成空操作——" +
        "去重一次都没发生，而一句报错也不会有（§5.8 禁的正是这一格）。" +
        "要真去重就别把行号选进来；要先去重再编号，把内层那句交 WithSql 或走原生 SQL（文档站 14 篇）。");

    /// <summary>分组。键的表达式落进模型 GroupBy（SQL 侧的真相，Explain() 的文本里有它），
    /// 而「g.Key 怎么落回去」这份 CLR 侧的对照表留在引擎的 _grouping 上。
    /// 投影在这里刻意不动：分组链的出口只有 Select，而 GroupBy 到 Select 之间还要接 Having 与分组序，
    /// 那两步的 lambda 都要求作用域里已经有这一格。</summary>
    public QueryEngine GroupBy(LambdaExpression keySelector)
    {
        if (Model.GroupBy.Count > 0)
            throw new QuickSqlException("同一条链上只能分组一次：再 GroupBy 一次要么替换掉前一次、要么套成派生表，两者本版本都没有对应物。要按更多列分组请写成 GroupBy(o => new { o.A, o.B })。");

        if (Model.Plan is not EntityProjection entity)
            throw new QuickSqlException("投影之后不能再分组：投影列已经离开列契约，分组键无处可落。把 GroupBy 挪到 Select 之前。");

        if (Model.Offset is > 0 || Model.Limit is not null)
            throw new QuickSqlException("带 Skip/Take 的查询要先包成派生表再分组（每组是一行，页与组的单位在这一版接不上），本版本未接入（§5.12）。请在未分页的查询上分组。");

        if (Model.Ordering.Count > 0)
            throw new QuickSqlException(
                $"分组之前排的序（{Model.Ordering.Count} 颗）到分组之后没有对应物：组里的行不再有先后。请把 OrderBy 挪到 GroupBy 之后（那时它排的是组，键与聚合都能用）。");

        var table = Model.Tables[entity.TableIndex];
        var scout = Scout(Model);
        var scope = Scope.Create(Model, keySelector, scout);
        var body = UnwrapConvert(keySelector.Body);

        // 复合键 = "把几颗键构造成一个形状"。匿名类型的成员名由编译器按实参命名，
        // 所以用户写什么名，g.Key 后面就只能读什么名。
        var members = KeyMembers(body);

        if (members is null)
        {
            var key = Orm.Expressions.Visit(body, scope);

            // 分组键那一格与谓词位同一条闸门：行号是"算完全部行才有"的值，而 GROUP BY 问的正是"按什么分行"。
            WindowGuard.RejectRowNumber(key, "GROUP BY 的键");

            return Advanced(scout, Model with { GroupBy = [key] }, new GroupingShape(table, key, null));
        }

        var keys = new List<SqlExpression>(members.Count);
        var map = new Dictionary<string, SqlExpression>(StringComparer.Ordinal);

        foreach (var (name, expression) in members)
        {
            var node = Orm.Expressions.Visit(expression, scope);

            WindowGuard.RejectRowNumber(node, "GROUP BY 的键");

            keys.Add(node);
            map[name] = node;
        }

        // 同一个成员名出现两次（MemberInit 那形写得出）：
        // 键的顺序照原样进 GROUP BY，而 g.Key.<名字> 只能落一个，取后写的那一颗会静默改掉用户的意思，所以直接报错，不猜。
        if (map.Count != members.Count)
            throw new QuickSqlException("复合分组键里有重名的成员：g.Key.<名字> 无法决定落哪一颗。请给它们不同的名字。");

        return Advanced(scout, Model with { GroupBy = [.. keys] }, new GroupingShape(table, null, map));
    }

    /// <summary>分组之后的筛条件。落 Having 而不是 WHERE：这件事由「它在 GroupBy 之后」决定，
    /// 不由书写顺序决定；多条以 AND 叠加，与谓词那一族同样。</summary>
    public QueryEngine Having(LambdaExpression predicate)
    {
        if (_grouping is null)
            throw new QuickSqlException("Having 要在 GroupBy 之后：没有分组键就没有可筛的聚合值。请把这一条放到 GroupBy 之前的 Where。");

        var scout = Scout(Model);
        var node = Orm.Expressions.Predicate(predicate, Scope.ForGrouping(Model, predicate, _grouping.Value, scout));
        var existing = Model.Having;

        return Advanced(scout, Model with { Having = existing is null ? node : new SqlBinaryExpression(BinaryOperator.And, existing, node) });
    }

    /// <summary>键选择器 lambda 的体是不是"由几颗键构造出来的形状"。是则给出成员名 → 表达式
    /// （顺序即 GROUP BY 的列序），否则 null（单列键）。</summary>
    static IReadOnlyList<(string Name, Expression Value)>? KeyMembers(Expression body) => body switch
    {
        // 匿名类型与具名 DTO 的构造器形状：实参与成员名一一对应（编译器为匿名类型生成的构造器就是这个形状）。
        NewExpression { Constructor: not null, Members: { Count: > 0 } members } @new
            => [.. members.Select((m, i) => (m.Name, UnwrapConvert(@new.Arguments[i])))],

        MemberInitExpression init
            => [.. init.Bindings.OfType<MemberAssignment>().Select(b => (b.Member.Name, UnwrapConvert(b.Expression)))],

        _ => null,
    };

    static Expression UnwrapConvert(Expression node)
        => node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? UnwrapConvert(unary.Operand)
            : node;

    /// <summary>这一条 lambda 的作用域：分组链上形参是 IGrouping，它不是表位，
    /// 所以走 Scope.ForGrouping；其余一律按位置绑表。scout 是这一颗 lambda 的翻译现场（自动联表的欠账）：
    /// 绑作用域这一步自己一个都不补，它只是把"欠着了往哪儿记"递进去——补与落地分在两层，是因为落点只有算子知道。</summary>
    Scope Binding(LambdaExpression lambda, NavigationJoinScout? scout)
        => _grouping is { } grouping
            ? Scope.ForGrouping(Model, lambda, grouping, scout)
            : Scope.Create(Model, lambda, scout);

    public QueryEngine Order(LambdaExpression key, bool descending, bool append)
    {
        if (Model.Plan is not EntityProjection)
            throw new QuickSqlException("投影之后不能再排序：投影列没有列契约，补出来的序会指向错列。把 OrderBy 挪到 Select 之前，或在源查询上排好序再投影。");

        var scout = Scout(Model);
        var order = new OrderExpression(Orm.Expressions.Visit(key.Body, Binding(key, scout)), descending);

        return Append(scout, Model, order, append);
    }

    /// <summary>按名字排序：这一条不经过表达式翻译，串只拿去契约上认列，认出来的是列契约，
    /// 发出去的是那一颗列加方言引号——所以"传进来的文本进了 SQL"这一形在这里没有对应的代码路径。
    /// 拆分只拆名字与方向（SortSpec），一颗都不认列。闸门与叠加与 lambda 那一条共用 Append，
    /// 两条口对同一个投影形状给同一句话。</summary>
    public QueryEngine Order(string sortSpec, bool descending, bool append)
    {
        if (Model.Plan is not EntityProjection)
            throw new QuickSqlException("投影之后不能再排序：投影列没有列契约，补出来的序会指向错列。把 OrderBy 挪到 Select 之前，或在源查询上排好序再投影。");

        var contract = Model.Root.Contract
            ?? throw new QuickSqlException($"根表位 {Model.Root.Table.Name} 没有实体契约，按成员名排不出序：认列靠的就是那份契约。用 lambda 形态，或把这条链的表源换成一张实体表。");

        var keys = SortSpec.Parse(sortSpec, descending);

        if (keys.Count == 0)
            throw NoSortMember(contract);

        var orders = new OrderExpression[keys.Count];

        for (var i = 0; i < keys.Count; i++)
        {
            orders[i] = new OrderExpression(new ColumnExpression(Model.Root, Column(contract, keys[i].Name)), keys[i].Descending);
        }

        return Append(null, Model, orders, append);
    }

    /// <summary>把一颗序落到模型上。追加还是替换由这一处决定：契约上 OrderBy 之后再
    /// OrderBy 是替换前一串（LINQ 的口径），而链式对象上那两颗口各自把 append 写死，
    /// 所以两种形态（lambda 与名字串）不会各写一套叠加规则。scout 与 model 成对进来：
    /// 名字串那一形不经过表达式翻译，没有欠账可落，那一口递 null——两条口在 Advanced 那里合流，
    /// 叠加规则仍然只有一份。</summary>
    QueryEngine Append(NavigationJoinScout? scout, QueryModel model, OrderExpression order, bool append)
        => Append(scout, model, [order], append);

    QueryEngine Append(NavigationJoinScout? scout, QueryModel model, IReadOnlyList<OrderExpression> orders, bool append)
        => Advanced(scout, append ? model.AddOrderings(orders) : model with { Ordering = [.. orders] });

    /// <summary>成员名 → 列，三档逐字节优先：先属性名、再列名、最后大小写不敏感的唯一命中
    /// （第三档是给 HTTP 查询串上 createTime 那一形的，不是给通配的）。认不出与认得多颗都当场抛，
    /// 没有"原样发出去"的退路：那一路的产物是一条看起来合理的错句，而排序键错了在页面上读得出来的
    /// 只有顺序，没人会为此报修。一颗名字与一整串走的是同一句：SortSpec 拆完之后每一颗都从这道咽喉过。</summary>
    static ColumnContract Column(EntityContract contract, string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            throw NoSortMember(contract);

        if (contract.ByMember.TryGetValue(memberName, out var byMember)) return byMember;
        if (contract.ByColumn.TryGetValue(memberName, out var byColumn)) return byColumn;

        var matches = contract.Columns
            .Where(c => string.Equals(c.MemberName, memberName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.ColumnName, memberName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有名为 {memberName} 的列：按名字排只认这张表上映射过的列（属性名或列名），" +
                "导航属性、投影列与 SQL 片段都不认。要排别的表那一侧的列请改用 lambda 形态（读引用导航会自动联表，§5.2）。" +
                "串里带方向只认挂在名字后面的 asc / desc（ascending / descending 同义），其余的每一段都按列名去认。");

        if (matches.Length > 1)
            throw new QuickSqlException($"{contract.Table.Name} 上能对上 {memberName} 的列不止一颗（{string.Join(" / ", matches.Select(m => m.MemberName))}）：" +
                "大小写不敏感这一档撞在了两张列上，而本家不替调用方挑一颗。按逐字节的写法命中（属性名或列名写全），或改用 lambda 形态。");

        return matches[0];
    }

    /// <summary>整串里一颗名字都没有（空串、全逗号、null）时的那一句。写在一处是因为认不出空名的两处
    /// —— 单颗那一形与拆完的空清单——要给同一个读数，消息是本格的交付物而不是细节。</summary>
    static QuickSqlException NoSortMember(EntityContract contract)
        => new($"{contract.Table.Name} 的排序成员名是空的：按名字排只认这一张表上的列，请把要排的那一列的属性名或列名给出来。");

    /// <summary>Skip 只落偏移位、Take 只落行数位，都不在这一步检查序：
    /// Skip().Take().OrderBy() 与 OrderBy().Skip().Take() 必须产出同一个模型，
    /// 所以"有没有序"只能在出口判定，见 Effective。</summary>
    public QueryEngine Skip(int count) => Derive(Model with { Offset = count });

    public QueryEngine Take(int count) => Derive(Model with { Limit = count });

    public QueryEngine Distinct()
    {
        // 写在 Select 之后那一头的写法在这里判，写在 Select 之前那一头的由 Project 那一句接住。
        if (WindowGuard.HasRowNumber(Model.Plan))
            throw DistinctWithRowNumber();

        return Derive(Model with { Distinct = true });
    }

    /// <summary>AsNoTracking() 的落点：模型一个字都不改，只把手里那份会话松开。
    /// 所以它产出的是「同一条查询的无跟踪视图」而不是「另一条查询」。</summary>
    public QueryEngine NoTracking() => new(Orm, Model, tracker: null, _ignoredFilters, _grouping, _navigation);

    /// <summary>「这一条链上没有任何全局过滤器」那一句的落点：递进来的清单里只有 object 一颗，
    /// 于是对每条注册都判真。先问 mandatory 再置位：那条约束要的是"关掉不可忽略的过滤器"这句话当场响，
    /// 而不是安静地生成一条少了一个条件的语句——少一个租户条件这件事在结果集上看不出来。</summary>
    public QueryEngine IgnoreFilters() => IgnoreFilters(typeof(object));

    /// <summary>IgnoreQueryFilter&lt;TFilter&gt;() 的落点：只把 key 递进清单里，其余在册的一条都不动。
    /// 判 mandatory 时递的是叠过这一颗之后的清单而不是这一颗：一条链先 Ignore 一条再 Ignore 另一条，
    /// 第二次要把第一次那份账一起问一遍——只问新来的那一颗的话，一条链上多颗 mandatory 只有第一颗响，
    /// 而这两句在 SQL 上造成的缺口是一样的。</summary>
    public QueryEngine IgnoreFilters(Type key)
    {
        IReadOnlyList<Type> ignored = _ignoredFilters is { } existing ? [.. existing, key] : [key];

        Orm.Filters.AssertIgnorable(Model.Tables, ignored);

        return new QueryEngine(Orm, Model, _tracker, ignored, _grouping, _navigation, _gate);
    }

    public QueryEngine Cache(string cacheKey, TimeSpan ttl) => Derive(Model with { CacheKey = cacheKey, CacheTtl = ttl });

    /// <summary>FromPrimary() / FromReplica() 的落点。与 NoTracking 同一形状：改的是这一次执行的去向，
    /// 模型里的查询形状一个字不动。它不在这里判"能不能"：事务里点从库、没配从库还点从库，
    /// 判据在 DataSourceRouting.Resolve 一处，由执行时的那次取连接兑现——在这里判等于两处各写一套，
    /// 而"这一句到底走了哪条"从此有了两个答案。</summary>
    public QueryEngine Role(ChainChoice choice) => Derive(Model with { Role = choice });

    public QueryEngine Alias(string alias)
    {
        // 别名改的是 FROM 表位，而已翻译的谓词持有旧 TableRef 实例：先加别名再叠条件是安全的，反过来会写出
        // FROM [Orders] AS [o] 配 WHERE [t0].[State] —— 报"无法绑定多部分标识符"，比静默错结果好，但仍要说清楚。
        if (Model.Predicates.Count > 0 || Model.Joins.Count > 0 || Model.Ordering.Count > 0)
            throw new QuickSqlException($"As(\"{alias}\") 要放在链式最前面：表上已叠加 {Model.Predicates.Count} 个谓词/{Model.Joins.Count} 个联表/{Model.Ordering.Count} 个排序，它们的列引用仍指向旧别名。");

        var root = Model.Root with { Alias = alias };

        // 覆盖源里的 TableRef 是 WithSql 那一刻抓住的旧别名实例：不改写它，FROM 挂 "t0" 而谓词引用 "o"，
        // 得到的是一句"无法绑定多部分标识符"。与上面那颗守卫同一个理由，只是这一格的引用藏在表源里。
        var source = Model.Source is SourceRawText raw ? raw with { Ref = root } : Model.Source;

        return Derive(Model with { Tables = [root, .. Model.Tables.Skip(1)], Source = source });
    }

    /// <summary>WithSql：把根表位的 FROM 换成一整段用户自己写的 SELECT，
    /// 链上后面的 Where/OrderBy/Select 仍然照契约端出去——外面这一层是库写的、里面那一段是你写的，
    /// 两边在同一个发射器里，AOP 与日志仍是同一条链。三条口径与 RawSql 完全一致：文本一颗不动；
    /// 参数名 = 本家前缀 + 属性名；位置式占位符那一家当场拒。多一条：参数名不许长成"前缀 + p + 数字"的形状
    /// ——那是 SqlWriter 给生成参数留的号段（编号长在项目参数集合的长度上），
    /// 撞上去的症状是两处绑定抢同一个名字、值串到隔壁列。外面那一层按契约选列，所以内层必须交回契约里那些列名。</summary>
    public QueryEngine WithSql(string sql, object? args)
    {
        var prefix = Orm.Provider.Dialect.ParameterPrefix;
        var folded = RawSql.ToDialectSql(sql, args, prefix);

        foreach (var parameter in folded.Parameters)
            if (IsGeneratedName(parameter.Name, prefix))
                throw new QuickSqlException(
                    $"WithSql 的参数名 {parameter.Name} 占了生成 SQL 的号段（前缀 {prefix} + p + 数字，那里按项目参数集合的长度编号）：" +
                    "同名的两颗会让外层生成的参数与原生文本里的这颗抢同一个绑定。请把它改成不叫 p<数字> 的名字。");

        return Derive(Model with { Source = new SourceRawText(Model.Root, folded.Text, folded.Parameters) });
    }

    static bool IsGeneratedName(string name, char prefix)
    {
        if (name.Length < 3 || name[0] != prefix || name[1] != 'p')
            return false;

        for (var i = 2; i < name.Length; i++)
            if (!char.IsAsciiDigit(name[i]))
                return false;

        return true;
    }

    /// <summary>分表实体的按时间范围选片：起与止各下整到本档的周期，中间逐周期展开成一份片清单，
    /// 落进模型。时间档就是"这段区间里可能有哪几张表"，所以剪枝不是优化而是定义——
    /// 三月查二月那张表里没有的行，与"没查"不是一回事。说一次算一次，后一次覆盖前一次：
    /// 片清单是这一份模型的事实，叠加两份会得到第三种没人要的区间。</summary>
    public QueryEngine SplitTables(DateTime from, DateTime to)
    {
        var split = SplitOrThrow();

        return Derive(Model with { Shards = SplitRouting.EnumerateTime(Model.Root.Table, split, from, to) });
    }

    /// <summary>分表实体的按桶号选片：给哪几个桶就读哪几张，顺序就是 UNION ALL 的分支顺序，重复桶号去掉。
    /// 没有"全部桶"这一颗口：桶数写在实体上，而"把全部桶 UNION 起来"在几百个桶上不是一句可执行的查询，
    /// 给它一个顺手的写法就是给它一条通往线上的路。真要全量统计，就按桶分批或在库侧做（跨片聚合本家不接）。</summary>
    public QueryEngine SplitTables(params int[] buckets)
    {
        var split = SplitOrThrow();

        return Derive(Model with { Shards = SplitRouting.EnumerateBuckets(Model.Root.Table, split, buckets) });
    }

    /// <summary>选片口只给分表实体用：在不分表的实体上调它，得到的不是"整张表"而是一个错——
    /// 那句话会让人以为自己的实体分了片。判据取契约上那一格，不做二次缓存。</summary>
    SplitFacts SplitOrThrow()
        => Model.Root.Table.Split
           ?? throw new QuickSqlException(
                $"实体 {Model.Root.Contract?.ClrType.Name} 没有声明分表（类上没有 [SplitTable]），没有片可选。");

    /// <summary>联表的落点。ON 的最后一个形参就是新表（arity 递增），所以新表的 CLR 类型从它取，
    /// 不需要调用方再报一遍。形参名不参与解析，所以 (o, i) => 与 (x, y) => 产出同一个模型。</summary>
    public QueryEngine Join(JoinKind kind, LambdaExpression on)
    {
        Filterable();

        var detail = on.Parameters[^1].Type;

        if (detail.IsGenericType && detail.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            throw new QuickSqlException("联表的右表是集合导航（IEnumerable<T>）：集合导航的加载是 IncludeMany/子查询的职责，P1（§5.7）。");

        var table = Orm.NewTableRef(detail, Model.Tables.Count);
        var scoped = Model with { Tables = [.. Model.Tables, table] };
        var clause = new JoinClause(kind, new SourceTable(table), Orm.Expressions.Predicate(on, Scope.Create(scoped, on)));

        return Derive(Model.AddJoin(clause, table));
    }

    void Filterable()
    {
        // 投影之后再 Where 只能引用投影列，而投影列不在列契约里——引用不到就报，不猜。
        if (Model.Plan is not EntityProjection)
            throw new QuickSqlException("投影之后不能再加 WHERE：投影列已经离开列契约，继续翻译会静默丢列或认错列。把条件放到 Select 之前。");
    }

    // ── Include ─────────────────────────────────────────────────────────

    /// <summary>解析引用导航属性并构造 IncludeNode：从根实体的契约中查找关系，创建目标表位，
    /// 构建 LEFT JOIN 条件（FK = PK）。</summary>
    internal IncludeNode Include(LambdaExpression navigation)
    {
        if (Model.Plan is not EntityProjection rootProjection)
            throw new QuickSqlException("Include 只能在实体查询上使用，投影之后不能再加载导航属性。把 Include 放到 Select 之前。");

        var rootTable = Model.Tables[rootProjection.TableIndex];
        var rootContract = rootTable.Contract ?? throw new QuickSqlException($"表位 {rootTable.Alias} 没有实体契约，无法解析导航属性。");

        var memberName = ExtractMemberName(navigation.Body);
        var relationship = FindRelationship(rootContract, memberName);

        // 先读导航、后 Include 那一序：这一根边已经作为模型里的真 JOIN 进 FROM 了（自动联表），
        // Include 就收养那一颗而不是再新造一张——两个方向合起来才是"一根边一张 JOIN"。
        var targetTable = Joined(rootTable, memberName)
                          ?? Orm.NewTableRef(relationship.TargetType, Model.Tables.Count);
        var onCondition = BuildJoinCondition(rootTable, relationship, targetTable);

        return new IncludeNode
        {
            NavigationMemberName = memberName,
            Relationship = relationship,
            TargetTable = targetTable,
            OnCondition = onCondition,
        };
    }

    /// <summary>在已有 Include 的基础上继续加载更深层导航。从指定父节点的实体契约中查找关系，
    /// 新节点作为父节点的子节点加入树中。</summary>
    internal IncludeNode ThenInclude(IncludeNode parent, LambdaExpression navigation)
    {
        var parentContract = parent.TargetTable.Contract
            ?? throw new QuickSqlException($"Include 的目标表 {parent.TargetTable.Alias} 没有实体契约。");

        var memberName = ExtractMemberName(navigation.Body);
        var relationship = FindRelationship(parentContract, memberName);
        var targetTable = Joined(parent.TargetTable, memberName)
                          ?? Orm.NewTableRef(relationship.TargetType, Model.Tables.Count);
        var onCondition = BuildJoinCondition(parent.TargetTable, relationship, targetTable);

        var node = new IncludeNode
        {
            NavigationMemberName = memberName,
            Relationship = relationship,
            TargetTable = targetTable,
            OnCondition = onCondition,
        };

        parent.Children.Add(node);

        return node;
    }

    /// <summary>将顶级 IncludeNode 加入模型（追加到 Includes 列表 + 追加目标表位）。
    /// 表位可能已经在场：这一根边先被自动联表补进 FROM、Include 收养它的那一序，
    /// 那时候不能再添第二份，否则 Tables 里同一种表出现两次，
    /// 而按位置绑形参的那道会把 arity≥2 的 lambda 错配一格。</summary>
    public QueryEngine AddInclude(IncludeNode node)
        => Derive(Model with
        {
            Includes = [.. Model.Includes, node],
            Tables = WithTable(Model.Tables, node.TargetTable),
        });

    /// <summary>将 ThenInclude 的子节点的目标表位加入模型。节点已经在父节点的 Children 里，
    /// 不需要再追加到 Includes 列表。</summary>
    public QueryEngine AddIncludeTable(IncludeNode node)
        => Derive(Model with { Tables = WithTable(Model.Tables, node.TargetTable) });

    static IReadOnlyList<TableRef> WithTable(IReadOnlyList<TableRef> tables, TableRef table)
    {
        foreach (var existing in tables)
            if (existing.Index == table.Index)
                return tables;

        return [.. tables, table];
    }

    /// <summary>这条链上那一根边（宿主表位的这一格成员）是不是已经联过，是就交回那一张表位。
    /// 判据与 NavigationJoin 同一把尺：按宿主表位的位置加成员名，不按实例。</summary>
    TableRef? Joined(TableRef host, string member)
    {
        foreach (var join in _navigation)
            if (join.HostIndex == host.Index && join.Member == member)
                return join.Table;

        return null;
    }

    /// <summary>AsSplitQuery() 的落点：引用导航不走 LEFT JOIN，改在主查询之后单独发查询再内存缝合。
    /// 模型里只置一个位，真正改写法的是编译器（跳过 Include 列收集）与执行层（ListWithSplitIncludesAsync）。
    /// 与 IncludeMany 互斥、当场拒：执行分派按拆查先赢，IncludeManys 在场就是无声蒸发——
    /// 那正是本仓库最不肯做的形状。两条接链顺序各自在这一格与那一边拦，凑成双向护栏。</summary>
    public QueryEngine SplitQuery()
    {
        if (Model.IncludeManys.Count > 0)
            throw new QuickSqlException(
                "AsSplitQuery() 与 IncludeMany 不接在同一句链上（此前这一形状会静默丢掉集合导航：" +
                "执行分派按拆查先赢，集合那一家无声蒸发）。把 IncludeMany 链单独成句发一条，" +
                "或去掉 AsSplitQuery() 走合批加载（深度 = 查询条数）。");

        return Derive(Model with { SplitQuery = true });
    }

    /// <summary>从表达式体提取成员名。只支持简单的属性访问（o => o.Customer），
    /// 不支持方法调用或复杂表达式。</summary>
    static string ExtractMemberName(Expression body)
    {
        if (body is MemberExpression member)
            return member.Member.Name;

        throw new QuickSqlException(
            $"Include/ThenInclude 的 lambda 必须是简单的属性访问（如 o => o.Customer），当前表达式形态是 {body.NodeType}。");
    }

    /// <summary>从实体契约中查找指定名称的关系（引用导航）。找不到时抛出明确的错误消息。</summary>
    static RelationshipContract FindRelationship(EntityContract contract, string memberName)
    {
        foreach (var rel in contract.Relationships)
        {
            if (rel.MemberName == memberName)
                return rel;
        }

        throw new QuickSqlException(
            $"实体 {contract.ClrType.Name} 上没有名为 {memberName} 的导航属性。" +
            $"已注册的关系：{string.Join(", ", contract.Relationships.Select(r => r.MemberName).DefaultIfEmpty("(无)"))}。" +
            "请确认属性名拼写正确，且已在契约中配置（Fluent 或注解）。");
    }

    /// <summary>构建 LEFT JOIN 的 ON 条件：依赖侧的 FK 列 = 主端实体的 PK 列，
    /// 复合外键时生成多个等值条件用 AND 连接。全仓库只有这一处会按一条引用导航造 JOIN 条件：
    /// Include 的急切加载与自动联表交出来的必须是同一句 ON，
    /// 两条边在两种来路上长成两种形状的话，"这根导航怎么联"就有了第二套真相。</summary>
    internal static SqlExpression BuildJoinCondition(TableRef sourceTable, RelationshipContract relationship, TableRef targetTable)
    {
        var targetContract = targetTable.Contract
            ?? throw new QuickSqlException($"目标表 {targetTable.Alias} 没有实体契约，无法构建 JOIN 条件。");

        var targetKeys = targetContract.PrimaryKey;

        if (targetKeys.Length == 0)
            throw new QuickSqlException(
                $"目标实体 {targetContract.ClrType.Name} 没有主键，无法构建 Include 的 JOIN 条件。" +
                "请为该实体配置主键（[Key] 注解或 Fluent API）。");

        if (relationship.ColumnNames.Count != targetKeys.Length)
            throw new QuickSqlException(
                $"关系 {relationship.MemberName} 的外键列数（{relationship.ColumnNames.Count}）" +
                $"与目标实体主键列数（{targetKeys.Length}）不匹配。复合外键的列序必须与主键一一对应。");

        SqlExpression? condition = null;

        for (var i = 0; i < relationship.ColumnNames.Count; i++)
        {
            var fkColumnName = relationship.ColumnNames[i];
            var fkColumn = sourceTable.Contract!.ByColumn[fkColumnName];
            var pkColumn = targetKeys[i];

            var fkExpr = new ColumnExpression(sourceTable, fkColumn);
            var pkExpr = new ColumnExpression(targetTable, pkColumn);
            var equal = new SqlBinaryExpression(BinaryOperator.Equal, fkExpr, pkExpr);

            condition = condition is null ? equal : new SqlBinaryExpression(BinaryOperator.And, condition, equal);
        }

        return condition!;
    }

    // ── IncludeMany ─────────────────────────────────────────────────────

    /// <summary>解析集合导航属性并构造 IncludeManyNode。不改写主查询 SQL，执行层发两条查询再缝合。
    /// FK 发现：先在子实体契约的 Relationships 里找指向父类型的边，找不到时按约定匹配
    /// （父类型名Id / 父类型名+PK列名）。子型那一档的建档委托由泛型口给：档位只有 For&lt;T&gt;() 能建，
    /// 它要编译期的 T；节点上放运行期类型就建不出档，放委托就建得出。</summary>
    internal IncludeManyNode IncludeMany<TDetail>(
        LambdaExpression navigation,
        Func<ChangeTracker, ITypeTracker> childTrackerFactory
    )
    {
        if (Model.Plan is not EntityProjection rootProjection)
            throw new QuickSqlException("IncludeMany 只能在实体查询上使用，投影之后不能再加载集合导航。把 IncludeMany 放到 Select 之前。");

        // 与 SplitQuery 互斥的另一向：AsSplitQuery() 先点、IncludeMany 后挂，链在这一格响。
        if (Model.SplitQuery)
            throw new QuickSqlException(
                "AsSplitQuery() 与 IncludeMany 不接在同一句链上（此前这一形状会静默丢掉集合导航：" +
                "执行分派按拆查先赢，集合那一家无声蒸发）。把 IncludeMany 链单独成句发一条，" +
                "或去掉 AsSplitQuery() 走合批加载（深度 = 查询条数）。");

        var memberName = ExtractMemberName(navigation.Body);
        var rootTable = Model.Tables[rootProjection.TableIndex];
        var parentContract = rootTable.Contract
            ?? throw new QuickSqlException($"表位 {rootTable.Alias} 没有实体契约，无法解析集合导航。");

        return NewIncludeManyNode<TDetail>(parentContract, memberName, childTrackerFactory);
    }

    /// <summary>把根上的那一颗集合节点加进模型（追加到 IncludeManys 列表）。子节点走
    /// ThenIncludeMany，不入这张清单——它挂在本节点的 Children 上。</summary>
    public QueryEngine AddIncludeMany(IncludeManyNode node)
        => Derive(Model with { IncludeManys = [.. Model.IncludeManys, node] });

    /// <summary>在已有集合节点上再挂一层集合导航（ThenIncludeMany）。父端是上一层刚缝出来的
    /// 那一批子实体，所以这一档不需要表位、也不动模型——主查询 SQL 一个字符不变，
    /// 多出来的只是执行层的一条 SELECT。判"这一层能不能挂"的是父节点子型的契约上有没有那根集合边。
    /// 子型那一档的建档委托同样由泛型口给。</summary>
    internal IncludeManyNode ThenIncludeMany<TDetail>(
        IncludeManyNode parent,
        LambdaExpression navigation,
        Func<ChangeTracker, ITypeTracker> childTrackerFactory
    )
    {
        var parentContract = (Schemas ?? Orm.Schemas).Contract(parent.ChildType);
        var node = NewIncludeManyNode<TDetail>(parentContract, ExtractMemberName(navigation.Body), childTrackerFactory);

        parent.Children.Add(node);

        return node;
    }

    /// <summary>在已有集合节点上再挂一层引用导航（ThenInclude）。父端还是上一层刚缝出来的那批子实体，
    /// 与 ThenIncludeMany 同一条纪律：不动模型、不发主查询的任何东西，节点记在本节点的 References 上，
    /// 执行期另发一条 SELECT ... WHERE 目标.PK IN (那一批的 FK 值) 再逐只缝回——走的是 AsSplitQuery
    /// 那一档已有的那颗加载器，一条引用边在两条路上必须长成同一个答案。集合导航在这一档当场拒：
    /// 按引用那一档发出去只缝得上集合里的最后一只，而"只少一只"这种错在结果里看不出形状。</summary>
    internal IncludeNode ThenInclude(IncludeManyNode parent, LambdaExpression navigation)
    {
        var memberName = ExtractMemberName(navigation.Body);
        var parentContract = (Schemas ?? Orm.Schemas).Contract(parent.ChildType);

        if (parentContract.Collections.Any(c => c.MemberName == memberName))
            throw new QuickSqlException(
                $"实体 {parentContract.ClrType.Name} 上的 {memberName} 是集合导航：这一档只走引用导航，" +
                $"集合换 ThenIncludeMany（它按外键把那一批全挂上）。用 ThenInclude 挂集合只会剩下最后一只。");

        var relationship = FindRelationship(parentContract, memberName);

        // 两侧表位的索引都取 0：这一颗不进 FROM，执行期那条独立 SELECT 自己现取别名（与拆分加载同一处理）。
        // ON 仍按全仓库共用的那一颗现造——它是"这根边的键对不对得上"唯一的判据，
        // 让它在链上就响，比在执行期缝出一只空导航、没人知道为什么好。
        var sourceTable = Orm.NewTableRef(parentContract.ClrType, 0);
        var targetTable = Orm.NewTableRef(relationship.TargetType, 0);

        var node = new IncludeNode
        {
            NavigationMemberName = memberName,
            Relationship = relationship,
            TargetTable = targetTable,
            OnCondition = BuildJoinCondition(sourceTable, relationship, targetTable),
        };

        parent.References.Add(node);

        return node;
    }

    /// <summary>给链上最后一层集合节点的子行点序（OrderByMany）。动的是节点，不是模型：
    /// 序记在这一层的 Orderings 上，主查询与模型的清单一个字符不变。认列在接上链的这一步就做完
    /// （共用 Column 那颗三档咽喉）：排错列的代价是页面上"看着合理的错顺序"，没人会为此报修，
    /// 所以它只能在这里响，不许拖到执行期。再点一次是整串替换——与契约上 OrderBy 之后再 OrderBy
    /// 同一个 LINQ 口径，多列子行序是这格今天没给的射程。</summary>
    public void OrderByMany(IncludeManyNode node, LambdaExpression key, bool descending)
    {
        var contract = Orm.Schemas.Contract(node.ChildType);
        var column = Column(contract, ManyOrderMember(key));

        node.Orderings.Clear();
        node.Orderings.Add(new IncludeManyOrdering(column, descending));
    }

    /// <summary>子行序 key 的形态闸：只收子实体上的直接属性访问（允许值类型装箱那一层 Convert 壳）。
    /// 嵌套路径当场拒而不是悄悄剥成末段：那一剥会拿"宿主的 Name"去撞子实体自己的列名，
    /// 撞中了就是排错列、撞不中就是排空气——两条都比"接链时响一句、让用户自己换列"坏。
    /// 这一档不发 JOIN（子 SELECT 的形状由外键那一条边定死），所以排在别的表那一侧的列根本发不出来，
    /// 拒它是射程声明，不是偷懒。</summary>
    static string ManyOrderMember(LambdaExpression key)
    {
        var body = key.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } box
            ? box.Operand
            : key.Body;

        if (body is MemberExpression { Expression: ParameterExpression } member)
            return member.Member.Name;

        throw new QuickSqlException(
            $"OrderByMany 的排序键必须是子实体上的直接属性访问（如 d => d.Order）：这颗口只把序落进子行那一条另发的 SELECT 的 ORDER BY，" +
            $"这一档不发 JOIN，嵌套路径（如 d => d.Warehouse.Name）与算式都排不出去。当前表达式形态是 {key.Body.NodeType}。");
    }

    /// <summary>两层共用的一颗节点构造：从父契约上按属性名取集合元素类型，再发现外键。
    /// 运行期元素型与编译期 TDetail 必须一致：节点上那份建档委托是按 TDetail 建的档，
    /// 两者不是同一型时，建出来的是另一型的档，登记与缝合就落在两张不同的账上——这一格当场响，不留静默。</summary>
    IncludeManyNode NewIncludeManyNode<TDetail>(
        EntityContract parentContract,
        string memberName,
        Func<ChangeTracker, ITypeTracker> childTrackerFactory
    )
    {
        var navProperty = parentContract.ClrType.GetProperty(memberName)
            ?? throw new QuickSqlException(
                $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的属性。");

        var childType = GetCollectionElementType(navProperty.PropertyType)
            ?? throw new QuickSqlException(
                $"属性 {parentContract.ClrType.Name}.{memberName} 不是集合类型（IEnumerable<T>）。IncludeMany 只用于集合导航。");

        if (childType != typeof(TDetail))
            throw new QuickSqlException(
                $"IncludeMany 的泛型参数与导航属性的元素类型对不上：导航 {parentContract.ClrType.Name}.{memberName} 的元素型是 " +
                $"{childType.Name}，而这一句给的泛型参数是 {typeof(TDetail).Name}。集合导航的元素型就是子实体，" +
                "两处写成不同的型是调用方的错 —— 那一档的跟踪档会按泛型参数建，与缝上去的实例不是同一型。");

        var childContract = Orm.Schemas.Contract(childType);
        var fkColumns = DiscoverFkColumns(parentContract, childContract, memberName);

        return new IncludeManyNode
        {
            NavigationMemberName = memberName,
            ChildType = childType,
            ChildFkColumnNames = fkColumns,
            ChildTrackerFactory = childTrackerFactory,
        };
    }

    /// <summary>从子实体契约上发现指向父实体 PK 的外键列。先查显式关系（子端有引用导航回父），
    /// 再看那根集合导航上点名的外键成员，最后按约定匹配列名——三档与级联落键同序。
    /// 返回的列顺序与父实体 PK 列对齐。</summary>
    static IReadOnlyList<string> DiscoverFkColumns(EntityContract parentContract, EntityContract childContract, string memberName)
    {
        var parentKeys = parentContract.PrimaryKey;

        if (parentKeys.Length == 0)
            throw new QuickSqlException(
                $"父实体 {parentContract.ClrType.Name} 没有主键，无法发现子实体 {childContract.ClrType.Name} 的外键。");

        foreach (var rel in childContract.Relationships)
        {
            if (rel.TargetType == parentContract.ClrType)
            {
                if (rel.ColumnNames.Count != parentKeys.Length)
                    throw new QuickSqlException(
                        $"子实体 {childContract.ClrType.Name} 到 {parentContract.ClrType.Name} 的关系有 {rel.ColumnNames.Count} 个外键列，" +
                        $"但父实体有 {parentKeys.Length} 个主键列，数量不匹配。");

                return rel.ColumnNames;
            }
        }

        if (ContractResolver.ResolveDeclaredFkColumns(parentContract, childContract, memberName) is { } declaredColumns)
            return declaredColumns;

        var parentTypeName = parentContract.ClrType.Name;

        if (parentKeys.Length == 1)
        {
            var conventionNames = new[] { $"{parentTypeName}Id", $"{parentTypeName}ID" };

            foreach (var conventionName in conventionNames)
            {
                if (childContract.ByColumn.ContainsKey(conventionName))
                    return [conventionName];
            }

            var byColumnName = $"{parentTypeName}{parentKeys[0].ColumnName}";

            if (childContract.ByColumn.ContainsKey(byColumnName))
                return [byColumnName];
        }

        var compositeNames = new List<string>(parentKeys.Length);

        foreach (var pk in parentKeys)
        {
            var name = $"{parentTypeName}{pk.ColumnName}";

            if (!childContract.ByColumn.ContainsKey(name))
                throw new QuickSqlException(
                    $"无法发现子实体 {childContract.ClrType.Name} 指向父实体 {parentContract.ClrType.Name} 的外键列。" +
                    $"约定匹配失败：在子实体上找不到 {(parentKeys.Length == 1 ? $"\"{parentTypeName}Id\" 或 \"{parentTypeName}{parentKeys[0].ColumnName}\"" : $"\"{name}\"")}。" +
                    "请在子实体上配置引用导航或在 Fluent API 中显式指定外键。");

            compositeNames.Add(name);
        }

        return compositeNames;
    }

    static Type? GetCollectionElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>可执行模型：先贴全局过滤器，再补分页的序（全仓库只有这一处读 PagingOrdering）。
    /// 过滤器落在这里是因为这是读侧唯一的一个咽喉：ToList/Count/Any/ToPaged/Stream/Explain/ToSql
    /// 全走这一格，写在算子上的话每加一个出口就要重记一遍"过滤器会不会漏"；
    /// 序补在过滤器之后是有意的：补出来的那一个主键序可能与被筛掉的行无关，两件事本来就该各说各的。</summary>
    public QueryModel Effective()
    {
        var model = Filtered();

        if ((model.Offset is null or 0 && model.Limit is null) || model.Ordering.Count > 0)
            return model;

        switch (Orm.PagingOrdering)
        {
            case PagingOrderingPolicy.Allow:
                return model;

            case PagingOrderingPolicy.AutoOrderPrimaryKey:
                // 分组的那一条不能补主键序：主键不在分组键里，ORDER BY t0.Id 配 GROUP BY t0.State
                // 在每一家都是错句，而这一版的模型层发不出"按组内主键排序"这种东西。
                if (model.GroupBy.Count > 0)
                    throw new QuickSqlException(
                        "分页（未给序）落在分组查询上，而 AutoOrderPrimaryKey 补不出序：主键不是分组键，补出来的那一句在库里不合法。" +
                        "请在 GroupBy 之后显式 OrderBy(g => g.Key) 或 OrderByDescending(g => g.Count())（§9.4）。");

                return model with { Ordering = [.. PrimaryKeyOrdering(model)] };

            default:
                // 那句话不能写"两家的 OFFSET..FETCH 都语法错"：前半对 T-SQL，后半对 PG 系不成立
                //（它只需要 OFFSET 存在即可）。两家的共同点只有一个：没有 ORDER BY 就没有可复现的行序。
                throw new QuickSqlException(
                    $"分页（OFFSET {model.Offset ?? 0} ROWS，FETCH {model.Limit?.ToString() ?? "全部"}）没有 ORDER BY：这一家的 OFFSET 不保证行序，" +
                    "有的方言族甚至要求先有 ORDER BY 才合法。放开策略只会得到一个数据库不保证的结果。补一个 OrderBy(...)，或按需把 PagingOrdering 设成 AutoOrderPrimaryKey / Allow（§9.4）。");
        }
    }

    /// <summary>全局过滤器贴进这份模型的那一步。没注册过任何东西时这里一个对象都不造
    /// （HasAny 一次数组长度比较就返回），那几格分配数字不为"有人可能用过滤器"这件事付钱。
    /// _ignoredFilters 那一支仍然要问一次 mandatory：标记位可能是在表位长齐之前置的
    /// （IgnoreQueryFilters() 之后再 InnerJoin 一张带 mandatory 过滤器的表），漏掉那一次问就等于留一条绕法。</summary>
    QueryModel Filtered()
    {
        if (!Orm.Filters.HasAny)
            return Model;

        if (_ignoredFilters is { } ignored)
        {
            Orm.Filters.AssertIgnorable(Model.Tables, ignored);

            return Orm.Filters.Apply(Model, ignored);
        }

        return Orm.Filters.Apply(Model);
    }

    IEnumerable<OrderExpression> PrimaryKeyOrdering(QueryModel model)
    {
        var contract = model.Root.Contract ?? throw new QuickSqlException("根表位没有实体契约，补不出主键序。");
        var keys = contract.PrimaryKey;

        if (keys.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有主键，AutoOrderPrimaryKey 补不出序。请显式 OrderBy，或改用 PagingOrdering.Allow（§9.4）。");

        return keys.Select(key => new OrderExpression(new ColumnExpression(model.Root, key), false));
    }

    public SelectStatement Compile(QueryModel model) => Orm.Compiler.Compile(model);

    public DialectSql BuildSql(out SelectStatement ast) => Build(Effective(), out ast);

    DialectSql Build(QueryModel model, out SelectStatement ast)
    {
        ast = Compile(model);

        var dialect = Orm.Provider.Dialect;
        var writer = new SqlWriter(new(QueryCompiler.EstimateSqlLength(ast)), new(), dialect.ParameterPrefix);
        dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    public string ToSql(out SelectStatement ast) => Build(Effective(), out ast).Text;

    /// <summary>不写成 ToSql(out _)：那里有两个 out 重载，丢弃参数写不成类型，编译器报二义。</summary>
    public string ToSql() => Build(Effective(), out _).Text;

    public string ToSql(out IReadOnlyList<AdoParameter> parameters)
    {
        var sql = Build(Effective(), out _);
        parameters = sql.Parameters;

        return sql.Text;
    }

    public string ToLogSql() => Build(Effective(), out _).ToLogSql(Orm.Provider);

    public QueryPlan Explain()
    {
        var model = Effective();
        var sql = Build(model, out var ast);

        // 已注册的改写类钩子要在这里逐个回放；今天的改写钩子只作用在写路径的值上，
        // 读路径没有可记的步骤 —— 空集合是真的空，不是漏了。
        return new QueryPlan(ast, sql, model.StableKey, []) { Provider = Orm.Provider };
    }

    // ── 执行 ──────────────────────────────────────────────────────────

    public async Task<List<TResult>> ListAsync<TResult>(CancellationToken ct)
    {
        _gate?.Enter();

        try
        {
            return await Rows<TResult>(Effective(), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate?.Exit();
        }
    }

    /// <summary>ListAsync 的数组形态：同一条 Rows，只在末尾多一次整段拷贝。拷贝落在这一层而不是链式对象上，
    /// 为的是"交出去的那一只不是缓存里那一只"这件事全仓只有一处说法
    /// （缓存那格存的是 List 本身，所以命中时列表口交出去的就是它）。</summary>
    public async Task<TResult[]> ArrayAsync<TResult>(CancellationToken ct)
    {
        _gate?.Enter();

        try
        {
            return (await Rows<TResult>(Effective(), ct).ConfigureAwait(false)).ToArray();
        }
        finally
        {
            _gate?.Exit();
        }
    }

    /// <summary>内部行数帽（First 取 1、Single 取 2 判重复）。帽不走分页的序策略：
    /// "任意一行"本来就是 LINQ 语义，为此要求用户补序只会变成噪音（方言侧的占位序见 DialectBase.EmitPagingOrdering）。</summary>
    public async Task<List<TResult>> CappedAsync<TResult>(int rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        _gate?.Enter();

        try
        {
            return await Rows<TResult>(Effective() with { Limit = rows }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate?.Exit();
        }
    }

    async Task<List<TResult>> Rows<TResult>(QueryModel model, CancellationToken ct)
    {
        Detect(model);

        if (Cached<TResult>(model) is { } hit)
        {
            Track(model, hit);

            return hit;
        }

        List<TResult> rows;

        // 组合期两道护栏（SplitQuery()/IncludeMany 双向）漏掉的形状在这里兜底：宁可在这里炸，
        // 也不让 IncludeManys 分派永远走不到而无声蒸发。
        if (model.SplitQuery && model.IncludeManys.Count > 0)
            throw new QuickSqlException("AsSplitQuery() 与 IncludeMany 不接在同一句链上（组合期护栏应已拦下，这一格是执行期兜底）。");

        if (model.SplitQuery && model.Includes.Count > 0 && model.Plan is EntityProjection)
            rows = await ListWithSplitIncludesAsync<TResult>(model, ct).ConfigureAwait(false);
        else if (model.IncludeManys.Count > 0 && model.Plan is EntityProjection)
            rows = await ListWithIncludeManyAsync<TResult>(model, ct).ConfigureAwait(false);
        else
            rows = await Orm.Ado.QueryAsync(Build(model, out _), Reader<TResult>(model), Tag("select"), model.Limit, ct).ConfigureAwait(false);

        Track(model, rows);
        Store(model, rows);

        return rows;
    }

    /// <summary>IncludeMany 的两条查询执行。先执行主查询拿父实体，再发第二条查询拿子实体
    /// （WHERE FK IN (@parentPks)），最后在内存里把子实体缝回父实体的集合属性。
    /// 父实体为空时跳过子查询（避免生成空 IN 子句）。链上挂了 ThenIncludeMany 就往下一层递归，
    /// 挂了 ThenInclude 就在这一层的子实体上另发一条拆分加载：根一条、每一层各一条、每一层旁边每根引用导航再一条。</summary>
    [RequiresUnreferencedCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    [RequiresDynamicCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    async Task<List<TResult>> ListWithIncludeManyAsync<TResult>(QueryModel model, CancellationToken ct)
    {
        var parents = await Orm.Ado.QueryAsync(
            Build(model, out _), Reader<TResult>(model), Tag("select"), model.Limit, ct).ConfigureAwait(false);

        if (parents.Count == 0)
            return parents;

        var entity = (EntityProjection)model.Plan;
        var parentContract = entity.TableIndex >= 0 ? model.Tables[entity.TableIndex].Contract! : throw new QuickSqlException("IncludeMany 需要实体投影。");

        foreach (var includeMany in model.IncludeManys)
        {
            var parentObjects = new List<object>(parents.Count);

            foreach (var p in parents)
                parentObjects.Add(p!);

            await LoadChainAsync(parentObjects, parentContract, includeMany, ct).ConfigureAwait(false);
        }

        return parents;
    }

    /// <summary>逐层往下缝：本层刚捞出的子实体就是下一层的父端。上一层没行就直接停——下一颗节点没有
    /// 可当 IN 值的键，而这一档不发空 IN（旁边那一档引用导航同理，它收的也是这一批子实体）。</summary>
    [RequiresUnreferencedCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    [RequiresDynamicCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    async Task LoadChainAsync(List<object> parents, EntityContract parentContract, IncludeManyNode node, CancellationToken ct)
    {
        var children = await LoadChildrenAsync(parents, parentContract, node, ct).ConfigureAwait(false);

        if (children.Count == 0)
            return;

        var childContract = Orm.Schemas.Contract(node.ChildType);

        // 旁边那一档：这一批子实体各自手里的引用导航。父端就是刚缝出来的这一批，所以直接把拆分加载器递过去。
        if (node.References.Count > 0)
            await LoadSplitIncludeNodesAsync(children, childContract, node.References, ct).ConfigureAwait(false);

        foreach (var child in node.Children)
            await LoadChainAsync(children, childContract, child, ct).ConfigureAwait(false);
    }

    [RequiresUnreferencedCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    [RequiresDynamicCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    async Task<List<object>> LoadChildrenAsync(
        List<object> parents, EntityContract parentContract,
        IncludeManyNode node, CancellationToken ct)
    {
        var childContract = Orm.Schemas.Contract(node.ChildType);
        var childRead = Orm.Schemas.ReadDelegate(node.ChildType);
        var parentKeys = parentContract.PrimaryKey;

        var parentKeyGetters = BuildKeyGetters(parentContract.ClrType, parentKeys);
        var parentKeyValues = new List<object>(parents.Count);
        var parentMap = new Dictionary<object, List<object>>(parents.Count,
            parentKeys.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var parent in parents)
        {
            object key;

            if (parentKeys.Length == 1)
                key = parentKeyGetters[0](parent);
            else
            {
                var values = new object[parentKeys.Length];

                for (var j = 0; j < parentKeys.Length; j++)
                    values[j] = parentKeyGetters[j](parent);

                key = values;
            }

            parentKeyValues.Add(key);

            if (!parentMap.TryGetValue(key, out var list))
            {
                list = [];
                parentMap[key] = list;
            }

            list.Add(parent);
        }

        var childTable = Orm.NewTableRef(node.ChildType, 0);

        var fkColumns = new ColumnContract[node.ChildFkColumnNames.Count];

        for (var i = 0; i < fkColumns.Length; i++)
            fkColumns[i] = childContract.ByColumn[node.ChildFkColumnNames[i]];

        // 子行序（OrderByMany）：点过就把序灌进这一层子 SELECT 的 ORDER BY 槽，发射在方言里；
        // 没点过这一格就是空清单，方言不发 ORDER BY，那条子句与本口存在之前逐字节同形。
        var ordering = new OrderExpression[node.Orderings.Count];

        for (var i = 0; i < ordering.Length; i++)
            ordering[i] = new OrderExpression(new ColumnExpression(childTable, node.Orderings[i].Column), node.Orderings[i].Descending);

        // IN 的值按参数预算分块发射（QueryInBatchesAsync）：父集合超过预算时是多条子 SELECT，不是整条失败。
        var children = await QueryInBatchesAsync(
            parentKeyValues, fkColumns, childTable, childContract,
            Materializer.EntityRows<object>(childRead, childContract),
            ordering, Tag("include-many"), ct).ConfigureAwait(false);

        var childKeyGetters = BuildKeyGettersByColumnName(node.ChildType, childContract, node.ChildFkColumnNames);
        var nav = MemberAccessors.BindCollection(
            parentContract.ClrType, node.NavigationMemberName, node.ChildType,
            $"实体 {parentContract.ClrType.Name} 上没有名为 {node.NavigationMemberName} 的集合导航属性，无法挂上 IncludeMany 加载的子实体。");

        foreach (var child in children)
        {
            object key;

            if (childKeyGetters.Length == 1)
                key = childKeyGetters[0](child);
            else
            {
                var values = new object[childKeyGetters.Length];

                for (var j = 0; j < childKeyGetters.Length; j++)
                    values[j] = childKeyGetters[j](child);

                key = values;
            }

            if (parentMap.TryGetValue(key, out var matchedParents))
            {
                // 缝的是「会话里的那一只」：这一行可能早就在身份映射里（先查过明细、再补载父的那一串），
                // 而按刚物化那只实例缝会让会话里同一行留两只 —— 级联删除撞上第二只时只能说"同键两只没有定义"。
                var stitched = _tracker?.CanonicalOf(child, node.ChildType) ?? child;

                foreach (var parent in matchedParents)
                    nav.Attach(parent, stitched);
            }
        }

        if (_tracker is not null)
        {
            // 子型那一档先建好：TrackAll 按运行期类型找档，而档位只有泛型那一颗 For<T>() 能建。
            // 建档委托是接链那一刻（IncludeMany<TDetail> / ThenIncludeMany<…,TDetail>）按编译期子型给的，
            // 所以这一格不必反射，也不必调用方自己先摸一句 Set<子型>()。
            var childTracker = node.ChildTrackerFactory?.Invoke(_tracker);

            // 建出来的档与节点上的子型必须同型：登记与缝合要落在同一张账上。
            if (childTracker is not null && childTracker.EntityType != node.ChildType)
                throw new QuickSqlException(
                    $"IncludeMany 缝出来的子型（{node.ChildType.Name}）与建出来的那一档（{childTracker.EntityType.Name}）不是同一型：" +
                    "登记与缝合会落在两张不同的账上。这是内核缺陷，不是调用方写错。请带这个查询的 Explain() 报缺陷。");

            _tracker.TrackAll(children, node.ChildType);
        }

        return children;
    }

    /// <summary>拆分查询执行。引用导航不走 LEFT JOIN：先执行主查询拿根实体，再为每个 Include 节点
    /// 单独发一条 SELECT ... WHERE PK IN (父端 FK 值集合)，最后在内存里把子实体缝回父实体的引用导航属性。
    /// 嵌套的 ThenInclude 递归处理：子实体加载完后继续为它的子节点发查询。</summary>
    [RequiresUnreferencedCode("AsSplitQuery 按运行期 Type 反射读取主键值与设置引用导航属性。")]
    [RequiresDynamicCode("AsSplitQuery 按运行期 Type 反射读取主键值与设置引用导航属性。")]
    async Task<List<TResult>> ListWithSplitIncludesAsync<TResult>(QueryModel model, CancellationToken ct)
    {
        var roots = await Orm.Ado.QueryAsync(
            Build(model, out _), Reader<TResult>(model), Tag("select"), model.Limit, ct).ConfigureAwait(false);

        if (roots.Count == 0)
            return roots;

        var entity = (EntityProjection)model.Plan;
        var rootContract = model.Tables[entity.TableIndex].Contract!;
        var rootObjects = new List<object>(roots.Count);

        foreach (var r in roots)
            rootObjects.Add(r!);

        await LoadSplitIncludeNodesAsync(rootObjects, rootContract, model.Includes, ct).ConfigureAwait(false);

        return roots;
    }

    /// <summary>递归加载一层拆分 Include 节点。对每个节点：收集父端 FK 值 → 查子表 WHERE PK IN (...) →
    /// 按 PK 建索引 → 缝回父实体的引用导航属性 → 递归处理子节点的 Children。</summary>
    [RequiresUnreferencedCode("拆分 Include 按运行期 Type 反射读取 FK 值与设置引用导航属性。")]
    [RequiresDynamicCode("拆分 Include 按运行期 Type 反射读取 FK 值与设置引用导航属性。")]
    async Task LoadSplitIncludeNodesAsync(
        List<object> parents, EntityContract parentContract,
        IReadOnlyList<IncludeNode> includes, CancellationToken ct)
    {
        foreach (var node in includes)
        {
            var childContract = node.TargetTable.Contract!;
            var childRead = Orm.Schemas.ReadDelegate(childContract.ClrType);
            var childPkColumns = childContract.PrimaryKey;

            // 缝回父实体：与 DbSet.Load 同一处理 —— 属性名与父型在节点上就定了，
            // 没道理每配到一颗子行都按运行期类型找一次属性再反射写一次。
            var setNavigation = MemberAccessors.Setter(
                parentContract.ClrType, node.NavigationMemberName,
                $"实体 {parentContract.ClrType.Name} 上没有名为 {node.NavigationMemberName} 的引用导航属性，无法挂上拆分 Include 加载的实体。",
                $"实体 {parentContract.ClrType.Name}.{node.NavigationMemberName} 没有公共写口：Include 要求导航属性可写。");

            var parentFkGetters = BuildKeyGettersByColumnName(parentContract.ClrType, parentContract, node.Relationship.ColumnNames);

            var fkValues = new HashSet<object>(
                parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);
            var parentByFk = new Dictionary<object, List<object>>(
                parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

            foreach (var parent in parents)
            {
                object fkKey;

                if (parentFkGetters.Length == 1)
                    fkKey = parentFkGetters[0](parent);
                else
                {
                    var vals = new object[parentFkGetters.Length];

                    for (var j = 0; j < parentFkGetters.Length; j++)
                        vals[j] = parentFkGetters[j](parent);

                    fkKey = vals;
                }

                fkValues.Add(fkKey);

                if (!parentByFk.TryGetValue(fkKey, out var list))
                {
                    list = [];
                    parentByFk[fkKey] = list;
                }

                list.Add(parent);
            }

            var childTable = Orm.NewTableRef(childContract.ClrType, 0);

            // IN 的值按参数预算分块发射（QueryInBatchesAsync）：父端 FK 集超过预算时是多条子 SELECT，不是整条失败。
            var children = await QueryInBatchesAsync(
                [.. fkValues], childPkColumns, childTable, childContract,
                Materializer.EntityRows<object>(childRead, childContract),
                [], Tag("split-include"), ct).ConfigureAwait(false);

            // 先登记再缝：TrackAll 就地写回"会话里的那一只"，于是导航上挂的、往下递归加载的都是那一只。
            // 这一档配的是子端主键对父端外键，登记恒安全；IncludeMany 那一档配的是外键——会话里改过外键
            // 还没写出去的行会配到错的父上，所以那一档缝的时候才现问身份映射。
            if (_tracker is not null)
                _tracker.TrackAll(children, childContract.ClrType);

            var childPkGetters = BuildKeyGetters(childContract.ClrType, childPkColumns);

            foreach (var child in children)
            {
                object pkKey;

                if (childPkGetters.Length == 1)
                    pkKey = childPkGetters[0](child);
                else
                {
                    var vals = new object[childPkGetters.Length];

                    for (var j = 0; j < childPkGetters.Length; j++)
                        vals[j] = childPkGetters[j](child);

                    pkKey = vals;
                }

                if (parentByFk.TryGetValue(pkKey, out var matchedParents))
                {
                    foreach (var parent in matchedParents)
                        setNavigation(parent, child);
                }
            }

            if (node.Children.Count > 0)
            {
                var childObjects = new List<object>(children.Count);

                foreach (var c in children)
                    childObjects.Add(c);

                await LoadSplitIncludeNodesAsync(childObjects, childContract, node.Children, ct).ConfigureAwait(false);
            }
        }
    }

    [RequiresUnreferencedCode("按运行期 Type 反射取主键属性读口。")]
    [RequiresDynamicCode("把主键属性读口编译成委托。")]
    static Func<object, object>[] BuildKeyGetters(Type entityType, ColumnContract[] keyColumns)
    {
        var getters = new Func<object, object>[keyColumns.Length];

        for (var i = 0; i < keyColumns.Length; i++)
            getters[i] = MemberAccessors.Getter(
                entityType, keyColumns[i].MemberName,
                $"实体 {entityType.Name} 上没有主键属性 {keyColumns[i].MemberName}",
                $"实体 {entityType.Name} 的主键属性 {keyColumns[i].MemberName} 没有公共 getter");

        return getters;
    }

    [RequiresUnreferencedCode("按运行期 Type 反射取外键列对应属性的读口。")]
    [RequiresDynamicCode("把外键列对应属性的读口编译成委托。")]
    static Func<object, object>[] BuildKeyGettersByColumnName(Type entityType, EntityContract contract, IReadOnlyList<string> columnNames)
    {
        var getters = new Func<object, object>[columnNames.Count];

        for (var i = 0; i < columnNames.Count; i++)
        {
            var memberName = contract.ByColumn[columnNames[i]].MemberName;

            getters[i] = MemberAccessors.Getter(
                entityType, memberName,
                $"实体 {entityType.Name} 上没有属性 {memberName}",
                $"实体 {entityType.Name} 的属性 {memberName} 没有公共 getter");
        }

        return getters;
    }

    /// <summary>「按一批键 IN 捞子行」的共用发射段（IncludeMany、拆分 Include 与 DbSet.Load 两条补载路，全仓四处调用）。
    /// IN 的值按 provider 的参数预算分块（公式与 Insert 链共用 StatementEmitter.RowsPerStatement，没声明预算的走同一颗保守值），
    /// 每块一条子 SELECT，结果按块序串接；复合键发逐行等值的 OR 链（(fk1=@0 AND fk2=@1) OR …），不发"每列各一个 IN"——
    /// 那是值的叉积，缝合会静默配错行。行读取器在循环外只建一份，跨块复用；空键集一条语句都不发。</summary>
    internal async Task<List<object>> QueryInBatchesAsync(
        IReadOnlyList<object> keyValues,
        IReadOnlyList<ColumnContract> inColumns,
        TableRef targetTable,
        EntityContract targetContract,
        Func<DbDataReader, object> rowMap,
        IReadOnlyList<OrderExpression> ordering,
        ExecutionTag tag,
        CancellationToken ct)
    {
        var budget = Orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var perChunk = StatementEmitter.RowsPerStatement(budget, Math.Max(1, inColumns.Count));

        var projections = new Projection[targetContract.Columns.Length];

        for (var i = 0; i < projections.Length; i++)
            projections[i] = new Projection(new ColumnExpression(targetTable, targetContract.Columns[i]), null);

        var dialect = Orm.Provider.Dialect;
        var result = new List<object>();

        var composite = inColumns.Count > 1;
        var inTarget = composite ? null : new ColumnExpression(targetTable, inColumns[0]);

        for (var first = 0; first < keyValues.Count; first += perChunk)
        {
            var width = Math.Min(perChunk, keyValues.Count - first);

            SqlExpression? where = null;

            if (composite)
            {
                // 逐行等值的 OR 链：(fk1=@0 AND fk2=@1) OR (fk1=@2 AND fk2=@3)。
                // 发射侧对混嵌 AND/OR 自动加括号（DialectBase.EmitLogical），链条右倾拼接即得人手写的形状。
                for (var i = 0; i < width; i++)
                {
                    var key = (object[])keyValues[first + i]!;

                    SqlExpression? row = null;

                    for (var c = 0; c < inColumns.Count; c++)
                    {
                        var equality = new SqlBinaryExpression(BinaryOperator.Equal,
                            new ColumnExpression(targetTable, inColumns[c]),
                            new ParameterValue(key[c], null));

                        row = row is null ? equality : new SqlBinaryExpression(BinaryOperator.And, row, equality);
                    }

                    where = where is null ? row : new SqlBinaryExpression(BinaryOperator.Or, where, row!);
                }
            }
            else
            {
                var inValues = new List<SqlExpression>(width);

                for (var i = 0; i < width; i++)
                    inValues.Add(new ParameterValue(keyValues[first + i], null));

                where = new InExpression(inTarget!, inValues);
            }

            var ast = new SelectStatement
            {
                Columns = projections,
                From = new SourceTable(targetTable),
                Where = where,
                OrderBy = ordering,
            };

            var writer = new SqlWriter(
                new StringBuilder(QueryCompiler.EstimateSqlLength(ast)),
                new List<AdoParameter>(),
                dialect.ParameterPrefix);
            dialect.Emit(in ast, writer);

            var rows = await Orm.Ado.QueryAsync(
                new DialectSql(writer.ToSqlString(), writer.Parameters),
                rowMap,
                tag,
                null,
                ct).ConfigureAwait(false);

            result.AddRange(rows);
        }

        return result;
    }

    /// <summary>复合外键的字典比较器：按元素比较 object[] 键值。</summary>
    sealed class StructuralComparer : IEqualityComparer<object>
    {
        public static readonly StructuralComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is object[] ax && y is object[] ay)
                return ax.SequenceEqual(ay);

            return object.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is object[] arr)
            {
                var hash = new HashCode();

                foreach (var item in arr)
                    hash.Add(item);

                return hash.ToHashCode();
            }

            return obj.GetHashCode();
        }
    }

    public async Task<TResult?> FirstAsync<TResult>(CancellationToken ct)
        => await CappedAsync<TResult>(1, ct).ConfigureAwait(false) is [var only, ..] ? only : default;

    /// <summary>First 的落点：执行档与 FirstAsync 同一条（CappedAsync(1)），差别只在空集那格——那颗给 default，这颗抛。
    /// 多行的链上两颗一样只取一行，"多条里取一条"的取舍仍归调用方的 OrderBy。</summary>
    public async Task<TResult> FirstOrThrowAsync<TResult>(CancellationToken ct)
    {
        var rows = await CappedAsync<TResult>(1, ct).ConfigureAwait(false);

        return rows.Count > 0
            ? rows[0]
            : throw new QuickSqlException(
                $"First 没有可返回的行：查询结果为空，而 First 的语义是『没有行算错误』。" +
                $"空集是正常业务的那一口请用 FirstOrDefault(Async)（{typeof(TResult).Name}）。");
    }

    /// <summary>Single 的落点：句子上限 2 行（CappedAsync(2)），0 行与 ≥2 行都是错误。
    /// 多行那一条抛"链条算错了"而不是"取第一行"——判重帽截在第 2 行，库里明明还有更多也不会数给调用方。</summary>
    public async Task<TResult> SingleAsync<TResult>(CancellationToken ct)
    {
        var rows = await CappedAsync<TResult>(2, ct).ConfigureAwait(false);

        return rows.Count switch
        {
            1 => rows[0],
            0 => throw new QuickSqlException(
                    $"Single 没有可返回的行：查询结果为空，而 Single 的语义是『恰一行，没有行也是错』。" +
                    $"空集是正常业务的那一口请用 SingleOrDefault(Async)（{typeof(TResult).Name}）。"),
            _ => throw new QuickSqlException(
                    "Single 应恰为一行，实际读到不止一行（判重帽截在第 2 行，库里可能还有更多）：多行说明" +
                    "链条算错了——要么缺过滤，要么数据违反了本该存在的唯一约束。要『多条里取一条』请先 " +
                    "OrderBy 再用 First(Async)。"),
        };
    }

    /// <summary>SingleOrDefault 的落点：与 SingleAsync 同一颗判重帽，只放宽 0 行那格（交回 default）。
    /// 多行照旧抛——"恰一行"的判据不因缺省而放宽，多行永远是链条算错的信号。</summary>
    public async Task<TResult?> SingleOrDefaultAsync<TResult>(CancellationToken ct)
    {
        var rows = await CappedAsync<TResult>(2, ct).ConfigureAwait(false);

        return rows.Count switch
        {
            0 => default,
            1 => rows[0],
            _ => throw new QuickSqlException(
                    "SingleOrDefault 应至多一行，实际读到不止一行（判重帽截在第 2 行）：多行说明链条算错了" +
                    "——要么缺过滤，要么数据违反了本该存在的唯一约束。要『多条里取一条』请先 OrderBy 再用 " +
                    "FirstOrDefault(Async)。"),
        };
    }

    /// <summary>All 的落点：发"数出不满足的行"——原谓词取反并入 WHERE 再计数（COUNT 一族），计数为 0 即全称成立。
    /// 谓词为 NULL 的行按"通过"计，与 SQL 三值逻辑一致。代价与 Count 同阶：数"不满足"的行要数完才见分晓
    /// （COUNT 不像 EXISTS 那样找到第一行就收口），这是全称量词的固有代价。</summary>
    public async Task<bool> AllAsync(LambdaExpression predicate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var probe = WhereNot(predicate);

        return await probe.CountAsync(ct).ConfigureAwait(false) == 0;
    }

    /// <summary>AllAsync 的现场：与 Where 同一条翻译步法（Scout → 翻译 → 欠账落地），差别只在翻译出的节点
    /// 外面再包一颗 Not——取反发生在 SQL 表达式层（发射时就是 NOT (…)），不在 lambda 层改写表达式树。</summary>
    QueryEngine WhereNot(LambdaExpression predicate)
    {
        Filterable();

        var scout = Scout(Model);
        var node = Orm.Expressions.Predicate(predicate, Scope.Create(Model, predicate, scout));
        var negated = new SqlUnaryExpression(UnaryOperator.Not, node);

        return Advanced(scout, Model.AddPredicate(negated));
    }

    /// <summary>流式出口：一次只有一行在内存里。batchSize 在别的库上是驱动侧的 FetchSize，而 TDS 没有逐批
    /// 取回的游标协议、DbDataReader 本来就是从网络缓冲里逐行读——所以这个值在这里只用于校验，
    /// 不影响结果集，也不假装调了什么服务端开关。</summary>
    public IAsyncEnumerable<TResult> StreamAsync<TResult>(int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        return Streamed<TResult>(Effective(), ct);
    }

    async IAsyncEnumerable<TResult> Streamed<TResult>(QueryModel model, [EnumeratorCancellation] CancellationToken ct)
    {
        // 流式那颗闸从第一次 MoveNext 抓到枚举收尾：for-each 中途别的操作进场照样被拒，
        // 读到一半的游标不会被另一条查询偷换连接。finally 在枚举 Dispose 那一刻放闸。
        _gate?.Enter();

        try
        {
            Detect(model);

            var tracker = _tracker;
            var rows = Orm.Ado.StreamAsync(Build(model, out _), Reader<TResult>(model), Tag("stream"), ct);

            // 流式与整批走同一份登记规则：一次查询在两种出口下"进没进身份映射"必须一致，
            // 否则 foreach (var o in db.Set<T>()) 之后 Entry(o) 会问它不在会话里，而 ToListAsync 之后又在。
            if (tracker is not null && EntityClrType(model) is { } streamedType)
            {
                await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
                    yield return (TResult) tracker.TrackEntity(row!, streamedType);

                yield break;
            }

            await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
                yield return row;
        }
        finally
        {
            _gate?.Exit();
        }
    }

    // ── 跟踪接缝 ───────────────────────────────────────────────────

    /// <summary>这一份模型投影出来的是实体本身时的 CLR 类型，否则 null。DTO / 标量 / 聚合投影一律落空：
    /// 它们不是「库里那一行」，没有身份映射可言——把 Orders 投影成的 OrderRow 登记进去，
    /// 得到的是一个既不能写也不该写的假实例。</summary>
    static Type? EntityClrType(QueryModel model)
        => model.Plan is EntityProjection projection && model.Tables[projection.TableIndex].Contract is { } contract
            ? contract.ClrType
            : null;

    /// <summary>物化之前先 DetectChanges 的落点。判据与 Track 同一条：只有要往身份映射里登记东西，
    /// 才需要先把已有的意图算清楚——无跟踪的读、DTO 投影的读，这条路上一次比较都不付（零额外分配要靠这个才成立）。</summary>
    void Detect(QueryModel model)
    {
        if (_tracker is { AutoDetectChangesEnabled: true } && EntityClrType(model) is not null)
            _tracker.DetectChanges();
    }

    void Track<T>(QueryModel model, List<T> rows)
    {
        if (_tracker is null)
            return;

        if (EntityClrType(model) is { } clrType)
            _tracker.TrackAll(rows, clrType);
    }

    /// <summary>聚合出口。序由 QueryModel.StripsOrdering 自动剥掉（聚合计划的 UsesOrdering 为 false），
    /// 所以"复用带 OrderBy 的链先 Count 再取页必炸"的高频事故在这里不可能复现。</summary>
    public async Task<T> AggregateAsync<T>(AggregateKind kind, LambdaExpression? key, CancellationToken ct)
    {
        _gate?.Enter();

        try
        {
            var model = AggregateModel(Effective(), kind, key);

            // 聚合查询恒定返回一行（COUNT/SUM/MIN 都是如此），所以"零行"不是空结果而是实现或方言错误。
            // 报出来比给 default 好：给 0 会让一个漏了 GROUP BY 的聚合看起来像"确实没有数据"。
            return await Orm.Ado.FirstAsync(Build(model, out _), Reader<T>(model), Tag(kind.ToString().ToLowerInvariant()), ct).ConfigureAwait(false)
                   ?? throw new QuickSqlException($"{kind} 的聚合查询没有返回任何行：聚合恒定一行，这是方言发射或模型派生的缺陷，不是空结果集。");
        }
        finally
        {
            _gate?.Exit();
        }
    }

    public Task<int> CountAsync(CancellationToken ct) => AggregateAsync<int>(AggregateKind.Count, null, ct);

    public Task<long> LongCountAsync(CancellationToken ct) => AggregateAsync<long>(AggregateKind.LongCount, null, ct);

    /// <summary>存在性探针：发「SELECT 1 … 取第一行」而不是 COUNT(*) > 0。差别是算法级的：COUNT 在大表上
    /// 是全量计数，而存在性只要库交出第一行就能收口——谓词命中越早省得越多，最坏（空集）也只与 COUNT 同阶。
    /// 投影换成常量 1（不进参数集合）、Limit = 1、序剥掉（存在性与行序无关，T-SQL 那一族的占位序由方言补）；
    /// Offset 原样保留：Skip(n).Any() 问的是"跳过 n 行之后还有没有"，不是整表。分组与 HAVING 天然成立：
    /// SELECT 1 … GROUP BY k LIMIT 1 交回一行即至少有一组。读到一行即 true，空集交回 false。</summary>
    public async Task<bool> AnyAsync(CancellationToken ct)
    {
        _gate?.Enter();

        try
        {
            var model = Effective() with
            {
                Plan = new ValueProjection(new InlineLiteral("1"), typeof(int)),
                Ordering = [],
                Limit = 1,
            };

            // int? 那一档是判据本身：FirstAsync 对空集交回 default，只有可空型才分得开"没有行"与"值为 1 的那一行"。
            var row = await Orm.Ado.FirstAsync(Build(model, out _), Reader<int?>(model), Tag("any"), ct).ConfigureAwait(false);

            return row.HasValue;
        }
        finally
        {
            _gate?.Exit();
        }
    }

    public Task<TResult> SumAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Sum, key, ct);

    public Task<TResult> MaxAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Max, key, ct);

    public Task<TResult> MinAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Min, key, ct);

    public Task<TResult> AverageAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Average, key, ct);

    internal QueryModel AggregateModel(QueryModel model, AggregateKind kind, LambdaExpression? key)
    {
        var countLike = kind is AggregateKind.Count or AggregateKind.LongCount or AggregateKind.Any;

        // 派生表那一档：这三种形状下"在原模型上换一个 COUNT 出来"得到的是一个看起来完全合理的错数字——
        // 分组之后按行数是"每组的行数"，DISTINCT 之后按行数是去重前的行数，而带页的整句换掉分页就数了全表。
        // 现在它们各发各的句子：整份模型原样进内层，外层只剩一个 COUNT(*)。
        if (NeedsDerivedTable(model))
        {
            // 只有 COUNT 一族发得出这一档：外层要引用内层的列（SELECT SUM(t0.Total) FROM (…) t0）
            // 就得先给派生行造一份列契约，而那一步今天没有形状（派生行的列名与类型只在内层的投影计划里）。
            if (!countLike)
                throw new QuickSqlException(
                    $"分组之后、或带 Skip/Take、DISTINCT 之后再 {kind} 要的是外层引用内层的列（SELECT {kind}(t0.那一列) FROM (…) t0），" +
                    "而派生行的列契约今天没有形状 —— 这一档只接 COUNT 一族。请把这一列在未分组、未分页的查询上聚合，" +
                    "或者走 §11 的原生 SQL 口（orm.Ado.QueryAsync / ExecuteScalarAsync）自己写外层。");

            return Derived(model, kind);
        }

        var scout = Scout(model);
        var plan = countLike
            ? new AggregateProjection(kind, new StarExpression(null), kind == AggregateKind.LongCount ? typeof(long) : typeof(int), Distinct: false)
            : key is null
                ? throw new QuickSqlException($"{kind} 需要值选择器，例如 q.Sum(o => o.Amount)。")
                : new AggregateProjection(kind, Orm.Expressions.Visit(key.Body, Scope.Create(model, key, scout)), key.Body.Type, Distinct: false);

        // 这一份是聚合出口就地换投影造出来的临时模型，它不进链（链上后面没有算子会再读它），
        // 所以这一颗 lambda 欠下的联表落进这一句为止：不延长 _navigation，那是链上的账。
        var aggregate = model with { Plan = plan, Ordering = [], Offset = null, Limit = null };

        return scout is null ? aggregate : scout.Flush(aggregate);
    }

    /// <summary>这一份模型能不能就地补一个聚合、还是必须先包成内层。</summary>
    static bool NeedsDerivedTable(QueryModel model)
        => model.GroupBy.Count > 0 || model.Distinct || model.Offset is > 0 || model.Limit is not null;

    /// <summary>包内层：外层清掉一切子句（它们都活在内层那一份里，重复端出去就是"同一条 WHERE 写两遍而第二遍引用不到列"），
    /// 只留表位与聚合计划。内层的序按一句库的事实处理：T-SQL 拒绝带 ORDER BY 的派生表（除非内层自己有分页，
    /// 那时那个序是 OFFSET..FETCH 的一部分，摘掉就改了页的内容），而 count 不关心序，没有分页时剥掉它既合法又省。</summary>
    static QueryModel Derived(QueryModel model, AggregateKind kind)
    {
        var inner = model.Offset is null && model.Limit is null ? model with { Ordering = [] } : model;

        var plan = new AggregateProjection(kind, new StarExpression(null), kind == AggregateKind.LongCount ? typeof(long) : typeof(int), Distinct: false);

        return model with
        {
            Plan = plan,
            Ordering = [],
            GroupBy = [],
            Having = null,
            Offset = null,
            Limit = null,
            Distinct = false,
            Predicates = [],
            Joins = [],
            Includes = [],
            IncludeManys = [],
            DerivedFrom = inner,
        };
    }

    public async Task<PagedResult<TResult>> PagedAsync<TResult>(int page, int size, PagedOptions? options, CancellationToken ct)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "页码从 1 开始。");

        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "页大小必须为正：0 会写成 FETCH NEXT 0 ROWS，读回来是空页而不是全量。");

        _gate?.Enter();

        try
        {
            return await PagedCore<TResult>(page, size, options, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate?.Exit();
        }
    }

    /// <summary>PagedAsync 的本体：取页行与探下一页（必要时再补一记 count）都在这一段里发生，闸门罩住的是它——
    /// 参数校验留在闸外，那是调用方的错，不是一次操作。</summary>
    async Task<PagedResult<TResult>> PagedCore<TResult>(int page, int size, PagedOptions? options, CancellationToken ct)
    {
        var source = Effective();
        var method = Resolve(page, options?.CountMethod ?? CountMethod.Auto);

        // 偏移与"多取一行"两步都按 long 算再收回 int：size + 1 与 (page - 1) * size 在 int.MaxValue
        // 那一档翻成负数，发出去是 LIMIT 负数（PG 2201W "LIMIT must not be negative"），读起来像库坏了，其实是这里溢的。
        // int.MaxValue 不是假想档：消费侧仓储的"取全部"缺省就是它。
        var offset = (long)(page - 1) * size;

        if (offset > int.MaxValue)
            throw new QuickSqlException(
                $"第 {page} 页、每页 {size} 行的偏移是 {offset}，超出 int 上限：这一页在任何库里都不存在。"
                + "请回退页号或改用键集分页（Skip/Take 到这一档已经没有意义）。");

        // 探下一页那一行：页大小已经顶到 int 上限时不再多取 —— 那一档下面不可能再放得下一整页，
        // 而多取一行会给库发一条负 LIMIT。此时 HasNext 恒 false，是这句话的本意而不是失真。
        var peek = method == CountMethod.SkipTotal && size < int.MaxValue ? 1 : 0;
        var items = source with { Offset = (int)offset, Limit = size + peek };
        var rows = await Rows<TResult>(items, ct).ConfigureAwait(false);

        if (method == CountMethod.SkipTotal)
            return new PagedResult<TResult>
            {
                Items = [.. rows.Take(size)],
                Page = page,
                Size = size,
                Total = null,
                HasNext = rows.Count > size,
            };

        // 页码与页大小覆盖查询上已叠的 Skip/Take —— "第 N 页"本来就是绝对位置，叠出来的偏移没人想要。
        var total = await TotalCount(source, options, ct).ConfigureAwait(false);

        return new PagedResult<TResult>
        {
            Items = [.. rows],
            Page = page,
            Size = size,
            Total = total,
            HasNext = offset + size < total,
        };
    }

    async Task<int> TotalCount(QueryModel source, PagedOptions? options, CancellationToken ct)
    {
        if (options is { StripOrderingForCount: false })
            throw new QuickSqlException("StripOrderingForCount = false 会让 count 带上 ORDER BY，而 SQL Server 与 PG 血统的库对此直接报错（§5.12 的表）。没有一种库需要这个形态。");

        var model = AggregateModel(source with { Offset = null, Limit = null }, AggregateKind.Count, key: null);

        return await Orm.Ado.FirstAsync(Build(model, out _), Reader<int>(model), Tag("count"), ct).ConfigureAwait(false);
    }

    /// <summary>CountMethod.Auto 的落位：首页只判断有没有下一页，第二页起才付 count。
    /// CountOverWindow 与 SeparateTransaction 一条都没实现——没实现就别让它看起来能用。</summary>
    static CountMethod Resolve(int page, CountMethod requested) => requested switch
    {
        CountMethod.Auto => page == 1 ? CountMethod.SkipTotal : CountMethod.CountStar,
        CountMethod.SkipTotal or CountMethod.CountStar => requested,
        CountMethod.CountOverWindow => throw new QuickSqlException("COUNT(*) OVER() 单往返（§5.12 Q1）要改投影计划（结果集多出一列总数），未接入。"),
        CountMethod.SeparateTransaction => throw new QuickSqlException("同连接同事务的两条语句（§5.12 Q3 的一致性形态）要等 §6 的事务层。"),
        _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "未知 CountMethod"),
    };

    Func<DbDataReader, TResult> Reader<TResult>(QueryModel model)
        => model.Includes.Count > 0 && model.Plan is EntityProjection && !model.SplitQuery
            ? Materializer.CreateWithIncludes<TResult>(model, Orm.Schemas)
            : Materializer.Create<TResult>(model, Orm.Schemas);

    ExecutionTag Tag(string operation) => new(operation, _tables, _cluster, Model.Role);

    /// <summary>缓存只在 WithCache 与 ICacheAdapter 同时就位时生效。宿主没装缓存适配器时
    /// 这一层就是没有缓存——结果集不受影响，不是降级，不需要留痕。</summary>
    List<TResult>? Cached<TResult>(QueryModel model)
    {
        if (model.CacheKey is not { Length: > 0 } key || Orm.Aop.Cache is not { } cache)
            return null;

        return cache.TryGet<List<TResult>>(key, out var hit) ? hit : null;
    }

    void Store<TResult>(QueryModel model, List<TResult> rows)
    {
        if (model.CacheKey is { Length: > 0 } key && model.CacheTtl is { } ttl && Orm.Aop.Cache is { } cache)
            cache.Set(key, rows, ttl);
    }
}

/// <summary>算子的稳定标识：链式方法与 Queryable.* 调用都归到这里，所以两侧不可能各写一套语义。</summary>
internal enum QueryOp
{
    Where,
    Select,
    OrderBy,
    OrderByDescending,
    ThenBy,
    ThenByDescending,
    Skip,
    Take,
    Distinct,
}
