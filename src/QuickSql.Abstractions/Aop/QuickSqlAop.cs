using QuickSql.Metadata;

namespace QuickSql.Aop;

/// <summary>钩子分类学：类别决定能不能改 SQL、异常后果、以及能否在 Explain() 里回放。
/// P0 只有观察类全量加改写类的 AuditValue 与 ParameterBinding。</summary>
public enum HookCategory { Observe, Rewrite, Decide }

/// <summary>硬规则 2「未订阅即零成本」由这些 bool 兑现：先判订阅，再决定要不要构造事件对象。</summary>
public sealed class QuickSqlAop
{
    // ── 观察类：异常只进 Error，不中断执行（硬规则 3）
    /// <summary>执行前的播报位。发射点只有一处（AdoExecutor.BeforeExecute），位置在
    /// CommandTerminating 之后、命令交给驱动之前——顺序是有裁决的：被闸门取消的句子
    /// 不该留下一行"正在执行"，所以先判可否、再播报。
    /// 它不是 CommandLogEvent：那一颗记录的三格读数（耗时、行数、路由）在播报这一刻一个都还不存在——
    /// 句子还没跑，填 0 与假路由等于让订阅方每次都猜"这一份是读数还是占位"。
    /// 要耗时、行数与实际走了哪条连接，订 Executed；要"这一句即将发出去"（挂住、死锁时唯一还留得下现场
    /// 的那一位），订这一位。零成本规则照旧：未订阅时不构造事件对象。</summary>
    public Action<CommandExecutingEvent>? Executing;
    public Action<CommandLogEvent>? Executed;
    public Action<CommandErrorEvent>? Error;
    public Action<SlowCommandEvent>? SlowQuery;
    public Action<MaterializedEvent>? Materialized;
    /// <summary>观察类：这一轮有哪些实体被写出去。
    /// 发射点是会话的写阶段：三档（增/改/删）各一次、逐只实体派发，且在语句建出来之前——
    /// 订阅者可以观察（发领域事件、记审计流水），也可以改实体：那一格改出来的值会被紧接着建出来的
    /// 语句带出去（取值段在建语句之前，而取值段读的是实体）。这是它与 AuditValue 的分工：
    /// 那一位逐格改值，这一位逐只改实体。只覆盖会话那一条路：链式的 orm.Insert / UpdateRange /
    /// DeleteRange 不走跟踪器，那三条路上没有这一格（它们各有自己的值钩子 AuditValue 与整句钩子 CudBuilding）。
    /// 零成本规则照旧：未订阅时不构造事件对象。</summary>
    public Action<EntityWrittenEvent>? EntityWritten;
    /// <summary>改写类：软删那一档往上加的 SET 项（列 + 值）。
    /// 软删自己只写置位与时间两格，而"谁删的"那一格（操作人、删除原因）是调用方的领域事实、库里替不了
    /// （库不知道当前用户是谁、也不该知道）——这一颗让调用方把那一格往上加，库负责把它参数化、写出去，
    /// 并按与那两格同一把尺子验（自增列与并发列不收）。
    /// 逐行触发：有行才触发；链式 orm.Delete().Where() 那一档没有行可给（它删的是一批行，不是一只），
    /// 不触发这一颗——那一档要自己写那一格。零成本规则照旧：未订阅时不构造事件对象，
    /// SET 清单与原来逐字节同形。</summary>
    public Action<SoftDeleteValuesEvent>? SoftDeleteValues;

    /// <summary>批量降级的那一位（DegradeAndLog 的落地位）。它存在的理由是一格缺口：
    /// 原稿要求"每一次降级在三处留痕（日志 + OTel 标签 + BulkReport）"，而本仓库既没有日志器也没有 OTel，
    /// 三处里今天只有 BulkReport 是自带的产物。这一位就是它的落地形态：降级这件事从此可订阅，
    /// "批量悄悄变成逐条"不再只能靠人读 Reason。零成本规则照旧：未订阅时不构造事件对象。</summary>
    public Action<BulkDegradedEvent>? BulkDegraded;

    // ── 改写类：值级改写，按注册顺序串联
    /// <summary>逐格取值时问一次，两个发射点：插入链的取值段与整行更新链的取值段（orm.UpdateRange）。
    /// 两者靠 AuditValueEvent.OperationKind 分派，一条订阅可以同时管创建列与修改列。
    /// 点名式的那条更新链（orm.Update().Set().Where()）与删除链都不问它：
    /// 那两条链上没有"整行的值"这个东西可读，在这里跑一次等于凭空写调用方没说的列——
    /// 那一条的改写槽位是 ParameterBinding。零成本规则照旧：未订阅就不构造事件对象。</summary>
    public Action<AuditValueEvent>? AuditValue;
    public Action<ParameterBindingEvent>? ParameterBinding;

