using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Query.Model;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace QuickSql.Query;

/// <summary>树形闭包：三颗口（Descendants / Ancestors / ToTree）共用这一颗内核。
/// 取数只有两种形状——整片一句 SELECT，或按锚点逐层爬（每层一句 IN，按参数预算分块）；
/// 两种都不要求库端有递归语法，所以 19 家 provider 交回同一份语义。
/// Depth / Path / 环 / 孤儿全在缝合这一段算，库里一个字都不多要。</summary>
sealed partial class QueryEngine
{
    /// <summary>接树那一刻就把形状守卫与父键认列做完：报错响在链上，不是等到执行期发了一句才发现。
    /// 只单向拦（树口接在急切加载/拆查之后要拒）：反方向没有出口——三颗树口交回的是 <c>ITreeQuery&lt;T&gt;</c>，
    /// 那上面根本没有 Include/IncludeMany/AsSplitQuery 几颗口，所以"先点树再挂加载器"这件事在类型上就写不出来，
    /// 为此加一份运行期护栏就是死代码。与 AsSplitQuery↔IncludeMany 那一对双向护栏的差别在此。</summary>
    internal QueryEngine Tree(LambdaExpression parentSelector, TreeDirection direction, object[] anchors)
    {
        ArgumentNullException.ThrowIfNull(parentSelector);
        ArgumentNullException.ThrowIfNull(anchors);

        if (Model.Plan is not EntityProjection root)
            throw new QuickSqlException(
                "树形查询只能在实体整行查询上使用：投影之后 Depth/ParentKey 无处落，缝合也没有实体可挂。把树口放到 Select 之前。");

        if (Model.IncludeManys.Count > 0 || Model.Includes.Count > 0)
            throw new QuickSqlException(
                "树形查询与急切加载（Include / IncludeMany）不接在同一句链上：两者都要在执行期另发句子，" +
                "叠起来句数就读不出账，而 Include 的 JOIN 展开列还会让闭包行的列清单与实体对不上。要孩子就取完树另发一条。");

        if (Model.SplitQuery)
            throw new QuickSqlException(
                "树形查询与 AsSplitQuery() 不接在同一句链上：拆查改的是引用导航的取数路，树改的是整条链的取数路，" +
                "两颗策略叠在一句上执行分派只能有一颗赢，另一颗无声蒸发。去掉 AsSplitQuery()，或把树口单独成句。");

        if (Model.Source is not null || Model.DerivedFrom is not null || Model.Joins.Count > 0)
            throw new QuickSqlException(
                "树形查询只落在单表实体链上：WithSql 覆盖源、派生表、集合运算与联表都会换掉根表位或列清单，" +
                "而闭包的每一句都要按同一张表位发 IN 臂。要联表就取完树再按主键另查。");

        if (Model.Shards is not null || Model.Root.Table.Split is not null)
            throw new QuickSqlException(
                "树形查询第一版不接分表实体：锚点所在片与后代所在片可以不同，跨片闭包的路由没量过。" +
                "先在单片上取树，或按片各取一次再在应用侧合。");

        if (Model.GroupBy.Count > 0 || Model.Having is not null)
            throw new QuickSqlException("树形查询不接分组：分组计划的选列是键加聚合，与实体列对不上，缝合没有行可挂。");

        if (Model.Offset is not null || Model.Limit is not null)
            throw new QuickSqlException(
                "树形查询不接 Skip/Take：分页截的是闭包的一段，截出来的行父不一定在结果里，交回的是残段而不是树。" +
                "要限量就用 Depth(int) 限层，或取完树在应用侧裁。");

        // WithCache 只在整片那一档有意义（它缓存的是取片那一句的结果）。逐层爬是多句，
        // 一颗键缓存不了整趟闭包；接了就是"点了名却不生效"那一类，所以在这里拒而不是静默不接。
        if (Model.CacheKey is not null && direction != TreeDirection.Slice)
            throw new QuickSqlException(
                "WithCache 只接 ToTree（整片一句那一档）：Descendants / Ancestors 是多句闭包，" +
                "缓存键对应的不是整趟取数。要缓存这一片就先用 Where 走 ToTree，或把结果自己存。");

        var contract = Model.Tables[root.TableIndex].Contract
            ?? throw new QuickSqlException($"表位 {Model.Tables[root.TableIndex].Alias} 没有实体契约，认不出父键列。");

        if (contract.PrimaryKey.Length != 1)
            throw new QuickSqlException(
                $"树形查询第一版只接单列主键（{contract.ClrType.Name} 的主键有 {contract.PrimaryKey.Length} 列）：" +
                "闭包的键集与父键臂都要按一颗列发 IN，复合键的逐行 OR 链在层与层之间会放大成叉积。");

        var parentColumn = ResolveTreeParentColumn(contract, parentSelector);

        // 父键与主键必须同一种值：缝合拿父键去命中键表，而逐层爬把上一层的键原样发回 IN 臂。
        // 主键 bigint 配父键 int 这种形状，句子里两侧的值根本不是同一个 CLR 型，
        // 字典永远命不中 ⇒ 交回一棵"每行都是根"的扁表，而库里一句错都不报。所以接链就拒。
        if (UnderlyingType(parentColumn.ClrType) != UnderlyingType(contract.PrimaryKey[0].ClrType))
            throw new QuickSqlException(
                $"树形查询的父键 {contract.ClrType.Name}.{parentColumn.MemberName}（{UnderlyingType(parentColumn.ClrType).Name}）" +
                $"与主键 {contract.PrimaryKey[0].MemberName}（{UnderlyingType(contract.PrimaryKey[0].ClrType).Name}）不是同一种值：" +
                "闭包靠两边同型的键互认，宽窄不同的两列在库里能连、在内存里对不上。把父键列改成与主键同型（可空性不计这一格）。");

        foreach (var anchor in anchors)
            if (anchor is null)
                throw new QuickSqlException("树形查询的锚点主键不能为 null：null 不是键，IN 臂里它也永远命不中行。");

        // 整片那一档必须有片界定：没有 Where 也没有过滤器就是全表搬进内存，那不是查询是事故。
        if (direction == TreeDirection.Slice && Filtered().Predicates.Count == 0)
            throw new QuickSqlException(
                "ToTree 需要片界定条件：这条链上既没有 Where 也没有任何生效的全局过滤器，取片等于全表搬进内存。" +
                "给一个 Where（租户/域/根条件），或改用 Descendants 从锚点逐层爬。");

        // 锚点数组在接链那一刻拷贝：本体是调用方 params 现场造的那一只，链式对象不可变那一档纪律连它也要算上
        // —— 此后调用方就地改内容，这条链的规范文本、StableKey 与执行期的 IN 臂都按接链时的锚点算。
        return Derive(Model with
        {
            Tree = new TreeRequest
            {
                Direction = direction,
                ParentColumn = parentColumn,
                Anchors = [.. anchors],
            },
        });
    }

