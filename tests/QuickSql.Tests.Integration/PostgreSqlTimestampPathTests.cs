using QuickSql.Metadata;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>PG 写侧那颗"时刻形状"的活库半（离线五臂在 <c>PostgreSqlTimestampBindingTests</c>）。
///
/// <para><b>事故的形状</b>（本轮的证人）：消费侧一次登录 500 —— <c>IdentitySecurityLog.CreationTime</c>
/// 是 ABP 时钟交回的 <c>Kind=Utc</c> 钟面，而现库那一族列是 <c>timestamp without time zone</c>，
/// Npgsql 在绑定当场就拒：<c>Cannot write DateTime with Kind=UTC to PostgreSQL type
/// 'timestamp without time zone'</c>。离线那册量的是"交出去的那颗值长什么样"，
/// 而本案要的是<b>这一行真的进了库、库里那一格读回来还是同一串钟面</b>。</para>
///
/// <para><b>裁判一律裸 ADO</b>，与 <see cref="WritePathTests"/> 同一条纪律：写的一侧是被审的 ORM，
/// 读回库里那一格不能再经它。<see cref="Database.Rows"/> 那一句问的是库里的文本形，
/// 而 ORM 那一句只比 Ticks（<c>Kind</c> 由各家驱动自己说，本案判的不是那一格）。</para>
///
/// <para><b>本册只跑 PG 那一场，另几家按名跳过</b>：这一格的存在理由是 Npgsql 6 起
/// <em>按 <c>Kind</em> 分型</c>，而那一族列的存在理由是消费侧那套现库全是裸 <c>timestamp</c>。
/// T-SQL/SQLite 上 <c>Kind=Utc</c> 本来就直接写进裸时刻列，本案那一红在两家根本没有对象 ——
/// 按 §13.4 的分工，这不是"裁判写错了家族"，是"这一族没有这一问"。</para></summary>
public sealed class PostgreSqlTimestampPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static string Quote(string identifier) => Database.Provider.CodeFirst.Identifiers.Quote(identifier);

    static void SkipUnlessPostgreSql()
        => Assert.SkipWhen(Database.Family != Database.Target.PostgreSql,
            "本案判的是 Npgsql 按 DateTime.Kind 分型那一格（6.0 起的驱动行为），另几家没有这一问：Utc 钟面在 T-SQL/SQLite 上直接写进裸时刻列。");

    /// <summary>ABP 那颗时钟的形状：一个 UTC 瞬间，<c>Kind=Utc</c> —— 正是被驱动拒掉的那一形。</summary>
    static DateTime UtcMoment() => new(2026, 10, 15, 3, 4, 5, 678, DateTimeKind.Utc);

    [Fact]
    public async Task A_utc_clock_face_written_to_a_bare_timestamp_column_comes_back_as_the_same_clock()
    {
        SkipUnlessPostgreSql();

        Database.Ready();

        Database.EnsureTable(typeof(MomentRow));

        var moment = UtcMoment();

        // 键由库给（这一台是默认装配档：整型主键 ⇒ 自增），本案要判的是时刻那一格，不是键那一格。
        var key = await Orm.Insert(new MomentRow
        {
            Bare = moment,
            DeclaredOffset = moment,
            Instant = new DateTimeOffset(moment),
        }).ExecuteIdentityAsync(ct);

        // 裁判一：库里那一格读回来就是这串钟面 —— 没有本机偏移那几个小时混进来。
        var stored = Assert.Single(Database.Rows(
            $"SELECT {Quote(nameof(MomentRow.Bare))} AS clock FROM {Quote("QuickSqlMomentRow")} WHERE {Quote(nameof(MomentRow.Id))} = @key",
            ("@key", key)));

        Assert.Equal("2026-10-15 03:04:05", stored["clock"][..19]);

        // 裁判二：ORM 读回来仍是同一串钟面（Ticks 逐位相同）。
        var readBack = Assert.Single(await Orm.Select<MomentRow>().Where(r => r.Id == key).ToListAsync(ct));

        Assert.Equal(moment.Ticks, readBack.Bare.Ticks);

        await Orm.Delete<MomentRow>().Where(r => r.Id == key).ExecuteAffrowsAsync(ct);
    }

    [Fact]
    public async Task The_column_declared_with_a_time_zone_keeps_the_instant_across_the_round_trip()
    {
        SkipUnlessPostgreSql();

        Database.Ready();

        Database.EnsureTable(typeof(MomentRow));

        var moment = UtcMoment();

        var key = await Orm.Insert(new MomentRow
        {
            Bare = moment,
            DeclaredOffset = moment,
            Instant = new DateTimeOffset(moment),
        }).ExecuteIdentityAsync(ct);

        var readBack = Assert.Single(await Orm.Select<MomentRow>().Where(r => r.Id == key).ToListAsync(ct));

        // 带时区那一列存的是瞬时：写进去与读回来必须是同一个 UTC 瞬间。
        // 钟面随会话时区摆是这一型的本分，所以这里比 ToUniversalTime() 而不是比 Ticks。
        Assert.Equal(moment.Ticks, readBack.DeclaredOffset.ToUniversalTime().Ticks);

        await Orm.Delete<MomentRow>().Where(r => r.Id == key).ExecuteAffrowsAsync(ct);
    }
}
