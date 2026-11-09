using System.Collections;
using QuickSql.Metadata;

namespace QuickSql.Tracking;

/// <summary>级联的三个方向。只有这三档：插入顺导航图把人带进会话，删除顺同一张图把子行标删，
/// 摘行只处理"上一次看见、这一次不在了"的那几只（从集合里摘掉就是要把那一行删掉）。
/// 「父实体本轮还是新增、这一句 Remove 只是把它忘掉」那一档故意不在这里，理由写在 EntityTracker 的 Remove。</summary>
enum CascadeMode
{
    Insert,
    Delete,

    /// <summary>摘行档。它不调 Walk（那是"顺全部集合边走一遍"），只处理一根边上几只已经不在那一串里的实例
    /// ——现读的那一串里根本没有它们。静态型仍由同一把 CascadeEnter 委托带进来，名单由摘行者换掉。</summary>
    Detach,
}

/// <summary>一条欠着的外键：父实体手里那一串键值要在「父写出去之后、子的语句建出来之前」落进子实体的那几列。
/// 不复用临时主键那条改写路径：它换的是本实体 to-one 边上的列，而级联的子端常常只有集合边
/// （OrderItem 手里有 OrderId 列、却没有 Order 导航），那条路径根本不知道这些列存在，负数会原样进库。
/// 这里换的是父此刻的键：要么已经有值，要么刚被 INSERT 回填回来。</summary>
readonly record struct CascadeLink(
    ITypeTracker Parent,
    object ParentEntity,
    ITypeTracker Child,
    object ChildEntity,
    int[] Columns);

/// <summary>级联的那一次遍历：Add/Remove 顺导航图带人。每一次 Add/Remove 新建一份，而不是挂在会话上：
/// 递归的形状是 Enter → 子端 Add → 子端自己的级联 → ……，共用一份游标会让内层把外层的边覆盖掉，
/// 「孙实体挂到错的父上」只有崩了才看得见；新建一份的代价是一次对象分配，只在真带集合边的类型上发生。
/// 静态型由契约上那把 CascadeEnter 委托调用，这一层没有 MakeGenericMethod，公开的 Add/Remove 上不必挂裁剪标注。</summary>
sealed class CascadeWalker(ChangeTracker session, CascadeMode mode) : ICascadeSink
{
    ITypeTracker _parent = null!;
    CollectionEdgeContract _edge = null!;

    /// <summary>摘行档要送进子端那一档的那几只已经不在集合里的实例。只在 Detach 档有人读它，
    /// 其余两档恒为 null，一次分配都不付。</summary>
    IReadOnlyList<object>? _victims;

    /// <summary>走完这一张实体手里的集合边。调用点是 EntityTracker.Add/Remove 里「刚新登记/刚新标删」那两条分支：
    /// 已经跟踪过的实例再 Add 一次是幂等的，递归的终止靠的正是「新登记才往下走」——图里有环时也是它收敛。</summary>
    public void Walk(ITypeTracker parent, object entity)
    {
        _parent = parent;

        foreach (var edge in parent.Contract.Collections)
        {
            if (!edge.Cascade)
                continue;

            var children = edge.NavigationReader(entity);

            if (children is null)
            {
                // 删除档：集合位是空的（没 Load、没 Include）就是"捞不到"，拒——
                // 静默留下一串库里还挂着的孤儿行，比多一句报错难查得多。
                // 插入档相反：手里没有子实体就是没有要带的人，无话可说。
                if (mode == CascadeMode.Delete)
                    throw Unreachable(parent, edge);

                continue;
            }

            // 空集合在删除档也不响：那是"这一串我看过，确实没有"的一句声明。
            // 声明错了（库里有）由库里那条外键约束响，不替它在这里编一份账。
            if (!Any(children, edge))
                continue;

            _edge = edge;
            edge.CascadeEnter(this, entity);
        }
    }

    /// <summary>摘行档的入口：把 victims 那几只送进子端那一档标删（连带它们自己手里的子串）。
    /// 委托带上来的那一份成员要被换掉：委托读的是此刻那一串，而这一档要的是"刚才在、现在没有了"的那几只，
    /// 现读读不到它们。仍走同一把委托而不是再开一把：会话手里只有 Type，多一把委托换不到类型安全。</summary>
    public void Detach(ITypeTracker parent, object entity, CollectionEdgeContract edge, IReadOnlyList<object> victims)
    {
        _parent = parent;
        _edge = edge;
        _victims = victims;

        edge.CascadeEnter(this, entity);
    }

