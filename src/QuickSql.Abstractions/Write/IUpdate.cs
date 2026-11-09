using System.Linq.Expressions;
using QuickSql.Aop;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Write;

/// <summary>UPDATE 链。与 IInsert 同一种形状：算子只攒模型，出口才生成文本，
/// 所以 ToSql() 与紧随其后的 ExecuteAffrowsAsync() 是同一条语句。
/// 这一条链上没有自增主键回填那一档：UPDATE 回填要的是改后整行，
/// 那是 OUTPUT deleted/inserted 的另一套形状，随 SaveChanges 的余项一起设计。</summary>
public interface IUpdate<T> where T : class
{
    /// <summary>把一列写成给定的值。值走参数而不是字面量（与插入链同一纪律），
    /// 所以带引号的值不可能拼出注入来。列由 lambda 决定，
    /// 因此改字段名/重命名之后编译就报错，而字符串列名的库要到运行期才发现写错了列。</summary>
    IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, TProp value);

    /// <summary>把一列写成由同一行的列算出来的值（表达式自增那档）。
    /// 这是"绕开跟踪器"的那条路——读改写如果在应用侧做，两个并发请求会互相覆盖，
    /// 而 SET 列 = 列 + 参数 在库里是原子的。</summary>
    IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, Expression<Func<T, TProp>> value);

    /// <summary>条件。可以叠加（多次 Where 按 AND 折叠成一条链），但不能没有：一次 Where 都不叠的
    /// UPDATE 会在构建时抛（三处出口同一句话），因为"整表每一行改成同一个值"没有一种是有意的默认行为；
    /// 要清全表就显式写恒真条件。全局过滤器不算这里说的"条件"：过滤器叠进 WHERE 的是
    /// "你没资格改那些行"，不是"你要改这些行"——这道闸门判的是后者。</summary>
    IUpdate<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>分表实体的 UPDATE 要打的那一片。一次一片，不是范围：一条 UPDATE 只有一个目标表，
    /// 给范围就要悄悄执行 N 条，而这条链的契约写的是 ToSql 与紧随其后的 ExecuteAffrows 是同一条语句。
    /// 参数是那一片里的一个日期，它下整到本档的周期就是片名。必须放在链首：SET 与 WHERE 的列引用
    /// 是翻译时绑到当时的目标上的，事后换片会让前缀停在模板名上。不分表的实体上调它直接抛。</summary>
    IUpdate<T> SplitTable(DateTime at);

    /// <summary>分表实体的 UPDATE 要打的那一片（哈希档）：给桶号，
    /// 越界的桶号抛。同一句"一次一片、必须放在链首"的纪律。</summary>
    IUpdate<T> SplitTable(int bucket);

    UpdatePlan Explain();

    string ToSql();

    string ToSql(out IReadOnlyList<AdoParameter> parameters);

    /// <summary>逐参内联字面量的那一份文本（给 DBA / 日志）。UPDATE 不分块，所以没有块分节。</summary>
    string ToLogSql();

    /// <summary>受影响行数。库里匹配到但值没变的行也算（T-SQL 的 @@ROWCOUNT 就是谓词命中数），
    /// 所以"返回 0"只证明没匹配上，不证明没改到东西——变更守卫（IS DISTINCT FROM）
    /// 是批量保存那一支的事，这里没接。</summary>
    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);
}

/// <summary>一次 UPDATE 的完整决策记录。与 InsertPlan 不同，这里没有分块：一条 UPDATE 就是一条语句，
/// 参数预算的账落在 Where 里的集合上（见 Reason 的那句提醒）。</summary>
public sealed record UpdatePlan(
    UpdateStatement Ast,
    DialectSql Sql,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    /// <summary>与 InsertPlan.Provider 同理：内联字面量要问那一家方言的 binder。</summary>
    public required IDbProvider Provider { get; init; }

    public string ToSql() => Sql.Text;

    public string ToLogSql() => Sql.ToLogSql(Provider);

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}

/// <summary>DELETE 链。这一句发出去的是 DELETE 还是 UPDATE，看这张表上有没有在册的软删行为：
/// 注册过软删的实体，这一句在 AST 层就被换成"置位那一格写 true、时间那一格记一颗"的 UPDATE，
/// WHERE 逐字不动；没注册的仍发 DELETE。两条都发得动这件事不许靠猜：DeletePlan.Reason 每一支都按
/// 实际发出去的那一句自陈；库里预置的那一颗只改形状、不占文本改写的口子——调用方自己的 CudBuilding
/// 订阅者看到的是改写后的整句文本，仍能接着改。
/// 这一句的 WHERE 可能不止调用方写的那几句：在册的全局过滤器会叠进来，叠了几条也写在 Reason 里。
/// 软删不往 WHERE 里加"且尚未置位"那一支，加了就要先有人裁"被挡住的行算不算一次并发冲突"。</summary>
public interface IDelete<T> where T : class
{
    /// <summary>条件。不能没有：一次 Where 都不叠的 DELETE 在构建时抛，理由同 UPDATE
    /// （整表清空不该是一个漏写条件的默认结果）。</summary>
    IDelete<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>分表实体的 DELETE 要打的那一片，口径与 UPDATE 的 SplitTable 逐字相同：
    /// 一次一片（一条 DELETE 只有一个目标表），且必须放在链首。</summary>
    IDelete<T> SplitTable(DateTime at);

    /// <summary>分表实体的 DELETE 要打的那一片（哈希档）。</summary>
    IDelete<T> SplitTable(int bucket);

    DeletePlan Explain();

    string ToSql();

    string ToSql(out IReadOnlyList<AdoParameter> parameters);

    string ToLogSql();

    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);
}

/// <summary>一次 DELETE 的决策记录。Reason 必须自陈两件事：最终文本落在"物理删除"、
/// "本库的软删改写"还是"已被 CudBuilding 改写"哪一支，以及这一句的 WHERE 里叠了几条全局过滤器——
/// 因为这条链在开了软删或多租户的项目里看起来应该产出 UPDATE、而且它的命中范围比调用方写的那几句窄。
/// Ast 是这条链要的那一句（永远是 DELETE），Sql 是真正发出去的那一句——两者只在软删在册时不一致，
/// 而那一格是这一档的语义而不是 bug：调用方说的是"删掉这些行"，库里执行的是"把这些行的那一格置位"。
/// 要在改写后的形状上做文章（比如按列名核对置位对不对）请读 Sql.Text 或订 CudBuilding，
/// 而不是猜 Ast 有没有被换过。</summary>
public sealed record DeletePlan(
    DeleteStatement Ast,
    DialectSql Sql,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    public required IDbProvider Provider { get; init; }

    public string ToSql() => Sql.Text;

    public string ToLogSql() => Sql.ToLogSql(Provider);

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}
