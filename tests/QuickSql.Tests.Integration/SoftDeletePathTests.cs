using System.Globalization;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using QuickSql.Write;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§10.7 约束 3 的那入口子（v0.38 第十七笔）在<b>真库</b>上的读数：
/// <c>orm.Behaviors.SoftDelete</c> 在册时，删除落库的是"置位 + 记时间"的 <c>UPDATE</c>，行还在。
///
/// <para><b>这一份不是离线册的重复，而是离线册量不到的那三件事</b>。① 发出去的那一句<em>库收不收</em>：
/// 离线钉的是文本（<c>UPDATE [t0] SET [Deleted] = @p0, [DeletedAt] = @p1 FROM … WHERE …</c>），
/// 黄金样本比的是字符，而别名形态的写语句在解析器上过不过只有真跑一次知道 ——
/// 这一格与 <see cref="UpdateDeletePathTests"/> 为 UPDATE/DELETE 两条链立的理由逐字同源。
/// ② 两颗值<em>落进库的形状</em>：<c>true</c> 在 MySQL 那一族落成 <c>1</c>、在 T-SQL 落成
/// <c>BIT</c> 的 <c>True</c>，而那颗 <c>DateTime</c> 要过 1/300 秒取整（T-SQL）或 <c>datetime(3)</c>
/// 的截断（MySQL）；离线那一条只证明参数表里是 <c> Moment</c>，不证明库里那一格是它。
/// ③ <c>SaveChanges</c> 那一档<em>只有这里能判</em>：那条路上的语句是跟踪器自己拼的，
/// 链式的 <c>Explain()</c> 看不见它，所以"摘一行落库的是 UPDATE 而不是 DELETE"这一格在本册，
/// 不在离线那 19 条里。</para>
///
/// <para><b>每台 orm 都是自己那一台</b>（<see cref="soft"/> 走 <see cref="Database.OrmWith"/>）：
/// 行为册与过滤册都挂在实例上，把注册写到 <see cref="Database.Orm"/> 那颗共享的上面，
/// 同一场里另外六册的 DELETE 就会一起变成置位 —— 那是把别的用例的判据改掉，不是加一份见证。
/// 只有"没有注册时仍然删行"那一条负样本用共享的那一颗，因为它要的正是"什么都没注册"这个现场。</para>
///
/// <para><b>靶表是 <see cref="Ticket"/>，由用例自己按契约建</b>（<see cref="Database.EnsureTable(Type)"/>，
/// 幂等）：夹具那四张表里没有任何一张带 <c>Deleted</c>/<c>DeletedAt</c>，而这两列正是这一档要写的两列。
/// 每一批行带本次运行独占的标签前缀，写谓词只按标签或按自增键落点，所以别人（包括上一轮的自己）
/// 留下的行一条也不会被碰到；人工清理是一句 <c>LIKE 'quicksql-write/soft-%'</c>。</para>
///
/// <para><b>取数一律走裁判连接</b>（<see cref="Database.Rows"/>）：这里没有事务用例，
/// 所以"写进去的是哪一格"必须由另一条连接回答，而不经 ORM 的读路径 ——
/// 否则过滤器的存在会让"读不到"既可能是"行没了"也可能是"被筛掉了"，两件事共用一个读数。</para></summary>
public sealed class SoftDeletePathTests
{
    /// <summary>钉住的那颗时钟。<b>注册时递常量而不是用缺省的 <c>DateTime.Now</c></b>，
    /// 为的是下面那句"库里那一格读回来等于它"能逐字比（缺省那颗只能比"非空"，量不到毫秒位）。</summary>
    static readonly DateTime Moment = new(2026, 10, 7, 13, 14, 15, 160);

    // Batch 每用例一颗（static 会让同类各用例共一篇标签，隔离押在 xunit 方法序上，重编译可翻红；理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-write/soft-" + Guid.NewGuid().ToString("N")[..8];

    static CancellationToken ct => TestContext.Current.CancellationToken;

    string tagged(int n) => $"{Batch}/{n}";

    /// <summary>一台注册了软删的 orm；<paramref name="withFilter"/> 那一档另加一条"读侧排除已置位行"的过滤器。
    /// <b>两个开关分开是因为这两半各自不完整</b>：只注册行为，行留住了而读侧照样把它读出来；
    /// 只注册过滤器，读侧干净而 <c>Delete</c> 还是把行删掉。规格 §10.7 把两句并排写就是这个意思。</summary>
    QuickSqlOrm soft(bool withFilter = false)
    {
        var orm = Database.OrmWith();

        orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () => Moment);

        if (withFilter)
            orm.Filters.Add<Ticket>(t => !t.Deleted);

