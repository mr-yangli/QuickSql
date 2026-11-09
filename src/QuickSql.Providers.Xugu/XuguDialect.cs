using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的 SQL 文本层。分页是句尾 LIMIT n OFFSET m、不要求 ORDER BY，但 OFFSET 单独出现不合法，
/// 所以无 LIMIT 时补哨兵（见 NoLimitSentinel）；表别名没有 AS；写语句的目标不起别名（UPDATE 表 SET …）；
/// 没有 UPDATE … FROM，SupportsJoinedWrites 取 false，合并档整批退回一句一实体；
/// 驱动不发保存点（XuguClient 的 XGTransaction 只覆写了无参 Rollback，带名字的三颗调即 NotSupportedException），
/// 本家改用文本三句——这是"驱动层缺能力、由方言层补文本"的开关，换个覆写了那三颗的驱动翻这一颗就够；
/// 有 RETURNING 且落句尾，转交 XuguKeys.EmitReturning；布尔谓词取基类的 = 1/= 0（布尔列落 TINYINT、装得下 2，
/// IS TRUE 与 = 1 不是同一判据，而库有 TRUE/FALSE 字面量仍不用——基类那对四家共用）。
/// 类是 sealed：这一族没有第二种库种。本层产出的文本在真库上执行过，但"执行过"不等于"每一条都执行过"
/// （键回填那一格红着、按名跳过），也不覆盖"现场驱动是否交付入库的那一颗"。</summary>
public sealed class XuguDialect(XuguTypes types, XuguKeys keys, XuguValues values, XuguExpressions expressions)
    : DialectBase(IdentifierPolicy.Xugu, values, expressions)
{
    /// <summary>OFFSET 单独出现时补的"无上限"哨兵：LIMIT 在这一家有厂商写死的上界 2147483647，超过就报错，
    /// 所以补的是那颗天花板本身而不是一个够大的数（与 MySQL 那颗 2^64−1 同形不同义：那边是"这一型的最大值"）。
    /// 写成文本而不是参数：它不是一格数据，参数预算量的是真值个数。</summary>
    const string NoLimitSentinel = "2147483647";

    public override DbKind Kind { get; } = DbKind.Xugu;

    /// <summary>冒号（取 Oracle 血统那一支；产物里那枚 :XuguParam 是唯一直接证据，而它是参数名不是文本占位符）。
    /// 前缀错了会红在每一条语句上，是本文件里最便宜就能证伪的一格。</summary>
    public override char ParameterPrefix => ':';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>没有 UPDATE … FROM，合并档整批退回一句一实体。这颗开关不是错误豁免：EmitJoinedSources 仍然抛——
    /// 走到那里说明有人绕过了开关，那种时候宁可红。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>驱动不发保存点（带名字的三颗停在 DbTransaction 基类上，一句文本都能收），执行层据此改走文本那条路；
    /// 嵌套事务的语义与另四家相同，动的只是"这句话由谁发"。</summary>
    public override bool SupportsDriverSavepoints => false;

    /// <summary>SAVEPOINT 名字，名字按探针证过的裸名形原样发出、刻意不走 EmitIdentifier（走它会把名字折成带引号的大写形，那一形没有活库见证）。
    /// 裸名是安全的：三口出口读同一个生成名（字母/数字/下划线、非用户输入），折叠一致就既无保留字风险也无注入面——
    /// 一旦有人把用户可控的字符串接成保存点名，"不走 EmitIdentifier"立刻变成洞。</summary>
    public override void EmitSavepoint(SqlWriter writer, string savepointName)
        => writer.Append("SAVEPOINT ").Append(savepointName);

    /// <summary>ROLLBACK TO SAVEPOINT 名字：内层回滚只撤销内层。名字取法同上一口；本家没有 SAVE TRANSACTION 那一形（实测语法错）。</summary>
    public override void EmitRollbackToSavepoint(SqlWriter writer, string savepointName)
        => writer.Append("ROLLBACK TO SAVEPOINT ").Append(savepointName);

    /// <summary>RELEASE SAVEPOINT 名字：内层提交把改动并入外层。这一口不许省——有些库的释放是纯装饰，
    /// 而这里缺了它红点会落在内层提交而不是创建那句上，难查一档。</summary>
    public override void EmitReleaseSavepoint(SqlWriter writer, string savepointName)
        => writer.Append("RELEASE SAVEPOINT ").Append(savepointName);

    /// <summary>目标不起别名，在 SET 与 WHERE 里用自己的表名引用自己，且刻意不带模式前缀
    /// （目标一旦限定到模式，引用侧就只认那个不带模式的名字）。</summary>
    public override string WriteTargetAlias(TableInfo table) => table.Name;

    protected override string SourceAliasKeyword => " ";

    /// <summary>与 WriteTargetAlias 成对：基类默认出口是 UPDATE 表 AS 别名，而本族按产物是 UPDATE 表 SET…，别名无处安放。
    /// EmitDeleteTarget 默认转给这一口，DELETE 半边跟着一起改对。</summary>
    protected override void EmitUpdateTarget(SqlWriter writer, TableRef table) => EmitTableName(writer, table.Table);

    /// <summary>句尾两子句，各自可缺省，但只有 OFFSET 时缺省 LIMIT 不合法（金仓恰好相反），那一支补 NoLimitSentinel。
    /// 逗号形（LIMIT 偏移, 条数）把偏移放前面、与阅读顺序相反，取标准两子句形。</summary>
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

    /// <summary>转交 XuguKeys.EmitReturning：与金仓那一族同一个转交形状，两家共用"INSERT 与 UPDATE 共用一份文案"这一条纪律。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>抛：这一家有 MERGE INTO … USING (…) 的语法，但合并档发的 (VALUES …) AS t(cols) 行集没有语法
    /// （实测 syntax error），行集只能从 USING (SELECT …) 那一格带——将来要接的是 MERGE INTO 而不是 UPDATE … FROM。
    /// 所以消息说的是"没接"而不是"没有"；走到这里说明 SupportsJoinedWrites 被绕过，宁可报错也不悄悄丢表源。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句不接受目标表之外的表源（{keyword.Trim()} 那一段有 {joins.Count} 个）：这一家有 MERGE INTO … USING (…) ON (…) 的语法"
            + "（已发布产物里出现过），但 §12.5 的合并档发的是 (VALUES …) AS t(cols) 那种带列名清单的行集，"
            + "而那一形在这一家没有语法（v0.32 在活库上正问过：SELECT * FROM (VALUES …) AS t(cols) 交 syntax error, unexpected VALUES），所以今天不接。"
            + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
            + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。");
    }

    /// <summary>MERGE 的目标/源别名，不带 AS（与 SourceAliasKeyword 那颗空格同源）。</summary>
    const string MergeTarget = "t0";

    const string MergeSource = "qsrc";

    /// <summary>InsertOrUpdate 发 MERGE INTO … USING (SELECT … FROM dual UNION ALL …)，与 Oracle 血统同形；
    /// 不发 USING (VALUES …)——那一形实测语法错，行集只能从 USING (SELECT …) 带。
    /// 档位要说清：MERGE 骨架在活库上跑过（单行、手工写的那颗），本层产出的多行 UNION ALL、目标别名、
    /// 整句拼接未复点。ON/WHEN 两支由共用层 EmitMergeTail 发，不收分号。</summary>
    public override void Emit(in UpsertStatement upsert, SqlWriter writer)
    {
        writer.Append("MERGE INTO ");
        EmitTableName(writer, upsert.Table.Table);
        writer.Append(' ');
        EmitIdentifier(writer, MergeTarget);

        // USING (SELECT :p0 AS "col0", :p1 AS "col1" FROM dual UNION ALL SELECT …) qsrc
        writer.Append(" USING (");

        for (var row = 0; row < upsert.Rows.Count; row++)
        {
            if (row > 0)
                writer.Append(" UNION ALL ");

            writer.Append("SELECT ");

            var cells = upsert.Rows[row];

            for (var column = 0; column < cells.Count; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                EmitExpression(cells[column]!, writer);
                writer.Append(" AS ");
                EmitIdentifier(writer, upsert.Columns[column].ColumnName);
            }

            writer.Append(" FROM dual");
        }

        writer.Append(") ");
        EmitIdentifier(writer, MergeSource);

        EmitMergeTail(writer, MergeTarget, MergeSource, upsert, semicolon: false);
    }
}
