using QuickSql.Aop;
using QuickSql.Metadata;
using System.Collections.Concurrent;

namespace QuickSql.Tracking;

/// <summary>会话的跟踪器（db.ChangeTracker），也就是 EntityTracker 按实体类型切开之后的那层调度。
/// 这一层里没有一处 MakeGenericMethod：泛型参数 T 全部由编译期已知的调用点带进来，运行期只按 Type 查一张表
/// ——「加载可以反射，调用不行」的分界线画在这里。非线程安全，由 SessionGate 守门：一个会话同一时刻只归
/// 一个执行流，身份映射与 _byRef 都没有加锁；档登记（_byType/_orderView）按构造并发安全，
/// 逐实体的账由那颗闸单一在飞保证独占。</summary>
sealed class ChangeTracker(QuickSqlOrm orm, Schema.IEntitySchemaRegistry? schemas = null) : DbChangeTracker
{
    /// <summary>会话级模型覆写（DbContext.OnModelCreating）给的注册表，null = 没覆写。
    /// For 造 EntityTracker 时递下去，跟踪侧的契约与读写委托改问这一份。</summary>
    internal Schema.IEntitySchemaRegistry? Schemas => schemas;

    /// <summary>按调用点上的静态类型（Set〈T〉() 的那个 T）分档。键不是 GetType()：派生实例经 Set〈Base〉()
    /// 进会话时写出去的是基类那张表的列，Entries 也得在同一条档里看得见它，两边用同一个键才自洽。
    /// 用 ConcurrentDictionary：档的登记要能与一只已在飞的操作并发（两条分支同时首次 Set），
    /// 与 SessionGate 的分工是——登记这一格按构造并发安全（含 _orderView），逐实体的账仍然一锁不锁、
    /// 由会话闸单一在飞保证独占。</summary>
    readonly ConcurrentDictionary<Type, ITypeTracker> _byType = new();

    /// <summary>类型的首次出现序的建造名单：只在 _registryGate 里追加，追加完重铸 _orderView。
    /// 枚举一律走视图，不摸这份名单。</summary>
    readonly List<ITypeTracker> _order = [];

    /// <summary>_order 的只读投影视图：登记侧在锁里重铸，读侧（侦测、报告、保存序）不加锁枚举这一份
    /// 不可变数组——迭代中途来了新档，旧视图照样自洽，下一轮自然看见。</summary>
    volatile ITypeTracker[] _orderView = [];

    /// <summary>档登记那道锁。只护 _order/_orderView/_hasCollections 这三格建造账，
    /// 不护任何逐实体的账——那些归会话闸。</summary>
    readonly object _registryGate = new();

    /// <summary>级联欠着的外键（CascadeLink）。懒建：绝大多数会话一次级联都没有；这一串的生命周期只到
    /// 子那一档的 Insert 语句建出来为止——用完即摘，不留跨保存的账。保存中途抛了它会有残留，那是安全的：
    /// 下一次保存把同一批值再落一遍，落的是同一颗键。</summary>
    List<CascadeLink>? _links;

    /// <summary>集合边 → 子端列下标的解算结果，按（父型、子型、导航名）缓存一次。
    /// 契约是冻结的，所以这份缓存与类型档表同寿命；Clear 不清它（清的是数据不是判据）。</summary>
    Dictionary<(Type Parent, Type Child, string Member), int[]>? _cascadeKeys;

    /// <summary>这一会话里建过任何一条带集合边的档。摘行那一次遍历的闸门：只跟踪标量实体的会话每轮侦测
    /// 不该为"有没有人从集合里被摘掉"多走一遍档表。它只朝"要扫"的方向偏（建过档就不撤）——
    /// 偏保守多扫一次空表，偏乐观就是漏删。</summary>
    bool _hasCollections;

    /// <summary>三档的执行序（先插、再改、最后删）。档内的类型序另算：见 OrderedByDependency，
    /// 那里才是「先插被依赖的、再删依赖方」。</summary>
    static readonly WriteOperation[] Phases = [WriteOperation.Insert, WriteOperation.Update, WriteOperation.Delete];

    /// <summary>自动侦测开关。关掉的只有「物化之前那一次」和 HasChanges 里的那一次，
    /// 而 SaveChanges 那一步（WritesAreDue）不看这个开关、恒先侦测再写：跳过这一步会让 SaveChanges
    /// 漏掉没被任何显式赋值覆盖的改动，而「漏写」查不出来。</summary>
    internal bool AutoDetectChangesEnabled { get; set; } = true;

    public override int Count
    {
        get
        {
            var count = 0;

            foreach (var tracker in _orderView)
                count += tracker.Count;

            return count;
        }
    }

    /// <summary>它不是只读的：开着自动侦测时它会先比一轮。不这么做的话，「改了值、还没轮到侦测」
    /// 这一段里 HasChanges 会报 false，调用方拿这个 false 去跳过 SaveChanges 就是丢数据
    /// ——偏保守（多一次无用的 UPDATE）可以接受，偏乐观不行。代价随会话里的实例数线性，
    /// 是一句诊断用的话，不是热路径开关。</summary>
    public override bool HasChanges
    {
        get
        {
            if (AutoDetectChangesEnabled)
                DetectChanges();

            return AnyDue();
        }
    }

