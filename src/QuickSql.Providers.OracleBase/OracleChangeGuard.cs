using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using System.Collections.Frozen;

namespace QuickSql.Providers.OracleBase;

/// <summary>Oracle 血统的变更守卫。这一家没有 IS DISTINCT FROM，比较式与 T-SQL 同档走三段式展开（共用 ChangeGuardShapes）；
/// 这一家 '' 就是 NULL："库里那格是空串、新值写了 NULL"在守卫眼里是没变（另四家判"变了"——那两格在库里本来就是同一个值），
/// 跨库对账时这一档不是同一条判定。与各族同一立场：宁可退回逐条也不用 DBMS_LOB.COMPARE 或哈希近似——
/// 撞了哈希的行会被判成"没变"而把新值静默丢掉，那是丢写。</summary>
public sealed class OracleChangeGuard : IChangeGuard
{
    /// <summary>比较式里用不了的型：四颗 LOB（BFILE 是"指向文件的指针"，等号同样没有定义）。
    /// 没有 LONG：那一型在新建表里已被厂商弃用，能不能进 WHERE 取决于版本，现场没有一台库能证；
    /// 真撞上了会红在发射期，比悄悄少比较一列好。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "clob", "nclob", "blob", "bfile" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引 ——
        // 与 T-SQL 那一家逐字同判，所以两家的合并语句在"只改了 NOT NULL 列"这一档上同形（只差引号与前缀）。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：后者只有 ApplyStorageDefaults 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完。切在第一个 '(' 或空格之前落基名，LOB 四颗都不带限定词。
        var name = OracleTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
