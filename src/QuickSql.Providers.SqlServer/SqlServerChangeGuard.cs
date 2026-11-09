using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.SqlServer;

/// <summary>T-SQL 的变更守卫。合并语句靠它把"这一行的新值库里已经有了"的行挡在 UPDATE 之外，
/// 否则一条 UPDATE ... FROM (VALUES) 的 @@ROWCOUNT 是命中数而不是变化数，审计列会跟着全表刷新。
/// T-SQL 没有 IS DISTINCT FROM，近似式 (a IS NULL) &lt;&gt; (s.a IS NULL) 又连语法都过不了
/// （布尔表达式不是一等值，真库用例钉住了这个报错），所以可空列展开成三段 OR：不等、只有表侧空、
/// 只有行集侧空——形状住在 ChangeGuardShapes，这一家只留"要不要用那三段"的判断。
/// 不可比的那几型返回 false 而不是造 CHECKSUM 近似：哈希会撞，撞上的行被判成「没变」、新值被静默丢掉
/// ——那是丢写，比不合并严重一个量级；调用方拿到 false 要把这一批退回逐条。</summary>
public sealed class SqlServerChangeGuard : IChangeGuard
{
    /// <summary>连等号都不接受的存储类型（LOB 三兄弟 + xml + 两套空间型 + sql_variant）。
    /// 名字取 ( 之前的那一段，所以 XML 与 xml、带长度的 VARCHAR(n) 与不带的都能命中。
    /// 并发令牌现在是整型 Version 或 AppSetsValue 列，不再是 binary(8) 的 rowversion。</summary>
    static readonly FrozenSet<string> Incomparable = new[]
    {
        "text",
        "ntext",
        "image",
        "xml",
        "geography",
        "geometry",
        "sql_variant",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(
        ColumnContract column,
        SqlExpression stored,
        SqlExpression incoming
    )
    {
        var different = new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引。
        if (!column.IsNullable)
            return different;

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 Hint：后者只有 ApplyStorageDefaults 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完。
        var name = SqlServerTypes.Instance.GetStoreType(column);
        var paren = name.IndexOf('(');

        return (paren < 0 ? name : name[..paren]).Trim();
    }
}
