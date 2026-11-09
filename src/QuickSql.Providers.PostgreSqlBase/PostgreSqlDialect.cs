using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的 SQL 文本层。分岔逐条：标识符双引号且大小写敏感（不带引号被折成小写，
/// "QuickSqlOrder"与 quicksqlorder 是两张表）；分页 LIMIT/OFFSET 不要求 ORDER BY，
/// 且"光写 OFFSET 也合法"只有这一族（另两家各补一颗"无上限"哨兵、两颗数不同源）；布尔是真类型，
/// 谓词写 IS TRUE/IS FALSE 而不是 = 1；RETURNING 在整句末尾；UPDATE … FROM 与 DELETE … USING
/// 只吃逗号分隔的表源（连接条件折进 WHERE，只对 INNER 折得动）；恒假/恒真谓词写布尔字面量 false/true——
/// 1 = 0 恰好也合法，写错了不会报错，那种形状必须留在方言里。
/// 类不 sealed：叶子只覆写真正分岔的那几处。本类的产出文本没有在一台真库上跑过。</summary>
public class PostgreSqlDialect(
    DbKind kind,
    PostgreSqlTypes types,
    PostgreSqlKeys keys,
    PostgreSqlValues values,
    PostgreSqlExpressions expressions
) : DialectBase(IdentifierPolicy.PostgreSQL, values, expressions)
{
    public override DbKind Kind { get; } = kind;

    public override char ParameterPrefix => '@';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>恒假/恒真都写成布尔字面量。今天只有恒假那颗有调用点（空集合 IN () 特化）；
    /// AlwaysTruePredicate 在 Core 里 0 处消费——接口注释里写的"合并语句的守卫"不是已接线路径。</summary>
    public override string EmptySetPredicate => "false";

    public override string AlwaysTruePredicate => "true";

    protected override string DeleteJoinKeyword => " USING ";

    /// <summary>IS TRUE / IS FALSE：可空布尔列上 = FALSE 匹配不到 NULL，IS FALSE 也匹配不到——
    /// 两侧同义；但这一家的 = 0 是类型错误而不是三值逻辑问题，所以换的是写法而不是语义。</summary>
    protected override void EmitBooleanEquality(SqlWriter writer, bool value) =>
        writer.Append(value ? " IS TRUE" : " IS FALSE");

    /// <summary>转交给 PostgreSqlKeys.EmitReturning：主键回填那一族在 T-SQL 归方言、在这一系归 IKeyStrategy，
    /// 因为它的形状与"怎么拿回自增值"是同一件事。间距由调用方补，这里只落地那一段。</summary>
    protected override void EmitReturning(
        SqlWriter writer,
        IReadOnlyList<ColumnContract> returning
    ) => keys.EmitReturning(writer, returning);

    /// <summary>LIMIT n OFFSET m：两个子句各自可缺省。与 T-SQL 那一族的关键差别是
    /// OFFSET 不是 FETCH 的前置条件，缺省偏移不必落成常量 0，也不必补占位序。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is { } limit)
        {
            writer.Append(" LIMIT ");
            EmitExpression(limit, writer);
        }

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");
            EmitExpression(offset, writer);
        }
    }

    /// <summary>目标表之外的表源用逗号并列，连接条件折进 WHERE
    /// （判据与"为什么只有 INNER 折得动"在 DialectBase.EmitJoinedSources 那一格）。</summary>
    protected override void EmitJoinedSources(
        SqlWriter writer,
        string keyword,
        IReadOnlyList<JoinClause> joins,
        out SqlExpression? folded
    )
    {
        folded = null;

        for (var i = 0; i < joins.Count; i++)
        {
            var join = joins[i];

            if (join.Kind != JoinKind.Inner)
                throw new QuickSqlException(
                    $"{Kind} 的 {keyword.Trim()} 子句不接受 {join.Kind.ToString().ToUpperInvariant()} JOIN：这一家只吃逗号分隔的表源，"
                        + "连接条件要并进 WHERE，而外连接那样做会把未匹配行整个筛掉（语义就变了）。"
                );

            if (join.On is not { } on)
                throw new QuickSqlException(
                    $"{Kind} 的 {keyword.Trim()} 子句里 {join.Source.Ref.Alias} 缺少 ON 条件：折进 WHERE 需要它本体。"
                );

            writer.Append(i == 0 ? keyword : ", ");
            EmitSource(join.Source, writer);

            folded = folded is null ? on : new SqlBinaryExpression(BinaryOperator.And, folded, on);
        }
    }
}
