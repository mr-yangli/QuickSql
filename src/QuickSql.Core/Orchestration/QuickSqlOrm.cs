using QuickSql.Aop;
using QuickSql.Behaviors;
using QuickSql.Core.Schema;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Query.Filters;
using QuickSql.Query.Model;
using QuickSql.Schema;
using QuickSql.Sql.Ast;
using QuickSql.Tracking;
using QuickSql.Transactions;
using QuickSql.Translation;
using QuickSql.Write;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace QuickSql;

/// <summary>组装根的输入。没有 Type 字段、没有 Type.GetType(string)：provider 由调用方直接构造并交进来，
/// 驱动的反射只发生在 DriverRegistry 拿 DbProviderFactory 那一步。</summary>
public sealed class QuickSqlOrmOptions
{
    public required IDbProvider Provider { get; init; }

    /// <summary>连接串留在字符串形态：内核不认识任何驱动的连接串构建器。这一颗说的是缺省集群 main 的主库，
    /// 从库与其余具名集群走下面那两颗。</summary>
    public required string ConnectionString { get; init; }

    /// <summary>缺省集群的从库清单，按给出的顺序轮询。为空即"这座库没有从库"，读落主库。</summary>
    public IReadOnlyList<string> Replicas { get; init; } = [];

    /// <summary>除 main 之外的具名集群。实体用 [DataSource("名字")] 点名自己住哪一座（解析键按实体）。</summary>
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = [];

    public QuickSqlAop Aop { get; init; } = new();

    /// <summary>Fluent 配置（优先级最高的配置面）。null 表示只用 DataAnnotations。</summary>
    public ModelConfiguration? Model { get; init; }

    /// <summary>字符串列没声明宽度时的缺省宽度。null（缺省）= 维持旧行为，交给 provider 的大文本型
    /// （SQL Server 落 NVARCHAR(MAX)）。正数 = 未声明的 string 列按这个宽度落 DDL，写侧长度尺子也按它量。
    /// 三档不吃这一格：显式宽度（注解 / Fluent）赢过它；显式非正数（[QuickColumn(Length = -1)]、
    /// Fluent HasMaxLength(-1)）表示"这一列明确不要上限"；点名了存储型的列（HasDbType / [Column(TypeName)])
    /// 也不吃——那一列的形状已经由声明者自己钉死。0 与负数是配置错误，构造 orm 时当场抛。</summary>
    public int? DefaultStringLength { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>默认 Throw：宁可少跑一条 SQL，也不要一个"数据库不保证的行序"被当成稳定的分页结果。</summary>
    public PagingOrderingPolicy PagingOrdering { get; init; } = PagingOrderingPolicy.Throw;

    /// <summary>结构同步策略。默认 Disabled：生产库常禁 DDL 权限，不自动执行。
    /// Development 环境可设为 Execute 自动建表；非 Development 环境要求同时设 ForceSchemaSync，否则启动期抛错。</summary>
    public SchemaSyncPolicy SchemaSync { get; init; } = SchemaSyncPolicy.Disabled;

    /// <summary>非 Development 环境下强制执行结构同步的开关。默认 false：光设 SchemaSync 不够，还要这一颗显式放行。</summary>
    public bool ForceSchemaSync { get; init; } = false;

    /// <summary>列级同步那一档：库里已经在的表，要不要逐列比形状、补备注、补索引。默认开；
    /// 关掉退到只有"缺表就建"的那一档。它是 opt-out 而不是 opt-in，因为会丢数据的那两档（删列、改主键与自增）
    /// 不在这一格里——它们在计划层就拒了，任何一颗开关都放行不了。删索引不在这一颗下面，另开 SchemaSyncDropIndexes。</summary>
    public bool SchemaSyncColumns { get; init; } = true;

    /// <summary>删索引那一档：库里有一条契约上没有的索引时，要不要 DROP 掉它。默认关——关掉时那一条只进
    /// 同步结果的"要人工"清单并指名。开与关的分别只在库里多出来的那一条上；唯一约束即便开了也不删：
    /// DROP INDEX 删不动约束，发出去只会以报错收尾。</summary>
    public bool SchemaSyncDropIndexes { get; init; }
}

/// <summary>一座具名集群：一个名字、一颗主库、若干从库。直给的记录形状——加一座库就是往
/// QuickSqlOrmOptions.Clusters 的清单里添一条。Name 是实体侧 [DataSource(Name)] 点的名字，main 是保留名；
/// Primary 是主库串（写、事务与强制主库都落它）；Replicas 按顺序轮询，null 或空即这座集群不分读写。</summary>
public sealed record ClusterConfig(string Name, string Primary, IReadOnlyList<string>? Replicas = null);

/// <summary>组合根（生命周期单例）。只持有连接工厂、元数据缓存与方言，不持有连接：
/// 连接按语句从驱动的连接池取。可以跨线程共享，DbContext 才需要 Scoped。</summary>
public sealed class QuickSqlOrm
{
    ExpressionTranslator? _expressions;
    ProjectionTranslator? _projections;
    Func<Type, int, TableRef>? _tableRefFactory;

