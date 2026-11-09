using System.Linq.Expressions;

namespace QuickSql.Tracking;

/// <summary>一个被跟踪实例的入口（Entry(entity).State 与 Property(...).IsModified 那两格）。
/// 本身不持有实体的一份拷贝：它指向跟踪器里那个节点，所以 State 与 PropertyEntry.IsModified
/// 写下去改的就是跟踪器的状态，不需要再"提交"回会话。</summary>
public abstract class EntityEntry
{
    /// <summary>被跟踪的那个实例本体（跟踪器发的就是它，不是副本——身份映射要的就是引用相等）。
    /// 实现侧交的是 EntityInstance：这一条只是它的转交口。</summary>
    public object Entity => EntityInstance;

    /// <summary>实现类要交的那一条。抽象成员不直接叫 Entity，是因为派生类的 Entity 要返回子类型
    /// 而 C# 没有协变返回的重写，只能"隐藏"（new）；隐藏一个抽象成员是编译错误（CS0533），
    /// 所以抽象的口留在这里、名字不同，公开的 Entity 两档各自成型——两者必然给同一个答案。</summary>
    protected abstract object EntityInstance { get; }

    /// <summary>列契约取自哪个类型——是会话为它建跟踪器的那一个（调用点上的 T），不是 GetType()。
    /// 两者只在"按基类纳入了一只派生实例"时分岔，而那时的语句也是按基类那张表的列发的。</summary>
    public abstract Type EntityType { get; }

    /// <summary>状态。赋 Modified 等于"每列都算脏"（除主键），那是"我知道它变了但我不想让你比"的显式说法；
    /// 赋 Detached 把它从会话里摘出去，之后 SaveChanges 不再理它。
    /// 主键列永远不进 SET：改一颗已经有值的主键是换键外加把所有依赖方的外键那一格一起改过去，
    /// 所以这样写会得到一句拒绝。</summary>
    public abstract EntityState State { get; set; }

    /// <summary>这一行是否欠着 SaveChanges 的一次处理。Added/Deleted 恒真，
    /// Modified 与 Unmodified 看有没有任何一列被判定改过，Detached 恒假。
    /// 它不等于"会发出一条语句"：脏的那一列若是主键或并发列，进不了 SET，而这一句仍然是真——
    /// 那一行欠着的是一次拒绝，不是零。想知道具体会写出去哪几列，问 ModifiedMembers。
    /// 判脏与否以最近一次 DetectChanges 为准（自动侦测在 SaveChanges 与链式物化之前各跑一次，
    /// 关掉自动侦测就要自己调）。</summary>
    public abstract bool IsModified { get; }

    /// <summary>当前判脏的成员名（列契约的 MemberName，按列序）。
    /// 空集合意味着这一行不进语句——与 IsModified 为假是同一件事的两个读法，
    /// 分开是因为测试与日志要的是"哪几列"而不是"有没有"。</summary>
    public abstract IReadOnlyList<string> ModifiedMembers { get; }

    /// <summary>按成员名取一列的可读写入口。名字写错立刻抛而不是静默给一个 null：
    /// "CustmerName" 这种笔误在字符串列名的库里要等到第一次运行才现形，而这里在调用点上就现形。</summary>
    public abstract PropertyEntry Property(string memberName);
}

/// <summary>类型化的那一档，为的是 Property 能收 lambda（精确控制）。
/// 收 lambda 而不是收字符串是纪律的延续：改了字段名/重命名之后编译就报错。</summary>
public abstract class EntityEntry<T> : EntityEntry where T : class
{
    /// <summary>类型化的读法。它是"隐藏"（new）而不是"重写"：C# 没有协变返回的属性重写。
    /// 实现类不需要（也不该能）覆盖这一条——它只是把 EntityInstance 转交一次，
    /// 所以两档必然给同一个答案；抽象口只有一个，就是 EntityInstance。</summary>
    public new T Entity => (T) base.Entity;

    /// <summary>取一列。只认"本实体单个成员的读法"这一种形状，其余一律拒（与写侧 IUpdate 的 Set
    /// 左侧同一条判据，所以两个成员相加的写法不会看起来能用）。</summary>
    public PropertyEntry Property<TProp>(Expression<Func<T, TProp>> column) => Property(MemberOf(column));

    static string MemberOf(LambdaExpression column)
    {
        var body = column.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } widen)
            body = widen.Operand;

        if (body is not MemberExpression { Expression: ParameterExpression } member)
            throw new QuickSqlException($"Property 的参数必须是本实体单个成员的读法（形如 o => o.列名），当前写的是 {body}。");

        return member.Member.Name;
    }
}

/// <summary>一列的读写与脏标记。IsModified 是"覆盖"而不是"结论"：
/// 显式写 true 会让这一列进 SET（即使值没变，那是"我要强制写这一列"的说法），
/// 显式写 false 会让它在比较判脏之后仍然被剔出 SET（精确控制那格），
/// 没表过态的列才由快照比较决定。三种状态需要两枚位，所以实现侧存的是 bool?[] 而不是 bool[]。</summary>
public abstract class PropertyEntry
{
    public abstract string MemberName { get; }

    /// <summary>实体上此刻的值。写它等于改实体，跟踪器下一次比较会看见。</summary>
    public abstract object? CurrentValue { get; set; }

    /// <summary>物化（或 Attach/Add）那一刻拍下的快照值。
    /// Added 实体上它就是同一个"新值"，因为那时还没有"原来的值"这回事。</summary>
    public abstract object? OriginalValue { get; }

    public abstract bool IsModified { get; set; }
}
