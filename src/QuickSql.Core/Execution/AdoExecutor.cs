using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Transactions;

namespace QuickSql.Execution;

/// <summary>一次执行的标签：操作名 + 涉及的表 + 这条语句要落在哪座集群、链上有没有点名主/从，
/// 只喂观察类钩子、日志与路由。由查询对象算一次并随对象复用，不在每行上重算。
/// Cluster 缺省取 DataSourceRouting.DefaultCluster；Choice 为 ChainChoice.Auto 才是交给路由判。</summary>
public sealed record ExecutionTag(string Operation, IReadOnlyList<string> Tables,
    string Cluster = DataSourceRouting.DefaultCluster, ChainChoice Choice = ChainChoice.Auto);

/// <summary>执行层：连接取还、参数落地、超时、观察类钩子。只接 DialectSql，既不认识查询模型也不生成文本，
/// 「ToSql() 不连库」在这里是结构性的，不靠约定。</summary>
public sealed partial class AdoExecutor
{
    readonly ConnectionSources _sources;

    // ── 会话级连接复用 ────────────────────────────────────────
    // DbContext 在 Set<T>() 时注册一只工厂（只是 delegate 引用，不建连接），
    // 真正需要连接时调工厂取一条，之后同一会话的语句按 (集群, 角色) 各复用一条。
    // 槽位里放可变对象而不是连接本身：AsyncLocal 的值只有同步被调方写得回调用方的帧，
    // 所以靠"引用不变、内容在长"的对象把父帧与子帧粘住。

    static readonly AsyncLocal<SessionConnections?> _sessionConnections = new();

    /// <summary>会话工厂槽位：存的是 (工厂, 主人) 而不是裸 delegate。同一条流上嵌套两只 DbContext 时先到的是主人，
    /// 后到的只在位不改字，只有主人能清槽。否则里层先 Dispose 会把整条流的工厂清掉，外层每一句悄悄退回
    /// "每句取还池"，会话复用与写后读的保证无声失效。</summary>
    static readonly AsyncLocal<SessionFactorySlot?> _sessionFactory = new();

    /// <summary>"同一条流上这座集群写过"的戳子。写它的必须是同步帧（InstallFlowWrites），
    /// 异步的写语句只改那个对象、不换槽位。</summary>
    static readonly AsyncLocal<FlowWrites?> _flowWrites = new();

    /// <summary>注册会话工厂：槽位空着就挂上并成为主人；已被占用就当场不动字——嵌套即共享，
    /// 之后的连接照旧经主人的工厂落账，里层先结束也拆不了主人的槽。</summary>
    internal static void InstallSessionFactory(object owner, Func<string, ConnectionRole, DbConnection> factory)
        => _sessionFactory.Value ??= new SessionFactorySlot(owner, factory);

    /// <summary>拆会话工厂，只有主人能拆：非主人的 Dispose 里这一格不动，
    /// 否则里层先释放就把外层还在用的工厂整条拔掉。</summary>
    internal static void ClearSessionFactory(object owner)
    {
        if (_sessionFactory.Value is { } slot && ReferenceEquals(slot.Owner, owner))
            _sessionFactory.Value = null;
    }

    internal static void BindSessionConnection(string cluster, ConnectionRole role, DbConnection connection)
        => (_sessionConnections.Value ??= new SessionConnections()).Set(cluster, role, connection);

    internal static void UnbindSessionConnection(DbConnection connection) => _sessionConnections.Value?.Remove(connection);

    /// <summary>钉在这只 executor 上的那场事务（会话事务）。AsyncLocal 环境槽位跟执行流走、异步被调方写不回
    /// 调用方的帧，装配层在自己的帧里开事务再回到仓储帧时槽位是空的，于是 QuickSqlTransaction.For
    /// 在帧里找不到环境事务时回退读这里。槽位挂在实例上而不是类型上，两只 orm 共用一条流时各钉各的。</summary>
    internal QuickSqlTransaction? SessionTransaction { get; private set; }

    /// <summary>把根事务钉到本实例上。 唯一会调它的地方，
    /// 所以「谁钉的」与「钉了几场」都只有一条路。</summary>
    internal void BindSessionTransaction(QuickSqlTransaction transaction)
    {
        SessionTransaction = transaction;
        transaction.SessionScoped = true;
    }

    /// <summary>摘钩。只摘自己那一场：RequiresNew 那一族里内层结束时外层仍在位，
    /// 按引用比一次才不会把别人钉的那场抹掉。</summary>
    internal void UnbindSessionTransaction(QuickSqlTransaction transaction)
    {
        if (ReferenceEquals(SessionTransaction, transaction))
            SessionTransaction = null;
    }

    /// <summary>在调用方的同步帧里挂上这流的写后读戳子，返回一个"关掉它"的把手（会话释放用）。
    /// 已经挂着就复用那一枚 —— 两只 DbContext 嵌套时共用同一份"写过哪些集群"的账，
    /// 因为要防的那件事（读完自己刚写的旧值）跨会话也一样成立。</summary>
    internal static IDisposable InstallFlowWrites()
    {
        var ownsSlot = _flowWrites.Value is null;

        if (ownsSlot)
            _flowWrites.Value = new FlowWrites();

        return new FlowWritesHandle(ownsSlot);
    }