    /// <summary>G2 兜底口：元数据注册表由内核反射构建。
    /// 裁剪/AOT 场景请走带注册表的构造，把 G1 生成的注册表传进来。</summary>
    [RequiresUnreferencedCode("G2 兜底注册表按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("G2 兜底注册表编译读写委托。")]
    public QuickSqlOrm(QuickSqlOrmOptions options)
        : this(options, new EntitySchemaRegistry(new ContractResolver(options.Provider, options.Model,
            defaultStringLength: options.DefaultStringLength)))
    {
    }

    /// <summary>干净口：注册表由外部提供（G1 生成、测试替身、或宿主自己的实现）。这条路径构造期零反射。</summary>
    public QuickSqlOrm(QuickSqlOrmOptions options, IEntitySchemaRegistry schemas)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(options.Provider);

        Options = options;
        Provider = options.Provider;
        Schemas = schemas;
        Aop = options.Aop;
        PagingOrdering = options.PagingOrdering;
        DefaultStringLength = options.DefaultStringLength;
        Sources = new ConnectionSources(options.ConnectionString, options.Replicas, options.Clusters);
        Compiler = new QueryCompiler(Provider);
        Ado = new AdoExecutor(Provider, Sources, options.Aop, options.CommandTimeoutSeconds, ContractByTableName);
        Filters = new FilterRegistry(this);
        Behaviors = new BehaviorRegistry(this);
    }

    /// <summary>表名 → 契约的查表口，交给 AdoExecutor 做完整性违例的列回查。只在收口失败那一格被读：
    /// 写路径发语句前必然解析过契约，撞约束的那一只一定在册——查不到的要么是库自起的名字，
    /// 要么是契约外的对象，两格都该给 null。</summary>
    Metadata.EntityContract? ContractByTableName(string tableName)
    {
        foreach (var clrType in Schemas.RegisteredTypes)
        {
            var contract = Schemas.Contract(clrType);

            if (string.Equals(contract.Table.Name, tableName, StringComparison.OrdinalIgnoreCase))
                return contract;
        }

        return null;
    }

    /// <summary>一行注册的 DI 侧：已知工厂、只要一行组装根。工厂由调用方递进来，provider 由生成器在编译期
    /// 登记进 ProviderRegistry 的表里查出来。走 G2 兜底注册表，所以带 RUC/RDC；
    /// 要干净路径就 new QuickSqlOrm(options, schemas)。</summary>
    [RequiresUnreferencedCode("Create 组装的是 G2 兜底构造器；AOT 场景请按 §1.7 用干净口。")]
    [RequiresDynamicCode("Create 组装的是 G2 兜底构造器；AOT 场景请按 §1.7 用干净口。")]
    public static QuickSqlOrm Create(DbKind kind, string connectionString, DbProviderFactory driverFactory)
    {
        if (!ProviderRegistry.TryCreate(kind, driverFactory, out var provider))
            throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记。可能原因：① 生成器 QuickSql.Generators 未作为 Analyzer 挂进编译；② 该 provider 程序集未被引用；③ 该 provider 类未带 [QuickSqlProvider] 或缺 (DbProviderFactory) 公开构造器。已登记：{string.Join(", ", ProviderRegistry.Registered)}。");
        return new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
        });
    }

    /// <summary>一行注册的驱动侧：只报 DbKind 与连接串，工厂由 DriverRegistry 按内建表现场解析。
    /// 与上一颗同样依赖生成器登记；这一颗会真去按程序集名装载驱动，现场没驱动时抛的是
    /// DriverNotAvailableException（消息带驱动名与 drivers/ 探测位）而不是"未登记"。</summary>
    [RequiresUnreferencedCode("Create 组装的是 G2 兜底构造器，且工厂解析读 Instance 静态字段（§12.7 F）。")]
    [RequiresDynamicCode("Create 组装的是 G2 兜底构造器。")]
    public static QuickSqlOrm Create(DbKind kind, string connectionString)
    {
        var driverKey = ProviderRegistry.DriverKeyOf(kind)
            ?? throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记（生成器未挂或 provider 未引用）。已登记：{string.Join(", ", ProviderRegistry.Registered)}。");
        var factory = new QuickSql.DriverLoading.DriverRegistry().Factory(driverKey);
        return Create(kind, connectionString, factory);
    }

    public QuickSqlOrmOptions Options { get; }

    public IDbProvider Provider { get; }

