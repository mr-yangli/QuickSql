using System.Data;
using System.Data.Common;
using QuickSql.Materialization;
using QuickSql.Query.Model;
using QuickSql.Schema;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§12.7 C 第一条：DTO 投影的读取器<b>编译一次</b>而不是每次执行一次。
///
/// <para>判据是<b>委托引用相等</b>。未命中必然走到 <c>Expression.Lambda(...).Compile()</c>，
/// 而那一定产出一只新的委托，所以"两次拿到同一个引用"就是"命中了缓存"的充分可观察证据 ——
/// 不必为了测试在 <c>Materializer</c> 上开一个编译计数器口（那种口一旦有了，就会开始被别处消费）。</para>
///
/// <para>形状是手搭的 <see cref="ObjectProjection"/>，不经过 <c>ProjectionTranslator</c>：
/// 翻译器产出什么形状由 §13.11 那批用例钉，这里要钉的是"物化层把什么认作同一个形状"，
/// 而后者是一项缓存语义，不该需要一个活库才能判定。<b>推论：这一批证明不了"真链式的槽位带不带可空性"</b> ——
/// 那一半发生在翻译器侧，见证在集成层 <c>ReadPathTests</c> 里读真库 <c>PaidAt IS NULL</c> 行的那条 DTO 投影用例；
/// 别把手搭形状的用例读成那条也测过了。</para></summary>
public sealed class MaterializerCacheTests
{
    static readonly IEntitySchemaRegistry Registry = SqlServer.Orm().Schemas;

    static readonly (string Name, Type Member)[] TripleShape = [("Id", typeof(int)), ("City", typeof(string)), ("Amount", typeof(decimal))];

    static QueryModel Dto<T>(bool viaConstructor, params (string Name, Type Member)[] slots)
        => new()
        {
            // DTO 分支不读表位：列就是槽位序号 0..n-1（Materializer.Assemble）。给空表是故意的 ——
            // 哪天它开始读 Tables，炸在这里比安静地复用一只不匹配的委托好。
            Tables = [],
            Plan = new ObjectProjection(typeof(T), [.. slots.Select(s => new ProjectionSlot(s.Name, Literal(s.Name), s.Member))], viaConstructor),
        };

    /// <summary>占位的投影表达式。物化委托根本不读它（它只决定发射哪一列），
    /// 但两次调用给的<b>必须是不同实例</b>：缓存键若把表达式节点算进去，同一条链换个参数就永不相遇，
    /// 而"每次执行各编一次"正是要修的那个毛病。</summary>
    static InlineLiteral Literal(string name) => new("[" + name + "]");

    static T Materialize<T>(Func<DbDataReader, T> reader, params (Type Column, object? Value)[] columns)
    {
        var table = new DataTable();

        for (var i = 0; i < columns.Length; i++)
            table.Columns.Add("c" + i, columns[i].Column);

        table.Rows.Add([.. columns.Select(c => c.Value)]);

        using var data = table.CreateDataReader();

        _ = data.Read();

        return reader(data);
    }

    static readonly (Type, object?)[] TripleRow = [((Type) typeof(int), 7), (typeof(string), "city-a"), (typeof(decimal), 1.25m)];

    [Fact]
    public void The_same_dto_shape_reuses_one_compiled_reader()
    {
        var first = Materializer.Create<Row3>(Dto<Row3>(true, TripleShape), Registry);
        var second = Materializer.Create<Row3>(Dto<Row3>(true, TripleShape), Registry);

        Assert.True(ReferenceEquals(first, second),
            "同一个 DTO 形状拿到了两只不同的委托：装配仍然每次执行编一遍（§12.7 C 第一条）。");
    }

    [Fact]
    public void Constructor_and_member_init_projections_of_one_type_do_not_share_a_reader()
    {
        var constructed = Materializer.Create<Row3>(Dto<Row3>(viaConstructor: true, TripleShape), Registry);
        var initialized = Materializer.Create<Row3>(Dto<Row3>(viaConstructor: false, TripleShape), Registry);

        // 装配方式不进键的话，后一次会拿到前一只委托 —— 而 New 与 MemberInit 建的委托体是不同的，
        // 差在"值落到哪儿去"这一级，不是版式一级。
        Assert.False(ReferenceEquals(constructed, initialized),
            "同一个类型上，构造器投影与成员初始化投影共用了委托：缓存键少了装配方式。");
    }

    [Fact]
    public void Slot_names_are_part_of_the_shape_for_member_init_projections()
    {
        var straight = Materializer.Create<Pair>(Dto<Pair>(false, ("Left", typeof(string)), ("Right", typeof(string))), Registry);
        var swapped = Materializer.Create<Pair>(Dto<Pair>(false, ("Right", typeof(string)), ("Left", typeof(string))), Registry);

        Assert.False(ReferenceEquals(straight, swapped),
            "换了成员名还复用同一只委托：缓存键少了槽位名，两批行会互相落错列。");

        var columns = new (Type, object?)[] { (typeof(string), "first-column"), (typeof(string), "second-column") };

        var named = Materialize(straight, columns);

        Assert.Equal("first-column", named.Left);
        Assert.Equal("second-column", named.Right);

        // 反着命名 = 第 0 列绑到 Right。这两个断言是"名字进了键"的行为见证：
        // 只有引用不等还不够，共用一只委托的实现会在这里把值送错成员。
        var flipped = Materialize(swapped, columns);

        Assert.Equal("second-column", flipped.Left);
        Assert.Equal("first-column", flipped.Right);
    }

