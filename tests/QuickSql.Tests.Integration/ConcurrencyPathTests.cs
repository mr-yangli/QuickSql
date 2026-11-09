using System.Globalization;
using System.Text.RegularExpressions;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>乐观并发列（§8.2）在真库上的那一半。rowversion 支持已移除：令牌只有应用自维护两族——
/// 整型 <c>Version</c>（ORM 在 UPDATE 里发 <c>Version = Version + 1</c>）与 <c>AppSetsValue</c>
/// （应用在保存前给新值）。这一份钉三件事：<b>WHERE 用的是快照而不是当前值</b>、
/// <b>成功保存后实体与库里的 Version 同步</b>、以及 <b>0 行的两种成因分得开</b>
/// （“别人改过”与“这一行没了”在行数上一模一样，处置却相反）。
///
/// <para><b>冲突一律由会话之外的人制造</b>：<see cref="Database.Execute"/> 是裁判连接上的裸 ADO，
/// 不是 <c>DbContext</c>。整型令牌的外部改动必须同时 <c>Version = Version + 1</c>，
/// 否则快照与库里仍然同值，冲突不会成立——那正是这一格要判的东西。</para></summary>
public sealed class ConcurrencyPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static DbContext Session() => new(Orm);

    readonly string Batch = "quicksql-conc-" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 6, 1, 2, 3, 4, 567);

    static readonly DateTime Later = new(2026, 7, 2, 3, 4, 5, 678);

    string tagged(int n) => $"{Batch}/{n}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static InventoryItem row(string sku, int quantity) => new() { Sku = sku, Quantity = quantity };

    static List<SaveChangesOp> Ops(SaveChangesReport report, WriteOperation phase)
        => [.. report.Operations.Where(o => o.Operation == phase)];

    static async Task<int> Seed(string sku, int quantity = 1)
        => (int) await Orm.Insert(row(sku, quantity)).ExecuteIdentityAsync(ct);

    /// <summary>裁判读那一行。令牌列按库里的原文交回：整型 Version 是十进制串，
    /// CheckedAt 的文本形在各家不同，需要时走 ORM 读回而不是逐字比。</summary>
    static List<Dictionary<string, string>> Rows(string where, params (string Name, object? Value)[] args)
        => Database.Rows(
            "SELECT [Id], [Sku], [Quantity], [Version], [CheckedAt], [Exception] FROM [dbo].[QuickSqlInventoryItem] WHERE " + where,
            args);

    static Dictionary<string, string> Row(int id) => Assert.Single(Rows("[Id] = @id", ("@id", id)));

    static string Version(int id) => Row(id)["Version"];

    static async Task<InventoryItem> Read(DbContext db, int id)
        => Assert.Single(await db.Set<InventoryItem>().Where(i => i.Id == id).ToListAsync(ct));

    /// <summary>会话之外改一行：数量换了，整型令牌也 +1。少了后半句，快照仍然匹配，
    /// 这一句就不是“别人改过”的现场。</summary>
    static void BumpOutside(int id, int quantity)
        => Database.Execute(
            "UPDATE [dbo].[QuickSqlInventoryItem] SET [Quantity] = @q, [Version] = [Version] + 1 WHERE [Id] = @id",
            ("@q", quantity), ("@id", id));

    [Fact]
    public async Task A_new_row_starts_at_version_zero_and_the_next_save_uses_that_version()
    {
        Database.Ready();

        var db = Session();
        var item = row(tagged(1), 3);

        // 列名恰好叫 Exception：写进去再读回来，确认它没有被当成类型名。
        item.Exception = "E-123";

        db.Add(item);

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Insert));

        // 整型令牌不进 INSERT 列清单：初始值 0 由列默认值给。
        Assert.DoesNotContain(op.Columns, c => string.Equals(c, "Version", StringComparison.OrdinalIgnoreCase));
        Assert.True(item.Id > 0);
        Assert.Equal(0, item.Version);
        Assert.Null(item.CheckedAt);
        Assert.Equal("0", Version(item.Id));
        Assert.Equal("E-123", Row(item.Id)["Exception"]);
        Assert.Same(item, db.Set<InventoryItem>().Local.Find(item.Id));

        // 实体手里停在第 0 版；下一次保存的 WHERE 必须能命中这一行。
        item.Quantity = 4;

        var update = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        Assert.Equal(1, update.Rows);
        Assert.Equal("4", Row(item.Id)["Quantity"]);
        Assert.Equal("1", Version(item.Id));
        Assert.Equal(1, item.Version);
    }

    [Fact]
    public async Task Three_saves_in_a_row_all_succeed_because_each_one_syncs_the_new_version()
    {
        Database.Ready();

        var sku = tagged(2);
        var id = await Seed(sku);
        var db = Session();
        var item = await Read(db, id);

        for (var round = 1; round <= 3; round++)
        {
            // 会话手里那一份与库里那一份同值 —— 这是“上一轮同步回来了”的证据。
            Assert.Equal(round - 1, item.Version);
            Assert.Equal(item.Version.ToString(CultureInfo.InvariantCulture), Version(id));

            item.Quantity = round * 10;

            var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

            Assert.Equal(1, op.Rows);
            Assert.Equal(round, item.Version);
            Assert.Equal(round.ToString(CultureInfo.InvariantCulture), Version(id));
        }

        Assert.Equal("30", Row(id)["Quantity"]);

        // 结算之后快照重拍：同一句再保存一次不该有任何东西可写。
        Assert.Empty((await db.SaveChangesAsync(ct)).Operations);
    }

    [Fact]
    public async Task The_update_carries_the_snapshot_in_the_where_and_increments_the_token()
    {
        Database.Ready();

        var sku = tagged(3);
        var id = await Seed(sku, 5);
        var db = Session();
        var item = await Read(db, id);

        item.Quantity = 6;

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // 令牌出现两次以上：SET 里 Version = Version + 1，WHERE 里 Version = 快照。
        Assert.True(Regex.Matches(op.Sql, "Version", RegexOptions.IgnoreCase).Count >= 2, op.Sql);
        Assert.Equal("6", Row(id)["Quantity"]);
        Assert.Equal(1, item.Version);
        Assert.Equal("1", Version(id));
    }

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

        BumpOutside(clashed, 99);

        first.Quantity = 10;
        second.Quantity = 20;

        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync(ct));

        Assert.Equal("update", failure.Operation);
        Assert.Same(second, failure.Entity);
        Assert.False(failure.RowIsGone);

        Assert.Contains("Version: 会话以为是 0，库里是 1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CheckedAt: 会话以为是 NULL，库里是 NULL", failure.Message, StringComparison.Ordinal);
        Assert.Contains("它被别人改过", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Version", failure.Sql, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, Convert.ToInt32(failure.CurrentValues["Version"], CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(failure.DatabaseValues!["Version"], CultureInfo.InvariantCulture));
        Assert.Null(failure.DatabaseValues["CheckedAt"]);

        // 冲突把整批带走：那一句已经发出去的 UPDATE 也一起撤销。
        Assert.Equal("1", Row(mine)["Quantity"]);
        Assert.Equal("99", Row(clashed)["Quantity"]);

        // 而会话没有结算：两行都还欠着那一句。
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

        BumpOutside(moved, 55);

        db.Remove(first);
        db.Remove(second);

        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync(ct));

        Assert.Equal("delete", failure.Operation);
        Assert.Contains("Version", failure.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("55", Row(moved)["Quantity"]);

        db.ChangeTracker.Clear();

        // 另一只实例仍然欠着删除意图，重读之后照旧删得掉：冲突不是“这个会话作废”。
        var again = await Read(db, still);

        db.Remove(again);

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Delete));

        Assert.Empty(op.Columns);
        Assert.Equal(1, op.Rows);
        Assert.Empty(Rows("[Id] = @id", ("@id", still)));
    }

    [Fact]
    public async Task A_nullable_app_sets_token_uses_is_null_then_compares_by_value()
    {
        Database.Ready();

        var sku = tagged(7);
        var id = await Seed(sku);

        var db = Session();
        var item = await Read(db, id);

        Assert.Null(item.CheckedAt);

        item.Quantity = 7;

        var first = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // NULL 快照折成 IS NULL，否则 [CheckedAt] = @p 恒为 UNKNOWN，每一次保存都是假冲突。
        Assert.Contains("CheckedAt", first.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, item.Version);

        // 应用给值那一族：新值在实体上，随 SET 写出去。
        item.CheckedAt = Moment;

        Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        Assert.Equal(2, item.Version);

        var stored = await Read(Session(), id);

        Assert.Equal(Moment, stored.CheckedAt);

        // 会话之外只改 CheckedAt（Version 不动）：WHERE 里的 CheckedAt 快照对不上，照样冲突。
        Database.Execute("UPDATE [dbo].[QuickSqlInventoryItem] SET [CheckedAt] = @stamp WHERE [Id] = @id",
            ("@stamp", Later), ("@id", id));

        item.Quantity = 8;

        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync(ct));

        Assert.False(failure.RowIsGone);
        Assert.Contains("CheckedAt:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("它被别人改过", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.DatabaseValues);
        Assert.NotNull(failure.DatabaseValues["CheckedAt"]);
        Assert.Equal("7", Row(id)["Quantity"]);

        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Writing_a_value_into_an_integer_token_column_is_refused_before_anything_goes_out()
    {
        Database.Ready();

        var sku = tagged(8);
        var id = await Seed(sku, 12);

        var db = Session();
        var item = await Read(db, id);

        item.Version = 99;

        var failure = await Assert.ThrowsAsync<QuickSqlException>(async () => await db.SaveChangesAsync(ct));

        Assert.Contains("只比较、不书写", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Version", failure.Message, StringComparison.Ordinal);
        Assert.Equal("12", Row(id)["Quantity"]);

        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task The_report_carries_the_replay_of_a_hook_that_rewrote_a_SaveChanges_statement()
    {
        Database.Ready();

        var sku = tagged(9);
        var id = await Seed(sku);

        var armed = Database.OrmWith();
        var db = new DbContext(armed);

        armed.Aop.CudBuilding = e => e.Rewritten = e.Sql with { Text = e.Sql.Text + " AND 1 = 1" };

        var item = await Read(db, id);

        item.Quantity = 13;

        var report = await db.SaveChangesAsync(ct);

        var step = Assert.Single(report.HookReplay);

        Assert.Equal(0, step.Step);
        Assert.Equal("CudBuilding", step.HookName);
        Assert.DoesNotContain("AND 1 = 1", step.Before, StringComparison.Ordinal);
        Assert.EndsWith(" AND 1 = 1", step.After, StringComparison.Ordinal);

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

        var fresh = row(tagged(11), 1);

        db.Add(fresh);

        var both = await db.SaveChangesAsync(ct);

        Assert.Single(Ops(both, WriteOperation.Insert));
        Assert.Empty(both.HookReplay);
        Assert.True(fresh.Id > 0);
        Assert.Equal(0, fresh.Version);
        Assert.Equal("0", Version(fresh.Id));
    }
}