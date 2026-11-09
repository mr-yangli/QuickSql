using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Values;
using QuickSql.Sql.Ast;

namespace QuickSql.Sql;

/// <summary>AST → 文本的共用步法。方言只覆写真正分岔的那几处：分页、UPDATE/DELETE 的目标写法、
/// RETURNING 落点、类型名、标识符引号。之所以住在 Core 而不是各 provider 里：
/// 金仓/openGauss/瀚高共享 PostgreSqlBase，而 provider 之间禁止互引，共用的东西只能往上一层。</summary>
public abstract class DialectBase(IdentifierPolicy identifiers, IValueBinder values, IExpressionRules expressions) : ISqlDialect
{
    /// <summary>报错时要能说出是哪一家，所以每个方言必须报出自己的库种。</summary>
    public abstract DbKind Kind { get; }

    public abstract char ParameterPrefix { get; }

    /// <summary>默认 false = 执行器照旧摘前缀：只有拿到"带前缀那一路有活库见证"的这一家才许翻成 true，
    /// 判据与出处写在 ISqlDialect.DriverAcceptsPrefixedParameterNames。</summary>
    public virtual bool DriverAcceptsPrefixedParameterNames => false;

    public virtual bool PrefixUnicodeLiterals => false;

    public virtual string EmptySetPredicate => "1 = 0";

    public virtual string AlwaysTruePredicate => "1 = 1";

    public virtual bool PagingRequiresOrdering => false;

    /// <summary>默认 true：两个已落地的方言族都有 UPDATE … FROM（T-SQL 与 PG 系各用自己的写法），
    /// 合并档对它们是同一份文本形状。取 false 的那一家必须在 EmitJoinedSources 里抛而不是悄悄丢掉那些
    /// 表源——这一颗只是"要不要走合并那条路"的开关，不是错误豁免。</summary>
    public virtual bool SupportsJoinedWrites => true;

    /// <summary>默认 true：已落地的几家驱动各自覆写了保存点三件套（判据与逐家出处写在
    /// ISqlDialect.SupportsDriverSavepoints）。这一颗也不是错误豁免：取 false 的那一家必须把下面三口
    /// 一起覆写，只改开关会得到一处更靠里的抛。</summary>
    public virtual bool SupportsDriverSavepoints => true;

    /// <summary>没有文本保存点的发射口：SupportsDriverSavepoints 取 true 的那一家永远走不到这里
    /// （执行层在驱动对象上直接调 DbTransaction.Save）。走到这里说明有人把那颗开关翻成 false 而没有给出
    /// 文本——与 EmitReturning 和 EmitJoinedSources 那两格同一条纪律：宁可抛，不要悄悄发一句它不认的文本，
    /// 也不要悄悄什么都不发（后者会得到一只"能提交、能回滚、其实什么都没做"的保存点事务）。</summary>
    public virtual void EmitSavepoint(SqlWriter writer, string savepointName)
        => throw new QuickSqlException($"{Kind} 没有文本保存点的发射口，却被要求创建一只（SupportsDriverSavepoints 取 false 的方言必须覆写 EmitSavepoint，§4）");

    /// <summary>同上一口，必须成对覆写：只覆写创建而不覆写回退，得到的是"内层回滚撤销了外层全部改动"
    /// ——那正是这一格要避免的那个语义，而它不会在任何一次绿测试里现形。</summary>
    public virtual void EmitRollbackToSavepoint(SqlWriter writer, string savepointName)
        => throw new QuickSqlException($"{Kind} 没有文本保存点的回退发射口，却被要求回退到 {savepointName}（SupportsDriverSavepoints 取 false 的方言必须覆写 EmitRollbackToSavepoint，§4）");

    /// <summary>同前两口，第三件。缺它的话红点在内层提交那一句上，而不是在创建那一句上。</summary>
    public virtual void EmitReleaseSavepoint(SqlWriter writer, string savepointName)
        => throw new QuickSqlException($"{Kind} 没有文本保存点的释放发射口，却被要求释放 {savepointName}（SupportsDriverSavepoints 取 false 的方言必须覆写 EmitReleaseSavepoint，§4）");

    protected IdentifierPolicy Identifiers { get; } = identifiers;

    protected IValueBinder Values { get; } = values;

    protected IExpressionRules Expressions { get; } = expressions;

    // ── 语句 ──────────────────────────────────────────────────────────

    public virtual void Emit(in SelectStatement select, SqlWriter writer)
    {
        // 分页有两种形状：在句尾加子句（T-SQL 的 OFFSET..FETCH、PG 系的 LIMIT/OFFSET），或者把整句
        // 包进两层 ROWNUM 过滤（Oracle 系）。第二种必须占一个槽位而不是覆写 EmitPaging：
        // 要包就意味内层那一份不能再有分页尾巴，而 EmitPaging 恰好是在尾巴那个位置上被调用的——
        // 只给它的话，覆写方拿不到"不发尾巴"这件事。
        if (PagingWrapsSelect && (select.Limit is not null || select.Offset is not null))
        {
            EmitWrappedPaging(select, writer);
            return;
        }

        EmitSelectBody(select, writer);

        EmitPaging(writer, select);
    }

