using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s 的乐观并发。比较式与五家逐字同形，落"应用给值"侧——第一家连"库给值"的语法都没读到：
/// 没有 rowversion 那一型（存储型可按名字穷举，"没读到"与"没有"差距比虚谷小但仍是差距），
/// 也没有 ON UPDATE CURRENT_TIMESTAMP 那类列级自动改写子句（DEFAULT 只在插入未给值时生效）。
/// CURRENT 与 DBINFO('utc_current') 两颗"库给值"表达式有，缺的是契约里"插入不带这一列、事后回读"的槽位。
/// 本家特有的两处防坑：占位符是位置形 ?，谓词里那颗参数是哪一颗只由编号回答——改发射顺序会把
/// 两个值互换而不报错；版本列若落 DATETIME YEAR TO FRACTION(3)（毫秒），而 CLR 的 DateTime 带 100 纳秒刻度，
/// 同毫秒内两次保存的冲突会被判成"没发生"（契约里没有"版本列精度"那一格，这是共用层缺口）。</summary>
public sealed class Gbase8sConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另五家同一个调用点。空值那一档由调用方先折成 IS NULL 再下来：等号在 NULL 上恒为 UNKNOWN，
    /// 每次保存都会成一次假冲突。本家也收 CHAR，但比较时是否忽略尾部空格没有读到（挂住不猜），
    /// 谓词两侧都是原值。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
