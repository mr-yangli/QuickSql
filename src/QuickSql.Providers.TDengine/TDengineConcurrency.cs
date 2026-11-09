using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的乐观并发，比较式与另几族逐字同形（"Version" = @p）。分岔在谁负责让那一格变：
/// 没有由库自动递增的版本戳那一型，也没有列定义上的自动改写子句，所以 TimestampColumn 在本家说的
/// 只有一种可能——版本列由应用自己写新值。精度那半格：版本列走 DateTime 臂落 TIMESTAMP，
/// 而库侧刻度由建库 PRECISION 参数定（ms/us/ns）——比毫秒粗的库上两次保存落在同一格内时
/// 等号成立、冲突被判成"没发生"（契约里没有"版本列精度"那一格），已知缺口记 notes。</summary>
public sealed class TDengineConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另几族同一个调用点（发射之前合取进 AST 的 WHERE）。空值那一档由调用方先折成
    /// IS NULL 再下来："col" = ? 在 NULL 上恒为 UNKNOWN，不折的话每次保存都会成一次假冲突
    /// ——三值逻辑不是本家的特性。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
