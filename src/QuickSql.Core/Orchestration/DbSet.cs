using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text;
using QuickSql.Execution;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.Tracking;

namespace QuickSql;

/// <summary>某一类实体在一个会话里的入口。链式全部可用，from…in 不可用（跟踪集合不是 IQueryable）——
/// 本类不新增任何查询算子，只把 QuickSqlQuery 那条链和会话的跟踪器接在一起：db.Orders.Where(...) 得到的
/// IQuery〈Order〉仍然跟踪（派生出来的引擎还带着会话），AsNoTracking() 之后才是同一个模型的无跟踪视图
/// ——标记位在引擎上，不在模型里。它不是缓存：每次 Set〈T〉() 得到同一个对象（同一份身份映射），
/// 但链式算子每次产出新值，两者不矛盾。</summary>
public sealed class DbSet<T> : QuickSqlQuery<T>, ITrackedSet<T> where T : class
{
    /// <summary>本类型的档。构造时就先建好：物化路径按 Type 查档，查不到档意味着这一行没人跟——
    /// 那是"看起来跟了其实没跟"，本仓库不接受这种降级。</summary>
    readonly EntityTracker<T> _tracker;

    /// <summary>会话那一份调度器。本型的档不够用：补载（Load）要把子型的实体缝进父的导航，
    /// "这一行在会话里是哪一只"要按子型那一档问。留会话、不留一堆档，是因为档按 Type 查就好。</summary>
    readonly ChangeTracker _session;

    LocalView<T>? _local;

    /// <summary>这两个标注是从 RootEngine 传下来的入口税，不是本类自己反射：写侧成员一个都没有。</summary>
    [RequiresUnreferencedCode("根模型要按 Type 向注册表取实体契约（G2）。")]
    [RequiresDynamicCode("G2 注册表编译读写委托。")]
    internal DbSet(DbContext context)
        : base(context.Orm.RootEngine(typeof(T), context.Tracker, context.Gate, context.ModelSchemas))
    {
        _tracker = context.Tracker.For<T>();
        _session = context.Tracker;
        // 会话级模型覆写：Load/ThenInclude 那几条按子型现取契约的路改问同一份，
        // 否则主查询按覆写模型、补载按组装根模型——半句人话两种模型，比全错更难查。
        _contextSchemas = context.ModelSchemas;
    }

    readonly Schema.IEntitySchemaRegistry? _contextSchemas;

    /// <summary>补载那条路用的注册表：覆写在场用覆写，缺席用组装根的。</summary>
    Schema.IEntitySchemaRegistry SchemasForLoad => _contextSchemas ?? Engine.Orm.Schemas;

    /// <summary>会话里已跟踪实例的内存视图。只反映本会话见过的实体，不等于库里有什么；
    /// 每次枚举取当时那一份，枚举中途 Remove 不炸，但也看不到自己刚删的那一步
    /// （被标删的实例留在视图里直到 SaveChanges 真把那句 DELETE 发出去）。</summary>
    public LocalView<T> Local => _local ??= new LocalView<T>(_tracker.SnapshotEntities, _tracker.Find);

