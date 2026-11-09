using System.Data.Common;
using QuickSql.Metadata;

namespace QuickSql.Schema;

/// <summary>G1 的接缝：源生成器把一个实体的编译期事实装进这里，运行期只补 provider 那一半
/// （EntityContractFactory）与实例化生成的 schema/validator。带 CreateSchema 工厂而不是让注册表
/// 按 Type 反射构造，是为了守住 G1 路径全链路 0 反射——这里没有任何 MakeGenericType。
/// CreateValidator 为 null 表示这个实体没有生成的校验器（例如需要 DataAnnotations 档），注册表会为它退回 G2。</summary>
public sealed record GeneratedEntityMetadata(
    Type ClrType,
    TableInfo Table,
    IReadOnlyList<ColumnFacts> Columns,
    IReadOnlyList<IndexFacts> Indexes,
    IReadOnlyList<SkipIndexFacts> SkipIndexes,
    IReadOnlyList<RelationshipFacts> Relationships,
    Func<EntityContract, object> CreateSchema,
    Func<EntityContract, object>? CreateValidator = null,
    StableFacts? Stable = null);

/// <summary>按运行期 Type 取 G1 事实。生成器产出的查询方法形状就是这个委托，
/// 由 IEntitySchemaRegistry 的 G1 实现持有；返回 false = 这个实体没被生成
/// （未标注、有基类、含不可写属性等），退回 G2。</summary>
public delegate bool GeneratedEntityLookup(Type clrType, out GeneratedEntityMetadata? metadata);

/// <summary>生成的 schema 顺带把"装箱形态的行读取器"静态交出来。
/// IEntitySchemaRegistry.ReadDelegate 要的是按 Type 取的非泛型口，
/// G2 用一次 MakeGenericMethod 装箱（启动期），G1 有这条就不必装箱。</summary>
public interface IBoxedEntityTypeSchema
{
    Func<DbDataReader, ReadPlan, object> BoxedReader { get; }
}

/// <summary>同上，校验侧：跟踪器里存的是 object，所以非泛型口必须存在，
/// 而它不该以"反射调泛型方法"为代价。</summary>
public interface IBoxedEntityValidator
{
    void Validate(object entity, List<ValidationFailure> failures);
}
