using System.Diagnostics.CodeAnalysis;
using QuickSql.Aop;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Write;

/// <summary>写侧链式契约。与读侧同一套入口风格但不合并：
/// 读侧的出口是行，写侧的出口是受影响行数与回填的键，两者的钩子集合也不同（改写类只在写侧有意义）。
/// 读侧那条"不可变值、可复用、不需要 Clone"在这里不成立：
/// AuditValue 会写实体，所以一个插入对象只算一次列值，
/// 之后的出口共用同一份结果——于是 ToSql() 与紧随其后的 ExecuteAffrowsAsync() 必然是同一条语句，
/// 不会出现"看着 SQL 执行了另一条"。</summary>
public interface IInsert<T> where T : class
{
    /// <summary>不回填键那一档的计划（对应 ExecuteAffrowsAsync）。</summary>
    InsertPlan Explain();

    /// <summary>回填键那一档的计划（对应 ExecuteReturnKeysAsync / ExecuteIdentityAsync）。
    /// 单独一个口，而不是让 Explain 看情况给两种文本：要不要 OUTPUT 由出口决定，
    /// 而"要审阅的那条 SQL 到底是哪一条"不能靠调用方猜。两档共用同一份取值结果，差别只在文本。</summary>
    InsertPlan ExplainKeys();

    /// <summary>不连库。只有单块时可取：分块后每块是一份独立的参数表，
    /// 拼在一起会得到两段 @p0 起始的文本——那不是任何一条能执行的语句。</summary>
    string ToSql();

    string ToSql(out IReadOnlyList<AdoParameter> parameters);

    /// <summary>逐块内联字面量，多块时带块分节：交付给 DBA 的文本必须能看出有几条语句。</summary>
    string ToLogSql();

    /// <summary>这一批走批量阶梯的哪一层，以及拿不到那一层时怎么办。
    /// 它只改 ExecuteAffrowsAsync 与 ExecuteBulkAsync 这两条出口的走法，不改它们的语义：
    /// 回填键的两颗出口要回填库里生成的那些列，而装载通道给不了（不能回填自增主键），
    /// 所以回填那一档永远走文本层，请求了 CopyChannel 也一样。
    /// 不调这一句时 ExecuteAffrowsAsync 的默认层是 MultiValue 而不是 Auto——这不是偷懒而是必须的裁决：
    /// 若默认就取阶梯最高层，SQL Server 上"接了 SqlBulkCopy"这一件事会让每一句 ExecuteAffrowsAsync
    /// 不再产生 SQL 文本，CudBuilding（软删改写、影子表）与 ParameterBinding 两个改写钩子集体失效；
    /// 反过来 ExecuteBulkAsync 在不调用这一句时默认取 Auto，那一条出口存在的意义就是"我要按层要细节"。
    /// rowsPerRoundTrip 只许往下压、不许往上抬——抬过参数预算会产出一条驱动直接拒掉的语句
    /// （那正是分块预算要防的"换宽表就出事"），所以上限由预算算出来、请求值取小，被压这件事写在 Reason 里；
    /// 装载通道那一档它说的是每次往返送几行（没有参数预算那一道界）。</summary>
    IInsert<T> WithBulk(
        BulkMethod method,
        FallbackPolicy fallback = FallbackPolicy.DegradeAndLog,
        int? rowsPerRoundTrip = null);

