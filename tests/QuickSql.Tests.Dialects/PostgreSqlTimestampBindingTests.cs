using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>PG 系写侧的<b>时刻形状</b>：一颗 <see cref="DateTime"/> 交出去之前，<c>Kind</c> 要与列的形状对上。
/// 全程 <see cref="SqlServer.NoDriverFactory"/>，判的是交给驱动的那颗<em>值</em>，不是 SQL 文本
/// （文本一个字都不动，第四臂钉这一格）。
///
/// <para><b>事故的形状</b>（本轮的证人，真库 PostgreSQL）：ABP 的时钟交回 <c>Kind=Utc</c> 的钟面，
/// 而消费侧那一套现库的时间列全是 <c>timestamp without time zone</c>。Npgsql 6 起按 <c>Kind</c> 分型，
/// 于是绑定当场抛 <c>ArgumentException: Cannot write DateTime with Kind=UTC to PostgreSQL type
/// 'timestamp without time zone'</c> —— 症状是宿主启动就死、一次登录写一条安全日志就 500，
/// 而库里一个字节都没进去。旧底座（SqlSugar）发的是它自己那一份参数对象，这一格从来量不到。</para>
///
/// <para><b>为什么折的是说法而不是钟面</b>：裸 <c>timestamp</c> 那一列存的就是"一串钟面，无所谓时区"，
/// 把 <c>Utc</c> 的说法摘掉（<c>SpecifyKind(Unspecified)</c>）是逐字节同文的改写；换成 <c>Local</c>
/// 才是数据改写 —— 钟面会被挪掉本机那几个小时。带时区那一型反过来：那一列要的就是瞬时，
/// 所以没表过态的钟面按瞬时补 <c>Utc</c>、<c>Local</c> 折成 UTC 瞬间。
/// 读侧对应的两臂在 <c>TypedReadTests</c>。</para></summary>
public sealed class PostgreSqlTimestampBindingTests
{
    static QuickSqlOrm orm() => PostgreSql.Orm();

    static readonly DateTime Bare = new(2026, 10, 15, 3, 4, 5, 678, DateTimeKind.Unspecified);
    static readonly DateTime Utc = new(2026, 10, 15, 3, 4, 5, 678, DateTimeKind.Utc);
    static readonly DateTime Local = new(2026, 10, 15, 3, 4, 5, 678, DateTimeKind.Local);

    static MomentRow Row(DateTime bare, DateTime declaredOffset, DateTimeOffset? instant = null) => new()
    {
        Id = 1,
        Bare = bare,
        DeclaredOffset = declaredOffset,
        Instant = instant ?? DateTimeOffset.UnixEpoch,
    };

    /// <summary>取这一批实参里点名那一列的那颗值（参数按契约列序给出，<c>Hint</c> 说的是哪一列）。</summary>
    static object? BoundValue(MomentRow row, string member)
    {
        orm().Insert(row).ToSql(out var parameters);

        return parameters.Single(p => p.Hint?.MemberName == member).Value;
    }

    static DateTime Bound(MomentRow row, string member) => Assert.IsType<DateTime>(BoundValue(row, member));

    // ── 第一臂：裸列收 Utc ⇒ 摘说法，钟面逐字节不动 ────────────────

    [Fact]
    public void A_utc_moment_bound_to_a_bare_timestamp_column_keeps_the_clock_and_loses_the_kind()
    {
        var bound = Bound(Row(Utc, Utc), nameof(MomentRow.Bare));

        Assert.Equal(DateTimeKind.Unspecified, bound.Kind);
        Assert.Equal(Utc.Ticks, bound.Ticks);
    }

    [Fact]
    public void The_two_kinds_that_already_fit_a_bare_column_pass_through_untouched()
    {
        // 反向那一半：已经对上列形状的两种 Kind 不许被动过 —— 无条件 SpecifyKind 在这里红。
        var bare = Bound(Row(Bare, Utc), nameof(MomentRow.Bare));

        Assert.Equal(DateTimeKind.Unspecified, bare.Kind);
        Assert.Equal(Bare.Ticks, bare.Ticks);

        var local = Bound(Row(Local, Utc), nameof(MomentRow.Bare));

        Assert.Equal(DateTimeKind.Local, local.Kind);
        Assert.Equal(Local.Ticks, local.Ticks);
    }

