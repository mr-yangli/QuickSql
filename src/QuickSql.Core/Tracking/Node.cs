using QuickSql.Aop;
using QuickSql.Metadata;

namespace QuickSql.Tracking;

/// <summary>跟踪器里一个实例的状态。internal：三张按列下标对齐的数组一旦公开，
/// 就成了"改一下数组就能伪造脏判定"的后门。三张数组按 EntityContract.Columns 的下标对齐、不按名字：
/// 跟踪一万行时每次比较都查一次字典的话，DetectChanges 的开销就从"读一遍字段"变成"读一遍字段 + 一万次哈希"，
/// 列名只出现在出入口（ModifiedMembers 与报告）。</summary>
sealed class Node(object entity, int columnCount, int collectionCount)
{
    /// <summary>被跟踪的实例本体。身份映射与去重判的都是它的引用，所以这张表用的是引用相等比较器。</summary>
    public readonly object Entity = entity;

    /// <summary>最近一次"承认"的值（物化那一刻、Attach、或 SaveChanges 之后）。
    /// 比较判脏读的是它， 交的也是它。</summary>
    public object?[] Snapshot { get; } = new object?[columnCount];

    /// <summary>调用方对每一列表过态的 IsModified：null = 没说过，比较结论作数；
    /// true/false = 强制进/不出 SET。这就是  那句"三态"的存储形态。</summary>
    public bool?[] Overrides { get; } = new bool?[columnCount];

    /// <summary>本次要进 SET 的列（比较结论与覆盖的合成）。只在 DetectChanges 里被写。</summary>
    public bool[] Dirty { get; } = new bool[columnCount];

    /// <summary>每一根集合边上一次看见的那一串子实体的引用，按 EntityContract.Collections 的下标对齐；
    /// 没有集合边的类型上是 null，跟踪一万行标量实体不为这一格多付一只数组。
    /// 它是摘行那一档唯一的判据：collection.Remove(x) 既不发事件也不留痕迹，会话只能拿上一次看见的
    /// 那一串跟现在这一串比。某一格为 null 是"这一串我从来没见过"——与"见过且为空"严格不同：
    /// 前者不判（不替没看见的东西编一份缺席的账），后者才判得出摘掉的是谁。</summary>
    public List<object>?[]? Baselines { get; } = collectionCount > 0 ? new List<object>?[collectionCount] : null;

    public EntityState State { get; set; } = EntityState.Detached;

    /// <summary>它登记在身份映射里用的那个键；null = 没登记（主键还没值，或这个实体类型没有自增键可登记）。
    /// 存着它是为了能从字典里精确摘掉——按当前值重算一次键的话，中途改过主键的实例会摘掉别人的那一条。</summary>
    public IdentityKey? Registered { get; set; }
}

/// <summary>跟踪器按实体类型切开的内部接缝。存在的唯一理由是 ChangeTracker 手里只有 Type 而工作要按 T 做：
/// InsertEngine 与 IEntityTypeSchema 都是泛型的，非泛型的调度层要是也带这些成员，就得开 MakeGenericMethod。
/// 所以本接口上没有一个成员需要知道 T，除了返回契约名字那几条。</summary>
interface ITypeTracker
{
    Type EntityType { get; }

    EntityContract Contract { get; }

    /// <summary>当前跟踪的实例数（含各状态）。</summary>
    int Count { get; }

    /// <summary>跟踪序那张表本身（不是拷贝）。这一格只给内核的只读遍历用：DetectChanges 一类的循环每轮
    /// 都要按会话规模过一遍，每拷一份就是一个 N 元素的引用数组（1 000 只那一档上是 24 KB 一次保存）。
    /// 不拷贝换来的约束：枚举期间不许改这张表——内核的调用点都满足（摘行那一档把「判」与「动手」分成两趟，
    /// 动手那一趟枚举的是自己攒的那份 due）。真需要"当时的快照"的调用方自己拷：
    /// Entries() 要「调用方可以在枚举里 Remove」的公开承诺，SnapshotEntities() 要"快照而不是活集合"，
    /// 两处都付拷贝，因为它们答的就是快照那道题。</summary>
    IReadOnlyList<Node> TrackedNodes { get; }

    /// <summary>这个类型有没有"还没写出去或写的时候必须算一次"的东西。SaveChanges 用它跳过空桶，
    /// 只读过数据的会话不会为每个类型各扫三遍。</summary>
    bool MayHaveWrites { get; }

    /// <summary>这一档这一轮同时欠着插入和删除——「腾位再占位」那句提前删除的触发条件。
    /// 判据只有状态两格（Added 与 Deleted 各至少一行），不去问库里有没有唯一索引：
    /// 这一档要的是「要不要换个先后」，而换了先后对没有唯一索引的表也是中性的。</summary>
    bool HasInsertsAndDeletes { get; }

    int DetectChanges();

    void Clear();

