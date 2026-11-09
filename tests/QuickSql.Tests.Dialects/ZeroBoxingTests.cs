using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using QuickSql.Generated;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§16.21 那一格的落地：<b>「这一列能不能不装箱」的探测从每值挪到每场次</b>，
/// 于是每值只剩 <c>IsDBNull</c> + 原始访问器（真驱动实测那格 89.0 ns / 0 B，装箱口 95.2 ns / 24 B）。
/// 形态是 <see cref="ReadPlan"/>（每场次一张票）+ <see cref="ReadOps"/>（唯一的判定处）+
/// <see cref="DirectRead"/>（一型一条臂，臂的非直读分支就是那扇装箱口）。
///
/// <para><b>这一批改的是 v0.14 那条否证的边界，不是把它推翻</b>：当年红的是"每值问一次
/// <c>GetFieldType</c>"（+41.5 ns 买 24 B），而 §16.21 记下它要成立所需的形态是"每场次问一次、
/// 每值只留原始访问器"。所以这里的判据全在问那三条边界：① 探测的次数是每列每场次一次
/// （<see cref="The_probe_asks_the_driver_once_per_column_per_session_not_once_per_value"/>）；
/// ② 臂只在驱动交回的类型与成员声明类型<em>是同一个</em>时才生效
/// （<see cref="Only_a_column_whose_driver_type_matches_the_declaration_gets_an_arm"/>）；
/// ③ 省下来的确实是装箱那一份而值逐格没变（后五条）。</para>
///
/// <para><b>全程不连库</b>，用两片替身，各有分工：判定表与值对拍跑在 <see cref="DataTableReader"/> 上
/// （它是门禁那片假驱动，与 §12.8 的读数同源），而<b>装箱那两条跑在 <see cref="RawReader"/> 上</b> ——
/// <see cref="DataTableReader"/> 的 <c>GetInt32</c> 也是"取 object 再转"，装箱一分不省，
/// 用它会量出"臂没有省任何东西"这个假结论（这是第一版踩过的坑，台账在 §12.7 C 末尾那句"假驱动更差"）。
/// 分配形状的两条只在 <b>Release</b> 二进制上成立，与方言册其余的分配断言同一条口径。</para>
///
/// <para><b>Include 与批量投影里"换 reader 就重探"那一档</b>，这里只钉到 <see cref="Materializer.EntityRows"/>
/// 这一层（它是根实体与 Include 子节点共用的那个包装）。<c>IncludePlan.Materialize</c> 自己那份票、
/// 以及真库上的读，见证在集成册 <c>ReadPathTests</c>；别把这里的用例读成那两条也测过了。</para></summary>
public sealed class ZeroBoxingTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static IEntitySchemaRegistry Reflected() => new EntitySchemaRegistry(new ContractResolver(Provider));

    static IEntitySchemaRegistry Generated() => new GeneratedEntitySchemaRegistry(Provider, QuickSqlGeneratedMetadata.TryGet);

    static EntityContract Contract() => Reflected().Contract(typeof(Order));

    // ── ① 判定：只有"驱动交回的类型与声明类型是同一个"才直读 ──────────────────

    /// <summary>探测的判据表。<b>每一格都会因为"把判据放宽成看着像"而红</b>：长成员配窄列
    /// （<c>long</c> vs <c>int</c>）如果按"都是整数"就直读，那一列会在 <c>GetInt64</c> 上抛；
    /// <c>decimal</c> 成员配 <c>double</c> 列如果直读，值会静默丢精度。
    /// <c>byte[]</c> 那一列给了两个格子是故意的：成员 <c>Guid</c> 时是"不同型"（DM 把 uniqueidentifier
    /// 回成 byte[] 的形态），成员换成 <c>byte[]</c> 时是"相等但没有臂"——两条不同的理由，同一张答案：装箱口。</summary>
    [Fact]
    public void Only_a_column_whose_driver_type_matches_the_declaration_gets_an_arm()
    {
        using var reader = Table(
                [(typeof(int), 1), (typeof(decimal), 1.25m), (typeof(string), "abc"), (typeof(DateTime), Moment),
                 (typeof(byte[]), new byte[16]), (typeof(bool), true), (typeof(long), 9L), (typeof(char), 'x'),
                 (typeof(Guid), Key), (typeof(double), 2.5d)])
            .CreateDataReader();

        Assert.True(reader.Read());

        var cases = new (Type Declared, int Ordinal, byte Expected, string Why)[]
        {
            (typeof(int), 0, ReadOps.Int32, "相等 ⇒ 直读"),
            (typeof(int?), 0, ReadOps.Int32, "可空成员按底层型探"),
            (typeof(long), 0, ReadOps.Boxed, "窄列宽成员：整数族不同型，交回门里 Convert"),
            (typeof(decimal), 1, ReadOps.Decimal, "相等 ⇒ 直读"),
            (typeof(double), 1, ReadOps.Boxed, "decimal 列读成 double 会丢精度，不直读"),
            (typeof(string), 2, ReadOps.String, "相等 ⇒ 直读"),
            (typeof(DateTime), 3, ReadOps.DateTime, "相等 ⇒ 直读"),
            (typeof(byte[]), 4, ReadOps.Boxed, "类型相等，但 DbDataReader 没有「一次调用一个值」的臂"),
            (typeof(Guid), 4, ReadOps.Boxed, "DM 把 uniqueidentifier 回成 byte[]：列与成员不同型，落回收容所"),
            (typeof(bool), 5, ReadOps.Boolean, "相等 ⇒ 直读"),
            (typeof(long), 6, ReadOps.Int64, "相等 ⇒ 直读"),
            (typeof(char), 7, ReadOps.Char, "相等 ⇒ 直读"),
            (typeof(Guid), 8, ReadOps.Guid, "相等 ⇒ 直读"),
            (typeof(decimal), 9, ReadOps.Boxed, "double 列配 decimal 成员：不同型"),
            (typeof(OrderState), 0, ReadOps.Boxed, "枚举成员 vs 底层整数列，永远不同型 ⇒ 永远进门"),
            (typeof(OrderState?), 0, ReadOps.Boxed, "可空枚举同理：编号只落在 ByType 里那 12 型上"),
            (typeof(DateTimeOffset), 3, ReadOps.Boxed, "类型不等；而它即使相等也没有臂（臂表里没有这一型）"),
        };

        foreach (var (declared, ordinal, expected, why) in cases)
            Assert.Equal(expected, ReadOps.Probe(reader, ordinal, declared));

        // 判据只有一条：`GetFieldType(ordinal) == 声明的底层型`。上面那张表里落 Boxed 的格子只有两种理由——
        // 两个类型不相等，或者相等而臂不存在（byte[]、DateTimeOffset）。没有第三种理由，
        // 所以"以后要加条件"（按驱动名放行、按存储类型名放行）必须先把这张表换掉。
        Assert.All(cases, c => Assert.True(
            c.Expected == ReadOps.Boxed ||
            reader.GetFieldType(c.Ordinal) == (Nullable.GetUnderlyingType(c.Declared) ?? c.Declared),
            "有一格在列型与声明型不等时被标成了直读：" + c.Why));

        // 枚举是连问都不问驱动的：它在 <c>Probe</c> 的第一条短路里就出去了，排在 GetFieldType 之前。
        // 这件事记在这里是因为它改变了 <see cref="The_probe_asks_the_driver_once_per_column_per_session_not_once_per_value"/>
        // 的期望长度（八列的实体一场次只问七次），别让那七次被读成"漏探了一列"。
        using var counted = OrderRows(1);

        _ = ReadOps.Build(counted, Contract(), 0);

        Assert.DoesNotContain(4, counted.FieldTypeAsks);
    }

    /// <summary>臂表的宽度是<b>故意</b>窄的：一型一臂的前提是 <c>DbDataReader</c> 有"一次调用一个值"的原始访问器。
    /// 缺席的那一族（<c>byte[]</c> / <c>DateOnly</c> / <c>TimeOnly</c> / <c>DateTimeOffset</c> / <c>TimeSpan</c> /
    /// <c>sbyte</c> / 无符号整族 / 枚举 / 引用类型）不是因为难写，是因为没有对应的访问器可用。
    /// 而<b>可空值类型有自己的一族臂</b>（理由见 <see cref="A_null_keeps_the_nullability_of_the_member"/>）。</summary>
    [Fact]
    public void The_arm_table_is_deliberately_narrower_than_the_clr_type_space()
    {
        var value = new[]
        {
            typeof(bool), typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float),
            typeof(double), typeof(decimal), typeof(char), typeof(DateTime), typeof(Guid),
        };

        foreach (var type in value)
        {
            var plain = DirectRead.ArmFor(type);
            var lifted = DirectRead.ArmFor(typeof(Nullable<>).MakeGenericType(type));

            Assert.NotNull(plain);
            Assert.NotNull(lifted);

            // 两支臂必须真的两支：返回类型跟着声明型走。摊平成一支的话就是那次 0001-01-01 的回归。
            Assert.Equal(type, plain!.Method.ReturnType);
            Assert.Equal(typeof(Nullable<>).MakeGenericType(type), lifted!.Method.ReturnType);
        }

        Assert.NotNull(DirectRead.ArmFor(typeof(string)));

        var hasNot = new[]
        {
            typeof(OrderState), typeof(OrderState?), typeof(byte[]), typeof(DateOnly), typeof(TimeOnly),
            typeof(DateTimeOffset), typeof(TimeSpan), typeof(sbyte), typeof(ushort), typeof(uint),
            typeof(ulong), typeof(object),
        };

        foreach (var type in hasNot)
            Assert.Null(DirectRead.ArmFor(type));

        // 「GetTimeSpan 根本不存在」是构建期撞出来的（第一版把它写进了臂表，编译失败）。
        // 这一句钉的是"别把它当可读的"：DbDataReader 的公开取数口里没有 TimeSpan 那一首。
        Assert.DoesNotContain(typeof(DbDataReader).GetMethods().Select(m => m.Name), n => n == "GetTimeSpan");
    }

    // ── ② 时机：每列每场次一次，不是每值一次 ──────────────────────────────────

    /// <summary>整个提升的立论就是这一句：<b>探测的次数只跟"列数 × reader 数"有关，跟行数无关</b>。
    /// 三行 × 八列如果按每值探测就是 24 次问驱动（v0.14 那条否证的代价来源），按场次探测只有 7 次
    /// （枚举那一列不问驱动，见 <see cref="Only_a_column_whose_driver_type_matches_the_declaration_gets_an_arm"/> 末尾）。
    /// 计数用的是替身自己记的账：<b>产品代码里没有计数器</b>，也没有为了这条用例开的观测口（§12.7 C）。
    ///
    /// <para>后半段（第二只 reader 又问一遍）钉的是<b>另一件事</b>：票按 <em>reader 引用</em> 判，
    /// 换 reader 必重探。少了这一半，"每场次一张票"会退化成"进程级共享一张票"，
    /// 而两只 reader 的列类型或偏移不同时，症状是读错列或抛。</para></summary>
    [Fact]
    public void The_probe_asks_the_driver_once_per_column_per_session_not_once_per_value()
    {
        var contract = Contract();

        // 七次而不是八次：枚举那一列在 Probe 的第一条短路里就出去了，根本没问驱动。
        int[] asks = [0, 1, 2, 3, 5, 6, 7];
        var rows = Materializer.EntityRows<Order>(Reflected().ReadDelegate(typeof(Order)), contract);

        using var first = OrderRows(3);
        var read = 0;

        while (first.Read())
        {
            Assert.Equal(1 + read, rows(first).Id);

            read++;
        }

        Assert.Equal(3, read);
        Assert.Equal(asks, first.FieldTypeAsks);

        using var second = OrderRows(1);

        Assert.True(second.Read());
        _ = rows(second);

        Assert.Equal(asks, second.FieldTypeAsks);
    }

    // ── ③ 票据的形状：列序与档位同源 ─────────────────────────────────────────

    /// <summary>票里的两截必须<em>同源</em>：档位是按 <c>Offset + i</c>（<see cref="ReadPlan.Offset"/>）去问驱动的。
    /// <c>startOffset</c> 那一档不是装饰 —— Include 的子节点列在展宽结果集中段（<c>IncludeNode.ColumnOffset</c>），
    /// 如果票把序号写死成 0..n-1 而探测按 0..n-1 问，子节点就会拿父表的列型判自己的直读档。</summary>
    [Fact]
    public void A_ticket_carries_an_offset_and_one_op_per_column_from_the_same_probe()
    {
        var contract = Contract();
        var expected = new[] { ReadOps.Int32, ReadOps.String, ReadOps.Int32, ReadOps.Decimal,
            ReadOps.Boxed, ReadOps.DateTime, ReadOps.DateTime, ReadOps.String };

        using var plain = OrderTable().CreateDataReader();

        Assert.True(plain.Read());

        var atZero = ReadOps.Build(plain, contract, 0);

        Assert.Equal(0, atZero.Offset);
        Assert.Equal(expected, atZero.Ops);

        var padded = new DataTable();

        for (var i = 0; i < 4; i++)
            padded.Columns.Add("pad" + i, typeof(string));

        foreach (DataColumn column in OrderTable().Columns)
            padded.Columns.Add(column.ColumnName, column.DataType);

        foreach (DataRow source in OrderTable().Rows)
            padded.Rows.Add([.. Enumerable.Repeat("pad", 4), .. source.ItemArray]);

        using var wide = padded.CreateDataReader();

        Assert.True(wide.Read());

        var atFour = ReadOps.Build(wide, contract, 4);

        Assert.Equal(4, atFour.Offset);
        Assert.Equal(expected, atFour.Ops);

        // 偏移那一档真的能读：第 4 列起才是实体，读出来的值必须与零偏移那张一模一样。
        var offset = Materializer.EntityRows<Order>(Reflected().ReadDelegate(typeof(Order)), contract, 4);

        Assert.Equal(1, offset(wide).Id);
        Assert.Equal(OrderState.Shipped, offset(wide).State);
    }

    // ── ④ 值：省装箱不改值，可空性也不改 ─────────────────────────────────────

    /// <summary>同一只 reader、同一张票喂给两条路（G1 生成的、G2 反射的），而票的档位只由
    /// <see cref="ReadOps.Build"/> 落一次。<b>这里给两次读用的是同一张票与同一张票的"全装箱"版本</b>：
    /// 前者走臂，后者整列退回门。四只对象必须逐格相同 —— 这才说明臂省的是"怎么取"，不是"取什么"。
    /// 绝对值也一起钉（<see cref="Order"/> 那一行的八个成员），否则四只一起错也相等。</summary>
    [Fact]
    public void Direct_arms_and_the_boxed_door_return_the_same_row_on_both_paths()
    {
        var contract = Contract();

        using var reader = OrderTable().CreateDataReader();

        Assert.True(reader.Read());

        var armed = ReadOps.Build(reader, contract, 0);
        var boxed = new ReadPlan(armed.Offset, new byte[contract.Columns.Length]);

        Assert.Equal(armed.Offset, boxed.Offset);
        Assert.All(boxed.Ops, op => Assert.Equal(ReadOps.Boxed, op));
        Assert.NotEqual(armed.Ops, boxed.Ops);

        var shape = new[]
        {
            "Id=1", "CustomerName=张三", "CustomerId=42", "Amount=1234.5600", "State=Shipped",
            "CreatedAt=2026-01-02T03:04:05.0000000", "PaidAt=", "Note=加急，别丢字",
        };

        foreach (var schemas in new[] { Reflected(), Generated() })
        {
            var read = schemas.ReadDelegate(typeof(Order));

            Assert.Equal(shape, Vals((Order) read(reader, armed)));
            Assert.Equal(shape, Vals((Order) read(reader, boxed)));

            // 空值那一列是 PaidAt（DateTime? 成员）：它由可空那一族臂或门给 null，两种票都得钉。
            // 非空值类型成员那一侧的 NULL 行为在 A_null_keeps_the_nullability_of_the_member 里改判了
            // （第七笔：臂不再问 IsDBNull ⇒ 抛），所以这一片替身把 Id 留成有值。
            Assert.Null(((Order) read(reader, armed)).PaidAt);
            Assert.Null(((Order) read(reader, boxed)).PaidAt);
        }

        Assert.Equal("QuickSqlGeneratedOrderSchema", Generated().Schema<Order>().GetType().Name);
    }

    /// <summary>这一条是<b>回归线</b>，而且它先红过：第一版的臂只有非空一族，
    /// 于是 <c>DateTime?</c> 成员配一个非空的 <c>datetime</c> 列、值为 NULL 时，
    /// 读出来的是 <c>0001-01-01</c> 而不是 <c>null</c>（非空臂交回底层型，NULL 那一格由门给 <c>default(DateTime)</c>）。
    /// 那一版在方言册里是全绿的：票、臂、装箱口都没有错，错的是"臂按底层型交回"这件事被当成可以直接交给可空成员。
    ///
    /// <para><b>第七笔把非空那一半边改了判</b>：臂不再问 <c>IsDBNull</c>（真驱动量到那一截要
    /// mysql 268.9 ns/行），所以「非空值类型成员 + 库里是 NULL」现在是<em>抛</em>而不是给 <c>0</c> ——
    /// 库里塞了模型说不可能有的东西，谎话不再被咽下来。装箱票那一侧由门接住，照旧给 <c>0</c>，
    /// 这一半没动。可空成员那一侧两头都钉：可空成员 + NULL ⇒ <c>null</c>（臂与门都得给），
    /// 因为可空那一族 11 首臂仍问 <c>IsDBNull</c>。四条组合（G1/G2 × 臂票/装箱票）都跑，
    /// 因为摊平可能只发生在一侧的模板上。</para></summary>
    [Fact]
    public void A_null_keeps_the_nullability_of_the_member()
    {
        var contract = Contract();

        // Id（非空 int 成员）这一行是 NULL，PaidAt（DateTime? 成员）这一行也是 NULL：
        // 两列的票上都有编号（列型与声明的底层型相等），所以臂都会被调到。
        using var raw = new RawReader(
            ("Id", typeof(int), new[] { 0 }, new[] { true }),
            ("CustomerName", typeof(string), new string?[] { null }, new[] { true }),
            ("CustomerId", typeof(int), new[] { 42 }, null),
            ("Amount", typeof(decimal), new[] { 1234.5600m }, null),
            ("State", typeof(int), new[] { (int) OrderState.Shipped }, null),
            ("CreatedAt", typeof(DateTime), new[] { Moment }, null),
            ("PaidAt", typeof(DateTime), new DateTime[1], new[] { true }),
            ("Note", typeof(string), new string?[] { "加急，别丢字" }, null));

        Assert.True(raw.Read());

        var plan = ReadOps.Build(raw, contract, 0);

        Assert.Equal(ReadOps.Int32, plan.Ops[0]);
        Assert.Equal(ReadOps.DateTime, plan.Ops[6]);

        var allBoxed = new ReadPlan(plan.Offset, new byte[contract.Columns.Length]);

        // 第二片替身：只有 PaidAt（DateTime? 成员）与 CustomerName（string 成员）是 NULL，
        // Id 有值 —— 这一片才看得见可空那一族的 NULL 行为，第一片在 Id 上就抛了。
        using var soft = new RawReader(
            ("Id", typeof(int), new[] { 7 }, null),
            ("CustomerName", typeof(string), new string?[] { null }, new[] { true }),
            ("CustomerId", typeof(int), new[] { 42 }, null),
            ("Amount", typeof(decimal), new[] { 1234.5600m }, null),
            ("State", typeof(int), new[] { (int) OrderState.Shipped }, null),
            ("CreatedAt", typeof(DateTime), new[] { Moment }, null),
            ("PaidAt", typeof(DateTime), new DateTime[1], new[] { true }),
            ("Note", typeof(string), new string?[] { "加急，别丢字" }, null));

        Assert.True(soft.Read());

        var softPlan = ReadOps.Build(soft, contract, 0);

        Assert.Equal(ReadOps.Int32, softPlan.Ops[0]);
        Assert.Equal(ReadOps.DateTime, softPlan.Ops[6]);

        var softBoxed = new ReadPlan(softPlan.Offset, new byte[contract.Columns.Length]);

        foreach (var schemas in new[] { Reflected(), Generated() })
        {
            var read = schemas.ReadDelegate(typeof(Order));

            // 第七笔改判的正身见证：非空 int 成员 + 库里 NULL + 臂票 ⇒ 抛（臂不再问 IsDBNull，
            // 替身与真驱动同形，原始访问器在空值上就是抛）。
            var thrown = Assert.Throws<InvalidCastException>(() => read(raw, plan));

            Assert.Contains("列 Id 的这一行是空值", thrown.Message, StringComparison.Ordinal);

            // 装箱票那一侧由门接住，非空成员照旧给声明类型的 default —— 这一半没跟着改。
            var boxed = (Order) read(raw, allBoxed);

            Assert.Equal(0, boxed.Id);
            Assert.Null(boxed.CustomerName);
            Assert.Equal(42, boxed.CustomerId);
            Assert.Equal(Moment, boxed.CreatedAt);
            Assert.Null(boxed.PaidAt);

            // 可空那一族两头都钉：NULL ⇒ null，不摊平成 0001-01-01。
            foreach (var ticket in new[] { softPlan, softBoxed })
            {
                var order = (Order) read(soft, ticket);

                Assert.Null(order.PaidAt);
                Assert.Null(order.CustomerName);
                Assert.Equal(7, order.Id);
            }
        }
    }

    /// <summary>驱动"撒谎"的一行：列类型与成员声明类型不同型（<c>long</c> 列 vs <c>int</c> 成员、
    /// <c>string</c> 列 vs <c>decimal</c> 成员、<c>short</c> 列 vs 枚举成员），票上那些列必须一律是
    /// <see cref="ReadOps.Boxed"/>，值由收容所换算。这一条与 <c>TypedReadTests.A_drifted_column_still_reaches_the_converter</c>
    /// 是同一件事的两头：那头测门接得住，这头测<b>臂没有把它们截走</b>。</summary>
    [Fact]
    public void A_drifted_column_is_left_to_the_door_with_the_values_the_converter_gives()
    {
        var table = new DataTable();

        table.Columns.Add("Id", typeof(long));
        table.Columns.Add("CustomerName", typeof(string));
        table.Columns.Add("CustomerId", typeof(string));
        table.Columns.Add("Amount", typeof(string));
        table.Columns.Add("State", typeof(short));
        table.Columns.Add("CreatedAt", typeof(DateTime));
        table.Columns.Add("PaidAt", typeof(DateTime));
        table.Columns.Add("Note", typeof(string));

        table.Rows.Add(1L, "张三", "42", "1234.56", (short) OrderState.Shipped, Moment, DBNull.Value, "加急，别丢字");

        var contract = Contract();

        using var reader = table.CreateDataReader();

        Assert.True(reader.Read());

        var plan = ReadOps.Build(reader, contract, 0);

        Assert.Equal(new[] { ReadOps.Boxed, ReadOps.String, ReadOps.Boxed, ReadOps.Boxed,
            ReadOps.Boxed, ReadOps.DateTime, ReadOps.DateTime, ReadOps.String }, plan.Ops);

        var order = (Order) Reflected().ReadDelegate(typeof(Order))(reader, plan);

        Assert.Equal(1, order.Id);
        Assert.Equal(42, order.CustomerId);
        Assert.Equal(1234.5600m, order.Amount);
        Assert.Equal(OrderState.Shipped, order.State);
        Assert.Null(order.PaidAt);

        // 换成 G1 的同一条票：两侧对"哪些列不能直读"的判断必须一模一样（§13.11 在这一格上的口径）。
        Assert.Equal(Vals(order), Vals((Order) Generated().ReadDelegate(typeof(Order))(reader, plan)));
    }

    // ── ⑤ 省下来的确实是装箱那一份 ───────────────────────────────────────────

    /// <summary>提升的另一半立论：<b>省的是每值那颗盒子</b>。判据是
    /// "同一张票、同样的列序，只有档位数组全 0"那一次与这一次之差 —— 全 0 那一版就是第五笔丙之前的读法。
    /// 跑在 <see cref="RawReader"/> 上（<see cref="DataTableReader"/> 两条路都装箱，量出来必然是 0，
    /// 那正是第一版在这里踩的坑）。八列里能省的格子：<c>Id</c>、<c>CustomerId</c>、<c>Amount</c>、
    /// <c>State</c>（成员是枚举，两侧都进门 ⇒ 不省）、<c>CreatedAt</c>、<c>PaidAt</c>（NULL ⇒ 不省）、
    /// 两个字符串列（引用不装箱 ⇒ 不省）。下界因此取"三颗整数盒 + 一颗 decimal 盒"这一档，实测数打进失败消息。</summary>
    [Fact]
    public void Both_paths_save_the_boxing_on_the_columns_that_have_arms()
    {
        var contract = Contract();

        using var reader = OrderRows(1);

        Assert.True(reader.Read());

        var armed = ReadOps.Build(reader, contract, 0);
        var boxed = new ReadPlan(armed.Offset, new byte[contract.Columns.Length]);

        var report = new List<string>();
        var least = double.MaxValue;

        foreach (var (name, schemas) in new[] { ("G2", Reflected()), ("G1", Generated()) })
        {
            var read = schemas.ReadDelegate(typeof(Order));

            var direct = PerCall(() => _ = read(reader, armed));
            var viaDoor = PerCall(() => _ = read(reader, boxed));

            least = Math.Min(least, viaDoor - direct);

            report.Add($"{name}：直读 {direct} B/行，装箱口 {viaDoor} B/行，省 {Math.Round(viaDoor - direct, 1)} B");
        }

        Assert.True(least >= 48,
            "直读臂没有省下装箱（Id/CustomerId/Amount/CreatedAt 至少各一颗盒子）：\n  " +
            string.Join("\n  ", report) + "\n  判据下界 48 B，实测两侧最小省量 " + Math.Round(least, 1) + " B。");
    }

    /// <summary>与 <c>TypedReadTests</c> 同一个量法：先预热到稳定分层，再按当前线程字节数取差。
    /// 预热里包含了"第一次调用要建票"那一截，所以票据本身的分配不进读数。</summary>
    static double PerCall(Action body)
    {
        for (var i = 0; i < 2_000; i++)
            body();

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 20_000; i++)
            body();

        return Math.Round((GC.GetAllocatedBytesForCurrentThread() - before) / 20_000.0, 1);
    }

    // ── ⑥ 架构：臂只在门之前，编号只有一处定义 ────────────────────────────────

    /// <summary>§12.7 C 第二条在提升之后仍然成立的那份形状：<b>每条臂的非直读分支就是那扇门</b>，
    /// 不是复制来的等价实现。所以"这一列怎么读"仍然只有一处可改，臂只多回答"它能不能少一次装箱"。
    /// 这条用例数的是源文件里的字样：每一首的下一行必须出现 <c>ValueConverter.Read&lt;</c>，
    /// 而且必须带 <c>op ==</c> 那个条件；空值那一半按族分：可空那一族与 <c>string</c> 臂仍带
    /// <c>!IsDBNull</c>，非空值类型那一族带上是红（第七笔的改判，理由见 <see cref="DirectRead"/> 的段注）。
    /// 可空那一族与非空那一族必须成对
    /// （唯一不该配对的是 <c>string</c>，它本来就没有摊平问题）。</summary>
    [Fact]
    public void Every_arm_ends_back_at_the_single_door_and_only_the_op_decides()
    {
        var source = File.ReadAllLines(Path.Combine(Root(), "src", "QuickSql.Core", "Materialization", "ReadOps.cs"));
        var arms = new List<(string Type, string Op, string Name)>();

        for (var i = 0; i < source.Length; i++)
        {
            var match = Regex.Match(source[i],
                @"public static (?<type>[\w\[\]\.?]+?) Read(?<kind>Nullable)?(?<op>\w+)\(DbDataReader reader, int ordinal, byte op\)");

            if (!match.Success) continue;

            var (type, kind, op) = (match.Groups["type"].Value, match.Groups["kind"].Value, match.Groups["op"].Value);
            var body = source[i + 1].TrimStart();

            arms.Add((type, op, "Read" + kind + op));

            Assert.Contains("op == ReadOps." + op, body);

            // 第七笔改判的形状：非空值类型那一族臂不再问 IsDBNull（一次取值只一趟驱动调用），
            // 而 string 臂与可空那一族 11 首必须问 —— NULL 在它们是成员本身要表达的合法值。
            // 该跳的那一侧反向钉：字样出现就是红，免得有人把守卫顺手加回去。
            if (kind.Length > 0 || type == "string")
                Assert.Contains("!reader.IsDBNull(ordinal)", body);
            else
                Assert.DoesNotContain("!reader.IsDBNull(ordinal)", body);

            Assert.Contains("ValueConverter.Read<" + type + ">", body);
            Assert.DoesNotContain("reader.GetValue(", body);
        }

        Assert.Equal(23, arms.Count);
        Assert.Equal(arms.Select(a => a.Name).Distinct().Count(), arms.Count);

        var plain = arms.Where(a => !a.Name.StartsWith("ReadNullable")).ToArray();
        var lifted = arms.Where(a => a.Name.StartsWith("ReadNullable")).ToArray();

        Assert.Equal(12, plain.Length);
        Assert.Equal(11, lifted.Length);
        Assert.All(lifted, l => Assert.Contains(plain, p => p.Op == l.Op && p.Type + "?" == l.Type));
        Assert.Equal(["string"], plain.Where(p => lifted.All(l => l.Op != p.Op)).Select(p => p.Type));

        var ops = source.Count(l => Regex.IsMatch(l.TrimStart(), @"^public const byte \w+ = \d+;"));

        Assert.Equal(13, ops);
        Assert.Contains(source, l => l.Contains("Boxed = 0"));

        // 编号的分配处（ByType）与臂的族必须同宽：一边加了型、另一边没加臂，
        // 症状是"探测落了编号而那一族的臂不存在"，而它既不会编译失败也不会读错值，只会静默不省。
        var keyed = source.Count(l => Regex.IsMatch(l.TrimStart(),
            @"\[typeof\(\w+\)\] = (Boolean|Byte|Int16|Int32|Int64|Single|Double|Decimal|Char|DateTime|String|Guid),"));

        Assert.Equal(plain.Length, keyed);
    }

    /// <summary>G1 与 G2 的队列靠<b>方法名</b>对齐（G2 从方法组转换拿句柄，G1 的模板按名字发射调用）。
    /// 这里核对的是生成器那张"CLR 全名 → 臂名后缀"表：每一条都必须能在 <c>DirectRead</c> 上找到
    /// 对应的那一首（可空值类型还要找到 <c>ReadNullable</c> 那一首），而臂的总数不能比表多 ——
    /// <b>多出来的一首就是没人调的死码，而少一首是静默不省</b>，两种都只会留在源码里烂掉（§12.7 C 的口径）。
    /// <see cref="Both_paths_save_the_boxing_on_the_columns_that_have_arms"/> 只覆盖 <see cref="Order"/> 用到的那几型，
    /// 补不上这一格。</summary>
    [Fact]
    public void The_generators_arm_table_and_the_runtime_arms_are_the_same_set()
    {
        var builder = File.ReadAllText(Path.Combine(Root(), "src", "QuickSql.Generators", "EntityFactsBuilder.cs"));

        var emitted = Regex.Matches(builder, @"""System\.(?<clr>[A-Z]\w*)"" => ""(?<suffix>[A-Z]\w*)""")
            .Select(m => (Clr: m.Groups["clr"].Value, Suffix: m.Groups["suffix"].Value))
            .ToArray();

        Assert.Equal(12, emitted.Length);

        var real = typeof(DirectRead)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m =>
            {
                var p = m.GetParameters();

                return p.Length == 3 && p[0].ParameterType == typeof(DbDataReader) && p[2].ParameterType == typeof(byte);
            })
            .Select(m => m.Name)
            .ToArray();

        Assert.Equal(23, real.Length);

        foreach (var (clr, suffix) in emitted)
        {
            Assert.Contains("Read" + suffix, real);

            // 值类型才有可空那一首；string 的运行期类型不随可空注解变。
            Assert.Equal(clr != "String", real.Contains("ReadNullable" + suffix));
        }

        // 缺席的那一族要真的缺席，而且"当初想写而写不了"的理由必须留在文件里：
        // GetTimeSpan 不存在是构建期撞出来的（第一版写了它，编译失败）。
        foreach (var absent in new[] { "TimeSpan", "DateOnly", "TimeOnly", "DateTimeOffset", "SByte" })
            Assert.DoesNotContain(absent, emitted.Select(e => e.Clr));

        Assert.Contains("GetTimeSpan", builder);
    }

    static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "QuickSql.slnx")))
                return dir.FullName;

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上找不到 QuickSql.slnx。");
    }

    static string[] Vals(Order order)
        =>
        [
            $"Id={order.Id}", $"CustomerName={order.CustomerName}", $"CustomerId={order.CustomerId}",
            $"Amount={order.Amount:0.0000}", $"State={order.State}", $"CreatedAt={order.CreatedAt:O}",
            $"PaidAt={order.PaidAt:O}", $"Note={order.Note}",
        ];

    // ── 替身行 ───────────────────────────────────────────────────────────────

    static readonly DateTime Moment = new(2026, 1, 2, 3, 4, 5);

    static readonly Guid Key = Guid.Parse("11112222-3333-4444-5555-666677778888");

    static DataTable Table((Type Type, object? Value)[] columns)
    {
        var table = new DataTable();

        for (var i = 0; i < columns.Length; i++)
            table.Columns.Add("c" + i, columns[i].Type);

        table.Rows.Add([.. columns.Select(c => c.Value)]);

        return table;
    }

    /// <summary>按 <see cref="Order"/> 的契约列序造一行（<see cref="DataTableReader"/> 用）。
    /// <b>列序就是契约列序</b>是实体投影的前提（<c>QueryCompiler.EntityColumns</c>），票的 0..n-1 全靠它；
    /// <c>State</c> 给的是底层 <c>int</c> 而不是枚举，因为驱动交回的就是那个整数。</summary>
    static DataTable OrderTable()
    {
        var table = new DataTable();

        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("CustomerName", typeof(string));
        table.Columns.Add("CustomerId", typeof(int));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("State", typeof(int));
        table.Columns.Add("CreatedAt", typeof(DateTime));
        table.Columns.Add("PaidAt", typeof(DateTime));
        table.Columns.Add("Note", typeof(string));

        table.Rows.Add(1, "张三", 42, 1234.5600m, (int) OrderState.Shipped, Moment, DBNull.Value, "加急，别丢字");

        return table;
    }

    /// <summary>同样那一行，但存储是类型化数组（<see cref="RawReader"/> 用）：
    /// 只有这片替身能让"原始访问器不装箱"这件事在离线册里被量出来。</summary>
    static RawReader OrderRows(int rows)
        => new(
            ("Id", typeof(int), Enumerable.Range(1, rows).ToArray(), null),
            ("CustomerName", typeof(string), Enumerable.Repeat((string?) "张三", rows).ToArray(), null),
            ("CustomerId", typeof(int), Enumerable.Repeat(42, rows).ToArray(), null),
            ("Amount", typeof(decimal), Enumerable.Repeat(1234.5600m, rows).ToArray(), null),
            ("State", typeof(int), Enumerable.Repeat((int) OrderState.Shipped, rows).ToArray(), null),
            ("CreatedAt", typeof(DateTime), Enumerable.Repeat(Moment, rows).ToArray(), null),
            ("PaidAt", typeof(DateTime), new DateTime[rows], Enumerable.Repeat(true, rows).ToArray()),
            ("Note", typeof(string), Enumerable.Repeat((string?) "加急，别丢字", rows).ToArray(), null));
}
