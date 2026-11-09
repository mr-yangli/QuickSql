using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Query.Model;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.Write;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace QuickSql.Translation;

/// <summary>lambda → AST 表达式。链式与 LINQ 共用这一个访问器（LINQ 侧不许有第二套翻译路径）；
/// 翻译不了的形态一律抛 UntranslatableExpressionException 并给改写建议，绝不静默客户端求值。
/// 闭包常量求值需要反射与委托编译，因此整颗标 RUC/RDC：AOT 场景要么换表达式树解释器，
/// 要么走 QuickSql.Generators 预翻译的查询——这条边界只有一处，由调用方（查询提供者）显式承担。</summary>
[RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
[RequiresDynamicCode("闭包常量求值的退路是 LambdaExpression.Compile()。")]
public sealed class ExpressionTranslator(IDbProvider provider, IEntitySchemaRegistry schemas, Func<Type, int, TableRef> newTableRef)
{
    public IDbProvider Provider { get; } = provider;
    readonly IEntitySchemaRegistry _schemas = schemas;
    readonly Func<Type, int, TableRef> _newTableRef = newTableRef;
    int _subqueryIndex = 1000;

    /// <summary>谓词位：bool 列要补成 [flag] = 1，SQL Server 不接受裸 bit 列作条件。</summary>
    public SqlExpression Predicate(LambdaExpression lambda, Scope scope)
    {
        var node = Visit(lambda.Body, scope);

        // 行号不能拿来筛行——这一问在组合时就判，不等库报错（判据与两条出路在 WindowGuard）。
        WindowGuard.RejectRowNumber(node, "谓词位（WHERE / HAVING / 连接条件 / 全局过滤器）");

        return Booleanized(lambda.Body, node);
    }

    public SqlExpression Visit(Expression node, Scope scope, ColumnContract? hint = null)
    {
        switch (node)
        {
            case BinaryExpression binary:
                return Binary(binary, scope, hint);

            case UnaryExpression unary:
                return Unary(unary, scope, hint);

            case MemberExpression member:
                return Member(member, scope, hint);

            case MethodCallExpression call:
                return Call(call, scope, hint);

            case ConditionalExpression conditional:
                return new CaseExpression(null,
                [
                    new WhenClause(Booleanized(conditional.Test, Visit(conditional.Test, scope)), Visit(conditional.IfTrue, scope, hint)),
                ], Visit(conditional.IfFalse, scope, hint));

            case ConstantExpression constant:
                return Value(constant.Value, hint);

            case ParameterExpression parameter:
                throw new UntranslatableExpressionException(scope.NameOf(parameter),
                    "整行不能当值用：成员位置只能放列（如 new { o.Id, c.Name }），要整行就只选一张表（Select((o, c) => o)）");

            case InvocationExpression invocation:
                throw new UntranslatableExpressionException(invocation.ToString(), "去掉显式委托调用，把表达式直接内联");

            default:
                return Reduced(node, scope, hint);
        }
    }

    SqlExpression Reduced(Expression node, Scope scope, ColumnContract? hint)
    {
        // 不引用任何查询参数的子表达式 = 客户端可求值常量（闭包变量、静态字段、预计算算术）。
        if (ReferencesScope(node, scope))
            throw new UntranslatableExpressionException(node.ToString(),
                "该形态当前无映射。对照 §5.7 覆盖矩阵；确需本地计算就先 ToList()/ToListAsync() 落到内存再算", node.NodeType.ToString());

        return Value(Evaluate(node), hint);
    }

    // ── 叶子 ──────────────────────────────────────────────────────────

    static SqlExpression Value(object? value, ColumnContract? hint)
    {
        if (value is null)
            return new ParameterValue(null, hint);

        var type = value.GetType();

        // 枚举按底层整型落库：不这样做的库会按名字绑参，读回来就是一串问号。
        if (type.IsEnum)
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(type));

        return new ParameterValue(value, hint);
    }

    public static bool ReferencesScope(Expression node, Scope scope)
        => node switch
        {
            ParameterExpression p => scope.Contains(p),
            LambdaExpression lambda => ReferencesScope(lambda.Body, scope),
            BinaryExpression b => ReferencesScope(b.Left, scope) || ReferencesScope(b.Right, scope),

            // 缺这一臂时，"引用了行的成员初始化"被判成不引用参数，走进 Evaluate 的编译退路，
            // 吐出来的是 InvalidOperationException: variable 'o' referenced from scope ''
            // —— 一句指不了路的内部错误；补上之后同一颗表达式拿到的是"该形态当前无映射"。
            // net10 上 MemberInitExpression 已不继承 UnaryExpression，靠下面那条兜不到（实测过）。
            // 集合初始化（new C { Items = { o.A } }）与 IN 的集合侧（new[]{ o.X }.Contains(y)）仍未覆盖，症状同上。
            MemberInitExpression init
                => init.Bindings.OfType<MemberAssignment>().Any(b => ReferencesScope(b.Expression, scope))
                   || ReferencesScope(init.NewExpression, scope),

            UnaryExpression u => ReferencesScope(u.Operand, scope),
            MemberExpression m => m.Expression is not null && ReferencesScope(m.Expression, scope),
            MethodCallExpression c => (c.Object is not null && ReferencesScope(c.Object, scope)) || c.Arguments.Any(a => ReferencesScope(a, scope)),
            ConditionalExpression c => ReferencesScope(c.Test, scope) || ReferencesScope(c.IfTrue, scope) || ReferencesScope(c.IfFalse, scope),
            NewExpression n => n.Arguments.Any(a => ReferencesScope(a, scope)),
            NewArrayExpression a => a.Expressions.Any(e => ReferencesScope(e, scope)),
            InvocationExpression i => ReferencesScope(i.Expression, scope),
            TypeBinaryExpression t => t.Expression is not null && ReferencesScope(t.Expression, scope),
            _ => false,
        };

    /// <summary>求值「不引用任何查询参数」的子表达式。先走常数/字段/属性这条无代码生成的快路，
    /// 只有真正需要执行用户算术时才付 Compile() 的成本。编译产物按节点缓存（ConditionalWeakTable，
    /// 键随节点回收）：链式对象复用时 Where 交回来的是同一棵 lambda 子树，不缓存就是每次执行数十微秒级的
    /// 固定税；节点的求值只依赖节点自身（闭包已捕获在里面），与 Scope 无关，所以这份缓存是安全的。</summary>
    static readonly ConditionalWeakTable<Expression, StrongBox<Func<object>>> CompiledEvaluators = new();

    public static object? Evaluate(Expression node) => node switch
    {
        ConstantExpression c => c.Value,

        MemberExpression { Member: FieldInfo field } m => field.GetValue(m.Expression is null ? null : Evaluate(m.Expression)),

        MemberExpression { Member: PropertyInfo property } m => property.GetValue(m.Expression is null ? null : Evaluate(m.Expression)),

        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u => Coerce(Evaluate(u.Operand), u.Type),

        // default(T)、用户算术、方法调用等一律交给编译退路：形态再多也不必在此枚举一遍。
        // 编译成 Func<object> 直接调用，不走 DynamicInvoke（每次调用多一层参数封送与装箱，
        // 而这颗退路按"每颗不可约子表达式"付）；void 子表达式在 Convert 处编不出来，单独按 null 交回。
        _ => node.Type == typeof(void) ? null : CompiledEvaluator(node)(),
    };

    static Func<object> CompiledEvaluator(Expression node)
        => CompiledEvaluators.GetValue(node,
            static n => new StrongBox<Func<object>>(
                Expression.Lambda<Func<object>>(Expression.Convert(n, typeof(object))).Compile())).Value!;

    static object? Coerce(object? value, Type target)
    {
        if (value is null || target.IsInstanceOfType(value))
            return value;

        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        return underlying.IsEnum ? Enum.ToObject(underlying, value) : Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    // ── 成员 ──────────────────────────────────────────────────────────

    SqlExpression Member(MemberExpression member, Scope scope, ColumnContract? hint)
    {
        // Nullable<T> 的三个读法先截掉，否则会被当成不存在的列。
        if (member.Expression is not null && member.Expression.Type.IsGenericType
            && member.Expression.Type.GetGenericTypeDefinition() == typeof(Nullable<>)
            && member.Member.Name is "HasValue" or "Value" or "GetValueOrDefault")
        {
            var target = Visit(member.Expression, scope, hint);

            return member.Member.Name == "HasValue"
                ? new SqlUnaryExpression(UnaryOperator.IsNotNull, target)
                : target;
        }

        // 分组形参（g）不是表位，它的成员读法在列契约之外：单列键是 g.Key 那一颗表达式，复合键是 g.Key.成员。
        // 判在位置绑定与 Part 之前——走后两条路都会把 g 当表位问，得到一句把正确写法报成错误的消息。
        if (RootedAtGrouping(member, scope, out var grouping, out var path))
            return GroupingKey(grouping, path, member);

        if (member.Expression is ParameterExpression parameter)
        {
            var table = scope.TableOf(parameter);
            var contract = table.Contract ?? throw new QuickSqlException($"表位 {table.Alias} 没有实体契约，无法解析 {member.Member.Name}");

            if (contract.ByMember.TryGetValue(member.Member.Name, out var column))
                return new ColumnExpression(table, column);

            throw Navigation(member, $"{table.Alias}.{member.Member.Name}");
        }

        if (member.Expression is null || !ReferencesScope(member.Expression, scope))
            return Value(Evaluate(member), hint);

        // 引用了表位、却又不是列的成员读法（o.CreatedAt.Year、o.CustomerName.Length）
        // 只能是 CLR 的派生形状。认不出来必须抛：把 o.CreatedAt.Kind 静默翻译成一个裸列，
        // 产出的是"语法正确、结果错"的 SQL。
        return Part(member, scope, hint);
    }

    /// <summary>这一串成员读法是不是长在一个分组形参上（g），并把它的路径按外→内收成 ["Key"] / ["Key","City"]。
    /// 中间只要出现非成员节点（方法调用、转换…）就不算，交给原有那两条路给出它们各自的报错。</summary>
    static bool RootedAtGrouping(MemberExpression member, Scope scope, out GroupingShape grouping, out string[] path)
    {
        var names = new List<string>();
        Expression at = member;

        while (at is MemberExpression chain)
        {
            names.Add(chain.Member.Name);
            at = chain.Expression!;
        }

        grouping = default;

        if (at is not ParameterExpression parameter || !scope.TryGroupingOf(parameter, out grouping))
        {
            path = [];

            return false;
        }

        names.Reverse();
        path = [.. names];

        return true;
    }

    /// <summary>g.Key 与 g.Key.成员 的落点。除了这两条形就没有别的：组里的行在分组之后不再是一列列可选的值，
    /// 而这正是 IGrouping 那个类型只有 Key 一个属性的原因。</summary>
    static SqlExpression GroupingKey(GroupingShape grouping, IReadOnlyList<string> path, Expression node)
    {
        if (path[0] != "Key")
            throw new UntranslatableExpressionException(node.ToString(),
                $"分组后的形参上可读的成员只有 Key（这里读的是 {path[0]}）：组内的行要聚合成值（g.Count()、g.Sum(o => o.列) 等），" +
                "非分组键的整行列在分组之后不是一颗可选的值");

        if (grouping.Members is null)
        {
            if (path.Count == 1)
                return grouping.KeyOrThrow(node.ToString());

            throw new UntranslatableExpressionException(node.ToString(),
                "单列分组键没有成员：g.Key 已经是那一颗表达式。要按几列分组请写成 GroupBy(o => new { o.A, o.B })，再读 g.Key.A");
        }

        // 复合键：g.Key 整体不是值（下面那句 KeyOrThrow 的说法），g.Key.A 才是。
        if (path.Count == 1)
            return grouping.KeyOrThrow(node.ToString());

        if (path.Count != 2)
            throw new UntranslatableExpressionException(node.ToString(),
                "复合分组键只到一层（g.Key.<成员>）：再往下的成员读法属于 CLR 形状，不在分组键上");

        return grouping.Members.TryGetValue(path[1], out var key)
            ? key
            : throw new UntranslatableExpressionException(node.ToString(),
                $"分组键里没有成员 {path[1]}（在册的成员：{string.Join("、", grouping.Members.Keys)}）");
    }

    /// <summary>列的 CLR 形状是闭合的：值类型（含枚举、Guid、decimal、日期）加上 string 与 byte[]。
    /// 除此之外的类型当宿主用，只能是导航或未映射成员。别拿 StoreTypeOf 来问"这是不是列"——
    /// 它对认不出的类型直接抛"没有类型映射"，而这里要的是判断本身，不是那条报错。</summary>
    static bool IsStorable(Type owner) => owner.IsValueType || owner == typeof(string) || owner == typeof(byte[]);

    /// <summary>把宿主写成 t0.Customer 这种能直接对照 FROM 的路径：报错要指到表位，
    /// 而不是复述一遍用户写的形参名（形参名不参与任何解析）。</summary>
    static string OwnerPath(Expression owner, Scope scope)
        => owner switch
        {
            UnaryExpression { NodeType: ExpressionType.Convert } widen => OwnerPath(widen.Operand, scope),
            MemberExpression chain => OwnerPath(chain.Expression!, scope) + "." + chain.Member.Name,
            ParameterExpression parameter => scope.TableOf(parameter).Alias,
            _ => owner.ToString(),
        };

    static UntranslatableExpressionException Navigation(Expression node, string path)
        => new(node.ToString(), typeof(IEnumerable).IsAssignableFrom(node.Type) && node.Type != typeof(string)
            ? $"{path} 是集合导航而不是列：批量加载（IncludeMany，两次查询 + 内存缝合）见 §5.7；要现在拿到明细行请显式 join 到明细表，注意那会让主表行重复。引用导航那一档（to-one）在投影/谓词里已经会自动联表（§5.2），集合导航没有这一档，因为一条 LEFT JOIN 就把每行主表复制成明细行数那么多行 —— 页码、行数与聚合会一起改语义。"
            : $"{path} 不是列 —— 引用导航整个当值用没有对应物（要读它的列请再往下写一格，如 {path}.某一列，那条 LEFT JOIN 库自己补，§5.2；判那条边空不空则要比较它那一侧的主键列）；未映射的计算成员（表达式体属性、[Computed] 之外的那些）则是根本取不到列。");

    /// <summary>列上的 CLR 派生读法 → 符号函数。键是属性名，与 SymbolicKey 那张"方法名"表是两套形状：
    /// 表达式树里属性访问是 MemberExpression，只有 get_XXX 以方法形态出现时才走那边。</summary>
    SqlExpression Part(MemberExpression member, Scope scope, ColumnContract? hint)
    {
        var owner = member.Expression!;
        var kind = Nullable.GetUnderlyingType(owner.Type) ?? owner.Type;

        // 穿过引用导航读成员（o.Customer.City）：宿主不是列，契约上有那条 to-one 边就把宿主补成一张表位
        // （LEFT JOIN 由 NavigationJoinScout 欠着，算子落地时进 FROM），读的那一格落在这张新表上——
        // 调用方既不写 Include 也不写 join。认不出才抛：把 o.CreatedAt.Kind 静默翻译成裸列，
        // 产出的是"语法正确、结果错"的 SQL。
        if (!IsStorable(kind))
        {
            var target = scope.NavigationTable(owner);

            if (target?.Contract is { } targetContract && targetContract.ByMember.TryGetValue(member.Member.Name, out var column))
                return new ColumnExpression(target, column);

            // 宿主联上了而这一格仍认不出（导航的导航整个当值、集合导航、未映射成员）时报的是整条读法：
            // 只报宿主会把"缺的是 .Manager 那一格"说成"Customer 不是列"，而 Customer 这一形刚刚才落得成。
            throw target is null
                ? Navigation(owner, OwnerPath(owner, scope))
                : Navigation(member, OwnerPath(member, scope));
        }

        var temporal = kind == typeof(DateTime) || kind == typeof(DateTimeOffset) || kind == typeof(DateOnly);

        var symbol = member.Member.Name switch
        {
            nameof(string.Length) when kind == typeof(string) => QuickFn.Length,
            "Year" when temporal => QuickFn.DateYear,
            "Month" when temporal => QuickFn.DateMonth,
            "Day" when temporal => QuickFn.DateDay,
            "Hour" when temporal => QuickFn.DateHour,
            "Minute" when temporal => QuickFn.DateMinute,
            "Second" when temporal => QuickFn.DateSecond,
            "Date" when temporal => QuickFn.ToDate,
            _ => null,
        };

        return symbol is null
            ? throw new UntranslatableExpressionException(member.ToString(),
                  $"{kind.Name}.{member.Member.Name} 没有 SQL 对应形态：可翻译成函数的只有 §5.7 矩阵里那些读法，" +
                  "确需本地计算就把这一段先 ToList()/ToListAsync() 落到内存再读这一档")
            : Template(symbol, [Visit(owner, scope, hint)]);
    }

    // ── 二元 ──────────────────────────────────────────────────────────

    SqlExpression Binary(BinaryExpression node, Scope scope, ColumnContract? hint)
    {
        if (node.NodeType == ExpressionType.Coalesce)
            return Template(QuickFn.Coalesce, [Visit(node.Left, scope, hint), Visit(node.Right, scope, hint)]);

        if (node.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
        {
            var op = node.NodeType == ExpressionType.AndAlso ? BinaryOperator.And : BinaryOperator.Or;
            return Fold(op,
                Booleanized(node.Left, Visit(node.Left, scope)),
                Booleanized(node.Right, Visit(node.Right, scope)));
        }

        // 先按「直连列」定尺子，再递归：这样 StringLength / Unicode 能进到参数绑定（单一真相）。
        var leftColumn = DirectColumn(node.Left, scope);
        var rightColumn = DirectColumn(node.Right, scope);
        var lhs = Visit(node.Left, scope, leftColumn ?? hint);
        var rhs = Visit(node.Right, scope, rightColumn ?? hint);

        // null / bool 两侧都判（Yoda 条件也要走同一口折叠）：折叠只看右的那一版，
        // null == o.Name 会落成 WHERE @p0 = [Name]——语法对、结果错，任何行都是 UNKNOWN。
        // 列侧在哪边就折哪边。
        if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            var isEqual = node.NodeType == ExpressionType.Equal;

            if (rhs is ParameterValue { Value: null })
                return new SqlUnaryExpression(isEqual ? UnaryOperator.IsNull : UnaryOperator.IsNotNull, lhs);

            if (lhs is ParameterValue { Value: null })
                return new SqlUnaryExpression(isEqual ? UnaryOperator.IsNull : UnaryOperator.IsNotNull, rhs);
        }

        if (node.NodeType == ExpressionType.Equal)
        {
            if (rhs is ParameterValue { Value: bool flag })
                return new SqlUnaryExpression(flag ? UnaryOperator.IsTrue : UnaryOperator.IsFalse, lhs);

            if (lhs is ParameterValue { Value: bool yodaFlag })
                return new SqlUnaryExpression(yodaFlag ? UnaryOperator.IsTrue : UnaryOperator.IsFalse, rhs);
        }

        // C# 的 "a + b" 把 null 当空串，而 T-SQL/Oracle 的 + 会把整个表达式变成 NULL：
        // 不折成 CONCAT，== "xy" 这类查询在库里会悄悄少行。
        if (node.NodeType == ExpressionType.Add && node.Type == typeof(string))
            return Template(QuickFn.Concat, [lhs, rhs]);

        return new SqlBinaryExpression(Operator(node.NodeType), lhs, rhs);
    }

    /// <summary>CLR 侧是 bool、SQL 侧却不是一个"谓词形状"时要补 = 1（T-SQL 不接受裸 bit 列作条件）。
    /// 谓词形状是一张闭合表：比较/逻辑/LIKE、IS NULL/NOT、EXISTS、IN，以及方言自己给的恒真/恒假片段
    /// （空集合那条 1 = 0）。漏掉一种就得到 [Id] IN (…) = 1 这种在库里直接语法错的文本——
    /// 它只在真连库时才暴露，所以形状表要写全。</summary>
    static bool IsPredicate(SqlExpression ast) => ast is SqlBinaryExpression or SqlUnaryExpression
                                                  or ExistsExpression or InExpression or InlineLiteral;

    SqlExpression Booleanized(Expression linqNode, SqlExpression ast)
        => linqNode.Type == typeof(bool) && !IsPredicate(ast)
            ? new SqlUnaryExpression(UnaryOperator.IsTrue, ast)
            : ast;

    ColumnContract? DirectColumn(Expression node, Scope scope)
        => node is MemberExpression { Expression: ParameterExpression p } member && scope.Contains(p)
            ? scope.TableOf(p).Contract?.ByMember.GetValueOrDefault(member.Member.Name)
            : null;

    static BinaryOperator Operator(ExpressionType type) => type switch
    {
        ExpressionType.Add => BinaryOperator.Add,
        ExpressionType.Subtract => BinaryOperator.Subtract,
        ExpressionType.Multiply => BinaryOperator.Multiply,
        ExpressionType.Divide => BinaryOperator.Divide,
        ExpressionType.Modulo => BinaryOperator.Modulo,
        ExpressionType.Equal => BinaryOperator.Equal,
        ExpressionType.NotEqual => BinaryOperator.NotEqual,
        ExpressionType.LessThan => BinaryOperator.LessThan,
        ExpressionType.LessThanOrEqual => BinaryOperator.LessThanOrEqual,
        ExpressionType.GreaterThan => BinaryOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => BinaryOperator.GreaterThanOrEqual,
        ExpressionType.And => BinaryOperator.BitwiseAnd,
        ExpressionType.Or => BinaryOperator.BitwiseOr,
        _ => throw new UntranslatableExpressionException(type.ToString(), "该运算符没有 SQL 对应"),
    };

    static SqlExpression Fold(BinaryOperator op, SqlExpression left, SqlExpression right)
    {
        // 保持 AND/OR 扁平右倾：否则 a && b && c 在链式（两次 Append）与 LINQ（一棵树）下
        // 会长出两棵不同的树，等价性测试立刻红。
        if (left is SqlBinaryExpression { Operator: var same } existing && same == op)
            return existing with { Right = Fold(op, existing.Right, right) };

        return new SqlBinaryExpression(op, left, right);
    }

    SqlExpression Unary(UnaryExpression node, Scope scope, ColumnContract? hint)
    {
        switch (node.NodeType)
        {
            case ExpressionType.Not:
                return new SqlUnaryExpression(UnaryOperator.Not, Booleanized(node.Operand, Visit(node.Operand, scope, hint)));

            case ExpressionType.Negate or ExpressionType.NegateChecked:
                return new SqlUnaryExpression(UnaryOperator.Negate, Visit(node.Operand, scope, hint));

            case ExpressionType.Convert or ExpressionType.ConvertChecked:
                {
                    var operand = Visit(node.Operand, scope, hint);

                    // enum ↔ int、T ↔ T?、装箱成 object 只是 CLR 的表示差异，不进 SQL。
                    // 装箱这条最常从 string.Concat(object, object) 里冒出来：C# 把 a + "/" + i 编成它，
                    // 而 CONCAT 本来就会把数字转成文本 —— 补一个 CAST 既没有目标类型可填，也是多余的。
                    if (node.Type == typeof(object) || RepresentationOnly(node.Operand.Type, node.Type))
                        return operand;

                    var storeType = Provider.StoreTypeOf(Nullable.GetUnderlyingType(node.Type) ?? node.Type);
                    return storeType is null ? operand : new CastExpression(operand, storeType);
                }

            case ExpressionType.TypeAs:
                throw new UntranslatableExpressionException(node.ToString(), "as 转换依赖鉴别器投影（P1）");

            default:
                throw new UntranslatableExpressionException(node.NodeType.ToString(), null);
        }
    }

    static bool IsSameColumnar(Type from, Type to)
        => (Nullable.GetUnderlyingType(from) ?? from) == (Nullable.GetUnderlyingType(to) ?? to);

    /// <summary>这一颗 Convert 只是 CLR 的表示差异、不改变落进库的值，因此不发射 SQL 转换。
    /// 判据一律看穿一层 Nullable，且不收紧原有豁免只放宽：enum == enum? 与 int 列 == short? 量这类比较，
    /// C# 长出的是提升后的转换，两侧各裹一层 Nullable 就让同列判断比出不同型，落到 StoreTypeOf 取到的是
    /// DDL 建表型名（MySQL 给 "INT"），发出去成 CAST(@p0 AS INT)，而 MySQL 的 CAST 不认 INT，整条句子语法错。
    /// 整型之间只豁免加宽（16→32、32→64）：加宽不改变值，且免掉列侧的 CAST 才守得住索引；收窄会真丢值，
    /// 留着 CAST 让它响。char 不进整型表：它的列是 CHAR(1) 存文本，拿数值去比字符是库级静默降级，比报错更坏。</summary>
    static bool RepresentationOnly(Type from, Type to)
    {
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;

        if (source.IsEnum || target.IsEnum || IsSameColumnar(from, to))
            return true;

        var width = IntegerWidth(source);
        return width > 0 && width < IntegerWidth(target) && source != typeof(ulong);
    }

    /// <summary>整型的位宽；不在表上的（含 char、decimal、引用型）回 0，表示这一族不讨论加宽。</summary>
    static int IntegerWidth(Type type)
    {
        if (type == typeof(sbyte) || type == typeof(byte)) return 8;
        if (type == typeof(short) || type == typeof(ushort)) return 16;
        if (type == typeof(int) || type == typeof(uint)) return 32;
        if (type == typeof(long) || type == typeof(ulong)) return 64;
        return 0;
    }

    // ── 方法调用 ──────────────────────────────────────────────────────

    SqlExpression Call(MethodCallExpression node, Scope scope, ColumnContract? hint)
    {
        if (TryIQuerySubquery(node, scope, hint, out var iqueryResult))
            return iqueryResult;

        var declaring = node.Method.DeclaringType;

        // 行号四颗在"逐颗 Visit 实参"那一段之前先接走。两件事都在这里定：① 这一颗的三档分工要看原始实参
        // （方向那颗要当场求值，落成参数就改了句子的形状）；② 报错点必须是这一颗自己的指路——晚一步的话，
        // 第二颗实参里一颗本家不认的表达式会抢先把消息换成"为 String.PadRight 注册映射"，
        // 用户看到的是隔壁那一格，而重载决议走错档这件事从头到尾没被说过。
        if (declaring == typeof(QuickFx) && node.Method.Name == nameof(QuickFx.RowNumber))
            return RowNumber(node, scope);

        // 分组链上的 Enumerable 方法先判掉：g.Count() 落 COUNT(*)、g.Sum(o => o.列) 落 SUM(那一列)，
        // 其余那几颗（Any/First/Distinct…）在 GROUP BY 语义下没有对应物，要给出的是这个理由，
        // 而不是下面那句"为 Enumerable.X 注册映射"——那里指错了地方：缺的不是方言模板。
        // 判在集合导航那两条之前：二者在树上是同一个 Enumerable 扩展方法，区别只在第一个实参是
        // "表位形参的导航成员"还是"分组形参本身"，分错支会得到一条相关子查询套进 GROUP BY 句里。
        if (declaring == typeof(Enumerable) && IsOverGrouping(node, scope, out _))
            return GroupAggregate(node, scope);

        switch (node.Method.Name)
        {
            // 实测形状（.NET 10）：int[] 的 ids.Contains(x) 编成 2 元的 MemoryExtensions.Contains，
            // 枚举数组同写法编成 3 元、末尾多一个值为 null 的 IEqualityComparer<T> 实参。
            // 所以判别条件不能钉在实参数量上，只能钉在"这两个类"上，再对多出来的那一元单独放行。
            case "Contains" when node.Arguments.Count is 2 or 3 && (declaring == typeof(Enumerable) || declaring == typeof(MemoryExtensions)):
                {
                    // 比较器实参只有 null 这一种可以当没看见 —— 它在两侧都等价于 EqualityComparer<T>.Default。
                    // 真给了比较器还照默认语义发射，等于把用户显式指定的相等规则偷偷换掉。
                    if (node.Arguments.Count == 3 && node.Arguments[2] is not ConstantExpression { Value: null })
                        throw new UntranslatableExpressionException(node.ToString(),
                            "IN 的元素相等规则要下推到库里才保得住语义，本版本只接受默认比较器（编译期传下来的 null）。" +
                            "请去掉自定义比较器，或先物化到内存再过滤。");

                    return InFromCollection(AsEnumerable(Evaluate(SpanSource(node.Arguments[0])), node), node.Arguments[1], scope, hint);
                }

            case "Contains" when node.Object is { Type: not null } enumerable && enumerable.Type != typeof(string)
                                 && typeof(IEnumerable).IsAssignableFrom(enumerable.Type)
                                 && !ReferencesScope(enumerable, scope):
                return InFromCollection(AsEnumerable(Evaluate(node.Object), node), node.Arguments[0], scope, hint);

            case "StartsWith" or "EndsWith" or "Contains" when declaring == typeof(string):
                return StringLike(node, scope);

            case "op_Equality" when node.Arguments.Count == 2:
                return Binary(Expression.MakeBinary(ExpressionType.Equal, node.Arguments[0], node.Arguments[1]), scope, hint);

            // 带"相等规则"的那一颗（StringComparison / 某个比较器）先拒掉，理由与上面 Contains 那条同一条口径：
            // 规则下推不到库里，而照默认语义发射等于把调用方显式指定的相等规则偷偷换掉。
            case "Equals" when HasEqualityRuleArgument(node):
                throw new UntranslatableExpressionException(node.ToString(),
                    "带 StringComparison / IEqualityComparer 的相等规则下推不到库里：各家 COLLATE 与大小写口径不同，" +
                    "照默认语义发射就是把调用方指定的规则偷偷换掉。要比大小写不敏感就两侧都写 ToLower()/ToUpper()，" +
                    "或先 ToList()/ToListAsync() 落到内存再比。");

            // 值相等那一族：x.Equals(y)（实例）与 object.Equals(x, y) / string.Equals(x, y)（静态）。
            // 只认 string 一家的话，x.Id.Equals(id)（Id 是 long/Guid，C# 绑到 Int64.Equals 或 Object.Equals）
            // 整族会落到"为 Object.Equals 注册映射"那句指错地方的报错上；`=` 才是它的落点。
            case "Equals" when TryValueEquality(node, out var equalsLeft, out var equalsRight):
                return ValueEquality(node, equalsLeft, equalsRight, scope, hint);

            case "IsNullOrEmpty" when declaring == typeof(string) && node.Arguments.Count == 1:
                {
                    var target = Visit(node.Arguments[0], scope, hint);
                    var empty = new ParameterValue(string.Empty, DirectColumn(node.Arguments[0], scope));

                    return new SqlBinaryExpression(BinaryOperator.Or,
                        new SqlUnaryExpression(UnaryOperator.IsNull, target),
                        new SqlBinaryExpression(BinaryOperator.Equal, target, empty));
                }

            case "IsNullOrWhiteSpace" when declaring == typeof(string) && node.Arguments.Count == 1:
                return Template(QuickFn.Blank, [Visit(node.Arguments[0], scope, hint)]);

            case "Any" or "Count" when declaring == typeof(Enumerable):
                if (TryNavigationSubquery(node, scope, out var subqueryResult))
                    return subqueryResult;
                throw new UntranslatableExpressionException(node.ToString(),
                    "集合导航上的子查询聚合只支持直接导航属性（如 o.Items.Any(...)），当前形态无法识别为导航子查询");

            case "Sum" or "Max" or "Min" or "Average" when declaring == typeof(Enumerable):
                throw new UntranslatableExpressionException(node.ToString(), "集合导航上的子查询聚合（Sum/Max/Min/Average）在 P1（关联子查询 / APPLY）");
        }

        var arguments = node.Object is null
            ? node.Arguments.Select(a => Visit(a, scope, hint)).ToArray()
            : [Visit(node.Object, scope, hint), .. node.Arguments.Select(a => Visit(a, scope, hint))];

        var key = SymbolicKey(node);

        if (key == QuickFn.Concat)
            return FoldConcatenation(arguments);

        if (key is not null)
            return Template(key, arguments);

        if (Provider.Expressions.TryGetFunction(MethodKey(node.Method), out var registered))
            return new FunctionExpression(registered.Template, [.. arguments]);

        if (node.Method.DeclaringType == typeof(QuickFx) && FxShape(node, arguments, scope) is { } shape)
            return shape;

        throw new UntranslatableExpressionException(node.ToString(),
            $"为 {declaring?.Name}.{node.Method.Name} 注册映射：provider.Expressions.Register(\"{MethodKey(node.Method)}\", new FunctionMapping(\"模板\"))；不想注册就先 ToList()/ToListAsync() 落到内存再调这个方法");
    }

    /// <summary>这一颗 Equals 的实参里带不带"相等规则"（StringComparison 或某个比较器）。
    /// 按实参的型判而不是按 GetParameters()：省一次反射分配，而这两个型只可能从规则实参上冒出来。</summary>
    static bool HasEqualityRuleArgument(MethodCallExpression node)
    {
        foreach (var argument in node.Arguments)
            if (argument.Type == typeof(StringComparison) || typeof(IEqualityComparer).IsAssignableFrom(argument.Type))
                return true;

        return false;
    }

    /// <summary>把 Equals 认成"两侧各一个值"的形状：实例一元（x.Equals(y)），或 object/string 上那颗静态二元
    /// （object.Equals(x, y)）。认不出就回 false，让 Call 末尾那句"为 … 注册映射"照旧开口——
    /// 例如 EqualityComparer&lt;T&gt;.Default.Equals(a, b)（实例二元）就落在那一头。</summary>
    static bool TryValueEquality(MethodCallExpression node, out Expression left, out Expression right)
    {
        if (node.Object is { } receiver && node.Arguments.Count == 1)
        {
            left = receiver;
            right = node.Arguments[0];
            return true;
        }

        if (node.Object is null && node.Arguments.Count == 2
            && node.Method.DeclaringType is { } declaring
            && (declaring == typeof(object) || declaring == typeof(string)))
        {
            left = node.Arguments[0];
            right = node.Arguments[1];
            return true;
        }

        left = right = null!;
        return false;
    }

    /// <summary>值相等落 =。两侧同型这一问在这里比 == 那条路严一档：== 上有编译器替调用方做好的提升
    /// （int 列 == long 量在树上已经是一层 Convert），Equals 上没有——装进 object 的两个值只有同型才相等，
    /// intCol.Equals(someLong) 在 CLR 上恒为 false，照 = 发射就把它翻成"值相同就中"；整型加宽与
    /// enum 对底层型这两条在 == 那一格是豁免（RepresentationOnly），在这一格必须拒。
    /// 它故意不走 Binary 那条路：那一条会把 == null 折成 IS NULL，而 CLR 上 x.Equals(null) 恒为 false，
    /// 折成 IS NULL 会让空列那一行反而命中，是错的。反向的一格：x.Equals(y) 在 x 为 null 时 CLR 抛
    /// NullReferenceException，库里那一行只是不中——这一格上库比 CLR 宽，与 string.Equals 那支行为逐字相同。</summary>
    SqlExpression ValueEquality(MethodCallExpression node, Expression left, Expression right, Scope scope, ColumnContract? hint)
    {
        var leftType = ComparedType(left);
        var rightType = ComparedType(right);

        if (leftType is not null && rightType is not null
            && (Nullable.GetUnderlyingType(leftType) ?? leftType) != (Nullable.GetUnderlyingType(rightType) ?? rightType))
            throw new UntranslatableExpressionException(node.ToString(),
                $"Equals 的两侧不同型（{leftType.Name} ∶ {rightType.Name}），而 CLR 上这一句恒为 false —— " +
                "装进 object 的两个值只有同型才相等，照 = 发射会把一行都不该中翻成值相同就中。" +
                "要按值比就写 ==（让编译器去做提升），或把量转成列的那一型。");

        return new SqlBinaryExpression(BinaryOperator.Equal, Visit(left, scope, hint), Visit(right, scope, hint));
    }

    /// <summary>参与"同型"那一问的型：剥掉装箱（Convert(x, object)，C# 在 Equals(object) 上总会补一层）；
    /// 空常量回 null，表示它不参与这一问（ValueEquality 里那句账）。</summary>
    static Type? ComparedType(Expression node) => node switch
    {
        ConstantExpression { Value: null } => null,
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } box when box.Type == typeof(object)
            => ComparedType(box.Operand),
        _ => node.Type,
    };

    static IEnumerable AsEnumerable(object? value, Expression node)
        => value as IEnumerable ?? throw new UntranslatableExpressionException(node.ToString(), "IN 的集合必须是可求值的集合常量");

    /// <summary>剥掉数组 → Span&lt;T&gt;/ReadOnlySpan&lt;T&gt; 的隐式转换。.NET 10 起 ids.Contains(x)（ids 是数组）
    /// 绑到 MemoryExtensions.Contains(ReadOnlySpan&lt;T&gt;, T) 而不是 Enumerable.Contains，树上因此多一层
    /// op_Implicit；span 不是 IEnumerable，也不可能被常量求值认出来，而数组→span 是无损的，
    /// 所以回到原数组才是正确读法。</summary>
    static Expression SpanSource(Expression node)
        => node is MethodCallExpression { Method.Name: "op_Implicit", Method.DeclaringType: { IsGenericType: true } span, Arguments.Count: 1 } cast
           && IsSpan(span.GetGenericTypeDefinition())
            ? cast.Arguments[0]
            : node;

    // typeof(X) 不是常量，写不成 `is typeof(Span<>) or typeof(ReadOnlySpan<>)`。
    static bool IsSpan(Type definition) => definition == typeof(Span<>) || definition == typeof(ReadOnlySpan<>);

    SqlExpression InFromCollection(IEnumerable items, Expression targetNode, Scope scope, ColumnContract? hint)
    {
        var target = Visit(targetNode, scope, hint);
        var column = (target as ColumnExpression)?.Column ?? hint;
        var values = new List<SqlExpression>();

        foreach (var item in items)
            values.Add(Value(item, column));

        if (values.Count == 0)
            // 空集合特化成恒假，恒假的写法由方言决定。
            return new InlineLiteral(Provider.Dialect.EmptySetPredicate);

        // 按 provider 预算分块：整条 IN 一次发出，SQL Server 的 2100 参数上限、Oracle 的 1000 项 IN 列表上限
        // 会让大集合直接在驱动侧炸——Include 那条路早就按预算分块了，唯独用户谓词这条最高频的 IN 是漏网的。
        // 超预算发 x IN (…) OR x IN (…)：发射侧对混嵌 AND/OR 自动加括号（EmitLogical / EmitBooleanOperand），
        // NOT 包整个链条也括得对。
        var budget = Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var perChunk = Math.Max(1, StatementEmitter.RowsPerStatement(budget, 1));

        if (values.Count <= perChunk)
            return new InExpression(target, [.. values]);

        SqlExpression? chain = null;

        for (var first = 0; first < values.Count; first += perChunk)
        {
            var width = Math.Min(perChunk, values.Count - first);
            var condition = new InExpression(target, values.GetRange(first, width));

            chain = chain is null ? condition : new SqlBinaryExpression(BinaryOperator.Or, chain, condition);
        }

        return chain!;
    }

    // ── 分组链上的聚合 ────────────────────────────────────────────────

    /// <summary>这一颗 Enumerable 扩展方法是不是打在分组形参上（g.Count() / g.Sum(o => …)）。
    /// 判据只有一个：第一个实参是 Scope 里那个分组形参本身。不判方法的重载：Enumerable.Sum 有十来颗重载，
    /// 认重载名要反射比 MethodInfo，而这一格要的只是"宿主是不是那一组"。</summary>
    static bool IsOverGrouping(MethodCallExpression node, Scope scope, out GroupingShape grouping)
    {
        grouping = default;

        if (node.Object is not null || node.Arguments.Count == 0)
            return false;

        return node.Arguments[0] is ParameterExpression parameter && scope.TryGroupingOf(parameter, out grouping);
    }

    /// <summary>g.Count() → COUNT(*)，g.Sum(o => o.Amount) → SUM([t0].[Amount])。发射用的模板与聚合出口
    /// 那一条（QueryCompiler.Aggregate）取的是同一张注册表，方言不需要为分组新登记任何东西；区别只在参数：
    /// 那里的聚合是整句的选列，这里的聚合是一颗投影槽位或 HAVING 的一侧，组由 GROUP BY 划，没有第二层 FROM。</summary>
    SqlExpression GroupAggregate(MethodCallExpression node, Scope scope)
    {
        IsOverGrouping(node, scope, out var grouping);

        var countLike = node.Method.Name is "Count" or "LongCount";

        if (!countLike && node.Method.Name is not ("Sum" or "Max" or "Min" or "Average"))
            throw new UntranslatableExpressionException(node.ToString(),
                "分组链上可用的聚合只有 g.Count()、g.Sum(o => o.列)、g.Max/Min/Average(o => o.列)；" +
                "其余形状（Any/First/Distinct/带条件的计数…）在 GROUP BY 句里没有对应物，本版本未接入");

        if (countLike)
        {
            if (node.Arguments.Count != 1)
                throw new UntranslatableExpressionException(node.ToString(),
                    "带条件的计数（g.Count(o => …)）要下推成 COUNT(CASE WHEN … THEN 1 END)，本版本未接入");

            // LongCount 与 Count 发同一句 COUNT(*)：库里那一列本来就是整数，
            // 读回 int 还是 long 由投影槽位的声明类型定。
            return Template(QuickFn.Count, [new StarExpression(null)]);
        }

        if (node.Arguments.Count != 2 || StripQuote(node.Arguments[1]) is not LambdaExpression selector)
            throw new UntranslatableExpressionException(node.ToString(),
                $"g.{node.Method.Name}(…) 需要一个列选择器，例如 g.{node.Method.Name}(o => o.Amount)");

        if (selector.Parameters.Count != 1)
            throw new UntranslatableExpressionException(node.ToString(),
                "分组聚合的选择器只有一个形参（组里的一行）");

        var symbol = node.Method.Name switch
        {
            "Sum" => QuickFn.Sum,
            "Max" => QuickFn.Max,
            "Min" => QuickFn.Min,
            _ => QuickFn.Average,
        };

        // 组里的行就是那张表的行，所以形参绑回同一个表位：这一格不建子查询、不给别名，
        // 与集合导航那条路（TryNavigationSubquery 里另起一个表位 + 关联条件）是两种形状。
        var argument = Visit(selector.Body, scope.Derive(grouping.Table, selector.Parameters[0]));

        // SUM(ROW_NUMBER() OVER (…)) 是"窗口值还没算完就要开始数"的句子，库里那一格不收。
        // 位置名与顶层那一颗（"顶层聚合的参数位"）分开写：两处的改写建议不一样，消息撞在一起就分不出是谁拒的。
        WindowGuard.RejectRowNumber(argument, "分组聚合的参数位");

        return Template(symbol, [argument]);
    }

    // ── 关联子查询（集合导航上的 Any / Count） ────────────────────────

    /// <summary>把 o.Items.Any(...) / o.Items.Count(...) 翻译成关联子查询。检测模式：Arguments[0] 是
    /// parameter.Navigation，且该成员不是列而是集合导航。关联条件由子端的外键列指向父端的主键列拼出
    /// （与 IncludeMany 的 FK 发现逻辑同一套约定）。</summary>
    bool TryNavigationSubquery(MethodCallExpression node, Scope scope, out SqlExpression result)
    {
        result = null!;

        var source = StripConvert(node.Arguments[0]);

        if (source is not MemberExpression { Expression: ParameterExpression parentParam } memberAccess)
            return false;

        if (!scope.Contains(parentParam))
            return false;

        var parentTable = scope.TableOf(parentParam);
        var parentContract = parentTable.Contract;

        if (parentContract is null)
            return false;

        var navName = memberAccess.Member.Name;

        if (parentContract.ByMember.ContainsKey(navName))
            return false;

        var childType = CollectionElementType(memberAccess.Type);

        if (childType is null)
            return false;

        EntityContract childContract;

        try { childContract = _schemas.Contract(childType); }
        catch { return false; }

        IReadOnlyList<string> fkColumnNames;

        try { fkColumnNames = DiscoverFkColumnsForSubquery(parentContract, childContract, navName); }
        catch { return false; }

        var parentKeys = parentContract.PrimaryKey;

        if (parentKeys.Length == 0 || fkColumnNames.Count != parentKeys.Length)
            return false;

        var childTable = _newTableRef(childType, _subqueryIndex++);

        SqlExpression? correlation = null;

        for (var i = 0; i < parentKeys.Length; i++)
        {
            var fkColumn = childContract.ByColumn[fkColumnNames[i]];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal,
                new ColumnExpression(childTable, fkColumn),
                new ColumnExpression(parentTable, parentKeys[i]));
            correlation = correlation is null ? equal : new SqlBinaryExpression(BinaryOperator.And, correlation, equal);
        }

        SqlExpression? filter = null;

        if (node.Arguments.Count == 2 && node.Arguments[1] is LambdaExpression predicateLambda)
        {
            var subScope = scope.DeriveInner(childTable, predicateLambda.Parameters[0]);
            var visited = Visit(predicateLambda.Body, subScope);
            filter = Booleanized(predicateLambda.Body, visited);
        }

        SqlExpression? where = correlation;

        if (filter is not null)
            where = where is null ? filter : new SqlBinaryExpression(BinaryOperator.And, where, filter);

        if (node.Method.Name == "Any")
        {
            result = new ExistsExpression(new SelectStatement
            {
                Columns = [new Projection(new InlineLiteral("1"), null)],
                From = new SourceTable(childTable),
                Where = where,
            });
        }
        else
        {
            result = new ScalarSubqueryExpression(new SelectStatement
            {
                Columns = [new Projection(new FunctionExpression("COUNT(*)", []), null)],
                From = new SourceTable(childTable),
                Where = where,
            });
        }

        return true;
    }

    // ── 非导航子查询（IQuery 闭包捕获） ──────────────────────────────

    /// <summary>检测闭包捕获的 QuickSqlQuery 上的终端方法调用，翻译成子查询 AST。形态：subQuery.Count() →
    /// 标量子查询；subQuery.Contains(x) → IN 子查询；subQuery.Sum/Max/Min/Average(selector) → 聚合标量子查询；
    /// Enumerable.Contains(subQuery, x) → IN 子查询（扩展方法后备）。检测在 Call 最前面跑，早于所有现有的
    /// 方法分派——否则 Contains 会走集合常量路径、Count 会落到"未注册映射"的报错。</summary>
    bool TryIQuerySubquery(MethodCallExpression node, Scope scope, ColumnContract? hint, [NotNullWhen(true)] out SqlExpression? result)
    {
        result = null;

        if (node.Object is null || ReferencesScope(node.Object, scope))
            return false;

        var engine = ExtractQuery(node.Object);

        if (engine is null)
            return false;

        switch (node.Method.Name)
        {
            case "Count" when node.Arguments.Count == 0:
                result = HandleIQueryCount(engine);
                return true;

            case "Sum" or "Max" or "Min" or "Average" when node.Arguments.Count == 1 && StripQuote(node.Arguments[0]) is LambdaExpression lambda:
                result = HandleIQueryAggregate(engine, node.Method.Name, lambda);
                return true;

            case "Contains" when node.Arguments.Count == 1:
                result = HandleIQueryContains(engine, node.Arguments[0], scope, hint);
                return true;
        }

        if (node.Method.Name == "Contains" && node.Arguments.Count is 2 or 3
            && (node.Method.DeclaringType == typeof(Enumerable) || node.Method.DeclaringType == typeof(MemoryExtensions)))
        {
            var sourceEngine = ExtractQuery(SpanSource(node.Arguments[0]));

            if (sourceEngine is not null)
            {
                if (node.Arguments.Count == 3 && node.Arguments[2] is not ConstantExpression { Value: null })
                    throw new UntranslatableExpressionException(node.ToString(),
                        "IN 子查询的元素相等规则要下推到库里才保得住语义，本版本只接受默认比较器。请去掉自定义比较器。");

                result = HandleIQueryContains(sourceEngine, node.Arguments[1], scope, hint);
                return true;
            }
        }

        return false;
    }

    /// <summary>从表达式节点提取运行期的 QueryEngine。闭包捕获的 QuickSqlQuery 在表达式树里是一串
    /// MemberExpression（闭包字段访问链），Evaluate 能把它求回运行期对象；然后走 IEngineCarrier 这一型判断拿引擎。
    /// 只接受"简单变量引用"：查询上再链了 Where/Select 等方法调用时，lambda 里可能引用外层参数（关联子查询），
    /// Compile 退路拿不到外层参数会炸，所以这一律拒绝；用户需要先把子查询存进变量再调终端方法。</summary>
    static QueryEngine? ExtractQuery(Expression expr)
    {
        // 先挡掉方法调用链（那串只能靠 Compile 求值，引用外层参数时会炸），再做类型判断。
        if (!IsSimpleClosureReference(expr))
            return null;

        // 原先这一处是 type.GetProperty("Engine", …)?.GetValue(obj)：每次编译一句关联子查询都反射一次。
        // 换成内部接口后，"链式对象带引擎"这件事写在类型上，而不是写在一串按名字找成员的运行期约定里。
        return Evaluate(expr) is IEngineCarrier carrier ? carrier.Engine : null;
    }

    /// <summary>判表达式是否"简单闭包变量引用"——一串 MemberExpression 字段访问，不含方法调用。
    /// 方法调用链（如 query.Where(...)）的 lambda 里可能引用外层参数，Evaluate 的 Compile 退路拿不到会炸。</summary>
    static bool IsSimpleClosureReference(Expression expr) => expr switch
    {
        ConstantExpression => true,
        MemberExpression m => m.Expression is null || IsSimpleClosureReference(m.Expression),
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
            => IsSimpleClosureReference(u.Operand),
        _ => false,
    };

    SqlExpression HandleIQueryCount(QueryEngine engine)
    {
        var effective = engine.Effective();
        var aggregateModel = engine.AggregateModel(effective, AggregateKind.Count, null);
        var ast = RemapSubqueryAliases(engine.Compile(aggregateModel));
        return new ScalarSubqueryExpression(ast);
    }

    SqlExpression HandleIQueryAggregate(QueryEngine engine, string methodName, LambdaExpression selector)
    {
        var kind = methodName switch
        {
            "Sum" => AggregateKind.Sum,
            "Max" => AggregateKind.Max,
            "Min" => AggregateKind.Min,
            "Average" => AggregateKind.Average,
            _ => throw new UntranslatableExpressionException(methodName, null),
        };

        var effective = engine.Effective();
        var aggregateModel = engine.AggregateModel(effective, kind, selector);
        var ast = RemapSubqueryAliases(engine.Compile(aggregateModel));
        return new ScalarSubqueryExpression(ast);
    }

    SqlExpression HandleIQueryContains(QueryEngine engine, Expression targetNode, Scope scope, ColumnContract? hint)
    {
        var target = Visit(targetNode, scope, hint);
        var effective = engine.Effective();
        var ast = RemapSubqueryAliases(engine.Compile(effective));
        return new InExpression(target, Subquery: ast);
    }

    /// <summary>子查询的 SelectStatement 里的 TableRef 来自子查询自己的引擎，别名总是 t0、t1……
    /// 嵌进外层 WHERE 后和外层的别名撞车——SUM(t0.Amount) 指的到底是子查询的表还是外层的表？SQL 引擎按
    /// 最近绑定选，结果就指错了。这一手把子查询 AST 里所有 TableRef 按 _subqueryIndex 重发一套不冲突的别名，
    /// 与导航子查询同一套策略。</summary>
    SelectStatement RemapSubqueryAliases(SelectStatement stmt)
    {
        var map = new Dictionary<TableRef, TableRef>();
        CollectTableRefs(stmt, map);
        foreach (var kv in map.Keys)
        {
            var idx = _subqueryIndex++;
            map[kv] = new TableRef(idx, QuickSqlOrm.AliasAt(idx), kv.Table, kv.Contract);
        }
        return RemapStatement(stmt, map);
    }

    static void CollectTableRefs(SelectStatement stmt, Dictionary<TableRef, TableRef> map)
    {
        if (stmt.From is not null) CollectTableRefs(stmt.From, map);
        foreach (var j in stmt.Joins) CollectTableRefs(j.Source, map);
        foreach (var c in stmt.Columns) CollectTableRefs(c.Node, map);
        if (stmt.Where is not null) CollectTableRefs(stmt.Where, map);
        foreach (var g in stmt.GroupBy) CollectTableRefs(g, map);
        if (stmt.Having is not null) CollectTableRefs(stmt.Having, map);
        foreach (var o in stmt.OrderBy) CollectTableRefs(o.Node, map);
        if (stmt.Offset is not null) CollectTableRefs(stmt.Offset, map);
        if (stmt.Limit is not null) CollectTableRefs(stmt.Limit, map);
    }

    static void CollectTableRefs(TableSource src, Dictionary<TableRef, TableRef> map)
    {
        map.TryAdd(src.Ref, src.Ref);
        switch (src)
        {
            case SourceSubquery sub: CollectTableRefs(sub.Query, map); break;
            // 派生表内层引用的是它自己那份 Root，而它与外层的 Ref 是同一个对象 —— 不下钻就得不到改名，
            // 于是内层的 [t0] 与外层改出来的 [t6] 指向两套别名，嵌进外层 WHERE 的那一句会指错列。
            case SourceDerived derived: CollectTableRefs(derived.Query, map); break;
            // 分表那一份是 N 个内层，每一个的 Root 都是它自己那张分片表位（别名同一个），下钻要逐个下。
            case SourceUnion union: foreach (var branch in union.Branches) CollectTableRefs(branch, map); break;
        }
    }

    static void CollectTableRefs(SqlExpression expr, Dictionary<TableRef, TableRef> map)
    {
        switch (expr)
        {
            case ColumnExpression c: map.TryAdd(c.Table, c.Table); break;
            case StarExpression s when s.Table is not null: map.TryAdd(s.Table, s.Table); break;
            case SqlBinaryExpression b: CollectTableRefs(b.Left, map); CollectTableRefs(b.Right, map); break;
            case SqlUnaryExpression u: CollectTableRefs(u.Operand, map); break;
            case FunctionExpression f: foreach (var a in f.Arguments) CollectTableRefs(a, map); break;
            // OVER 里那两段引用的是外层句子的表位（本节点不开新 FROM 位），下钻漏一段，
            // 子查询捕获改名时内层就少改一颗列前缀 —— 得到的是一句指错列的 SQL，而不是一句报错。
            case RowNumberExpression w:
                foreach (var p in w.PartitionBy) CollectTableRefs(p, map);
                foreach (var o in w.OrderBy) CollectTableRefs(o.Node, map);
                break;
            case CastExpression ca: CollectTableRefs(ca.Operand, map); break;
            case InExpression ie:
                CollectTableRefs(ie.Target, map);
                if (ie.Values is not null) foreach (var v in ie.Values) CollectTableRefs(v, map);
                if (ie.Subquery is not null) CollectTableRefs(ie.Subquery, map);
                break;
            case ExistsExpression ee: CollectTableRefs(ee.Query, map); break;
            case ScalarSubqueryExpression se: CollectTableRefs(se.Query, map); break;
            case CaseExpression ce:
                if (ce.Operand is not null) CollectTableRefs(ce.Operand, map);
                foreach (var w in ce.Whens) { CollectTableRefs(w.When, map); CollectTableRefs(w.Then, map); }
                if (ce.Else is not null) CollectTableRefs(ce.Else, map);
                break;
        }
    }

    static SelectStatement RemapStatement(SelectStatement stmt, Dictionary<TableRef, TableRef> map)
    {
        return stmt with
        {
            From = stmt.From is not null ? RemapTableSource(stmt.From, map) : null,
            Joins = stmt.Joins.Select(j => j with
            {
                Source = RemapTableSource(j.Source, map),
                On = j.On is not null ? RemapExpression(j.On, map) : null,
            }).ToArray(),
            Columns = stmt.Columns.Select(c => c with { Node = RemapExpression(c.Node, map) }).ToArray(),
            Where = stmt.Where is not null ? RemapExpression(stmt.Where, map) : null,
            GroupBy = stmt.GroupBy.Select(g => RemapExpression(g, map)).ToArray(),
            Having = stmt.Having is not null ? RemapExpression(stmt.Having, map) : null,
            OrderBy = stmt.OrderBy.Select(o => o with { Node = RemapExpression(o.Node, map) }).ToArray(),
            Offset = stmt.Offset is not null ? RemapExpression(stmt.Offset, map) : null,
            Limit = stmt.Limit is not null ? RemapExpression(stmt.Limit, map) : null,
        };
    }

    static TableSource RemapTableSource(TableSource src, Dictionary<TableRef, TableRef> map)
    {
        var newRef = map.TryGetValue(src.Ref, out var nr) ? nr : src.Ref;
        return src switch
        {
            SourceTable => new SourceTable(newRef),
            SourceSubquery sub => new SourceSubquery(newRef, RemapStatement(sub.Query, map)),
            SourceDerived derived => new SourceDerived(newRef, RemapStatement(derived.Query, map)),
            SourceUnion union => new SourceUnion(newRef, [.. union.Branches.Select(b => RemapStatement(b, map))], union.Operation),
            // 原生文本这一形没有可改名的列引用（那一段一颗不动），但别名要跟着改：外层那句的
            // WHERE 里的 [t1000].[Amount] 已经被改名，而 FROM (…) 挂的名字若还停在 [t0]，
            // 得到的是一句"无法绑定多部分标识符"。
            SourceRawText raw => new SourceRawText(newRef, raw.Sql, raw.Parameters),
            _ => src,
        };
    }

    static SqlExpression RemapExpression(SqlExpression expr, Dictionary<TableRef, TableRef> map)
    {
        switch (expr)
        {
            case ColumnExpression c:
                var t = map.TryGetValue(c.Table, out var nt) ? nt : c.Table;
                return ReferenceEquals(t, c.Table) ? expr : new ColumnExpression(t, c.Column);
            case StarExpression s when s.Table is not null:
                var st = map.TryGetValue(s.Table, out var snt) ? snt : s.Table;
                return ReferenceEquals(st, s.Table) ? expr : new StarExpression(st);
            case SqlBinaryExpression b:
                var bl = RemapExpression(b.Left, map);
                var br = RemapExpression(b.Right, map);
                return ReferenceEquals(bl, b.Left) && ReferenceEquals(br, b.Right) ? expr : new SqlBinaryExpression(b.Operator, bl, br);
            case SqlUnaryExpression u:
                var uo = RemapExpression(u.Operand, map);
                return ReferenceEquals(uo, u.Operand) ? expr : new SqlUnaryExpression(u.Operator, uo);
            case FunctionExpression f:
                var fa = f.Arguments.Select(a => RemapExpression(a, map)).ToArray();
                return fa.SequenceEqual(f.Arguments) ? expr : new FunctionExpression(f.Template, fa);
            case RowNumberExpression w:
                // 与 CollectTableRefs 那一段成对：那里收的下钻，这里必须给对应的改写，
                // 否则 OVER 里的列前缀停在旧别名，而外层改过名之后那一句指的是不存在的表。
                var wp = w.PartitionBy.Select(a => RemapExpression(a, map)).ToArray();
                var wo = w.OrderBy.Select(o => o with { Node = RemapExpression(o.Node, map) }).ToArray();
                return wp.SequenceEqual(w.PartitionBy) && wo.SequenceEqual(w.OrderBy) ? expr : new RowNumberExpression(wp, wo);
            case CastExpression ca:
                var co = RemapExpression(ca.Operand, map);
                return ReferenceEquals(co, ca.Operand) ? expr : new CastExpression(co, ca.StoreType);
            case InExpression ie:
                var it = RemapExpression(ie.Target, map);
                var iv = ie.Values?.Select(v => RemapExpression(v, map)).ToArray();
                var isq = ie.Subquery is not null ? RemapStatement(ie.Subquery, map) : null;
                return ReferenceEquals(it, ie.Target) && (iv is null || iv.SequenceEqual(ie.Values!)) && ReferenceEquals(isq, ie.Subquery)
                    ? expr : new InExpression(it, iv, ie.Negated, isq);
            case ExistsExpression ee:
                var eq = RemapStatement(ee.Query, map);
                return ReferenceEquals(eq, ee.Query) ? expr : new ExistsExpression(eq, ee.Negated);
            case ScalarSubqueryExpression se:
                var sq = RemapStatement(se.Query, map);
                return ReferenceEquals(sq, se.Query) ? expr : new ScalarSubqueryExpression(sq);
            case CaseExpression ce:
                var cOp = ce.Operand is not null ? RemapExpression(ce.Operand, map) : null;
                var cWhens = ce.Whens.Select(w => new WhenClause(RemapExpression(w.When, map), RemapExpression(w.Then, map))).ToArray();
                var cElse = ce.Else is not null ? RemapExpression(ce.Else, map) : null;
                return new CaseExpression(cOp, cWhens, cElse);
            default:
                return expr;
        }
    }

    static Expression StripConvert(Expression node) => node switch
    {
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u => StripConvert(u.Operand),
        _ => node,
    };

    /// <summary>剥掉 Expression&lt;T&gt; 参数在表达式树里的 Quote 包裹。
    /// 方法形参是 Expression&lt;Func&lt;T,TResult&gt;&gt; 时，调用方的 lambda 在树上多一层 Quote。</summary>
    static Expression StripQuote(Expression node) => node is UnaryExpression { NodeType: ExpressionType.Quote } q ? StripQuote(q.Operand) : node;

    [RequiresUnreferencedCode("按运行期 Type 反射判集合元素型。")]
    [RequiresDynamicCode("按运行期 Type 反射判集合元素型。")]
    static Type? CollectionElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>子查询版的 FK 发现：与 QueryEngine/DbSet 里那两份逻辑同三档（点名那一档走
    /// ContractResolver.ResolveDeclaredFkColumns，同一根边不许两个答案），
    /// 但只返回列名清单、不抛带上下文的异常（调用方按 return false 处理）。</summary>
    static IReadOnlyList<string> DiscoverFkColumnsForSubquery(EntityContract parentContract, EntityContract childContract, string memberName)
    {
        var parentKeys = parentContract.PrimaryKey;

        foreach (var rel in childContract.Relationships)
        {
            if (rel.TargetType == parentContract.ClrType)
                return rel.ColumnNames;
        }

        if (ContractResolver.ResolveDeclaredFkColumns(parentContract, childContract, memberName) is { } declaredColumns)
            return declaredColumns;

        var parentTypeName = parentContract.ClrType.Name;

        if (parentKeys.Length == 1)
        {
            foreach (var conventionName in new[] { $"{parentTypeName}Id", $"{parentTypeName}ID" })
            {
                if (childContract.ByColumn.ContainsKey(conventionName))
                    return [conventionName];
            }

            var byColumnName = $"{parentTypeName}{parentKeys[0].ColumnName}";

            if (childContract.ByColumn.ContainsKey(byColumnName))
                return [byColumnName];
        }

        var compositeNames = new List<string>(parentKeys.Length);

        foreach (var pk in parentKeys)
        {
            var name = $"{parentTypeName}{pk.ColumnName}";

            if (!childContract.ByColumn.ContainsKey(name))
                throw new QuickSqlException($"子实体 {childContract.ClrType.Name} 上没有指向 {parentContract.ClrType.Name} 的外键列");

            compositeNames.Add(name);
        }

        return compositeNames;
    }

    /// <summary>CLR 方法 → 符号函数键。返回 null 表示没有形态规则，交给 provider 的注册表按方法全名兜。</summary>
    string? SymbolicKey(MethodCallExpression node)
    {
        var declaring = node.Method.DeclaringType;
        var name = node.Method.Name;

        if (declaring == typeof(string))
            return name switch
            {
                "get_Length" => QuickFn.Length,
                "ToUpper" or "ToUpperInvariant" => QuickFn.Upper,
                "ToLower" or "ToLowerInvariant" => QuickFn.Lower,
                "Trim" when node.Arguments.Count == 0 => QuickFn.Trim,
                "TrimStart" when node.Arguments.Count == 0 => QuickFn.TrimStart,
                "TrimEnd" when node.Arguments.Count == 0 => QuickFn.TrimEnd,
                "Substring" => QuickFn.Substring,
                "Replace" => QuickFn.Replace,
                "IndexOf" => QuickFn.IndexOf,
                "Concat" => QuickFn.Concat,
                "ToString" when node.Arguments.Count == 0 => QuickFn.CastText,
                _ => null,
            };

        if (declaring == typeof(Math))
            return name switch
            {
                "Round" => QuickFn.Round,
                "Abs" => QuickFn.Abs,
                "Ceiling" => QuickFn.Ceiling,
                "Floor" => QuickFn.Floor,
                "Power" => QuickFn.Power,
                "Sqrt" => QuickFn.Sqrt,
                "Max" => QuickFn.Greatest,
                "Min" => QuickFn.Least,
                _ => null,
            };

        if (declaring is { } t && (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)))
            return name switch
            {
                "get_Year" => QuickFn.DateYear,
                "get_Month" => QuickFn.DateMonth,
                "get_Day" => QuickFn.DateDay,
                "get_Hour" => QuickFn.DateHour,
                "get_Minute" => QuickFn.DateMinute,
                "get_Second" => QuickFn.DateSecond,
                "get_Date" => QuickFn.ToDate,
                "AddYears" => QuickFn.AddYears,
                "AddMonths" => QuickFn.AddMonths,
                "AddDays" => QuickFn.AddDays,
                "AddHours" => QuickFn.AddHours,
                "AddMinutes" => QuickFn.AddMinutes,
                "AddSeconds" => QuickFn.AddSeconds,
                "get_Now" => QuickFn.Now,
                "get_UtcNow" => QuickFn.UtcNow,
                _ => null,
            };

        if (declaring == typeof(decimal))
            return name switch
            {
                "get_Value" => null,
                _ => null,
            };

        return null;
    }

    static string MethodKey(MethodInfo method) => (method.DeclaringType?.FullName ?? "global") + "." + method.Name;

    /// <summary>string.Concat(a,b,c) 折成两两嵌套的 CONCAT：模板是按元数登记的，
    /// 让 N 参直接进 CONCAT({0}, {1}) 会被 string.Format 静默丢掉第三个实参。</summary>
    SqlExpression FoldConcatenation(IReadOnlyList<SqlExpression> arguments)
    {
        if (arguments.Count == 0)
            return new ParameterValue(string.Empty, null);

        var result = arguments[0];

        for (var i = 1; i < arguments.Count; i++)
            result = Template(QuickFn.Concat, [result, arguments[i]]);

        return result;
    }

    // ── QuickFx 三口（日期范围/交集） ──────────────────────────────────

    /// <summary>QuickFx 三口。它们是谓词形状而不是函数调用：译文只由比较与 AND 组成，
    /// 十八家方言共用这一份，谁都不用登记模板（凡要落 FunctionExpression 的形状都得逐家给）。
    /// 名字/元数认不出时交回 null，让调用点走「未登记函数」的那句原话——QuickFx 将来添别的形状时，
    /// 漏在这颗计数的自动拿到可读的拒而不是静默。</summary>
    SqlExpression? FxShape(MethodCallExpression node, IReadOnlyList<SqlExpression> arguments, Scope scope)
    {
        switch ((node.Method.Name, arguments.Count))
        {
            case (nameof(QuickFx.DateRange), 3):
                // 上界的"+1 天"要在参数侧算：两颗边界必须能当场读出 DateOnly 值。
                // 拿查询行上的成员当边界（ReferencesScope）就得把日期算术落成方言函数，
                // 共用层不为这一档开方言知识 —— 拒并指路。先判 ReferencesScope 再 Evaluate：
                // 引着 scope 的子树直送 Evaluate，会撞「variable 'o' referenced from scope ''」那句没有路的内部错。
                if (ReferencesScope(node.Arguments[1], scope) || ReferencesScope(node.Arguments[2], scope)
                    || !TryEvaluateBound(node.Arguments[1], out var begin)
                    || !TryEvaluateBound(node.Arguments[2], out var end))
                    throw new UntranslatableExpressionException(node.ToString(),
                        "DateRange 的两颗边界要能当场读出 DateOnly 值（局部变量、闭包、内联常量都行）——" +
                        "拿查询行上的成员当边界时，「终态 +1 天」要落成方言日期算术，本库不在共用层做这一档。" +
                        "请改用 QuickFx.DateIntersection，或手写 date >= begin && date < end.AddDays(1) 自己控制边界。");

                // 闭端进、半开出：下一天在 C# 侧算好，列上零表达式、索引照走。
                // begin > end 是合法的空段（区间数学的空集：上界不大于下界，整句恒不中），照发不拦。
                // MaxValue 没有下一天，AddDays 会溢出 —— 上界那半边不发，只留 >= begin。
                var lower = new SqlBinaryExpression(
                    BinaryOperator.GreaterThanOrEqual, arguments[0], Value(begin, null));

                if (end == DateOnly.MaxValue)
                    return lower;

                return new SqlBinaryExpression(BinaryOperator.And, lower,
                    new SqlBinaryExpression(BinaryOperator.LessThan, arguments[0], Value(end.AddDays(1), null)));

            case (nameof(QuickFx.DateIntersection), 4):
            case (nameof(QuickFx.DateTimeIntersection), 4):
                // 闭端 [begin, end] 两段重叠：{0} <= {3} AND {1} >= {2}，
                // 对应手写 s.BeginDate <= To && s.EndDate >= From 的自然语序，口径等价、逐字节可比。
                return new SqlBinaryExpression(BinaryOperator.And,
                    new SqlBinaryExpression(BinaryOperator.LessThanOrEqual, arguments[0], arguments[3]),
                    new SqlBinaryExpression(BinaryOperator.GreaterThanOrEqual, arguments[1], arguments[2]));

            default:
                return null;
        }
    }

    /// <summary>行号四颗 → 一颗 RowNumberExpression。入口在 Call 的最上面（早于逐颗 Visit 实参那一段），
    /// 留在这里的是三档分工本身。判据取方法签名上第二颗形参的型：(orderKey, bool) 是方向档、
    /// (partitionKey, orderKey) 是分堆档，两档同元数而实参的型分不开它们——调用方拿一颗 bool 表达式当第二颗
    /// 实参写时，重载决议已经把那颗送上 bool 档了，此处照签名读到的就是它去的那一档，
    /// 于是拿到的是"方向那颗读不出值"这条指路，而不是一句静默的错句。</summary>
    SqlExpression RowNumber(MethodCallExpression node, Scope scope)
    {
        var signature = node.Method.GetParameters();
        var partitioned = signature.Length switch
        {
            1 => false,
            2 => signature[1].ParameterType != typeof(bool),
            3 => true,
            // 元数这一道判据今天只能在这里：入口那颗只按名字分派（早于逐颗 Visit 实参那一段），
            // 少了这一行，将来添一颗四元的重载就会把多出来的实参静默丢掉。
            _ => throw new UntranslatableExpressionException(node.ToString(),
                $"RowNumber 只认 1~3 元这一族，这一颗是 {signature.Length} 元：多键分区与多键序今天都没有句形，不接。"),
        };

        var orderKey = partitioned ? node.Arguments[1] : node.Arguments[0];
        var order = WindowKey(orderKey, scope, "序键");

        IReadOnlyList<SqlExpression> partition = partitioned ? [WindowKey(node.Arguments[0], scope, "分堆键")] : [];

        var descending = (partitioned, signature.Length) switch
        {
            (false, 2) => WindowDirection(node.Arguments[1], scope),
            (true, 3) => WindowDirection(node.Arguments[2], scope),
            _ => false,
        };

        return new RowNumberExpression(partition, [new OrderExpression(order, descending)]);
    }

    /// <summary>OVER 里的一颗键：先问它挂不挂在查询行上，再翻，翻完不许里面已经藏了一颗行号。
    /// 常量序键在这里就是拒，不是发：ORDER BY @p0 在库里是一句能执行的话，而它给的是"任意序"，
    /// 行号于是随执行计划漂——那种句子绿在测试里、错在报表上。判 ReferencesScope 早于 Visit：
    /// 引着行的子树直送 Visit 各按各的路走，而不引用行的那颗到了 Visit 会折成参数，报错的人就变成库里那个人了。</summary>
    SqlExpression WindowKey(Expression key, Scope scope, string role)
    {
        if (!ReferencesScope(key, scope))
            throw new UntranslatableExpressionException(key.ToString(),
                $"行号的{role}要落在查询行上（列，或列的可翻译表达式）：不引用行的实参进库是一颗常量，" +
                "按常量排出来的行号没有可复现的序。整集行号就写 RowNumber(o.主键)，只要头一行请改用 FirstOrDefault()。");

        var node = Visit(key, scope);

        if (WindowGuard.HasRowNumber(node))
            throw new UntranslatableExpressionException(key.ToString(),
                "OVER 里再套 OVER（行号的序键或分堆键自己又是一颗行号）不是任何一家的合法句子。" +
                "要两级序号就分两句：内层先出第一级、外层再按那一列排，而内层那一句今天只能走 WithSql 或原生 SQL（文档站 14 篇）。");

        return node;
    }

    /// <summary>方向那颗要当场读得出值：ORDER BY 列 DESC 是句子的形状，不是每行各问一次的东西。
    /// 落在行上的 bool 成员在这一档已经被重载决议送进来了（RowNumber 头部那句），
    /// 所以这句要把那条重载的分岔说出来，否则用户看到的是"我明明写的是排序键"。</summary>
    static bool WindowDirection(Expression node, Scope scope)
    {
        if (ReferencesScope(node, scope))
            throw new UntranslatableExpressionException(node.ToString(),
                "RowNumber 的第二颗是方向（bool），要能当场读出值（局部变量、闭包、内联常量都行）：" +
                "一句 SELECT 里只能有一种序。想按一颗 bool 列排行号，换一元那颗 RowNumber(orderKey: …)，" +
                "它没有方向位、也就没有这一问；要分堆又按它排就写具名实参 " +
                "RowNumber(partitionKey: …, orderKey: …)——具名之后重载决议才认得你想要哪一颗。");

        return Evaluate(node) is true;
    }

    /// <summary>DateRange 的边界求值：能读出 DateOnly 就交真值；装在装箱 Convert 里的闭包
    /// 先剥一层再求值。其余形状（null、似是而非的型）汇总交 false，由调用点的同一条消息收拢。</summary>
    static bool TryEvaluateBound(Expression bound, out DateOnly value)
    {
        value = default;

        var day = (bound switch
        {
            ConstantExpression constant => constant.Value,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } cover
                when cover.Type == typeof(DateOnly) || cover.Type == typeof(object) => Evaluate(cover.Operand),
            _ => Evaluate(bound),
        }) as DateOnly?;

        if (day is not { } typed)
            return false;

        value = typed;

        return true;
    }

    SqlExpression Template(string symbol, IReadOnlyList<SqlExpression> arguments)
    {
        // 键带元数：同一符号在 1 参与 2 参下模板不同（Math.Round），少了这一步 string.Format 会静默丢掉多余实参。
        var key = QuickFn.Key(symbol, arguments.Count);

        return Provider.Expressions.TryGetFunction(key, out var mapping)
            ? new FunctionExpression(mapping.Template, arguments)
            : throw new UntranslatableExpressionException(key, $"{Provider.Kind} 未登记函数 {key}（可用 provider.Expressions.Register 覆写）");
    }

    /// <summary>pattern 就是最终模式串：通配符有意义，本方法不再转义它。
    /// 用户给的字面量要先进 EscapeLike 再补通配符，反了会把通配符一起转义掉。</summary>
    public SqlExpression Like(SqlExpression target, string pattern, ColumnContract? hint)
        => new SqlBinaryExpression(BinaryOperator.Like, target, new ParameterValue(pattern, hint));

    /// <summary>转义用户输入里被当成字面量的通配符，否则 "50%" 会匹配到 "50abc"。
    /// 转义符自身要先替换，不然第二步的 % 会被当成刚写下的通配符转义。</summary>
    string EscapeLike(string literal)
    {
        var escape = Provider.Expressions.LikeEscapeCharacter.ToString();

        return literal.Replace(escape, escape + escape, StringComparison.Ordinal)
                      .Replace("%", escape + "%", StringComparison.Ordinal)
                      .Replace("_", escape + "_", StringComparison.Ordinal);
    }

    /// <summary>StartsWith / EndsWith / Contains(string) 的 LIKE 形态。先转义字面量、后补通配符：
    /// 两个动作对调，通配符就被 EscapeLike 一起吞了，Contains 会静默退化成全等匹配（少行而不是报错）。</summary>
    SqlExpression StringLike(MethodCallExpression node, Scope scope)
    {
        var target = Visit(node.Object!, scope, null);
        var pattern = Evaluate(node.Arguments[0]) as string
                      ?? throw new UntranslatableExpressionException(node.ToString(), "LIKE 的模式串必须是可求值的字符串");
        var hint = (target as ColumnExpression)?.Column;
        var literal = EscapeLike(pattern);

        return node.Method.Name switch
        {
            "StartsWith" => Like(target, literal + "%", hint),
            "EndsWith" => Like(target, "%" + literal, hint),
            "Contains" => Like(target, "%" + literal + "%", hint),
            _ => throw new UntranslatableExpressionException(node.Method.Name, null),
        };
    }
}

static class DbProviderExtensions
{
    /// <summary>只为 CAST 目标服务：拿一个干净的探针契约问 provider 要类型名。</summary>
    public static string? StoreTypeOf(this IDbProvider provider, Type clrType)
    {
        var probe = new ColumnContract
        {
            MemberName = "cast",
            ClrType = clrType,
            ColumnName = string.Empty,
            LengthSemantics = LengthSemantics.Char,
            Unicode = true,
            IsNullable = false,
            IsPrimary = false,
            IsIdentity = false,
            MaxChars = clrType == typeof(string) ? 4000 : null,
            MaxBytes = null,
            Precision = null,
            Scale = null,
            Origin = new OriginTrace(MetadataLayer.ProviderFallback, "CAST 目标类型"),
        };

        return provider.Dialect.GetStoreType(provider.Types.ApplyStorageDefaults(probe));
    }
}