    public override IEnumerable<EntityEntry> Entries()
    {
        foreach (var tracker in _orderView)
            // 这一处留拷贝是故意的：Entries() 是调用方手里的诊断视图，"在枚举里改会话"（一边看一边 Remove）
            // 是公开口能承诺的形状；内核那六处遍历走 TrackedNodes，不付这一份。
            foreach (var node in tracker.TrackedNodes.ToArray())
                yield return tracker.EntryOf(node);
    }

    /// <summary>返回的是「本次改了状态的实例数」（各类型相加），0 就是这一轮没有任何东西变过。
    /// 今天是全员逐列比（哈希短路"没碰过的行不进列比较"还没接线），代价随会话规模线性。</summary>
    public override int DetectChanges()
    {
        var changed = 0;

        // 摘行先判、逐列比较在后：摘掉的那只此刻多半还是 Unmodified，先比列就会给它发一句 UPDATE
        // 再发一句 DELETE——同一行两个方向相反的语句。逐列比较自己跳过 Added/Deleted。
        if (_hasCollections)
            changed += DiffDetachedChildren();

        foreach (var tracker in _orderView)
            changed += tracker.DetectChanges();

        return changed;
    }

    /// <summary>清的是数据，留的是档：类型注册表留着，会话上那些已经发出去的 DbSet 与查询引擎
    /// 才不会再撞上「档不存在」。之后 Set〈T〉() 拿到的还是同一份空跟踪器。</summary>
    public override void Clear()
    {
        foreach (var tracker in _orderView)
            tracker.Clear();

        // 级联欠的键跟着数据一起清：实体都离开会话了，那些引用指向的是不存在的账。
        _links?.Clear();
    }

    /// <summary>取（或建）某一个实体类型的那一份跟踪器。只有泛型调用点用它：
    /// EntityTracker 的构造要 Schema〈T〉() 与 InsertEngine〈T〉，这些是编译期参数而不是运行期能补的东西。</summary>
    internal EntityTracker<T> For<T>() where T : class
    {
        // 登记在锁里：_byType 读侧虽是并发字典，但 _order 的追加与视图重铸必须与登记一一对应，
        // 两次并发首登记只许留一份档（双建会让同一类型两条档、保存序与报告各算一遍）。
        lock (_registryGate)
        {
            if (_byType.TryGetValue(typeof(T), out var known))
                return (EntityTracker<T>) known;

            var tracker = new EntityTracker<T>(orm, this, schemas);
            _byType[typeof(T)] = tracker;
            _order.Add(tracker);
            _orderView = [.. _order];

            // 摘行那一次遍历的闸门：会话里出现过一条集合边才可能有人被摘掉。
            // 它只朝"要扫"的方向偏（建过档就不撤），因为档本身也不撤——Clear() 留档。
            if (tracker.Contract.Collections.Count > 0)
                _hasCollections = true;

            return tracker;
        }
    }

    /// <summary>级联：顺这一张实体的集合边把子实体带进会话（插入档）或标成删除（删除档）。
    /// 闸门只看契约上有没有集合边，没有导航的实体一次分配都不付。每一次调用新建一份 CascadeWalker：
    /// 递归时内层不能踩外层的游标。</summary>
    internal void Cascade(ITypeTracker parent, object entity, CascadeMode mode)
    {
        if (parent.Contract.Collections.Count == 0)
            return;

        new CascadeWalker(this, mode).Walk(parent, entity);
    }

    internal void AddCascadeLink(CascadeLink link) => (_links ??= []).Add(link);

