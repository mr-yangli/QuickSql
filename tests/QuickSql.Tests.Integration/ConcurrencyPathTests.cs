using System.Globalization;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>乐观并发列（§8.2，v0.18）在<b>真库</b>上跑的那一半。离线的 <c>ConcurrencyTokenTests</c>
/// 钉的是"这一列进不了 <c>SET</c>"，这一份钉的是它<em>说得上话</em>的那三件事：
/// <b><c>WHERE</c> 里那句并发谓词用的确实是快照而不是当前值</b>（写反了就是每一次都命中 0 行，
/// 或者每一次都不命中）、<b><c>OUTPUT</c> 确实把新版本读回了实体</b>（不读回就是下一次保存必假冲突，
/// 而那正是"看起来实现了"与"真能用"的分界），以及 <b>0 行的两种成因分得开</b>（"别人改过"与"这一行没了"
/// 在行数上一模一样，处置却相反）。
///
/// <para><b>冲突一律由会话之外的人制造</b>：<see cref="Database.Execute"/> 是裁判连接上的裸 ADO，
/// 不是 <c>DbContext</c>，也不是 <c>Orm.Update</c>。理由与 <see cref="Database.Rows"/> 同一条 ——
/// 用被测的那条链造现场，等于让读写两侧的同一个缺陷自证正确，而并发这一格错了就是静默覆盖别人的行。</para>
///
/// <para>写入只落本次运行的标签行（<see cref="Batch"/> 进 <c>Sku</c>，那一列是 <c>nvarchar(32)</c>，
/// 所以标签比 <see cref="TrackingPathTests"/> 那一份短），断言一律按主键或按标签。这张表没有种子、
/// 只由这一份写，所以留下的行（上一批运行的）既不会让用例变绿也不会让它变红 —— 不必像
/// <c>QuickSqlSessionToken</c> 那样自己清。</para></summary>
public sealed class ConcurrencyPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static DbContext Session() => new(Orm);

    // Batch 每用例一颗（static 会让同类各用例共一篇标签，隔离押在 xunit 方法序上，重编译可翻红；理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-conc-" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 6, 1, 2, 3, 4, 567);

    static readonly DateTime Later = new(2026, 7, 2, 3, 4, 5, 678);

    string tagged(int n) => $"{Batch}/{n}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static InventoryItem row(string sku, int quantity) => new() { Sku = sku, Quantity = quantity };

    static List<SaveChangesOp> Ops(SaveChangesReport report, WriteOperation phase)
        => [.. report.Operations.Where(o => o.Operation == phase)];

    /// <summary>种子走<b>不跟踪的那条链</b>：新增档自己也是被测对象，用它准备现场会让"插入没回填"这种缺陷
    /// 先把前置条件一起改掉（<see cref="TrackingPathTests"/> 里 <c>Seed</c> 的同一条理由）。
    ///
    /// <para><b>v0.32 第二格：这一颗在本文件是"全量收口"那一格</b> —— 十条用例今天<em>全部</em>红在
    /// <c>InsertEngine.ReadBackAsync</c>（本文件没有一条不需要库给键的证人），所以闸门写在种子里、
    /// 十条一起按名字跳掉，而不是十条各写一句。
    /// <b>【v0.34 第一笔改判：收口的名字换了】</b>单颗自增列的回填已改走语句后回读，旧闸门
    /// （<c>Database.RequireKeyBackfill</c>）整族退场；本文件十条今天红的只剩并发列那两颗缺口
    /// （回填集带"键＋版本"仍发 RETURNING 交空结果集；那一列连 SELECT 都抛 DataOutOfBindException），
    /// 闸门因此改挂 <see cref="Database.RequireConcurrencyColumn"/>，落点不动——还是写在种子里。
    /// 与之相对：<c>UpdateDeletePathTests</c> 与 <c>TrackingPathTests</c> 都还有<em>不</em>走键回填的绿用例，
    /// 那两处的闸门因此也写在种子上、但结案前逐条核过"没有一条绿用例被顺带跳掉"。</para></summary>
    static async Task<int> Seed(string sku, int quantity = 1)
    {
        Database.RequireConcurrencyColumn();

        return (int) await Orm.Insert(row(sku, quantity)).ExecuteIdentityAsync(ct);
    }

    /// <summary>裁判读那一行的原文。<b>二进制那一列要绕一道</b>：对 <c>rowversion</c>（<c>sys.types</c> 里它就叫
    /// <c>timestamp</c>）直接 <c>CONVERT(varchar, col, 2)</c> 时那个 style <em>被忽略</em> —— 2026-09-22 在 102 上实测
    /// 交回的是八个原始字节，而 936 代码页把其中的 <c>07 E6</c> 当成一个双字节字符<em>折成一格</em>，
    /// 于是拿到的是七个字符的乱码（§10.4 那一类"字节被吃掉但不报错"的现场）。
    /// 先转成 <c>varbinary</c> 才走 binary→char 那条认 style 的路，配上 <c>UPPER</c> 就能与
    /// <see cref="Hex"/> 逐字比。<c>CheckedAt</c> 走 <c>121</c> 是 datetime2 的正常档位。</summary>
    static List<Dictionary<string, string>> Rows(string where, params (string Name, object? Value)[] args)
        => Database.Rows(
            "SELECT [Id], [Sku], [Quantity], UPPER(CONVERT(varchar(16), CONVERT(varbinary(8), [RowVersion]), 2)) AS RowVersion, " +
            "CONVERT(varchar(23), [CheckedAt], 121) AS CheckedAt " +
            $"FROM [dbo].[QuickSqlInventoryItem] WHERE {where}",
            args);

    static Dictionary<string, string> Row(int id) => Assert.Single(Rows("[Id] = @id", ("@id", id)));

    static string Hex(byte[]? value) => value is null ? "NULL" : "0x" + Convert.ToHexString(value);

    /// <summary>库里那一行的版本号原文，形如 <c>0x00000000000007D9</c>，好与实体手里的那一份直接比。</summary>
    static string Version(int id) => "0x" + Row(id)["RowVersion"];

    static async Task<InventoryItem> Read(DbContext db, int id)
        => Assert.Single(await db.Set<InventoryItem>().Where(i => i.Id == id).ToListAsync(ct));

    // ── 回填：并发列的值归库给，而"归库给"必须落到实体上 ──────────────

    [Fact]
    public async Task A_new_row_gets_its_version_from_the_database_and_the_next_save_uses_that_version()
    {
        Database.Ready();

        // 本文件唯一一颗<em>不在 <c>Seed</c> 里</em>的闸门（这条自己 Add 一行新实体，不要种子），
        // 而它挂的东西从 v0.34 第一笔起只剩一颗——并发列的两格缺口：
        // ~~① 自增键回填不了 —— <c>item.Id > 0</c> 就是它，§14 那一行结案之后这一颗能绿~~
        //   （v0.34 第一笔结了那颗口子；但这条用例是会话路径，回填集"键＋版本"不止一颗自增列，
        //   改判对它不生效，红的还是 0/N —— 账在 <see cref="Database.RequireConcurrencyColumn"/> 第一条）；
        // ② <c>RowVersion</c> 那一格<em>连读都读不回来</em> —— 同一台库上 <c>SELECT "VERSION"</c>
        //   由驱动侧抛 <c>System.DataOutOfBindException：参数绑定超界</c>（v32-recon-mat-4.log 的 A-06c，
        //   四列同句 A-06 与单列 A-06c 同号，去掉那一列的 A-06b 则是绿的），
        //   所以 <c>item.RowVersion!.Length == 8</c> 那一颗就算把缝补上也仍然红。
        // 这一颗放在最前面而不是分两处：跳过只报一次，而两句都在同一颗用例里，账记在这条注释里更省事。
        // 其余九颗用例的闸门都在 <c>Seed</c>，因为它们都<em>先</em>要一行种子。
        Database.RequireConcurrencyColumn();

        var sku = tagged(1);
        var db = Session();
        var item = row(sku, 3);

        db.Add(item);

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Insert));

        // 插入档的列清单里没有那两列：它们不进 VALUES，而库里那一行仍然有值 —— 值是引擎给的。
        Assert.DoesNotContain("RowVersion", op.Columns);
        Assert.DoesNotContain("CheckedAt", op.Columns);
        Assert.True(item.Id > 0);
        Assert.Null(item.CheckedAt);
        Assert.Equal(8, item.RowVersion!.Length);

        var version = Hex(item.RowVersion);

        Assert.Equal(version, Version(item.Id));
        Assert.Same(item, db.Set<InventoryItem>().Local.Find(item.Id));

        // 这一句是回填的全部理由：实体手里停在没有版本的那一版，下一次保存的 WHERE 就命中 0 行。
        item.Quantity = 4;

        Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        Assert.Equal("4", Row(item.Id)["Quantity"]);
        Assert.NotEqual(version, Version(item.Id));
    }

    [Fact]
    public async Task Three_saves_in_a_row_all_succeed_because_each_one_reads_the_new_version_back()
    {
        Database.Ready();

        var sku = tagged(2);
        var id = await Seed(sku);
        var db = Session();
        var item = await Read(db, id);

        for (var round = 1; round <= 3; round++)
        {
            var before = Version(id);

            // 会话手里的那一份与库里的那一份逐字节相同 —— 这是"上一轮读回来了"的证据，而不是"某处有一份版本"。
            Assert.Equal(before, Hex(item.RowVersion));

            item.Quantity = round * 10;

            var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

            Assert.Equal(1, op.Rows);

            // 每一轮都比上一轮的新版本：只回填一次的话，第二轮就把自己造成的冲突抛出来了。
            Assert.NotEqual(before, Version(id));
            Assert.Equal(Version(id), Hex(item.RowVersion));
        }

        Assert.Equal("30", Row(id)["Quantity"]);

        // 结算之后连并发列一起重拍快照：同一句再保存一次不该有任何东西可写。
        Assert.Empty((await db.SaveChangesAsync(ct)).Operations);
    }

    [Fact]
    public async Task The_update_carries_the_snapshot_in_the_where_and_reads_both_tokens_back()
    {
        Database.Ready();

        var sku = tagged(3);
        var id = await Seed(sku, 5);
        var db = Session();
        var item = await Read(db, id);
        var snapshot = item.RowVersion;

        item.Quantity = 6;

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // 文本三处各有归属：<c>OUTPUT</c> 在 <c>SET</c> 之后、<c>FROM</c> 之前（T-SQL 只认这个位置，
        // 而 PG 的 <c>RETURNING</c> 在整句末尾 —— 顺序与理由写在 §10.1 的落地记录里），并发列的次序是契约列序，
        // 而 <c>CheckedAt</c> 只能是 <c>IS NULL</c>（<c>= @p</c> 在 NULL 上恒为 UNKNOWN，于是每一次保存都是假冲突）。
        Assert.Contains("SET [Quantity] = @p0 OUTPUT inserted.[RowVersion], inserted.[CheckedAt] FROM", op.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE [t0].[Id] = @p1 AND [t0].[RowVersion] = @p2 AND [t0].[CheckedAt] IS NULL", op.Sql, StringComparison.Ordinal);

        Assert.Equal("6", Row(id)["Quantity"]);
        Assert.NotSame(snapshot, item.RowVersion);
        Assert.Equal(Version(id), Hex(item.RowVersion));
    }

    // ── 冲突：0 行的两种成因 ──────────────────────────────────────────

    [Fact]
    public async Task A_row_somebody_else_changed_raises_with_both_sides_and_leaves_the_batch_unsettled()
    {
        Database.Ready();

        var sku = tagged(4);

        var mine = await Seed(sku + "/a");
        var clashed = await Seed(sku + "/b");

        var db = Session();
        var first = await Read(db, mine);
        var second = await Read(db, clashed);
        var stale = second.RowVersion;

        // 会话之外改那一行：这一句把 rowversion 顶上去，于是会话手里的快照成了旧版。
        Database.Execute("UPDATE [dbo].[QuickSqlInventoryItem] SET [Quantity] = @q WHERE [Id] = @id",
            ("@q", 99), ("@id", clashed));

        first.Quantity = 10;
        second.Quantity = 20;

        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync(ct));

        Assert.Equal("update", failure.Operation);
        Assert.Same(second, failure.Entity);
        Assert.False(failure.RowIsGone);

        // 逐列给两边：会话以为的那一版 vs 补查回来的现值。两半都对得上裁判，才算这条消息能用 ——
        // 而二进制那一档必须打成十六进制，否则这一句的两个值读起来一模一样。
        Assert.Contains($"RowVersion: 会话以为是 {Hex(stale)}，库里是 {Version(clashed)}", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CheckedAt: 会话以为是 NULL，库里是 NULL", failure.Message, StringComparison.Ordinal);
        Assert.Contains("它被别人改过", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[t0].[RowVersion] = @p2", failure.Sql, StringComparison.Ordinal);

        Assert.Equal(Hex(stale), Hex(Assert.IsType<byte[]>(failure.CurrentValues["RowVersion"])));
        Assert.Equal(Version(clashed), Hex(Assert.IsType<byte[]>(failure.DatabaseValues!["RowVersion"])));
        Assert.Null(failure.DatabaseValues["CheckedAt"]);

        // 冲突把整批带走：那一句已经发出去的 UPDATE 也一起撤销（三档共用一条事务，§8.2 第一条）。
        Assert.Equal("1", Row(mine)["Quantity"]);
        Assert.Equal("99", Row(clashed)["Quantity"]);

        // 而会话没有结算：两行都还欠着那一句 —— 这是"抛出时不顺手 Clear"的判据。
        Assert.True(db.ChangeTracker.HasChanges);
        Assert.Equal(EntityState.Modified, db.Entry(first).State);
        Assert.Equal(10, first.Quantity);

        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task A_row_that_vanished_is_reported_as_gone_rather_than_changed()
    {
        Database.Ready();

        var sku = tagged(5);
        var id = await Seed(sku);

        var db = Session();
        var item = await Read(db, id);

        Database.Execute("DELETE FROM [dbo].[QuickSqlInventoryItem] WHERE [Id] = @id", ("@id", id));

        item.Quantity = 7;

        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync(ct));

        // 同一句 0 行、同一把谓词，差别只在这一格：补查按主键也没查到，那就不是"别人改过"。
        Assert.True(failure.RowIsGone);
        Assert.Null(failure.DatabaseValues);
        Assert.Contains("按主键补查也没有这一行：它被删掉了", failure.Message, StringComparison.Ordinal);
        Assert.Contains("库里已经没有这一行", failure.Message, StringComparison.Ordinal);

        Assert.Empty(Rows("[Id] = @id", ("@id", id)));

        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task A_delete_refuses_a_row_that_moved_and_still_takes_the_one_that_did_not()
    {
        Database.Ready();

        var sku = tagged(6);

        var moved = await Seed(sku + "/moved");
        var still = await Seed(sku + "/still");

        var db = Session();
        var first = await Read(db, moved);
        var second = await Read(db, still);

        Database.Execute("UPDATE [dbo].[QuickSqlInventoryItem] SET [Quantity] = @q WHERE [Id] = @id",
            ("@q", 55), ("@id", moved));

        db.Remove(first);
        db.Remove(second);

        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync(ct));

        // 「删掉我读到的那一版」：那一版被人改过就不是这一行了，删下去会抹掉别人新写的内容。
        // 删除没有 SET，所以主键是 @p0、并发列从 @p1 起。
        Assert.Equal("delete", failure.Operation);
        Assert.Contains("[t0].[Id] = @p0 AND [t0].[RowVersion] = @p1 AND [t0].[CheckedAt] IS NULL", failure.Sql, StringComparison.Ordinal);
        // 冲突只否掉了我们这一句：裁判那条 UPDATE 早就提交了，不跟着我们一起回滚。
        Assert.Equal("55", Row(moved)["Quantity"]);

        db.ChangeTracker.Clear();

        // 另一只实例仍然欠着删除意图，重读之后照旧删得掉：冲突不是"这个会话作废"。
        var again = await Read(db, still);

        db.Remove(again);

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Delete));

        Assert.Empty(op.Columns);
        Assert.Equal(1, op.Rows);
        Assert.Contains("[t0].[CheckedAt] IS NULL", op.Sql, StringComparison.Ordinal);
        Assert.Empty(Rows("[Id] = @id", ("@id", still)));
    }

    [Fact]
    public async Task A_non_null_snapshot_is_compared_by_value_and_conflicts_when_that_value_moves()
    {
        Database.Ready();

        var sku = tagged(7);
        var id = await Seed(sku);

        // 由库外把那一列填上值，而且<b>会话是在那之后才第一次读它</b>：于是快照里是一个值，走 <c>= @p</c> 那一档。
        Database.Execute("UPDATE [dbo].[QuickSqlInventoryItem] SET [CheckedAt] = @when WHERE [Id] = @id",
            ("@when", Moment), ("@id", id));

        var db = Session();
        var item = await Read(db, id);

        Assert.Equal(Moment, item.CheckedAt);

        item.Quantity = 8;

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // 这一句同时是"IS NULL 那一档不是永真式"的另一半：填了值的行不会被折成 IS NULL，而是真的按值比。
        Assert.Contains("[t0].[CheckedAt] = @p3", op.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[CheckedAt] IS NULL", op.Sql, StringComparison.Ordinal);
        Assert.Equal("8", Row(id)["Quantity"]);

        var stale = item.RowVersion;

        Database.Execute("UPDATE [dbo].[QuickSqlInventoryItem] SET [CheckedAt] = @when WHERE [Id] = @id",
            ("@when", Later), ("@id", id));

        item.Quantity = 9;

        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync(ct));

        // 顶上那一格又动了：两列一起给出，谁变的看得见（<c>RowVersion</c> 也跟着每一次 UPDATE 走）。
        Assert.False(failure.RowIsGone);
        Assert.Contains($"CheckedAt: 会话以为是 {Moment.ToString(CultureInfo.InvariantCulture)}，库里是 {Later.ToString(CultureInfo.InvariantCulture)}",
            failure.Message, StringComparison.Ordinal);
        Assert.Contains($"RowVersion: 会话以为是 {Hex(stale)}，", failure.Message, StringComparison.Ordinal);
        Assert.Equal("8", Row(id)["Quantity"]);

        db.ChangeTracker.Clear();
    }

    // ── 「只比较、不书写」的那一档真的走得到（v0.18 改掉的那处静默丢弃） ──

    [Fact]
    public async Task Writing_a_value_into_a_token_column_is_refused_before_anything_goes_out()
    {
        Database.Ready();

        var sku = tagged(8);
        var id = await Seed(sku, 12);

        var db = Session();
        var item = await Read(db, id);

        item.RowVersion = [9, 9, 9, 9];

        var failure = await Assert.ThrowsAsync<QuickSqlException>(async () => await db.SaveChangesAsync(ct));

        // 判据是"有任何一列脏"而不是"有一列进得了 SET"（<c>WillWrite</c>），所以这一句拒得出来；
        // 按后者答就是静默丢弃：报告空、库里没动，而用户看到的是一句"保存成功了"。
        Assert.Contains("只比较、不书写", failure.Message, StringComparison.Ordinal);
        Assert.Contains("RowVersion", failure.Message, StringComparison.Ordinal);
        Assert.Equal("12", Row(id)["Quantity"]);

        db.ChangeTracker.Clear();
    }

    // ── SaveChanges 侧的钩子回放（§10.1 硬规则 1 的第四处出口） ────────

    [Fact]
    public async Task The_report_carries_the_replay_of_a_hook_that_rewrote_a_SaveChanges_statement()
    {
        Database.Ready();

        var sku = tagged(9);
        var id = await Seed(sku);

        // 独立一台 ORM：钩子是挂在 <c>Aop</c> 上的进程内状态，改共享那一份会串到同场其他用例。
        // <b>v0.32 第二格： provider 与连接串改由缝给</b>（这一颗从前是 <c>SqlServer.Orm(SqlServer.Driver())</c>，
        // 而本方法的 <c>Read</c>/<c>Row</c> 都走 <c>Database</c> —— 第五家那一跑里"写在一台库、判在另一台"）
        var armed = Database.OrmWith();
        var db = new DbContext(armed);

        // 钩子做一件真实形状的事：往 WHERE 尾巴上并一个恒真条件 —— 租户过滤器那一派就长这样。
        // 它必须<em>执行得过</em>才算这条链通，所以这一格只能在真库上判（离线那份只判文本）。
        armed.Aop.CudBuilding = e => e.Rewritten = e.Sql with { Text = e.Sql.Text + " AND 1 = 1" };

        var item = await Read(db, id);

        item.Quantity = 13;

        var report = await db.SaveChangesAsync(ct);

        var step = Assert.Single(report.HookReplay);

        Assert.Equal(0, step.Step);
        Assert.Equal("CudBuilding", step.HookName);
        Assert.DoesNotContain("AND 1 = 1", step.Before, StringComparison.Ordinal);
        Assert.EndsWith(" AND 1 = 1", step.After, StringComparison.Ordinal);

        // 报告里的语句文本是改写之后的那一份 —— 否则"回放"与"发了什么"会在同一份报告里说两件事。
        Assert.Equal(step.After, Assert.Single(Ops(report, WriteOperation.Update)).Sql);
        Assert.Equal("13", Row(id)["Quantity"]);
    }

    [Fact]
    public async Task A_SaveChanges_with_no_subscriber_reports_an_empty_replay()
    {
        Database.Ready();

        var sku = tagged(10);
        var id = await Seed(sku);

        var db = Session();
        var item = await Read(db, id);

        item.Quantity = 14;

        var update = await db.SaveChangesAsync(ct);

        Assert.Empty(update.HookReplay);
        Assert.Single(Ops(update, WriteOperation.Update));

        // 三档都在这一份里留一笔：插入档的回放来自引擎那份计划（§10.1），没订钩子时同样为空。
        var fresh = row(tagged(11), 1);

        db.Add(fresh);

        var both = await db.SaveChangesAsync(ct);

        Assert.Single(Ops(both, WriteOperation.Insert));
        Assert.Empty(both.HookReplay);
        Assert.True(fresh.Id > 0);
        Assert.Equal("0x" + Row(fresh.Id)["RowVersion"], Hex(fresh.RowVersion));
    }
}
