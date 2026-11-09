using QuickSql.Sql.Ast;

namespace QuickSql.Sql;

/// <summary>变更守卫的共用形状："库里这一格与行集里那一格是不是不同"在多数家里是同一个三段式——
/// a 与 b 不等、a 空且 b 不空、a 不空且 b 空。需要它的条件是结构性的：那个库的布尔表达式不是一等值，
/// 近似式 (a IS NULL) 与 (b IS NULL) 的比较发不出去（T-SQL 真库报"布尔不能出现在比较符一侧"定下的；
/// Oracle 血统同一条限制，理由同源）。有 IS DISTINCT FROM 的那一族（PG 系与金仓）用不着这里——
/// 一句就够，那颗算子仍留在 BinaryOperator 里由 provider 自选；其余各家或因这条结构性限制、
/// 或因共用层发不出那颗算子（IS DISTINCT FROM 要按版本发）、或因两者皆无证据（三段式是唯一发得出的形状），
/// 都落进三段这一边。</summary>
public static class ChangeGuardShapes
{
    /// <summary>「不同则真」的三段展开。右倾：同种连接词在 DialectBase 里不加括号，
    /// 所以各家出来的都是人写的那种平铺 OR，而黄金样本比的是版式。</summary>
    public static SqlExpression ThreeWay(SqlExpression stored, SqlExpression incoming)
    {
        var different = new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        var storedNull = new SqlUnaryExpression(UnaryOperator.IsNull, stored);
        var incomingNull = new SqlUnaryExpression(UnaryOperator.IsNull, incoming);
        var storedPresent = new SqlUnaryExpression(UnaryOperator.IsNotNull, stored);
        var incomingPresent = new SqlUnaryExpression(UnaryOperator.IsNotNull, incoming);

        var onlyIncoming = And(storedNull, incomingPresent);
        var onlyStored = And(storedPresent, incomingNull);

        return Or(different, Or(onlyIncoming, onlyStored));
    }

    static SqlExpression And(SqlExpression left, SqlExpression right)
        => new SqlBinaryExpression(BinaryOperator.And, left, right);

    static SqlExpression Or(SqlExpression left, SqlExpression right)
        => new SqlBinaryExpression(BinaryOperator.Or, left, right);
}