    /// <summary>摘行的侦测（裁决③：order.Items.Remove(x) 之后 SaveChanges 要把 x 那一行删掉——owned 语义）。
    /// 判据只有一处来源：Node.Baselines 里上一次看见的那一串，比此刻集合位里那一串（比的是引用，会话认实例）。
    /// 某一格基线是 null 就不判——那是"这一串我从来没见过"，与"见过、而它是空的"严格不同：
    /// 看不见就不判（不替没看见的东西编一份缺席的账），判得着才动手。动手的是级联那颗老接缝：
    /// 把被摘掉的那几只送进子端那一档 Remove，"摘掉一只"与"整串被标删"走同一条路。为什么分两趟：
    /// 中途响（改父那一格）不能留下一份改到一半的基线，否则重试保存时响第二次变成"没改动"。
    /// 到位是同一趟里的镜像：那一串里有、基线里没有、会话还没跟过的，送进子端插入档，
    /// 外键由级联链接在父写出去之后补——两半合起来才是「明细换了没换」的完整答案。</summary>
    int DiffDetachedChildren()
    {
        List<Detached>? due = null;

        // 这一趟只判，不动 _order（动手在 due 那一趟），所以直接枚举档表。
        foreach (var tracker in _orderView)
        {
            if (tracker.Contract.Collections.Count == 0)
                continue;

            var edges = tracker.Contract.Collections;

            foreach (var node in tracker.TrackedNodes)
            {
                // 已经标删的父：它那一串归删除档的级联管（捞不到还会响），不在这里再判一次缺席。
                if (node.State == EntityState.Deleted || node.Baselines is not { } baselines)
                    continue;

                for (var slot = 0; slot < edges.Count; slot++)
                {
                    // 基线是 null = 没见过，摘与到都不判；是空表 = 见过且没人，没人可缺席、但谁都算新到。
                    if (!edges[slot].Cascade || baselines[slot] is not { } baseline)
                        continue;

                    var live = CascadeWalker.LiveMembers(edges[slot], node.Entity);

                    // 整串被换成 null = 那句"我看过这一串"收回去了。不猜它是"全删了"还是"我不知道"
                    // （前者是拿 null 当空用）。基线跟着收回去，下一轮重新捞才算。
                    if (live is null)
                    {
                        baselines[slot] = null;
                        continue;
                    }

                    var victims = Missing(baseline, live);
                    var arrivals = Arrived(baseline, live);

                    if (victims is null && arrivals is null)
                    {
                        // 谁也没动：基线跟着往前走。
                        baselines[slot] = live;
                        continue;
                    }

                    (due ??= []).Add(new Detached(tracker, node, slot, edges[slot], live, victims, arrivals));
                }
            }
        }

        if (due is null)
            return 0;

        var removed = 0;

        // 只在真有人被摘掉的那一轮才建：此刻还被人捏着的子实体 → 捏着它的那根边（改父的护栏）。
        var held = HeldByOtherParents();

        foreach (var item in due)
        {
            // 已经在删的那只不必判第二次：它本来就归这次删除，而且多半同时还挂在别人的集合里。
            item.Victims?.RemoveAll(victim => OwnerOf(victim)?.StateOf(victim) == EntityState.Deleted);

            foreach (var victim in item.Victims ?? [])
                if (held.TryGetValue(victim, out var holder))
                    throw Reparented(item, victim, holder);
        }

        foreach (var item in due)
        {
            if (item.Victims is { Count: > 0 })
                new CascadeWalker(this, CascadeMode.Detach).Detach(item.Tracker, item.Node.Entity, item.Edge, item.Victims);

            // 新到的那几只送进插入档：外键由级联链接在父写出去之后补（与 Add 顺导航带人同一条路）。
            if (item.Arrivals is { Count: > 0 })
                new CascadeWalker(this, CascadeMode.Insert).Arrive(item.Tracker, item.Node.Entity, item.Edge, item.Arrivals);

            item.Node.Baselines![item.Slot] = item.Live;
            removed += (item.Victims?.Count ?? 0) + (item.Arrivals?.Count ?? 0);
        }

        return removed;
    }

    static List<object>? Missing(List<object> baseline, List<object> live)
    {
        List<object>? victims = null;
        var kept = new HashSet<object>(live, ReferenceEqualityComparer.Instance);

        foreach (var member in baseline)
            if (!kept.Contains(member!))
                (victims ??= []).Add(member);

        return victims;
    }

    /// <summary>到位的那一半（裁决③的镜像）：此刻那一串里有、基线里没有、且会话还没跟过的那几只。
    /// 与 Missing 同一把参考尺（会话认的是实例）。「还没跟过」是第三道筛：已经在会话里的实例
    /// （别的路径 Add/Attach/物化进来的）不重复送——把它们再 Add 一遍等于替调用方改主意。</summary>
    List<object>? Arrived(List<object> baseline, List<object> live)
    {
        List<object>? arrivals = null;
        var seen = new HashSet<object>(baseline, ReferenceEqualityComparer.Instance);

        foreach (var member in live)
        {
            if (member is null || seen.Contains(member) || OwnerOf(member) is not null)
                continue;

            (arrivals ??= []).Add(member);
        }

        return arrivals;
    }

    /// <summary>此刻还被人捏在集合位里的那些子实体 → 捏着它的那根边。判的是引用，不是键：
    /// 改父的现场是"同一只实例从甲那一串挪到乙那一串"，而那颗临时键在甲乙手里可以是同一个值。</summary>
    Dictionary<object, (Type Parent, string Member)> HeldByOtherParents()
    {
        var held = new Dictionary<object, (Type, string)>(ReferenceEqualityComparer.Instance);

        foreach (var tracker in _orderView)
        {
            if (tracker.Contract.Collections.Count == 0)
                continue;

            var edges = tracker.Contract.Collections;

            foreach (var node in tracker.TrackedNodes)
            {
                if (node.State == EntityState.Deleted)
                    continue;

                for (var slot = 0; slot < edges.Count; slot++)
                {
                    if (!edges[slot].Cascade)
                        continue;

                    if (CascadeWalker.LiveMembers(edges[slot], node.Entity) is not { } live)
                        continue;

                    foreach (var member in live)
                        held[member!] = (tracker.EntityType, edges[slot].MemberName);
                }
            }
        }

        return held;
    }

