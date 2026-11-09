using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.SqlServer;

/// <summary>SQL Server 的乐观锁谓词。rowversion 支持已移除：并发令牌只有"应用自维护"这一条路，
/// 比较形态仍是 [Version] = @p（整型 Version 由 ORM 在 SET 里 +1，或 AppSetsValue 由应用给新值）。
/// 不要拿 @@DBTS 代替，那是数据库级的、不是行级的。</summary>
public sealed class SqlServerConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>比较式合取进 AST 的 WHERE，在发射之前调用；文本级的改写钩子排在这之后。
    /// 空值那一档走不到这里：[列] = @p 在 NULL 上是 UNKNOWN，每次保存都会成假冲突，
    /// 所以调用方把它折成 IS NULL 之后再下来。</summary>
    public SqlExpression BuildPredicate(
        SqlExpression target,
        ColumnContract column,
        object? originalValue
    ) =>
        new SqlBinaryExpression(
            BinaryOperator.Equal,
            target,
            new ParameterValue(originalValue, column)
        );
}
