using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Materialization;
using QuickSql.Sql;

namespace QuickSql.Execution;

/// <summary>逃生舱的执行侧落点：字符串 SQL + 参数对象那一族口。它们全部只是把「文本 + 匿名对象」
/// 折成 DialectSql 再交给已有的低层口，所以 AOP、会话连接复用、事务环境槽位、超时与摘前缀纪律
/// 一行都没有第二份实现——逃生舱自带执行路径会在日志格式与连接策略上漂移，而报表 SQL 恰恰最需要
/// 与链式 SQL 出现在同一份日志里。行出口两种形状：标量直读第 0 列；对象按列名对属性名装配（RawRowReader）。
/// 构造器 DTO 与 record 装不出来，那是带委托的 QueryAsync 的落点。</summary>
public sealed partial class AdoExecutor
{
    /// <summary>把参数对象折成 DialectSql：文本原样不动，参数名取本家方言的 ParameterPrefix 加属性名
    /// （口径与 RawSql 相同）。要把一句自定义的行委托接到原生 SQL 上时，入口就是这里——
    /// 下面那三个口只是它的三个常用特例。</summary>
    [RequiresUnreferencedCode(RawSql.ParameterMessage)]
    public DialectSql ToRawSql(string sql, object? args)
        => RawSql.ToDialectSql(sql, args, Provider.Dialect.ParameterPrefix);

    [RequiresUnreferencedCode(RawExitMessage)]
    [RequiresDynamicCode(RawExitMessage)]
    public Task<List<T>> QueryAsync<T>(string sql, object? args = null, CancellationToken ct = default)
        => QueryAsync(ToRawSql(sql, args), RawRowReader.Create<T>(), RawSql.Tag, null, ct);

    [RequiresUnreferencedCode(RawExitMessage)]
    [RequiresDynamicCode(RawExitMessage)]
    public Task<T?> FirstAsync<T>(string sql, object? args = null, CancellationToken ct = default)
        => FirstAsync(ToRawSql(sql, args), RawRowReader.Create<T>(), RawSql.Tag, ct);

    /// <summary>逐行流式：链式 IAsyncEnumerable 出口在原生 SQL 侧的对应物。</summary>
    [RequiresUnreferencedCode(RawExitMessage)]
    [RequiresDynamicCode(RawExitMessage)]
    public IAsyncEnumerable<T> StreamAsync<T>(string sql, object? args = null, CancellationToken ct = default)
        => StreamAsync(ToRawSql(sql, args), RawRowReader.Create<T>(), RawSql.Tag, ct);

    /// <summary>第一行第一列的值，DBNull 折成 null。零行交出 null——那句 SQL 是你写的，它选出零行是可能的。</summary>
    [RequiresUnreferencedCode(RawSql.ParameterMessage)]
    public async Task<object?> ExecuteScalarAsync(string sql, object? args = null, CancellationToken ct = default)
    {
        Func<DbDataReader, object?> map = static reader => reader.IsDBNull(0) ? null : reader.GetValue(0);

        return await FirstAsync(ToRawSql(sql, args), map, RawSql.Tag, ct).ConfigureAwait(false);
    }

    /// <summary>非查询语句（DDL、批量脚本片段、存储过程式的调用）。返回受影响行数，
    /// 与 NonQueryAsync 同一个出口，因此同一个 AOP 链。</summary>
    [RequiresUnreferencedCode(RawSql.ParameterMessage)]
    public Task<int> ExecuteNonQueryAsync(string sql, object? args = null, CancellationToken ct = default)
        => NonQueryAsync(ToRawSql(sql, args), RawSql.Tag, ct);

    /// <summary>一次调用发多句文本，共用一条连接与一次事务环境：语句之间不各自开连接。
    /// 它不开事务——要把这一串包进事务请显式 BeginTransaction，因为"这一串是不是一个原子单位"只有 SQL 作者知道。</summary>
    public async Task ExecuteBatchAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statements);

        for (var i = 0; i < statements.Count; i++)
            await NonQueryAsync(new DialectSql(statements[i], []), RawSql.Tag, ct).ConfigureAwait(false);
    }

    const string RawExitMessage = "原生 SQL 的行出口按运行期类型找属性并编译读写委托。";
}