    /// <summary>本条流上这座集群是否写过。戳子不在位即 false（链式侧不显式开帧时就是这个默认，
    /// 于是那条路上"写后读"不生效，而它生效的地方是会话与显式开了帧的流）。</summary>
    internal static bool FlowWroteCluster(string cluster) => _flowWrites.Value?.Contains(cluster) == true;

    internal static void MarkFlowWrite(string cluster) => _flowWrites.Value?.Mark(cluster);

    sealed class FlowWritesHandle(bool ownsSlot) : IDisposable
    {
        public void Dispose()
        {
            if (ownsSlot)
                _flowWrites.Value = null;
        }
    }

    public AdoExecutor(IDbProvider provider, ConnectionSources sources, QuickSqlAop aop, int commandTimeoutSeconds, Func<string, Metadata.EntityContract?>? contractByTable = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(aop);

        Provider = provider;
        _sources = sources;
        Aop = aop;
        CommandTimeoutSeconds = commandTimeoutSeconds;
        _contractByTable = contractByTable;
    }

    /// <summary>表名 → 实体契约的查表口（组装根注入）。只在完整性违例收口那一格被读：拿库给回的约束名回契约
    /// 查唯一索引的列集合。null（测试壳或没接注册表的装配）= 列回查整格关闭，DbUpdateException.Columns 恒 null。</summary>
    readonly Func<string, Metadata.EntityContract?>? _contractByTable;

    public IDbProvider Provider { get; }

    public QuickSqlAop Aop { get; }

    public int CommandTimeoutSeconds { get; }

    /// <summary>预容量的天花板（行）。"取全部"那一档若按请求行数开数组，会在读第一行之前就把进程按死。</summary>
    const int MaxPreallocatedRows = 8192;

    /// <summary>expectedRows 取自模型的 Take(n)，null 表示行数未知。预容量按上限收着开：
    /// Take(n) 说的是"最多读 n 行"，不是"准备 n 行的数组"，照抄请求行数会在"取全部"档上直接 OOM。</summary>
    public async Task<List<T>> QueryAsync<T>(DialectSql sql, Func<DbDataReader, T> map, ExecutionTag tag, int? expectedRows, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, ct).ConfigureAwait(false);

        try
        {
            // reader 的获取在 try 之内：驱动在 ExecuteReader 当场抛（SELECT 语法错、连接断）也进 Failed，
            // 与消费段同一口出——否则观察钩子与完整性分类对"发送阶段"的失败集体失明。
            await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var list = new List<T>(expectedRows is > 0 and var wanted ? Math.Min(wanted, MaxPreallocatedRows) : 0);

            if (Aop.Materialized is { } materialized)
            {
                for (var ordinal = 0; await reader.ReadAsync(ct).ConfigureAwait(false); ordinal++)
                {
                    var value = map(reader);
                    list.Add(value);

                    if (value is not null)
                        materialized(new MaterializedEvent(value, typeof(T).Name, ordinal));
                }
            }
            else
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    list.Add(map(reader));
            }