    /// <summary>整句文本级的改写槽位（软删、影子表、全局注释）。
    /// 发射点是 StatementEmitter.ApplyCud，写侧三条链与跟踪器的两个发射口都走那里，
    /// 位置在 AST 已成文之后、ParameterBinding 之前。读侧的 QueryBuilding 与 CommandCreating 仍是桩。</summary>
    public Action<CudBuildingEvent>? CudBuilding;

    // ── 决策类：可取消 / 可接管
    /// <summary>执行前最后一道闸门：返回 true = 取消执行，抛 QuickSqlCommandCancelledException。
    /// 典型用法是拦没有 WHERE 的 UPDATE/DELETE（生产禁整表写硬闸），也可以做租户级禁写、审计拦截等。
    /// 零成本规则：未订阅时 HasDeciders 为 false，执行路径上只走一次分支预测。</summary>
    public Func<CommandTerminatingEvent, bool>? CommandTerminating;

    public ICacheAdapter? Cache { get; set; }

    public long SlowQueryThresholdMilliseconds { get; set; } = 500;

    public bool HasObservers => Executing is not null || Executed is not null || Error is not null || SlowQuery is not null || Materialized is not null || EntityWritten is not null;

    public bool HasValueRewriters => AuditValue is not null || ParameterBinding is not null;

    public bool HasCudRewriters => CudBuilding is not null;

    public bool HasDeciders => CommandTerminating is not null;

    /// <summary>观察类钩子的异常边界：吞掉但上报，不让日志代码打挂业务写入。</summary>
    public void Observe(Action? hook, CommandLogEvent? context = null)
    {
        if (hook is null)
            return;

        try
        {
            hook();
        }
        catch (Exception ex)
        {
            if (Error is { } error)
                error(new CommandErrorEvent(ex, context?.Sql.Text ?? string.Empty, context?.ElapsedMilliseconds ?? 0));
        }
    }
}

/// <summary>执行前播报的事件（QuickSqlAop.Executing）。形状就是"这一刻已知的东西"：
/// 文本与参数（Sql 自己带着）、操作名、涉及的表——三格都是发射点手上确实有的，
/// 没有一格是填了好看的占位。这一位是观察类、改不了句子：整句文本级的改写槽位是 CudBuilding，
/// 取消这一句的闸门是 CommandTerminating。</summary>
public sealed record CommandExecutingEvent(
    Sql.DialectSql Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames);

/// <summary>一句已执行语句的观察事件。Route 是必填的一格：发射点只有 AdoExecutor.Succeeded 一处，
/// 而它手上一定有那次借用的路由结论，所以"订阅方看不见这句走了哪条连接"在这一版是结构上不可能的事。
/// Route 是这句落在哪座集群、哪个角色、为什么——读写分离与分库最难验收的部分正是
/// "这一句到底走了哪条连接、按哪一档走的"，而翻库比读这一格贵得多。注意它是结论不是意图：
/// FromReplica() 在没配从库的集群上是当场拒而不是静默落主库，所以订阅方看见的理由就是那句语句实际走的路。</summary>
public sealed record CommandLogEvent(
    Sql.DialectSql Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames,
    long ElapsedMilliseconds,
    int AffectedRows,
    int? FirstRowSummary,
    DataSourceRoute Route)
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
}

public sealed record CommandErrorEvent(Exception Exception, string Sql, long ElapsedMilliseconds);

public sealed record SlowCommandEvent(Sql.DialectSql Sql, long ElapsedMilliseconds, long ThresholdMilliseconds);

public sealed record MaterializedEvent(object Entity, string EntityTypeName, int OrdinalInBatch);

/// <summary>会话那一轮写出去的实体（QuickSqlAop.EntityWritten）。
/// 逐只一次：一只实体在这一轮里只被写一次，所以订阅者收到的事件与"这一轮改了几行"一一对应。
/// 它在语句建出来之前发：订阅者改得动实体，而那一格改出来的值会被紧接着建出来的语句带出去。
/// OperationKind 是 insert / update / delete，与 ExecutionTag、CudBuildingEvent.OperationKind 同一个词。</summary>
public sealed record EntityWrittenEvent(object Entity, string OperationKind);

/// <summary>软删那一档往上加的 SET 项（QuickSqlAop.SoftDeleteValues）。
/// 订阅者往 ExtraValues 里加（列, 值）对，库把它们参数化并写进那句 UPDATE 的 SET；
/// 值的形状与 AuditValueEvent.SetValue 那一格同形（CLR 值，库负责绑定）。
/// 列要过与那两格同一把尺子：自增列与并发列不收（前者不接受写，后者只比较不书写）。</summary>
public sealed class SoftDeleteValuesEvent
{
    internal SoftDeleteValuesEvent(object entity, string tableName)
    {
        Entity = entity;
        TableName = tableName;
    }

