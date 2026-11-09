using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using QuickSql.Materialization;
using QuickSql.Query.Model;
using QuickSql.Schema;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>DTO 的形状规则（§5.7 的"实体 class / DTO record 或 struct"）在**离线**能判的那一半：
/// 物化层认哪些装配形状、翻译层把哪些形状认作"一个形状"而不是"一颗值"。
/// 真库上的纵向切片在集成层 <c>DtoShapeTests</c>（同名不同项目）。</summary>
public sealed class DtoShapeTests
{
    static readonly IEntitySchemaRegistry Registry = SqlServer.Orm().Schemas;

    static readonly (string Name, Type Member)[] TripleShape =
        [("Id", typeof(int)), ("City", typeof(string)), ("Amount", typeof(decimal))];

    static QueryModel Dto<T>(bool viaConstructor) => new()
    {
        Tables = [],
        Plan = new ObjectProjection(typeof(T),
            [.. TripleShape.Select(s => new ProjectionSlot(s.Name, Literal(s.Name), s.Member))], viaConstructor),
    };

    static InlineLiteral Literal(string name) => new("[" + name + "]");

    /// <summary>三列一行：int / string（可空）/ decimal。<see cref="NullCity"/> 那一行给的是 DBNull，
    /// 用来同时钉"NULL 落进 <c>string?</c> 是 null 而不是空串"。</summary>
    static DbDataReader Reader(params object?[] city)
    {
        var table = new DataTable();
        table.Columns.Add("c0", typeof(int));
        table.Columns.Add("c1", typeof(string));
        table.Columns.Add("c2", typeof(decimal));

        for (var i = 0; i < city.Length; i++)
            table.Rows.Add([i + 1, city[i]!, (i + 1) / 4m]);

        return table.CreateDataReader();
    }

    static T First<T>(bool viaConstructor)
    {
        using var data = Reader("city-a");

        _ = data.Read();

        return Materializer.Create<T>(Dto<T>(viaConstructor), Registry)(data);
    }

    // ---------- 物化层：struct 的三种装配 ----------

    [Fact]
    public void A_record_struct_projection_assembles_from_its_positional_constructor()
    {
        var value = First<RecStructPos>(viaConstructor: true);

        Assert.Equal(1, value.Id);
        Assert.Equal("city-a", value.City);
        Assert.Equal(0.25m, value.Amount);
    }

    /// <summary>这一条钉的是 <c>Materializer</c> 里"值类型走无 ctor 的 <c>Expression.New</c>"那一改：
    /// <c>record struct</c> 在反射里查不到无参构造器（<c>GetConstructor(Type.EmptyTypes)</c> 交回 null），
    /// 而 C# 的 <c>new S { … }</c> 合法 —— 两侧不是同一件事，按反射查到的那颗来判就会把这一整类挡掉。</summary>
    [Fact]
    public void A_record_struct_projection_assembles_from_member_init_without_a_declared_parameterless_constructor()
    {
        var value = First<RecStructProps>(viaConstructor: false);

        Assert.Equal(1, value.Id);
        Assert.Equal("city-a", value.City);
        Assert.Equal(0.25m, value.Amount);
    }

    [Fact]
    public void A_struct_with_public_fields_assembles_from_member_init()
    {
        var value = First<PlainStruct>(viaConstructor: false);

        Assert.Equal(1, value.Id);
        Assert.Equal("city-a", value.City);
        Assert.Equal(0.25m, value.Amount);
    }

    [Fact]
    public void A_readonly_record_struct_projection_assembles_and_keeps_null_as_null()
    {
        using var data = Reader(DBNull.Value);

        _ = data.Read();

        var value = Materializer.Create<RoRecStructPos>(Dto<RoRecStructPos>(viaConstructor: true), Registry)(data);

        Assert.Equal(1, value.Id);
        Assert.Null(value.City);
        Assert.Equal(0.25m, value.Amount);
    }

