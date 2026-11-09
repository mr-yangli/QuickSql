using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;

namespace QuickSql.Providers;

/// <summary>批量写入走哪一层。只有今天真有实现的层才进这颗枚举（数组绑定、临时表、MERGE 那一整档没有成员）：
/// 加一颗点不到货的名号只会让 BulkReport.Method 多一个永不为真的取值。
/// Auto 不是"另一层"，是"由这一条出口自己从上往下取第一个满足条件的层"。</summary>
public enum BulkMethod
{
    /// <summary>从上往下取第一个满足条件的层：装载通道 → 多值语句。</summary>
    Auto,

    /// <summary>批量装载通道：不走 SQL 文本、不走参数（SQL Server 的 SqlBulkCopy）。</summary>
    CopyChannel,

    /// <summary>多值语句：一条 INSERT 带多个 VALUES，按参数预算分块。</summary>
    MultiValue,

    /// <summary>逐条：同一份文本反复执行，一次往返一行。阶梯的兜底档，永远可达。</summary>
    SingleRow,
}

/// <summary>请求的层拿不到时怎么办。默认 DegradeAndLog：批量在绝大多数现场是纯优化路径，
/// 挡在门外比放进来更糟；但"静默逐条"必须是能被订阅出来的，否则一次 600 行的保存悄悄变成 600 次往返，
/// 而现场只看得到"它跑通了"。留痕的落地形态是 Aop.QuickSqlAop.BulkDegraded 那一个订阅位。</summary>
public enum FallbackPolicy
{
    /// <summary>降级，并在 Aop.QuickSqlAop.BulkDegraded 上留一次痕（未订阅时零成本）。</summary>
    DegradeAndLog,

    /// <summary>降级且不声张：只在批量本身是缓存回填那一类纯优化路径时用。</summary>
    DegradeSilently,

    /// <summary>拿不到请求的层就抛。给 CI 与上线前压测用——唯一能防住静默逐条的手段。</summary>
    Never,
}

/// <summary>一次批量写到底走了哪一层、付了几次往返、为什么（纯函数、可单测、可打印）。
/// Reason 必须是给人看的一句话：现场先看到"为什么是 5 次往返"，而不是去读源码。
/// Requested 与 Method 分开记，因为两者不同正是要三处留痕的那一件事。</summary>
public sealed record BulkReport(
    BulkMethod Requested,
    BulkMethod Method,
    int RowCount,
    int RoundTrips,
    int ChunkSize,
    bool Degraded,
    string Reason);

/// <summary>递给装载通道的一批数据。值已经是驱动侧的形态（null 是 DBNull、枚举已经折成底层整数），
/// 由内核过 IValueBinder 那一扇门——通道不许自己再猜一遍类型，否则"同一条插入在几家参数化形状不同"
/// 那句话就有第二个出处；代价是这一趟每格多一次 AdoParameter 装箱。
/// DestinationTable 已按方言取号限定（由 ISqlDialect.EmitTableName 产出，COPY 落的那一张与 INSERT 写的必然是同一张）；
/// Rows 一行一个数组、下标与 Columns 对齐，行序就是入参顺序——装载通道没有回包可对，
/// "进去的第 n 行是哪一行"只有这一处真相。</summary>
public sealed record BulkCopyRequest(
    string DestinationTable,
    IReadOnlyList<ColumnContract> Columns,
    IReadOnlyList<object?[]> Rows);

/// <summary>装载通道借用执行层的那三格：一条连接、它上面可能挂着的事务、超时。
/// 与 BulkCopyRequest 分成两颗是因为来路不同：数据那一半归链式对象（取值段算一次），
/// 连接那一半只在执行的一刻才知道（环境事务 / 会话连接 / 工厂 / 新建四支）。</summary>
public sealed record BulkConnection(DbConnection Connection, DbTransaction? Transaction, int TimeoutSeconds);

/// <summary>批量装载通道在 SPI 上的形状。签名里没有驱动类型：给的是一条已经开好的
/// DbConnection 与它上面可能挂着的事务，所以 provider 内部怎么找到自家的装载类（反射、缓存委托、
/// 或直接强类型）都是它自己的事，共用层与其余家一个字都不必知道。
/// WriteAsync 交回的是"这一批真进去了多少行"，通道给不出这个数时宁可抛，也不要报一个看着对的数。</summary>
public interface IBulkCopyChannel
{
    /// <summary>这一列能不能进装载通道。假是一等公民：一列不合格时整批退回上一层（多值语句），
    /// 而不是把那一列从通道里省掉——省一列等于那一列的值不进库，那是丢写。</summary>
    bool SupportsColumn(ColumnContract column);

    /// <summary>这条连接串下这一家通道真的可用吗。交回非 null = 装载类在场、而这条串过不去，
    /// 交回的是给人看的一句话：它在 Auto 与 CopyChannel 两档请求下都进 BulkReport.Reason
    /// （点名 MultiValue/SingleRow 的请求根本不解通道），整批因此落在多值语句而不是付掉往返再在半路炸。
    /// "落在多值语句"与"留降级痕"是两件事：请求了 CopyChannel 而拿不到才算降级，Auto 拿不到不算
    /// （它没承诺过任何一层），所以这句话是 Auto 那条路上唯一的可见面，写得越能行动越好（点名缺的是哪颗参数）。
    /// 默认 null = 通道存在即可用；有闸门的家覆写这一颗（MySQL 血统的 LOAD DATA LOCAL 要连接串放行
    /// AllowLoadLocalInfile=true，缺它时驱动在执行当场抛 NotSupportedException），闸门本身归家、不归内核。</summary>
    string? UnavailableBecauseOf(string connectionString) => null;

    [RequiresUnreferencedCode("装载类不在 DbProviderFactory 的抽象面上，只能按类型名在驱动程序集里找；被裁剪的驱动会在这里报通道不存在。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析驱动类型需要运行时生成代码。")]
    Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct);
}

/// <summary>递给 UpsertClause 的三份已经按本家方言取过号的列名清单。
/// 三份都要，是因为两家各用其中两份、而共用那份的顺序：MySQL 的行别名清单必须与 INSERT 的列清单
/// 逐位同序（按 Settable 与 Keys 各拼一遍就会与 VALUES 错列），PG 的 ON CONFLICT 只点名键。
/// Settable 是冲突那一支要改写的列（已按整行更新的同一把尺排除主键、库里给值的列与并发列）；
/// Keys 是定位与冲突判定的主键列、顺序同契约（复合键在这里装得下：ON CONFLICT (a, b) 是一句合法文本，
/// 与"IN 装不下元组"是两件事）；ValueOrder = Settable 加 Keys，与内核拼出的 INSERT 列清单逐位相同。
/// 取号只有内核这一扇门（ISqlDialect.EmitIdentifier），provider 既不猜引号也不猜大小写折叠；
/// Identifier 把一颗裸标识符换成这一家的取号形态，provider 自己点名的名字（如 MySQL 给行别名起的 fs）
/// 必须从这里过——手拼引号就等于在 IdentifierPolicy 之外多出第二个口径。</summary>
public sealed record UpsertColumns(
    IReadOnlyList<string> ValueOrder,
    IReadOnlyList<string> Settable,
    IReadOnlyList<string> Keys,
    Func<string, string> Identifier);
