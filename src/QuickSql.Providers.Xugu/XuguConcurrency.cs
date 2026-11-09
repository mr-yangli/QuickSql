using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的乐观并发。比较式与另四家逐字同形，分岔只在"谁负责让那一格变"，这一家落"应用给值"侧：
/// 没有 rowversion 那一档可用的读法——库其实收这一型、也会自增，但那一列的值驱动取不回（带它的投影一取行就抛
/// System.DataOutOfBindException），发出去是一列"看着能用"的废列；也没有 ON UPDATE CURRENT_TIMESTAMP
/// 那类列级自动改写子句。所以 TimestampColumn 在这一家只有一种可能：那一列由应用自己写新值。
/// 另记：厂商给了 SYS_UUID() 等取号函数，"库给值"有现成表达式，缺的是契约里"插入不带这一列、事后回读"的槽位。</summary>
public sealed class XuguConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另四家同一个调用点。空值那一档由调用方先折成 IS NULL 再下来：等号在 NULL 上恒为 UNKNOWN，
    /// 每次保存都会成一次假冲突。版本列若是字符串型，CHAR/VARCHAR"存入去尾空格"影响的是守卫那一侧的判据
    /// （见 XuguChangeGuard），不改这里的谓词——两侧都是原值。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
