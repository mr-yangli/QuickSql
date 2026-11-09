namespace QuickSql.Sql.Ast;

public abstract record AstNode;

// ── 表源 ──────────────────────────────────────────────────────────────

/// <summary>FROM 序列中的一个表位。别名默认由 Index 生成（形参名不参与解析）。</summary>
public sealed record TableRef(int Index, string Alias, Metadata.TableInfo Table, Metadata.EntityContract? Contract = null)
{
    public bool IsRoot => Index == 0;
}

public abstract record TableSource(TableRef Ref) : AstNode;

public sealed record SourceTable(TableRef Ref) : TableSource(Ref);

public sealed record SourceSubquery(TableRef Ref, SelectStatement Query) : TableSource(Ref);

/// <summary>派生表：外层那句由库规划，Query 是内层的整份 SELECT。
/// 它与 SourceSubquery 的分别是谁写的内层：那一份的内层来自表达式里的相关子查询
/// （WHERE / IN / EXISTS），这一份的内层来自同一个模型的另一份计划，于是"分组之后数有几组"
/// 与"分页之后那一段共有几行"第一次有了能发的句子。只有一个内层，没有清单——
/// 一句 SELECT 的 FROM 位今天只承接一个源。</summary>
public sealed record SourceDerived(TableRef Ref, SelectStatement Query) : TableSource(Ref);

/// <summary>集合运算的关键字档。四个成员在任何一家都是句子级的东西，所以进形状（AST）而不进方言——
/// 唯一的例外走 DialectBase 的覆写点：EXCEPT 的别名由 Oracle 血统交 MINUS，语义同格。</summary>
public enum SetOperationKind
{
    /// <summary>UNION ALL：合并不去重。</summary>
    UnionAll,

    /// <summary>UNION：合并后整句去重。</summary>
    Union,

    /// <summary>INTERSECT：整串交集——两腿都出现的行。</summary>
    Intersect,

    /// <summary>EXCEPT：整串差集——第一腿有、第二腿没有的行。</summary>
    Except,
}

/// <summary>集合运算的形状（分表与链式合并共用）：一条腿一条完整的 SELECT，
/// 按 Operation 指的关键字串起来，整串占一个表位。
/// 与 SourceDerived 的分别是内层的份数——那一份永远是一个，这一份至少两条。
/// Branches 里每条都不许带 ORDER BY 与分页：那些只长在外层，否则
/// "取前 10 行"会变成"每片各取 10 行"，而两家的语法都执行得动
/// （另一家 ORM 把 Take/Skip/OrderBy 原样复制进每一片，正是这一格的反面教材）。
/// 别名沿用根表位那一个，所以外层的谓词与选列不必重写。</summary>
public sealed record SourceUnion(TableRef Ref, IReadOnlyList<SelectStatement> Branches, SetOperationKind Operation = SetOperationKind.UnionAll) : TableSource(Ref);

/// <summary>FROM 位换成一整段用户自己写的 SELECT 文本（WithSql）。
/// 与 RawExpression 同一前提：文本一颗不动，只在外面包一层括号再挂别名，
/// 因为那一列叫什么、按什么顺序，只有 SQL 作者知道。Parameters 是已经按本家前缀造好名的那一串，
/// 发射时逐颗登记进书写器——名字活在文本里，所以登记而不落地名字。</summary>
public sealed record SourceRawText(TableRef Ref, string Sql, IReadOnlyList<AdoParameter> Parameters) : TableSource(Ref);

public sealed record SourceFunction(TableRef Ref, string Name, IReadOnlyList<SqlExpression> Arguments) : TableSource(Ref);

/// <summary>一个字面行集：(VALUES (@p0,@p1),(@p2,@p3)) AS [t1] ([Id],[Name])。
/// 它是合并语句的另一侧——一批"键 + 要写的值"作为一个表参与连接，于是 N 只实体合成一条语句。
/// Columns 是这一侧的列名清单，Rows 的每一行都按它的顺序排列，
/// 两处必须同序：列清单写歪一格，值就整体串列，而串列的 UPDATE 每家库都执行得动。</summary>
public sealed record SourceRows(TableRef Ref, IReadOnlyList<Metadata.ColumnContract> Columns, IReadOnlyList<IReadOnlyList<SqlExpression>> Rows) : TableSource(Ref);

public enum JoinKind { Inner, Left, Right, Full, Cross }

public sealed record JoinClause(JoinKind Kind, TableSource Source, SqlExpression? On) : AstNode;