    /// <summary>树形链的 SQL 出口：整片档给那一句取片；锚点档给锚点那一句的逻辑形状
    /// （主键 IN 全部锚点 + 链上谓词与过滤器）。执行期按参数预算分块，实际句数看 AOP 播报——
    /// 一颗 string 装不下多句，假装装得下就是诊断口骗人。</summary>
    internal DialectSql TreeFirstSql(out SelectStatement ast)
    {
        var model = Effective();
        var tree = model.Tree ?? throw new QuickSqlException("这一条链不是树查询：树口没有接上。");

        if (tree.Direction == TreeDirection.Slice)
            return Build(model, out ast);

        if (tree.Anchors.Count == 0)
            throw new QuickSqlException(
                "锚点为空：这一档一句 SQL 都不发（执行期直接交回空集），所以没有句子可给。" +
                "要看形状就给至少一颗锚点主键。");

        var contract = model.Tables[((EntityProjection)model.Plan).TableIndex].Contract!;
        var table = model.Root;
        var projections = new Projection[contract.Columns.Length];

        for (var i = 0; i < projections.Length; i++)
            projections[i] = new Projection(new ColumnExpression(table, contract.Columns[i]), null);

        var values = new List<SqlExpression>(tree.Anchors.Count);

        foreach (var anchor in tree.Anchors)
            values.Add(new ParameterValue(anchor, null));

        SqlExpression where = new InExpression(new ColumnExpression(table, contract.PrimaryKey[0]), values);

        if (QueryCompiler.Fold(model.Predicates) is { } extra)
            where = new SqlBinaryExpression(BinaryOperator.And, where, extra);

        ast = new SelectStatement
        {
            Columns = projections,
            From = new SourceTable(table),
            Where = where,
            OrderBy = model.Ordering,
        };

        var dialect = Orm.Provider.Dialect;
        var writer = new SqlWriter(new(QueryCompiler.EstimateSqlLength(ast)), new(), dialect.ParameterPrefix);
        dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    internal QueryPlan TreeExplain()
    {
        var sql = TreeFirstSql(out var ast);

        return new QueryPlan(ast, sql, Effective().StableKey, []) { Provider = Orm.Provider };
    }

    /// <summary>父键认列：只收实体自己的直接属性访问，且那一颗必须是契约里的列。
    /// 这一段不借 <c>ExtractMemberName</c>（那颗的消息指名 Include/ThenInclude，而这条链上一颗 Include 都没有：
    /// 报错要指名说错话的是哪一颗口）。可空性不计——主键 int、父键 int? 是同一族形状。</summary>
    static ColumnContract ResolveTreeParentColumn(EntityContract contract, LambdaExpression parentSelector)
    {
        if (parentSelector.Body is not MemberExpression member)
            throw new QuickSqlException(
                $"Descendants/Ancestors/ToTree 的父键 lambda 必须是简单的属性访问（如 n => n.ParentId），" +
                $"实体 {contract.ClrType.Name} 这一句给的是 {parentSelector.Body.NodeType}。");

        if (!contract.ByMember.TryGetValue(member.Member.Name, out var column))
            throw new QuickSqlException(
                $"实体 {contract.ClrType.Name} 上的 {member.Member.Name} 不是一颗列，认不出树形查询的父键。" +
                "父键要指向本实体自己的主键（自引用），导航属性与计算属性在这一档没有出口。");

        return column;
    }

    /// <summary>剥掉 Nullable 外壳的那一颗型：可空性不算"不同一种值"，int 与 long 才算。</summary>
    static Type UnderlyingType(Type clrType) => Nullable.GetUnderlyingType(clrType) ?? clrType;

    internal QueryEngine TreeDepth(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        var tree = Model.Tree ?? throw new QuickSqlException("Depth(int) 只能接在树口（Descendants / Ancestors）之后。");

        return Derive(Model with { Tree = tree with { MaxDepth = max } });
    }

    internal QueryEngine TreeIncludeSelf(bool include)
    {
        var tree = Model.Tree ?? throw new QuickSqlException("IncludeSelf(bool) 只能接在树口（Descendants / Ancestors）之后。");

        return Derive(Model with { Tree = tree with { IncludeSelf = include } });
    }

    /// <summary>树形出口。整片走 Rows（过滤/缓存/跟踪与任何一句查询同一条咽喉），
    /// 锚点闭包走逐层爬；两者交回同一颗缝合器，所以"行与节点对不上"没有存在的空间。</summary>
    internal async Task<List<TreeNode<T>>> TreeForestAsync<T>(CancellationToken ct)
    {
        _gate?.Enter();

        try
        {
            var tree = Model.Tree ?? throw new QuickSqlException("这一条链不是树查询：树口（Descendants / Ancestors / ToTree）没有接上。");
            var model = Effective();
            var contract = model.Tables[((EntityProjection)model.Plan).TableIndex].Contract!;

            // 键与父键的读口按列序取自 schema：G1 那一侧是生成期写死的强转，G2 那一侧是启动期编译好的委托表。
            // 热路径上因此没有"按名字反射读成员"这一步（口径见 IEntitySchemaRegistry.Accessors）。
            var accessors = (Schemas ?? Orm.Schemas).Accessors(contract.ClrType);
            var keyOrdinal = Array.IndexOf(contract.Columns, contract.PrimaryKey[0]);
            var parentOrdinal = Array.IndexOf(contract.Columns, tree.ParentColumn);

            List<T> rows;

            if (tree.Direction == TreeDirection.Slice)
                rows = await Rows<T>(model, ct).ConfigureAwait(false);
            else
                rows = await CrawlAsync<T>(model, tree, contract, accessors, keyOrdinal, parentOrdinal, ct).ConfigureAwait(false);

            return Stitch(rows, accessors, keyOrdinal, parentOrdinal);
        }
        finally
        {
            _gate?.Exit();
        }
    }

    /// <summary>逐层爬：锚点一句（主键 IN 锚点集），之后每层一句——子集档按父键 IN 上一层键集，
    /// 父级档按主键 IN 上一层父键集。每层只收没见过的键（第一道拦环），层数到上限就停（第二道）。
    /// IN 臂按参数预算分块，复用 IncludeMany 那颗共用发射段，预算口径全仓一份。</summary>
    async Task<List<T>> CrawlAsync<T>(
        QueryModel model, TreeRequest tree, EntityContract contract,
        Schema.IBoxedEntityAccessor accessors, int keyOrdinal, int parentOrdinal, CancellationToken ct)
    {
        var rows = new List<T>();

        if (tree.Anchors.Count == 0)
            return rows;

        var table = model.Root;
        var keyColumn = contract.PrimaryKey[0];
        var read = (Schemas ?? Orm.Schemas).ReadDelegate(contract.ClrType);
        var rowMap = Materializer.EntityRows<object>(read, contract);

        // 过滤器与链上 Where 落到每一句：闭包行与锚点行同一份口径。代价写在册子里——
        // 链上被筛掉的行会让它的子树整段读不到（与软删父节点同一形状）。
        var extra = QueryCompiler.Fold(model.Predicates);
        var visited = new HashSet<object>(StructuralComparer.Instance);
        var pending = DistinctKeys(tree.Anchors);
        var anchorLevel = true;

        for (var depth = 0; pending.Count > 0 && depth <= tree.MaxDepth; depth++)
        {
            // 锚点句恒按主键认；子集档往下一层改按父键认，父级档恒按主键认（IN 的是上一层的父键集）。
            var inColumns = anchorLevel || tree.Direction == TreeDirection.Ancestors
                ? new[] { keyColumn }
                : [tree.ParentColumn];

            var found = await QueryInBatchesAsync(
                pending, inColumns, table, contract, rowMap, model.Ordering,
                Tag(anchorLevel ? "tree-anchor" : "tree-level"), ct, extra).ConfigureAwait(false);

            var next = new List<object>();

            foreach (var o in found)
            {
                var key = accessors.GetValue(o, keyOrdinal)!;

                if (!visited.Add(key))
                    continue;

                if (!anchorLevel || tree.IncludeSelf)
                    rows.Add((T)o);

                if (depth >= tree.MaxDepth)
                    continue;

                if (tree.Direction == TreeDirection.Descendants)
                    next.Add(key);
                else if (accessors.GetValue(o, parentOrdinal) is { } parentKey && !visited.Contains(parentKey))
                    next.Add(parentKey);
            }

            pending = DistinctKeys(next);
            anchorLevel = false;
        }

        Detect(model);

        // 会话身份映射就在这一步里：TrackAll 就地写回列表元素（「会话里的那一只」可能与物化出来的那只
        // 不是同一只），所以这里不要再自己问一次 CanonicalOf——问了也不改变结果，只会让人以为
        // 缝合读的是另一批实例。缝合发生在 Track 之后，拿到的必然是会话认的那一批。
        Track(model, rows);

        return rows;
    }

    /// <summary>缝合：一次遍历分桶 + 一次 BFS 落 Depth/Path，O(n)。
    /// 找不到父的行升为根（孤儿即根），环上剩下的行同样升为根——一行都不静默丢。
    /// 孩子的顺序 = 取数顺序，不在内存里重排。
    /// 键表一律用 <see cref="StructuralComparer"/>：默认比较器对 byte[] 走引用相等，
    /// 而每一趟物化都新建一只 byte[] —— 那样一来每个节点都"找不到父"，交回的是扁表而不是树。</summary>
    static List<TreeNode<T>> Stitch<T>(
        List<T> rows, Schema.IBoxedEntityAccessor accessors, int keyOrdinal, int parentOrdinal)
    {
        var nodes = new List<TreeNode<T>>(rows.Count);
        var byKey = new Dictionary<object, TreeNode<T>>(rows.Count, StructuralComparer.Instance);

        foreach (var row in rows)
        {
            var node = new TreeNode<T>
            {
                Value = row,
                Key = accessors.GetValue(row!, keyOrdinal)!,
                ParentKey = accessors.GetValue(row!, parentOrdinal),
            };

            nodes.Add(node);
            byKey.TryAdd(node.Key, node);
        }

        var byParent = new Dictionary<object, List<TreeNode<T>>>(nodes.Count, StructuralComparer.Instance);
        var roots = new List<TreeNode<T>>();

        foreach (var node in nodes)
        {
            // 自父（父键 = 自己的键）按根收：它没有可挂的上一级，当成孩子会造出一颗只含自己的环。
            if (node.ParentKey is { } parentKey
                && byKey.TryGetValue(parentKey, out var parent)
                && !ReferenceEquals(parent, node))
            {
                if (!byParent.TryGetValue(parentKey, out var siblings))
                {
                    siblings = [];
                    byParent[parentKey] = siblings;
                }

                siblings.Add(node);
            }
            else
            {
                node.IsRoot = true;
                roots.Add(node);
            }
        }

        var seen = new HashSet<TreeNode<T>>(nodes.Count, ReferenceEqualityComparer.Instance);

        foreach (var root in roots)
            Walk(root, seen, byParent);

        // 环上的行：每一颗都有父可挂，于是没有一颗进得了 roots。把它们逐颗升为根再走一遍，
        // 环在哪一颗断开由取数序决定（确定性来自 rows 的顺序，不来自字典的枚举序）。
        foreach (var node in nodes)
        {
            if (seen.Contains(node))
                continue;

            node.IsRoot = true;
            roots.Add(node);
            Walk(node, seen, byParent);
        }

        return roots;
    }

    static void Walk<T>(TreeNode<T> root, HashSet<TreeNode<T>> seen, Dictionary<object, List<TreeNode<T>>> byParent)
    {
        root.Depth = 0;
        root.Path = KeyText(root.Key);

        var queue = new Queue<TreeNode<T>>();
        queue.Enqueue(root);
        seen.Add(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (!byParent.TryGetValue(node.Key, out var children))
                continue;

            foreach (var child in children)
            {
                if (!seen.Add(child))
                    continue;

                child.Depth = node.Depth + 1;
                child.Path = node.Path + "/" + KeyText(child.Key);
                node.AddChild(child);
                queue.Enqueue(child);
            }
        }
    }

    /// <summary>前序摊平：与森林同一次缝合的另一种交法。显式栈，深树不靠调用栈。</summary>
    internal static List<TreeRow<T>> Flatten<T>(List<TreeNode<T>> forest)
    {
        var rows = new List<TreeRow<T>>();
        var stack = new Stack<TreeNode<T>>();

        for (var i = forest.Count - 1; i >= 0; i--)
            stack.Push(forest[i]);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            rows.Add(new TreeRow<T>
            {
                Value = node.Value,
                Key = node.Key,
                ParentKey = node.ParentKey,
                Depth = node.Depth,
                Path = node.Path,
            });

            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }

        return rows;
    }

    /// <summary>键进 Path 串的那一格：二进制按十六进制写，否则每一段都是 "System.Byte[]"，
    /// Path 就成了看不出层级的装饰。与 QueryModelPrinter.AnchorText 同一口径（两处都是"键的规范形态"）。</summary>
    static string KeyText(object key) => key switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
    };

    static List<object> DistinctKeys(IReadOnlyList<object> keys)
    {
        var seen = new HashSet<object>(keys.Count, StructuralComparer.Instance);
        var distinct = new List<object>(keys.Count);

        foreach (var key in keys)
            if (seen.Add(key))
                distinct.Add(key);

        return distinct;
    }
}
