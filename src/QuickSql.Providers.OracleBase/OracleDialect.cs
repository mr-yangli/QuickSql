using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.OracleBase;

/// <summary>Oracle 血统的 SQL 文本层，分岔比第二族更深——不只差句尾子句，连"一句 SELECT 长什么样"都不同。
/// 要点：分页要整句包两层（ROWNUM 在同一层里先于 ORDER BY 赋号，要"排序后再截"必须把排好序的那一句降成内层表源，
/// 共用层的 PagingWrapsSelect 开关由此而来）；表别名不带 AS（只在列别名上接受 AS，过不过解析器的事）；
/// 写语句的目标不能起别名（引用自己只能用表名）；没有 UPDATE … FROM（SupportsJoinedWrites 取 false，
/// 合并档整桶退回一句一实体，EmitJoinedSources 仍抛——那是给手工构造的 AST 与将来的 MERGE INTO 留的落点）；
/// 布尔与 uuid 非原生（没有 IS TRUE，恒假谓词只能是 1 = 0，可空布尔列上 = 0 匹配不到 NULL 的事故也跟着回来）；
/// 标识符发射前折成大写，建表与查询共用同一个归一口。类不 sealed：达梦与神通只覆写真正分岔的那几处。
/// 本类的产出文本没有在一台真库上跑过——每一处"这一家不认 X"都是厂商文档口径。</summary>
public class OracleDialect(
    DbKind kind,
    OracleTypes types,
    OracleKeys keys,
    OracleValues values,
    OracleExpressions expressions
) : DialectBase(IdentifierPolicy.OracleFamily, values, expressions)
{
    public override DbKind Kind { get; } = kind;

    /// <summary>这一族的参数占位符是冒号。同一颗 @ 在这一家是数据库链接运算符、在 T-SQL 是占位符——
    /// 那颗符号两家各说各的话，正说明它必须由方言给而不是共用层猜。</summary>
    public override char ParameterPrefix => ':';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>没有"把另一张表带进写语句"的那种语法（见 EmitJoinedSources 那一条）。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>目标不起别名，所以它在 SET 与 WHERE 里用自己的表名引用自己。
    /// 刻意不带模式前缀：UPDATE sch."ORDERS" SET … WHERE sch."ORDERS"."Id" = :p 在这一家不合法，
    /// 目标一旦限定到模式，引用侧就只认那个不带模式的名字。</summary>
    public override string WriteTargetAlias(TableInfo table) => table.Name;

    protected override string SourceAliasKeyword => " ";

    /// <summary>这一族不认 EXCEPT，差集的关键字是 MINUS（语义同格）；达梦与神通继承这一格。</summary>
    protected override string ExceptKeyword => "MINUS";

    protected override bool PagingWrapsSelect => true;

    /// <summary>转交 OracleKeys.EmitReturning（那里的答案永远是抛）。文案要点名"缺的是哪个槽位"：
    /// "Oracle 没有 RETURNING"是一句会让现场去找 RETURNING … INTO 的错话——那一条要的是 OUT 绑定，本 SPI 表达不了。</summary>
    protected override void EmitReturning(
        SqlWriter writer,
        IReadOnlyList<ColumnContract> returning
    ) => keys.EmitReturning(writer, returning);

    /// <summary>走包装那一支时 Emit 在 EmitWrappedPaging 处就返回了，能到这里的一条必然没有分页。
    /// 这是抽象口的形状要求而不是一条分支：写成"到了这里就抛"会是死代码。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select) { }

    /// <summary>两三层的 ROWNUM 分页，层数是两档：只有 Take 包一层（外层不再需要那个伪列，不产生多余的列）；
    /// 带 Skip 必须再包一层——WHERE ROWNUM ＞ m 直接写在内层永远为空（第 1 行拿到 1 时比较失败就不继续编号），
    /// 中间那层把 ROWNUM 落成一颗真列，代价是外层多带一列且不进任何投影。上界写成 (:skip + :take) 而不是在 CLR 里加好：
    /// 两侧都是参数，加完就不是参数了——那是把一条共享游标的语句换成一条按值独享的语句。
    /// ROWNUM 与比较符之间不加括号：编号在谓词里、不在比较里。</summary>
    protected override void EmitWrappedPaging(in SelectStatement select, SqlWriter writer)
    {
        if (select.Offset is null)
        {
            writer.Append("SELECT * FROM (");
            EmitSelectBody(select, writer);
            writer.Append(") WHERE ROWNUM <= ");

            // 那颗感叹号有闸门背书：DialectBase.Emit 只在 Limit 与 Offset 至少一个非空时才走到这里，
            // 所以"Offset 为空"这一支里的 Limit 必然非空 —— 少那一道闸门这里就得改成抛。
            EmitExpression(select.Limit!, writer);

            return;
        }

        writer.Append("SELECT * FROM (SELECT ");
        EmitIdentifier(writer, PageSource);
        writer.Append(".*, ROWNUM ");
        EmitIdentifier(writer, RowNumber);
        writer.Append(" FROM (");

        EmitSelectBody(select, writer);

        writer.Append(") ");
        EmitIdentifier(writer, PageSource);

        // 只有 Take 的份里没有这一道界，所以这一支的层数与中间谓词是成对出现的。
        if (select.Limit is { } limit)
        {
            writer.Append(" WHERE ROWNUM <= (");
            EmitExpression(select.Offset, writer);
            writer.Append(" + ");
            EmitExpression(limit, writer);
            writer.Append(')');
        }

        writer.Append(") WHERE ");
        EmitIdentifier(writer, RowNumber);
        writer.Append(" > ");

        EmitExpression(select.Offset, writer);
    }

    /// <summary>内层表源的名字。带 $ 是因为它不能与任何业务列同名：外层引的是 "QUICKSQL$PAGE".*，
    /// 撞名的那一列会被库判成"歧义"，而那是一条库里的错而不是这里的错。</summary>
    const string PageSource = "QUICKSQL$PAGE";

    const string RowNumber = "QUICKSQL$ROWNUM";

    /// <summary>ANSI 那一份是 UPDATE 表 AS 别名，T-SQL 是 UPDATE 别名 … FROM 表 AS 别名，
    /// 这一族两个都不是：目标位只有一张表，别名那颗参数无处安放。DELETE 共用这一口：
    /// 这一家的 DELETE FROM 表 与 UPDATE 表 在"目标位不接受别名"这一条上同形。</summary>
    protected override void EmitUpdateTarget(SqlWriter writer, TableRef table) =>
        EmitTableName(writer, table.Table);

    /// <summary>这一族没有 UPDATE … FROM 也没有 DELETE … USING，带表源的写语句在这里抛而不是悄悄丢掉那些表源：
    /// 一条少了行集的合并 UPDATE 会把同一个值写给整张表，那是最坏的一种静默退化。与 EmitUpdateTarget 是同一条分岔的两半
    /// （不能起别名，也就不能带别的表进来）；DialectBase.EmitJoinedSources 本是覆写点，这里的答案是"带不了，等 MERGE INTO"。</summary>
    protected override void EmitJoinedSources(
        SqlWriter writer,
        string keyword,
        IReadOnlyList<JoinClause> joins,
        out SqlExpression? folded
    )
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句不接受额外的表源：这一家没有 {keyword.Trim()} 这种子句，"
                + "带别的表的更新要写成 MERGE INTO … USING (…) ON (…) WHEN MATCHED THEN …，本版本未接（§12.5 末尾那一格）。"
                + "日常路径不会到这里 —— SupportsJoinedWrites 为 false 时 SaveChanges 整桶退回一句一实体，"
                + "语义与合并档等价（§12.4 第 1 项），代价是那 "
                + joins.Count
                + " 组表源所代表的行各占一次往返。"
        );
    }

    /// <summary>源在本家起的别名（目标另起 t0）。两家都是不带 AS 的表别名——MERGE 的两个别名都是表位。</summary>
    const string MergeTarget = "t0";

    const string MergeSource = "qsrc";

    /// <summary>InsertOrUpdate 在本血统的发射：MERGE INTO … USING (SELECT … FROM dual UNION ALL …) …。
    /// 这一家没有 (VALUES …) 那种行集构造子，多行只能写成一颗 SELECT … FROM dual 的 UNION ALL 串
    /// （每颗格是一枚参数、以一个列别名命名，后面的 ON ∶ SET ∶ INSERT VALUES 都引那别名）；
    /// ON 与两支的去向由共用层那份 EmitMergeTail 发，不收分号。目标位起别名——这一家只有 UPDATE 的目标不能起别名，MERGE 能。
    /// 形态是厂商文档口径、未在一台真库上跑过；Oracle MERGE 的老坑"WHEN MATCHED UPDATE SET 不能改 ON 里用到的那一列"
    /// （ORA-38104）构造上不会踩：键只进 ON 与 INSERT，SET 只有可写列。</summary>
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