            Succeeded(sql, tag, started, list.Count, session.Route);
            return list;
        }
        catch (Exception ex)
        {
            throw Rethrow(ex, Failed(ex, sql, started, tag));
        }
    }

    public async Task<T?> FirstAsync<T>(DialectSql sql, Func<DbDataReader, T> map, ExecutionTag tag, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, ct).ConfigureAwait(false);

        try
        {
            // 同 QueryAsync：reader 获取在 try 之内（发送阶段失败也进 Failed）。
            await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Succeeded(sql, tag, started, 0, session.Route);
                return default;
            }

            var value = map(reader);
            Succeeded(sql, tag, started, 1, session.Route);

            return value;
        }
        catch (Exception ex)
        {
            throw Rethrow(ex, Failed(ex, sql, started, tag));
        }
    }

    /// <summary>流式读取：连接活在整个枚举期间，中途 break 由 DisposeAsync 归还。失败也要报失败——
    /// 取数与映射包在内层 try（yield 不许进带 catch 的 try，会抛的工作都挪到 yield return 之前），
    /// 失败走分类与 Error 播报；正常走完记全量行，中途 break 由 finally 记实际行数。</summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(DialectSql sql, Func<DbDataReader, T> map, ExecutionTag tag, [EnumeratorCancellation] CancellationToken ct = default)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();
        var rows = 0;
        var failed = false;

        await using var session = await OpenAsync(sql, tag, ct).ConfigureAwait(false);

        DbDataReader readerSource;

        try
        {
            readerSource = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            Failed(ex, sql, started, tag);
            throw;
        }

        await using var reader = readerSource;

        try
        {
            while (true)
            {
                T current;

                try
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                        break;

                    current = map(reader);
                }
                catch (Exception ex)
                {
                    failed = true;
                    Failed(ex, sql, started, tag);
                    throw;
                }

                rows++;
                yield return current;
            }
        }
        finally
        {
            // 正常走完记全量行；提前 break 不算错误，但行数必须是"实际读到的行数"，不能报成完整结果集；
            // 失败那一格在 Failed 里报过，这里不重复。
            if (!failed)
                Succeeded(sql, tag, started, rows, session.Route);
        }
    }

    // ── 写出口 ──────────

    /// <summary>非查询出口，返回驱动报回的受影响行数。多值 INSERT 时这个数就是行数。</summary>
    public async Task<int> NonQueryAsync(DialectSql sql, ExecutionTag tag, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, RouteIntent.Write, ct).ConfigureAwait(false);

        try
        {
            var rows = await session.Command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // 写过就记账：这条流上这座集群之后的读一律落主库（复制延迟测不到，所以这一档只肯保守）。
            MarkFlowWrite(session.Route.Cluster);

            Succeeded(sql, tag, started, rows, session.Route);
            return rows;
        }
        catch (Exception ex)
        {
            throw Rethrow(ex, Failed(ex, sql, started, tag));
        }
    }

    /// <summary>带结果集的写出口：一条 OUTPUT/RETURNING 语句执行完就地读回它产出的行，consumeRow 在第 i 行上
    /// 被调一次（i 从 0 起，就是回填时要对上的源行序）。返回值按回包行数计而不是驱动的行数。
    /// 多语句批（语句后回读）时 MySQL 血统驱动交回的第一个结果是 INSERT 自己那颗 0 列结果，键在下一个结果集，
    /// 所以要先跳到第一个带列的结果集。</summary>
    public async Task<int> WriteWithResultAsync(DialectSql sql, ExecutionTag tag, Action<DbDataReader, int> consumeRow, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, RouteIntent.Write, ct).ConfigureAwait(false);

        try
        {
            // reader 的获取在 try 之内：完整性违例（FK/唯一键）恰恰是在 ExecuteReader 当场抛的 ——
            // 这是 OUTPUT/RETURNING 写路径的常态，挪进来之后分类漏斗才真正盖住这条最高频的写出口。
            await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // 跳到第一个"带列"的结果集（见方法注释）。空回包在这里同样是"没有下一结果集"，
            // 落回 0 行由调用方的 0/N 守卫接住，不在此处另立判据。
            while (reader.FieldCount == 0 && await reader.NextResultAsync(ct).ConfigureAwait(false))
            { }

            var rows = 0;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                consumeRow(reader, rows);
                rows++;
            }

            MarkFlowWrite(session.Route.Cluster);

            Succeeded(sql, tag, started, rows, session.Route);
            return rows;
        }
        catch (Exception ex)
        {
            throw Rethrow(ex, Failed(ex, sql, started, tag));
        }
    }

    // ── 装载通道 ──────────────────────────────────────

    /// <summary>批量装载通道：不发命令，只把一条已经开好的连接借出去（连同它上面可能活着的事务与超时），
    /// 由 provider 的 IBulkCopyChannel 自家高速入口接管那一批行。四支连接来路是执行层私有知识，
    /// provider 伸手去拿会在环境事务在位时拿到旁支连接，这一层必须存在。
    /// 观察钩子收到的是一句合成描述符（"BULK COPY INTO 表"、参数表为空——装载通道没有参数），
    /// BeforeExecute 照常过 CommandTerminating 闸门，新增写出口不得绕开它。</summary>
    [RequiresUnreferencedCode("转调装载通道：驱动自家的批量类不在 DbProviderFactory 的抽象面上，只能按类型名在驱动程序集里找。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public async Task<int> CopyAsync(IBulkCopyChannel channel, BulkCopyRequest request, ExecutionTag tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = new DialectSql($"BULK COPY INTO {request.DestinationTable}", []);

        BeforeExecute(descriptor, tag);

        var started = Stopwatch.GetTimestamp();

        await using var rented = await RentAsync(tag, RouteIntent.Write, ct).ConfigureAwait(false);

        try
        {
            var rows = await channel.WriteAsync(
                new BulkConnection(rented.Connection, rented.Transaction, CommandTimeoutSeconds), request, ct)
                .ConfigureAwait(false);

            // 装载通道是一片/一批一次递交，但它同样是写：账要记，否则这座集群之后的读会落到还没装完的从库上。
            MarkFlowWrite(rented.Route.Cluster);

            Succeeded(descriptor, tag, started, rows, rented.Route);
            return rows;
        }
        catch (Exception ex)
        {
            throw Rethrow(ex, Failed(ex, descriptor, started, tag));
        }
    }

    // ── 事务 ────────────────────────────────────────────

    /// <summary>开一条真事务并就地写进环境槽位。整个方法必须同步：ADO.NET 的 begin/commit/rollback 没有异步重载，
    /// 且 AsyncLocal 只能由同步被调方写回调用方的帧；连接被这条事务独占到结束。
    /// 嵌套 = SAVEPOINT：当前帧已有环境事务时在外层事务上建保存点并返回保存点事务（Commit 释放、Rollback 回退到保存点，
    /// 外层不受影响）；保存点由驱动发还是由我们发文本，由方言 SupportsDriverSavepoints 决定，在此定一次并记账。
    /// propagation 为 RequiresNew 时挂起当前环境事务、新开独立事务，结束后恢复。</summary>
    public QuickSqlTransaction BeginTransaction(IsolationLevel level, TransactionPropagation propagation = TransactionPropagation.Requires)
        => BeginTransaction(DataSourceRouting.DefaultCluster, level, propagation);

    /// <summary>钉住一座集群开事务：这只事务里的每一条语句都要落在同一座集群的主库那条连接上，
    /// 别座集群的语句当场拒（判据与 RentAsync 的 ambient 支共用一条）。跨集群的一场事务
    /// （两阶段、XA）规格未裁，不给它一个能表达出来的形状。</summary>
    public QuickSqlTransaction BeginTransaction(string cluster, IsolationLevel level, TransactionPropagation propagation = TransactionPropagation.Requires)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        // 事务可选那道口：provider 没声明 Transactions 位就当场抛（NotSupportedException 带明话）。
        // 严禁静默假事务：没有位的 provider 上"开一条事务再照常发语句"会把 autocommit 引擎
        // （ClickHouse）读成"每句各自递交"，一次提交的承诺一句都对不上。
        // 写路径不经过这一格：SaveChanges / InsertOrUpdate / DeleteRange 三处先判位再定包不包；
        // 本格先抛之后保存点那一支在这家不可达，不另设铁闸。
        if (!TransactionCapability.Declared(Provider))
            throw new NotSupportedException(
                $"Provider {GetType().Name} 不支持事务（{nameof(DbCapabilities.Transactions)} 位未声明）。" +
                "这一家的事务要么驱动不理解、要么服务端默认不开（如 ClickHouse 的多语句事务是实验档且驱动 BeginDbTransaction 本地抛）：" +
                "不要在这里开事务——内核写路径已经按这一位改走逐句 autocommit 直发；要一次整批落库，请在库侧用单条语句表达（§6）。");

        // 帧里没有环境事务时回退读本实例钉着的那场：不回退的话，装配层在别的帧开的会话事务还在位而这里
        // 看不见，就会在新连接上开出第二场互不相干的独立事务——内层 Commit 即落库，外层 Rollback 撤不掉。
        var ambient = QuickSqlTransaction.Ambient ?? SessionTransaction;

        // Requires：外层事务在位时，创建保存点而不是新开事务。
        // 隔离级别在保存点路径上被忽略（SAVEPOINT 继承外层事务的隔离级别，这是 SQL 标准行为）。
        if (propagation == TransactionPropagation.Requires && ambient is not null)
        {
            if (!ReferenceEquals(ambient.Executor, this))
                throw new QuickSqlException(
                    $"当前执行流上已经有一条属于另一个 orm 实例的环境事务：不能把{Provider.Kind}的语句塞进另一家库的事务里。" +
                    "如果要同时操作两个库，请在不同的执行流（不同的 async 方法或 Task.Run）里分别开事务。");

            if (!string.Equals(ambient.Cluster, cluster, StringComparison.Ordinal))
                throw new QuickSqlException($"当前环境事务钉在集群 {ambient.Cluster} 上，又要为集群 {cluster} 开事务：同一座集群里嵌套才是保存点，跨集群的嵌套不是一场事务。请按集群分别开事务、各自提交。");

            // 计数器解析到根再用：保存点事务各持自己的 _nextSavepointId，二级嵌套就地递增会发一个与父同名的
            // 保存点——MySQL 血统上同名删旧的，中层 Commit 的 RELEASE 撤的是最里层，外层保存点从此不存在。
            var root = ambient;

            while (root._parent is { } parent)
                root = parent;

            // 隔离级别门禁：SAVEPOINT 没有自己的隔离级别（继承外层是 SQL 标准行为），传进来的 level 落不了地，
            // 传了与根不同的级别当场拒；要别的级别走 RequiresNew。Unspecified = 调用方没有表态，放行。
            if (level != IsolationLevel.Unspecified && level != root.IsolationLevel)
                throw new QuickSqlException(
                    $"嵌套事务传了隔离级别 {level}，但保存点继承外层的 {root.IsolationLevel}（SAVEPOINT 没有自己的隔离级别，这是 SQL 标准行为），" +
                    "传入的级别在这条路上不会生效。要别的隔离级别请改用 TransactionPropagation.RequiresNew：" +
                    "挂起外层、按传入级别新开一条独立事务，两边互不影响。");

            var savepointName = $"quicksql_sp_{root._nextSavepointId++}";

            // 两条路做的是同一件事，差别只在"这句话由驱动发还是由我们发"。走哪条路在这里定一次并带进
            // 保存点事务——创建它的那条路决定拆除它的那条路，不许跨路拆卸。
            var byDriver = Provider.Dialect.SupportsDriverSavepoints;

            if (byDriver)
                ambient.DbTransaction.Save(savepointName);
            else
                ExecuteSavepoint(ambient, SavepointStep.Create, savepointName);

            var nested = new QuickSqlTransaction(ambient, savepointName, textSavepoints: !byDriver);

            nested.Install();

            return nested;
        }

        // RequiresNew：挂起当前环境事务（如果有），新开一条独立事务。挂起不提前生效：
        // 这里只把引用接住，槽位的清除交给 Install 的覆写——若提前清掉，新事务一旦开不出来
        // （驱动加载失败、连接开不出、BeginTransaction 抛），被挂起的那条就永远回不到槽位。
        QuickSqlTransaction? suspended = null;

        if (ambient is not null)
            suspended = ambient;

        var connectors = Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Provider.Kind.ToString().ToLowerInvariant(), failure);

        // 会话级连接复用：DbContext 在 (这座集群, 主库) 那一格已有连接时直接用它开事务，事务结束后连接不归池。
        // 事务钉的是主库那一格，从库那一格不参与：写不进从库，而"事务里的读"必须看见自己的写。
        DbConnection? sessionConn = null;
        var sessions = _sessionConnections.Value;

        if (sessions is not null && sessions.TryGet(cluster, ConnectionRole.Primary, out var bound))
            sessionConn = bound;
        else if (_sessionFactory.Value is { } slot)
        {
            sessionConn = slot.Factory(cluster, ConnectionRole.Primary);
            BindSessionConnection(cluster, ConnectionRole.Primary, sessionConn);
        }

        var ownsConnection = sessionConn is null;
        var connection = sessionConn ?? connectors.CreateConnection(_sources.PrimaryConnectionString(cluster));

        try
        {
            if (ownsConnection)
                connection.Open();

            var transaction = new QuickSqlTransaction(this, cluster, connection, connection.BeginTransaction(level), ownsConnection, suspended, level);

            transaction.Install();

            return transaction;
        }
        catch
        {
            if (ownsConnection)
                ConnectionRelease.Release(connection);
            throw;
        }
    }

    /// <summary>开一场事务并钉在本 executor 上——归属跟实例走，不跟执行流走。开它的那一帧与它的所有下游
    /// 都进这场事务，父帧与兄弟帧靠 SessionTransaction 那一格回来。已经钉过就当场拒——
    /// 同一实例上钉两场，「一次提交」那句话就又是假的。</summary>
    public QuickSqlTransaction BeginSessionTransaction(string cluster, IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (QuickSqlTransaction.For(this) is { } existing)
            throw new QuickSqlException(
                $"这只 orm 已经在事务里（集群 {existing.Cluster}），不能再钉一场会话事务：同一只实例上钉两场，"
                + "「一场工作单元一次提交」就不成立了。要先把手里那场收掉，或者按集群各开一只 orm。");

        var transaction = BeginTransaction(cluster, level, TransactionPropagation.Requires);

        // 根事务才钉实例；保存点事务（这一颗不会走到，因为上面已经拒了嵌套）共用外层那一条归属。
        BindSessionTransaction(transaction);

        return transaction;
    }

    /// <summary>在已经被这条事务占有的连接上发一句文本保存点（三口文本由方言给）。不走 OpenAsync 取还连接的路：
    /// 保存点必须落在外层事务那条连接与那个 DbTransaction 上。命令构造复用 Prepare（超时、事务绑定与业务语句同源），
    /// 但 AOP 钩子一个都不过——SAVEPOINT 不是改写钩子该看到的文本。必须同步且用 ExecuteNonQuery 而不是 reader：
    /// 后者会把进程撞在虚谷驱动 get_nextrow 的原生 0xC0000005 上（实测）。</summary>
    internal void ExecuteSavepoint(QuickSqlTransaction transaction, SavepointStep step, string savepointName)
    {
        var dialect = Provider.Dialect;

        // 容量：最长那句是 `ROLLBACK TO SAVEPOINT `（22 字符）加名字，三口共用这一份预容量。
        var writer = new SqlWriter(new StringBuilder(24 + savepointName.Length), [], dialect.ParameterPrefix);

        switch (step)
        {
            case SavepointStep.Create: dialect.EmitSavepoint(writer, savepointName); break;
            case SavepointStep.Rollback: dialect.EmitRollbackToSavepoint(writer, savepointName); break;
            case SavepointStep.Release: dialect.EmitReleaseSavepoint(writer, savepointName); break;
            default: throw new QuickSqlException($"不存在的保存点步骤：{step}（新增了 SavepointStep 的成员而没在这里给出对应的文本出口）");
        }

        using var command = Prepare(transaction.Connection, transaction.DbTransaction, new DialectSql(writer.ToSqlString(), writer.Parameters));

        command.ExecuteNonQuery();
    }

    ValueTask<Session> OpenAsync(DialectSql sql, ExecutionTag tag, CancellationToken ct) => OpenAsync(sql, tag, RouteIntent.Read, ct);

    async ValueTask<Session> OpenAsync(DialectSql sql, ExecutionTag tag, RouteIntent intent, CancellationToken ct)
    {
        var rented = await RentAsync(tag, intent, ct).ConfigureAwait(false);

        DbCommand command;

        try
        {
            command = Prepare(rented.Connection, rented.Transaction, sql);
        }
        catch
        {
            // 自有的连接还没装进 Session、await using 接不到手，Prepare 炸了就地还掉，否则泄漏一条；
            // 借用支的 DisposeAsync 是空操作，照调无害。
            await rented.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // 借来的那一条不在这里 dispose：所有权随 Session 走，命令与连接同一个借用周期。
        return new Session(rented, command);
    }

    /// <summary>连接的四支来路，按"谁已经占着它"的优先级排：环境事务 → 会话连接 → 会话工厂 → 新建并打开。
    /// 这是全仓库唯一决定"用哪条连接"的地方：读写分离与分库都长在这一颗上，集群与角色由 DataSourceRouting
    /// 判一次，结论随借用交给 RentedConnection.Route，观察钩子读得出"这句走了哪条"。环境事务那一支多一条硬闸：
    /// 事务钉着一座集群，要另一座集群的语句当场拒。返回值的连接归调用方 dispose（只在"这一支是我们自己新建的"
    /// 时才真的还）；OpenAsync 把所有权转给 Session，CopyAsync 自己 await using。</summary>
    async ValueTask<RentedConnection> RentAsync(ExecutionTag tag, RouteIntent intent, CancellationToken ct)
    {
        var cluster = tag.Cluster;
        var ambient = QuickSqlTransaction.For(this);
        var route = DataSourceRouting.Resolve(cluster, intent, tag.Choice, ambient is not null,
            FlowWroteCluster(cluster), _sources.ReplicaCount(cluster));

        // 环境事务在位时不取新连接：语句必须走那条被事务独占的连接，否则"自动 enlist"是假的。
        if (ambient is not null)
        {
            if (!string.Equals(ambient.Cluster, cluster, StringComparison.Ordinal))
                throw new QuickSqlException($"当前环境事务钉在集群 {ambient.Cluster} 上，而这条 {tag.Operation} 语句要落在集群 {cluster}：一场事务只有一条连接。要分两座库写就分别开事务、各自提交。");

            return new RentedConnection(ambient.Connection, ambient.DbTransaction, ownsConnection: false, route);
        }

        // 会话级连接复用：DbContext 绑的连接在位时直接走它，不再每句取还。
        // 键是 (集群, 角色)："同一会话里读到刚写的那一份"由写后读戳子保证（把会话内的读送回主库那条）。
        if (_sessionConnections.Value?.TryGet(cluster, route.Role, out var sessionConn) == true)
            return new RentedConnection(sessionConn, transaction: null, ownsConnection: false, route);

        // 工厂在位但这一格还没建——首次用到时调工厂取连接，之后同一会话复用那一条。
        // 从库因此是"会话钉住一颗"而不是"每句换一颗"：会话内的两次读才互相看得见对方的顺序。
        if (_sessionFactory.Value is { } factory)
        {
            var conn = factory.Factory(cluster, route.Role);
            BindSessionConnection(cluster, route.Role, conn);

            return new RentedConnection(conn, transaction: null, ownsConnection: false, route);
        }

        var connectors = Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Provider.Kind.ToString().ToLowerInvariant(), failure);

        var connection = connectors.CreateConnection(_sources.ConnectionStringFor(cluster, route.Role));

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await ConnectionRelease.ReleaseAsync(connection).ConfigureAwait(false);
            throw;
        }

        return new RentedConnection(connection, transaction: null, ownsConnection: true, route);
    }

    DbCommand Prepare(DbConnection connection, DbTransaction? transaction, DialectSql sql)
    {
        var command = Provider.Connectors.CreateCommand(connection);

        command.Transaction = transaction;
        command.CommandText = sql.Text;
        command.CommandTimeout = CommandTimeoutSeconds;

        foreach (var parameter in sql.Parameters)
            Bind(command, parameter);

        return command;
    }

    /// <summary>把 provider 已经算好的 AdoParameter 搬进驱动参数对象。DbType.Object 表示 binder 有意不表态，
    /// 硬设会让部分驱动连非 DBNull 的值也拒掉。名字在搬进去之前摘掉方言前缀——只对没有声明
    /// DriverAcceptsPrefixedParameterNames 的驱动成立；SqlWriter 造的是"文本占位符与驱动参数名同形"的两用名，
    /// 有的驱动只认不带前缀的名字。摘前缀只发生在跨进驱动对象那一步，文本与日志里的名字仍是带前缀的原样。</summary>
    void Bind(DbCommand command, AdoParameter parameter)
    {
        var ado = command.CreateParameter();

        var prefix = Provider.Dialect.ParameterPrefix;
        var name = parameter.Name;

        ado.ParameterName = parameter.DriverName
            ?? (Provider.Dialect.DriverAcceptsPrefixedParameterNames
                ? name
                : name.Length > 0 && name[0] == prefix ? name.Substring(1) : name);
        ado.Value = parameter.Value ?? DBNull.Value;

        if (parameter.DbType != DbType.Object)
            ado.DbType = parameter.DbType;

        if (parameter.Size > 0)
            ado.Size = parameter.Size;

        command.Parameters.Add(ado);
    }

    void Succeeded(DialectSql sql, ExecutionTag tag, long started, int rows, DataSourceRoute route)
    {
        var elapsed = Elapsed(started);

        if (Aop.Executed is { } executed)
            Aop.Observe(() => executed(new CommandLogEvent(sql, tag.Operation, tag.Tables, elapsed, rows, FirstRowSummary: null, route)));

        if (elapsed >= Aop.SlowQueryThresholdMilliseconds && Aop.SlowQuery is { } slow)
            Aop.Observe(() => slow(new SlowCommandEvent(sql, elapsed, Aop.SlowQueryThresholdMilliseconds)));
    }

    Exception Failed(Exception exception, DialectSql sql, long started, ExecutionTag? tag = null)
    {
        // 硬规则 3：钩子自己炸了不许盖掉业务异常，所以走 Observe 的吞-and-转投边界。
        if (Aop.Error is { } error)
            Aop.Observe(() => error(new CommandErrorEvent(exception, sql.Text, Elapsed(started))));

        // 分类排在播报之后，这个先后是这一格的全部裁决：挂在 Error 上的日志与告警要看见的是
        // 库的回答（驱动原话）；往上抛的那一颗才换成"哪一档完整性违例"，判不出就原样交回——不包装、不猜。
        return exception is DbException driverError
               && Provider.ClassifyIntegrityError(driverError) is { } violation
            ? IntegrityException(violation, driverError, sql.Text, ColumnsFor(violation, tag))
            : exception;
    }

    /// <summary>拿库给回的约束名回契约查唯一索引的列集合。查不到（库自起名、表不在册、名字对不上）就交 null，
    /// 不抛也不猜：宁可提示粗一点，也不把半句人话当列名摆上 UI。</summary>
    IReadOnlyList<string>? ColumnsFor(IntegrityViolation violation, ExecutionTag? tag)
    {
        if (violation.Kind is not IntegrityKind.Unique
            || violation.ConstraintName is not { Length: > 0 } name
            || _contractByTable is null
            || tag is null)
            return null;

        foreach (var table in tag.Tables)
        {
            if (_contractByTable(table) is not { } contract)
                continue;

            foreach (var index in contract.Indexes)
                if (index.Kind is IndexKind.Unique
                    && string.Equals(index.Name, name, StringComparison.OrdinalIgnoreCase))
                    return index.ColumnNames;
        }

        return null;
    }

    /// <summary>收口之后的重抛。判不出分类（Failed 原样交回）时用 ExceptionDispatchInfo 重抛——
    /// throw 原异常会把 StackTrace 重写到这一行，驱动侧的原始抛出点整体丢失；
    /// 包装了新异常的走 throw 表达式原样返回。六个出口的 catch 统一写 throw Rethrow(ex, Failed(...))。</summary>
    static Exception Rethrow(Exception original, Exception mapped)
    {
        if (!ReferenceEquals(mapped, original))
            return mapped;

        ExceptionDispatchInfo.Capture(original).Throw();

        throw new UnreachableException("ExceptionDispatchInfo.Throw 不会返回。");
    }

    /// <summary>按档选类型。不给 default 分支留活路：将来  添一档而这里没跟上，
    /// 要的是当场响，而不是悄悄落进某一颗现有异常里、三个月后在某个消费者的 catch 里现形。</summary>
    static DbUpdateException IntegrityException(IntegrityViolation violation, DbException error, string sql, IReadOnlyList<string>? columns)
    {
        string? constraint = violation.ConstraintName;
        string suffix = constraint is { Length: > 0 } ? $"约束 {constraint}。" : string.Empty;

        return violation.Kind switch
        {
            IntegrityKind.Unique => new UniqueConstraintException(
                $"唯一键冲突：库里已有另一行占着同一组键值。{suffix}", sql, constraint, error, columns),
            IntegrityKind.NotNull => new NotNullConstraintException(
                $"这一列不许为空，而语句给它写了空值。{suffix}", sql, constraint, error, columns),
            IntegrityKind.ForeignKey => new ForeignKeyConstraintException(
                $"外键对不上：引用不到那一行，或那一行还被别处引用着。{suffix}", sql, constraint, error, columns),
            IntegrityKind.Check => new CheckConstraintException(
                $"值不满足库上的检查约束。{suffix}", sql, constraint, error, columns),
            var kind => throw new ArgumentOutOfRangeException(
                nameof(violation), kind, $"{nameof(IntegrityKind)} 添了新一档而 {nameof(IntegrityException)} 没跟上"),
        };
    }

    static long Elapsed(long started) => Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>执行前的那道关口，两道按固定顺序走：先决策类闸门（可把这句叫停），后观察类播报。
    /// 顺序是这一格的全部裁决：被闸门取消的句子不该在日志里留下一行"正在执行"。
    /// 位置在计时起点之前，播报这一刻只有文本、操作名与表名，耗时、行数与路由在 Succeeded 那一发上。
    /// 六个出口都在这一道门上——新增写出口从旁边绕过去，就是同时绕过禁整表写的硬闸与执行日志。</summary>
    void BeforeExecute(DialectSql sql, ExecutionTag tag)
    {
        CheckNotCancelled(sql, tag);

        if (Aop.Executing is { } executing)
            Aop.Observe(() => executing(new CommandExecutingEvent(sql, tag.Operation, tag.Tables)));
    }

    /// <summary>决策类钩子的发射点：执行前最后一道闸门。未订阅时零成本，订阅后返回 true 即取消执行并抛
    /// QuickSqlCommandCancelledException。典型用法：拦没有 WHERE 的 UPDATE/DELETE（生产禁整表写硬闸）。</summary>
    void CheckNotCancelled(DialectSql sql, ExecutionTag tag)
    {
        if (Aop.CommandTerminating is not { } hook)
            return;

        var ev = new CommandTerminatingEvent(sql.Text, tag.Operation, tag.Tables, sql.Parameters);

        if (hook(ev))
            throw new QuickSqlCommandCancelledException(
                $"CommandTerminating 钩子取消了这条 {tag.Operation} 语句（涉及表：{string.Join("、", tag.Tables)}）。" +
                "如果要放行，请检查钩子的注册条件。");
    }

    /// <summary>一次借用：连接、事务与判出来的路由。主构造参数只能进属性初始化器、不能在方法体里再引用
    /// （CS9124），所以方法体读的是属性。</summary>
    sealed class RentedConnection(DbConnection connection, DbTransaction? transaction, bool ownsConnection, DataSourceRoute route) : IAsyncDisposable
    {
        public DbConnection Connection { get; } = connection;

        public DbTransaction? Transaction { get; } = transaction;

        /// <summary>这次借用是怎么决定用这条连接的（集群、角色、理由）。</summary>
        public DataSourceRoute Route { get; } = route;

        public async ValueTask DisposeAsync()
        {
            // 环境事务那条连接归事务，会话那条连接归会话，语句结束都不能把它还给池 ——
            // 连接上还有一个活着的事务（或还有后面的语句要用它），归还等于把别人的事务交给下一个借用者。
            if (ownsConnection)
                await ConnectionRelease.ReleaseAsync(Connection).ConfigureAwait(false);
        }
    }

    /// <summary>一次取还的会话。连接处置整个交给 rental，这里只负责命令——"谁自有的连接谁还"
    /// 只有 RentedConnection.DisposeAsync 一处。主构造参数不能既捕获又初始化（CS9124）。</summary>
    sealed class Session(RentedConnection rental, DbCommand command) : IAsyncDisposable
    {
        public DbCommand Command { get; } = command;

        /// <summary>这句落在哪座集群、哪个角色、为什么 —— 归借用它的那次取连接判出来，观察钩子读它。</summary>
        public DataSourceRoute Route => rental.Route;

        public async ValueTask DisposeAsync()
        {
            // 各自 try/finally：命令释放失败（驱动在 Dispose 里也抛过）不能连累连接归还，
            // 否则自有连接泄漏一格、借用连接少一次还点。
            try
            {
                await Command.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await rental.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>一次会话持有的连接：按 (集群, 角色) 一格一条。并发扇出的两条分支共享同一只账本，
    /// 同时首用同一格是对非线程安全字典的并发写（结构损坏级竞态），所以用 ConcurrentDictionary：
    /// 每句至多一读一写，无锁路径零额外成本。</summary>
    internal sealed class SessionConnections
    {
        readonly ConcurrentDictionary<(string, ConnectionRole), DbConnection> _bySlot = [];

        public bool TryGet(string cluster, ConnectionRole role, out DbConnection connection)
        {
            if (_bySlot.TryGetValue((cluster, role), out var found))
            {
                connection = found;
                return true;
            }

            connection = null!;
            return false;
        }

        public void Set(string cluster, ConnectionRole role, DbConnection connection) => _bySlot[(cluster, role)] = connection;

        /// <summary>按引用摘掉一格。会话释放时逐条还池，槽位里不留悬空的引用。</summary>
        public void Remove(DbConnection connection)
        {
            foreach (var slot in _bySlot.Where(kv => ReferenceEquals(kv.Value, connection)).Select(kv => kv.Key).ToArray())
                _bySlot.TryRemove(slot, out _);
        }

        public IReadOnlyList<DbConnection> Connections => [.. _bySlot.Values];
    }

    /// <summary>会话工厂槽位的本体：(工厂, 主人) 一对。主人在  里按引用比一次，
    /// 与  那颗"只摘自己那一场"的判法同一形状。</summary>
    sealed class SessionFactorySlot(object owner, Func<string, ConnectionRole, DbConnection> factory)
    {
        internal object Owner { get; } = owner;

        internal Func<string, ConnectionRole, DbConnection> Factory { get; } = factory;
    }

    /// <summary>"这条流上哪几座集群写过"的一本账。槽位里放这本账而不是布尔：异步被调方改不动父帧的槽位，
    /// 但父子帧共用同一枚引用，写侧在深处 Mark 一笔、外层的下一次读看得见。
    /// 账只增不减——一座集群被写过，这条流上它之后的读一律落主库（复制延迟测不到，只肯保守）。</summary>
    internal sealed class FlowWrites
    {
        readonly List<string> _clusters = [];

        public bool Contains(string cluster)
        {
            lock (_clusters)
                return _clusters.Contains(cluster, StringComparer.Ordinal);
        }

        public void Mark(string cluster)
        {
            lock (_clusters)
                if (!_clusters.Contains(cluster, StringComparer.Ordinal))
                    _clusters.Add(cluster);
        }
    }
}

/// <summary>把自己自有的连接交回那一步的唯一写法：先 Close，再 Dispose（同步或异步两支都一样）。
/// 这不是防御性冗余：虚谷驱动只有 Close() 把会话还回池，Dispose()/DisposeAsync() 不还——
/// 单用它们正好在第 1000 次 Open 时红（实测）。另四族的 Dispose 做的本来就是 Close 那件事，
/// 先 Close 再 Dispose 对它们是同义反复、不新增语义，所以这一句写给全部五族而不开一族特例。</summary>
internal static class ConnectionRelease
{
    public static void Release(DbConnection connection)
    {
        connection.Close();
        connection.Dispose();
    }

    public static async ValueTask ReleaseAsync(DbConnection connection)
    {
        connection.Close();

        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