    public IEntitySchemaRegistry Schemas { get; }

    public QuickSqlAop Aop { get; }

    public QueryCompiler Compiler { get; }

    public AdoExecutor Ado { get; }

    /// <summary>在册的集群与各自的串。它与 Options 一样是"配置"的一侧：
    /// 实体上只有集群名，串只在这一颗与 Options 上看得见。</summary>
    public ConnectionSources Sources { get; }

    public PagingOrderingPolicy PagingOrdering { get; }

    /// <summary>Options.DefaultStringLength 的回读口：DbContext 自建注册表时要把同一颗缺省宽度带过去，
    /// 否则同一个 orm 的两种注册路径会建出两种宽度的表。</summary>
    public int? DefaultStringLength { get; }

    /// <summary>全局过滤器（软删 / 多租户）。一个 orm 实例一份，注册在启动期做完，
    /// 之后读路径与 Update / Delete 两条链都会带上它。它与 Aop 的分工：CudBuilding 是"整句文本改写的地方"，
    /// 这一份是"谓词从哪儿来"。今天它管不到 SaveChanges 与批量：那两条路上的 WHERE 长在 EntityTracker 上（欠账）。</summary>
    public FilterRegistry Filters { get; }

    /// <summary>库里预置的写侧行为（今天只有 SoftDelete 一件）。它与 Filters 是两只对象、各管一半：
    /// 这一份把 DELETE 换成置位的 UPDATE，Filters 把已置位的行从读侧与写侧的 WHERE 里筛掉——
    /// 只装其中一只的项目要么删得动而读得见，要么读得干净而删的还是行本身。
    /// 三处删除出口（Delete 链、DeleteRange、SaveChanges 里被摘掉的行）都读这一份。</summary>
    public BehaviorRegistry Behaviors { get; }

