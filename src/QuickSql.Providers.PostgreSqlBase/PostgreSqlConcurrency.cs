using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的乐观并发：没有 rowversion 那种由库自动递增的类型，TimestampColumn 说的是
/// "应用或触发器维护的一列"。这是本方言族"能建、能比，但库不会替你维护"的能力：
/// 不配 BEFORE UPDATE 触发器或应用侧赋值，保存永远成功而冲突永不发生——静默的假乐观锁，
/// 触发器生成还没接线。今天"能建能比库不维护"是四家的处境，而"库为什么不维护"在三条不同的路上。</summary>
public sealed class PostgreSqlConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与 T-SQL 同一个调用点（发射之前合取进 AST 的 WHERE）。NULL 档由调用方先折成 IS NULL：
    /// "col" = NULL 恒为 UNKNOWN，每次保存都成假冲突。</summary>
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
