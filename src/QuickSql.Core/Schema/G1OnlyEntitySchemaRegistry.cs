using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;

namespace QuickSql.Schema;

/// <summary>G1-only 注册表：只认源生成器在编译期产出的实体事实，没有 G2 反射兜底——
/// 未命中（实体没被生成、或被 Fluent 配置过）当场抛 NotSupportedException，而不是静默退回反射。
/// 这是「trim/AOT 下 0 动态代码」的静态保证口：整条链零反射、零表达式树、零 MakeGenericType，
/// 所以类上不带 RUC/RDC（对照 GeneratedEntitySchemaRegistry：兜底口在，标注就得在）。
/// 前提由 QuickSql003 诊断在编译期点名：没标注 [Table]、有基类、无公共无参构造、
/// 含 init/受限 set 属性的实体不走 G1，这一档对它们直接拒绝。
/// Fluent 覆写同样拒绝：生成器看不见运行期的 ModelConfiguration，让它接管会把高优先层的配置静默吃掉。
/// 校验只有 ContractOnly（生成的契约尺子）与 Off 两档；DataAnnotations/Custom 要读运行期注解实例，
/// 那是反射档的事，要用请换带兜底的注册表。</summary>
public sealed class G1OnlyEntitySchemaRegistry : IEntitySchemaRegistry
{
    readonly GeneratedEntityLookup _lookup;
    readonly ITypeMap _types;
    readonly ModelConfiguration? _model;
    readonly bool _integralPrimaryKeyAutoIncrement;
    readonly int? _defaultStringLength;
    readonly TypeTable<EntityContract> _contracts = new();
    readonly TypeTable<object> _schemas = new();
    readonly TypeTable<object> _validators = new();
    readonly TypeTable<Func<DbDataReader, ReadPlan, object>> _readers = new();

    /// <summary>与 GeneratedEntitySchemaRegistry 的 _g1Types 同一颗账：SchemaSyncer 靠
    /// RegisteredTypes 枚举要同步的表，只透传"解析过哪些"会让启动期 Sync 静默交回 Empty。</summary>
    readonly TypeTable<byte> _registered = new();

    public G1OnlyEntitySchemaRegistry(
        IDbProvider provider,
        GeneratedEntityLookup lookup,
        EntitySchemaOptions? options = null,
        ModelConfiguration? model = null,
        bool integralPrimaryKeyAutoIncrement = true,
        int? defaultStringLength = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(lookup);

        _types = provider.Types;
        _lookup = lookup;
        _model = model;
        Options = options ?? new EntitySchemaOptions();
        _integralPrimaryKeyAutoIncrement = integralPrimaryKeyAutoIncrement;
        _defaultStringLength = defaultStringLength;
    }

    public EntitySchemaOptions Options { get; }

    public IReadOnlyCollection<Type> RegisteredTypes => _registered.Keys;

    public EntityContract Contract(Type clrType)
        => _contracts.GetOrAdd(clrType, static (type, self) => self.BuildContract(type), this);

    public EntityContract Contract<T>() where T : class => Contract(typeof(T));

    public IEntityTypeSchema<T> Schema<T>() where T : class
        => (IEntityTypeSchema<T>)_schemas.GetOrAdd(typeof(T), static (type, self) => self.BuildSchema(type), this);

    public IEntityValidator<T> Validator<T>() where T : class
    {
        if (Options.Mode == ValidationMode.Off)
            return NullValidator<T>.Instance;

        if (Options.Mode != ValidationMode.ContractOnly)
            throw new NotSupportedException(
                $"G1-only 注册表不提供 ValidationMode.{Options.Mode}：那一档要读运行期注解实例（ValidationAttribute.IsValid），是反射档。" +
                "请用 ContractOnly（契约尺子，校验器由生成器产出）或 Off，或换 GeneratedEntitySchemaRegistry / EntitySchemaRegistry。");

        return (IEntityValidator<T>)_validators.GetOrAdd(typeof(T), static (type, self) => self.BuildValidator(type), this);
    }

    public Func<DbDataReader, ReadPlan, object> ReadDelegate(Type clrType)
        => _readers.GetOrAdd(clrType, static (type, self) => ((IBoxedEntityTypeSchema)self.BuildSchema(type)).BoxedReader, this);

    public IBoxedEntityAccessor Accessors(Type clrType)
        => (IBoxedEntityAccessor)_schemas.GetOrAdd(clrType, static (type, self) => self.BuildSchema(type), this);

    /// <summary>取事实册，未命中即抛。Fluent 先判：配置过的实体即使被生成过也拒——
    /// 与 GeneratedEntitySchemaRegistry.Find 同一条判据，只是那一颗退回 G2，这一颗拒绝。</summary>
    GeneratedEntityMetadata Facts(Type clrType)
    {
        if (_model?.Find(clrType) is not null)
            throw new NotSupportedException(
                $"实体 {clrType.Name} 有 Fluent 配置（ModelConfiguration），G1-only 注册表拒绝接管：生成器看不见运行期覆写，" +
                "接管就会把高优先层的配置静默吃掉。请去掉该实体的 Fluent 配置，或换 GeneratedEntitySchemaRegistry（G1 优先、G2 兜底）。");

        if (!_lookup(clrType, out var metadata) || metadata is null)
            throw new NotSupportedException(
                $"实体 {clrType.Name} 不在 G1 生成事实册里（QuickSql003）：可能没标注 [Table]、有基类、无公共无参构造器、" +
                "含 init/受限 set 属性或是泛型实体。G1-only 注册表没有反射兜底：请让实体满足 G1 条件（编译期 QuickSql003 诊断会点名原因），" +
                "或换 GeneratedEntitySchemaRegistry / EntitySchemaRegistry（反射档，trim/AOT 下带 RUC/RDC 税）。");

        return metadata;
    }

    /// <summary>编译期事实 + provider 那一半 = 最终契约。与 G2/G1 混合档共用同一个收尾器，
    /// 「生成路径与反射路径逐字段相等」在这里同样是同一段代码的两个输入。</summary>
    EntityContract BuildContract(Type clrType)
    {
        _registered.Add(clrType, 0);

        var metadata = Facts(clrType);

        return EntityContractFactory.Build(metadata.ClrType, metadata.Table, metadata.Columns, metadata.Indexes,
            metadata.Relationships, _types, _integralPrimaryKeyAutoIncrement, metadata.SkipIndexes, _defaultStringLength, metadata.Stable);
    }

    object BuildSchema(Type clrType)
    {
        _registered.Add(clrType, 0);

        return Facts(clrType).CreateSchema(Contract(clrType));
    }

    object BuildValidator(Type clrType)
    {
        _registered.Add(clrType, 0);

        var metadata = Facts(clrType);

        return metadata.CreateValidator?.Invoke(Contract(clrType))
               ?? throw new NotSupportedException(
                   $"实体 {clrType.Name} 的生成事实里没有校验器工厂：G1-only 注册表的 ContractOnly 档要求生成器产出校验器。" +
                   "请确认 QuickSql.Generators 已作为 Analyzer 挂进编译且该实体走了 G1。");
    }

    sealed class NullValidator<T> : IEntityValidator<T> where T : class
    {
        public static readonly NullValidator<T> Instance = new();

        public ValidationMode Mode => ValidationMode.Off;

        public bool TryValidate(T entity, List<ValidationFailure> failures) => true;
    }
}