    QuickSqlException Reparented(Detached item, object victim, (Type Parent, string Member) holder)
        => new($"{item.Tracker.EntityType.Name} 的集合导航 {item.Edge.MemberName} 里摘掉了一只 {victim.GetType().Name}，" +
               $"而它此刻还挂在 {holder.Parent.Name} 的 {holder.Member} 里：这是改父，不是删行，" +
               "而改父要写的是一句 SET 外键 —— 那一档没接线（§14）。会话不替它猜：要么自己改那只实例的外键列再保存，" +
               "要么先 SaveChanges 把这一串删干净，再往另一只父的那一串里插新的一只。");

    /// <summary>摘行/到位侦测中途的一份现场：哪一档、哪只父、哪一根边（连同它在契约里的槽位）、此刻那一串、
    /// 被摘掉的那几只、新到的那几只。存在的唯一理由是把"判"与"动手"分成两趟。
    /// 任一半都可以是 null（只摘不加、只加不摘都是常态）。</summary>
    sealed class Detached(ITypeTracker tracker, Node node, int slot, CollectionEdgeContract edge, List<object> live, List<object>? victims, List<object>? arrivals)
    {
        public ITypeTracker Tracker { get; } = tracker;
        public Node Node { get; } = node;
        public int Slot { get; } = slot;
        public CollectionEdgeContract Edge { get; } = edge;
        public List<object> Live { get; } = live;
        public List<object>? Victims { get; } = victims;
        public List<object>? Arrivals { get; } = arrivals;
    }

    /// <summary>父实体写出去之后、子那一档建语句之前，把父此刻的键落进子行的外键列。
    /// 落不动就拒：键还是空的或还是临时值（父本轮没写出去、或已经离开会话）时，
    /// 写出去就是一个库里不存在的键——级联留下的孤儿行比一句报错难查太多。</summary>
    void ApplyCascadeLinks(ITypeTracker tracker)
    {
        if (_links is null || _links.Count == 0)
            return;

        for (var i = _links.Count - 1; i >= 0; i--)
        {
            var link = _links[i];

            if (!ReferenceEquals(link.Child, tracker))
                continue;

            // 那只子行已经不欠这一句 INSERT 了：它被摘出会话或被显式 Detach 了。
            // 账留着不会写错东西（发出去的是子那一档的语句，而它已经不在档里），
            // 但父的键落不进时会先响一句"键还没值"，那句响的是一个已经不存在的行——假案。
            if (link.Child.StateOf(link.ChildEntity) != EntityState.Added)
            {
                _links.RemoveAt(i);
                continue;
            }

            var values = link.Parent.KeyValuesOf(link.ParentEntity);

            for (var column = 0; column < link.Columns.Length; column++)
            {
                if (!IsUnsetKey(values[column]))
                    continue;

                throw new QuickSqlException(
                    $"级联插不进 {tracker.EntityType.Name} 的外键：父实体 {link.Parent.EntityType.Name} 的键此刻还没值" +
                    "（它本轮没有写出去，或者已经离开会话）。父行都还没进库，子行的外键就没有能指的东西 —— " +
                    "把父子放进同一次 SaveChanges，父那一句 INSERT 会把键回填回来。");
            }

            tracker.SetForeignKeys(link.ChildEntity, link.Columns, values);
            _links.RemoveAt(i);
        }
    }

    /// <summary>「这颗键还不能拿去向库里指认一行」的判据。只认这几颗：null、负整数（临时主键）、Guid.Empty。
    /// 别的都是调用方自己填的值，那一格由库里那条外键约束判，不由会话替他猜。</summary>
    static bool IsUnsetKey(object? value) => value switch
    {
        null => true,
        Guid empty => empty == Guid.Empty,
        int small => small < 0,
        long wide => wide < 0,
        _ => false,
    };

    /// <summary>集合边的那一串外键成员名在子端那张表上，所以要两份契约才算得出来（不进契约工厂收尾的原因）。
    /// 三档按优先级试，推不出就响（裁决④）：静默不级联等于把明细留在库里挂着一根空外键，
    /// 那种行只有对账时才现形。</summary>
    internal int[] CascadeForeignKeyOrdinals(EntityContract parent, EntityContract child, CollectionEdgeContract edge)
    {
        var key = (parent.ClrType, child.ClrType, edge.MemberName);

        if (_cascadeKeys is not null && _cascadeKeys.TryGetValue(key, out var known))
            return known;

        var ordinals = ResolveCascadeForeignKey(parent, child, edge);

        (_cascadeKeys ??= new Dictionary<(Type, Type, string), int[]>())[key] = ordinals;

        return ordinals;
    }

