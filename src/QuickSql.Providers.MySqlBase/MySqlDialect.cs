using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的 SQL 文本层。与各族分岔逐条：标识符反引号（五家唯一，引号字符不随会话模式分岔——
/// ANSI_QUOTES 只把双引号变成字符串字面量引号，反引号两种模式都只作标识符引号）；分页 LIMIT n OFFSET m
/// 不要求 ORDER BY，但 OFFSET 不能单独出现（缺 LIMIT 时补 2^64-1 哨兵）；写语句不接受目标表之外的任何表源
/// （没有 UPDATE … FROM，也没有带列名清单的行集，共用层的合并档在这里落不成一句）；没有 RETURNING
/// （转交 MySqlKeys.EmitReturning 那一抛，INSERT 与 UPDATE 两档共用同一份带出处的文案）；
/// 布尔谓词与恒假/恒真刻意不覆写（TINYINT(1) 装得下 2，IS TRUE 判 2 为真而 = 1 判假；1 = 0/1 = 1 逐字合法）。
/// 类不 sealed：派生叶子只覆写真正分岔的那几处。本类的产出文本没有在一台真库上跑过。</summary>
public class MySqlDialect(DbKind kind, MySqlTypes types, MySqlKeys keys, MySqlValues values, MySqlExpressions expressions)
    : DialectBase(IdentifierPolicy.MySql, values, expressions)
{
    /// <summary>OFFSET 单独出现时的"无上限"哨兵：这一家的语法要求 OFFSET 必须跟在 LIMIT 之后，
    /// 而 LIMIT 吃无符号大整数，"全部"的写法就是 2^64-1。写成文本而不是参数：它不是一格数据，
    /// 占一颗参数位没有意义，且参数预算量的是真值个数——混进来会让 RowsPerStatement 少算一行。</summary>
    const string NoLimitSentinel = "18446744073709551615";

    public override DbKind Kind { get; } = kind;

    public override char ParameterPrefix => '@';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>取 false 的判据见类注释。这颗开关不是错误豁免：共用层据此整批退回一句一实体，
    /// 而 EmitJoinedSources 仍然抛——走到那里说明有人绕过了开关，那种时候宁可红。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>LIMIT n OFFSET m：两个子句各自可缺省，但只有 OFFSET 时缺省 LIMIT 不合法
    /// （金仓那族恰好相反：OFFSET 可以单独出现）。逗号形 LIMIT m, n 把偏移放在前面，与阅读顺序相反，
    /// 取标准形——与金仓逐字同形，少一处假分岔。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is null && select.Offset is null)
            return;

        writer.Append(" LIMIT ");

        if (select.Limit is { } limit)
            EmitExpression(limit, writer);
        else
            writer.Append(NoLimitSentinel);

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");
            EmitExpression(offset, writer);
        }
    }

    /// <summary>转交给 MySqlKeys.EmitReturning（与金仓那族同一个转交形状）：基类默认文案点名"未实现"，
    /// 这一家要点名缺的是 LAST_INSERT_ID() 那颗槽位，所以转交而不留默认那句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>"任何表源都不接受"：来了就抛，keyword 连看都不看。与 Oracle 血统同抛而理由不同条：
    /// 那一族目标不能起别名，这一家目标能起别名，缺的是"把别的表带进来"的子句本身。
    /// 不学金仓把 INNER 条件折进 WHERE：那一家只吃逗号表源，折一下语义不变；这一家连逗号表源都没有
    /// （多表 UPDATE 要 SET 里带表名前缀的另一整条语法）。折得动与否不由引号决定，由有没有那个子句决定。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句不接受目标表之外的表源（{keyword.Trim()} 那一段有 {joins.Count} 个）：这一家没有 UPDATE … FROM / DELETE … USING，"
            + $"也没有 (VALUES …) AS t(cols) 那种带列名清单的行集，所以 §12.5 的合并档发不成一句。"
            + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
            + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。");
    }
}
