using System.Text;
using QuickSql.Sql.Ast;

namespace QuickSql.Query.Model;

/// <summary>把模型渲染成与方言无关的规范文本。两个用途：等价性判据（两个模型的规范文本必须逐字符相等），
/// 以及 Explain() 里给人看的模型段。刻意不复用 SqlWriter——那是方言出口，
/// 格式会随 provider 变（引号、分页写法），规范文本必须跨库稳定。</summary>
static class QueryModelPrinter
{
    public static string Print(QueryModel model)
    {
        var b = new StringBuilder(256);

        for (var i = 0; i < model.Tables.Count; i++)
        {
            var table = model.Tables[i];
            b.Append(i == 0 ? "FROM " : "TABLE ").Append('t').Append(i).Append(" = ")
             .Append(table.Table.Schema ?? "dbo").Append('.').Append(table.Table.Name)
             .Append(" AS ").Append(table.Alias).Append('\n');
        }

        // 表源的两种非默认来路都要在文本里看得见，否则等价性测试会漏掉一整类差异。
        if (model.Source is SourceRawText raw)
            b.Append("  SOURCE RAW[").Append(raw.Sql).Append("]{")
             .Append(string.Join(", ", raw.Parameters.Select(p => p.Name))).Append("}\n");

        // 合并的形状（关键字与两条腿各自的全文）要在文本里看得见；关键字打规范形（EXCEPT 不打 MINUS）。
        if (model.Source is SourceSetOps setOps)
        {
            b.Append("  SOURCE ").Append(setOps.Operation switch
            {
                SetOperationKind.UnionAll => "UNION ALL",
                SetOperationKind.Union => "UNION",
                SetOperationKind.Intersect => "INTERSECT",
                SetOperationKind.Except => "EXCEPT",
                _ => throw new QuickSqlException($"未知集合运算档 {setOps.Operation}"),
            }).Append('\n');
            b.Append("    LEG1\n").Append(Indent(Print(setOps.First)));
            b.Append("    LEG2\n").Append(Indent(Print(setOps.Second)));
        }

        if (model.DerivedFrom is { } inner)
            b.Append("  DERIVED\n").Append(Indent(Print(inner)));

        // 选片清单按换算后的物理名打：范围是链式入口的参数，名字才是这句要发出去的东西。
        if (model.Shards is { } shards)
            b.Append("  SHARDS ").Append(string.Join(" | ", shards.Select(s => (s.Schema ?? "dbo") + "." + s.Name))).Append('\n');

        foreach (var join in model.Joins)
        {
            var ref_ = join.Source.Ref;
            b.Append("  JOIN ").Append(join.Kind).Append(' ').Append(ref_.Table.Name).Append(" AS ").Append(ref_.Alias)
             .Append(join.On is { } on ? " ON " + Expr(on) : " ON <none>").Append('\n');
        }

        foreach (var predicate in model.Predicates)
            b.Append("  WHERE ").Append(Expr(predicate)).Append('\n');

        if (model.GroupBy.Count > 0)
            b.Append("  GROUP BY ").Append(Join(model.GroupBy)).Append('\n');

        if (model.Having is { } having)
            b.Append("  HAVING ").Append(Expr(having)).Append('\n');

        if (!model.StripsOrdering)
        {
            foreach (var order in model.Ordering)
                b.Append("  ORDER ").Append(Expr(order.Node)).Append(order.Descending ? " DESC" : " ASC").Append('\n');
        }

        b.Append("  PLAN ").Append(Plan(model.Plan)).Append('\n');

        if (model.Distinct)
            b.Append("  DISTINCT\n");

        if (model.Offset is { } offset)
            b.Append("  OFFSET ").Append(offset).Append('\n');

        if (model.Limit is { } limit)
            b.Append("  LIMIT ").Append(limit).Append('\n');

        if (model.CacheKey is { } key)
            b.Append("  CACHE ").Append(key).Append('/').Append(model.CacheTtl).Append('\n');

        // 这里没有 NO TRACKING 一行：跟不跟踪挂在引擎上、不在模型里，
        // 文本因此与跟踪与否无关，不需要把标记位从文本里剥掉才成立。

        return b.ToString();
    }

    static string Join(IReadOnlyList<Sql.Ast.SqlExpression> nodes) => string.Join(", ", nodes.Select(Expr));