    /// <summary>不连库回答"这一批会走哪一层、几次往返、为什么"（纯函数，可单测、可打印）。
    /// requested 为 null 时问的就是 ExecuteBulkAsync 会做的事。
    /// 它与 Explain 的分工：Explain 交出的永远是文本层的语句，而这一颗交出的是一句决定——
    /// 装载通道那一档没有文本可解释。
    /// 不连库不等于不反射：判"这一家有没有通道"靠的正是那次按类型名的查找，所以修剪税在这里也要交——
    /// PublishTrimmed=true 的产物上 SqlBulkCopy 会被裁掉，于是这一颗在裁剪后报 MultiValue、
    /// 在完整运行时上报 CopyChannel，让读者以为两次的产物本该同形，比多一句标注贵得多。</summary>
    [RequiresUnreferencedCode("判据里包含一次按类型名的装载通道查找（见 IBulkStrategy.ResolveCopyChannel 的抬头），被裁剪的驱动会让这一颗报出不同的层。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    BulkReport ExplainBulk(BulkMethod? requested = null);

    /// <summary>受影响行数（多块时是各块之和）。不回填主键：要键用 ExecuteReturnKeysAsync。</summary>
    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);

    /// <summary>按 WithBulk 请求的那一层执行，并把决策本身交回来（走了哪一层、几次往返、
    /// 是否降级、为什么）。这一条入口是写路径上第一颗带修剪税的出口：
    /// SqlBulkCopy 不在 DbProviderFactory 的抽象面上，只能按类型名去找它，而裁剪器看不见那一句。
    /// 文本层（MultiValue/SingleRow）同走这一条入口，税因此是按方法收而不是按实际走的那一层收；
    /// 要零税就用 ExecuteAffrowsAsync，它的默认层永远是文本层。</summary>
    [RequiresUnreferencedCode("装载通道按类型名在驱动程序集里找批量装载类（SqlBulkCopy 那一族没有 ADO.NET 抽象面），被裁剪的驱动会在这里报通道不存在。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析驱动类型需要运行时生成代码。")]
    Task<BulkReport> ExecuteBulkAsync(CancellationToken ct = default);

    /// <summary>单行自增主键。多行或自增列不是一份整数时抛明确异常，而不是静默返回首行的键。</summary>
    Task<long> ExecuteIdentityAsync(CancellationToken ct = default);

    /// <summary>回填自增主键并返回同一批实体（顺序即入参顺序）。provider 无批量 RETURNING 时
    /// 自动退到逐条，语义不变。</summary>
    Task<List<T>> ExecuteReturnKeysAsync(CancellationToken ct = default);
}

/// <summary>一个块：一次 round-trip 的语句加它覆盖的行区间。
/// FirstRow 是回填时的实体下标，顺序映射就是这里唯一的真相
/// （OUTPUT/RETURNING 的回包按源行序）。</summary>
public sealed record InsertChunk(int FirstRow, int RowCount, Sql.Ast.InsertStatement Ast, DialectSql Sql)
{
    /// <summary>分表那一条路上，一个块装的是同一张片的行，而那些行在入参数组里不连续
    /// （按月切的表里，相邻两行可以隔三个月）。这一格给的就是那几行在入参数组里的下标，回填按它映射；
    /// 不分表时恒为 null，那时 FirstRow 加回包行号就是下标，热路径上一条语句都不多分配。</summary>
    public int[]? RowOrdinals { get; init; }
}

/// <summary>一次插入的完整决策记录。Reason 必须是给人看的一句话：
/// 现场排障时先看到"为什么是 63 行一块"，而不是先去读源码。</summary>
public sealed record InsertPlan(
    IReadOnlyList<InsertChunk> Chunks,
    int RowCount,
    int ColumnsPerRow,
    int RowsPerStatement,
    int ParameterBudget,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    /// <summary>与 QueryPlan 同理：AST 与文本本来就是某一家方言的产物，内联字面量要问它的 binder。</summary>
    public required IDbProvider Provider { get; init; }

    public bool IsSingleStatement => Chunks.Count == 1;

    /// <summary>0 行时没有语句（不连库、返回 0 行），所以这里抛的是"没有文本"，不是"没有执行"。</summary>
    public string ToSql() => Chunks.Count switch
    {
        1 => Chunks[0].Sql.Text,
        0 => throw new QuickSqlException("0 行插入没有对应的 SQL 文本：不产生语句，执行会直接返回 0。"),
        _ => throw new QuickSqlException($"这次插入被分成 {Chunks.Count} 块（{Reason}），没有单一 SQL 文本。取 Explain().Chunks[i].Sql，或用 ToLogSql() 看全部分块。"),
    };

    public string ToLogSql()
    {
        if (Chunks.Count == 0)
            return "-- 0 行：不产生任何语句";

        var lines = new string[Chunks.Count];

        for (var i = 0; i < Chunks.Count; i++)
        {
            var chunk = Chunks[i];
            var log = chunk.Sql.ToLogSql(Provider);

            lines[i] = Chunks.Count == 1 ? log : $"-- 块 {i + 1}：第 {chunk.FirstRow}~{chunk.FirstRow + chunk.RowCount - 1} 行{Environment.NewLine}{log}";
        }

        return string.Join(Environment.NewLine, lines);
    }

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}
