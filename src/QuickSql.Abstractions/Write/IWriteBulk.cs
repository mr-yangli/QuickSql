using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using QuickSql.Aop;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Write;

/// <summary>批量写链（UpdateRange / DeleteRange）走哪一档。
/// 只有今天真有实现的档才进这颗枚举，与 BulkMethod 同一条纪律：
/// StagingCopy（先 COPY 进临时表再一句 UPDATE FROM）今天没有一家接到能报行数的实现，所以这里没有它的成员。
/// Auto 与插入那一族的口径逐字相同：不是"另一档"，是"由这条出口自己从上往下取第一个满足条件的档"。
/// 而 Upsert 是这颗枚举里第一颗"不在阶梯上"的成员：它不比谁快，它换的是语义
/// （库里没有的那一行会被插进去），所以它只在被点名时参与，Auto 永不取它。</summary>
public enum BulkWriteMethod
{
    /// <summary>从上往下取第一个满足条件的档：Merged、然后 SingleRow。
    /// DeleteRange 那一条链上 Merged 永远满足（一句 WHERE 键 IN 是单条语句，不欠多语句能力），
    /// 所以它的 Auto 恒等于 Merged；UpdateRange 的 Merged 要 SupportsMergedStatements
    /// 为真才可达，拿不到的那一家落逐条、不算降级（它没承诺过任何一档）。
    /// "取第一个满足条件的档"里没有 Upsert 的位置：条件的第一条是语义而不是速度，而 upsert 的语义
    /// 与其余两档不同——让 Auto 拿它等于把"改不到就是没这行"那一种调用悄悄变成"改不到就插一行"。</summary>
    Auto,

    /// <summary>多条语句并成一条命令文本一次递交（UpdateRange），或一句 IN 清单删整批键
    /// （DeleteRange）。分块仍按参数预算走。</summary>
    Merged,

    /// <summary>一句多值 INSERT 加冲突子句把整批改完（MySQL 血统的 ON DUPLICATE KEY UPDATE、
    /// PG 血统的 ON CONFLICT DO UPDATE）。只有 IUpdateBulk 有这一档：IDeleteBulk 点名它当场拒——
    /// "缺键就插"在删除链上没有意义。
    /// 它与 Merged 的差别不是快慢而是命中之外那一支的去向：Merged 打不中就是 0 affected，
    /// 而这一档写进去一个新行。因此 Auto 永不取它，而点名它而这一家没接时
    /// 按 FallbackPolicy 三档办——降级退到的是"整行改"那一档，语义跟着退，
    /// 那句话写进 BulkWriteReport.Reason 而不是留给现场猜。
    /// 两道当场拒：全局过滤器在场（那一句没有 WHERE 可以叠过滤器，命中范围会宽过调用方以为的），
    /// 以及主键由库给值而这一行的键仍是默认值（0、Guid.Empty、空串——那种键的冲突判定
    /// 出不来，插进去的是一个新行而不是改了那一行）。</summary>
    Upsert,

    /// <summary>逐条：一行一条语句、一次往返。阶梯最底那一档，永远可达。</summary>
    SingleRow,
}

/// <summary>一次批量写到底走了哪一档、付了几次往返、为什么。形状与 BulkReport 逐字同族
/// （同一份"现场先看到为什么，而不是去读源码"的纪律），拆成两颗而不是复用，是因为档位集合不同：
/// 插入阶梯有装载通道那一档而这里没有，这里也不该冒出一颗 CopyChannel 取值。</summary>
public sealed record BulkWriteReport(
    BulkWriteMethod Requested,
    BulkWriteMethod Method,
    int RowCount,
    int RoundTrips,
    int ChunkSize,
    bool Degraded,
    string Reason)
{
    /// <summary>这一批实际是不是一次提交。它是 init 属性而不是位置参数：决策段与执行段各判一次"要不要包"，
    /// 而执行段才知道驱动有没有给事务，放进位置参数会让所有构造点跟着改号。
    /// 环境事务在场时这一颗恒为 false——那不是"没包"，是"包得比这一批更大"，提交归开事务的那个人。</summary>
    public bool UsedTransaction { get; init; }

    /// <summary>库报回来的受影响行数，跨这一批的全部命令累加。它只在执行段有值：
    /// 决策段与 Explain 交回的那一份里它是 0，因为那一刻还没有任何语句进过库。
    /// 与 RowCount 是两件事：那一颗是调用方交出的行数，这一颗是库认账的行数，
    /// 两者不等就是"有些行没被这条语句命中"——按主键定位外加 Where 条件时，差值就是
    /// 被别人改过或删掉的那些行（乐观锁要的正是这个读数）。
    /// 多行命令文本上的累加口径跟着驱动：一条命令里并了几句时，ExecuteNonQuery 交回的是那几条语句
    /// 行数之和（SQL Server 与 PostgreSQL 都如此），所以这一颗能判"整批有没有全中"，不能判"哪一行没中"；
    /// 要逐行定责就退到 SingleRow 档逐条看返回值。</summary>
    public int AffectedRows { get; init; }
}

