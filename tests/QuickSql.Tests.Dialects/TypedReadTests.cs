using System.Data;
using QuickSql.Materialization;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§12.7 C 第二条的<b>现状</b>：物化路径唯一的取数口是 <see cref="ValueConverter.Read{T}"/>，
/// 它把驱动交回的 object 一律收容进 <see cref="ValueConverter.To{T}"/>（那一扇门是 §9.3 要求存在的
/// 驱动漂移收容所，禁不得绕）。
///
/// <para>这一批全部不连库（<see cref="DataTableReader"/>），钉的是"这一口改不得"的三件事：
/// ① 值必须对（含 NULL → 声明类型默认值，§10.1 那句 ruling）；② 漂移必须仍然被接住，
/// 快慢不能由"看着像"决定；③ 调用点必须只有这一道门（<see cref="Every_materialization_call_site_reads_through_the_single_door"/>）。
/// 至于"省不省装箱"——那一句在 v0.14 是被实测**否掉**的：每值一次的类型探测在真驱动上值 42 ns，
/// 换回来的 24 B 买不起（数字在 §12.7 C 末尾）。<b>v0.38 第五笔丙按 §16.21 给的条件把这一格落了下来</b>：
/// 探测从每值挪到<b>每场次</b>（一张 <c>ReadPlan</c> 票），每值只剩 <c>IsDBNull</c> + 原始访问器。
/// 而落点不在下面这批用例里，也不在这扇门里——门本身一字未动（<see cref="The_read_door_is_byte_for_byte_the_converter"/>
/// 钉的还是那一句），臂与判定在 <c>ReadOps</c> 与 <c>DirectRead</c>，由同册的 <c>ZeroBoxingTests</c> 判。
/// 这一批因此只多了一件事要管：直读臂不许自己实现读法，也不许在源文件里直接取数（见配额）。</para></summary>
public sealed class TypedReadTests
{
    /// <summary>一行的 reader：列类型与值成对给出，读的时候按序号取。</summary>
    sealed class Row : IDisposable
    {
        readonly DataTableReader _reader;

        public Row(params (Type Type, object? Value)[] columns)
        {
            _reader = Table(columns).CreateDataReader();

            Assert.True(_reader.Read());
        }

        static DataTable Table((Type Type, object? Value)[] columns)
        {
            var table = new DataTable();

            for (var i = 0; i < columns.Length; i++)
                table.Columns.Add("c" + i, columns[i].Type);

            table.Rows.Add([.. columns.Select(c => c.Value)]);

            return table;
        }

        public DataTableReader Reader => _reader;

        public T Read<T>(int ordinal) => ValueConverter.Read<T>(_reader, ordinal);

        public void Dispose() => _reader.Dispose();
    }

    [Fact]
    public void An_exact_typed_column_comes_back_unchanged()
    {
        // 绝对值断言，不是"两侧相等"：这道门如果哪天开始交回默认值，这里必须响。
        var moment = new DateTime(2026, 3, 4, 5, 6, 7);
        var key = Guid.Parse("11112222-3333-4444-5555-666677778888");

        using var row = new Row((typeof(int), 7), (typeof(long), 4_000_000_000L), (typeof(decimal), 1.25m),
            (typeof(DateTime), moment), (typeof(Guid), key), (typeof(bool), true), (typeof(string), "abc"));

        Assert.Equal(7, row.Read<int>(0));
        Assert.Equal(4_000_000_000L, row.Read<long>(1));
        Assert.Equal(1.25m, row.Read<decimal>(2));
        Assert.Equal(moment, row.Read<DateTime>(3));
        Assert.Equal(key, row.Read<Guid>(4));
        Assert.True(row.Read<bool>(5));
        Assert.Equal("abc", row.Read<string>(6));
    }

    [Fact]
    public void A_null_lands_as_the_declared_default_for_nullable_and_non_nullable_members_alike()
    {
        // 成员是 int（非空）时 NULL 读成 0，成员是 int? 时读成 null——两句都测是因为它们走的是
        // To<T> 的两条不同分支：只断一句的话，把另一句那条分支删掉也照样绿。
        using var row = new Row((typeof(int), DBNull.Value), (typeof(int), DBNull.Value));

        Assert.Equal(0, row.Read<int>(0));
        Assert.Null(row.Read<int?>(1));
    }

