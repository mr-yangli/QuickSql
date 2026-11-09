using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using QuickSql.Materialization;
using QuickSql.Metadata;
using Da = System.ComponentModel.DataAnnotations;

namespace QuickSql.Schema;

/// <summary>G2 兜底实现：启动期用表达式树编译读写器，之后热路径零反射。
/// G1 源生成器产出同样形状的代码，两者由对拍用例断言行为一致。</summary>
[RequiresUnreferencedCode("G2 兜底实现：按成员名反射定位属性/字段并读取注解。")]
[RequiresDynamicCode("G2 兜底实现：用表达式树编译读写器。")]
public sealed class ReflectionEntitySchema<T> : IEntityTypeSchema<T> where T : class
{
    readonly Func<T> _factory;
    readonly Action<T, object?>[] _setters;
    readonly Func<T, object?>[] _getters;

    public ReflectionEntitySchema(EntityContract contract)
    {
        Contract = contract;

        // 无参构造不问可见性（protected X() 那一族实体），判据与出处见 MemberAccessors
        var ctor = MemberAccessors.ParameterlessConstructor(typeof(T))
                   ?? throw new QuickSqlException(
                       $"实体 {typeof(T).Name} 没有无参构造器，无法物化（公共与内部、受保护、私有都查过）");

        _factory = Expression.Lambda<Func<T>>(Expression.New(ctor)).Compile();
        _setters = new Action<T, object?>[contract.Columns.Length];
        _getters = new Func<T, object?>[contract.Columns.Length];

        for (var i = 0; i < contract.Columns.Length; i++)
            (_setters[i], _getters[i]) = BuildAccessors(contract.Columns[i].MemberName);

        Reader = BuildReader(contract);
    }

    public EntityContract Contract { get; }

    public Func<DbDataReader, ReadPlan, T> Reader { get; }

    public T New() => _factory();

    public void SetValue(T entity, int ordinal, object? value) => _setters[ordinal](entity, value);

    public object? GetValue(T entity, int ordinal) => _getters[ordinal](entity);

    public T Read(DbDataReader reader, ReadPlan plan) => Reader(reader, plan);

    (Action<T, object?>, Func<T, object?>) BuildAccessors(string memberName)
    {
        var member = ResolveMember(memberName);
        var entity = Expression.Parameter(typeof(T), "e");
        var value = Expression.Parameter(typeof(object), "v");
        var converted = Expression.Call(ConvertMethod.MakeGenericMethod(MemberType(member)), value);

        var setter = Expression.Lambda<Action<T, object?>>(Expression.Assign(MemberAccess(entity, member), converted), entity, value).Compile();
        var getter = Expression.Lambda<Func<T, object?>>(Expression.Convert(MemberAccess(entity, member), typeof(object)), entity).Compile();

        return (setter, getter);
    }

    /// <summary>编译这一型的行读取器。每一列两条臂，而两条臂都不是这一处自己实现的读法：
    /// 有臂的列进 DirectRead.ReadXxx（那一臂的非直读分支就是装箱口），没臂的列（枚举、byte[]、DateOnly…）
    /// 直接进装箱口。于是 G1 的模板与这一处是按同一批方法名排的队，对拍才比的是同一件事。</summary>
    Func<DbDataReader, ReadPlan, T> BuildReader(EntityContract contract)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "r");
        var plan = Expression.Parameter(typeof(ReadPlan), "p");
        var offset = Expression.Property(plan, nameof(ReadPlan.Offset));
        var ops = Expression.Property(plan, nameof(ReadPlan.Ops));
        // 取数口一次查好、逐列复用。写成局部 var 而不是静态 MethodInfo 字段，
        // 是为了不往 src/ 里再添一处反射字样（这一处与下面 ConvertMethod 那处同性质）。
        var read = typeof(ValueConverter).GetMethod(nameof(ValueConverter.Read),
            BindingFlags.Public | BindingFlags.Static, null, [typeof(DbDataReader), typeof(int)], null)!;
        var entity = Expression.Variable(typeof(T), "e");
        // 无参构造与上面那颗工厂同源（可见性不问），这里以前是 `!` 硬截——真没有就 NRE，
        // 而同一个缺口的两半不该一半抛得准、一半抛得莫名其妙
        var entityCtor = MemberAccessors.ParameterlessConstructor(typeof(T))
            ?? throw new QuickSqlException(
                $"实体 {typeof(T).Name} 没有无参构造器，无法物化（公共与内部、受保护、私有都查过）");

        var body = new List<Expression> { Expression.Assign(entity, Expression.New(entityCtor)) };

        for (var i = 0; i < contract.Columns.Length; i++)
        {
            var member = ResolveMember(contract.Columns[i].MemberName);
            var declared = MemberType(member);

            // 按声明类型（含 Nullable）转换：null 落进 int? 才是 null，落进 int 会变成 0——
            // 后者是"读出来看不出问题、写回去把 NULL 变成 0"这类事故的源头。
            // 取数一律走 ValueConverter.Read：这一处自己不判断类型、也不直接 GetValue，
            // 所以"某一列怎么读"只有一个地方可以改。
            var ordinal = Expression.Add(offset, Expression.Constant(i));

            // 臂的句柄是从方法组转换出来的委托上取的（Delegate.Method），不是反射查找；
            // 委托对象在这里只当句柄用，编译完就丢，每行调的是那个静态方法本身。
            Expression value = DirectRead.ArmFor(declared) is { } arm
                ? Expression.Call(arm.Method, reader, ordinal, Expression.ArrayIndex(ops, Expression.Constant(i)))
                : Expression.Call(read.MakeGenericMethod(declared), reader, ordinal);

            body.Add(Expression.Assign(MemberAccess(entity, member), Expression.Convert(value, declared)));
        }

        body.Add(entity);

        return Expression.Lambda<Func<DbDataReader, ReadPlan, T>>(Expression.Block([entity], body), reader, plan).Compile();
    }

    /// <summary>给 SetValue 那一档用的：那里的值已经是 object（外面递进来什么就是什么），
    /// 没有序号可问，所以只能进 To&lt;T&gt;(object) 这扇门。</summary>
    static readonly MethodInfo ConvertMethod =
        typeof(ValueConverter).GetMethod(nameof(ValueConverter.To), BindingFlags.Public | BindingFlags.Static, null, [typeof(object)], null)!;

    static Expression MemberAccess(Expression entity, MemberInfo member) => member switch
    {
        PropertyInfo property => Expression.Property(entity, property),
        FieldInfo field => Expression.Field(entity, field),
        _ => throw new QuickSqlException("不支持的成员形态"),
    };

    static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(object),
    };

    MemberInfo ResolveMember(string name)
        => (MemberInfo?)typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
           ?? typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Instance)
           ?? throw new QuickSqlException($"实体 {typeof(T).Name} 上没有成员 {name}");
}

