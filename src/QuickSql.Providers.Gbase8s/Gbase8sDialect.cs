using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的 SQL 文本层。分页是第四套：句首限定词 SELECT FIRST n SKIP m——
/// 它不在句尾也不包整句，共用层为前两套留的槽位接不住，EmitPagingQualifier 是为此新立的槽位
/// （六家里唯一的覆写者）。占位符是位置形 ? 且是唯一的一支：参数名字不进文本，EmitParameter 只负责
/// "登记参数 + 落一颗 ?"；限定词在句首使 FIRST/SKIP 两颗参数比投影与谓词先登记，
/// "编号序 = 文本序"是"槽位选在句首"顺手保住的耦合，别破坏。
/// 其余：没有 N' 宽字符字面量（codeset 建库时定，那一形压根不存在）；表别名没有 AS
/// （列别名的 AS 合法，只省 FROM 那一支）；没有 UPDATE … FROM，SupportsJoinedWrites 取 false
/// （有 WHERE EXISTS 那条等价路——"能力有、形状不对"）；布尔谓词取基类的 = 1/= 0（布尔列落 INTEGER）。
/// 本类产出的每一条文本都没在活库上跑过且本机没有可跑的库——每一格分岔都留了重开触发。</summary>
public sealed class Gbase8sDialect(Gbase8sTypes types, Gbase8sKeys keys, Gbase8sValues values, Gbase8sExpressions expressions)
    : DialectBase(IdentifierPolicy.Gbase8s, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.Gbase8s;

    /// <summary>问号，且是唯一的一支。这是本文件里最贵的一格：前缀错了在别家红在"名字对不上"，
    /// 在这一家红在值绑定到隔壁列（位置形没有第二道校验）。EmitFunction 那道核对管函数模板里的实参次序，
    /// 管不到语句骨架的发射次序——骨架那一边由 DialectBase 单一出口保证。</summary>
    public override char ParameterPrefix => '?';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>没有 UPDATE … FROM，合并档整批退回一句一实体。这颗开关不是错误豁免：EmitJoinedSources 仍然抛——
    /// 走到那里说明有人绕过了开关，那种时候宁可红。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>目标不起别名，在 SET 与 WHERE 里用自己的表名引用自己。来路比同形两家弱一档
    /// （没有产物可抄，取"与两家同形 + 手册 UPDATE 那页没出现过目标关联名"两条），"没出现"不等于"不许"
    /// 的那半边论证继承 XuguDialect 的取向。</summary>
    public override string WriteTargetAlias(TableInfo table) => table.Name;

    protected override string SourceAliasKeyword => " ";

    /// <summary>与 WriteTargetAlias 成对：基类默认出口是 UPDATE 表 AS 别名，本家按 UPDATE 表 SET…。
    /// EmitDeleteTarget 默认转给这一口，DELETE 半边跟着一起改对。</summary>
    protected override void EmitUpdateTarget(SqlWriter writer, TableRef table) => EmitTableName(writer, table.Table);

    /// <summary>InsertOrUpdate 没有 MERGE 可用，发两条：UPDATE 置值 WHERE 键等式，紧跟
    /// INSERT … SELECT … FROM systables WHERE tabid = 1 AND NOT EXISTS（没命中才插）——
    /// systables WHERE tabid = 1 是这一族惯用的单行源。
    /// 三处未证，重开触发是连一台活库跑绿这一对（尤其分号批）：① 多行即一个分号批，
    /// 而多语句批在本家没有任何读数（WHERE EXISTS 这形状天生要两条一起走，退不开）；
    /// ② 目标不起别名，三处目标引用都用表名本身；③ 位置形参数——同一行的键在三处各落一颗 ?
    /// （各登记一颗同值参数），语义对而参数偏多，是没 CTE 可共享行集的代价。</summary>
    public override void Emit(in UpsertStatement upsert, SqlWriter writer)
    {
        var table = upsert.Table.Table;
        var settable = upsert.Settable.Count;

        for (var row = 0; row < upsert.Rows.Count; row++)
        {
            // 每一行是一对独立语句：行与行之间也要用分号隔开（一对内部 UPDATE 与 INSERT 已带一颗分号）。
            if (row > 0)
                writer.Append("; ");

            var cells = upsert.Rows[row];

            // 一条：命中就改。SET 只并可写列，键只进 WHERE（改自己的键不是整行更新）。
            writer.Append("UPDATE ");
            EmitTableName(writer, table);
            writer.Append(" SET ");

            for (var column = 0; column < settable; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                EmitIdentifier(writer, upsert.Settable[column].ColumnName);
                writer.Append(" = ");
                EmitExpression(cells[column]!, writer);
            }

            writer.Append(" WHERE ");
            EmitWhereKeys(writer, upsert, cells, settable);

            writer.Append("; INSERT INTO ");
            EmitTableName(writer, table);
            writer.Append(" (");
            EmitColumnList(writer, upsert);
            writer.Append(") SELECT ");

            for (var column = 0; column < upsert.Columns.Count; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                EmitExpression(cells[column]!, writer);
            }

            writer.Append(" FROM systables WHERE tabid = 1 AND NOT EXISTS (SELECT 1 FROM ");
            EmitTableName(writer, table);
            writer.Append(" WHERE ");
            EmitWhereKeys(writer, upsert, cells, settable);
            writer.Append(")");
        }
    }

    /// <summary>键等式串（多键合取）。键格排在可写列之后，所以从 settable + j 取。</summary>
    void EmitWhereKeys(SqlWriter writer, in UpsertStatement upsert, IReadOnlyList<SqlExpression?> cells, int settable)
    {
        for (var key = 0; key < upsert.Keys.Count; key++)
        {
            if (key > 0)
                writer.Append(" AND ");

            EmitIdentifier(writer, upsert.Keys[key].ColumnName);
            writer.Append(" = ");
            EmitExpression(cells[settable + key]!, writer);
        }
    }

    /// <summary>整张写入清单（可写 + 键）的列名串，供 INSERT INTO 表 (…) 用。</summary>
    void EmitColumnList(SqlWriter writer, in UpsertStatement upsert)
    {
        for (var column = 0; column < upsert.Columns.Count; column++)
        {
            if (column > 0)
                writer.Append(", ");

            EmitIdentifier(writer, upsert.Columns[column].ColumnName);
        }
    }

    /// <summary>登记参数而不落地它的名字，只落一颗 ?——本家与另五家在文本层唯一一处结构性不同。
    /// 编号仍按基类那一条问，hint 与内核侧编号纪律一处不动。顺序纪律：先登记、后落文本——
    /// 反过来不会错，但调试输出里"文本里有几颗 ?"与"表里有几颗参数"会对不上，
    /// 而这一格对不上在本家没有别的查得出它的地方。</summary>
    protected override void EmitParameter(ParameterValue parameter, SqlWriter writer)
    {
        writer.RegisterParameter(Values.CreateParameter(writer.NextParameterName(), parameter.Value, parameter.Hint));

        writer.Append('?');
    }

    /// <summary>句首限定词：FIRST n（上限）与 SKIP m（偏移），各自独立可缺省。只有 SKIP 时不补"无上限"哨兵——
    /// 缺省那一支本身就是无上限。次序 FIRST 在前、SKIP 在后，两数反着写是语法错；
    /// "编号序 = 文本序"就是靠这一行守住的。两支都缺省时整段不发。</summary>
    protected override void EmitPagingQualifier(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is null && select.Offset is null)
            return;

        if (select.Limit is { } limit)
        {
            writer.Append("FIRST ");

            EmitExpression(limit, writer);

            writer.Append(' ');
        }

        if (select.Offset is { } offset)
        {
            writer.Append("SKIP ");

            EmitExpression(offset, writer);

            writer.Append(' ');
        }
    }

    /// <summary>空体：本家的分页文本全在 EmitPagingQualifier 那一段。两口成对覆写，改一处必看另一处——
    /// 否则同一份分页会在句首与句尾各发一遍（"两条子句都合法却发错"的一类）。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
    }

    /// <summary>转交 Gbase8sKeys.EmitReturning（那里抛）。宁可当场红，也不发一条"看着像在回填"的语句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>抛。措辞要能自己答"这一家怎么多表更新"：没有 UPDATE … FROM，但有 WHERE EXISTS 的等价路——
    /// 只是共用层不发那种形状。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句不接受目标表之外的表源（{keyword.Trim()} 那一段有 {joins.Count} 个）：这一家没有 UPDATE … FROM 那种子句，"
            + "也没有把行集带进写语句的形状（§12.5 合并档发的是 (VALUES …) AS t(cols) 那一形）。"
            + "这一家能到同一目的地的是 UPDATE … WHERE EXISTS (SELECT …)【文档·IBM】，而共用层不发那种形状。"
            + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
            + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。");
    }
}