    /// <summary>被删的那一只。</summary>
    public object Entity { get; }

    /// <summary>那张表的表名。</summary>
    public string TableName { get; }

    /// <summary>订阅者往上加的 SET 项（列 + 值）。</summary>
    public IList<KeyValuePair<ColumnContract, object?>> ExtraValues { get; } = [];
}

/// <summary>批量降级事件（QuickSqlAop.BulkDegraded）。只在真的降级时派发：
/// 请求 Auto 而拿到的就是阶梯上第一档不算降级，请求了装载通道而那一家根本没有通道才算。
/// Reason 与 BulkReport.Reason 是同一句话的两个读者（一个给人看、一个是订阅方要打的字段），
/// 所以两处由 InsertEngine 一次算出、不分头编。</summary>
public sealed record BulkDegradedEvent(
    Providers.BulkMethod Requested,
    Providers.BulkMethod Method,
    int RowCount,
    string Reason);

/// <summary>列级自动填充。零装箱的做法是回调只在"该列需要填充"时触发，且 SetValue 直接写回实体字段。</summary>
public sealed class AuditValueEvent : IEquatable<AuditValueEvent>
{
    readonly Action<object?> _setter;

    internal AuditValueEvent(object entity, ColumnContract column, object? currentValue, string operationKind, Action<object?> setter)
    {
        Entity = entity;
        Column = column;
        CurrentValue = currentValue;
        OperationKind = operationKind;
        _setter = setter;
    }

    public object Entity { get; }

    public ColumnContract Column { get; }

    /// <summary>这一格值是为哪一种写操作取的，insert 或 update，与 ExecutionTag、
    /// CudBuildingEvent.OperationKind 同一个词。它是这一颗事件里唯一能把两条链分开的东西：
    /// 一条链上是同一只实体、同一批列，而"创建时间只在新行补、修改时间每次改都盖"这类填充恰恰要分这两条链。
    /// 没有 delete 这一档：删除不取整行的列值，所以它没有可问的格。</summary>
    public string OperationKind { get; }

    public object? CurrentValue { get; private set; }

    public bool WasSet { get; private set; }

    public void SetValue(object? value)
    {
        CurrentValue = value;
        WasSet = true;
        _setter(value);
    }

    public bool Equals(AuditValueEvent? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

public sealed class ParameterBindingEvent
{
    internal ParameterBindingEvent(Sql.AdoParameter parameter, ColumnContract? column)
    {
        Parameter = parameter;
        Column = column;
    }

    public Sql.AdoParameter Parameter { get; private set; }

    public ColumnContract? Column { get; }

    public void SetValue(object? value) => Parameter = Parameter with { Value = value };

    public void ChangeType(System.Data.DbType dbType, int size) => Parameter = Parameter with { DbType = dbType, Size = size };
}

/// <summary>整句文本级改写的事件。Sql 是已经串到当前的那一份（前一个订阅者的产出），
/// 订阅者要改就整体换掉 Rewritten；不写这一格就是"看过但不改"，不产生回放记录。
/// OperationKind 是 insert / update / delete，与 ExecutionTag 同一个词。</summary>
public sealed class CudBuildingEvent
{
    public required string OperationKind { get; init; }

    public required Sql.DialectSql Sql { get; init; }

    public Sql.DialectSql? Rewritten { get; set; }
}

/// <summary>决策类钩子的事件载体（CommandTerminating）。
/// 订阅者拿到这只对象后返回 true = 取消执行（抛 QuickSqlCommandCancelledException），
/// 返回 false 或 null 订阅 = 放行。典型用法：拦没有 WHERE 的 UPDATE/DELETE、租户级禁写。</summary>
public sealed record CommandTerminatingEvent(
    string Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames,
    IReadOnlyList<Sql.AdoParameter> Parameters);

/// <summary>CommandTerminating 钩子返回 true 时抛出的异常。
/// 继承 QuickSqlException，调用方可以按这一档单独 catch。</summary>
public sealed class QuickSqlCommandCancelledException : QuickSqlException
{
    public QuickSqlCommandCancelledException(string message)
        : base(message)
    {
    }
}

public interface ICacheAdapter
{
    bool TryGet<T>(string key, out T? value);

    void Set<T>(string key, T value, TimeSpan ttl);
}

/// <summary>硬规则 1：已注册钩子在 Explain() 输出里逐个可回放、顺序可见。</summary>
public sealed record HookTrace(int Step, string HookName, HookCategory Category, string Before, string After);
