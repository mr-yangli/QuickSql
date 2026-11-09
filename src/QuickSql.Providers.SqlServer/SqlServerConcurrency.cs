using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.SqlServer;

/// <summary>rowversion 列的乐观锁谓词。SQL Server 的 rowversion 是 8 字节二进制、
/// 由库自动递增，所以比较形态是 [Version] = @p；不要拿 @@DBTS 代替，那是数据库级的、不是行级的。</summary>
public sealed class SqlServerConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.RowVersion;

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