/// <summary>批量写链的离线产物：一批命令文本（一条命令 = 一次往返）加上决策的那句话。
/// 与 InsertPlan 的分工同一族，而块的内容不同：这里一块是"多条语句并成的一条命令文本"，
/// 参数表整条命令共用一份（名字由 SqlWriter 的共享计数器发，跨语句不重号）。</summary>
public sealed record BulkWritePlan(
    IReadOnlyList<DialectSql> Commands,
    int RowCount,
    int RowsPerCommand,
    int ParameterBudget,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    /// <summary>与 InsertPlan.Provider 同理：内联字面量要问那一家方言的 binder。</summary>
    public required IDbProvider Provider { get; init; }

    public bool IsSingleCommand => Commands.Count == 1;

    /// <summary>不连库；只有单条命令时可取，口径与 InsertPlan.ToSql 逐字相同。</summary>
    public string ToSql() => Commands.Count switch
    {
        1 => Commands[0].Text,
        0 => throw new QuickSqlException("0 行批量写没有对应的 SQL 文本：不产生命令，执行会直接返回 0。"),
        _ => throw new QuickSqlException($"这批写被分成 {Commands.Count} 条命令（{Reason}），没有单一 SQL 文本。取 Explain().Commands[i]，或用 ToLogSql() 看全部。"),
    };

    /// <summary>逐条命令内联字面量，多条时带命令分节：交给人审阅的文本必须能看出有几条命令。</summary>
    public string ToLogSql()
    {
        if (Commands.Count == 0)
            return "-- 0 行：不产生任何命令";

        var lines = new string[Commands.Count];

        for (var i = 0; i < Commands.Count; i++)
        {
            var log = Commands[i].ToLogSql(Provider);

            lines[i] = Commands.Count == 1 ? log : $"-- 命令 {i + 1}{Environment.NewLine}{log}";
        }

        return string.Join(Environment.NewLine, lines);
    }

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}

