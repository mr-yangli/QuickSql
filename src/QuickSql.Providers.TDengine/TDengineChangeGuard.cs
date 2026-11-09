using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的变更守卫。这里的形状选择只服务一条：守住"如果哪天写格被接上，守卫必须已经是对的形状"
/// ——本家写语句本身整格抛，合并退回那一步今天走不到写侧。Compares 恒真：本家发得出型名的列全都可比
///（VARCHAR 等值可比、数值各档可比），大对象那一档在类型侧就抛了（本家没有 TEXT/CLOB 那一支），
/// 所以没有"问不出答案"的那一档，不需要名单。</summary>
public sealed class TDengineChangeGuard : IChangeGuard
{
    /// <summary>本家没有"不可比的那一型"在场（byte[] 落 VARBINARY 原生可比、string 落 VARCHAR 可比），所以恒真。</summary>
    public bool Compares(ColumnContract column) => true;

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // 列定义文法里没有 NOT NULL 那一颗（发射侧本家一律不发），可空性在库里是事实而不是声明——
        // 两侧都可能 NULL，所以三段形状对每一列都成立；多出来的两段 OR 只会让谓词更难走的那半
        // 论证（NOT NULL 列上一句 <> 就够）在本家没有成立的前提。
        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }
}