    /// <summary>物化路径的登记口（身份映射）。参数是 object 而不是 T：调度层手里只有一个 Type，
    /// 它要在拿到 EntityTracker〈T〉 之前就把这一步分发出去。静态类型不匹配是内核的不变量破了，不是调用方写错。</summary>
    Node Track(object entity);

    /// <summary>这一行在本档里已经是哪只实例；没登记过返回 null，而且不登记。
    /// 它挂在 Track 旁边是为了补一条 Track 补不到的问句：补载（Load / IncludeMany）把子实体缝进导航时
    /// 只想知道"会话里那一行是哪一只"，要不要把这一行收进会话不是这一步决定的
    /// ——拿 Track 办这件事会顺手把没建档的类型撞响。</summary>
    object? Canonical(object entity);

    /// <summary>这一档里有没有这个引用（不取入口，所以不构造对象）。存在的理由只有一条：
    /// ChangeTracker 要能说出"这只实例在哪一档"，而不是让 Entry 拿推断出来的泛型参数去猜（派生实例会猜错）。</summary>
    bool Owns(object entity);

    /// <summary>这个引用在这一档此刻的状态；null = 不在这一档里。摘行那一档用它把「已经在删的」与
    /// 「从没进过会话的」分开：前者不必再判第二次（它本来就归这次删除），后者才是要新登记成 Deleted 的那一只
    /// ——拿 Owns 判分不开这两格。</summary>
    EntityState? StateOf(object entity);

    /// <summary>读一只实例的主键值（级联落键那一步要拿父的键）。挂在接口上是"泛型参数只住在类型上"的产物：
    /// 读键要走 IEntityTypeSchema〈T〉，而调度层手里只有 Type 与一只装箱实例。</summary>
    object?[] KeyValuesOf(object entity);

    /// <summary>把父的键写进子行手里那几列外键（级联插入）。型不合要在这里说：
    /// schema 的 setter 是强转的装箱写入，而主键列 bigint 配外键列 int 是合法 DDL——
    /// 装不下的现场本来是一次 InvalidCastException，不是一句话。</summary>
    void SetForeignKeys(object entity, int[] ordinals, object?[] values);

    /// <summary>把已有的节点包成入口。不做查找：调度层已经按引用表拿到了节点，这里只补 T。
    /// 挂在接口上是因为 Entries 手里只有 Node，而 TrackedEntry〈T〉 必须是泛型的。</summary>
    EntityEntry EntryOf(Node node);

    /// <summary>这一行此刻会不会被写出去（ 的口径）。</summary>
    bool WillWrite(Node node);

    /// <summary>本档有没有发出去、还没换成真实键的临时主键。依赖方那一次改写先问这一句，
    /// 零个临时键的会话一颗值都不必读。</summary>
    bool HasTempKeys { get; }

    /// <summary>依赖方手里那颗外键值问主端档：它是不是本档发的临时键、换出去的真实键是什么。
    /// 挂在接口上是因为读主端实体的键要 IEntityTypeSchema〈T〉（调度层手里只有 Type 与一个装箱值）。</summary>
    TempProbe ProbeTemp(object? value, out object? real);

    /// <summary>把本类型在这一档（Insert / Update / Delete）里欠的语句发出去，返回报告条目。
    /// traces 是共享的钩子回放缓冲：钩子必须照顺序跑，回放要落在同一条路径上。</summary>
    Task<IReadOnlyList<SaveChangesOp>> WritePhaseAsync(WriteOperation phase, List<HookTrace> traces, CancellationToken ct);

    /// <summary>整批写成功之后结算：删的摘出会话，插的把库给的主键登记进身份映射，所有活着的重拍快照并转
    /// Unmodified。分开成一步而不是写在 WritePhaseAsync 里，是因为三档共用一个事务——
    /// 任何一档抛了都不能结算，否则"回滚了但会话以为写过了"。</summary>
    void PostFlush();
}

/// <summary>一颗临时主键此刻换不换得出真实键。四档而不是布尔：「这不是临时键」与「是，但换不出去」的处置
/// 完全相反——前者一颗值都不许动（那是调用方自己填的），后者必须拒（写出去就是一个库里不存在的键）。
/// 把两格并成一格，要么静默改错值、要么静默写错值。</summary>
enum TempProbe
{
    /// <summary>不是本档发出去的临时键：这个值是调用方自己填的，改写路径不碰它。</summary>
    NotTemp,

    /// <summary>是临时键，而主端那一行已经拿到库里给的真实键（值在 ProbeTemp 的 out 里）。</summary>
    Resolved,

    /// <summary>是临时键，但主端那一行的键还没换出来：它本轮没欠语句，或者它与依赖方在同一张表的同一批里
    /// （自引用——类型序排不出同一类型内部的先后）。</summary>
    Pending,

    /// <summary>是临时键，但它对应的那只实例已经离开会话：标删一只还没写出去的新增行等于忘掉它，
    /// 而依赖方手里那个引用还留着。</summary>
    Orphaned,
}
