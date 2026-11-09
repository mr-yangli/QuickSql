using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.OracleBase;

/// <summary>Oracle 血统的乐观并发，比较式与各族同形（"Version" = :p）。分岔在这一家没有 rowversion：
/// ORA_ROWSCN 伪列不是列（目录里读不到、建表声明不了、默认按块共享），于是落在 TimestampColumn——
/// 版本列的值归应用或触发器维护。与金仓那一族同一条警告：不配 BEFORE UPDATE 触发器也不在应用侧赋值时，
/// 保存永远成功而冲突永远不发生，那是静默的假乐观锁。既然库不维护那一列，实体上它多半是 int 或 DateTime，
/// 而 DateTime 在这一族截到毫秒之后一定会撞——同一次保存里两行拿到同一个 DateTime.Now 是常态。</summary>
public sealed class OracleConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与各族同一个调用点（发射之前合取进 AST 的 WHERE）。NULL 那一档由调用方先折成 IS NULL：
    /// "col" = NULL 恒为 UNKNOWN（三值逻辑——虽然这一家的空串就是 NULL，那颗 NULL 也仍然比不出真），
    /// 不折的话每次保存都会成假冲突。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
