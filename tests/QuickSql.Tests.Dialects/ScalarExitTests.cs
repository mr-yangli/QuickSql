using System.Collections;
using System.Data;
using System.Data.Common;
using System.Reflection;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Query.Model;
using QuickSql.Schema;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§14 债③ 的那一支结了：<b>标量与聚合出口也领一张票</b>（第八笔）。
///
/// <para>第五笔丙把"这一列能不能不装箱"的探测从每值提到每场次，但它只落在实体那一档，
/// 留下的理由是「标量那一档的调用点是泛型的，<c>T</c> 在编译期不知道，而臂是按型各一条的静态方法」。
/// <b>那句理由在第八笔被推翻</b>：<c>Materializer.ScalarRows&lt;T&gt;</c> 里 <c>typeof(T)</c> 每一支实例化
/// 都是定死的，按型落一支臂只是把它从"编译期已知"换成"实例化期已知"。这一批用例问的就是换完之后的三件事：
/// ① 落不落臂的判据在这一档没有换形（仍是 <c>GetFieldType</c> 与请求型同型）；
/// ② 省下来的确实是装箱那一份而值逐格没变；
/// ③ 这一档<b>唯一真正留着的分别是空值守归谁问</b> —— 实体侧的成员声明自带可空性，而这里的 <c>T</c>
/// 只是调用方要的形状：<c>Sum</c> 打在空集上、<c>Max</c> 打在全空那一列上交回的 NULL 是合法答案，
/// 不是模型在说谎，所以第七笔丁那一刀（非空值类型臂不再问 <c>IsDBNull</c>）在这一档由出口替它问。</para>
///
/// <para><b>全程不连库</b>，两片替身分工与方言册其余的读路径用例同一条口径：
/// 装箱那几条跑在 <see cref="RawReader"/> 上（<see cref="DataTableReader"/> 的 <c>GetInt32</c> 也是
/// "取 object 再转"，用它会量出"臂没有省任何东西"这个假结论），而"假驱动上值仍然对、就是不省"那一句
/// 反过来专门跑在 <see cref="DataTableReader"/> 上（<see cref="The_gate_fake_driver_reads_right_through_the_exit_and_saves_nothing"/>）。
/// 分配形状那两条只在 Release 二进制上成立，与 §12.8 的其余分配断言同一档。
/// 空值那一问由替身自己记的 <see cref="RawReader.NullAsks"/> 数出来 —— 产品代码里没有计数器（§12.7 C）。</para>
///
/// <para><b>这里没有覆盖的两件事，别读成覆盖过了</b>：真驱动上"臂不贵于门"的读档见证在集成册
/// <c>MaterializationAllocationTests</c>，而"空集上的 <c>Sum</c> 交回 0 而不是抛"那句 ORM 级的事实在
/// <c>ReadPathTests</c> 的真库用例里。本册只判离线替身能判的那一半。</para></summary>
public sealed class ScalarExitTests
{
    static readonly DateTime Moment = new(2026, 1, 2, 3, 4, 5);
    static readonly Guid Key = Guid.Parse("8f0d6f2a-3f0c-4a1b-9c2d-e5f6a7b8c9d0");

    static readonly IEntitySchemaRegistry Schemas = SqlServer.Orm().Schemas;

    static EntityContract Contract() => Schemas.Contract(typeof(Order));

