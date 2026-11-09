using QuickSql.Query.Model;
using QuickSql.Sql.Ast;

namespace QuickSql.Translation;

/// <summary>一条分组链在翻译侧的形状：分组来自哪张表、按哪些表达式分的组。SQL 侧的真相（GROUP BY 的表达式）
/// 在 QueryModel.GroupBy 里，本记录不复述；它只记「g.Key 该落回哪颗表达式」这一件翻译时才要用的事，
/// 因此不进模型、不进 StableKey 的规范文本。Table 是分组那张表位——g.Sum(o => o.Amount) 的 o 就绑在它上面，
/// 聚合实参是普通列、不需要相关子查询；Key 是单列键（g.Key 的落点），复合键时为 null；
/// Members 是复合键按成员名索引的键表达式（只到一层），与 Key 互斥。</summary>
internal readonly record struct GroupingShape(
    TableRef Table,
    SqlExpression? Key,
    IReadOnlyDictionary<string, SqlExpression>? Members)
{
    /// <summary>g.Key 的落点。复合键在这里不能给一个值：那个匿名对象在库里没有任何对应物，
    /// 静默挑第一颗键列会让 Select(g => new { K = g.Key }) 交回一列看起来对、其实少了一半条件的结果。</summary>
    public SqlExpression KeyOrThrow(string renderedLambda)
        => Key ?? throw new UntranslatableExpressionException(renderedLambda,
            "分组键是复合键（GroupBy 里给了 new { … }），g.Key 整体不是一个值：请选出具体成员 g.Key.<名字>");
}
