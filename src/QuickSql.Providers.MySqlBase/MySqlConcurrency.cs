using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的乐观并发。rowversion 支持已移除：并发令牌只有"应用自维护"这一条路，
/// 整型 Version 由 ORM 在 SET 里 +1，AppSetsValue 一族由应用给新值。MySQL 的
/// DATETIME … ON UPDATE CURRENT_TIMESTAMP 仍是列定义上的一个子句，但它不再作为"库给令牌"接入——
/// 只写 DDL 不接契约就是静默的假乐观锁：保存永远成功而冲突永不发生。</summary>
public sealed class MySqlConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另四家同一个调用点（发射之前合取进 AST 的 WHERE）。NULL 档由调用方先折成 IS NULL：
    /// col = NULL 恒为 UNKNOWN，每次保存都成假冲突。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