    [Fact]
    public void The_target_type_is_part_of_the_shape()
    {
        var typed = Materializer.Create<Row3>(Dto<Row3>(true, TripleShape), Registry);
        var boxed = Materializer.Create<object>(Dto<Row3>(true, TripleShape), Registry);

        // 投影类型相同而结果类型不同（T = Row3 与 T = object）：委托签名不同，共用一个字典项就会
        // 在取出来的那次强转上炸。键里带目标类型，是为了让它根本走不到那一步。
        Assert.False(ReferenceEquals(typed, boxed), "不同结果类型共用了同一只委托：缓存键少了目标类型。");

        Assert.IsType<Row3>(Materialize(boxed, TripleRow));
    }

    [Fact]
    public void A_shared_reader_materializes_each_result_set_on_its_own_terms()
    {
        var reader = Materializer.Create<Row3>(Dto<Row3>(true, TripleShape), Registry);

        Assert.Same(reader, Materializer.Create<Row3>(Dto<Row3>(true, TripleShape), Registry));

        var first = Materialize(reader, TripleRow);
        var second = Materialize(reader, ((Type) typeof(int), 4_001), (typeof(string), "city-b"), (typeof(decimal), 0.0001m));

        // 同一只缓存委托跑两个结果集：它必须无状态，唯一的"状态"是编译进去的列序号与类型。
        Assert.Equal((7, "city-a", 1.25m), (first.Id, first.City, first.Amount));
        Assert.Equal((4_001, "city-b", 0.0001m), (second.Id, second.City, second.Amount));
    }

    [Fact]
    public void A_null_lands_as_the_declared_slot_type_own_default_in_both_directions()
    {
        // 规则是"NULL 交回**声明类型自己的**默认值"，而这条规则只有两个方向都断才算钉住：
        // `int?` 交回 null（不是 0），`int` 交回 0（不是抛，也不是 skip 这一行）。
        // 摊平过一次的那个形状（可空成员 + `int` 槽位）就是这条规则被破坏的样子，见下一格。
        var nullable = Materializer.Create<NullableSlot>(Dto<NullableSlot>(false, ("Maybe", typeof(int?))), Registry);
        var plain = Materializer.Create<PlainSlot>(Dto<PlainSlot>(false, ("Maybe", typeof(int))), Registry);

        Assert.Null(Materialize(nullable, ((Type) typeof(int), DBNull.Value)).Maybe);
        Assert.Equal(0, Materialize(plain, ((Type) typeof(int), DBNull.Value)).Maybe);
    }

    [Fact]
    public void Nullability_is_part_of_the_shape_so_collapsed_and_declared_slots_never_share_a_reader()
    {
        // 这两个委托的差别只在槽位带不带 `Nullable<>`：共用一只的话，NULL 会静默变成 0，
        // 而缓存键少了这一维不会有任何报错 —— 它是这一族故障里最便宜的一种，所以单独钉。
        var declared = Materializer.Create<NullableSlot>(Dto<NullableSlot>(false, ("Maybe", typeof(int?))), Registry);
        var collapsed = Materializer.Create<NullableSlot>(Dto<NullableSlot>(false, ("Maybe", typeof(int))), Registry);

        Assert.False(ReferenceEquals(declared, collapsed), "可空槽位与摊平槽位共用了同一只委托：缓存键少了可空性。");

        Assert.Null(Materialize(declared, ((Type) typeof(int), DBNull.Value)).Maybe);
        Assert.Equal(0, Materialize(collapsed, ((Type) typeof(int), DBNull.Value)).Maybe);
    }

    [Fact]
    public void A_nullable_constructor_parameter_read_from_a_collapsed_slot_fails_loudly_at_assembly()
    {
        // 槽位类型与构造器参数类型不一致时装配那一步直接抛（`Expression.New` 不做隐式加宽）。
        // 记这一格是为了留下"同一条缺陷在两条分支上的两种脸色"：成员初始化那条静默给 0（上两格），
        // 构造器这条当场炸在装配上 —— 而炸点在第一次执行那次，不是编译期。
        Assert.Throws<ArgumentException>(
            () =>
            {
                _ = Materializer.Create<MaybeCtor>(Dto<MaybeCtor>(true, ("Maybe", typeof(int))), Registry);
            });

        // 同一形状给回声明类型（`int?`）就能装配，并且 NULL 落成 null —— 与成员初始化那条分支同一条规则。
        var declared = Materializer.Create<MaybeCtor>(Dto<MaybeCtor>(true, ("Maybe", typeof(int?))), Registry);

        Assert.Null(Materialize(declared, ((Type) typeof(int), DBNull.Value)).Maybe);
    }