    /// <summary>按运行时类型取这一型的标量出口委托。<b>只在不对量具的场合用</b>
    /// （<c>DynamicInvoke</c> 自己装箱，量不出"每值零装箱"那一半），为的是让下面那张表能按型逐格判。</summary>
    static Delegate ExitFor(Type declared)
        => (Delegate) typeof(Materializer)
            .GetMethod(nameof(Materializer.ScalarRows), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(declared).Invoke(null, null)!;

    static object? Door(Type declared, DbDataReader reader)
        => typeof(ValueConverter).GetMethod(nameof(ValueConverter.Read), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(declared).Invoke(null, [reader, 0]);

    /// <summary>与 <c>ZeroBoxingTests</c> 同一个量法（预热到位再按当前线程字节数取差），
    /// 但这里<b>跑三趟取最小</b>：这一批的判据含"出口每值 0 B"那一句按字节的硬断言，而
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> 记的是<em>当前线程</em>的账，跑在测试池线程上时，
    /// 池自己的那一截（第一版实测 <c>double?</c> 一格 0.3 B/值）会混进分子，把量具的私货读成产品的分配。
    /// <b>真分配躲不过这一手</b>：每值一颗盒子在三趟里是同一个数，稀有分配也在每趟的分子里按比例出现 ——
    /// 取最小只是剔掉与本用例无关的那一截，而不是把判据放宽。</summary>
    static double PerCall(Action body)
    {
        var least = double.MaxValue;

        for (var trial = 0; trial < 3; trial++)
        {
            for (var i = 0; i < 2_000; i++)
                body();

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 20_000; i++)
                body();

            least = Math.Min(least, (GC.GetAllocatedBytesForCurrentThread() - before) / 20_000.0);
        }

        return Math.Round(least, 1);
    }

    // ── ① 判据没有换形：落不落臂仍只看驱动交回的类型 ──────────────────────────

    /// <summary>这一档的判据表。每一格同时问三句：<b>落不落臂</b>（<see cref="ReadOps.Probe"/>）、
    /// <b>出口与门是否同值</b>（臂省的是"怎么取"）、<b>literal 值对不对</b>（不是两头一起错）。
    /// 第四句是这一批才问得出的新东西：<b>空值那一问的颗数</b> —— 有臂时每值一颗（出口替不问空值的臂问，
    /// 或可空那一族臂自己问），落门时一颗也没有（门只看 <c>GetValue</c> 交回的是不是 <c>DBNull</c>）。</summary>
    [Fact]
    public void Only_a_requested_type_the_driver_answers_in_kind_reads_through_an_arm()
    {
        var cases = new (Type Declared, Type Column, Array Values, object? Expected, bool Armed, string Why)[]
        {
            (typeof(int), typeof(int), new[] { 7 }, 7, true, "同型 ⇒ 臂"),
            (typeof(int?), typeof(int), new[] { 7 }, (int?) 7, true, "可空成员按底层型探，臂是可空那一族"),
            (typeof(long), typeof(int), new[] { 7 }, 7L, false, "窄列宽成员：整数族不同型 ⇒ 门"),
            (typeof(int), typeof(long), new[] { 7L }, 7, false, "宽列窄成员：同理进门，门窄化"),
            (typeof(decimal), typeof(decimal), new[] { 1.25m }, 1.25m, true, "同型 ⇒ 臂"),
            (typeof(decimal), typeof(double), new[] { 2.5d }, 2.5m, false, "double 列配 decimal 成员：不同型"),
            (typeof(double), typeof(decimal), new[] { 1.25m }, 1.25d, false, "decimal 列读成 double 会丢精度，不直读"),
            (typeof(string), typeof(string), new[] { "abc" }, "abc", true, "同型 ⇒ 臂"),
            (typeof(string), typeof(int), new[] { 7 }, "7", false, "整数列读成串：门交回 <c>ToString()</c>"),
            (typeof(DateTime), typeof(DateTime), new[] { Moment }, Moment, true, "同型 ⇒ 臂"),
            (typeof(DateOnly), typeof(DateTime), new[] { Moment }, DateOnly.FromDateTime(Moment), false, "驱动没有 DateOnly 那一首 ⇒ 门"),
            (typeof(TimeOnly), typeof(DateTime), new[] { Moment }, TimeOnly.FromDateTime(Moment), false, "同上"),
            (typeof(bool), typeof(bool), new[] { true }, true, true, "同型 ⇒ 臂"),
            (typeof(bool), typeof(int), new[] { 1 }, true, false, "整数列读成布尔：门按 0/非 0 落"),
            (typeof(byte), typeof(byte), new byte[] { 3 }, (byte) 3, true, "同型 ⇒ 臂"),
            (typeof(sbyte), typeof(sbyte), new sbyte[] { -3 }, (sbyte) -3, false, "类型相等而臂不存在（<c>GetSByte</c> 没有这一首）"),
            (typeof(short), typeof(short), new short[] { 4 }, (short) 4, true, "同型 ⇒ 臂"),
            (typeof(float), typeof(float), new[] { 2.5f }, 2.5f, true, "同型 ⇒ 臂"),
            (typeof(double), typeof(double), new[] { 2.5d }, 2.5d, true, "同型 ⇒ 臂"),
            (typeof(char), typeof(char), new[] { 'x' }, 'x', true, "同型 ⇒ 臂"),
            (typeof(Guid), typeof(Guid), new[] { Key }, Key, true, "同型 ⇒ 臂"),
            (typeof(Guid), typeof(byte[]), new[] { System.Text.Encoding.ASCII.GetBytes(Key.ToString()) }, Key, false, "36 字节串形态：不同型 ⇒ 门的 <c>ToGuid</c>"),
            (typeof(byte[]), typeof(byte[]), new[] { new byte[] { 1, 2, 3 } }, new byte[] { 1, 2, 3 }, false, "类型相等，但没有「一次调用一个值」的臂"),
            (typeof(OrderState), typeof(int), new[] { (int) OrderState.Paid }, OrderState.Paid, false, "枚举成员 vs 底层整数列，永远不同型 ⇒ 永远进门"),
            (typeof(OrderState?), typeof(int), new[] { (int) OrderState.Paid }, (OrderState?) OrderState.Paid, false, "可空枚举同理"),
            (typeof(object), typeof(int), new[] { 7 }, 7, false, "请求型是 object：探测在 ByType 之外 ⇒ 门"),
            (typeof(TimeSpan), typeof(int), new[] { 5 }, TimeSpan.FromTicks(5), false, "整数列读成 TimeSpan：门按 ticks 落"),
        };

        foreach (var (declared, column, values, expected, armed, why) in cases)
        {
            using var reader = new RawReader(("v", column, values, null));

            Assert.True(reader.Read());

            Assert.Equal(armed, ReadOps.Probe(reader, 0, declared) != ReadOps.Boxed);

            // 上面那句探测本身问过一次驱动，清空两截账，让下面读的是"出口自己付了什么"。
            reader.FieldTypeAsks.Clear();
            reader.NullAsks = 0;

            var viaDoor = Door(declared, reader);

            // 门那一趟只问 GetValue：既不问驱动列型（探测不在这一档），也不问空值（DBNull 自己会说话）。
            Assert.Empty(reader.FieldTypeAsks);
            Assert.Equal(0, reader.NullAsks);

            var viaExit = ExitFor(declared).DynamicInvoke(reader);

            // <b>探测在这一档也是短路的</b>（<c>ReadOps.Probe</c> 先按底层型查编号表，查不到就交 <c>Boxed</c>，
            // 连 <c>GetFieldType</c> 都不问）：所以"问没问驱动"不能写死成"每格一颗"。
            // 那颗数从臂表反推而不是抄一份类型清单：<c>ArmFor</c> 的键域与探测的短路判据同源，
            // 抄清单会在产品加一族臂时红成"用例跟不上"，反推则跟着一起放宽 —— 而这一条要钉的只是"同一次探测"。
            var underlying = Nullable.GetUnderlyingType(declared) ?? declared;
            List<int> asks = DirectRead.ArmFor(underlying) is not null ? [0] : [];

            Assert.Equal(asks, reader.FieldTypeAsks);
            Assert.Equal(armed ? 1 : 0, reader.NullAsks);
            Assert.True(Same(viaDoor, viaExit), $"{why}：出口与门在这一格给了两个答案（门 {viaDoor} ∶ 出口 {viaExit}）");
            Assert.True(Same(expected, viaExit), $"{why}：值不对（要 {Show(expected)}，出口交回 {Show(viaExit)}）");
        }
    }

    /// <summary>两格等不等。<b>不能写成 <c>Equals</c></b>：那张表里有 <c>byte[]</c> 这一格，
    /// 而数组的 <c>Equals</c> 是引用相等 —— 字面量那一颗与门交回的那一颗内容相同、不是同一个对象，
    /// 用 <c>Equals</c> 会得到一句"值不对（要 System.Byte[]，出口交回 System.Byte[]）"这种看不出所以然的红。
    /// 判"数组读回来是不是同一串字节"用的是 <see cref="StructuralComparisons"/>，其它型照旧走 <c>Equals</c>。</summary>
    static bool Same(object? expected, object? actual)
        => expected is null
            ? actual is null
            : expected.Equals(actual)
              || (expected is IStructuralEquatable a && actual is IStructuralEquatable b
                  && a.Equals(b, StructuralComparisons.StructuralEqualityComparer));

    static string Show(object? value)
        => value is Array values
            ? string.Join(",", values.Cast<object?>().Select(v => v?.ToString()))
            : value?.ToString() ?? "null";

    // ── ② 时机：每场次一颗探测，与行数无关 ────────────────────────────────────

    /// <summary>提升的立论在这一档的原样形式：<b>探测只跟 reader 数有关，跟值的个数无关</b>。
    /// 后半段钉的是"票按 reader 引用判要不要重探"，而这一档重探之后<em>换的是整条路</em>：
    /// 第一只 reader 的列型与请求型同型（落臂），第二只不同型（落门），同一只委托两头都得读对。
    /// 少了这一半，"每场次一张票"会退化成"委托级一张票"，症状是换了列型还按旧编号取。</summary>
    [Fact]
    public void The_exit_asks_the_driver_once_per_reader_however_many_values_it_reads()
    {
        var read = Materializer.ScalarRows<int>();

        using var five = new RawReader(("v", typeof(int), new[] { 1, 2, 3, 4, 5 }, null));

        var seen = new List<int>();

        while (five.Read())
            seen.Add(read(five));

        Assert.Equal([1, 2, 3, 4, 5], seen);
        Assert.Equal([0], five.FieldTypeAsks);
        Assert.Equal(5, five.NullAsks);

        using var drift = new RawReader(("v", typeof(long), new[] { 42L }, null));

        Assert.True(drift.Read());
        Assert.Equal(42, read(drift));
        Assert.Equal([0], drift.FieldTypeAsks);

        // 空值颗数是这一档的形状判据：落门那一趟由 <c>GetValue</c> 交 <c>DBNull</c>，一句 IsDBNull 也不问。
        Assert.Equal(0, drift.NullAsks);

        using var rearmed = new RawReader(("v", typeof(int), new[] { 7 }, null));

        Assert.True(rearmed.Read());
        Assert.Equal(7, read(rearmed));
        Assert.Equal([0], rearmed.FieldTypeAsks);
        Assert.Equal(1, rearmed.NullAsks);
    }

    // ── ③ 空值：这一档由出口答，与第七笔丁那一刀相反 ──────────────────────────

    /// <summary>这一条是第八笔的语义线，而它必须先红才说明改动有意义：
    /// <b>有臂而值为 NULL 时，出口交回 <c>default</c> 而不是抛</b>。同一只 reader、同一格上
    /// <see cref="DirectRead.ReadInt32"/> 是直接抛的（那片替身的 <c>Typed&lt;T&gt;</c> 与真驱动同形），
    /// 实体那一档也抛（<c>ZeroBoxingTests.A_null_keeps_the_nullability_of_the_member</c> 钉的就是那一头）——
    /// 差别不是臂换了两套实现，是<b>问空值的那个人换成了出口</b>，因为这一档的请求型不携带可空性。
    /// 可空那一族与 <c>string</c> 两头都给 <c>null</c>（臂自己问得到），落门的那些型仍由门接住。</summary>
    [Fact]
    public void A_null_at_the_scalar_exit_returns_the_doors_answer_instead_of_throwing()
    {
        using var raw = new RawReader(("v", typeof(int), new[] { 0 }, new[] { true }));

        Assert.True(raw.Read());
        Assert.Equal(ReadOps.Int32, ReadOps.Probe(raw, 0, typeof(int)));

        Assert.Equal(0, Materializer.ScalarRows<int>()(raw));

        var thrown = Assert.Throws<InvalidCastException>(() => DirectRead.ReadInt32(raw, 0, ReadOps.Int32));

        Assert.Contains("列 v 的这一行是空值", thrown.Message, StringComparison.Ordinal);

        // 实体那一档的同一格现在是抛（第七笔丁），而这一档不是：两句话在同一个文件里对读才看得出这一档换了答的人。
        using var order = new RawReader(
            ("Id", typeof(int), new[] { 0 }, new[] { true }),
            ("CustomerName", typeof(string), new string?[] { "张三" }, null),
            ("CustomerId", typeof(int), new[] { 42 }, null),
            ("Amount", typeof(decimal), new[] { 1234.5600m }, null),
            ("State", typeof(int), new[] { (int) OrderState.Shipped }, null),
            ("CreatedAt", typeof(DateTime), new[] { Moment }, null),
            ("PaidAt", typeof(DateTime), new DateTime[1], new[] { true }),
            ("Note", typeof(string), new string?[] { "加急，别丢字" }, null));

        Assert.True(order.Read());

        var plan = ReadOps.Build(order, Contract(), 0);

        Assert.Equal(ReadOps.Int32, plan.Ops[0]);
        Assert.Throws<InvalidCastException>(() => Schemas.ReadDelegate(typeof(Order))(order, plan));
        Assert.Equal(0, Materializer.ScalarRows<int>()(order));

        // 可空那一族与 string：NULL ⇒ null，不摊平（臂自己问空值，出口没有再问第二遍）。
        using var nullable = new RawReader(("v", typeof(int), new[] { 0 }, new[] { true }));

        Assert.True(nullable.Read());
        Assert.Null(Materializer.ScalarRows<int?>()(nullable));

        foreach (var (type, values) in new (Type, Array)[]
        {
            (typeof(decimal), new decimal[1]),
            (typeof(DateTime), new DateTime[1]),
            (typeof(Guid), new Guid[1]),
            (typeof(bool), new bool[1]),
            (typeof(char), new char[1]),
        })
        {
            using var lifted = new RawReader(("v", type, values, new[] { true }));

            Assert.True(lifted.Read());
            Assert.Null(ExitFor(typeof(Nullable<>).MakeGenericType(type)).DynamicInvoke(lifted));
        }

        using var text = new RawReader(("v", typeof(string), new string?[] { null }, new[] { true }));

        Assert.True(text.Read());
        Assert.Null(Materializer.ScalarRows<string>()(text));

        // 落门的型由门接住，答案照旧是声明类型的 default —— 这一半跟着改动没有动。
        using var enumColumn = new RawReader(("v", typeof(int), new[] { 0 }, new[] { true }));

        Assert.True(enumColumn.Read());
        Assert.Equal(OrderState.Draft, ExitFor(typeof(OrderState)).DynamicInvoke(enumColumn));
        Assert.Equal(new DateTime(), Materializer.ScalarRows<DateTime>()(enumColumn));
    }

    // ── ④ 省下来的确实是装箱那一份 ───────────────────────────────────────────

    /// <summary>每型各量一次：<b>出口一次取值 0 B，而门那一格至少付一颗盒子</b>（下界按型取 24 或 32）。
    /// 判据写法照 <c>ZeroBoxingTests</c> 那条口径 —— 量具必须真会装箱（所以跑在 <see cref="RawReader"/> 上），
    /// 而"门竟然不装箱了"要红成"判据失效，改判据而不是删用例"。</summary>
    [Fact]
    public void The_exit_saves_exactly_the_box_on_the_types_that_have_arms()
    {
        var report = new List<string>();

        Armed<int>("int", new[] { 7 }, 24, report);
        Armed<long>("long", new[] { 7L }, 24, report);
        Armed<short>("short", new short[] { 4 }, 24, report);
        Armed<byte>("byte", new byte[] { 3 }, 24, report);
        Armed<bool>("bool", new[] { true }, 24, report);
        Armed<char>("char", new[] { 'x' }, 24, report);
        Armed<float>("float", new[] { 2.5f }, 24, report);
        Armed<double>("double", new[] { 2.5d }, 24, report);
        Armed<DateTime>("DateTime", new[] { Moment }, 24, report);
        Armed<decimal>("decimal", new[] { 1.25m }, 32, report);
        Armed<Guid>("Guid", new[] { Key }, 32, report);

        Armed<int?>("int?", new[] { 7 }, 24, report);
        Armed<long?>("long?", new[] { 7L }, 24, report);
        Armed<short?>("short?", new short[] { 4 }, 24, report);
        Armed<byte?>("byte?", new byte[] { 3 }, 24, report);
        Armed<bool?>("bool?", new[] { true }, 24, report);
        Armed<char?>("char?", new[] { 'x' }, 24, report);
        Armed<float?>("float?", new[] { 2.5f }, 24, report);
        Armed<double?>("double?", new[] { 2.5d }, 24, report);
        Armed<DateTime?>("DateTime?", new[] { Moment }, 24, report);
        Armed<decimal?>("decimal?", new[] { 1.25m }, 32, report);
        Armed<Guid?>("Guid?", new[] { Key }, 32, report);

        // 字符串那一格要单独看一眼：两边都是 0 B，所以"相等"这件事本身不说明门是薄的 ——
        // 它能说明的只有"出口在这一格没有添上分配"，而那一句和上面 22 型共用一条判据（0 B）。
        using var reader = new RawReader(("v", typeof(string), new[] { "abc" }, null));

        Assert.True(reader.Read());

        var read = Materializer.ScalarRows<string>();
        var door = PerCall(() => _ = ValueConverter.Read<string>(reader, 0));
        var exit = PerCall(() => _ = read(reader));

        Assert.Equal(0, door);
        Assert.Equal(0, exit);

        report.Add($"string：装箱口 {door} B/值，出口 {exit} B/值（引用本来就不装箱）");

        // <b>读数要进日志</b>：这一批的判据是"每型两格"的那 23 对字节数，而 <c>report</c> 只在红的时候
        // 才跟着断言消息开口 —— 绿的时候一份痕迹都不留，台账那一格就只能写成"跑过了"。
        // 这里是替身驱动上的读数，真驱动那五家的同一批数在集成册（<c>MaterializationAllocationTests</c>）里打。
        Console.WriteLine("标量出口 ∶ RawReader（替身）∶ 每型的装箱口 ∶ 出口\n  " + string.Join("\n  ", report));
    }

    /// <summary>一型的两次量：<see cref="ValueConverter.Read{T}"/> 那一格与出口那一格，
    /// 中间只换了"走不走臂"。委托在测量之外建好，所以领票那一次的分配不进读数。</summary>
    static void Armed<T>(string name, Array values, double boxFloor, List<string> report)
    {
        using var reader = new RawReader(("v", values.GetType().GetElementType()!, values, null));

        Assert.True(reader.Read());
        Assert.NotEqual(ReadOps.Boxed, ReadOps.Probe(reader, 0, typeof(T)));

        var read = Materializer.ScalarRows<T>();
        var door = PerCall(() => _ = ValueConverter.Read<T>(reader, 0));
        var exit = PerCall(() => _ = read(reader));

        report.Add($"{name}：装箱口 {door} B/值，出口 {exit} B/值");

        Assert.True(door >= boxFloor, $"{name}：装箱口没有装箱那一份（实测 {door} B/值，下界 {boxFloor}）：" +
            "量具或运行时变了，先重看这一批的判据，别把这一句放开。");
        Assert.True(exit == 0,
            $"{name}：出口这一档不是每值零装箱（实测 {exit} B/值，同型的装箱口 {door} B/值）：" +
            "臂在这一型上多付了一截。先按这一格单独归因（是票、是守卫，还是三趟里最小的读数仍然非零），" +
            "再判要不要改判据 —— 不要把它改成「不贵于门」就交卷，那句在这批用例里是另一条（第 ⑤ 组）。");
    }

    // ── ⑤ 假驱动：值仍然对，就是不省 ─────────────────────────────────────────

    /// <summary><see cref="DataTableReader"/> 是门禁那一片替身，它的每一条 <c>GetXxx</c> 都是
    /// "取 object 再转"（§12.7 C 末尾那句"假驱动更差"，也是 §12.8 第 3 行至今 736 B/行的原因）。
    /// 所以这一档在它上面必须<b>读对而不省</b>：读对是正确性，不省是量具的性质。
    /// <b>这一条的存在是为了挡住一种假结论</b> —— 谁把上面第 ④ 组改成跑在 <c>DataTableReader</c> 上，
    /// 量到的是"臂没有省任何东西"，而那正是这片替身教过一次的坑。
    ///
    /// <para>每一型各摆一片表、把要量的那一颗放在<b>第 0 列</b>：这一档读的就是第 0 列，摆错了一列它不会报
    /// "这一列不是那个型"，只会安静地交回隔壁那一列的值（第一版就是被 <c>Id</c> 那一格骗过去的，
    /// 症状是 <c>Assert.Equal(1.25, 7)</c> 这种看不出所以然的红）。</para></summary>
    [Fact]
    public void The_gate_fake_driver_reads_right_through_the_exit_and_saves_nothing()
    {
        // 四颗的列型都与各自的请求型同型 ⇒ 这一片上臂同样生效，只是省不到东西。
        GateCase("Id", 7);
        GateCase("Amount", 1.25m);
        GateCase("CreatedAt", Moment);
        GateCase("CustomerName", "张三");

        using var reader = Gate("Id", 7);

        var read = Materializer.ScalarRows<int>();
        var door = PerCall(() => _ = ValueConverter.Read<int>(reader, 0));
        var exit = PerCall(() => _ = read(reader));

        Assert.True(door > 0, $"这片假驱动现在不装箱了（实测 {door} B/次）：量具换了行，上面第④组的分工要一起重看。");
        Assert.Equal(door, exit);
    }

    static void GateCase<T>(string name, T value)
    {
        using var reader = Gate(name, value!);

        Assert.Equal(value, Materializer.ScalarRows<T>()(reader));
    }

    /// <summary>门禁那片假驱动的一行两列表：第 0 列是要量的那一颗，第 1 列是任何标量出口都读不到的填料。</summary>
    static DbDataReader Gate(string name, object value)
    {
        var table = new DataTable();

        table.Columns.Add(name, value.GetType());
        table.Columns.Add("Filler", typeof(int));
        table.Rows.Add(value, 7);

        var reader = table.CreateDataReader();

        _ = reader.Read();

        return reader;
    }

    // ── ⑥ 路由：两种标量形状都走到这张票上 ───────────────────────────────────

    /// <summary><see cref="Materializer.Create{T}"/> 的那两个 case 标签后面挂的是这一档的票，
    /// 不是一句逐字进箱的读法。形状是手搭的 <see cref="ValueProjection"/> 与 <see cref="AggregateProjection"/>
    /// （<c>QueryCompiler</c> 产出什么形状由 §13.11 那批用例钉，这里要钉的是物化层的分派）。
    /// 第二句是这一档的新语义见证：<b>空集上的 <c>SUM</c> 交回 NULL，而它是合法答案</b> ⇒ 0 而不是抛。</summary>
    [Fact]
    public void Both_scalar_projection_shapes_route_to_the_ticketed_exit()
    {
        var value = Materializer.Create<int>(Model(new ValueProjection(new InlineLiteral("[v]"), typeof(int))), Schemas);
        var sum = Materializer.Create<decimal>(
            Model(new AggregateProjection(AggregateKind.Sum, new InlineLiteral("[v]"), typeof(decimal), false)), Schemas);

        using var ints = new RawReader(("v", typeof(int), new[] { 7 }, null));

        Assert.True(ints.Read());
        Assert.Equal(7, value(ints));

        for (var i = 0; i < 1_000; i++)
            _ = value(ints);

        // 一千个值仍然只有一颗探测 —— 债③ 之前这一档根本没有票，也就没有这一句可问。
        Assert.Equal([0], ints.FieldTypeAsks);

        using var nulls = new RawReader(("v", typeof(decimal), new decimal[1], new[] { true }));

        Assert.True(nulls.Read());
        Assert.Equal(0m, sum(nulls));

        // 落门那一支也在同一只委托上：请求型是 int 而列是 long，值由门窄化，探测仍每场次一颗。
        using var drift = new RawReader(("v", typeof(long), new[] { 42L }, null));

        Assert.True(drift.Read());
        Assert.Equal(42, value(drift));
        Assert.Equal([0], drift.FieldTypeAsks);
    }

    static QueryModel Model(ProjectionPlan plan)
        => new()
        {
            // 标量分支不读表位：读的就是第 0 列。给空表是故意的 —— 哪天它开始读 Tables，
            // 炸在这里比安静地拿一张按父表列型落好的票好（§12.7 C 的口径与 MaterializerCacheTests 同一条）。
            Tables = [],
            Plan = plan,
        };

    // ── ⑦ 架构：接线只有一处，而臂的尽头仍是那扇门 ────────────────────────────

    /// <summary>§12.7 C 第二条在这一档的形式：<b>这一列怎么读仍然只有一处可改</b>。
    /// 数的是源文件字样：两个 case 标签后面必须紧跟 <c>return ScalarRows&lt;T&gt;();</c>，
    /// 出口里落不了臂那一支必须整句是 <c>ValueConverter.Read&lt;T&gt;(reader, 0)</c>，
    /// 而 <c>ScalarArm&lt;T&gt;</c> 的那一支兜底必须是门本身（<c>?? Door</c> 后面那一句就是 <c>Read&lt;T&gt;</c>）——
    /// 不是复制来的等价实现。<b>两处都不许出现 <c>reader.GetValue(</c></b>：那等于把门复制到门外，
    /// 而这一条与 <c>TypedReadTests.Every_materialization_call_site_reads_through_the_single_door</c> 的配额是两码事，
    /// 那句数的是全文件出现次数，这一句数的是"这两段里没有"。</summary>
    [Fact]
    public void The_scalar_exit_is_wired_at_the_two_case_labels_and_ends_back_at_the_door()
    {
        var source = File.ReadAllLines(Path.Combine(Root(), "src", "QuickSql.Core", "Materialization", "Materializer.cs"));

        var routed = Lines(source, "case ValueProjection:", "case ObjectProjection");
        var exit = Lines(source, "public static Func<DbDataReader, T> ScalarRows<T>()", "static class ScalarArm");
        var arm = Lines(source, "static class ScalarArm<T>", "── Include 物化");

        Assert.Equal(["case ValueProjection:", "case AggregateProjection:", "return ScalarRows<T>();"], routed);

        Assert.Contains("op = ReadOps.Probe(reader, 0, typeof(T));", exit);
        Assert.Contains("if (op == ReadOps.Boxed)", exit);
        Assert.Contains("return ValueConverter.Read<T>(reader, 0);", exit);
        Assert.Contains("return ScalarArm<T>.GuardsNull && reader.IsDBNull(0)", exit);
        Assert.Contains(": ScalarArm<T>.Read(reader, 0, op);", exit);

        Assert.Contains("DirectRead.ArmFor(typeof(T)) as Func<DbDataReader, int, byte, T> ?? Door;", arm);
        Assert.Contains("static T Door(DbDataReader reader, int ordinal, byte op) => ValueConverter.Read<T>(reader, ordinal);", arm);
        Assert.Contains("typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null;", arm);

        Assert.DoesNotContain(exit, l => l.Contains("reader.GetValue("));
        Assert.DoesNotContain(arm, l => l.Contains("reader.GetValue("));
    }

    /// <summary><see cref="ScalarGuard"/> 那条分界线的可核对形式：<b>出口问不问空值，跟着臂的返回型走</b> ——
    /// 臂交回 <c>T</c> 的那一族不问（不问正是第七笔丁敢跳的理由），交回 <c>T?</c> 或 <c>string</c> 的那一族
    /// 自己会问，所以出口不能再问一遍。判据从 <see cref="DirectRead.ArmFor"/> 现取，不是抄一份类型清单：
    /// 有人给某一型补了臂而忘了它属于哪一族，这一句会红在"两头都不问"或"两头都问"上。</summary>
    [Fact]
    public void The_exit_only_asks_for_the_null_when_its_arm_cannot_answer_it()
    {
        var types = new[]
        {
            typeof(bool), typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float),
            typeof(double), typeof(decimal), typeof(char), typeof(DateTime), typeof(Guid), typeof(string),
            typeof(OrderState), typeof(byte[]), typeof(DateOnly), typeof(TimeOnly), typeof(DateTimeOffset),
            typeof(TimeSpan), typeof(sbyte), typeof(ushort), typeof(uint), typeof(ulong), typeof(object),
        };

        var asked = 0;

        foreach (var type in types)
            foreach (var declared in Variants(type))
            {
                var arm = DirectRead.ArmFor(declared);

                if (arm is null) continue;

                asked++;

                // 判据是"臂交回的那个类型自己能不能表达 null"：能（string 与可空那一族）就不该由出口再问一遍，
                // 不能（非空值类型那一族，第七笔丁之后臂自己也不问了）就必须由出口问。
                var answersNull = arm.Method.ReturnType == typeof(string)
                    || Nullable.GetUnderlyingType(arm.Method.ReturnType) is not null;

                Assert.Equal(!answersNull, ScalarGuard(declared));
            }

        Assert.Equal(23, asked);

        // 没有臂的那些<em>引用</em>型：出口不问，答案整个来自门。
        foreach (var declared in new[] { typeof(byte[]), typeof(object), typeof(string) })
            Assert.False(ScalarGuard(declared));

        // 没有臂的那些<em>值</em>型（枚举、DateOnly、TimeOnly、TimeSpan、无符号族）这一格是 true，而它读不到那一格：
        // 出口在 <c>op == ReadOps.Boxed</c> 那一支就先 return 了门，守卫与臂都排在它后面。
        // 真正的判据不在这颗旗子上，在上面第①组的那颗计数里（落门 ⇒ 每值零问）；这里钉 true 而不是 false，
        // 是因为它是那条不变式的另一半：<b>旗子跟着"臂交回的类型能不能表达 null"走</b>，跟着有没有臂走反而会说谎。
        foreach (var declared in new[] { typeof(OrderState), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan), typeof(sbyte), typeof(ushort) })
            Assert.True(ScalarGuard(declared));
    }

    static bool ScalarGuard(Type declared)
    {
        var open = typeof(Materializer)
            .GetNestedTypes(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(t => t.Name.StartsWith("ScalarArm", StringComparison.Ordinal));

        return (bool) open.MakeGenericType(declared)
            .GetField("GuardsNull", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
    }

    /// <summary>一个 CLR 型的"请求型变体"：值类型还要问它的可空注解形态（<c>Nullable&lt;string&gt;</c> 不合法，
    /// 所以引用类型只有一种）。</summary>
    static Type[] Variants(Type type) => type.IsValueType ? [type, typeof(Nullable<>).MakeGenericType(type)] : [type];

    /// <summary>取 <paramref name="from"/> 那一行之后到 <paramref name="to"/> 之前的<b>去缩进、去空行</b>那一段，
    /// 好让上面的字样判据不必把源文件的缩进与排版也钉进去（缩进变了不该红，接线变了才该红）。</summary>
    static string[] Lines(string[] source, string from, string to)
    {
        var start = Array.FindIndex(source, l => l.Trim().Contains(from, StringComparison.Ordinal));
        var end = Array.FindIndex(source, start + 1, l => l.Trim().Contains(to, StringComparison.Ordinal));

        Assert.True(start >= 0 && end > start, $"在 Materializer.cs 里找不到那一段（{from} ∶ {to}）：接线挪了位置，先看清再改字样。");

        return [.. source[start..end].Select(l => l.Trim()).Where(l => l.Length > 0)];
    }

    static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "QuickSql.slnx")))
                return dir.FullName;

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上找不到 QuickSql.slnx。");
    }
}