    // ── 第二臂：列声明成带时区时，同一颗 CLR 型的答案换成瞬时 ──────

    [Fact]
    public void A_declared_timestamptz_column_takes_the_instant_rather_than_a_relabelled_clock()
    {
        // 同一颗 DateTime，落在形状不同的两列上给出两个答案 —— 这一条是"按列说话"的全部意思。
        // 裸那一列收摘了说法的钟面（第一臂），带时区这一列收的是 <c>DateTimeOffset</c>：
        // 驱动按 DbType 认型，而 <c>DbType.DateTime2</c> 就是裸那一型，钟面再怎么说也换不了它。
        var utc = Assert.IsType<DateTimeOffset>(BoundValue(Row(Bare, Utc), nameof(MomentRow.DeclaredOffset)));

        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(Utc.Ticks, utc.UtcTicks);

        // 没表过态的钟面进这一列：那一列只收瞬时，所以按"这一串就是 UTC"记一个零偏移的瞬时。
        var repaired = Assert.IsType<DateTimeOffset>(BoundValue(Row(Bare, Bare), nameof(MomentRow.DeclaredOffset)));

        Assert.Equal(TimeSpan.Zero, repaired.Offset);
        Assert.Equal(Bare.Ticks, repaired.UtcTicks);

        // 本机钟面 → 带着本机偏移的那一个瞬时：偏移留着，时刻才不动。
        var localized = Assert.IsType<DateTimeOffset>(BoundValue(Row(Bare, Local), nameof(MomentRow.DeclaredOffset)));

        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(Local), localized.Offset);
        Assert.Equal(Local.Ticks, localized.Ticks);
        Assert.Equal(Local.ToUniversalTime().Ticks, localized.UtcTicks);
    }

    // ── 第三臂：CLR 侧就带偏移的那一形不归这一管 ───────────────────

    [Fact]
    public void A_datetimeoffset_value_is_handed_over_as_it_is()
    {
        var instant = new DateTimeOffset(2026, 10, 15, 3, 4, 5, TimeSpan.Zero);

        Assert.Equal(instant, BoundValue(Row(Bare, Utc, instant), nameof(MomentRow.Instant)));
    }

    // ── 第四臂：值的改写不渗进文本 ─────────────────────────────────

    [Fact]
    public void The_sql_text_is_byte_identical_whether_the_clock_carried_a_kind_or_not()
    {
        // 列清单、占位符编号、参数个数一处不许动：这一臂是"绑定适配"与"重写语句"的分界。
        Assert.Equal(orm().Insert(Row(Utc, Utc)).ToSql(), orm().Insert(Row(Bare, Utc)).ToSql());

        // 取值段算一次（§12 那颗缓存），两臂各拿一份参数表：颗数同、序号同，只有那一格的写法不同。
        orm().Insert(Row(Utc, Utc)).ToSql(out var withKind);
        orm().Insert(Row(Bare, Utc)).ToSql(out var bareHanded);

        Assert.Equal(withKind.Count, bareHanded.Count);
        Assert.Equal(
            withKind.Select(p => p.Name),
            bareHanded.Select(p => p.Name));
    }

    // ── 第五臂：这一族以外不受影响 ─────────────────────────────────

    [Fact]
    public void The_other_families_hand_the_same_moment_over_unchanged()
    {
        // T-SQL 那一族的 datetime2 没有"按 Kind 分型"这一说，值原样递 —— 否则这一改动渗进了别家。
        SqlServer.Orm().Insert(new Order { Id = 1, CustomerId = 1, CreatedAt = Utc }).ToSql(out var parameters);

        var created = Assert.IsType<DateTime>(parameters.Single(p => p.Hint?.MemberName == nameof(Order.CreatedAt)).Value);

        Assert.Equal(DateTimeKind.Utc, created.Kind);
    }
}
