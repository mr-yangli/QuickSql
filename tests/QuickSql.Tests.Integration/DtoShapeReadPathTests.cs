using QuickSql.Query;
using QuickSql.TestInfra;
using QuickSql.Translation;
using System.Linq.Expressions;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§5.7 的形状规则在<b>真库</b>上的那一半：DTO 写成 <c>record struct</c> / 普通 <c>struct</c>
/// 时，整条链（翻译 → 发射 → ADO → 物化装配）必须和 class 那一侧同样可用。
///
/// <para>为什么这批要在集成层重跑一遍、而不是只留离线用例：离线的假驱动只喂 <c>GetValue</c>，
/// 而 struct 装配真正依赖的"NULL 交回声明类型自己的默认值"要走驱动返回的 <c>DBNull</c>，
/// 槽位顺序则要走真库的列序。两句都在这一层才有见证（§13.2：真库用例证的是"库认这段文本、值对得上"）。</para>
///
/// <para>四类形状各一条，是因为它们在物化器里是<b>四条不同的装配代码</b>：位置构造器、
/// 成员初始化（struct 走无 ctor 的 <c>Expression.New</c>）、字段直写、只读 record struct。
/// 只跑一条等于其余三条没跑过。</para></summary>
public class DtoShapeReadPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static IQuery<Order> Tagged() => Orm.Select<Order>().Where(o => o.Note == Database.Tag);

    /// <summary>本批次订单按主键升序，与 <see cref="Database.Orders"/> 下标一一对应。
    /// 三列形状：<c>Id</c> / <c>CustomerName</c>（第二、四条是 NULL）/ <c>Amount</c>。</summary>
    static Task<List<T>> ShapesAsync<T>(Expression<Func<Order, T>> projector)
        => Tagged().OrderBy(o => o.Id).Select(projector).ToListAsync(TestContext.Current.CancellationToken);

    /// <summary>三列形状逐行比对，四类形状共用它：所以"少比一行"不可能靠复制粘贴蒙过去。
    /// 第二、四条的 <c>CustomerName</c> 在种子里就是 NULL。</summary>
    static void AssertEveryRow<T>(List<T> rows, Func<T, string?> city, Func<T, decimal> amount, Func<T, int> id)
    {
        Assert.Equal(Database.Orders.Length, rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Database.Orders[i].CustomerName, city(rows[i]));
            Assert.Equal(Database.Orders[i].Amount, amount(rows[i]));
            Assert.True(id(rows[i]) > 0);
        }
    }

    [Fact]
    public async Task A_record_struct_dto_filled_by_its_positional_constructor()
    {
        Database.Ready();

        var rows = await ShapesAsync(o => new RecStructPos(o.Id, o.CustomerName, o.Amount));

        AssertEveryRow(rows, r => r.City, r => r.Amount, r => r.Id);

        // NULL 那一格在 struct 里仍然是 null：值类型没有"整颗对象为 null"可言，可空性只能落在槽位上。
        Assert.Null(rows[1].City);
    }

    [Fact]
    public async Task A_record_struct_dto_filled_by_member_init()
    {
        Database.Ready();

        var rows = await ShapesAsync(o => new RecStructProps { Id = o.Id, City = o.CustomerName, Amount = o.Amount });

        AssertEveryRow(rows, r => r.City, r => r.Amount, r => r.Id);
        Assert.Null(rows[1].City);
    }

    /// <summary>只读 record struct：槽位只有 <c>init</c> 访问器，构造器那条分支必须自己走通，
    /// 不能悄悄退化成"先建再改"。</summary>
    [Fact]
    public async Task A_readonly_record_struct_dto_filled_by_its_positional_constructor()
    {
        Database.Ready();

        var rows = await ShapesAsync(o => new RoRecStructPos(o.Id, o.CustomerName, o.Amount));

        AssertEveryRow(rows, r => r.City, r => r.Amount, r => r.Id);
        Assert.Null(rows[1].City);
    }

    /// <summary>普通 struct 的公有<b>字段</b>：这一类走的是成员赋值落到 <c>FieldInfo</c> 上，
    /// 和属性那条不是一回事。</summary>
    [Fact]
    public async Task A_plain_struct_dto_with_fields_filled_by_member_init()
    {
        Database.Ready();

        var rows = await ShapesAsync(o => new PlainStruct { Id = o.Id, City = o.CustomerName, Amount = o.Amount });

        AssertEveryRow(rows, r => r.City, r => r.Amount, r => r.Id);
        Assert.Null(rows[1].City);
    }

    /// <summary>class 那一侧的对照，钉住"这一批改的只有值类型"：record class 的位置构造器
    /// 行为与形状都不许因为 struct 那条分支而漂移。</summary>
    [Fact]
    public async Task A_record_class_dto_still_works_the_same_way()
    {
        Database.Ready();

        var rows = await ShapesAsync(o => new RecPos(o.Id, o.CustomerName, o.Amount));

        AssertEveryRow(rows, r => r.City, r => r.Amount, r => r.Id);
        Assert.Null(rows[1].City);
    }

    /// <summary>元组仍然不是投影形状（§14 记着的缺口）：<c>ValueTuple</c> 住在 BCL 里，
    /// 与 <c>DateTime</c> 同一侧，所以它按"一颗值"处理、当场拒绝，而不是发射出一段没人要的列。
    /// 元组字面量 <c>(o.Id, o.Name)</c> 更早就在编译期过不去（CS8143 表达式树不能包含元组字面量）。</summary>
    [Fact]
    public async Task A_tuple_projection_is_still_rejected_rather_than_guessed()
    {
        Database.Ready();

        var exception = Assert.Throws<UntranslatableExpressionException>(
            () => Tagged().Select(o => new ValueTuple<int, decimal>(o.Id, o.Amount)).ToSql());

        Assert.Contains("无映射", exception.Message, StringComparison.Ordinal);

        // 同一条查询换成受支持的形状必须照样跑得动：拒绝不能是"这一批用例整体坏了"的伪装。
        Assert.Equal(Database.Orders.Length, (await ShapesAsync(o => new RecPos(o.Id, o.CustomerName, o.Amount))).Count);
    }
}

public record RecPos(int Id, string? City, decimal Amount);

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