    /// <summary>lambda → AST 的归约器。按需构造：只有真正走到链式翻译的调用方才为它付 AOT 代价。
    /// 标注只能落在 get 上——RequiresUnreferencedCode 的目标是方法，属性本身不是合法目标（CS0592）。</summary>
    internal ExpressionTranslator Expressions
    {
        [RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
        [RequiresDynamicCode("不可约子表达式的退路是 LambdaExpression.Compile()。")]
        get => _expressions ??= new ExpressionTranslator(Provider, Schemas, TableRefFactory);
    }

    internal ProjectionTranslator Projections
    {
        [RequiresUnreferencedCode("转调 ExpressionTranslator。")]
        [RequiresDynamicCode("转调 ExpressionTranslator。")]
        get => _projections ??= new ProjectionTranslator(Expressions);
    }

    /// <summary>读侧入口。返回的对象是不可变的值，可以复用、传出去、不需要 Clone。没有非泛型口：
    /// 按运行期 Type 造查询只能靠 Activator.CreateInstance + MakeGenericMethod，那是点名要避开的形态；
    /// 动态表名/影子表请走 ModelConfiguration 的表名映射。这里的标注是整条读路径的入口税。</summary>
    [RequiresUnreferencedCode("G2/G3 读路径：闭包求值与投影组装按运行期 Type 反射。")]
    [RequiresDynamicCode("G2/G3 读路径：投影与比较委托要编译表达式树，AOT 下不可用。")]
    public IQuery<T> Select<T>() where T : class => new QuickSqlQuery<T>(RootEngine(typeof(T)));

    /// <summary>根模型：一个表位 + 实体投影。取契约这一步就是 G2（按 Type 反射解析实体），标注跟着 Select。
    /// tracker 是会话的跟踪器，null 就是这条链不跟踪——它不进模型，所以 orm.Select〈T〉() 与
    /// db.Set〈T〉().AsNoTracking() 的模型文本逐字节相等。gate 是会话的并发闸，null 就是根链，不进闸。</summary>
    [RequiresUnreferencedCode("按 Type 向注册表取实体契约（G2）。")]
    [RequiresDynamicCode("G2 注册表编译读写委托。")]
    internal QueryEngine RootEngine(Type clrType, ChangeTracker? tracker = null, SessionGate? gate = null, Schema.IEntitySchemaRegistry? schemas = null)
    {
        var table = NewTableRef(clrType, 0, schemas);
        var model = new QueryModel { Tables = [table], Plan = new EntityProjection(0) };

        return new QueryEngine(this, model, tracker, gate: gate, schemas: schemas);
    }

    /// <summary>造一个会话。这条路上有反射：Activator.CreateInstance 按 Type 找构造函数，所以带 RUC/RDC，
    /// AOT 场景请自己 new——普通构造调用一行反射都没有，功能完全等价。
    /// 每次调用都是新会话（新的身份映射），这里没有任何共享或池化。要求 TContext 有
    /// public TContext(QuickSqlOrm orm) 构造函数，没有的话 MissingMethodException 从这里抛出。</summary>
    [RequiresUnreferencedCode("CreateDbContext 按 Type 反射构造 TContext（§12.7 F）；AOT 下请自己 new。")]
    [RequiresDynamicCode("CreateDbContext 按 Type 反射构造 TContext；AOT 下请自己 new。")]
    public TContext CreateDbContext<TContext>() where TContext : DbContext
        => (TContext)Activator.CreateInstance(typeof(TContext), this)!;

    /// <summary>在当前执行流上取一只会话：第一次取时建一只，之后每次取到的都是那一只，
    /// 同一流里前后两个方法不必经 DI 注册也共享同一份身份映射。共享范围是一条 async 执行流，
    /// 不是进程也不是线程：AsyncLocal 的写入只在同步那段回到调用方的帧，所以 await 之后的子任务里建的那只
    /// 留在它自己的分支里，父帧和兄弟分支取不到；反过来父帧先建、子任务后取，拿到的就是父帧那一只。
    /// 三条边界：槽位挂在实例上（两只 orm 共用一条流时各拿各的）；共享的是同一只 ChangeTracker 而它不是线程安全的，
    /// 这一档不设闸门，要并发就各自开流或各自 CreateDbContext；会话没有 IDisposable，"结束一条共享会话"
    /// = 让那条流出作用域，或在流末端复用它的场合自己按 DbChangeTracker.Clear 清账。</summary>
    [RequiresUnreferencedCode("走 CreateDbContext 的反射构造（§12.7 F）；AOT 下请自己 new 并把实例传下去。")]
    [RequiresDynamicCode("走 CreateDbContext 的反射构造；AOT 下请自己 new。")]
    public TContext CreateSharedContext<TContext>() where TContext : DbContext
    {
        var type = typeof(TContext);

        for (var at = _shared.Value; at is not null; at = at.Next)
        {
            if (at.Type == type)
                return (TContext)at.Context;
        }

        // 先建、再把新节点挂到这条流自己的那份链表头上：赋值 AsyncLocal 是"换引用"而不是"改表"，
        // 父帧的链头永远不被子任务改动——上面那句「不回写父帧」全靠这一行是赋值而不是 Add。
        var created = CreateDbContext<TContext>();
        _shared.Value = new SharedFrame(type, created, _shared.Value);

        return created;
    }

    /// <summary>本实例在当前执行流上的共享会话链表。节点不可变，头指针按流复制（AsyncLocal 的语义）。</summary>
    readonly AsyncLocal<SharedFrame?> _shared = new();

    sealed record SharedFrame(Type Type, DbContext Context, SharedFrame? Next);


    /// <summary>FROM 序列里第 index 个表位的别名。由位置唯一决定，
    /// 形参名、重命名重构、nameof 都不影响产出的 SQL。</summary>
    internal static string AliasAt(int index) => "t" + index.ToString();

    /// <summary>FROM 序列里的一个表位。别名由位置生成（t0/t1…），
    /// 形参名、重命名重构、nameof 都不影响产出的 SQL。</summary>
    internal TableRef NewTableRef(Type clrType, int index, Schema.IEntitySchemaRegistry? schemas = null)
    {
        // 会话级模型覆写（DbContext.OnModelCreating）那颗口。null = 照旧走组装根的注册表
        // （orm 级 Model 在它身上）——没覆写的上下文一颗字节都不多付。
        var contract = (schemas ?? Schemas).Contract(clrType);

        return new TableRef(index, AliasAt(index), contract.Table, contract);
    }

    /// <summary>NewTableRef 的那颗委托，按实例缓存一次。递给翻译现场的是方法组，而方法组每次转换都新建一只
    /// 委托，那笔钱乘在读路径的算子次数上；缓存之后一只 Orm 只付一次。</summary>
    internal Func<Type, int, TableRef> TableRefFactory => _tableRefFactory ??= ((clrType, index) => NewTableRef(clrType, index));

    // ── 写侧入口 ──────────────────────────────────────────────

    /// <summary>单行插入。与 Select 不同，这条路径上没有 RUC/RDC 入口税：写侧只向注册表要列契约与已编译的
    /// 读写委托，不编译表达式树、不求值闭包。</summary>
    public IInsert<T> Insert<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new InsertEngine<T>(this, [entity]);
    }

