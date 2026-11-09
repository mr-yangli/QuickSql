using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using QuickSql.Query.Model;
using QuickSql.Sql.Ast;
using QuickSql.Translation;

namespace QuickSql.Query.Filters;

/// <summary>全局过滤器（orm.Filters）的登记处与唯一落点：贴进哪一张表、贴进那一句的哪一段。
/// 组装根上的 QuickSqlOrm.Filters 就是这一只，一个 orm 实例一份。这里存的是 lambda 本身，
/// 每一次出口（ToList/Count/ToPaged/Stream/Explain/ToSql 全部经 QueryEngine.Effective 那一个咽喉）
/// 拿当下的表位重新翻译——缓存翻译结果会把闭包第一次执行时那颗租户 id 冻进后面所有语句，
/// 换租户的那一次不报错、只会读到上一个租户的行，是比根本没装过滤器更糟的形态。
/// 代价写在明处：每条语句多付一次谓词翻译，这一档今天没有任何形式的缓存，是设计现状不是漏了。</summary>
public sealed class FilterRegistry
{
    readonly QuickSqlOrm _orm;

    /// <summary>登记是启动期的事（一次），读取是每条语句的事（无数次），所以写侧加锁换整表、读侧不加锁。
    /// volatile 落在这里而不是靠 lock 的栅栏：HasAny 要在读路径的热身位上被问一次，
    /// 它不进锁——进了锁就等于每条查询都要为"可能一个人也没注册"付一次 monitor 成本。</summary>
    readonly Lock _gate = new();

    volatile Registration[] _items = [];

    internal FilterRegistry(QuickSqlOrm orm) => _orm = orm;

    /// <summary>有没有任何一个过滤器在册。读侧与两条写链都靠它在没人用过滤器时整段短路：
    /// 不设短路的话，一条没有过滤器的查询要为"可能有人在册"多付一次列表与一次翻译。</summary>
    internal bool HasAny => _items.Length > 0;

