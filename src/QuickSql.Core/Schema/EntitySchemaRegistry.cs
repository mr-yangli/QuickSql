using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using QuickSql.Metadata;

namespace QuickSql.Schema;

/// <summary>注册表的构造选项。两个工厂就是 G1 的注入点：返回 null 表示这个实体没有被生成器覆盖，
/// 退回 Core 的反射兜底（「加载可以反射，调用不行」的落地处）。分开是因为生成器对同一实体
/// 产出 schema 与 validator 两个类型，一个实体的两半可以各自处于不同进度。</summary>
public sealed record EntitySchemaOptions(
    ValidationMode Mode = ValidationMode.ContractOnly,
    Func<Type, object?>? SchemaFactory = null,
    Func<Type, object?>? ValidatorFactory = null);

/// <summary>schema 与校验器的缓存。热路径拿到的都是已编译委托，查表之外零反射，
/// 查表那一步走 TypeTable（无锁读 + 冻结快照）。整个类是 G2 兜底实现的边界，所以 RUC/RDC 标注打在类上
/// 而不是逐个成员：G1 生成的注册表实现同一个接口、那条路径不需要动态代码；接口本身不带标注，
/// 内核各处经接口调用不会传染。</summary>
[RequiresUnreferencedCode("Reflection-based schema build: it walks properties and attributes of a runtime Type.")]
[RequiresDynamicCode("Reflection-based schema build compiles expression trees for entity readers and validators.")]
public sealed class EntitySchemaRegistry : IEntitySchemaRegistry
{
    static readonly MethodInfo BoxReader =
        typeof(EntitySchemaRegistry).GetMethod(nameof(WrapReader), BindingFlags.NonPublic | BindingFlags.Static)!;

    static readonly MethodInfo BoxValidator =
        typeof(EntitySchemaRegistry).GetMethod(nameof(WrapValidator), BindingFlags.NonPublic | BindingFlags.Static)!;

    readonly TypeTable<object> _schemas = new();
    readonly TypeTable<object> _validators = new();
    readonly TypeTable<Func<DbDataReader, ReadPlan, object>> _readDelegates = new();
    readonly TypeTable<Action<object, List<ValidationFailure>>> _validateDelegates = new();
    readonly TypeTable<byte> _resolvedTypes = new();

    public EntitySchemaRegistry(ContractResolver resolver, EntitySchemaOptions? options = null)
    {
        Resolver = resolver;
        Options = options ?? new EntitySchemaOptions();
    }

    public ContractResolver Resolver { get; }

    public EntitySchemaOptions Options { get; }

    public IReadOnlyCollection<Type> RegisteredTypes => _resolvedTypes.Keys;

    public EntityContract Contract(Type clrType)
    {
        _resolvedTypes.Add(clrType, 0);
        return Resolver.Resolve(clrType);
    }

    public EntityContract Contract<T>() where T : class => Contract(typeof(T));

    public IEntityTypeSchema<T> Schema<T>() where T : class
        => (IEntityTypeSchema<T>)_schemas.GetOrAdd(typeof(T), static (type, self) => self.Build(type), this);

    public IEntityValidator<T> Validator<T>() where T : class
        => Options.Mode == ValidationMode.Off
            ? NullValidator<T>.Instance
            : (IEntityValidator<T>)_validators.GetOrAdd(typeof(T), static (type, self) => self.BuildValidator(type), this);

    /// <summary>非泛型物化委托。用一次 MakeGenericMethod 把 Func 泛型实例装箱成 object 版——
    /// 启动期付出，之后按类型缓存。</summary>
    public Func<DbDataReader, ReadPlan, object> ReadDelegate(Type clrType)
        => _readDelegates.GetOrAdd(
            clrType,
            static (type, self) => (Func<DbDataReader, ReadPlan, object>)BoxReader
                .MakeGenericMethod(type)
                .Invoke(null, [self.SchemaHandle(type)])!,
            this);

    /// <summary>跟踪器里存的是 object，所以校验也要一个非泛型口。反射只在第一次取用时发生，
    /// 之后与泛型口走同一条已编译路径。</summary>
    public Action<object, List<ValidationFailure>> ValidateDelegate(Type clrType)
        => _validateDelegates.GetOrAdd(
            clrType,
            static (type, self) => (Action<object, List<ValidationFailure>>)BoxValidator
                .MakeGenericMethod(type)
                .Invoke(null, [self.ValidatorHandle(type)])!,
            this);

    public bool TryValidate(Type clrType, object entity, List<ValidationFailure> failures)
    {
        var before = failures.Count;
        ValidateDelegate(clrType)(entity, failures);

        return failures.Count == before;
    }

    object SchemaHandle(Type clrType) => _schemas.GetOrAdd(clrType, static (type, self) => self.Build(type), this);

    object ValidatorHandle(Type clrType) => _validators.GetOrAdd(clrType, static (type, self) => self.BuildValidator(type), this);

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2067:UnrecognizedValueHandling",
        Justification = "G2 兜底路径要求实体带公共属性，这是契约而非反射猜测；G1 生成路径不会走到这里。")]
    object Build(Type clrType)
        => Options.SchemaFactory?.Invoke(clrType)
           ?? Activator.CreateInstance(typeof(ReflectionEntitySchema<>).MakeGenericType(clrType), Resolver.Resolve(clrType))!;

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2067:UnrecognizedValueHandling",
        Justification = "同上：validator 与 schema 同源，兜底路径只在没有生成器时启用。")]
    object BuildValidator(Type clrType)
        => Options.ValidatorFactory?.Invoke(clrType)
           ?? Activator.CreateInstance(
               typeof(ReflectionEntityValidator<>).MakeGenericType(clrType),
               Resolver.Resolve(clrType),
               Options.Mode,
               SchemaHandle(clrType))!;

    static Func<DbDataReader, ReadPlan, object> WrapReader<T>(IEntityTypeSchema<T> schema) where T : class
        => (reader, plan) => schema.Reader(reader, plan);

    static Action<object, List<ValidationFailure>> WrapValidator<T>(IEntityValidator<T> validator) where T : class
        => (entity, failures) => validator.TryValidate((T)entity, failures);

    sealed class NullValidator<T> : IEntityValidator<T> where T : class
    {
        public static readonly NullValidator<T> Instance = new();

        public ValidationMode Mode => ValidationMode.Off;

        public bool TryValidate(T entity, List<ValidationFailure> failures) => true;
    }
}