    static int[] ResolveCascadeForeignKey(EntityContract parent, EntityContract child, CollectionEdgeContract edge)
    {
        var keys = parent.PrimaryKey;

        if (keys.Length == 0)
            throw new QuickSqlException(
                $"{parent.ClrType.Name} 没有主键，级联插不进 {child.ClrType.Name} 的外键：父行没有可以被指认的东西。" +
                "要么给它一条主键，要么在这根导航上打 [NoCascade]、自己 Add 明细。");

        // 第一档：子端那根回到父的引用导航。列名已由 EntityContractFactory 换算过（外键列就在子端这张表上）。
        foreach (var relationship in child.Relationships)
        {
            if (relationship.TargetType == parent.ClrType)
                return Ordinals(child, relationship.MemberNames, keys, edge,
                    $"第一档：{child.ClrType.Name}.{relationship.MemberName} 那条引用导航");
        }

        // 第二档：集合导航上 [ForeignKey] 点名的成员。点了名而子端没有那一列 = 配置错误，直接说。
        if (edge.DeclaredForeignKeyMembers.Count > 0)
            return Ordinals(child, edge.DeclaredForeignKeyMembers, keys, edge, "第二档：集合导航上点名的外键成员");

        // 第三档：约定——与查询侧反推外键那三档同名同序（DbSet 的 DiscoverFkColumns）。两处必须一把尺子，
        // 否则 Include 缝得上、级联落不进键，同一根边在两条路径上得到两个答案。
        var name = parent.ClrType.Name;

        if (keys.Length == 1)
        {
            foreach (var candidate in new[] { $"{name}Id", $"{name}ID", $"{name}{keys[0].ColumnName}" })
            {
                if (child.ByColumn.ContainsKey(candidate))
                    return [Array.IndexOf(child.Columns, child.ByColumn[candidate])];
            }
        }
        else
        {
            var composite = new int[keys.Length];
            var resolved = true;

            for (var i = 0; i < keys.Length; i++)
            {
                if (!child.ByColumn.TryGetValue($"{name}{keys[i].ColumnName}", out var column))
                {
                    resolved = false;

                    break;
                }

                composite[i] = Array.IndexOf(child.Columns, column);
            }

            if (resolved)
                return composite;
        }

        throw new QuickSqlException(
            $"推不出 {child.ClrType.Name} 上指向 {name} 的外键列：{name}.{edge.MemberName} 是集合导航，而三档都命不中 —— " +
            $"第一档（{child.ClrType.Name} 上没有回到 {name} 的引用导航）、" +
            "第二档（那根导航没有 [ForeignKey]）、" +
            $"第三档（约定名 {name}Id / {name}ID / {name}{keys[0].ColumnName} 在 {child.ClrType.Name} 的列里都没有）。" +
            $"要么在 {name}.{edge.MemberName} 上写 [ForeignKey(\"明细那一列\")]，要么把父那一侧的引用导航补上，" +
            "要么打 [NoCascade] 自己 Add 明细（裁决④：推不出就响，不静默不级联）。");
    }

    /// <summary>成员名 → 子端的列下标，顺带钉一条基数：外键列数必须等于父主键列数，
    /// 否则一句 INSERT 的参数对不上键（复合主键配单列外键是能建的表，级联不替它猜哪个键进哪一列）。
    /// 认不出名字同样在这里响：契约解析已判过点名的成员必须是已映射列，走到这里还认不出是内核的账没对上。</summary>
    static int[] Ordinals(EntityContract child, IReadOnlyList<string> members, ColumnContract[] keys,
        CollectionEdgeContract edge, string tier)
    {
        if (members.Count != keys.Length)
            throw new QuickSqlException(
                $"级联的外键形状对不上：{tier} 给了 {members.Count} 列，而父实体有 {keys.Length} 列主键（导航 {edge.MemberName}）。" +
                "把两边做成同样的基数 —— 复合主键要逐列点名。");

        var ordinals = new int[members.Count];

        for (var i = 0; i < members.Count; i++)
        {
            if (!child.ByMember.TryGetValue(members[i], out var column))
                throw new QuickSqlException(
                    $"{child.ClrType.Name} 上没有名为 {members[i]} 的列（{tier}，导航 {edge.MemberName}）：" +
                    "外键成员点了名而子端没有那一列，是配置错误，级联不替它猜。");

            ordinals[i] = Array.IndexOf(child.Columns, column);
        }

        return ordinals;
    }

    /// <summary>物化路径的身份映射入口（同一上下文内重复读取返回同一实例）。
    /// entityType 是静态类型：GetType() 在这里会把「按基类纳入会话的派生实例」错分到另一档去，
    /// 而那种实例根本不可能被 For〈Derived〉() 建过档。</summary>
    internal object TrackEntity(object entity, Type entityType)
        => Tracker(entityType).Track(entity);

    /// <summary>整批登记。就地写回列表元素：去重之后「会话里的那一只」可能与物化出来的那一只不是同一只，
    /// 调用方拿到的必须是会话认的那一只——否则下一句 db.Entry(拿到的那个) 会说它不在会话里。</summary>
    internal void TrackAll<T>(List<T> rows, Type entityType)
    {
        var tracker = Tracker(entityType);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row is null)
                continue;