    /// <summary>给已跟踪实例补载导航属性。对当前会话里每一颗 T 实体，发一条 SQL 把 navigation 指向的那一批
    /// 子实体拿回来，再按 FK↔PK 缝到父实体的导航属性上。引用导航与集合导航都走这一颗口，
    /// 分岔判据是导航属性的 CLR 型是不是 IEnumerable。不静默返回 this 而真发 SQL：
    /// "调了 Load 就以为导航有值"是最难查的假象。</summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    public ITrackedSet<T> Load<TProp>(Expression<Func<T, TProp>> navigation)
        => LoadAsync<TProp>(navigation, default).GetAwaiter().GetResult();

    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    async Task<ITrackedSet<T>> LoadAsync<TProp>(Expression<Func<T, TProp>> navigation, CancellationToken ct)
    {
        // 补载不经七个执行口（它直接走 QueryInBatchesAsync），所以在这里自己进闸——
        // Load 跑到一半时同一会话上别的操作进场照样被拒。
        Engine.Gate?.Enter();

        try
        {
            return await LoadCore<TProp>(navigation, ct).ConfigureAwait(false);
        }
        finally
        {
            Engine.Gate?.Exit();
        }
    }

    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    async Task<ITrackedSet<T>> LoadCore<TProp>(Expression<Func<T, TProp>> navigation, CancellationToken ct)
    {
        var memberName = ExtractMemberName(navigation.Body);
        var parents = _tracker.SnapshotEntities();

        if (parents.Count == 0)
            return this;

        var parentContract = Engine.Model.Root.Contract
            ?? throw new QuickSqlException("Load 需要实体契约：根表位没有绑定实体。");

        var navProperty = parentContract.ClrType.GetProperty(memberName)
            ?? throw new QuickSqlException(
                $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的属性。");

        var childType = GetCollectionElementType(navProperty.PropertyType);

        if (childType is not null)
            await LoadCollectionNavigationAsync(parents, parentContract, memberName, childType, ct).ConfigureAwait(false);
        else
            await LoadReferenceNavigationAsync(parents, parentContract, memberName, navProperty.PropertyType, ct).ConfigureAwait(false);

        return this;
    }

    /// <summary>引用导航补载（如 Load(o => o.Customer)）：父端有 FK 列指向目标 PK。
    /// 收集所有父实体的 FK 值 → 查目标表 WHERE PK IN (...) → 按 PK 索引 → 缝回父实体。</summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    async Task LoadReferenceNavigationAsync(
        IReadOnlyList<T> parents, EntityContract parentContract,
        string memberName, Type targetType, CancellationToken ct)
    {
        var relationship = FindRelationship(parentContract, memberName);
        var targetContract = SchemasForLoad.Contract(targetType);
        var targetRead = SchemasForLoad.ReadDelegate(targetType);
        var targetPkColumns = targetContract.PrimaryKey;

        var parentFkGetters = BuildKeyGettersByColumnName(parentContract.ClrType, parentContract, relationship.ColumnNames);

        var fkValues = new HashSet<object>(
            parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);
        var parentByFk = new Dictionary<object, List<object>>(
            parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var parent in parents)
        {
            object fkKey = parentFkGetters.Length == 1
                ? parentFkGetters[0](parent)
                : BuildCompositeKey(parentFkGetters, parent);

            fkValues.Add(fkKey);

            if (!parentByFk.TryGetValue(fkKey, out var list))
            {
                list = [];
                parentByFk[fkKey] = list;
            }

            list.Add(parent);
        }

        var targetTable = Engine.Orm.NewTableRef(targetType, 0);

        // IN 的值按参数预算分块发射（QueryInBatchesAsync）：父端 FK 集超过预算
        //（SQL Server 2100 / Oracle IN 列表 1000）时是多条子 SELECT，不是整条在驱动侧失败。
        var children = await Engine.QueryInBatchesAsync(
            [.. fkValues], targetPkColumns, targetTable, targetContract,
            Materializer.EntityRows<object>(targetRead, targetContract),
            [],
            new ExecutionTag("load-ref", [targetContract.Table.Name], DataSourceRouting.ClusterOf(targetContract.Table)),
            ct).ConfigureAwait(false);

        var pkGetters = BuildKeyGetters(targetType, targetPkColumns);

        // 缝回父实体那一格：建一次委托、每行只调用，不逐行反射找属性写值。
        var setNavigation = MemberAccessors.Setter(
            parentContract.ClrType, memberName,
            $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的引用导航属性，无法把补载的实体缝回父实体。",
            $"实体 {parentContract.ClrType.Name}.{memberName} 没有公共写口：Load 要求导航属性可写。");

        foreach (var child in children)
        {
            object pkKey = pkGetters.Length == 1
                ? pkGetters[0](child)
                : BuildCompositeKey(pkGetters, child);

            if (parentByFk.TryGetValue(pkKey, out var matchedParents))
            {
                // 缝会话里的那一只：这一行可能早就在跟踪档里（先查过它、或它刚被别处补载过），
                // 按刚物化那只缝会让同一行在会话里有两只实例。
                var stitched = _session.CanonicalOf(child, targetType) ?? child;

                foreach (var parent in matchedParents)
                    setNavigation(parent, stitched);
            }
        }
    }

