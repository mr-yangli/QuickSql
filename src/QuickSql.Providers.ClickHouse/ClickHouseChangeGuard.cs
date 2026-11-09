using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的变更守卫。这里的形状选择只服务一条：守住"如果哪天写格被接上，守卫必须已经是对的形状"
/// ——本家写语句本身整格抛，合并退回那一步今天走不到写侧。Compares 恒真：本家发得出型名的列全都可比
///（String 等值可比、数值各档可比），大对象那一档在类型侧就抛了，所以没有"问不出答案"的那一档，不需要名单。</summary>
public sealed class ClickHouseChangeGuard : IChangeGuard
{
    /// <summary>本家没有"不可比的那一型"在场（byte[] 在类型侧抛、String 原生可比），所以恒真。</summary>
    public bool Compares(ColumnContract column) => true;

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走
        //（与另几族逐字同判）。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }
}