// ── 表达式 ────────────────────────────────────────────────────────────

public abstract record SqlExpression : AstNode;

public sealed record ColumnExpression(TableRef Table, Metadata.ColumnContract Column) : SqlExpression;

/// <summary>已经决定要参数化的值。与 InlineLiteral 的区别是"进不进参数集合"，
/// 由 IValueBinder 单点决定，不由调用方现场判断。</summary>
public sealed record ParameterValue(object? Value, Metadata.ColumnContract? Hint) : SqlExpression;

public sealed record InlineLiteral(string Sql) : SqlExpression;

public sealed record SqlBinaryExpression(BinaryOperator Operator, SqlExpression Left, SqlExpression Right) : SqlExpression;

public sealed record SqlUnaryExpression(UnaryOperator Operator, SqlExpression Operand) : SqlExpression;

/// <summary>函数节点。Template 是 provider 已经解析过的 SQL 模板（如 DATEADD(day, {0}, {1})），
/// 实参按 {i} 填入。C# 方法名到模板的映射在 IExpressionRules 完成，方言层不再认 CLR 方法名。</summary>
public sealed record FunctionExpression(string Template, IReadOnlyList<SqlExpression> Arguments) : SqlExpression;

/// <summary>行号（ROW_NUMBER）的节点：OVER 里那两段是句子的形状而不是模板的填充料，所以它不是
/// 一颗 FunctionExpression——模板走 string.Format，而 PARTITION BY 与 ORDER BY 各是一段带关键字的
/// 清单（清单可空、方向逐颗），填进 {i} 要把语法塞进 provider 的模板里。
/// 它与方言的关系与日期三口同档：共用层出文本，各家只出自己的引号（列名走 EmitExpression 的既有步法），
/// 一张模板都不登记。PartitionBy 可空 = 整集一序号；OrderBy 不可空——SQL Server 那一族要求 OVER 里
/// 至少一颗序，而"没有序的行号"在任何一家都不是一个能回答的问题，所以这一格由翻译器保证有序，
/// 方言层不必再判。两颗清单里的列引用都归外层那一句的表位，本节点不引入新的 FROM 位。</summary>
public sealed record RowNumberExpression(IReadOnlyList<SqlExpression> PartitionBy, IReadOnlyList<OrderExpression> OrderBy) : SqlExpression;

public sealed record CastExpression(SqlExpression Operand, string StoreType) : SqlExpression;

/// <summary>IN 表达式：目标值与一组常量值（Values 非空）或一条子查询（Subquery 非空）二选一。
/// 子查询形态产出 target IN (SELECT ...)，不需要额外的括号包裹——子查询的 SelectStatement
/// 由方言递归发射，自带圆括号。</summary>
public sealed record InExpression(
    SqlExpression Target,
    IReadOnlyList<SqlExpression>? Values = null,
    bool Negated = false,
    SelectStatement? Subquery = null) : SqlExpression;

public sealed record ExistsExpression(SelectStatement Query, bool Negated = false) : SqlExpression;

public sealed record ScalarSubqueryExpression(SelectStatement Query) : SqlExpression;

public sealed record CaseExpression(SqlExpression? Operand, IReadOnlyList<WhenClause> Whens, SqlExpression? Else) : SqlExpression;

public sealed record WhenClause(SqlExpression When, SqlExpression Then);

/// <summary>逃生舱：原样落地一段 SQL 片段。出现在黄金样本里时必须能被肉眼定位，
/// 因此 Why 是必填的——没有理由的 raw SQL 评审不过。</summary>
public sealed record RawExpression(string Sql, string Why) : SqlExpression;

public sealed record StarExpression(TableRef? Table) : SqlExpression;

public enum BinaryOperator
{
    Add, Subtract, Multiply, Divide, Modulo,
    Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual,
    And, Or, Like, BitwiseAnd, BitwiseOr,

    /// <summary>"不同则真，且两侧同空也算相等"。只有 PG 系与金仓有这一型（变更守卫那档），
    /// 所以它排在最后：前面的成员顺序不动，谁按序号存过什么都不受影响。</summary>
    IsDistinctFrom,
}

public enum UnaryOperator { Not, Negate, IsNull, IsNotNull, IsTrue, IsFalse }

// ── 语句 ──────────────────────────────────────────────────────────────

public sealed record Projection(SqlExpression Node, string? Alias) : AstNode;

public sealed record OrderExpression(SqlExpression Node, bool Descending) : AstNode;