    /// <summary>多行插入（L3 多值语句 + 客户端分块）。不叫 Insert：Insert(T) 与 Insert(IEnumerable〈T〉) 同族重载
    /// 传集合时必然选中前者（identity 转换胜过隐式引用转换），orm.Insert(list) 会把 List 当成实体去建契约。</summary>
    public IInsert<T> InsertRange<T>(IEnumerable<T> rows) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);

        // 立刻物化：延迟到执行才枚举的话，AuditValue 填充与"看到的那份数据"就可能不是同一份。
        return new InsertEngine<T>(this, rows as T[] ?? [.. rows]);
    }

    /// <summary>一句 upsert：按主键决定"没有就插、有就改"——每一行把除主键、库里给值的列与并发列以外的全部列
    /// 写进去，主键既是插入清单的一部分、又是冲突判定的键。发射形态由这一家自己定（MySQL/PG 血统走句尾，
    /// SqlServer/Oracle 血统/虚谷/Gbase8s 走整语句 MERGE），调用方不选。它是 UpdateRange 的 Upsert 档同一句文本的
    /// 另一扇门（同一把列尺子、同两道当场拒），独立出来只为让"我要 upsert"写在方法名上；入口零反射税。
    /// 数据走 SetSource 而不是形参，同一只链可以先 SetSource 一只再换一批。
    /// 主键由库给值而调用方没带键时当场拒——那种键的冲突判定出不来。</summary>
    public IInsertOrUpdate<T> InsertOrUpdate<T>() where T : class => new InsertOrUpdateEngine<T>(this);

    /// <summary>集合同步链（SqlSugar 的 Storageable 同一语义）：给一个集合、给一把键（不给就用实体主键，
    /// 自增键"没填"的行直接进新增桶），与库对账出新增/修改/删除三个桶；对账只发读（ToStorage），
    /// 执行与否、执行哪几桶由调用方挑（ExecuteCommandAsync 一口气做，顺序固定删 → 改 → 增）。
    /// 键点名、删除桶、分块对账的形状全在 IStorageable 抬头。</summary>
    [RequiresUnreferencedCode("对账要按 Type 解析实体契约（G2）并编译键读口。")]
    [RequiresDynamicCode("键读口编译成委托。")]
    public IStorageable<T> Storageable<T>(IEnumerable<T> rows) where T : class => new StorageableChain<T>(this, rows);

    /// <summary>UPDATE 链（orm.Update〈Item〉().Set(...).ExecuteAffrowsAsync(ct)）。
    /// 这一条链与 Select 一样要付 RUC/RDC 入口税，而 Insert 不付：条件与表达式值要过 ExpressionTranslator
    /// （闭包求值 + 退路编译），插入只向注册表要列契约与委托——这是"写侧两条链的 AOT 成本不同"的唯一出处。
    /// Filters 上在册的过滤器会叠进这一句的 WHERE，口径与 Delete 相同。</summary>
    [RequiresUnreferencedCode("Where/Set 的 lambda 归约走闭包常量求值。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    public IUpdate<T> Update<T>() where T : class => new UpdateEngine<T>(this);

    /// <summary>DELETE 链（orm.Delete〈Cart〉().Where(...)）。这一句改的是行本身还是 deleted 那一格，
    /// 取决于这张表上有没有在册的软删行为：注册过 SoftDelete 的实体，这一句在构建时就换成"置位 + 记时间"的
    /// UPDATE（改形状那一步在 EmitDelete 之前，文本仍走 ApplyCud 与 ApplyBinding，日志与审计看到的永远是
    /// 真正发出去的那一句）。没注册的实体仍发物理 DELETE，调用方自己的 CudBuilding 订阅者仍能在文本层再改一次
    /// ——三种情形 Explain().Reason 各说一句，按最终状态自陈。Filters 上在册的过滤器会叠进 WHERE。</summary>
    [RequiresUnreferencedCode("Where 的 lambda 归约走闭包常量求值。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    public IDelete<T> Delete<T>() where T : class => new DeleteEngine<T>(this);

    /// <summary>按实体定位的单行 UPDATE（orm.Update(order).ExecuteAffrowsAsync()）：列清单来自这只实体，
    /// WHERE 来自它的主键，读作"把这只实体写回它那一行"。它写的是带来值的那些列：除主键、库里给值的列与
    /// 并发列以外，值为 null 的属性不进 SET——与 UpdateRange 的「整行交出去，NULL 就是 NULL」正对面，
    /// 要把某一格真写成 NULL 只有链式那一条做得到。值类型列永远带来值，每趟都进 SET。
    /// 取的是 AuditValue 之后的值、种类是 "update"，逐列的契约尺子照链式那份过；主键为空、
    /// 或主键由库给值而交来的是没给的形态，当场拒。分表实体按它自己的分片键换算那一张片；
    /// 全局过滤器与软删那两道闸门在这条路上与链式同一条。交回的仍是 IUpdate，可以再叠 Where；
    /// Set 只补得动实体没带（即 null）的那些列——带值的那一列再点一次会被"设置了两次"守卫拦下。
    /// 取值段跑在这一颗入口的调用时刻，之后改实体不会再改出一份文本。</summary>
    [RequiresUnreferencedCode("全局过滤器的叠加走闭包常量求值。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public IUpdate<T> Update<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RefuseCollection(entity, nameof(Update));

        return UpdateEngine<T>.FromEntity(this, entity);
    }

    /// <summary>按实体定位的单行 DELETE（orm.Delete(order).ExecuteAffrowsAsync()）：这一格只借主键，不借值
    /// ——实体上其余列一列都不读，"哪几列是 null"在这条路上没有含义。发出去的是 DELETE 还是置位的 UPDATE，
    /// 与 Delete() 链同一条规则。复合主键在这里支持（逐列 = 再 AND）：DeleteRange 拒它靠的是
    /// "IN 清单装不下元组"，那一句在单行上根本不成立。分表实体同样按自己的分片键换算那一片。</summary>
    [RequiresUnreferencedCode("全局过滤器的叠加走闭包常量求值。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public IDelete<T> Delete<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RefuseCollection(entity, nameof(Delete));

        return DeleteEngine<T>.FromEntity(this, entity);
    }

    /// <summary>「把一批实体递给了一只实体的门」那道拦截。判据是实例的形状而不是 typeof(T) 的接口表：
    /// Type.GetInterface 会在裁剪产物上要求把 DynamicallyAccessedMembers 一路标到公开方法的泛型参数上，
    /// 为一道守卫把注解摊进公共 API 是不值的。能走到这一格的必然是引用类型的序列（IEnumerable 的协方差
    /// 只在引用类型之间成立），所以 string、byte[] 与值类型元素的集合天然落不进来；
    /// 真把 IEnumerable 实现成自身形状的聚合根会被拦——宁可让调用方改走链式，也不安静地按一张不存在的表建契约。</summary>
    static void RefuseCollection(object entity, string entrance)
    {
        if (entity is not IEnumerable<object>)
            return;

        throw new QuickSqlException($"{entrance} 收的是一只实体，而 {entity.GetType().Name} 是一批可以枚举的对象：" +
            $"把它当实体会去建一张不存在的表的契约。整批按主键写走 {entrance}Range(rows)，" +
            $"按条件写走 {entrance.ToLowerInvariant()}<实体>().Where(...)。");
    }

    /// <summary>批量整行更新：每行把除主键、库里给值的列与并发列以外的全部列写进 SET，主键进 WHERE；
    /// Merged 档把多条 UPDATE 并成一条命令文本一次递交，只有声明了 SupportsMergedStatements 的家可达。
    /// 入口零反射税（列来自契约、值来自编译好的委托，一个表达式树都不建）。
    /// 它不与 SaveChanges 合流：那条要跟踪器的脏列判定，这一条的契约就是"整行交出去"。</summary>
    public IUpdateBulk<T> UpdateRange<T>(IEnumerable<T> rows) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);

        // 立刻物化，理由与 InsertRange 相同：看到的那份数据与发出去的那份必须是同一份。
        return new UpdateBulkEngine<T>(this, rows as T[] ?? [.. rows]);
    }

    /// <summary>批量按主键删：Merged 档一句 WHERE 键 IN (…) 是单条语句，不欠多语句能力，
    /// 所以这一条在所有 provider 上 Auto 都直接取 Merged；复合主键当场拒并指路。入口同 UpdateRange：零反射税。</summary>
    public IDeleteBulk<T> DeleteRange<T>(IEnumerable<T> rows) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new DeleteBulkEngine<T>(this, rows as T[] ?? [.. rows]);
    }

    // ── 事务 ──────────────────────────────────────────
    /// <summary>开一条事务、把它放进当前执行流的环境槽位，之后本实例的每一条语句（读侧也算）
    /// 都走那条被它独占的连接。不调 Commit 而直接出 using 块 = 回滚。同步方法是有原因的，
    /// 见 IQuickSqlTransaction。</summary>
    public IQuickSqlTransaction BeginTransaction(IsolationLevel level = IsolationLevel.ReadCommitted, TransactionPropagation propagation = TransactionPropagation.Requires) => Ado.BeginTransaction(level, propagation);

    /// <summary>钉住一座集群开事务：之后这条事务里的每一条语句都必须说同一座集群，要另一座的当场拒。
    /// 一场事务底下只有一条连接，而"跨集群的一场事务"要的是两阶段提交那一族协议（规格未裁），
    /// 不给它一个能写出来的形状。上面那颗缺省重载说的就是缺省集群。</summary>
    public IQuickSqlTransaction BeginTransaction(string cluster, IsolationLevel level = IsolationLevel.ReadCommitted, TransactionPropagation propagation = TransactionPropagation.Requires)
        => Ado.BeginTransaction(cluster, level, propagation);

    /// <summary>环境事务。与 BeginTransaction 是同一个机制（AsyncLocal 槽位），只是调用方想说的是
    /// "后面所有调用自动进来"。level 是这一场开出来的隔离级别：之后在这条流上嵌套（Requires）开保存点时，
    /// 传了与它不同的级别当场拒（判据在 AdoExecutor.BeginTransaction）。</summary>
    public IQuickSqlTransaction BeginAmbientTransaction(IsolationLevel level = IsolationLevel.ReadCommitted) => Ado.BeginTransaction(level);

    /// <summary>开一场事务并钉在这只 orm 上（归属跟实例，不跟执行流）。用于调用方不是语句的父帧、
    /// 而是隔着一层 async 装配把它装上的现场：装配在自己的帧里 Begin，回到仓储那一帧时 AsyncLocal 那一笔
    /// 已经看不见了，每句各拿各的池连接自动提交；钉在实例上之后，凡是从这只 orm 发出的语句都进同一场。
    /// 保存点嵌套与集群闸门同一条判据；已经钉过再钉当场拒，结束（提交/回滚/Dispose）时自动摘钩。
    /// 同步方法，同一只实例上不要并发使。</summary>
    public IQuickSqlTransaction BeginSessionTransaction(IsolationLevel level = IsolationLevel.ReadCommitted)
        => Ado.BeginSessionTransaction(DataSourceRouting.DefaultCluster, level);

    /// <summary>钉住一座集群开会话事务：语句要落别座集群的，仍然当场拒。</summary>
    public IQuickSqlTransaction BeginSessionTransaction(string cluster, IsolationLevel level = IsolationLevel.ReadCommitted)
        => Ado.BeginSessionTransaction(cluster, level);

    /// <summary>不抛异常、返回结果对象。回滚失败不在这里吞：Dispose 兜底回滚那一步炸了就让它抛出去——
    /// "事务已回滚"这个结论必须由事务自己证明，不能由一个 Success = false 代替。
    /// 业务异常在先、回滚失败在后时，抛出去的必须是原先那一句——回滚失败（锁冲突/连接断了）从旁路记进
    /// TransactionResult.RollbackFailure，不再从收口冒出来把业务异常整个换掉；
    /// 收口失败两条都在时两个都看得到。</summary>
    public async Task<TransactionResult> ExecuteInTransactionAsync(
        Func<CancellationToken, Task> body,
        IsolationLevel level = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        var tran = BeginTransaction(level);

        try
        {
            await body(ct).ConfigureAwait(false);
            tran.Commit();

            return TransactionResult.Ok();
        }
        catch (Exception exception)
        {
            // 兜底回滚在这里完成（不留给 finally）：失败记进旁路而不是从收口冒出来盖掉业务异常。
            // RollbackQuiet 只住具体类上（接口是公开面）。
            return TransactionResult.Failed(exception, ((QuickSqlTransaction)tran).RollbackQuiet());
        }
        finally
        {
            tran.Dispose();
        }
    }

    public async Task<TransactionResult<T>> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> body,
        IsolationLevel level = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        var tran = BeginTransaction(level);

        try
        {
            var value = await body(ct).ConfigureAwait(false);
            tran.Commit();

            return TransactionResult.Ok(value);
        }
        catch (Exception exception)
        {
            // 同上：回滚失败走旁路，业务异常保住。
            return TransactionResult.Failed<T>(exception, ((QuickSqlTransaction)tran).RollbackQuiet());
        }
        finally
        {
            tran.Dispose();
        }
    }

    // ── 结构同步 ──────────────────────────────────────────────

    /// <summary>同步已注册实体的表结构，按 SchemaSync 策略决定行为：Disabled 返回空结果；Execute 建缺失的表，
    /// 并按 SchemaSyncColumns 决定要不要逐列比形状（补列、改长度与标度、改可空、重发备注、补索引）；
    /// Script 只生成建表 DDL 不执行，列级那一档在它身上判不了（不连库就没有"库里现形状"那半份判据），
    /// 结果里的 NotJudged 会指名这一句。库里多出来的那条索引默认只进"要人工"清单，
    /// 要它真 DROP 得开 SchemaSyncDropIndexes。非 Development 环境下 Execute 要求 ForceSchemaSync 为 true，
    /// 否则抛 InvalidOperationException。</summary>
    public async Task<SchemaSyncResult> SyncStructureAsync(CancellationToken ct = default)
    {
        var policy = Options.SchemaSync;

        if (policy == SchemaSyncPolicy.Execute)
            GuardSchemaSyncAllowed();

        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Sources, Options.SchemaSyncColumns, Options.SchemaSyncDropIndexes);

        return await syncer.SyncAsync(policy, ct).ConfigureAwait(false);
    }

    /// <summary>SyncStructureAsync 的同步壳，与 SaveChanges() 同样的 sync-over-async 形态。</summary>
    public SchemaSyncResult SyncStructure()
        => SyncStructureAsync().GetAwaiter().GetResult();

    // ── 分表建片 ─────────────────────────────────────────────

    /// <summary>按时间范围给分表实体建缺失的那几张片。范围是必填的："现存的分片该有哪几段"只有业务知道，
    /// 默认建全部在一颗日档实体上是凭空造几千张表。起与止各自下整到本档的周期，中间逐周期展开；
    /// 逐张先问存在再发 DDL，同一个范围跑两次第二次一张也不建。走 Execute 那一道闸门
    /// （非 Development 环境要 ForceSchemaSync）；档与范围不匹配（哈希实体传日期）当场抛。</summary>
    public async Task<SplitTablesResult> CreateSplitTablesAsync<T>(DateTime from, DateTime to, CancellationToken ct = default)
        where T : class
    {
        GuardSchemaSyncAllowed();

        var (template, shards) = SplitShards<T>(from, to);
        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Sources);

        return await syncer.CreateShardsAsync(template, shards, ct).ConfigureAwait(false);
    }

    /// <summary>CreateSplitTablesAsync（时间范围档）的同步壳（启动期建片用）。</summary>
    public SplitTablesResult CreateSplitTables<T>(DateTime from, DateTime to) where T : class
        => CreateSplitTablesAsync<T>(from, to).GetAwaiter().GetResult();

    /// <summary>按桶号给哈希档的分表实体建缺失的那几张片。桶号越界抛，重复去掉。</summary>
    public async Task<SplitTablesResult> CreateSplitTablesAsync<T>(int[] buckets, CancellationToken ct = default)
        where T : class
    {
        GuardSchemaSyncAllowed();

        var (template, shards) = SplitShards<T>(buckets);
        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Sources);

        return await syncer.CreateShardsAsync(template, shards, ct).ConfigureAwait(false);
    }

    /// <summary>CreateSplitTablesAsync（桶号档）的同步壳。</summary>
    public SplitTablesResult CreateSplitTables<T>(params int[] buckets) where T : class
        => CreateSplitTablesAsync<T>(buckets).GetAwaiter().GetResult();

    /// <summary>那一份建片脚本，不连库（Script 模式同一条路）：给 DBA 审批，也让"一个范围算出哪几张表、
    /// 每张表的 DDL 长什么样"在离线处就钉得住。SyncStructureAsync 的 Script 档跳过分表实体，
    /// 这一格是它们唯一的出脚本口。</summary>
    public string SplitTablesScript<T>(DateTime from, DateTime to) where T : class
    {
        var (template, shards) = SplitShards<T>(from, to);

        return new Core.Schema.SchemaSyncer(Provider, Schemas, Sources).ShardsScript(template, shards);
    }

    /// <summary>SplitTablesScript（时间范围档）的哈希档。</summary>
    public string SplitTablesScript<T>(params int[] buckets) where T : class
    {
        var (template, shards) = SplitShards<T>(buckets);

        return new Core.Schema.SchemaSyncer(Provider, Schemas, Sources).ShardsScript(template, shards);
    }

    /// <summary>契约 + 换算好的片清单。档不匹配的那一句由 SplitRouting 说
    /// （时间范围打在哈希档上、桶号打在时间档上都是它那句），这里不加第二套判据。</summary>
    (EntityContract Template, IReadOnlyList<TableInfo> Shards) SplitShards<T>(DateTime from, DateTime to) where T : class
    {
        var contract = Schemas.Contract<T>();

        return (contract, SplitRouting.EnumerateTime(contract.Table, NeedSplit(contract), from, to));
    }

    (EntityContract Template, IReadOnlyList<TableInfo> Shards) SplitShards<T>(int[] buckets) where T : class
    {
        var contract = Schemas.Contract<T>();

        return (contract, SplitRouting.EnumerateBuckets(contract.Table, NeedSplit(contract), buckets));
    }

    static SplitFacts NeedSplit(EntityContract contract)
        => contract.Table.Split
           ?? throw new QuickSqlException(
                $"{contract.Table.Name} 没有声明分表（类上没有 [SplitTable]），没有片可建。整库建表请用 orm.SyncStructure。");

    void GuardSchemaSyncAllowed()
    {
        if (Options.ForceSchemaSync)
            return;

        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        if (string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(
            $"非 Development 环境下执行结构同步需要 ForceSchemaSync=true（当前环境：{env ?? "(未设置)"}）。" +
            "生产库常禁 DDL 权限，请先用 SchemaSync=Script 生成脚本、交 DBA 审批后手动执行（§9.5）。");
    }
}
