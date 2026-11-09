using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;

namespace QuickSql.Schema;

/// <summary>G1 注册表：实体事实由源生成器在编译期算好，这里只做两件事——让 EntityContractFactory 补上
/// provider 那一半，以及把生成的 schema/validator 缓存住（TypeTable：命中路径一次易失读加一次冻结表查找，
/// 没有锁）。命中时全程零反射、零表达式树、零 MakeGenericType。未命中的实体（没标注 [Table]、有基类、
/// 或被 Fluent 配置过）逐条退回 EntitySchemaRegistry，所以这是「按实体混合」而不是「整库二选一」；
/// Fluent 那条退回是硬要求：Fluent 压在注解之上，而生成器看不到运行期的 ModelConfiguration，
/// 让它接管就会把高优先层的配置静默吃掉。也正因为这个兜底口在，标注才打在类上——
/// 想在 trim/AOT 下拿到「0 动态代码」，条件不是换注册表而是每个实体都被生成过，
/// 那件事由 QuickSql003 诊断保证，不是由这里默默假设。</summary>
[RequiresUnreferencedCode("G1 未命中的实体退回反射解析：按运行期 Type 走属性与注解（§12.7 A 的 G2 级）。")]
[RequiresDynamicCode("G1 未命中的实体退回 G2，而 G2 的读取器是表达式树编译出来的。")]
public sealed class GeneratedEntitySchemaRegistry : IEntitySchemaRegistry
{
    readonly GeneratedEntityLookup _lookup;
    readonly EntitySchemaRegistry _fallback;
    readonly ITypeMap _types;
    readonly TypeTable<EntityContract> _contracts = new();
    readonly TypeTable<object> _schemas = new();
    readonly TypeTable<object> _validators = new();
    readonly TypeTable<Func<DbDataReader, ReadPlan, object>> _readers = new();

    /// <summary>G1 侧"解析过哪些实体"的账。RegisteredTypes 若只透传兜底那一份，纯 G1 库上
    /// SchemaSyncer 就枚举不到任何一张生成表，SyncStructureAsync 静默交回 Empty
    /// （启动期"先 Sync 后写"的顺序下症状是运行期"表不存在"）。登记放在只跑一次的工厂里，命中路径零额外成本。</summary>
    readonly TypeTable<byte> _g1Types = new();

    public IReadOnlyCollection<Type> RegisteredTypes
        => _g1Types.Count == 0
            ? _fallback.RegisteredTypes
            : [.. _g1Types.Keys, .. _fallback.RegisteredTypes];
    readonly bool _integralPrimaryKeyAutoIncrement;
    readonly int? _defaultStringLength;

    public GeneratedEntitySchemaRegistry(IDbProvider provider, GeneratedEntityLookup lookup, EntitySchemaOptions? options = null, ModelConfiguration? model = null,
        bool integralPrimaryKeyAutoIncrement = true, int? defaultStringLength = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(lookup);

        _types = provider.Types;
        _lookup = lookup;
        Options = options ?? new EntitySchemaOptions();
        _integralPrimaryKeyAutoIncrement = integralPrimaryKeyAutoIncrement;
        _defaultStringLength = defaultStringLength;
        Resolver = new ContractResolver(provider, model, integralPrimaryKeyAutoIncrement, defaultStringLength);
        _fallback = new EntitySchemaRegistry(Resolver, Options);
    }

    /// <summary>退回口用的解析器。G1 命中的实体不会经过它。</summary>
    public ContractResolver Resolver { get; }

    public EntitySchemaOptions Options { get; }

    public EntityContract Contract(Type clrType)
        => Find(clrType) is null
            ? _fallback.Contract(clrType)
            : _contracts.GetOrAdd(clrType, static (type, self) => self.BuildContract(type), this);

    public EntityContract Contract<T>() where T : class => Contract(typeof(T));

    public IEntityTypeSchema<T> Schema<T>() where T : class
        => Find(typeof(T)) is null
            ? _fallback.Schema<T>()
            : (IEntityTypeSchema<T>)_schemas.GetOrAdd(typeof(T), static (type, self) => self.BuildSchema(type), this);

    public IEntityValidator<T> Validator<T>() where T : class
        => GeneratedValidator(typeof(T)) is null
            ? _fallback.Validator<T>()
            : (IEntityValidator<T>)_validators.GetOrAdd(typeof(T), static (type, self) => self.BuildValidator(type), this);

    /// <summary>命中 G1 时读取器直接来自生成的 IBoxedEntityTypeSchema：
    /// 反射装箱那一步（EntitySchemaRegistry 里的 MakeGenericMethod）在这条路径上不存在。</summary>
    public Func<DbDataReader, ReadPlan, object> ReadDelegate(Type clrType)
        => Find(clrType) is null
            ? _fallback.ReadDelegate(clrType)
            : _readers.GetOrAdd(clrType, static (type, self) => ((IBoxedEntityTypeSchema)self.BuildSchema(type)).BoxedReader, this);

    GeneratedEntityMetadata? Find(Type clrType)
        => _lookup(clrType, out var metadata) && Resolver.Configuration.Find(clrType) is null ? metadata : null;

    /// <summary>只有 ValidationMode.ContractOnly 档有生成的校验器：再往上一档要读注解实例
    /// （ValidationAttribute.IsValid），那是运行期的东西，交给反射兜底路径。</summary>
    Func<EntityContract, object>? GeneratedValidator(Type clrType)
        => Options.Mode == ValidationMode.ContractOnly && Find(clrType) is { CreateValidator: { } create } ? create : null;

    /// <summary>编译期事实 + provider 那一半 = 最终契约。与 G2 共用这一个收尾器，
    /// 所以「生成路径与反射路径逐字段相等」不是巧合，而是同一段代码的两个输入。</summary>
    EntityContract BuildContract(Type clrType)
    {
        _g1Types.Add(clrType, 0);

        var metadata = Find(clrType)!;

        return EntityContractFactory.Build(metadata.ClrType, metadata.Table, metadata.Columns, metadata.Indexes,
            metadata.Relationships, _types, _integralPrimaryKeyAutoIncrement, metadata.SkipIndexes, _defaultStringLength, metadata.Stable);
    }

    object BuildSchema(Type clrType)
    {
        _g1Types.Add(clrType, 0);

        var metadata = Find(clrType)!;

        return metadata.CreateSchema(Contract(metadata.ClrType));
    }

    object BuildValidator(Type clrType)
    {
        _g1Types.Add(clrType, 0);

        var metadata = Find(clrType)!;

        return metadata.CreateValidator!(Contract(metadata.ClrType));
    }
}