        return orm;
    }

    /// <summary>种一行并取回库里那一号：后面每一条谓词都落在"就是这一行"上。</summary>
    async Task<int> Seed(QuickSqlOrm orm, int n, int tenantId = 3)
        => (int) await orm.Insert(new Ticket { Title = tagged(n), TenantId = tenantId })
            .ExecuteIdentityAsync(ct);

    static string Millis(string column) => Database.MillisText(column);

    static List<Dictionary<string, string>> Tickets(string where, params (string Name, object? Value)[] args)
        => Database.Rows(
            "SELECT [Id], [Title], [TenantId], [Deleted], " + Millis("[DeletedAt]") + " AS DeletedAt " +
            $"FROM [dbo].[QuickSqlTicket] WHERE {where}",
            args);

    static Dictionary<string, string> Stored(int id) => Assert.Single(Tickets("[Id] = @id", ("@id", id)));

    static int Count(string where, params (string Name, object? Value)[] args)
        => Num(Assert.Single(Database.Rows($"SELECT COUNT(*) AS Cnt FROM [dbo].[QuickSqlTicket] WHERE {where}", args)), "Cnt");

    static int Num(Dictionary<string, string> stored, string column) => int.Parse(stored[column], CultureInfo.InvariantCulture);

    /// <summary>库里那一格是不是"已置位"。<b>写成两形折一次而不是按家族分岔</b>：
    /// 裁判那条路交回的是驱动的原始形状 —— T-SQL 的 <c>BIT</c> 是 <c>True</c>，MySQL 的
    /// <c>tinyint(1)</c> 是 <c>1</c>（<see cref="Database"/> 的 <c>Bind</c> 那一段末尾记的就是这笔账：
    /// 那是<em>回包形状</em>差而不是库里存的东西差）。两形都只可能来自"真"，所以这一折不会把 0 折成 1。</summary>
    static bool Flagged(Dictionary<string, string> stored) => stored["Deleted"] is "1" or "True";

    static bool Unflagged(Dictionary<string, string> stored) => stored["Deleted"] is "0" or "False";

    static DateTime? Stamped(Dictionary<string, string> stored)
        => stored["DeletedAt"] == "NULL"
            ? null
            : DateTime.Parse(stored["DeletedAt"], CultureInfo.InvariantCulture);

    // ── 链式出口：行留住而那一格写上了值 ─────────────────────────────

    [Fact]
    public async Task A_chain_delete_under_a_registered_behavior_writes_both_columns_and_keeps_the_row()
    {
        Database.Ready();
        Database.EnsureTable(typeof(Ticket));

        var orm = soft();
        var doomed = await Seed(orm, 1);
        var kept = await Seed(orm, 2);

        var affected = await orm.Delete<Ticket>().Where(t => t.Id == doomed).ExecuteAffrowsAsync(ct);

        // 命中数说的还是谓词圈到几行：一句 UPDATE 改了那一格，库里回 affected=1，
        // 而"1"在这条链上不再等于"少了一行"—— 这正是下面那条 repeat 用例要钉的东西。
        Assert.Equal(1, affected);

        var stored = Stored(doomed);

        Assert.True(Flagged(stored));
        Assert.Equal(Moment, Stamped(stored));
        Assert.Equal(tagged(1), stored["Title"]);

        // 没点到的那一行一个字节都没动：软删改的是命中集合内那几行的两列，不是全表置位。
        var survivor = Stored(kept);

        Assert.True(Unflagged(survivor));
        Assert.Null(Stamped(survivor));

        // 行还在 = 这张表当时的总数没有少。放在这里是因为"那一格是 1"与"行没被删"是两件事，
        // 前者绿而后者红的那一种实现（UPDATE 之后又发一句 DELETE）只在这一句上才露出来。
        // 参数名不叫 @like：虚谷的占位符是 ：形，而 ：后撞上保留字（like）整句就 E19260
        // 「unexpected LIKE」—— 冒号形式下那个名字会被它的词法器当关键字收【实测·2026-10-17 探针，
        // 同句换 ：lk 交回同一读数】。判据一字不变，只避开这一场撞名。
        Assert.Equal(2, Count("[Title] LIKE @lk", ("@lk", Batch + "/%")));
    }

    [Fact]
    public async Task The_same_predicate_a_second_time_hits_the_row_again_instead_of_reporting_an_empty_set()
    {
        Database.Ready();
        Database.EnsureTable(typeof(Ticket));

        var orm = soft();
        var id = await Seed(orm, 3);

        Assert.Equal(1, await orm.Delete<Ticket>().Where(t => t.Id == id).ExecuteAffrowsAsync(ct));

        // 物理删除那条路上"再跑一次"是 0（行不在了），软删这条路上它是 1 —— 因为 WHERE 里没有
        // "且尚未置位"那一支（本版刻意不加：加了就把这一格推到"被挡住的行算不算并发冲突"那句待裁的裁决上，
        // 见 §14 的 P1 余项与 DeletePlan.Reason 那一句）。幂等的清理写法在这条链上因此是"跑几遍都返回命中数"。
        Assert.Equal(1, await orm.Delete<Ticket>().Where(t => t.Id == id).ExecuteAffrowsAsync(ct));

        Assert.Equal(1, Count("[Id] = @id", ("@id", id)));

        // 命中 0 也照样是安静的 0，不是异常：库外的号在这一档与在物理那一档同一口径。
        Assert.Equal(0, await orm.Delete<Ticket>().Where(t => t.Id == int.MaxValue).ExecuteAffrowsAsync(ct));
    }

    [Fact]
    public async Task Without_a_registered_behavior_the_chain_still_removes_the_row()
    {
        Database.Ready();
        Database.EnsureTable(typeof(Ticket));

        // 负样本：这一条用的是共享那颗 orm（没有任何一册在上面注册过），所以它就是"没开软删的项目"。
        // 没有这一条，上面两条的绿就只证明了"什么都不删"，而本库默认那一支删的还是行本身。
        var id = await Seed(Database.Orm, 4);

        Assert.Equal(1, await Database.Orm.Delete<Ticket>().Where(t => t.Id == id).ExecuteAffrowsAsync(ct));

        Assert.Equal(0, Count("[Id] = @id", ("@id", id)));
    }

    // ── 两半各管一半：行为管写、过滤器管读 ───────────────────────────

    [Fact]
    public async Task A_behavior_without_a_filter_keeps_the_row_visible_on_the_read_side()
    {
        Database.Ready();
        Database.EnsureTable(typeof(Ticket));

        var orm = soft();
        var id = await Seed(orm, 5);

        Assert.Equal(1, await orm.Delete<Ticket>().Where(t => t.Id == id).ExecuteAffrowsAsync(ct));

        // 这一句读得到那一行，是"只注册行为"这一档的准确后果：库里行还在、那一格是 1，而没人替读侧筛掉它。
        Assert.NotNull(await orm.Select<Ticket>().Where(t => t.Id == id).FirstOrDefaultAsync(ct));

        // 注册了过滤器之后同一条问句读不到它：这一句才是"软删"完整的那一半，
        // 而它的判据不是"库里少了行"（上面已经钉过行在），是"读的那一边不把它算进来"。
        var filtered = soft(withFilter: true);

        Assert.Null(await filtered.Select<Ticket>().Where(t => t.Id == id).FirstOrDefaultAsync(ct));

        // 而过滤器不是删除：它自己不动任何一行，所以按 [Id] 数还是 1。
        Assert.Equal(1, Count("[Id] = @id", ("@id", id)));
    }

    // ── 订阅者往上加的那一格：软删自己只写两格，「谁删的」是调用方的领域事实 ──

    /// <summary><c>QuickSqlAop.SoftDeleteValues</c>：订阅者往上加的 SET 项真的进了那句 UPDATE，也真的落了库。
    ///
    /// <para><b>它为什么要有这一格</b>：软删自己只写两格（置位 + 时间），而「谁删的」那一格是调用方的领域事实 ——
    /// 库不知道当前用户是谁、也不该知道。这一颗让调用方把那一格往上加，库负责把它参数化、写出去，
    /// 并按与那两格同一把尺子验（自增列与并发列不收）。</para>
    ///
    /// <para><b>判据</b>：行还在（软删的本分）、置位那一格是 1、时间那一格是钉住的那颗、
    /// <b>而订阅者加的那一格是它给的那个值</b>。最后一句是这一条的全部意义 ——
    /// 那一格若没进 SET，前三句照样全绿。</para>
    ///
    /// <para><b>用 <c>Title</c> 当「谁删的」那一格</b>：这颗模型没有专用的删除人列，
    /// 而加一列会动 <c>Ticket</c> 的 DDL（那一片有黄金样本）。这一条判的是机制，
    /// 所以拿哪一列当载体不影响它判的那件事。</para></summary>
    [Fact]
    public async Task The_extra_column_a_subscriber_adds_lands_in_the_soft_delete_statement()
    {
        Database.Ready();
        Database.EnsureTable(typeof(Ticket));

        var aop = new QuickSqlAop();
        var orm = Database.OrmWith(aop);
        orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt, () => Moment);

        // 「谁删的」那一格要用契约上那一列 —— 订阅者拿到的是表名，列得自己从契约上取（列名 → 列契约）。
        var titleColumn = orm.Schemas.Contract(typeof(Ticket)).ByMember[nameof(Ticket.Title)];

        aop.SoftDeleteValues = e =>
            e.ExtraValues.Add(new KeyValuePair<ColumnContract, object?>(titleColumn, "谁删的"));

        var id = await Seed(orm, 6);

        // 走会话那一条路（跟踪器的摘行），那一档才有「一只一行」可给。
        var db = new DbContext(orm);
        var ticket = await db.Set<Ticket>().Where(t => t.Id == id).FirstOrDefaultAsync(ct);

        Assert.NotNull(ticket);

        db.Remove(ticket!);

        await db.SaveChangesAsync(ct);

        var stored = Stored(id);

        Assert.True(Flagged(stored));
        Assert.Equal(Moment, Stamped(stored));
        Assert.Equal("谁删的", stored["Title"]);
    }

    // ── 批量出口：一批键变一句置位的 UPDATE ──────────────────────────

    [Fact]
    public async Task A_bulk_delete_of_keys_flags_every_row_of_the_batch()
    {
        Database.Ready();
        Database.EnsureTable(typeof(Ticket));

        var orm = soft();

        var doomed = new List<int>();

        for (var i = 6; i <= 8; i++)
            doomed.Add(await Seed(orm, i));

        var keeper = await Seed(orm, 9);

        // 只带键的一批（这条链读的是主键那一列，其余列交什么都不看）；Merged 那一档是
        // 一句 WHERE 键 IN (…) 的<em>单条</em>语句，所以这一档在每一家都可达，不需要家族闸门。
        var report = await orm
            .DeleteRange([.. doomed.Select(id => new Ticket { Id = id })])
            .WithBulk(BulkWriteMethod.Merged)
            .ExecuteBulkAsync(ct);

        // 命中数还是"谓词圈到几行"：三颗键都圈到了，所以是 3，而库里那一行也没少。
        Assert.Equal(3, report.RowCount);

        Assert.All(doomed, id => Assert.True(Flagged(Stored(id))));

        // 同一批共用一颗时间：三行是"一次删除动作"，各读一次时钟就会把一件事记成三个时刻。
        // 离线册钉的是参数表里那一颗，这里钉的是库里那三格读回来逐字相等。
        var stamp = Stamped(Stored(doomed[0]));

        Assert.Equal(Moment, stamp);
        Assert.Equal(stamp, Stamped(Stored(doomed[1])));
        Assert.Equal(stamp, Stamped(Stored(doomed[2])));

        // 清单外那一颗键原样还在：IN 置的是那三颗，不是"这一族"。
        Assert.True(Unflagged(Stored(keeper)));
        Assert.Null(Stamped(Stored(keeper)));
    }

    // ── SaveChanges 出口：那条路上的语句只有这里看得见 ───────────────

    [Fact]
    public async Task SaveChanges_remove_sends_an_update_and_keeps_the_row_while_the_report_says_which_columns()
    {
        Database.Ready();
        Database.EnsureTable(typeof(Ticket));

        var orm = soft();
        var doomed = await Seed(orm, 10);
        var kept = await Seed(orm, 11);

        var db = new DbContext(orm);

        var loaded = await db.Set<Ticket>().OrderBy(t => t.Id).ToListAsync(ct);

        Assert.Contains(loaded, one => one.Id == doomed);

        db.Remove(loaded.Single(one => one.Id == doomed));

        var op = Assert.Single((await db.SaveChangesAsync(ct)).Operations, o => o.Operation == WriteOperation.Delete);

        // 报告里那句 SQL 是 UPDATE 而不是 DELETE —— 这一格是这条出口的<em>独有</em>判据：
        // 跟踪器发的那些语句带并发列的 WHERE 与（UPDATE 档的）OUTPUT，链式的 Explain() 看不见它们，
        // 所以离线那 19 条只能判到链式与批量两档的文本。
        Assert.StartsWith("UPDATE ", op.Sql, StringComparison.Ordinal);
        Assert.Equal(1, op.Rows);
        Assert.Equal(1, op.StatementCount);

        // 点名列那一格跟着语句走：这一条发的是 SET，所以它点名的就是被置位的那两列
        //（没有软删时这一格是空，那是 TrackingPathTests 钉着的另一条判据，两者不许互相串）。
        Assert.Equal(["Deleted", "DeletedAt"], op.Columns);

        Assert.True(Flagged(Stored(doomed)));
        Assert.Equal(Moment, Stamped(Stored(doomed)));
        Assert.True(Unflagged(Stored(kept)));

        // 会话那一侧仍然把它摘出去了：删除档的"摘出会话"看的是<em>状态</em>而不是库里那一行还在不在，
        // 所以软删不该让这一格变成"仍在会话里"。
        Assert.Null(db.Set<Ticket>().Local.Find(doomed));
    }
}
