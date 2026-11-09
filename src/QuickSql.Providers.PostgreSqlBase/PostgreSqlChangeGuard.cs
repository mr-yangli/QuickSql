using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的变更守卫：可空列一句 IS DISTINCT FROM（三值逻辑下两侧同空算相等），
/// NOT NULL 列一句 <> 就够（与 T-SQL 同一条理由：多出来的空值判断让谓词更难走索引）。
/// 宁可退回逐条也不哈希：撞哈希的行会被判成"没变"而把新值静默丢掉，那是丢写。
/// Compares 关 json（没有等值运算符，比较式直接语法错；今天类型映射产不出 json，这一支是护栏）。</summary>
public sealed class PostgreSqlChangeGuard : IChangeGuard
{
    public bool Compares(ColumnContract column)
    {
        // 这一家的 json 型没有等值运算符（jsonb 才有），比较式在它上面直接语法错。
        // 今天的类型映射产不出 json，所以这一支是护栏而不是现状（真实现落地时随位一起重开）。
        return BaseType(column) != "json";
    }

    public SqlExpression BuildDifference(
        ColumnContract column,
        SqlExpression stored,
        SqlExpression incoming
    ) =>
        new SqlBinaryExpression(
            column.IsNullable ? BinaryOperator.IsDistinctFrom : BinaryOperator.NotEqual,
            stored,
            incoming
        );

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：守卫在发射路径上调用，那一趟不保证 ApplyStorageDefaults 已跑完。
        var name = PostgreSqlTypes.Instance.GetStoreType(column);
        var paren = name.IndexOf('(');

        return (paren < 0 ? name : name[..paren]).Trim();
    }
}
