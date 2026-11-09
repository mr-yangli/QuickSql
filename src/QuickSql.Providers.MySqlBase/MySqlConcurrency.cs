using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的乐观并发。没有 rowversion，但有 DATETIME(3) … ON UPDATE CURRENT_TIMESTAMP(3)
/// 这一档库自动改写的列子句（金仓/Oracle 要建 BEFORE UPDATE 触发器，这里它是列定义上的一个子句）。
/// 今天落在"应用给值"那一半：发的 DDL 没有那个子句，契约也没有"这一列归库给"的格子——
/// 要接需两步（类型映射置 StoreFillsValue、CodeFirst 写子句），而第一步会让那一列从 INSERT 列清单里掉出去，
/// 没打算用库维护的现场会突然插不进那一列，故先不置。只写 DDL 不给那一列赋值是静默的假乐观锁：保存永远成功而冲突永不发生。</summary>
public sealed class MySqlConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另四家同一个调用点（发射之前合取进 AST 的 WHERE）。NULL 档由调用方先折成 IS NULL：
    /// col = NULL 恒为 UNKNOWN，每次保存都成假冲突。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