/// <summary>反射式校验器。ValidationMode.ContractOnly 只读 ColumnContract 这一份真相；
/// ValidationMode.DataAnnotations 再补注解的 IsValid。取值走 IEntityTypeSchema&lt;T&gt;，
/// 所以 G1 生成的 schema 也能直接喂进来。</summary>
[RequiresUnreferencedCode("G2 兜底实现：按成员名反射定位属性/字段并读取注解。")]
[RequiresDynamicCode("G2 兜底实现：用表达式树编译读写器。")]
public sealed class ReflectionEntityValidator<T> : IEntityValidator<T> where T : class
{
    readonly Func<T, object?>[] _getters;
    readonly ColumnContract[] _columns;
    readonly Da.ValidationAttribute[]?[] _attributes;

    public ReflectionEntityValidator(EntityContract contract, ValidationMode mode, IEntityTypeSchema<T> schema)
    {
        Mode = mode;
        _columns = contract.Columns;
        _getters = new Func<T, object?>[_columns.Length];
        _attributes = new Da.ValidationAttribute[]?[_columns.Length];

        for (var i = 0; i < _columns.Length; i++)
        {
            // for 的循环变量是**一个变量**，直接闭包进去会让所有 getter 都读到循环结束后的 i，
            // 于是第一次校验就 IndexOutOfRangeException。每条迭代必须自己拷一份序号。
            var ordinal = i;

            _getters[i] = entity => schema.GetValue(entity, ordinal);

            if (mode >= ValidationMode.DataAnnotations)
                _attributes[i] = AttributeSideOf(_columns[i].MemberName);
        }
    }

    public ValidationMode Mode { get; }

    public bool TryValidate(T entity, List<ValidationFailure> failures)
    {
        if (Mode == ValidationMode.Off)
            return true;

        var before = failures.Count;

        for (var i = 0; i < _columns.Length; i++)
        {
            var column = _columns[i];
            var value = _getters[i](entity);

            // 两条尺子的实现与消息文本都在 ContractRules：G1 生成的校验器调的是同一处。
            if (value is null && !column.IsNullable)
                ContractRules.Required(column, failures);

            if (value is string text)
                ContractRules.Length(column, text, failures);

            if (_attributes[i] is { } attributes)
            {
                var context = new Da.ValidationContext(entity) { MemberName = column.MemberName };

                foreach (var attribute in attributes)
                {
                    if (attribute.GetValidationResult(value, context) is { } result)
                        failures.Add(new ValidationFailure(column.MemberName, attribute.GetType().Name, result.ErrorMessage ?? "校验失败"));
                }
            }
        }

        return failures.Count == before;
    }

    /// <summary>长度与必填已由契约查过；注解再查一遍会出现「同一行报两次错」。
    /// 例外是 StringLengthAttribute.MinimumLength，契约里没有下界。</summary>
    static Da.ValidationAttribute[] AttributeSideOf(string memberName)
    {
        if (typeof(T).GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance) is not { } property)
            return [];

        var attributes = new List<Da.ValidationAttribute>();

        foreach (var attribute in property.GetCustomAttributes<Da.ValidationAttribute>())
        {
            switch (attribute)
            {
                case Da.RequiredAttribute or Da.MaxLengthAttribute:
                    break;

                case Da.StringLengthAttribute { MinimumLength: > 0 } minLength:
                    attributes.Add(minLength);
                    break;

                case Da.StringLengthAttribute:
                    break;

                default:
                    attributes.Add(attribute);
                    break;
            }
        }

        return [.. attributes];
    }
}