    /// <summary>SELECT 的本体：SELECT … FROM … WHERE … GROUP BY … HAVING … ORDER BY，不含分页。
    /// 它是"包装式分页"要套进内层的那一段，所以与 Emit 分开两口。句首那一段分页文本也挂在本方法上
    /// （EmitPagingQualifier）：本方法是"句首"唯一的出口。</summary>
    protected virtual void EmitSelectBody(in SelectStatement select, SqlWriter writer)
    {
        writer.Append("SELECT ");

        EmitPagingQualifier(writer, select);

        if (select.Distinct)
            writer.Append("DISTINCT ");

        EmitProjections(select.Columns, writer);

        if (select.From is { } from)
        {
            writer.Append(" FROM ");
            EmitSource(from, writer);
        }

        EmitJoins(select.Joins, writer);

        if (select.Where is { } filter)
        {
            writer.Append(" WHERE ");
            EmitExpression(filter, writer);
        }

        if (select.GroupBy.Count > 0)
        {
            writer.Append(" GROUP BY ");
            EmitList(select.GroupBy, writer);
        }

        if (select.Having is { } having)
        {
            writer.Append(" HAVING ");
            EmitExpression(having, writer);
        }

        if (select.OrderBy.Count > 0)
            EmitOrdering(select.OrderBy, writer);
    }

    /// <summary>分页的第三种落点：句首的限定词（Informix 血统的 SELECT FIRST n SKIP m）。前两套的槽位都
    /// 接不住它——EmitPaging 长在句尾，包装那一档要的是把整句包住，限定词在本句里面、又不包任何一句。
    /// 默认空实现 = 已落地的几家都没有这一档。走这一支的方言必须把 EmitPaging 收成空体，否则同一份分页
    /// 在句首与句尾各发一遍（与 Oracle 那一对「PagingWrapsSelect ∶ 空的 EmitPaging」同一种成对纪律）。
    /// 参数化的位置在这里也顺带定了：限定词在整句之最前，它登记的参数编号比投影与谓词都小——
    /// 对位置形占位符那一家，"编号序 = 文本序"这一条就是这么保住的。</summary>
    protected virtual void EmitPagingQualifier(SqlWriter writer, SelectStatement select)
    {
    }

    /// <summary>这一家要不要包装式分页。默认 false——两个已落地的方言族都是句尾加子句，
    /// 这个开关对它们的产出零影响。</summary>
    protected virtual bool PagingWrapsSelect => false;

    /// <summary>把 EmitSelectBody 包进分页的外层。声明要包装却不实现这一口的方言必须报错，
    /// 而不是悄悄发一条不分页的 SQL——那会把"取前 5 行"变成全表扫。</summary>
    protected virtual void EmitWrappedPaging(in SelectStatement select, SqlWriter writer)
        => throw new QuickSqlException($"{Kind} 声明了包装式分页却没有实现 {nameof(EmitWrappedPaging)}，宁可报错也不能发一条不分页的 SQL");

    /// <summary>分页的形状各家不同（T-SQL 跟在 ORDER BY 之后、PG 用 LIMIT、DM 要三层嵌套的 ROWNUM），
    /// 所以这里是抽象口。共同的「要不要补占位序」规则在 EmitPagingOrdering。
    /// 走包装那一支的方言不会到这里：它的分页文本在 EmitWrappedPaging 里。</summary>
    protected abstract void EmitPaging(SqlWriter writer, SelectStatement select);

    public virtual void Emit(in InsertStatement insert, SqlWriter writer)
    {
        writer.Append("INSERT INTO ");
        EmitTableName(writer, insert.Table.Table);

        if (insert.ExplicitColumnList)
        {
            writer.Append(" (");
            EmitIdentifiers(insert.Columns, writer);
            writer.Append(')');
        }

        // T-SQL 的 OUTPUT 必须在 VALUES 之前，PG 系的 RETURNING 在语句末尾：同一个位置差就要一个开关。
        // 两侧的空格由调用方补（IKeyStrategy.EmitReturning 只落地那一段，不管上下文）。
        if (ReturningPrecedesValues && insert.Returning.Count > 0)
        {
            writer.Append(' ');
            EmitReturning(writer, insert.Returning);
        }

        writer.Append(" VALUES ");

        for (var row = 0; row < insert.Rows.Count; row++)
        {
            if (row > 0)
                writer.Append(", ");

            var values = insert.Rows[row];

            writer.Append('(');

            for (var column = 0; column < insert.Columns.Count; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                // null 位置走库默认值：多值 VALUES 里只有 DEFAULT 这一个写法能表达「这一行不填这列」。
                if (values[column] is { } value)
                    EmitExpression(value, writer);
                else
                    writer.Append("DEFAULT");
            }

            writer.Append(')');
        }

        if (!ReturningPrecedesValues && insert.Returning.Count > 0)
        {
            writer.Append(' ');
            EmitReturning(writer, insert.Returning);
        }
    }

    protected virtual bool ReturningPrecedesValues => false;

    protected virtual void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
        => throw new QuickSqlException($"{Kind} 未实现 RETURNING/OUTPUT，但请求了主键回填（§4 的 IKeyStrategy）");

    /// <summary>整语句 upsert 的默认：抛。句尾那一族走 IBulkStrategy.UpsertClause 而不是这一颗
    /// （内核先问那一份句尾），只有"没有那颗槽"的一家（SQLite 的 ON CONFLICT、MERGE INTO 那一族）到这里；
    /// 没覆写又没有句尾的家，InsertOrUpdate 在内核那一层就当场拒，走不到这一句的默认抛。
    /// 这一句抛因此是"覆写漏了一家"的编程期信号，不是给现场的常规红。</summary>
    public virtual void Emit(in UpsertStatement upsert, SqlWriter writer)
        => throw new QuickSqlException($"{Kind} 既没接句尾 UpsertClause、也没覆写整语句 upsert 的 emit：InsertOrUpdate 在这一家不可达。");

