using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Schema;
using QuickSql.Tracking;
using QuickSql.Write;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace QuickSql;

/// <summary>工作单元：会话级连接复用——某一格 (集群, 角色) 首次执行时懒建连接并绑到当前 async 执行流，
/// 之后同一会话里用到那一格的语句（读/写/事务）都走它；DisposeAsync 逐条归还。
/// 生命周期 Scoped / Transient，绝不能单例：身份映射与脏标记都是它的状态，
/// 跨请求共享一个会话得到的不是缓存而是别人的未提交改动。
/// 派生类的等价写法是表达式体属性（public DbSet〈Order〉 Orders => Set〈Order〉();），
/// Set 按类型缓存，拿到的一直是同一只对象。两档构造（构造时收 orm / 先建后接 UseOrm）只在
/// 「组装根什么时候到手上」这一格不同，接好之后形状与语义逐字一致。</summary>
public class DbContext : IAsyncDisposable, IDisposable
{
    /// <summary>本会话的组装根。主构造那一档在构造时给，先建后接那一档在  那一刻给；
    /// 接好之前各成员经 / 的守卫抛可读错，不埋空引用。</summary>
    QuickSqlOrm? _orm;

    /// <summary>会话的跟踪裁判（身份映射、快照、脏列与摘行那两问）。随组装根一起建（）。</summary>
    ChangeTracker? _tracker;

    /// <summary>"这一条流上哪几座集群写过"的账本把手（写后读那一档）。挂在收到组装根的同步帧上——
    /// AsyncLocal 只有同步被调方才写得回调用方的帧（主构造那一帧是构造，先建后接是 UseOrm 的调用帧）。</summary>
    IDisposable? _flowWrites;

    /// <summary>会话上已经发出去的那些 DbSet，按实体类型缓存。用 ConcurrentDictionary：共享会话的两条分支
    /// 可以同时首次 Set，裸 Dictionary 的并发首写是字典内部结构损坏级别的竞态；GetOrAdd 的工厂在竞态下
    /// 可能跑多遍，多建的那只被丢弃，无害（工厂内只有幂等的挂工厂与档登记）。</summary>
    readonly ConcurrentDictionary<Type, object> _sets = [];
    readonly List<DbConnection> _sessionConnections = [];

    /// <summary>先建后接那一档的构造：派生类型由 DI 容器建出来（属性注入这一段归容器），
    /// 组装根等运行期解析出来再由派生类收。收之前各成员都是「还没接线」的守卫态——
    /// 首次使用之前必须 UseOrm，这是这一档唯一的纪律。</summary>
    protected DbContext() { }

    /// <summary>主构造（派生写法那一档）：构造即收下组装根，账本把手也挂在构造这一同步帧上。</summary>
    public DbContext(QuickSqlOrm orm) => UseOrm(orm);

    /// <summary>收下本会话的组装根：建跟踪裁判、挂写后读账本把手。一只会话只接一场，接第二场当场拒。
    /// 会话不建连接、连接按语句懒取，所以「接」本身没有连接要开、没有语句要发。</summary>
    protected void UseOrm(QuickSqlOrm orm)
    {
        ArgumentNullException.ThrowIfNull(orm);

        if (_orm is not null)
            throw new QuickSqlException(
                $"同一只会话只能接一场组装根：{GetType().Name} 已经接过一只（{_orm.GetType().Name}），" +
                "先建后接那一档一只上下文只收一回。"
            );

        _orm = orm;
        _flowWrites = AdoExecutor.InstallFlowWrites();
    }

    /// <summary>组装根到不到手上。先建后接那一档的卸载路径用它兜住「建出来了还没接线就 Dispose」
    /// 的合法路径（DI 容器收一个解析出来但没初始化的实例，不该炸）。</summary>
    protected bool HasOrm => _orm is not null;

    public QuickSqlOrm Orm => _orm ?? throw new QuickSqlException(
        "会话还没有接入组装根：构造时把 orm 给出来（主构造那一档），或派生侧在首次使用前调 UseOrm(orm)。"
    );

    /// <summary>会话的跟踪视图。DetectChanges()、Clear()、Entries() 都从这里进。</summary>
    public DbChangeTracker ChangeTracker => Tracker;