    /// <summary>§12.7 C 第一条的<b>执行期那一半</b>：导航访问器（<c>MemberAccessors</c> 的 Getter/Setter/
    /// BindCollection）也<b>编译一次</b>、按成员缓存，而不是每次调用各编一次。这一格与上面那批 DTO 读取器
    /// 同一条纪律，判据也同一个 —— <b>委托引用相等</b>：未命中必然走 <c>Expression.Lambda(...).Compile()</c>
    /// 产出一只新委托，所以"两次拿到同一个引用"就是"命中了缓存"的充分可观察证据。
    ///
    /// <para>为什么单钉这一格：<c>Include</c> / <c>IncludeMany</c> / 拆分加载 / <c>DbSet.Load</c> 四条路
    /// 都在<b>每次执行</b>调这三个口（不是启动期一次），没有缓存时每执行一趟就各付一轮反射定位 +
    /// <c>Expression.Compile</c>（数十 µs 起）。<b>报错文本不进键</b>是这里唯一要守的边界：四条路给的
    /// <c>missingMessage</c> 各不相同，若把它算进键，同一个成员就会因文案不同各编一份 —— 那正是要修的毛病，
    /// 所以两次调用故意给不同的报错文本，仍须命中同一只委托。</para></summary>
    [Fact]
    public void Navigation_accessors_compile_once_per_member_regardless_of_the_error_text()
    {
        // 同一个成员、两套不同的报错文本：键只认 (声明类型, 成员名[, 元素型])，文案不进键。
        var getterA = MemberAccessors.Getter(typeof(Order), nameof(Order.Note), "第一套缺失文案");
        var getterB = MemberAccessors.Getter(typeof(Order), nameof(Order.Note), "第二套完全不同的缺失文案");
        Assert.Same(getterA, getterB);

        var setterA = MemberAccessors.Setter(typeof(Order), nameof(Order.Note), "甲");
        var setterB = MemberAccessors.Setter(typeof(Order), nameof(Order.Note), "乙");
        Assert.Same(setterA, setterB);

        var bindA = MemberAccessors.BindCollection(typeof(Order), nameof(Order.Items), typeof(OrderItem), "A");
        var bindB = MemberAccessors.BindCollection(typeof(Order), nameof(Order.Items), typeof(OrderItem), "B");
        Assert.Same(bindA, bindB);

        // 不同成员是不同键：缓存不会把两颗成员并成一只委托（并了就是"取错值"而不是"没变快"）。
        var otherGetter = MemberAccessors.Getter(typeof(Order), nameof(Order.CustomerName), "另一颗成员");
        Assert.NotSame(getterA, otherGetter);

        // 缓存的是委托，不是"关掉了功能"：命中同一只委托仍读得出真值。
        var order = new Order { Note = "still-works" };
        Assert.Equal("still-works", getterA(order));
    }
}

/// <summary>三个槽位、三种类型，且同时给出构造器与可写属性 —— 为的是让"装配方式"这一维能在同一个类型上分开测。</summary>
public sealed class Row3
{
    public Row3()
    {
    }

    public Row3(int id, string city, decimal amount)
    {
        Id = id;
        City = city;
        Amount = amount;
    }

    public int Id { get; set; }

    public string City { get; set; } = "";

    public decimal Amount { get; set; }
}

/// <summary>两个同为 <see cref="string"/> 的槽位：类型分布完全相同，只差名字 ——
/// 于是"键里有没有名字"这件事只能在落上去的值里被看到，而这是唯一能把它逼出来的形状。</summary>
public sealed class Pair
{
    public string Left { get; set; } = "";

    public string Right { get; set; } = "";
}

/// <summary>可空成员：槽位给 <c>int?</c> 时读回 null，给摊平后的 <c>int</c> 时读回 0 —— 同一类型两种槽位形状都在这里，
/// 因为"可空性是不是键的一部分"必须能在同一个 <c>T</c> 上判，否则目标类型那一维会替它挡下来。</summary>
public sealed class NullableSlot
{
    public int? Maybe { get; set; }
}

/// <summary>非空成员：这一格不是凑数的对照。<c>int</c> 槽位遇到 NULL 交回 0 是<b>规则的另一半</b>
/// （"给声明类型自己的默认值"），只测可空那一侧的话，把 <c>ValueConverter</c> 的 null 分支整个删掉也能通过。</summary>
public sealed class PlainSlot
{
    public int Maybe { get; set; }
}

/// <summary>只有一个可空值类型参数的构造器形态：上面那格要的正是"<c>int</c> 实参配 <c>int?</c> 参数"这种错配，
/// 而它在成员初始化形态上是静默的、只有在这里才是响的。</summary>
public sealed record MaybeCtor(int? Maybe);