    /// <summary>内层那份模型的全文按四格缩进嵌进来。空行直接丢掉：外层 Print 每行都以
    /// '\n' 结尾，切出来的最后一段必然是空串，留着它就多一个尾随空行，而规范文本是按字节比的。</summary>
    static string Indent(string text)
    {
        var b = new StringBuilder(text.Length + 16);

        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0)
                b.Append("    ").Append(line).Append('\n');
        }

        return b.ToString();
    }

    static string Plan(ProjectionPlan plan) => plan switch
    {
        EntityProjection e => "Entity(t" + e.TableIndex + ')',
        ValueProjection v => $"Value({Expr(v.Node)}:{v.ResultType.Name})",
        AggregateProjection a => $"Aggregate({a.Kind}{(a.Distinct ? " Distinct" : "")}({Expr(a.Node)}):{a.ResultType.Name})",
        ObjectProjection o => "Object(" + o.ResultType.Name + (o.ViaConstructor ? " ctor" : string.Empty) + "){ "
                              + string.Join(", ", o.Slots.Select(s => s.Name + "=" + Expr(s.Node)))
                              + " }",
        _ => throw new UntranslatableExpressionException(plan.GetType().Name,
             "新增 ProjectionPlan 必须同时在 QueryModelPrinter 登记规范形态，否则等价性测试会静默漏判"),
    };

    public static string Expr(Sql.Ast.SqlExpression node) => node switch
    {
        ColumnExpression c => "t" + c.Table.Index + "." + c.Column.ColumnName,
        ParameterValue p => "§(" + (p.Value ?? "NULL") + ":" + (p.Hint?.ColumnName ?? p.Value?.GetType().Name ?? "obj") + ")",
        InlineLiteral l => "!" + l.Sql,
        SqlBinaryExpression b => "(" + Expr(b.Left) + " " + Operator(b.Operator) + " " + Expr(b.Right) + ")",
        SqlUnaryExpression u => u.Operator switch
        {
            UnaryOperator.Not => "NOT " + Expr(u.Operand),
            UnaryOperator.Negate => "-" + Expr(u.Operand),
            UnaryOperator.IsNull => "(" + Expr(u.Operand) + " IS NULL)",
            UnaryOperator.IsNotNull => "(" + Expr(u.Operand) + " IS NOT NULL)",
            UnaryOperator.IsTrue => "(" + Expr(u.Operand) + " = TRUE)",
            UnaryOperator.IsFalse => "(" + Expr(u.Operand) + " = FALSE)",
            _ => throw new UntranslatableExpressionException(u.Operator.ToString(), null),
        },
        FunctionExpression f => f.Template + "(" + Join(f.Arguments) + ')',
        // OVER 里那两段要在规范文本里看得见，否则等价性测试静默漏判。
        RowNumberExpression w => "ROW_NUMBER OVER[PART(" + Join(w.PartitionBy) + ") ORD("
                                 + string.Join(", ", w.OrderBy.Select(o => Expr(o.Node) + (o.Descending ? " DESC" : " ASC")))
                                 + ")]",
        CastExpression c => "CAST(" + Expr(c.Operand) + " AS " + c.StoreType + ')',
        InExpression i => i.Subquery is { } sub
            ? "(" + Expr(i.Target) + (i.Negated ? " NOT IN " : " IN ") + "SUB(" + Sub(sub) + "))"
            : "(" + Expr(i.Target) + (i.Negated ? " NOT IN " : " IN ") + Join(i.Values!) + ')',
        ExistsExpression e => (e.Negated ? "NOT EXISTS(" : "EXISTS(") + Sub(e.Query) + ')',
        ScalarSubqueryExpression s => "SUB(" + Sub(s.Query) + ')',
        CaseExpression c => "CASE "
                            + (c.Operand is { } op ? Expr(op) + " " : string.Empty)
                            + string.Join(" ", c.Whens.Select(w => "WHEN " + Expr(w.When) + " THEN " + Expr(w.Then)))
                            + (c.Else is { } elseValue ? " ELSE " + Expr(elseValue) : string.Empty)
                            + " END",
        RawExpression r => "RAW[" + r.Sql + ']',
        StarExpression s => s.Table is { } t ? "t" + t.Index + ".*" : "*",
        _ => throw new UntranslatableExpressionException(node.GetType().Name, null),
    };

    static string Sub(SelectStatement select)
        => "SELECT[" + string.Join(", ", select.Columns.Select(p => p.Alias is null ? Expr(p.Node) : Expr(p.Node) + " AS " + p.Alias))
           + (select.From is { } from ? "] FROM[" + from.Ref.Table.Name + " AS " + from.Ref.Alias : "] FROM[]")
           + (select.Where is { } filter ? " WHERE[" + Expr(filter) + "]" : " WHERE[]");

    static string Operator(BinaryOperator op) => op switch
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
        BinaryOperator.And => "AND",
        BinaryOperator.Or => "OR",
        BinaryOperator.Like => "LIKE",
        BinaryOperator.BitwiseAnd => "&",
        BinaryOperator.BitwiseOr => "|",
        _ => throw new UntranslatableExpressionException(op.ToString(), null),
    };
}