public sealed record SelectStatement
{
    public required IReadOnlyList<Projection> Columns { get; init; }

    public TableSource? From { get; init; }

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];

    public SqlExpression? Where { get; init; }

    public IReadOnlyList<SqlExpression> GroupBy { get; init; } = [];

    public SqlExpression? Having { get; init; }

    public IReadOnlyList<OrderExpression> OrderBy { get; init; } = [];

    public SqlExpression? Offset { get; init; }

    public SqlExpression? Limit { get; init; }

    public bool Distinct { get; init; }

    /// <summary>分页必须有确定序。方言层按能力位决定是补 ORDER BY (SELECT NULL) 占位、
    /// 还是像 SQL Server / 金仓那样直接报错。</summary>
    public bool RequireOrderingForPaging { get; init; } = true;
}

public sealed record InsertStatement
{
    public required TableRef Table { get; init; }

    public required IReadOnlyList<Metadata.ColumnContract> Columns { get; init; }

    /// <summary>行 × 列。null 表示该位置走数据库默认值。</summary>
    public required IReadOnlyList<IReadOnlyList<SqlExpression?>> Rows { get; init; }

    public IReadOnlyList<Metadata.ColumnContract> Returning { get; init; } = [];

    /// <summary>多值语句的参数预算已在此算好，方言只负责落地文本。</summary>
    public bool ExplicitColumnList { get; init; } = true;
}

public sealed record UpdateStatement
{
    public required TableRef Table { get; init; }

    public required IReadOnlyList<SetClause> Sets { get; init; }

    public SqlExpression? Where { get; init; }

    public IReadOnlyList<Metadata.ColumnContract> Returning { get; init; } = [];

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];
}

public sealed record SetClause(Metadata.ColumnContract Column, SqlExpression Value) : AstNode;

public sealed record DeleteStatement
{
    public required TableRef Table { get; init; }

    public SqlExpression? Where { get; init; }

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];
}

/// <summary>整语句 upsert 的 AST（InsertOrUpdate 链在无句尾槽的那一家发的东西）。它是一颗形态中立
/// 的行集载体：两种真实写法都从它出——SQLite 的 INSERT 加 ON CONFLICT DO UPDATE，与 MERGE INTO
/// 那一族（SqlServer、Oracle 血统、虚谷、Gbase8s）。
/// 它与 InsertStatement 的分别是走的门不同，不是内容：句尾那一族（MySQL、PG 血统）有 UpsertClause
/// 那颗槽，内核照旧发一颗 InsertStatement 再接一段尾巴；而没有那颗槽的一家（典型是整颗 Bulk 交回 null 的
/// SQLite——没有可举证的参数预算把它连槽一起挡在外面）只能把整个发射交给方言，由它发这一颗。
/// 于是这颗 AST 带上了句尾那一族不需要在 AST 上带的分工（Keys 与 Settable），因为发它的那一方
/// 拿不到内核替它拼好的列尺子。这颗 AST 与 UpsertClause 槽是同一个意思的两面（列清单 = 可写加键、
/// 键进冲突判定不进写入那一支），只是载体不同：槽交的是"给 INSERT 补一句尾巴"的字符串，
/// 这颗交的是"发一整句"的 AST。</summary>
public sealed record UpsertStatement
{
    /// <summary>目标表，也是"没命中就插"那一支插进去的表。</summary>
    public required TableRef Table { get; init; }

    /// <summary>写入清单的顺序 = Settable 加 Keys，与句尾族那条 INSERT VALUES 逐位同序，
    /// Rows 的每一行按它排列。</summary>
    public required IReadOnlyList<Metadata.ColumnContract> Columns { get; init; }

    /// <summary>行 × 列。每格都是参数（这一版不产 DEFAULT 格，与 InsertStatement 同一口径）。</summary>
    public required IReadOnlyList<IReadOnlyList<SqlExpression?>> Rows { get; init; }

    /// <summary>冲突判定的键：顺序同契约，复合键装得下
    /// （多条件合取与目标列对各是合法文本，与"IN 装不下元组"是两件事）。</summary>
    public required IReadOnlyList<Metadata.ColumnContract> Keys { get; init; }

    /// <summary>"命中就改"那一支改写的列 = 除键、库里给值的列与并发列以外的全部列，
    /// 与句尾族的 UpsertColumns.Settable 同一把尺。</summary>
    public required IReadOnlyList<Metadata.ColumnContract> Settable { get; init; }
}