            rows[i] = (T) tracker.Track(row).Entity;
        }
    }

    /// <summary>补载路径的身份映射问句：这一行在会话里已经是哪一只。它与 TrackAll 的分别是职权，
    /// 不只是一个方法的差别：TrackAll 顺带登记，而登记一个从没建过档的子型会撞"不变量被破"
    /// ——那格由接链处补上了（IncludeMany 把"按编译期子型建档"的委托交给节点）。补载这一步只该问
    /// "会话里有没有这一行"，有就把那一只递回去——没有档就是没在跟，原样递回去才是这句话的真话。
    /// 参数同 TrackEntity 是静态类型：按 GetType() 问会把派生实例问到另一颗从没建过的键上，
    /// 把同键两只实例留在会话里。</summary>
    internal object? CanonicalOf(object entity, Type entityType)
        => _byType.TryGetValue(entityType, out var tracker) ? tracker.Canonical(entity) : null;

    /// <summary>会话里有没有任何一档欠着临时主键。这是那条改写路径的闸门：一轮保存通常一颗临时键都没有，
    /// 判"有没有"只扫几个类型档，不必把每行的每个外键列都读一遍。问的是全会话而不是本档：
    /// 发号在主端那一档，拿着那颗值的是依赖方那一档，两边不是同一份账。</summary>
    internal bool HasPendingTemps
    {
        get
        {
            foreach (var tracker in _orderView)
                if (tracker.HasTempKeys)
                    return true;

            return false;
        }
    }

    /// <summary>按边的目标类型把临时键的问题送到主端那一档。目标档不存在就是"不是临时键"：
    /// 号只在 Add 时发，而 Add 必然先把档建好，所以没有档就没有号，不需要为它造一个空档。</summary>
    internal TempProbe ProbeTemp(Type targetType, object? value, out object? real)
    {
        if (!_byType.TryGetValue(targetType, out var tracker))
        {
            real = null;

            return TempProbe.NotTemp;
        }

        return tracker.ProbeTemp(value, out real);
    }

    /// <summary>一次 SaveChanges 的问句：先侦测一遍，再问有没有一行欠着语句。与 HasChanges 的分别是
    /// 它不看开关——SaveChanges 恒定先比一轮，关掉自动侦测不该把没被显式赋值覆盖的改动漏掉。
    /// 判据本身仍是逐行的 WillWrite，粘性位只用来跳过整档扫描。</summary>
    internal bool WritesAreDue()
    {
        DetectChanges();

        return AnyDue();
    }

    bool AnyDue()
    {
        foreach (var tracker in _orderView)
        {
            if (!tracker.MayHaveWrites)
                continue;

            foreach (var node in tracker.TrackedNodes)
                if (tracker.WillWrite(node))
                    return true;
        }

        return false;
    }

    /// <summary>一次 SaveChanges 的写入：三档 → 结算。前置条件是调用方刚跑过 WritesAreDue
    /// （那里就是"侦测 → 再决定要不要开事务"这一步）：这里不再比一遍，否则每次保存都要把全会话的字段读两遍
    /// ——本类比 Entries 那几处快照的余地都没有，非线程安全写在类注释上。</summary>
    internal async Task<SaveChangesReport> FlushDueAsync(CancellationToken ct)
    {
        var traces = new List<HookTrace>();
        var operations = new List<SaveChangesOp>();

        // 快照一份档列表：写的时候要是最开始那几档，中途不接纳新登记的类型。
        // 排序只动档内的类型先后，三档的先后（先插再改最后删）只在下面那一格提前。
        var trackers = OrderedByDependency(_orderView);

        // 「同一张表这一轮既插又删」把那一段删除提前（触发条件见 ITypeTracker.HasInsertsAndDeletes）。
        // 为什么三档的先后要让一步：删掉的那一行腾开的是一个业务唯一键（明细的行号、顺序位、编码），
        // 而插进来的那一行要占的正是同一个位置——先插就撞在还赖在表里的那一行上，
        // 会话按三档排出来的一串语句，库里逐条看都对，串起来是错的。
        // 提前的是整档删除，不是某一张表的删除：只把甲表的删除摘出来提前会拆散
        // 「被依赖的那一行最后删」那条不变量（甲是乙的主端时，甲先没了 → 一句删不掉的甲）。
        // 逆序枚举、同一个 WritePhaseAsync、同一批节点，语句形状与先后都没改，改的只有落在插入档的哪一侧；
        // 插入档内部（被依赖的在前）照旧，主端已回填的键不会被这趟删除拿走。
        // 没被触发的那一轮（只插不删、或只删不插）走原来的三档序，一字未动。
        var deletesFlushed = false;

        async Task FlushDeletesAsync()
        {
            if (deletesFlushed)
                return;

            deletesFlushed = true;

            for (var i = trackers.Length - 1; i >= 0; i--)
            {
                var tracker = trackers[i];

                if (!tracker.MayHaveWrites)
                    continue;

                operations.AddRange(await tracker.WritePhaseAsync(WriteOperation.Delete, traces, ct).ConfigureAwait(false));
            }
        }

        foreach (var phase in Phases)
            for (var i = 0; i < trackers.Length; i++)
            {
                // 删除走逆序：被依赖的那一行最后删，否则库里那条外键约束先把语句挡下来
                // ——而"外键约束报错"是这一格里最难读的一种失败形状。
                var tracker = trackers[phase == WriteOperation.Delete ? trackers.Length - 1 - i : i];

                if (!tracker.MayHaveWrites)
                    continue;

                if (phase == WriteOperation.Delete)
                {
                    await FlushDeletesAsync();

                    continue;
                }

                // 级联欠的键要在这一档建语句之前落进子实体：WriteInsertsAsync 读的是实体现值，
                // 拓扑序已把父那一档排在前面 await 完，此刻父的键要么已经有值、要么本轮没写（那一格拒）。
                if (phase == WriteOperation.Insert)
                    ApplyCascadeLinks(tracker);

                if (phase == WriteOperation.Insert && !deletesFlushed && tracker.HasInsertsAndDeletes)
                    await FlushDeletesAsync();

                operations.AddRange(await tracker.WritePhaseAsync(phase, traces, ct).ConfigureAwait(false));
            }

        foreach (var tracker in trackers)
            tracker.PostFlush();

        return new SaveChangesReport(operations, traces);
    }

    /// <summary>这一批的关系边排不排得出先后——只判、一条语句都不发。SaveChangesAsync 把它放在开事务之前：
    /// 排不出顺序是元数据的事，而 BeginTransaction 会先一步去碰驱动，"连不上库"那句会盖住成因。</summary>
    internal void EnsureWriteOrderResolvable() => OrderedByDependency(_orderView);

    /// <summary>分表实体与跟踪器互斥。判据取"这一行此刻会不会被写出去"（与 AnyDue 同一颗 WillWrite）：
    /// 只把分表实体读进会话是无害的，那条路不发语句。为什么不是"实现一半"：会话的账按主键记，
    /// 而分表里"同键的两行"是能存在的两行（Id 自增而表按月切时，两张片各自的 1 号互不相干），
    /// 身份映射不成立——那要先定"跨片的一行由什么指认"。出路：写侧三条链按行的分片键值定表，本来就不需要会话。</summary>
    internal void EnsureNoSplitWrites()
    {
        foreach (var tracker in _orderView)
        {
            if (!tracker.MayHaveWrites || tracker.Contract.Table.Split is not { } split)
                continue;

            foreach (var node in tracker.TrackedNodes)
                if (tracker.WillWrite(node))
                    throw new QuickSqlException(
                        $"{tracker.EntityType.Name} 声明了分表（{split.Kind} 档，分片键列 {split.ColumnName}），跟踪器写不出去：" +
                        "会话的账按主键记，而同一个主键在分表里可以占着两张片上的各一行，身份映射认不出“要写的是哪一行”。" +
                        "分表的写请用 orm.Insert/InsertRange/Update/Delete 那几条链（它们按行的分片键值算目标表）。");
        }
    }

    /// <summary>这一批写要落的那座集群（分库那一档），跨集群就整批拒。判据与 EnsureNoSplitWrites 同一颗
    /// WillWrite：只把另一座集群的实体读进会话不构成一批跨集群的写。为什么是"整批拒"而不是"按集群拆成
    /// 几场事务依次提交"：后者改了 SaveChanges「一批=一场事务」的承诺——第二批失败时第一批已经进了库，
    /// 而报告里只有一份 Success=false，那种"看着像原子，其实不是"的形状正是要避开的。要分库写就自己分次
    /// SaveChanges，或按集群各开一条会话。返回 null 说"这一批没有要写出去的行"，调用方按缺省集群开事务。</summary>
    internal string? WriteCluster()
    {
        string? cluster = null;

        foreach (var tracker in _orderView)
        {
            if (!tracker.MayHaveWrites)
                continue;

            var named = DataSourceRouting.ClusterOf(tracker.Contract.Table);

            foreach (var node in tracker.TrackedNodes)
            {
                if (!tracker.WillWrite(node))
                    continue;

                if (cluster is null)
                {
                    cluster = named;
                    continue;
                }

                if (!string.Equals(cluster, named, StringComparison.Ordinal))
                    throw new QuickSqlException(
                        $"这一批同时有集群 {cluster}（{tracker.EntityType.Name} 在集群 {named} 上）与集群 {named} 的写：一场事务只有一条连接，跨集群的保存拒。" +
                        "请按集群分两次 SaveChanges，或为每一座集群各开一条会话。");
            }
        }

        return cluster;
    }

    /// <summary>档内的类型序按外键依赖排：被依赖的（主端）在前。判据只有一个来源——契约上那条关系边
    /// （三处只读契约，不许各自去读注解）。排得出先后，接下来那一半才排得出引用：主端键要等库给的那一种
    /// 由临时主键接住——先按这里的顺序把主端写出去、真实键回填进实体，再在依赖方建语句之前换掉临时值。
    /// 没有任何关系边时原样返回（首次出现序），拓扑只在会话真出现跨实体关系时才付代价。</summary>
    static ITypeTracker[] OrderedByDependency(ITypeTracker[] trackers)
    {
        // 快路径：会话里没有任何关系边就不排（这一步在每次保存上都要走）。
        // 集合边也算一条：级联的先后正由它定。
        if (Array.TrueForAll(trackers, static t =>
                t.Contract.Relationships.Count == 0 && t.Contract.Collections.Count == 0))
            return trackers;

        var byType = new Dictionary<Type, int>(trackers.Length);

        for (var i = 0; i < trackers.Length; i++)
            byType[trackers[i].EntityType] = i;

        var edges = new List<Type>[trackers.Length];

        for (var i = 0; i < trackers.Length; i++)
            edges[i] = EdgesOf(trackers[i], trackers, byType);

        var ordered = new ITypeTracker[trackers.Length];
        var settled = new bool[trackers.Length];

        for (var emitted = 0; emitted < trackers.Length; emitted++)
        {
            // 同层之间保留首次出现序：稳定，所以"没有关系"的那些档不会因为字典枚举序而换位置。
            var next = -1;

            for (var i = 0; i < trackers.Length && next < 0; i++)
            {
                if (settled[i])
                    continue;

                if (Ready(i))
                    next = i;
            }

            if (next < 0)
                throw new QuickSqlException(CycleMessage(trackers, settled));

            ordered[emitted] = trackers[next];
            settled[next] = true;
        }

        return ordered;

        bool Ready(int index)
        {
            foreach (var dependency in edges[index])
            {
                if (!settled[byType[dependency]])
                    return false;
            }

            return true;
        }
    }

    /// <summary>这一档依赖哪些会话里在场的类型。自引用不算边：同一张表内部的先后不是类型序能解决的
    /// （那一行得先插自己再补自己的上级，那是另一件事），留着只会把自己判成环。两头的边都接：
    /// 本档自己的 to-one 边（外键列在我这张表上 → 我依赖目标），以及别人手里那根集合边指着我
    /// （我是别人的明细——OrderItem 往往没有回指 Order 的导航，那条依赖只存在于父端的集合边上）。
    /// 打了 [NoCascade] 的那一根不接：父不带子，父子两批的先后不该由会话替用户定，接上去只会多出原本不存在的环。</summary>
    static List<Type> EdgesOf(ITypeTracker tracker, ITypeTracker[] trackers, Dictionary<Type, int> inSession)
    {
        var edges = new List<Type>();

        foreach (var relationship in tracker.Contract.Relationships)
        {
            if (relationship.TargetType == tracker.EntityType)
                continue;

            if (inSession.ContainsKey(relationship.TargetType) && !edges.Contains(relationship.TargetType))
                edges.Add(relationship.TargetType);
        }

        foreach (var other in trackers)
        {
            if (ReferenceEquals(other, tracker))
                continue;

            foreach (var collection in other.Contract.Collections)
            {
                if (!collection.Cascade || collection.ChildType != tracker.EntityType)
                    continue;

                if (!edges.Contains(other.EntityType))
                    edges.Add(other.EntityType);
            }
        }

        return edges;
    }

    static string CycleMessage(ITypeTracker[] trackers, bool[] settled)
    {
        var stuck = new List<string>();

        for (var i = 0; i < trackers.Length; i++)
        {
            if (!settled[i])
                stuck.Add(trackers[i].EntityType.Name);
        }

        return $"这些实体的外键依赖成环，排不出写入先后：{string.Join(" / ", stuck)}。" +
               "临时主键（Tp，§8.2）能接住「主端的键要等库给」那一种，前提是排得出谁先谁后；环上任何一条边都没有先后可排，" +
               "所以这一版不替它「先插一行空外键、再补一句 UPDATE」—— 那是延迟写入那一档（§14）。" +
               "要么把环拆开（其中一端改成可空外键、由第二次保存补上），要么让环上至少一条边的主端本轮不欠语句。";
    }

    /// <summary>这只实例实际在哪一档（按引用问，不按调用点推断的泛型参数）。只服务于一条更准的拒绝话：
    /// DbContext.Entry〈T〉(entity) 的 T 是从实参推出来的，传一只派生实例就推出一个从没建过档的类型，
    /// "先 Set〈派生〉().Attach"会把人指向一条根本不该存在的档——而实例其实好好地待在基类那一档里。</summary>
    internal ITypeTracker? OwnerOf(object entity)
    {
        foreach (var tracker in _orderView)
            if (tracker.Owns(entity))
                return tracker;

        return null;
    }

    ITypeTracker Tracker(Type entityType)
        => _byType.TryGetValue(entityType, out var tracker)
            ? tracker
            // 不可达：引擎手里那份 ChangeTracker 只可能来自 Set〈T〉()，而它先把档建好。
            // 走到这里说明不变量破了，是内核缺陷不是用户写错——消息按这个口径写。
            : throw new QuickSqlException($"{entityType.Name} 在会话里没有跟踪档：物化路径被挂上了一个没有对应档位的跟踪器，" +
                "这是内核的不变量被破（Set<T>() 应当先建档再交给引擎），不是调用方写错。请带这个查询的 Explain() 报缺陷。");
}
