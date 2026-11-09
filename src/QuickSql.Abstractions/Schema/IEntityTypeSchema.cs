using System.Data.Common;
using QuickSql.Metadata;

namespace QuickSql.Schema;

/// <summary>实体物化与读写的类型化入口。G1 生成的实现是主路径（零反射、AOT 安全）；
/// Core 的反射兜底实现只在启动期编译一次委托，热路径同样零反射。</summary>
public interface IEntityTypeSchema<T> where T : class
{
    EntityContract Contract { get; }

    T New();

    /// <summary>按列序读写。ordinal 是 EntityContract.Columns 的下标，
    /// 不是结果集列号——结果集列号由物化层做一次映射。</summary>
    object? GetValue(T entity, int ordinal);

    void SetValue(T entity, int ordinal, object? value);

    /// <summary>从 reader 当前行构造实体。plan 带来这一趟的列序起点与每列的直读档，
    /// 所以它必须是这一趟读的那一张票（每场次建一次，见 ReadPlan）。</summary>
    T Read(DbDataReader reader, ReadPlan plan);

    /// <summary>已编译的行读取器。每查询一份"哪些列可直读"的向量随票进来，每值只剩 IsDBNull 加原始访问器。</summary>
    Func<DbDataReader, ReadPlan, T> Reader { get; }
}

public enum ValidationMode
{
    Off = 0,
    /// <summary>只查 ColumnContract 的尺子：长度量纲、可空、精度。零反射，批量/COPY 路径唯一可开的档。</summary>
    ContractOnly = 1,
    DataAnnotations = 2,
    Custom = 3,
}

/// <summary>校验器与 schema 同源：由 G1 一并生成，避免"校验时再反射读一遍注解"。</summary>
public interface IEntityValidator<T> where T : class
{
    ValidationMode Mode { get; }

    /// <summary>返回 false 表示有失败项；failures 由调用方提供缓冲，避免每行分配。</summary>
    bool TryValidate(T entity, List<ValidationFailure> failures);
}

/// <summary>元数据与校验器的解析入口。启动期解析、结果冻结。</summary>
public interface IEntitySchemaRegistry
{
    IEntityTypeSchema<T> Schema<T>() where T : class;

    IEntityValidator<T> Validator<T>() where T : class;

    EntityContract Contract<T>() where T : class;

    EntityContract Contract(Type clrType);

    /// <summary>非泛型物化委托，给 Materializer 的 EntityProjection 用。
    /// 返回 object，调用方按查询的类型参数转型。票必须由物化层每场次建一张：
    /// 那张票决定这一趟读里哪些列可以不装箱。</summary>
    Func<DbDataReader, ReadPlan, object> ReadDelegate(Type clrType);

    /// <summary>已解析过的所有实体类型。用于 SyncStructure 等需要枚举全部实体的场景。</summary>
    IReadOnlyCollection<Type> RegisteredTypes { get; }
}

/// <summary>结构同步策略。控制 SyncStructure 的行为：不执行、直接执行、还是只生成脚本。</summary>
public enum SchemaSyncPolicy
{
    /// <summary>不自动同步。要手动调用 SyncStructure 并自行决定执行方式。</summary>
    Disabled = 0,

    /// <summary>直接执行 DDL。只在 Development 环境有效；非 Development 环境要求 ForceSchemaSync=true，否则抛错。</summary>
    Execute = 1,

    /// <summary>只生成脚本，不执行。返回的 SchemaSyncResult 包含完整 DDL 文本。</summary>
    Script = 2,
}