    /// <summary>MERGE 一族共用的后半段：ON (键配) WHEN MATCHED UPDATE SET WHEN NOT MATCHED INSERT VALUES，
    /// 可选以分号收尾（T-SQL 的 MERGE 必须收分号，Oracle 血统与虚谷不收）。三家各写自己的
    /// MERGE INTO … USING (…) 前半段（源的形状与别名写法是它们的分岔点），尾巴都到这里，一处真相。
    /// 键只进 ON 与 INSERT、不进 UPDATE SET（改自己的键不是整行更新，与句尾族同判）；
    /// UPDATE SET 因此只并 UpsertStatement.Settable，INSERT 并整张 UpsertStatement.Columns（可写 + 键）。</summary>
    protected void EmitMergeTail(SqlWriter writer, string targetAlias, string sourceAlias, in UpsertStatement upsert, bool semicolon)
    {
        writer.Append(" ON (");

        for (var key = 0; key < upsert.Keys.Count; key++)
        {
            if (key > 0)
                writer.Append(" AND ");

            EmitQualified(writer, targetAlias, upsert.Keys[key].ColumnName);
            writer.Append(" = ");
            EmitQualified(writer, sourceAlias, upsert.Keys[key].ColumnName);
        }

        writer.Append(") WHEN MATCHED THEN UPDATE SET ");

        for (var column = 0; column < upsert.Settable.Count; column++)
        {
            if (column > 0)
                writer.Append(", ");

            EmitQualified(writer, targetAlias, upsert.Settable[column].ColumnName);
            writer.Append(" = ");
            EmitQualified(writer, sourceAlias, upsert.Settable[column].ColumnName);
        }

        writer.Append(" WHEN NOT MATCHED THEN INSERT (");
        EmitIdentifierList(writer, upsert.Columns);
        writer.Append(") VALUES (");

        for (var column = 0; column < upsert.Columns.Count; column++)
        {
            if (column > 0)
                writer.Append(", ");

            EmitQualified(writer, sourceAlias, upsert.Columns[column].ColumnName);
        }

        writer.Append(')');

        if (semicolon)
            writer.Append(';');
    }

    /// <summary>一颗「别名.列」：两侧各过一次 EmitIdentifier（引号与折叠归本家策略），中间一个点。</summary>
    protected void EmitQualified(SqlWriter writer, string alias, string column)
    {
        EmitIdentifier(writer, alias);
        writer.Append('.');
        EmitIdentifier(writer, column);
    }