    [Fact]
    public void A_drifted_column_still_reaches_the_converter()
    {
        // 三形都必须被接住：列给 byte[16] 而成员是 Guid（§9.3 的混合字节序）、列给 int 而成员是 long、
        // 列给 string 而成员是 int。共同点是"列类型与 typeof(T) 不等"，所以任何按类型强转的门都不许把它们挡掉——
        // 这正是 §16.21 那条提升后的门必须满足的条件：它只对**相等**的列生效，不等的一律回到这里。
        var raw = new byte[16];

        raw[0] = 0x88;
        raw[1] = 0x77;
        raw[2] = 0x66;
        raw[3] = 0x55;
        raw[4] = 0x44;
        raw[5] = 0x33;
        raw[6] = 0x22;
        raw[7] = 0x11;

        using var row = new Row((typeof(byte[]), raw), (typeof(int), 7), (typeof(string), "41"));

        // 混合字节序（§9.3 的 Guid_Raw16 读法）：Read<Guid> 必须仍然交回收容所算出的那一个值，而不是
        // InvalidCastException 或 Guid.Empty。字面值是**实测抄回来的**（第一次手推成 55667788-3344-1122，
        // 跑出来不等），两句一起断是为了既有绝对值、也有"确实经过收容所"的见证。
        Assert.Equal(Guid.Parse("88776655-4433-2211-0000-000000000000"), row.Read<Guid>(0));
        Assert.Equal(ValueConverter.FromMixedEndian16(raw), row.Read<Guid>(0));
        Assert.Equal(7L, row.Read<long>(1));
        Assert.Equal(41, row.Read<int>(2));
    }

    [Fact]
    public void An_enum_column_comes_back_as_the_declared_enum()
    {
        // 枚举列驱动交回的是底层整数，所以它是"列类型 ≠ typeof(T)"最常见的形态。
        // §16.21 那道提升后的门按 GetFieldType == typeof(T) 判定，天然把它留在收容所里——
        // 这一条钉的就是它必须一直留在这里（值对，且不抛）。
        using var row = new Row((typeof(int), 2));

        Assert.Equal(State.Paid, row.Read<State>(0));
    }

    enum State
    {
        Unpaid,
        Shipped,
        Paid,
    }

    // ── 带偏移的时刻 ↔ 裸时刻 那两折（真 PG 的 timestamptz 交回 Kind=Utc 的 DateTime，
    //    而 ABP 的 LockoutEnd 声明的是 DateTimeOffset?；从前这一格落到最后一句
    //    Convert.ChangeType 上，以 Invalid cast 收尾，宿主启动即 FTL）

    /// <summary>Utc 那颗裸时刻折成偏移为零的 DateTimeOffset，瞬时一字不动。可空那一档是现场在用的形状
    /// （<c>DateTimeOffset?</c>），而折型走的是同一扇门——臂只认 <c>underlying</c>。</summary>
    [Fact]
    public void A_utc_moment_folds_into_a_zero_offset_instant()
    {
        var utc = new DateTime(2026, 10, 15, 3, 4, 5, DateTimeKind.Utc);

        var folded = ValueConverter.To<DateTimeOffset?>(utc);

        Assert.NotNull(folded);
        Assert.Equal(TimeSpan.Zero, folded!.Value.Offset);
        Assert.Equal(utc, folded.Value.UtcDateTime);
    }

    /// <summary><c>timestamp without time zone</c> 那一格交回 Kind=Unspecified，而 BCL 对这一档写的
    /// 是本机偏移（<c>new DateTimeOffset(dt)</c> 那一条规则）。这一臂钉的是"不猜成 UTC"：猜成 UTC
    /// 会把一颗本机上写入的锁定时限推后或提前几个小时，而那句猜测在任何文档里都没有依据。</summary>
    [Fact]
    public void An_unspecified_moment_takes_the_local_offset_the_bcl_promises()
    {
        var bare = DateTime.SpecifyKind(new DateTime(2026, 10, 15, 3, 4, 5), DateTimeKind.Unspecified);

        var folded = ValueConverter.To<DateTimeOffset>(bare);

        Assert.Equal(new DateTimeOffset(bare, TimeZoneInfo.Local.GetUtcOffset(bare)), folded);
    }

    /// <summary>反方向：一列 <c>DateTime</c> 属性收到驱动交回的 <c>DateTimeOffset</c>（Oracle 那一家
    /// 有 TIMESTAMP WITH TIME ZONE，虚谷与金仓那一族也可能回这一型）时折成 <b>UTC 裸时刻</b>——
    /// 库里那一格存的是瞬时，而这套东西写侧一律按 UTC 落，折回本地会把同一个瞬时读成两个。</summary>
    [Fact]
    public void An_offset_value_folds_back_into_the_utc_instant()
    {
        var source = new DateTimeOffset(2026, 10, 15, 11, 4, 5, TimeSpan.FromHours(8));

        var folded = ValueConverter.To<DateTime>(source);

        Assert.Equal(DateTimeKind.Utc, folded.Kind);
        Assert.Equal(source.UtcDateTime, folded);
    }