    /// <summary>集合导航补载（如 Load(c => c.Orders)）：子端有 FK 列指向父端 PK。
    /// 收集所有父实体的 PK 值 → 查子表 WHERE FK IN (...) → 按 FK 分组 → 缝回父实体的集合属性。</summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    async Task LoadCollectionNavigationAsync(
        IReadOnlyList<T> parents, EntityContract parentContract,
        string memberName, Type childType, CancellationToken ct)
    {
        var childContract = SchemasForLoad.Contract(childType);
        var childRead = SchemasForLoad.ReadDelegate(childType);
        var fkColumnNames = DiscoverFkColumns(parentContract, childContract, memberName);

        var parentPkGetters = BuildKeyGetters(parentContract.ClrType, parentContract.PrimaryKey);

        var pkValues = new HashSet<object>(
            parentPkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);
        var parentByPk = new Dictionary<object, object>(
            parentPkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var parent in parents)
        {
            object pkKey = parentPkGetters.Length == 1
                ? parentPkGetters[0](parent)
                : BuildCompositeKey(parentPkGetters, parent);

            pkValues.Add(pkKey);
            parentByPk[pkKey] = parent;
        }

        var childTable = Engine.Orm.NewTableRef(childType, 0);

        var fkColumns = new ColumnContract[fkColumnNames.Count];

        for (var i = 0; i < fkColumns.Length; i++)
            fkColumns[i] = childContract.ByColumn[fkColumnNames[i]];

        // IN 的值按参数预算分块发射（QueryInBatchesAsync）：父端 PK 集超过预算
        //（SQL Server 2100 / Oracle IN 列表 1000）时是多条子 SELECT，不是整条在驱动侧失败。
        var children = await Engine.QueryInBatchesAsync(
            [.. pkValues], fkColumns, childTable, childContract,
            Materializer.EntityRows<object>(childRead, childContract),
            [],
            new ExecutionTag("load-coll", [childContract.Table.Name], DataSourceRouting.ClusterOf(childContract.Table)),
            ct).ConfigureAwait(false);

        var fkGetters = BuildKeyGettersByColumnName(childType, childContract, fkColumnNames);

        var grouped = new Dictionary<object, List<object>>(
            fkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var child in children)
        {
            object fkKey = fkGetters.Length == 1
                ? fkGetters[0](child)
                : BuildCompositeKey(fkGetters, child);

            if (!grouped.TryGetValue(fkKey, out var list))
            {
                list = [];
                grouped[fkKey] = list;
            }

            list.Add(child);
        }

        // 缝回集合导航：建一次四把委托（读、写、追加、造表），每行只调用。
        var nav = MemberAccessors.BindCollection(
            parentContract.ClrType, memberName, childType,
            $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的集合导航属性，无法把补载的子实体缝回父实体。");

        // 枚举的是父而不是分组结果：没有子行的父同样要拿到"我捞过，这里没有人"的声明。
        // 只缝有人的那几只会让空串留下一颗 null，而 null 在级联删除那一档是"还没捞"，
        // Load 一次之后仍然删不下去。
        foreach (var (pkKey, parent) in parentByPk)
        {
            var collection = nav.NewCollection();

            if (grouped.TryGetValue(pkKey, out var childList))
                foreach (var child in childList)
                    // 串里挂的是会话里的那一只，不是刚物化那一只（同一行同一实例）。
                    // 挂了双胞胎的后果要到级联删除才看得见：撞的是"同键两只实例在会话里没有定义"。
                    nav.Add(collection, _session.CanonicalOf(child, childType) ?? child);

            nav.Setter(parent, collection);

            // 缝完就记基线：从这一刻起"这一串里有谁"是一句捞过的声明，
            // 之后从里面摘掉一只，会话才判得出要删的是那一行（见 Node.Baselines）。
            _tracker.RememberCollections((T) parent);
        }
    }

    static string ExtractMemberName(Expression body)
    {
        if (body is MemberExpression member)
            return member.Member.Name;

        throw new QuickSqlException(
            $"Load 的 lambda 必须是简单的属性访问（如 o => o.Customer），当前表达式形态是 {body.NodeType}。");
    }

    static RelationshipContract FindRelationship(EntityContract contract, string memberName)
    {
        foreach (var rel in contract.Relationships)
        {
            if (rel.MemberName == memberName)
                return rel;
        }

        throw new QuickSqlException(
            $"实体 {contract.ClrType.Name} 上没有名为 {memberName} 的导航属性。" +
            $"已注册的关系：{string.Join(", ", contract.Relationships.Select(r => r.MemberName).DefaultIfEmpty("(无)"))}。");
    }

    [RequiresUnreferencedCode("按运行期 Type 反射判集合元素型。")]
    [RequiresDynamicCode("按运行期 Type 反射判集合元素型。")]
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

    /// <summary>补载/缝合那一条路的外键发现。三档与级联落键那三档同名同序（跟踪器侧的 ResolveCascadeForeignKey）：
    /// 同一根边在两条路径上不许有两个答案，否则「级联插得进去、Load 捞不上来」就成了死路。</summary>
    static IReadOnlyList<string> DiscoverFkColumns(
        EntityContract parentContract, EntityContract childContract, string memberName)
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
                    $"子实体 {childContract.ClrType.Name} 上没有指向父实体 {parentContract.ClrType.Name} 的外键列（尝试过约定名 {name}）。");

            compositeNames.Add(name);
        }

        return compositeNames;
    }

    [RequiresUnreferencedCode("按运行期 Type 反射取主键属性 getter。")]
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

    [RequiresUnreferencedCode("按运行期 Type 反射取 FK 属性 getter。")]
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

    static object BuildCompositeKey(Func<object, object>[] getters, object entity)
    {
        var vals = new object[getters.Length];

        for (var j = 0; j < getters.Length; j++)
            vals[j] = getters[j](entity);

        return vals;
    }

    sealed class StructuralComparer : IEqualityComparer<object>
    {
        public static readonly StructuralComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is object[] ax && y is object[] ay)
            {
                if (ax.Length != ay.Length) return false;

                for (var i = 0; i < ax.Length; i++)
                    if (!Equals(ax[i], ay[i])) return false;

                return true;
            }

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

    // ── 写侧 ──────────────────────────────────

    /// <summary>标为新增。与 DbContext.Add 是同一条路，只是 T 已经在这只对象上。</summary>
    public T Add(T entity)
    {
        _tracker.Add(entity);

        return entity;
    }

    public void AddRange(IEnumerable<T> entities)
    {
        foreach (var entity in entities)
            _tracker.Add(entity);
    }

    /// <summary>承认为「库里已有、当前值就是库值」：不写任何列，只登记快照。用于「我手里这只是从别处来的」。</summary>
    public T Attach(T entity)
    {
        _tracker.Attach(entity);

        return entity;
    }

    /// <summary>纳入会话且每列都算脏（Attach + Entry(...).State = Modified 的合成形态）。</summary>
    public T Update(T entity)
    {
        _tracker.Update(entity);

        return entity;
    }

    /// <summary>标为删除。此刻还没有发 DELETE，所以它仍在 Local 里；
    /// 而 SaveChanges 一抛就整个回滚，那一行会原样回到库里。</summary>
    public T Remove(T entity)
    {
        _tracker.Remove(entity);

        return entity;
    }

    public void RemoveRange(IEnumerable<T> entities)
    {
        foreach (var entity in entities)
            _tracker.Remove(entity);
    }

    /// <summary>本会话中这一行的状态与逐列入口。不在会话里的实例在这里抛：
    /// 取一个入口顺手把实体纳入会话，是一种没人说过的写入意图。泛型那一档才有收 lambda 的 Property。</summary>
    public EntityEntry<T> Entry(T entity) => _tracker.Entry(entity);

    // ── Find / Refresh ──────────────────────────────────────

    public T? Find(params object?[] primaryKeyValues)
        => FindAsync(default, primaryKeyValues).GetAwaiter().GetResult();

    public Task<T?> FindAsync(CancellationToken ct = default, params object?[] primaryKeyValues)
    {
        var cached = _tracker.Find(primaryKeyValues);

        if (cached is not null)
            return Task.FromResult<T?>(cached);

        return FindFromDbAsync(primaryKeyValues, ct);
    }

    async Task<T?> FindFromDbAsync(object?[] primaryKeyValues, CancellationToken ct)
    {
        var result = await ByPkQuery(primaryKeyValues).CappedAsync<T>(1, ct).ConfigureAwait(false);

        return result.Count > 0 ? result[0] : null;
    }

    public void Refresh(T entity)
        => RefreshAsync(entity, default).GetAwaiter().GetResult();

    public async Task RefreshAsync(T entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var schema = SchemasForLoad.Schema<T>();
        var keys = Engine.Model.Root.Contract!.PrimaryKey;
        var pkValues = new object?[keys.Length];

        for (var i = 0; i < keys.Length; i++)
            pkValues[i] = schema.GetValue(entity, Array.IndexOf(Engine.Model.Root.Contract.Columns, keys[i]));

        var fresh = await ByPkQuery(pkValues).NoTracking().FirstAsync<T>(ct).ConfigureAwait(false);

        if (fresh is null)
            throw new QuickSqlException("Refresh 找不到这一行：库里没有这个主键的记录。");

        var columns = Engine.Model.Root.Contract.Columns;
        var values = new object?[columns.Length];

        for (var i = 0; i < values.Length; i++)
            values[i] = schema.GetValue(fresh, i);

        _tracker.ApplyFreshValues(entity, values);
    }

    QueryEngine ByPkQuery(object?[] primaryKeyValues)
    {
        var root = Engine.Model.Root;
        var contract = root.Contract
            ?? throw new QuickSqlException("Find/Refresh 需要实体契约：根表位没有绑定实体。");
        var keys = contract.PrimaryKey;

        if (keys.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有主键：Find/Refresh 无法构建 WHERE 条件。");

        if (primaryKeyValues.Length != keys.Length)
            throw new QuickSqlException($"{contract.Table.Name} 的主键有 {keys.Length} 列，这里给了 {primaryKeyValues.Length} 个值。");

        SqlExpression? predicate = null;

        for (var i = 0; i < keys.Length; i++)
        {
            var column = keys[i];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal,
                new ColumnExpression(root, column),
                new ParameterValue(primaryKeyValues[i], column));

            predicate = predicate is null ? equal : new SqlBinaryExpression(BinaryOperator.And, predicate, equal);
        }

        return Engine.Derive(Engine.Model.AddPredicate(predicate!));
    }
}
