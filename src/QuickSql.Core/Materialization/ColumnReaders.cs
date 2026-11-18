using QuickSql.Metadata;
using QuickSql.Schema;

namespace QuickSql.Materialization;

/// <summary>按列序读实体键值的读器：导航缝合（IncludeMany/拆查/DbSet.Load）的父键/子键收取
/// 从 MemberAccessors 的「按名反射 + 编译委托 + 字典缓存」迁到 schema 的按列序读口——
/// G1 那一侧是生成期写死的 ordinal switch（零反射零间接），G2 那一侧是启动期编译好的委托表。
/// 与树形闭包（QueryEngine.Tree 的 CrawlAsync/Stitch）同一颗口、同一条纪律：
/// 键的 ordinal 在契约上就算得出来，热路径上不再有「按名字找成员」这一步。
/// 复合键仍交 object[]（StructuralComparer 按内容比）；单键直接交值，不付数组。
/// 空键语义与迁移前逐字一致：读口交回 null 时下游字典照样拒（null FK 本就不在缝合的射程内）。</summary>
internal readonly record struct ColumnReaders(IBoxedEntityAccessor Accessor, int[] Ordinals)
{
    /// <summary>一颗实体的一把键：单键直读，复合键逐列收进数组。</summary>
    public object Key(object entity)
    {
        if (Ordinals.Length == 1)
            return Accessor.GetValue(entity, Ordinals[0])!;

        var values = new object[Ordinals.Length];

        for (var i = 0; i < Ordinals.Length; i++)
            values[i] = Accessor.GetValue(entity, Ordinals[i])!;

        return values;
    }

    /// <summary>按位读一颗原始值（未规范化）：Storageable 的键读逐列取来再各自过 Canonical，
    /// 与 <see cref="Key"/> 的差别只在不做单键/复合键的合并——规范化按列型走，交给调用方。</summary>
    public object? Value(object entity, int index) => Accessor.GetValue(entity, Ordinals[index]);

    /// <summary>按列契约取读器（主键那一形：列契约实例就在契约的 Columns 数组里，按引用找下标）。</summary>
    public static ColumnReaders For(IEntitySchemaRegistry schemas, EntityContract contract, IReadOnlyList<ColumnContract> columns)
    {
        var ordinals = new int[columns.Count];

        for (var i = 0; i < columns.Count; i++)
            ordinals[i] = Array.IndexOf(contract.Columns, columns[i]);

        return new ColumnReaders(schemas.Accessors(contract.ClrType), ordinals);
    }

    /// <summary>按列名取读器（外键那一形：名字先经契约换算成列契约，再走同一颗下标口）。</summary>
    public static ColumnReaders ForColumns(IEntitySchemaRegistry schemas, EntityContract contract, IReadOnlyList<string> columnNames)
    {
        var columns = new ColumnContract[columnNames.Count];

        for (var i = 0; i < columnNames.Count; i++)
            columns[i] = contract.ByColumn[columnNames[i]];

        return For(schemas, contract, columns);
    }
}