    /// <summary>反向的那一半：class 仍然**必须**有无参构造器 —— 今天放宽的是它的<em>可见性</em>，
    /// 不是"要不要存在"。放宽到值类型时如果把这条一起放宽，"成员初始化投影"就会在
    /// <c>Expression.New</c> 里抛一句说不清的 ArgumentException。</summary>
    [Fact]
    public void A_class_without_a_parameterless_constructor_still_rejects_member_init_projection()
    {
        var exception = Assert.Throws<QuickSqlException>(() => First<CtorOnlyClass>(viaConstructor: false));

        Assert.Contains("没有无参构造器", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>无参构造器不必是公共的：ABP/DDD 的 <c>private X()</c> ∶ <c>protected X()</c> 那一族
    /// 也要能装配。<b>而字段初始化器必须跟着跑</b> —— 换成
    /// <c>RuntimeHelpers.GetUninitializedObject</c> 这一样能出实例、却把 <c>= ["…"]</c> 那一格留成 null，
    /// 症状是"集合导航一条都不缝"而不是报错，所以这一枚把两条一起钉住。</summary>
    [Fact]
    public void A_class_with_a_non_public_parameterless_constructor_assembles_by_member_init()
    {
        var value = First<PrivateSeedClass>(viaConstructor: false);

        Assert.Equal(1, value.Id);
        Assert.Equal("city-a", value.City);
        Assert.Equal(0.25m, value.Amount);

        // 构造器跑过的证据：初始化器给的种子值在场，且集合可追加
        Assert.Equal(["初始化器跑过"], value.Tags);
    }

    // ---------- 物化层：struct 到底省不省 ----------

    /// <summary>同一份三列形状：<b>struct 的装配本身一颗都不箱</b>（每行增量 0 B），
    /// 而 class 那一颗对象是每行 48 B（10 001 行、假驱动、string 列交回表里那个实例）。
    /// 分母是"同样三次 <c>GetValue</c> 但不装配任何东西"那一档，所以差值里只有 DTO 自己 ——
    /// 那三次 <c>GetValue</c> 的装箱两边都要付，它属于 §12.8 第 3 行，不属于这一条。
    /// 这一条不钉绝对值，只钉符号与"零"：绝对值随列数与形状走。
    /// <para><b>v0.38 第五笔把"零"写成了"每一行都为零"而不许是"整场为零"</b>：这条量法拿总增量除以 10 001 行，
    /// 于是任何一笔<em>每形状一次</em>的分配（三场里那颗整场 24 B —— 只有一头加一颗字段那一形的最小对象收得到这个数，
    /// 而它是哪一把没有定位：一次性的东西只在落进窗里那一场才看得见）只要落进取样窗，
    /// 就会读成 0.0024 而不是 0，同一份二进制连跑三场给的是 4 ∶ 3 ∶ 4 的翻色（<c>obj/logs/v38-c-dialects-4/5/6.log</c>）。
    /// 那笔一次性的钱不是这一条要问的问题：这一条问的是"每行有没有多装箱一颗"，而每行哪怕 1 B 在这里都读得出 1.0。
    /// 所以判据改成 <c>&lt; 1</c>：它保住"每行零装箱"的全部分辨率（一行一比特都会红），
    /// 只放掉"每形状一次"那一档 —— 那一档要量也得换量法（把首行的那一次单独取出来数），不在这一条里。</para></summary>
    [Fact]
    public void Assembling_a_struct_dto_allocates_nothing_per_row_while_a_record_class_dto_pays_its_object()
    {
        var table = Wide();

        var baseline = ReadOnlyPerRow(table);
        var recordClass = AssemblePerRow<RecPos>(table, viaConstructor: true);
        var recordStruct = AssemblePerRow<RecStructPos>(table, viaConstructor: true);
        var recordStructByMemberInit = AssemblePerRow<RecStructPos>(table, viaConstructor: false);

        Assert.True(recordClass - baseline > 0,
            $"class 那一侧连一颗对象都没付（差 {recordClass - baseline:0.0} B/行）：这一量法测不出东西，用例本身失效了。");
        Assert.True(recordStruct - baseline < 1,
            $"struct 构造那一档每行付了 {recordStruct - baseline:0.####} B（整场 {recordStruct - baseline:0.####} × {MeasureRows:N0} ≈ {(recordStruct - baseline) * MeasureRows:0} B）：每行一颗以上才是这一条要拦的");
        Assert.True(recordStructByMemberInit - baseline < 1,
            $"struct 成员初始化那一档每行付了 {recordStructByMemberInit - baseline:0.####} B（整场约 {(recordStructByMemberInit - baseline) * MeasureRows:0} B）");
    }

    const int MeasureRows = 10_001;

    static DataTable Wide()
    {
        var table = new DataTable();
        table.Columns.Add("c0", typeof(int));
        table.Columns.Add("c1", typeof(string));
        table.Columns.Add("c2", typeof(decimal));

        for (var i = 0; i < MeasureRows; i++)
            table.Rows.Add([i, "city-" + i, i / 4m]);

        return table;
    }

    /// <summary>只走驱动那一半：逐行三次 <c>GetValue</c>，一颗对象都不造。</summary>
    static double ReadOnlyPerRow(DataTable table)
    {
        using var data = table.CreateDataReader();

        var start = GC.GetAllocatedBytesForCurrentThread();
        var hash = 0;

        while (data.Read())
            hash += (data.GetValue(0)?.GetHashCode() ?? 0)
                    + (data.GetValue(1)?.GetHashCode() ?? 0)
                    + (data.GetValue(2)?.GetHashCode() ?? 0);

        var bytes = GC.GetAllocatedBytesForCurrentThread() - start;

        GC.KeepAlive(hash);

        return (double) bytes / MeasureRows;
    }

    static double AssemblePerRow<T>(DataTable table, bool viaConstructor)
    {
        var reader = Materializer.Create<T>(Dto<T>(viaConstructor), Registry);

        using var data = table.CreateDataReader();

        var start = GC.GetAllocatedBytesForCurrentThread();
        var last = default(T)!;

        while (data.Read())
            last = reader(data);

        var bytes = GC.GetAllocatedBytesForCurrentThread() - start;

        // 收进容器的那一档（List<T> 预分配）不在这个窗口里：class 侧是"对象 + 每格一个引用"、
        // struct 侧是"元素内联付自己的尺寸"，那一笔差在 §5.7 的实测里单独记。
        GC.KeepAlive(last);

        return (double) bytes / MeasureRows;
    }

    // ---------- 翻译层：形状与值的分界 ----------

    [Fact]
    public void A_struct_dto_projection_emits_one_column_per_slot()
    {
        var sql = SqlServer.Orm().Select<Order>()
            .Select(o => new SumStruct(o.Id, o.CustomerName, o.Amount))
            .ToSql();

        Assert.Equal(["[t0].[Id]", "[t0].[CustomerName]", "[t0].[Amount]"], Checks.Columns(sql));
    }

    [Fact]
    public void A_struct_dto_projection_by_member_init_emits_one_column_per_slot()
    {
        var sql = SqlServer.Orm().Select<Order>()
            .Select(o => new SumStruct { Id = o.Id, City = o.CustomerName, Amount = o.Amount })
            .ToSql();

        Assert.Equal(["[t0].[Id]", "[t0].[CustomerName]", "[t0].[Amount]"], Checks.Columns(sql));
    }

    /// <summary>分界的另一侧：BCL 的值类型仍然是"一颗值"。<c>new DateTime(…)</c> 这类如果被一起当成
    /// 对象投影，就会把一句标量取列变成"按构造器装配一颗 DateTime"，而那条路上根本没有发射。</summary>
    [Fact]
    public void A_bcl_value_type_construction_stays_a_scalar_projection()
    {
        var sql = SqlServer.Orm().Select<Order>()
            .Select(o => o.CreatedAt)
            .ToSql();

        Assert.Equal(["[t0].[CreatedAt]"], Checks.Columns(sql));

        Assert.Throws<UntranslatableExpressionException>(
            () => SqlServer.Orm().Select<Order>()
                .Select(o => new ValueTuple<int, int>(o.Id, o.CustomerId))
                .ToSql());
    }

    /// <summary>谓词里嵌一颗成员初始化：以前落到 <c>Evaluate</c> 的编译退路，吐的是 LINQ 内部的
    /// "variable 'o' … referenced from scope ''"（把这一臂临时注释掉重跑，两句都退回那句内部错误 —— 实测）。
    /// 现在两侧都必须在翻译层被拦住，而且要说清下一步往哪走。</summary>
    [Fact]
    public void A_member_init_inside_a_predicate_is_rejected_with_a_pointing_message()
    {
        // 整颗成员初始化当值用：走"该形态当前无映射"。
        var bare = Assert.Throws<UntranslatableExpressionException>(
            () => SqlServer.Orm().Select<Order>()
                .Where(o => new SumStruct { Id = o.Id } == default)
                .ToSql());

        Assert.Contains("该形态当前无映射", bare.Message, StringComparison.Ordinal);
        // v0.36 第一笔（§16.8 毛病 4）：尾巴从指向不存在的 .ToEnumerable() 改成真实出口，
        // 而"不存在"这一半也要钉 —— 话术里再长出幻影口就是回归。
        Assert.Contains("落到内存再算", bare.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ToEnumerable", bare.Message, StringComparison.Ordinal);

        // 取它的一个成员：拦在成员那一侧，因为真正没有 SQL 对应的是"非实体的成员读法"。
        var member = Assert.Throws<UntranslatableExpressionException>(
            () => SqlServer.Orm().Select<Order>()
                .Where(o => new SumStruct { Id = o.Id }.Id > 3)
                .ToSql());

        Assert.Contains("没有 SQL 对应形态", member.Message, StringComparison.Ordinal);
        Assert.Contains("落到内存再读这一档", member.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ToEnumerable", member.Message, StringComparison.Ordinal);
        // 毛病 1 的拼接钉：旧模板的「，可改写为 」前缀没了，句号直拼（两枚消息共用同一模板，钉一处即证模板）。
        Assert.DoesNotContain("可改写为", member.Message, StringComparison.Ordinal);
    }
}

public record RecPos(int Id, string? City, decimal Amount);

public sealed class CtorOnlyClass(int id, string? city, decimal amount)
{
    public int Id { get; } = id;
    public string? City { get; } = city;
    public decimal Amount { get; } = amount;
}

/// <summary>ABP/DDD 那一族：创建走工厂，无参构造只有自己能看见。</summary>
public sealed class PrivateSeedClass
{
    public int Id { get; set; }

    public string? City { get; set; }

    public decimal Amount { get; set; }

    public List<string> Tags { get; set; } = ["初始化器跑过"];

    private PrivateSeedClass()
    {
    }

    public static PrivateSeedClass Create() => new();
}

public record struct RecStructPos(int Id, string? City, decimal Amount);

public record struct RecStructProps
{
    public int Id { get; set; }
    public string? City { get; set; }
    public decimal Amount { get; set; }
}

public readonly record struct RoRecStructPos(int Id, string? City, decimal Amount);

public struct PlainStruct
{
    public int Id;
    public string? City;
    public decimal Amount;
}

public record struct SumStruct(int Id, string? City, decimal Amount);