/// <summary>orm.UpdateRange(rows) 的链：按主键整行更新——每一行把除主键、自增列、
/// 并发列以外的全部列写进 SET，主键进 WHERE。它不是 IUpdate 的批量皮
/// （那条链改的是调用方点名的几列），也不与 SaveChanges 合流：那条要跟踪器的脏列判定，这一条的契约
/// 就是"整行交出去"。取值零表达式树（列清单来自契约、值来自编译好的读写委托），所以这一条入口
/// 没有 RUC/RDC 入口税。</summary>
public interface IUpdateBulk<T> where T : class
{
    /// <summary>请求哪一档。默认不是 Auto 而是它自己判出来的那一档：
    /// 这条链存在的意义就是批量，没有"默认逐条"那一种调用姿势。fallback 的三档语义
    /// 与 IInsert.WithBulk 逐字相同（Never 给 CI 用）。</summary>
    IUpdateBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog);

    /// <summary>这一批要不要一次提交。默认 true，执行段在没有环境事务在场时
    /// 自开一条事务、末尾提交一次；传 false 关掉，回到"每条语句自己提交、半批可以在库里落地"。
    /// 这一颗首先不是原子性开关，是速度开关：并档把往返并了而没把提交并了——InnoDB 每次提交
    /// flush 一次 redo，远端实例上整批更新的耗时差距几乎全部来自提交次数。
    /// 只在语句数大于 1 时才包：一条语句本身就原子，包它只多付两次往返。
    /// 关掉它的唯一理由是调用方自己要用"半批已落地"做断点续传——那种用法要么自己开事务、
    /// 要么自己逐条调单行链，所以这里给的是一等公民的位置而不是暗示的路。</summary>
    IUpdateBulk<T> WithBatchTransaction(bool atomic = true);

    /// <summary>在主键之外再钉一个条件：每一行的 WHERE 变成"主键 AND 这一句"（全局过滤器
    /// 仍然叠在最后，形状与读侧同一条纪律）。要它的理由是乐观锁：按主键整行改的那一类调用方
    /// （"这行还是我读到的那一行时才改它"）需要的是一行一句的额外谓词，而这一条链此前只给键；
    /// 命中数从 BulkWriteReport.AffectedRows 读——它小于 RowCount 就是有些行被别人改过。
    /// 三格口径：它让这一条链付表达式翻译的税（谓词要翻成 SQL，与 Explain 同一个咽喉、同一对标注，
    /// 没调用这一颗时一个字节都不多付）；点名 Upsert 那一档时带着它就当场抛——upsert 档发的是
    /// 一句多值 INSERT 加冲突子句，那里没有可叠的 WHERE（冲突判定只认主键，条件塞不进句尾），
    /// 而静默丢掉调用方给的条件是这一族最不肯做的一件事；可以连写多次，折叠方向与读侧的多次
    /// Where 一致（往末尾追加 AND）。</summary>
    IUpdateBulk<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>不连库看这批会发出的命令文本（档已按 WithBulk 的请求落定）。
    /// "零入口税"说的是 WithBulk 与 ExplainBulk 这两颗，不是这一颗：
    /// 建文本要把 orm.Filters 上的全局过滤器叠进 WHERE，那一步走闭包常量求值——
    /// 没有装过滤器的项目照样走到这句标注，税在类型面上不在分支上。</summary>
    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Explain();

    /// <summary>不连库回答"这一批会走哪一档、几条命令、为什么"——判据与执行共用同一颗纯函数，
    /// 口径与 IInsert.ExplainBulk 同一条。这一颗不交过滤器那笔税：判档只看
    /// provider 表态与行数，不建文本。</summary>
    BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null);

    /// <summary>执行并交回决策本身。返回值逐字对齐 IInsert.ExecuteBulkAsync：
    /// 一条命令的 ExecuteNonQuery 行数按驱动口径累加，降级不留痕于 AOP（本档的
    /// BulkDegraded 接线等事件形状改成两种档位通用那天再补）。</summary>
    [RequiresUnreferencedCode("执行前建文本要走全局过滤器的叠加（同 Explain 那句）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default);
}

/// <summary>orm.DeleteRange(rows) 的链：按主键删整批，Merged 档产出的是
/// DELETE 加 WHERE 单列主键 IN——一句单条语句，不欠多语句能力，所以这一条链的
/// Auto 在所有 provider 上都恒等于 Merged（预算不够切块，同一把尺）。
/// 复合主键当场拒并指路（IN 装不下元组，逐对 OR 展开会绕开参数预算的账）。
/// 与 IUpdateBulk 同一族理由：WHERE 长在键上而不是表达式树上，入口零反射税。</summary>
public interface IDeleteBulk<T> where T : class
{
    IDeleteBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog);

    /// <summary>同 IUpdateBulk.WithBatchTransaction，而这一条链上它几乎不触发：
    /// Merged 档是一句 WHERE 键 IN，一条语句本身就原子，包事务只多两次往返。
    /// 真正会包的只有一种形状——键数超出一句的预算、这批被切成两句以上（MySQL 血统 65535 颗、
    /// SQL Server 2100 颗，所以那一家两万颗键就是两条命令）。</summary>
    IDeleteBulk<T> WithBatchTransaction(bool atomic = true);

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Explain();

    BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null);

    [RequiresUnreferencedCode("执行前建文本要走全局过滤器的叠加（同 Explain 那句）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default);
}