    /// <summary>内核自己那一份（DbSet 要的是它，而公开口只能看见抽象门面）。两个名字指同一个对象，
    /// db.Set〈Order〉().Local 与 db.ChangeTracker.Entries() 说的必然是同一份会话状态——
    /// 要是各拿一份，身份映射就变成两份要互相同步的账。</summary>
    internal ChangeTracker Tracker
    {
        // RUC/RDC 挂在 EnsureTracker 上（特性上不了属性访问器）。
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "触点已按 G2 入口税标注；EnsureTracker 自带 RUC。")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "同上。")]
        get => EnsureTracker();
    }

    [RequiresUnreferencedCode("懒建时按 Type 反射判覆写并构建会话注册表（G2）。")]
    [RequiresDynamicCode("懒建时编译会话注册表的读写委托（G2）。")]
    ChangeTracker EnsureTracker()
    {
        if (_tracker is null)
        {
            if (!HasOrm)
                throw new QuickSqlException(
                    "会话还没有接入组装根：跟踪裁判随组装根一起建（主构造那一档在构造时，先建后接那一档在 UseOrm 那一刻）。");

            // 懒建：覆写了模型钩子的上下文要先把会话注册表备好，跟踪器才带得上它——
            // 钩子只能在构造之外调（基类构造里虚调派生成员，派生的字段还没初始化）。
            _tracker = EnsureModel() is { } schemas
                ? new ChangeTracker(Orm, schemas)
                : new ChangeTracker(Orm);
        }

        return _tracker;
    }

    /// <summary>本会话的并发闸（SessionGate，EF Core 的 ConcurrencyDetector 同一条语义）：
    /// 同一会话上同时只许一个操作在飞，第二个进场被拒成一句听得懂的 QuickSqlException，
    /// 而不是跟踪器深处一颗随机 NRE（并发写更险：双倍落库且全程无异常）。闸随 DbSet 的根引擎下传，
    /// 读侧七个执行口与写侧 SaveChangesAsync、LoadAsync 进闸；根链没有会话，不进闸。
    /// 尽力检测、抓到即拒——结论仍然是"不要共用会话"，不是"有闸就共用"。</summary>
    internal SessionGate Gate { get; } = new();

    /// <summary>自动侦测开关。关掉的只有「物化之前」与 HasChanges 里那两次自动侦测；
    /// SaveChanges 恒定先侦测再写，否则关掉它就等于「只写你显式标过的行」，
    /// 而那是 Attach + Entry(...).State 那条显式路线的语义，不是一个开关该改的东西。</summary>
    public bool AutoDetectChangesEnabled
    {
        get => Tracker.AutoDetectChangesEnabled;
        set => Tracker.AutoDetectChangesEnabled = value;
    }

    // ── 模型钩子 ────────────────────────

    /// <summary>模型配置钩子（EF 同形）。派生类覆写它，用 ModelBuilder 配实体：表名/列名/长度/主键/自增/忽略/备注/默认值。
    /// 覆写即整份模型：覆写之后这一只上下文走这里配出的那份 Fluent，组装根上的 orm 级 Model 不再参与；
    /// 要全局默认就不覆写，配置按派生类型缓存。触发时机是第一次需要模型的那一刻（首次 Set/Add），不在构造里——
    /// 基类构造里虚调派生成员，派生的字段还没初始化。mb 点过名的实体按 KnownEntityTypes 预登记进会话注册表。
    /// 已知边界：结构同步走组装根那份注册表，覆写模型里新配的实体不在它的清单上——
    /// 要同步它们就把实体也喂给 orm 级 Contract〈T〉。</summary>
    protected virtual void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <summary>按派生类型缓存的模型配置（每类只跑一次 OnModelCreating；null = 没覆写）。
    /// 配置本身与 provider 无关（Fluent 只写声明），所以缓存键只有上下文类型。</summary>
    static readonly ConcurrentDictionary<Type, ModelConfiguration?> ContextModels = [];

    /// <summary>本上下文（连同同类型的兄弟实例）那份模型注册表；没覆写钩子就是 null，
    /// 全链照旧走组装根的注册表——覆写是少数派，那颗注册表税由用得上的人付。</summary>
    internal Schema.IEntitySchemaRegistry? ModelSchemas { get; private set; }

    bool _modelEnsured;

    /// <summary>把模型钩子跑出来、备好会话注册表（幂等，首访那一次付钱）。
    /// 交回的注册表给 ChangeTracker 与 DbSet 的根引擎穿线；null = 没覆写。</summary>
    [RequiresUnreferencedCode("按 Type 反射判覆写并构建会话注册表（G2）。")]
    [RequiresDynamicCode("会话注册表编译读写委托（G2）。")]
    Schema.IEntitySchemaRegistry? EnsureModel()
    {
        // 覆写判定只问方法表（不摸实例）：找到的那颗 OnModelCreating 若还声明在基类，就是没覆写。
        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = "只按固定签名找 OnModelCreating 一颗；G2 路径已整体带 RUC。")]
        static bool Overridden(Type contextType)
            => contextType.GetMethod("OnModelCreating", BindingFlags.Instance | BindingFlags.NonPublic,
                   [typeof(ModelBuilder)])!.DeclaringType != typeof(DbContext);


        if (_modelEnsured)
            return ModelSchemas;

        _modelEnsured = true;

        var config = ContextModels.GetOrAdd(GetType(), _ =>
        {
            // 派生类没覆写就缓存 null（占住键）：覆写判定与钩子本身都只在首访这一次发生。
            // 极端竞态下钩子可能跑两遍（GetOrAdd 工厂的重入语义），配置是纯声明、哪份赢都同形。
            if (!Overridden(GetType()))
                return null;

            var builder = new ModelBuilder();

            OnModelCreating(builder);

            return builder.Configuration;
        });

        if (config is null)
            return null;

        var schemas = new EntitySchemaRegistry(new ContractResolver(Orm.Provider, config,
            defaultStringLength: Orm.DefaultStringLength));

        // mb 点过名的实体预登记：结构同步与 Contract(type) 都按"摸过才算在册"记账。
        foreach (var clrType in config.KnownEntityTypes)
            schemas.Contract(clrType);

        ModelSchemas = schemas;

        return schemas;
    }

    /// <summary>某一类实体的会话入口。同一个类型在同一会话里是同一只对象，调两次共享的确实是同一份身份映射，
    /// 而不是"共享但每次重新装一遍"。这一格上的 RUC/RDC 是读路径的入口税（按 Type 向注册表取契约）；
    /// 写侧那几个便利方法没有这两个标注，因为 Set〈T〉() 已经把契约取好了。</summary>
    [RequiresUnreferencedCode("按 Type 向注册表取实体契约（G2）。")]
    [RequiresDynamicCode("G2 注册表编译读写委托。")]
    public DbSet<T> Set<T>() where T : class
        => (DbSet<T>) _sets.GetOrAdd(typeof(T), _ =>
        {
            // 注册会话连接工厂：只挂一只 delegate，不建连接，连接在首条语句执行时由 OpenAsync 调工厂懒建。
            // 竞态下工厂可能跑多遍（字段注释），多建的那只被丢弃——两只 DbSet 挂的是同一颗闸、同一本跟踪器。
            EnsureSessionFactory();

            return new DbSet<T>(this);
        });

    // ── 写侧便利方法 ────────────────────────────────────────

    /// <summary>标为新增。自增整数主键由会话先发一个负的临时主键：同一批里的依赖行因此有个值可以捏着，
    /// 真实的键由库在 SaveChanges 那一句里给回来、并在依赖行建语句之前把临时值换掉——临时值任何时候都不会进库。
    /// 客户端生成的键（Guid 那一派）要自己填：那一档不代填，空着是填错了而不是"还没生成"。</summary>
    public T Add<T>(T entity) where T : class
    {
        Tracker.For<T>().Add(entity);

        return entity;
    }

    /// <summary>整批标为新增。逐只走 Add，"同键两只实例"在冲突的那一只上抛，之前那几只已经进了会话
    /// ——抛出时批次是半成品：没写库，可也没把已经收进去的那几只退回去。</summary>
    public void AddRange<T>(IEnumerable<T> entities) where T : class
    {
        var tracker = Tracker.For<T>();

        foreach (var entity in entities)
            tracker.Add(entity);
    }

    /// <summary>承认为「库里已有、当前值就是库值」，之后逐字段比对决定写不写。</summary>
    public T Attach<T>(T entity) where T : class
    {
        Tracker.For<T>().Attach(entity);

        return entity;
    }

    /// <summary>纳入会话并且每列都算脏——db.Attach(order); db.Entry(order).State = Modified 的合成形态。</summary>
    public T Update<T>(T entity) where T : class
    {
        Tracker.For<T>().Update(entity);

        return entity;
    }

    /// <summary>标为删除。发出去的是 DELETE 还是置位的 UPDATE，由这张表上有没有注册 orm.Behaviors.SoftDelete 决定
    /// （会话这一档与两条链式出口同一册子、同一条规则，"这张表是不是软删表"不因入口不同而给出两种答案）。
    /// 被标过的实例仍留在会话里，直到那一句 DELETE 真的执行完——否则 SaveChanges 之后就没有任何东西能生成那条语句了。</summary>
    public T Remove<T>(T entity) where T : class
    {
        Tracker.For<T>().Remove(entity);

        return entity;
    }

    public void RemoveRange<T>(IEnumerable<T> entities) where T : class
    {
        var tracker = Tracker.For<T>();

        foreach (var entity in entities)
            tracker.Remove(entity);
    }

    /// <summary>某个实例的状态入口（Entry(o).State 与 Property(...).IsModified）。
    /// 它不顺手把实体纳入会话：没跟踪过的实例在这里抛——"查一个入口"与"我有一个新意图"是两件事。
    /// 派生实例要按基类取入口：派生实例共享基类那张表的契约（各占一档要等继承映射），
    /// 泛型参数推出的那一档根本不存在，"先 Set〈派生〉().Attach"成了一句错的建议。</summary>
    public EntityEntry<T> Entry<T>(T entity) where T : class
    {
        if (Tracker.OwnerOf(entity) is { } owner && owner.EntityType != typeof(T))
            throw new QuickSqlException($"这个实例是按 {owner.EntityType.Name} 纳入会话的，而 {typeof(T).Name} 那一档里没有它。" +
                $"派生实例目前共享基类那张表的契约（各占一档要等继承映射，§8.2、P2/P3），所以入口也要按基类取：Set<{owner.EntityType.Name}>().Entry(...)。");

        return Tracker.For<T>().Entry(entity);
    }

    /// <summary>绕过会话，直接走 orm.Insert 那条链。存在的理由是"我不想让这一行进身份映射"
    /// （导数、修补数据、写日志表），而它确实会改库里的行——会话里已经读过同一行的话，写完它不知道，
    /// 快照就此是旧的。要么别混用，要么写完 Clear()。</summary>
    public IInsert<T> InsertRaw<T>(T entity) where T : class => Orm.Insert(entity);

    // ── SaveChanges ───────────────────────────────────

    /// <summary>把会话里的意图写出去。同步出口是 sync-over-async（内核没有第二套同步执行路径），
    /// 异步可用时优先 SaveChangesAsync。</summary>
    public SaveChangesReport SaveChanges() => SaveChangesAsync(default).GetAwaiter().GetResult();

    /// <summary>一次 SaveChanges = 一条事务里的「侦测 → Insert 档 → Update 档 → Delete 档 → 结算」。
    /// 前提是 provider 声明了 Transactions 位：没有那一位的库上这一串走逐句 autocommit——多条语句不原子，
    /// 中途失败已发的不回滚（行为诚实条款全文在位注释）。没有任何一行欠着语句时，这里一条语句都不发、事务也不开
    /// （交一份空报告）。事务归属：当前执行流上已经有本 orm 的环境事务时，这一批就属于它——不另开一条，
    /// 也不提交它（提交归开它的那个人）；没有时才自己开一条、成功即提交。判据用 QuickSqlTransaction.For
    /// 而不是"有没有环境事务"：把语句塞进另一家库的事务里不报错，只是数据少了一半。
    /// 这一口开成虚的：宿主侧要在这颗收口上先做自己的概念（审计列、变更事件、软删整行、并发戳），
    /// 派生类覆写即整段替换，同步壳经虚分派走同一条路。</summary>
    public virtual async Task<SaveChangesReport> SaveChangesAsync(CancellationToken ct = default)
    {
        Gate.Enter();

        try
        {
            return await SaveChangesCore(ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>SaveChangesAsync 的本体：从"有没有写的账"到事务提交与兜底回滚全在这一段里，闸门罩住的是它。
    /// 重载的派生会话若不回调基类，就不进这颗闸——闸只拦"同一条基类路径上的第二次进场"。</summary>
    async Task<SaveChangesReport> SaveChangesCore(CancellationToken ct)
    {
        if (!Tracker.WritesAreDue())
            return new SaveChangesReport([], []);

        // 先判"这一批排不排得出写入先后"，再碰驱动：成环是元数据的事，抢在 BeginTransaction 前判
        // 才不会被"连不上库"盖住成因；注定失败的保存不该在库里留一个开过又回滚的事务。
        Tracker.EnsureWriteOrderResolvable();

        // 第二道元数据闸门：分表实体的写要走"按行的分片键值定表"，跟踪器给不出那句（理由见 EnsureNoSplitWrites）；
        // 同样放在开事务之前。
        Tracker.EnsureNoSplitWrites();

        // 第三道元数据闸门：这一批只能落一座集群，名字就是下面那场事务要钉的那一格；同前两道放在开事务之前。
        var cluster = Tracker.WriteCluster();

        // 确保会话连接工厂在位：SaveChanges 的事务会复用它，而不是另取一条。
        EnsureSessionFactory();

        if (QuickSqlTransaction.For(Orm.Ado) is not null)
            return await Tracker.FlushDueAsync(ct).ConfigureAwait(false);

        // 事务可选那道口：provider 声明了 Transactions 位才自开一条事务交收；没有这一位时走 autocommit 直发，
        // 一批里多条语句不原子，中途失败已发的不回滚（行为诚实条款全文在位注释与用法册）。
        if (!TransactionCapability.Declared(Orm.Provider))
            return await Tracker.FlushDueAsync(ct).ConfigureAwait(false);

        using var tran = Orm.BeginTransaction(cluster ?? DataSourceRouting.DefaultCluster);

        try
        {
            var report = await Tracker.FlushDueAsync(ct).ConfigureAwait(false);
            tran.Commit();

            return report;
        }
        catch
        {
            // 兜底回滚在 catch 里就位：using 收口处的回滚可能抛（锁冲突、连接断了），从那里冒出来
            // 会把 SQL 异常整个换掉——所以回滚走静默口（失败丢弃），原始异常照原样向外（裸 throw 连堆栈都保真），
            // 收口失败永远排在业务异常之后。RollbackQuiet 只住具体类上（接口是公开面）。
            ((QuickSqlTransaction)tran).RollbackQuiet();

            throw;
        }
    }

    // ── 会话级连接复用 ───────────────────────────────────────

    /// <summary>注册连接工厂——只挂一只 delegate，不建连接，连接在首条语句执行时由 OpenAsync 按 (集群, 角色)
    /// 调工厂懒建。嵌套即共享：槽位已被同一条流上的另一只 DbContext 占着时当场不动字，先到的那只是主人。
    /// 没有这一格的两种翻车：里层先 Set 就把外层的工厂顶掉；里层先 Dispose 又把整条流的工厂清空——
    /// 外层之后每一句悄悄退回"每句取还池"，会话复用与写后读无声失效。</summary>
    internal void EnsureSessionFactory()
        => AdoExecutor.InstallSessionFactory(this, CreateSessionConnection);

    /// <summary>连接工厂本体：某一格第一次要用连接时调它，创建、打开、绑定一步到位，
    /// 同一格之后的语句走已绑定的那一条。会话因此可以持有两条连接（读写分离之后"会话的那一条连接"
    /// 不成立了，成立的是"会话对每一格只有一条"）。从库轮询落在会话上——会话钉住一颗而不是每句换一颗，
    /// 会话内两次读要能在同一份副本上看对账。</summary>
    DbConnection CreateSessionConnection(string cluster, ConnectionRole role)
    {
        var connectors = Orm.Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Orm.Provider.Kind.ToString().ToLowerInvariant(), failure);

        var connection = connectors.CreateConnection(Orm.Sources.ConnectionStringFor(cluster, role));
        connection.Open();

        _sessionConnections.Add(connection);
        AdoExecutor.BindSessionConnection(cluster, role, connection);

        return connection;
    }

    /// <summary>归还会话连接：AsyncLocal 的格子先同步注销，再逐条关连接还池。工厂槽位只有主人能清——
    /// 嵌套时先到的是主人，里层先结束不能把外层还在用的工厂与连接一起带走，各自关的只是自己账本上那些连接。
    /// 这个方法本身不能是 async：AsyncLocal 的值只有同步被调方写得回调用方的帧，async 方法里的赋值只活在
    /// 它自己的副本上——加了 async 的后果是调用方那条流从此一直留着"写过"的戳子，读写分离静默失效且绿得看不出来。
    /// 虚口与 SaveChangesAsync 同一条理由：宿主侧的卸载要在收尾时做自己的事，覆写即接管，
    /// 基类的连接归还经 base 调用仍然发生。</summary>
    public virtual ValueTask DisposeAsync()
    {
        AdoExecutor.ClearSessionFactory(this);
        _flowWrites?.Dispose();

        return CloseSessionConnectionsAsync();
    }

    async ValueTask CloseSessionConnectionsAsync()
    {
        foreach (var connection in _sessionConnections)
        {
            AdoExecutor.UnbindSessionConnection(connection);
            await ConnectionRelease.ReleaseAsync(connection).ConfigureAwait(false);
        }

        _sessionConnections.Clear();

        GC.SuppressFinalize(this);
    }

    /// <summary>同步壳，给不用 await using 的调用方。DI 容器的 scoped 释放通常走 DisposeAsync。</summary>
    public virtual void Dispose()
    {
        AdoExecutor.ClearSessionFactory(this);

        foreach (var connection in _sessionConnections)
        {
            AdoExecutor.UnbindSessionConnection(connection);
            ConnectionRelease.Release(connection);
        }

        _sessionConnections.Clear();
        _flowWrites?.Dispose();

        GC.SuppressFinalize(this);
    }
}