    /// <summary>到位档的入口：把 arrivals 那几只送进子端插入档——基线里没有、此刻集合里新到、
    /// 且会话还没跟过的那几只。与摘行档同一把委托、同一次换成员，差别只在送进的档：
    /// 摘掉一只删一行，新到一只插一行（外键由级联链接在父写出去之后补）。</summary>
    public void Arrive(ITypeTracker parent, object entity, CollectionEdgeContract edge, IReadOnlyList<object> arrivals)
    {
        _parent = parent;
        _edge = edge;
        _victims = arrivals;

        edge.CascadeEnter(this, entity);
    }

    public void Enter<TParent, TChild>(TParent parent, IEnumerable<TChild>? children, string memberName)
        where TParent : class
        where TChild : class
    {
        var tracker = session.For<TChild>();

        // 边的列解算放在这里而不是 Walk：到这一格才既有父契约又有子契约，
        // 而它只在"这一串确实有人、且要落键"时才付钱（推不出就响，见 ChangeTracker）。
        var ordinals = mode == CascadeMode.Insert
            ? session.CascadeForeignKeyOrdinals(_parent.Contract, tracker.Contract, _edge)
            : null;

        foreach (var child in Pending(children) ?? [])
        {
            if (child is null)
                continue;

            var node = mode == CascadeMode.Insert ? tracker.Add(child) : tracker.Remove(child);

            // 只有"这一行欠一句 INSERT"才需要父的键：已经在库里的子行，外键本来就对着那一条。
            if (ordinals is not null && node.State == EntityState.Added)
                session.AddCascadeLink(new CascadeLink(_parent, parent, tracker, child, ordinals));
        }
    }

    /// <summary>送进去的那一份成员：摘行档送基线里那几只，到位档送新到的那几只（两者都换了名单，
    /// 因为要的都不是"此刻那一串"），其余送委托现读的那一串。</summary>
    IEnumerable<TChild>? Pending<TChild>(IEnumerable<TChild>? children) where TChild : class
        => _victims is not null ? [.. _victims.Select(static victim => (TChild) victim)] : children;

    /// <summary>那一格集合位此刻装着的成员（按引用原样收，null 元素跳过）。返回 null 只有一层意思：
    /// 那一格是空的（没 Load、没 Include）——调用方据此把"我从没见过这一串"与"我见过、而它是空的"分开：
    /// 前者判不出谁被摘掉（不替没看见的东西编一份缺席的账），后者才判得出。</summary>
    internal static List<object>? LiveMembers(CollectionEdgeContract edge, object entity)
    {
        var navigation = edge.NavigationReader(entity);

        if (navigation is null)
            return null;

        var members = new List<object>();

        foreach (var member in RequireEnumerable(navigation, edge))
            if (member is not null)
                members.Add(member);

        return members;
    }

    /// <summary>"这一串里到底有没有人"只问一句：拿第一个 enumerator 挪一格就退。</summary>
    static bool Any(object children, CollectionEdgeContract edge)
    {
        var tick = RequireEnumerable(children, edge).GetEnumerator();

        try
        {
            return tick.MoveNext();
        }
        finally
        {
            (tick as IDisposable)?.Dispose();
        }
    }

    /// <summary>退给非泛型的  而不是泛型那一支：读口交回来的就是装箱的集合位，
    /// 静态型到下一格才由  那把委托带回来。</summary>
    static IEnumerable RequireEnumerable(object navigation, CollectionEdgeContract edge)
        => navigation as IEnumerable ?? throw new QuickSqlException(
            $"集合导航 {edge.MemberName} 读出来的不是一串集合（是 {navigation.GetType().Name}）：" +
            "级联按可枚举处理，这一格认不出该带谁进会话、也认不出谁被摘掉了。");

    QuickSqlException Unreachable(ITypeTracker parent, CollectionEdgeContract edge)
        => new($"{parent.EntityType.Name} 声明了到 {edge.ChildType.Name} 的级联删除，而导航 {edge.MemberName} 是空的：" +
               "会话看不见子行，就不能替它判「没有要删的」还是「还没捞出来」。" +
               $"先把它捞进会话再删：Include 那根导航，或 db.Set<{edge.ChildType.Name}>().Load(父键)（裁决①：可达才删，捞不到就拒）。");
}