    /// <summary>注册一个全局过滤器。键可以是实体本身，也可以是它实现的接口或基类：
    /// 表位的实体类型能被键接住（IsAssignableFrom 反过来读），这个过滤器就对那张表生效。
    /// mandatory 为 true 时这条链上没有任何口子能把它关掉（IgnoreQueryFilters() 当场抛），默认 false。
    /// 谓词存下来的是 lambda 而不是文本，注册这一趟会试翻译一次，所以谓词必须无副作用。</summary>
    [RequiresUnreferencedCode("试翻译过滤器谓词要反射读闭包成员（与读侧 Where 同一套）。")]
    [RequiresDynamicCode("试翻译过滤器谓词的退路是编译表达式树。")]
    public FilterRegistry Add<TEntity>(Expression<Func<TEntity, bool>> predicate, bool mandatory = false) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);

        // 先试翻译、后登记：反过来的话，一条翻不动的谓词会留在册子上，而抛出去的那一句会让人以为没注册上。
        Probe(typeof(TEntity), predicate);

        lock (_gate)
            _items = [.. _items, new Registration(typeof(TEntity), predicate, mandatory)];

        return this;
    }

    /// <summary>注册期按"这张表"翻一遍，翻不动就在这里抛，不等第一次查询。
    /// 结果丢掉不是浪费——要的是那一次翻译走得通这个结论。接口与抽象基类键没有列契约，跳过。</summary>
    [RequiresUnreferencedCode("转调 ExpressionTranslator。")]
    [RequiresDynamicCode("转调 ExpressionTranslator。")]
    void Probe(Type key, LambdaExpression predicate)
    {
        if (key.IsInterface || key.IsAbstract)
            return;

        Translate(new Registration(key, predicate, false), _orm.NewTableRef(key, 0));
    }

    /// <summary>表位上在册的过滤器，顺序 = 注册序，与读侧多次 Where 的折叠方向同一条纪律
    /// （两侧都是往末尾追加），所以（用户条件 AND 过滤器1 AND 过滤器2）这个形状在读写两条链上一致。</summary>
    internal IReadOnlyList<Registration> For(Type? entity)
    {
        if (entity is null || _items.Length == 0)
            return [];

        List<Registration>? matched = null;

        foreach (var item in _items)
            if (item.Key.IsAssignableFrom(entity))
                (matched ??= []).Add(item);

        return matched ?? [];
    }

    internal IReadOnlyList<Registration> For(TableRef table) => For(table.Contract?.ClrType);

    /// <summary>写侧两条链（IUpdate / IDelete）的叠加口。它接在 Guard.Whereless 之后：
    /// 全局过滤器不是"你说的条件"，一条一个 Where 都没写的 UPDATE 不能因为册子上有过滤器就获得免死。
    /// 传进来的表位是那条语句自己的表位（自引用名由方言的 WriteTargetAlias 定）。</summary>
    [RequiresUnreferencedCode("谓词翻译走闭包常量求值。")]
    [RequiresDynamicCode("谓词翻译的退路是编译表达式树。")]
    internal SqlExpression ApplyTo(TableRef table, SqlExpression where)
    {
        var current = where;

        foreach (var registration in For(table))
            current = And(current, Translate(registration, table));

        return current;
    }

    /// <summary>这一张表上贴得进去几条过滤器，只给 Explain().Reason 那句话用：
    /// 一条链式 UPDATE 的自述必须说清 WHERE 里混进了过滤器，那与调用方那两句的命中范围不是一回事。</summary>
    internal int CountFor(Type entity) => For(entity).Count;

    /// <summary>读侧的注入口：把在册的过滤器贴进这份模型的 WHERE 或那一个 ON。
    /// 返回同一个实例表示什么都没贴（这张表上没过滤器），于是调用方的 with 也不会发生。
    /// ignored 是这条链点名要跳过的那些注册键：它在匹配之后、翻译之前问，被点名的那一条连翻译都不付。</summary>
    [RequiresUnreferencedCode("谓词翻译走闭包常量求值。")]
    [RequiresDynamicCode("谓词翻译的退路是编译表达式树。")]
    internal QueryModel Apply(QueryModel model, IReadOnlyList<Type>? ignored = null)
    {
        List<SqlExpression>? where = null;
        List<JoinClause>? joins = null;
        Dictionary<int, SqlExpression>? includeOn = null;

        for (var i = 0; i < model.Tables.Count; i++)
        {
            var table = model.Tables[i];

            foreach (var registration in For(table))
            {
                if (ignored is not null && IsIgnored(registration.Key, ignored))
                    continue;

                var node = Translate(registration, table);

                // LEFT 那一档进 ON，其余进 WHERE：把右表的过滤器写进 WHERE，未匹配的补 NULL 行会被它一起筛掉
                //（NULL 上任何比较都是 UNKNOWN），左联就静默退化成内联。根表与 INNER 两侧等价，放 WHERE 与用户的条件同一个形状。
                // RIGHT / FULL / CROSS 今天从链式走不到，补齐那三种联表形态时这一条判据要一起重看。
                if (i > 0 && i <= model.Joins.Count && model.Joins[i - 1].Kind == JoinKind.Left)
                {
                    // 从 joins 里读而不是从 model.Joins：同一张右表上两条过滤器要叠在同一份已有 ON 上，
                    // 读原文会丢掉前一条。
                    joins ??= [.. model.Joins];

                    var at = i - 1;
                    var clause = joins[at];

                    joins[at] = clause with { On = clause.On is null ? node : And(clause.On, node) };

                    continue;
                }

                // Include 的 LEFT JOIN 不在模型 Joins 里（编译期现造），上面那一岔对表位 tN 永远判不中：
                // 落进 WHERE 会让左联静默退化成内联、或 Count 档剩下一句引用不到表位的列。所以这里折进那颗节点自己的 ON。
                // 折的是副本，不是那颗节点：节点实例挂在链上、被同一份链的下一次执行共用，
                // 就地改写会让第二次执行把同一条过滤器再叠一遍（ON 越长越离谱而结果不变，是最难查的那一类）。
                if (model.Includes.Count > 0 && Covers(model.Includes, i))
                {
                    if (includeOn is null)
                        includeOn = new Dictionary<int, SqlExpression>();

                    includeOn[i] = includeOn.TryGetValue(i, out var stacked) ? And(stacked, node) : node;

                    continue;
                }

                (where ??= []).Add(node);
            }
        }

        if (where is null && joins is null && includeOn is null)
            return model;

        var result = model;

        if (where is not null)
            result = result with { Predicates = [.. result.Predicates, .. where] };

        if (joins is not null)
            result = result with { Joins = joins };

        if (includeOn is not null)
            result = result with { Includes = Fold(result.Includes, includeOn) };

        return result;
    }

    /// <summary>这一颗表位是不是 Include 树里某个节点的目标表（Include 的表位正是按模型表位数取的号）。</summary>
    static bool Covers(IReadOnlyList<IncludeNode> includes, int index)
    {
        foreach (var include in includes)
            if (include.TargetTable.Index == index || Covers(include.Children, index))
                return true;

        return false;
    }

    /// <summary>把 on 里那些过滤器折进对应节点的 ON，返回一份新树。
    /// 只有真的被折到的那一颗连同它的每一层祖先才换实例：祖先不换的话，改写的就不是副本而是原树
    /// （子节点列表在父节点身上），下一次执行就又踩回就地改写那一格。没被点到的子树原样交回，不复制。</summary>
    static IReadOnlyList<IncludeNode> Fold(IReadOnlyList<IncludeNode> includes, Dictionary<int, SqlExpression> on)
    {
        List<IncludeNode>? result = null;

        for (var i = 0; i < includes.Count; i++)
        {
            var node = includes[i];
            var children = Fold(node.Children, on);
            var extra = on.TryGetValue(node.TargetTable.Index, out var expression) ? expression : null;

            if (extra is null && ReferenceEquals(children, node.Children))
                continue;

            var rewritten = new IncludeNode
            {
                NavigationMemberName = node.NavigationMemberName,
                Relationship = node.Relationship,
                TargetTable = node.TargetTable,
                OnCondition = extra is null ? node.OnCondition : And(node.OnCondition, extra),
            };

            foreach (var child in children)
                rewritten.Children.Add(child);

            (result ??= [.. includes])[i] = rewritten;
        }

        return result ?? includes;
    }

    /// <summary>IgnoreQueryFilters() 撞上 mandatory 就抛。调用点有两处、两处都要：Ignore 当场问一次
    /// （报错落在写那句 Ignore 的人手里，而不是落在一次执行里），每一次出口再问一次
    /// （否则先 Ignore 再 LeftJoin 一张带 mandatory 过滤器的表就是那条约束上留下的洞）。
    /// ignored 非空时只判被点名要跳的那些：点名关一条过滤器并没有关别的条，拦它就是把约束写过头。</summary>
    internal void AssertIgnorable(IReadOnlyList<TableRef> tables, IReadOnlyList<Type>? ignored = null)
    {
        if (!HasAny)
            return;

        foreach (var table in tables)
            foreach (var registration in For(table))
            {
                if (ignored is not null && !IsIgnored(registration.Key, ignored))
                    continue;

                if (registration.Mandatory)
                    throw new QuickSqlException(
                        $"{table.Table.Name}（表位 {table.Alias}）上有一条 mandatory 的全局过滤器（注册自 {registration.Key.Name}），" +
                        "它不能被 IgnoreQueryFilters() 关掉 —— 规格 §10.7 约束 2 那句「多租户隔离不能靠约定大家都记得加条件」" +
                        "说的就是这一档：能关掉的那一条不叫隔离。要跳过它只有两条路：注册时别写 mandatory，" +
                        "或者不走这条链、直接把自己的条件写全（那一句是你负责的，不是库默认帮你省的）。");
            }
    }

    /// <summary>这一条注册是不是被 ignored 里某一颗点名跳过的。
    /// 判据是注册键的同一性，不是继承关系：点名点的是那一格注册，不是"某个类型的所有过滤器"。
    /// 一族式的匹配看着省事，但它在要命的方向上漏：点名 ISoftDelete 会连注册在实体上的租户过滤器一起跳掉，
    /// 而静默少一个租户条件是这一格唯一不能被原谅的失败形态。object 是唯一的例外，而它不是特例是顶类型：
    /// 不带参数那颗 IgnoreQueryFilters() 递进来的就是它。</summary>
    static bool IsIgnored(Type key, IReadOnlyList<Type> ignored)
    {
        foreach (var type in ignored)
            if (type == key || type == typeof(object))
                return true;

        return false;
    }

    /// <summary>过滤器谓词 → AST，并且把翻译失败换成一句点名"是谁贴不进谁"的话。
    /// 翻译器只知道 lambda 与表位，不知道这个 lambda 是用户当场写的还是启动期从另一只实体上注册来的。</summary>
    [RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    internal SqlExpression Translate(Registration registration, TableRef table)
        => TranslateInto(
            registration.Predicate,
            table,
            $"{registration.Key.Name} 上注册的全局过滤器",
            registration.Key);

    /// <summary>写链上调用方自己点名的那一颗条件（UpdateRange(rows).Where(...)）走这一颗口。
    /// 它与 Translate 共用同一份实现不是省事：全局过滤器与用户 lambda 必须经同一个翻译器，
    /// 否则同一条语句里两类条件的形状与失败口径会分岔。这里没有接口键那一档（调用方给的就是这一张实体上的 lambda）。</summary>
    [RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    internal SqlExpression TranslateFree(LambdaExpression predicate, TableRef table)
        => TranslateInto(predicate, table, "调用方点名的条件", null);

    [RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    SqlExpression TranslateInto(LambdaExpression predicate, TableRef table, string owner, Type? key)
    {
        // 形参按位置绑表，而过滤器的 lambda 只有一个形参，所以这里造的是"只有这一张表"的模型：
        // 表位实例是外面递进来的那一个，翻出来的列引用因此带的就是最终语句里的那个别名。
        var model = new QueryModel { Tables = [table], Plan = new EntityProjection(0) };

        try
        {
            return _orm.Expressions.Predicate(predicate, Scope.ForFilter(model, predicate));
        }
        catch (QuickSqlException exception)
        {
            throw new QuickSqlException(
                $"{owner}贴不进 {table.Table.Name}（表位 {table.Alias}）。" +
                (key is not null && (key.IsInterface || key.IsAbstract)
                    ? $"键是接口/基类，而这一档要成立得让每一个实现类的契约里都有谓词点到的那个成员 —— 注册期查不出来（没有契约可查），只有贴上某张表时才知道。"
                    : string.Empty) + $"翻译器原话：{exception.Message}",
                exception);
        }
    }

    static SqlExpression And(SqlExpression left, SqlExpression right)
        => new SqlBinaryExpression(BinaryOperator.And, left, right);

    /// <summary>一条注册。内部类型：它带着一整份 lambda 表达式，
    /// 公开它就等于公开"过滤器可以按运行期 Type 塞进来"这条路，而那条口要的是非泛型签名，另处决定。</summary>
    internal sealed record Registration(Type Key, LambdaExpression Predicate, bool Mandatory);
}