    protected void EmitIdentifierList(SqlWriter writer, IReadOnlyList<ColumnContract> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, columns[i].ColumnName);
        }
    }

    public virtual void Emit(in UpdateStatement update, SqlWriter writer)
    {
        writer.Append("UPDATE ");
        EmitUpdateTarget(writer, update.Table);

        writer.Append(" SET ");

        for (var i = 0; i < update.Sets.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            // SET 左侧一律不带别名前缀：T-SQL 与 PG 在此一致，带上了两家的解析器都不认。
            EmitIdentifier(writer, update.Sets[i].Column.ColumnName);
            writer.Append(" = ");
            EmitExpression(update.Sets[i].Value, writer);
        }

        EmitJoinedSources(writer, " FROM ", update.Joins, out var joined);

        EmitWhere(writer, joined, update.Where);

        // 空格在这里补而不是写进 IKeyStrategy.EmitReturning：那一颗要被 INSERT 与 UPDATE、
        // 被前置的 OUTPUT 与后置的 RETURNING 共用，两侧的间距各不相同。
        // 少一个就得到 `…WHERE @p0 = 1RETURNING "Id"` —— 这一格是第二个方言族接出来才现形的。
        if (!ReturningPrecedesValues && update.Returning.Count > 0)
        {
            writer.Append(' ');
            EmitReturning(writer, update.Returning);
        }
    }

    /// <summary>ANSI 写法：UPDATE [Orders] AS [t0]。T-SQL 不接受目标别名，所以它覆写这三步。</summary>
    protected virtual void EmitUpdateTarget(SqlWriter writer, TableRef table)
    {
        EmitTableName(writer, table.Table);
        writer.Append(" AS ");
        EmitIdentifier(writer, table.Alias);
    }

    public virtual void Emit(in DeleteStatement delete, SqlWriter writer)
    {
        writer.Append("DELETE FROM ");
        EmitDeleteTarget(writer, delete.Table);

        EmitJoinedSources(writer, DeleteJoinKeyword, delete.Joins, out var joined);

        EmitWhere(writer, joined, delete.Where);
    }

    protected virtual void EmitDeleteTarget(SqlWriter writer, TableRef table) => EmitUpdateTarget(writer, table);

    /// <summary>T-SQL 的 DELETE 在目标之后再来一个 FROM，PG 系要 USING。</summary>
    protected virtual string DeleteJoinKeyword => " FROM ";

    /// <summary>FROM 里表源与它别名之间的那颗连接词。Oracle 血统没有 AS：那一家只在列别名上接受 AS，
    /// 表别名只能是「FROM 表 别名」那种挨着写。它不是版式偏好而是"这条语句在这一家过不过解析器"，
    /// 所以留在方言而不是共用层的字面量里。</summary>
    protected virtual string SourceAliasKeyword => " AS ";

    /// <summary>目标表之外的那些表源落成一段子句。keyword 自带前后空格，因为它的形状本身就是分岔点之一
    /// （T-SQL 在 DELETE 后要再来一个 FROM，PG 系要 USING）。folded 是被这次发射消化掉、要并入 WHERE 的条件：
    /// 共用写法把条件留在 ON 上，于是返回 null；PG 系的 UPDATE … FROM / DELETE … USING 只吃逗号分隔的表源
    /// （连着目标表的 JOIN 在那两家都不合法），条件必须挪进 WHERE——那对 INNER JOIN 等价、对外连接不等价，
    /// 所以挪的那一家遇到非 INNER 要抛而不是折。</summary>
    protected virtual void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        writer.Append(keyword);
        EmitJoins(joins, writer);
    }

    /// <summary><c>WHERE</c> 只有一个出口：连接条件在前、调用方的谓词在后。合并语句里这就是
    /// 「先按主键对上，再问值变没变」的阅读顺序，而反过来的话同一条语句的两种写法会比不出差异。</summary>
    void EmitWhere(SqlWriter writer, SqlExpression? joined, SqlExpression? filter)
    {
        if (joined is null && filter is null)
            return;

        writer.Append(" WHERE ");

        if (joined is null)
            EmitExpression(filter!, writer);
        else if (filter is null)
            EmitExpression(joined, writer);
        else
            EmitExpression(new SqlBinaryExpression(BinaryOperator.And, joined, filter), writer);
    }

    // ── 版式 ──────────────────────────────────────────────────────────

    protected void EmitProjections(IReadOnlyList<Projection> columns, SqlWriter writer)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            var projection = columns[i];

            EmitExpression(projection.Node, writer);

            // 只有计算列才需要别名：直接列的名字对物化没有意义（列序才是真相），
            // 多打一个 AS 只会把黄金样本搞脏。
            if (projection.Alias is { Length: > 0 } alias && projection.Node is not ColumnExpression)
            {
                writer.Append(" AS ");
                EmitIdentifier(writer, alias);
            }
        }
    }

    protected void EmitJoins(IReadOnlyList<JoinClause> joins, SqlWriter writer)
    {
        foreach (var join in joins)
        {
            writer.Append(' ');
            writer.Append(join.Kind switch
            {
                JoinKind.Inner => "INNER JOIN ",
                JoinKind.Left => "LEFT JOIN ",
                JoinKind.Right => "RIGHT JOIN ",
                JoinKind.Full => "FULL JOIN ",
                JoinKind.Cross => "CROSS JOIN ",
                _ => throw new QuickSqlException($"{Kind} 未知连接类型 {join.Kind}"),
            });

            EmitSource(join.Source, writer);

            if (join.On is { } on)
            {
                writer.Append(" ON ");
                EmitExpression(on, writer);
            }
            else if (join.Kind != JoinKind.Cross)
            {
                // 无 ON 的非交叉连接在哪家都是语法错误或笛卡尔积，不能悄悄放行。
                throw new QuickSqlException($"{join.Kind} JOIN {join.Source.Ref.Alias} 缺少 ON 条件");
            }
        }
    }

    /// <summary>把一个内层语句包成表源：( … ) 之后由 EmitSource 挂别名。
    /// 括号是这句能当表源用的全部条件，两形（表达式里的相关子查询与规划出来的派生表）共用同一段。</summary>
    void EmitWrapped(SelectStatement inner, SqlWriter writer)
    {
        writer.Append('(');
        Emit(inner, writer);
        writer.Append(')');
    }

    /// <summary>集合运算四种关键字里唯一一颗有方言分岔的：EXCEPT 在 Oracle 血统（含达梦/神通）
    /// 上的别名是 MINUS（语义同格——第一腿有、第二腿没有，两腿各去重后求差）。
    /// 其余三档（UNION ALL / UNION / INTERSECT）任何一家同字，进形状不进方言；
    /// 只有这一档要开口，因为那一族的老版本只认 MINUS。</summary>
    protected virtual string ExceptKeyword => "EXCEPT";

    /// <summary>SourceUnion 的接缝关键字。四个档都是句子级的关键字（进形状不进方言），
    /// 这里只是把 SetOperationKind 翻成文本，分岔只有 ExceptKeyword 那一格。</summary>
    string SetOpKeyword(SetOperationKind operation) => operation switch
    {
        SetOperationKind.UnionAll => "UNION ALL",
        SetOperationKind.Union => "UNION",
        SetOperationKind.Intersect => "INTERSECT",
        SetOperationKind.Except => ExceptKeyword,
        _ => throw new QuickSqlException($"{Kind} 未知集合运算档 {operation}"),
    };

    protected void EmitSource(TableSource source, SqlWriter writer)
    {
        switch (source)
        {
            case SourceTable table:
                EmitTableName(writer, table.Ref.Table);
                break;

            case SourceSubquery subquery:
                EmitWrapped(subquery.Query, writer);
                break;

            case SourceDerived derived:
                EmitWrapped(derived.Query, writer);
                break;

            case SourceUnion union:
                // 一片一条 SELECT，按 Operation 指的关键字串起来，整串占一个表位。这里发的是本体而不是 Emit：
                // 分支带分页尾巴会写成"每片各取 N 行"，而括号里挂 ORDER BY 在 T-SQL 与 Oracle 那一族还是语法错。
                // 序与分页只长在外层，那是编译口的事（QueryCompiler.CompileSplit），本方法只保证发不出别的形状。
                writer.Append('(');

                for (var i = 0; i < union.Branches.Count; i++)
                {
                    if (i > 0)
                        writer.Append(' ').Append(SetOpKeyword(union.Operation)).Append(' ');

                    EmitSelectBody(union.Branches[i], writer);
                }

                writer.Append(')');
                break;

            case SourceRawText raw:
                // 文本一颗不动，只在外面包一层括号——别名那一段在下面与各家同一条路（GBase 8s / Oracle 血统不写 AS）。
                // 参数逐颗登记而不落地名字：名字已经在用户的文本里了，这里要的是让它进参数集合，
                // 否则症状是"文本里有 @min、驱动收不到 @min"那种要到运行期才现形的错。
                writer.Append('(').Append(raw.Sql).Append(')');

                foreach (var parameter in raw.Parameters)
                    writer.RegisterParameter(parameter);

                break;

            case SourceFunction function:
                writer.Append(function.Name).Append('(');

                for (var i = 0; i < function.Arguments.Count; i++)
                {
                    if (i > 0)
                        writer.Append(", ");

                    EmitExpression(function.Arguments[i], writer);
                }

                writer.Append(')');
                break;

            case SourceRows rowSet:
                writer.Append("(VALUES ");

                for (var row = 0; row < rowSet.Rows.Count; row++)
                {
                    if (row > 0)
                        writer.Append(", ");

                    writer.Append('(');
                    EmitList(rowSet.Rows[row], writer);
                    writer.Append(')');
                }

                writer.Append(')');
                break;

            default:
                throw new QuickSqlException($"{Kind} 未知表源形态 {source.GetType().Name}");
        }

        writer.Append(SourceAliasKeyword);
        EmitIdentifier(writer, source.Ref.Alias);

        // 行集的列名只有这一处来源：库不看 VALUES 里的参数叫什么，只看这份清单，
        // 而连接条件与 SET 右侧都按名字引它——清单与 SourceRows.Rows 不同序就是静默串列。
        if (source is SourceRows rows)
        {
            writer.Append(" (");
            EmitIdentifiers(rows.Columns, writer);
            writer.Append(')');
        }
    }

    /// <summary>子句自带前导空格：<c>Emit</c> 的各个环节（FROM/JOIN/WHERE…）都不以空格收尾，
    /// 少打一个就会得到 <c>[t0]ORDER BY</c> 这种「看着像 SQL、解析器不认」的文本。</summary>
    void EmitOrdering(IReadOnlyList<OrderExpression> ordering, SqlWriter writer)
    {
        writer.Append(" ORDER BY ");
        EmitOrderingList(ordering, writer);
    }

    /// <summary>序清单的本体（不含 ORDER BY 那四个字）：外层那句由 EmitOrdering 补前导空格，
    /// OVER 里那一段自己写关键字、前后各留一格——两处共用"逐颗带方向"这一份写法，
    /// 而 ASC 到底写不写正是两族必须一致的那一格。</summary>
    protected void EmitOrderingList(IReadOnlyList<OrderExpression> ordering, SqlWriter writer)
    {
        for (var i = 0; i < ordering.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitExpression(ordering[i].Node, writer);
            writer.Append(ordering[i].Descending ? " DESC" : " ASC");
        }
    }

    /// <summary>行号：ROW_NUMBER() OVER (PARTITION BY … ORDER BY …)。与 QuickFx 日期三口同一档分工——
    /// 共用层出文本，方言只出自己的引号，各家一张模板都不登记。virtual 留的那格不是给"大概相同"的邻居
    /// 覆写用的，是给将来某一家量出分岔用的（函数名大小写敏感的引擎、或走 QUALIFY 的那一系）：
    /// 今天没有一家有分岔的实证，DbCapabilities.WindowFunctions 那一位也不当事分派点用。</summary>
    protected virtual void EmitRowNumber(RowNumberExpression rowNumber, SqlWriter writer)
    {
        writer.Append("ROW_NUMBER() OVER (");

        if (rowNumber.PartitionBy.Count > 0)
        {
            writer.Append("PARTITION BY ");
            EmitList(rowNumber.PartitionBy, writer);
            writer.Append(' ');
        }

        // 序那一半由翻译器保证非空（没有序的行号不是一个问题），所以这里不判空、发得出即成立。
        writer.Append("ORDER BY ");
        EmitOrderingList(rowNumber.OrderBy, writer);

        writer.Append(')');
    }

    /// <summary>Skip/Take 无 OrderBy 时，抛错或补主键序由上游策略决定；走到这里说明策略是 Allow，
    /// 那么要求 ORDER BY 的库必须补一个占位序，否则文本直接语法错。</summary>
    protected void EmitPagingOrdering(SqlWriter writer, SelectStatement select)
    {
        if (select.OrderBy.Count > 0 || !select.RequireOrderingForPaging || !PagingRequiresOrdering)
            return;

        writer.Append(" ORDER BY ");
        EmitOrderingPlaceholder(writer);
    }

    protected virtual void EmitOrderingPlaceholder(SqlWriter writer) => writer.Append("(SELECT NULL)");

    protected void EmitList(IReadOnlyList<SqlExpression> nodes, SqlWriter writer)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitExpression(nodes[i], writer);
        }
    }

    void EmitIdentifiers(IReadOnlyList<ColumnContract> columns, SqlWriter writer)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, columns[i].ColumnName);
        }
    }

    void EmitInValues(IReadOnlyList<SqlExpression> values, SqlWriter writer)
    {
        writer.Append('(');

        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitExpression(values[i], writer);
        }

        writer.Append(')');
    }

    // ── 表达式 ────────────────────────────────────────────────────────

    /// <summary>发射前的最后一道方言改写机会：把 AST 节点换成另一颗再交给 switch。
    /// 默认原样返回；唯一的覆写者是 SqliteDialect——那一家把比较表达式里的 ParameterValue（decimal 值）
    /// 包一层 CAST(@p AS NUMERIC)，因为本家把定点存成 TEXT 而 SUM/AVG 返回 INTEGER，
    /// 存储类排序 INTEGER 小于 TEXT 会让比较静默失败。之所以是"发射前"而不是"翻译期"：
    /// CAST 是 SQL 文本层的事，AST 在翻译期应当保持 provider 无关。</summary>
    protected virtual SqlExpression PrepareForEmission(SqlExpression node) => node;

    protected virtual void EmitExpression(SqlExpression node, SqlWriter writer)
    {
        node = PrepareForEmission(node);

        switch (node)
        {
            case ColumnExpression column:
                EmitColumn(column, writer);
                break;

            case ParameterValue parameter:
                EmitParameter(parameter, writer);
                break;

            case InlineLiteral literal:
                writer.Append(literal.Sql);
                break;

            case SqlBinaryExpression binary:
                EmitBinary(binary, writer);
                break;

            case SqlUnaryExpression unary:
                EmitUnary(unary, writer);
                break;

            case FunctionExpression function:
                EmitFunction(function, writer);
                break;

            case RowNumberExpression rowNumber:
                EmitRowNumber(rowNumber, writer);
                break;

            case CastExpression cast:
                writer.Append("CAST(");
                EmitExpression(cast.Operand, writer);
                writer.Append(" AS ").Append(cast.StoreType).Append(')');
                break;

            case InExpression inExpr:
                EmitExpression(inExpr.Target, writer);
                writer.Append(inExpr.Negated ? " NOT IN " : " IN ");

                if (inExpr.Subquery is { } sub)
                {
                    writer.Append('(');
                    Emit(sub, writer);
                    writer.Append(')');
                }
                else
                {
                    EmitInValues(inExpr.Values!, writer);
                }

                break;

            case ExistsExpression exists:
                writer.Append(exists.Negated ? "NOT EXISTS (" : "EXISTS (");
                Emit(exists.Query, writer);
                writer.Append(')');
                break;

            case ScalarSubqueryExpression scalar:
                writer.Append('(');
                Emit(scalar.Query, writer);
                writer.Append(')');
                break;

            case CaseExpression @case:
                EmitCase(@case, writer);
                break;

            case RawExpression raw:
                writer.Append(raw.Sql);
                break;

            case StarExpression star:
                if (star.Table is { } table)
                {
                    EmitIdentifier(writer, table.Alias);
                    writer.Append('.');
                }

                writer.Append('*');
                break;

            default:
                throw new QuickSqlException($"{Kind} 未知表达式节点 {node.GetType().Name}");
        }
    }

    protected virtual void EmitColumn(ColumnExpression column, SqlWriter writer)
    {
        EmitIdentifier(writer, column.Table.Alias);
        writer.Append('.');
        EmitIdentifier(writer, column.Column.ColumnName);
    }

    /// <summary>参数化的唯一收口：名字由 writer 发号，形状由 IValueBinder 决定。
    /// 谁在这里自己 AddParameter，谁就会造出「文本里有 @p3、集合里没有 p3」这种只能执行期炸的 SQL。</summary>
    protected virtual void EmitParameter(ParameterValue parameter, SqlWriter writer)
        => writer.AppendParameter(Values.CreateParameter(writer.NextParameterName(), parameter.Value, parameter.Hint));

    void EmitCase(CaseExpression @case, SqlWriter writer)
    {
        writer.Append("CASE");

        if (@case.Operand is { } operand)
        {
            writer.Append(' ');
            EmitExpression(operand, writer);
        }

        foreach (var when in @case.Whens)
        {
            writer.Append(" WHEN ");
            EmitExpression(when.When, writer);
            writer.Append(" THEN ");
            EmitExpression(when.Then, writer);
        }

        if (@case.Else is { } @else)
        {
            writer.Append(" ELSE ");
            EmitExpression(@else, writer);
        }

        writer.Append(" END");
    }

    void EmitBinary(SqlBinaryExpression binary, SqlWriter writer)
    {
        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            EmitLogical(binary, writer);
            return;
        }

        if (binary.Operator == BinaryOperator.Like)
        {
            EmitOperand(binary.Left, binary.Operator, writer);
            writer.Append(" LIKE ");
            EmitOperand(binary.Right, binary.Operator, writer, isRight: true);

            // 转义符由翻译器烘进模式串，这里必须把它一起写出来，否则 '\' 变成普通字符。
            writer.Append(" ESCAPE '").Append(Expressions.LikeEscapeCharacter).Append('\'');
            return;
        }

        EmitOperand(binary.Left, binary.Operator, writer);
        writer.Append(' ').Append(OperatorText(binary.Operator)).Append(' ');
        EmitOperand(binary.Right, binary.Operator, writer, isRight: true);
    }

    /// <summary>同种连接词不加括号（翻译器已把 AND/OR 折成右倾扁平链），只在混嵌时加：
    /// 这样黄金样本读起来跟人手写的一样。</summary>
    void EmitLogical(SqlBinaryExpression binary, SqlWriter writer)
    {
        EmitLogicalChild(binary.Left, binary.Operator, writer);

        writer.Append(binary.Operator == BinaryOperator.And ? " AND " : " OR ");

        EmitLogicalChild(binary.Right, binary.Operator, writer);
    }

    void EmitLogicalChild(SqlExpression child, BinaryOperator parent, SqlWriter writer)
    {
        var needsParenthesis = child is SqlBinaryExpression nested
                               && nested.Operator is BinaryOperator.And or BinaryOperator.Or
                               && nested.Operator != parent;

        if (needsParenthesis)
            writer.Append('(');

        EmitExpression(child, writer);

        if (needsParenthesis)
            writer.Append(')');
    }

    void EmitOperand(SqlExpression operand, BinaryOperator parent, SqlWriter writer, bool isRight = false)
    {
        if (operand is SqlBinaryExpression nested && NeedsParenthesis(nested.Operator, parent, isRight))
        {
            writer.Append('(');
            EmitExpression(nested, writer);
            writer.Append(')');
            return;
        }

        EmitExpression(operand, writer);
    }

    /// <summary>优先级更低才加括号；同级只有右操作数需要，因为 SQL 全部左结合，
    /// 而减法/除法不满足结合律（<c>a-(b-c)</c> 不加括号就变成 <c>a-b-c</c>）。</summary>
    static bool NeedsParenthesis(BinaryOperator inner, BinaryOperator outer, bool isRight)
        => Precedence(inner) < Precedence(outer)
           || (isRight && Precedence(inner) == Precedence(outer)
                          && outer is BinaryOperator.Subtract or BinaryOperator.Divide or BinaryOperator.Modulo);

    /// <summary>优先级更低才加括号；同级只有右操作数需要，因为 SQL 全部左结合，
    /// 而减法/除法不满足结合律（<c>a-(b-c)</c> 不加括号就变成 <c>a-b-c</c>）。
    /// <b>数字越大越紧</b>，所以整张表是从 <c>AND/OR</c> 往上排的。</summary>
    static int Precedence(BinaryOperator op) => op switch
    {
        BinaryOperator.And or BinaryOperator.Or => 1,
        // IS DISTINCT FROM 排在比较符之下（更松）：a = b IS DISTINCT FROM c 读作 (a = b) IS DISTINCT FROM c。
        // 出处是 PG 手册 4.1.6（9.5 起才改成这一档，旧版相反）；金仓 V8 内核是 9.2 一线，两家可能不同形，
        // 未实测。今天谁都碰不到：合并守卫把 IS DISTINCT FROM 直接挂在 AND/OR 下面，改这个数不动黄金样本。
        BinaryOperator.IsDistinctFrom => 2,
        BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan
            or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual
            or BinaryOperator.Like => 3,
        BinaryOperator.BitwiseOr => 4,
        BinaryOperator.BitwiseAnd => 5,
        BinaryOperator.Add or BinaryOperator.Subtract => 6,
        _ => 7,
    };

    static string OperatorText(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Modulo => "%",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.LessThan => "<",
        BinaryOperator.LessThanOrEqual => "<=",
        BinaryOperator.GreaterThan => ">",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.IsDistinctFrom => "IS DISTINCT FROM",
        BinaryOperator.BitwiseAnd => "&",
        BinaryOperator.BitwiseOr => "|",
        _ => throw new QuickSqlException("逻辑运算符不该走到 OperatorText：" + op),
    };

    void EmitUnary(SqlUnaryExpression unary, SqlWriter writer)
    {
        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                writer.Append("NOT ");
                EmitBooleanOperand(unary.Operand, writer);
                break;

            case UnaryOperator.Negate:
                writer.Append('-');
                EmitExpression(unary.Operand, writer);
                break;

            case UnaryOperator.IsNull:
                EmitComparisonOperand(unary.Operand, writer);
                writer.Append(" IS NULL");
                break;

            case UnaryOperator.IsNotNull:
                EmitComparisonOperand(unary.Operand, writer);
                writer.Append(" IS NOT NULL");
                break;

            case UnaryOperator.IsTrue:
                EmitComparisonOperand(unary.Operand, writer);
                EmitBooleanEquality(writer, value: true);
                break;

            case UnaryOperator.IsFalse:
                EmitComparisonOperand(unary.Operand, writer);
                EmitBooleanEquality(writer, value: false);
                break;
        }
    }

    /// <summary>PG 系有 IS TRUE，T-SQL 与 Oracle 系没有，只能比 = 1 / = 0；
    /// 可空列上 = 0 匹配不到 NULL，是那一类事故的发生地，所以覆写点留在这里。</summary>
    protected virtual void EmitBooleanEquality(SqlWriter writer, bool value)
        => writer.Append(value ? " = 1" : " = 0");

    void EmitBooleanOperand(SqlExpression operand, SqlWriter writer)
    {
        var needsParenthesis = operand is SqlBinaryExpression nested
                               && nested.Operator is BinaryOperator.And or BinaryOperator.Or;

        if (needsParenthesis)
            writer.Append('(');

        EmitExpression(operand, writer);

        if (needsParenthesis)
            writer.Append(')');
    }

    void EmitComparisonOperand(SqlExpression operand, SqlWriter writer)
    {
        if (operand is SqlBinaryExpression { Operator: BinaryOperator.And or BinaryOperator.Or })
        {
            writer.Append('(');
            EmitExpression(operand, writer);
            writer.Append(')');
            return;
        }

        EmitExpression(operand, writer);
    }

    /// <summary>在一个私有文本上渲染子表达式，再回填到父 writer。参数进同一张表，编号也自然连续
    /// （编号长在那张表上，见 SqlWriter.NextParameterName）——两条分岔共用一份参数集合，
    /// 这是 SqlWriter.Fork 存在的唯一理由。</summary>
    string Sub(SqlWriter writer, SqlExpression node)
    {
        var forked = writer.Fork();

        EmitExpression(node, forked);

        return forked.ToString();
    }

    /// <summary>手写循环而不是 LINQ：闭包会捕获 SqlWriter，而它是 ref struct，捕获即编译错误。</summary>
    string[] RenderArguments(IReadOnlyList<SqlExpression> arguments, SqlWriter writer)
    {
        var rendered = new string[arguments.Count];

        for (var i = 0; i < rendered.Length; i++)
            rendered[i] = Sub(writer, arguments[i]);

        return rendered;
    }

    /// <summary>函数模板 → 文本。位置形占位符那一家在这里多一道核对：RenderArguments 按实参次序把参数
    /// 登记进那张共享表，string.Format 再按模板次序把渲染好的片段放进去——两个次序一旦不一致，带名字的
    /// 方言无感（@p3 跟着文本走，驱动按名字绑），位置形那一家是静默错配（? 按第几颗绑）。所以这一支既不许
    /// 实参重复出现、也不许编号倒序——两种都是名字式方言天天在用而位置式用不了的形状
    /// （CHARINDEX({1}, {0}) 是倒序，SUBSTRING({0}, {1} + 1, LEN({0})) 是重复）。抛而不是发：
    /// 错配的代价是值写到隔壁列上，而这一格没有第二次机会被人在评审里看见，它只在批量的第 3 千行上红。
    /// 判据取 ParameterPrefix 那颗字符而不新立一位布尔：位置形就是 ?，再挂一颗开关只给"两者各说一句话"留位置。</summary>
    protected virtual void EmitFunction(FunctionExpression function, SqlWriter writer)
    {
        if (ParameterPrefix == '?' && !PlaceholdersAscendOnce(function.Template, function.Arguments.Count))
            throw new QuickSqlException(
                $"{Kind} 的参数占位符是位置形 '?'，而函数模板 {function.Template} 的实参编号不是按 0,1,2… 各出现一次："
                + "文本里第几颗 '?' 与参数表里第几颗会错位，绑定的后果是值写到隔壁列上。"
                + "位置式方言要改的是本家 IExpressionRules 里的那一条模板（每个实参恰好出现一次、且按序），"
                + "不是把值内联成字面量绕过去（§12.7 的共享计划与 §5.8 的内联纪律同一格）。");

        writer.Append(QuickFn.Apply(function.Template, RenderArguments(function.Arguments, writer)));
    }

    /// <summary>扫模板里的 {n}：必须恰好是 0,1,2,…,argumentCount-1 且各出现一次。
    /// 双花括号是 string.Format 的转义写法，跳过；本仓库的模板里没有带格式说明符的写法，
    /// 所以遇到非数字的花括号不当作实参引用。</summary>
    static bool PlaceholdersAscendOnce(string template, int argumentCount)
    {
        var expected = 0;

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
                continue;

            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                i++;
                continue;
            }

            var at = i + 1;

            while (at < template.Length && char.IsAsciiDigit(template[at]))
                at++;

            if (at == i + 1)
                continue;

            if (int.Parse(template.AsSpan(i + 1, at - i - 1)) != expected)
                return false;

            expected++;
            i = at - 1;
        }

        return expected == argumentCount;
    }

    // ── 标识符与类型 ──────────────────────────────────────────────────

    public virtual void EmitIdentifier(SqlWriter writer, string identifier) => writer.AppendIdentifier(Identifiers, identifier);

    /// <summary>默认给写语句的目标沿用 t0——与 EmitUpdateTarget 那句"ANSI 写法"是同一件事的两半：
    /// 目标起了别名，引用就得用那个别名。Oracle 血统两半一起换（不能起别名则前缀只能用表名本身），
    /// 所以这一口与 EmitUpdateTarget 是成对覆写的，见 ISqlDialect.WriteTargetAlias。</summary>
    public virtual string WriteTargetAlias(TableInfo table) => QuickSqlOrm.AliasAt(0);

    /// <summary>表名在整个发射层唯一的出口：读、三种写、装载通道都从这里过。
    /// 分表模板不许原样发出去：带着占位符发出去要么得到一句打到不存在的表上的 SQL，
    /// 要么——走过一次 DDL——建出一张名字里真带 {year} 的表。两种都要到运行期才看得见，所以在这里当场抛。</summary>
    public virtual void EmitTableName(SqlWriter writer, TableInfo table)
    {
        if (table.Split is { } split)
            throw new QuickSqlException($"分表实体 {table.Name}（{split.Kind} 档，分片键列 {split.ColumnName}）的表名模板没换算成具体分片就被送去发射——读侧要先给选片范围，写侧按行的分片键值算目标表");

        if (table.Schema is { Length: > 0 } schema)
        {
            EmitIdentifier(writer, schema);
            writer.Append('.');
        }

        EmitIdentifier(writer, table.Name);
    }

    public abstract string GetStoreType(ColumnContract column);
}