    [Fact]
    public void The_read_door_is_byte_for_byte_the_converter()
    {
        // 这一条不测行为，测"这一口没有被偷偷换成别的东西"：撤掉强类型门之后，
        // Read<T> 的全部 contents 就是 To<T>(reader.GetValue(o))，所以两者必须逐格同价——
        // 包括分配。谁把门加回来（§16.21 那种提升形态是允许的，但要先改这批用例的说法），
        // 红的是这一条。
        using var row = new Row((typeof(int), 7), (typeof(int), DBNull.Value), (typeof(string), "41"));

        double Measure(Action body)
        {
            for (var i = 0; i < 1_000; i++)
                body();

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 20_000; i++)
                body();

            return Math.Round((GC.GetAllocatedBytesForCurrentThread() - before) / 20_000.0, 1);
        }

        Assert.Equal(7, row.Read<int>(0));
        Assert.Equal(7, ValueConverter.To<int>(row.Reader.GetValue(0)));

        var boxed = Measure(() => _ = ValueConverter.To<int>(row.Reader.GetValue(0)));
        var door = Measure(() => _ = ValueConverter.Read<int>(row.Reader, 0));

        Assert.True(boxed > 0, $"这条用例的判据已经失效：DataTableReader 现在不装箱了（实测 {boxed} B/次），" +
            "改判据而不是删用例。");
        Assert.Equal(boxed, door);

        // 字符串那一格要单独看一眼：两边都是 0 B，所以"相等"这件事本身不说明门是薄的。
        Assert.Equal(0, Measure(() => _ = ValueConverter.Read<string>(row.Reader, 2)));
        Assert.Equal(0, Measure(() => _ = ValueConverter.To<string>(row.Reader.GetValue(2))));
    }

    [Fact]
    public void Every_materialization_call_site_reads_through_the_single_door()
    {
        // §12.7 C 要的"用 grep 式架构测试卡住"：物化的调用点（G1 生成器模板、G2 读取委托、DTO 装配，
        // 以及 v0.38 第五笔丙加在门前的那一层臂）都不许再直接 reader.GetValue —— 否则"某一列的读法只有一处"
        // 这条会是一次顺手改回来的静默失效。直读臂的 else 分支必须仍然进收容所，所以 ReadOps.cs 的配额也是 0：
        // 它自己取数就得用 GetXxx 原始访问器，用 GetValue 就等于把门复制了一份到门外。
        // 全仓库唯一允许的直接取数是 ValueConverter.Read 自己那一句，所以 Materializer.cs 的配额是 1，
        // 而且还要钉住那一句确实是进收容所的（不然配额会被挪去掩护一个新调用点）。
        var root = Root();
        var quotas = new[]
        {
            (Path.Combine("src", "QuickSql.Core", "Materialization", "Materializer.cs"), 1),
            (Path.Combine("src", "QuickSql.Core", "Materialization", "ReadOps.cs"), 0),
            (Path.Combine("src", "QuickSql.Core", "Schema", "ReflectionEntitySchema.cs"), 0),
            (Path.Combine("src", "QuickSql.Generators", "EntityFactsWriter.cs"), 0),
        };
        var offenders = new List<string>();

        foreach (var (file, quota) in quotas)
        {
            var hits = new List<string>();
            var lines = File.ReadLines(Path.Combine(root, file)).ToList();

            for (var i = 0; i < lines.Count; i++)
            {
                var text = lines[i].TrimStart();

                if (text.StartsWith("//") || text.StartsWith('*') || !text.Contains("reader.GetValue(")) continue;

                hits.Add($"{file}:{i + 1} → {text}");
            }

            if (hits.Count != quota)
                offenders.AddRange(hits.Append($"{file}：直接取数 {hits.Count} 处，配额 {quota} 处"));
        }

        var door = Path.Combine(root, quotas[0].Item1);
        var doorLine = File.ReadAllLines(door).Single(l => l.TrimStart().StartsWith("=> To<T>(reader.GetValue("));

        Assert.True(offenders.Count == 0, "物化调用点又出现直接取数（应一律走 ValueConverter.Read）：\n  " + string.Join("\n  ", offenders) +
            "\n门本身是这一句：" + doorLine.Trim());
    }

    static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "QuickSql.slnx")))
                return dir.FullName;

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上找不到 QuickSql.slnx。");
    }
}
