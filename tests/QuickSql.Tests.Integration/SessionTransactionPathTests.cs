using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.TestInfra;
using QuickSql.Transactions;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>形态 (4)：把事务<b>钉在 orm 实例上</b>（<c>BeginSessionTransaction</c>）。
///
/// <para><b>事故的形状</b>（本轮的证人，装配层报上来的）：ABP 的工作单元在自己的 async 帧里
/// <c>BeginAmbientTransaction()</c>，之后由<em>调用方那一帧</em>（仓储方法）发语句。环境槽位是
/// <see cref="System.Threading.AsyncLocal"/>，而它那一笔只由<em>同步被调方</em>回到调用方的帧
/// （<c>IQuickSqlTransaction</c> 抬头 v0.12 那条实测）——异步被调方哪怕全程只 await 已完成的 Task，
/// 写进去的值父帧也看不见。后果不是报错，是「一场工作单元一次提交」静默退化成每句自提：
/// 中途抛异常时前半段的写已经落库。</para>
///
/// <para><b>这一格补的是归属，不是新事务</b>：一条 <c>DbConnection</c> + 一个 <c>DbTransaction</c>、
/// 保存点嵌套、集群闸门全都同一条判据；只是「这句算不算进这场事务」的问法从
/// 「流里有没有」扩成「流里有没有，<em>或者这只实例钉着哪一场</em>」。所以本册审三件事：
/// ① 子帧钉的场，父帧的写要进得去；② 另一只 orm 不能被顺带拖进去（槽位挂实例不挂类型）；
/// ③ 收口之后钩子要摘干净，别把下一条语句挂进已结束的事务。</para>
///
/// <para><b>裁判一律问裸 ADO</b>，与 <see cref="RowSwapOrderingPathTests"/> 同一条纪律：
/// 「进没进事务」这句话只有第二条连接说得准——同一条连接上自己读自己写，无论进没进都看得见。
/// 而「第二条连接在事务进行中去读」这一问法要的是 MVCC：SQLite 没有（写事务占住整库，
/// 旁路连接只会等到 busy timeout），那两格在本家按引擎的界跳过，不在应用侧造替身。</para></summary>
public sealed class SessionTransactionPathTests
{
    /// <summary>靶实体：主键归客户端给（<c>Guid</c>），只留一块板号当清理范围。</summary>
    [Table("QuickSqlSessionTxPath")]
    public sealed class SessionRow
    {
        public Guid Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Board { get; set; }

        [QuickColumn(Length = 32)]
        public string? Label { get; set; }
    }

    const string TableName = "QuickSqlSessionTxPath";

    static string Quote(string identifier) => Database.Provider.CodeFirst.Identifiers.Quote(identifier);

    static string Table => Quote(TableName);

    static string BoardColumn => Quote(nameof(SessionRow.Board));

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static async Task<QuickSqlOrm> NewStoreAsync()
    {
        Database.Ready();
        Database.Execute($"DROP TABLE IF EXISTS {Table}");

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

        // 先问一次契约，同步那一趟才看得见这张表（与 RowSwapOrderingPathTests 同一颗顺序）
        orm.Schemas.Contract<SessionRow>();
        await orm.SyncStructureAsync(ct);

        return orm;
    }

    static string NewBoard() => "quicksql-session-tx-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>裸 ADO 那一条连接上的行数——裁判不碰被审的那只会话。</summary>
    static int StoredRows(string board) => Database.Rows(
        $"SELECT {Quote(nameof(SessionRow.Id))} AS I FROM {Table} WHERE {BoardColumn} = @board",
        ("@board", board)).Count;

    static void ClearBoard(string board) =>
        Database.Execute($"DELETE FROM {Table} WHERE {BoardColumn} = @board", ("@board", board));

    /// <summary>把「事务在子帧里钉上」这一形原样复刻出来：这个方法必须 await 一次，
    /// 否则它就成了同步被调方，环境槽位那一笔本来会回到调用方的帧，本案要审的那一格就没被审到。</summary>
    static async Task<IQuickSqlTransaction> PinFromChildFrameAsync(QuickSqlOrm orm)
    {
        await Task.Yield();

        return orm.BeginSessionTransaction();
    }

    /// <summary>同一条形状、但用的是只认执行流的那一颗口（形态 (3)）：本案的反面证人。</summary>
    static async Task<IQuickSqlTransaction> AmbientFromChildFrameAsync(QuickSqlOrm orm)
    {
        await Task.Yield();

        return orm.BeginAmbientTransaction();
    }

    [Fact]
    public async Task A_transaction_pinned_by_a_child_frame_still_holds_the_parents_write()
    {
        var orm = await NewStoreAsync();
        var board = NewBoard();

        // 写入发在父帧：环境槽位里没有它，但这一只 orm 钉着那场
        using (var transaction = await PinFromChildFrameAsync(orm))
        {
            await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = board, Label = "甲" })
                .ExecuteAffrowsAsync(ct);

            transaction.Rollback();
        }

        Assert.Equal(0, StoredRows(board));
    }

    [Fact]
    public async Task The_same_shape_with_only_an_ambient_slot_leaks_the_write_out_of_the_transaction()
    {
        // 这一格审的是「那句写落在事务外的另一条连接上还能自己提交」，同样要 MVCC：
        // SQLite 上写事务占住整库，那句写直接撞成 database is locked——形状对得上，读数拿不到
        Assert.SkipWhen(
            Database.Family == Database.Target.Sqlite,
            "SQLite 的写锁是整库的：事务在外那条连接发不进旁路写，本家量不到「泄漏成一次自提」这一读数");

        var orm = await NewStoreAsync();
        var board = NewBoard();

        using (var transaction = await AmbientFromChildFrameAsync(orm))
        {
            await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = board, Label = "乙" })
                .ExecuteAffrowsAsync(ct);

            transaction.Rollback();
        }

        // 这一颗就是装配层踩到的形状：槽位回不到父帧，那句写自己提交了，回滚挡不住它
        Assert.Equal(1, StoredRows(board));

        ClearBoard(board);
    }

    [Fact]
    public async Task A_pinned_transaction_holds_writes_and_reads_from_both_frames()
    {
        var orm = await NewStoreAsync();
        var board = NewBoard();

        using var transaction = await PinFromChildFrameAsync(orm);

        await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = board, Label = "丙" })
            .ExecuteAffrowsAsync(ct);

        // 同一只实例的读走的是被事务独占的那条连接，所以看得见自己的写
        var seen = await orm.Select<SessionRow>().Where(r => r.Board == board).ToListAsync(ct);

        Assert.Single(seen);

        transaction.Commit();

        // 收口之后，第二条连接读到的就是那一行（这一问各家都给，不需要快照读法）
        Assert.Equal(1, StoredRows(board));
    }

    /// <summary>「第二条连接在事务进行中都读不到」这一问法要的是快照读法：SQLite 没有 MVCC
    /// （写事务占住整库），SQL Server 默认 READ COMMITTED 不开 RCSI —— 两家的读数都是<em>锁等待</em>
    /// 而不是"看不见"，把等超时当成判据就是本层拒绝的那一种红（§13.4：连不上与量不到要能被看出来）。
    /// 所以这一颗钉在两家能答的上面，另两家带着理由跳。</summary>
    [Fact]
    public async Task A_second_connection_cannot_read_the_uncommitted_write_of_the_pinned_transaction()
    {
        Assert.SkipWhen(
            Database.Family is not (Database.Target.PostgreSql or Database.Target.MySql),
            "这一问要快照读法：SQLite 无 MVCC、SQL Server 默认 READ COMMITTED 不开 RCSI，两家的读数都是锁等待");

        var orm = await NewStoreAsync();
        var board = NewBoard();

        using var transaction = await PinFromChildFrameAsync(orm);

        await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = board, Label = "壬" })
            .ExecuteAffrowsAsync(ct);

        Assert.Equal(0, StoredRows(board));

        transaction.Commit();

        Assert.Equal(1, StoredRows(board));
    }

    [Fact]
    public void Pinning_a_second_transaction_on_the_same_orm_is_refused()
    {
        Database.Ready();

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
        });

        using var transaction = orm.BeginSessionTransaction();

        var refused = Record.Exception(() => orm.BeginSessionTransaction());

        Assert.NotNull(refused);
        Assert.Contains("已经在事务里", refused!.Message);
    }

    [Fact]
    public async Task Another_orm_on_the_same_flow_is_not_dragged_into_the_pinned_transaction()
    {
        // 这一格要的正是「一场事务占一条连接、旁路各写各的」这种并存，SQLite 给不了
        Assert.SkipWhen(
            Database.Family == Database.Target.Sqlite,
            "两只实例各自占一条写连接在 SQLite 上是整库锁，本家测不了「另一只 orm 不进这场事务」");

        var orm = await NewStoreAsync();
        var other = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
        });

        var board = NewBoard();
        var pinnedBoard = NewBoard();

        other.Schemas.Contract<SessionRow>();

        using (var transaction = await PinFromChildFrameAsync(orm))
        {
            await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = pinnedBoard, Label = "丁" })
                .ExecuteAffrowsAsync(ct);

            // 另一只实例（另一套会话）说的不是同一场事务：这句写在 orm1 那场事务还开着的时候
            // 就已经自己落库了（下面"回滚之后它的行还在"就是这一格的读数）
            await other.Insert(new SessionRow { Id = Guid.NewGuid(), Board = board, Label = "戊" })
                .ExecuteAffrowsAsync(ct);

            transaction.Rollback();
        }

        Assert.Equal(0, StoredRows(pinnedBoard));
        Assert.Equal(1, StoredRows(board));

        ClearBoard(board);
    }

    [Fact]
    public async Task Finishing_a_pinned_transaction_unhooks_it_so_the_next_write_lands_alone()
    {
        var orm = await NewStoreAsync();
        var first = NewBoard();
        var second = NewBoard();

        var transaction = await PinFromChildFrameAsync(orm);
        await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = first, Label = "己" })
            .ExecuteAffrowsAsync(ct);
        transaction.Commit();
        transaction.Dispose();

        // 摘钩之后再写：这一句归它自己（挂在已结束的事务上是要命的形状）
        await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = second, Label = "庚" })
            .ExecuteAffrowsAsync(ct);

        Assert.Equal(1, StoredRows(second));

        // 钩子空出来了，所以同一只实例还能再钉一场
        using var again = orm.BeginSessionTransaction();
        again.Rollback();
    }

    [Fact]
    public async Task Disposing_a_pinned_transaction_without_committing_rolls_it_back()
    {
        var orm = await NewStoreAsync();
        var board = NewBoard();

        using (var transaction = await PinFromChildFrameAsync(orm))
        {
            await orm.Insert(new SessionRow { Id = Guid.NewGuid(), Board = board, Label = "辛" })
                .ExecuteAffrowsAsync(ct);
        }

        Assert.Equal(0, StoredRows(board));
    }

    // ── 嵌套 DbContext 与会话工厂的主从（v0.39 第二笔） ─────────────────

    /// <summary>嵌套里层的 <c>Dispose</c> 不能顺带收走外层那一份。
    /// 工厂槽位是「先到当主人」：外层先 <c>Set&lt;T&gt;()</c> 占了槽，里层后来只是搭着用；
    /// 里层先走时只能收自己账本上的连接，槽与工厂还是外层的，外层之后的写照常落会话复用。
    /// 修之前这一格的形状是：谁后注册就顶掉谁、谁先 Dispose 就把整条流的工厂清空——
    /// 外层在里层走了之后每一句悄悄退回"每句取还池"，行为字段里字面看不出来。
    /// (读数钉的行为面：两只各自 SaveChanges，各自的事务各自提交，裁判用裸 ADO 计数。)</summary>
    [Fact]
    public async Task An_inner_session_leaving_first_leaves_the_outer_factory_alone()
    {
        var orm = await NewStoreAsync();
        var outerBoard = NewBoard();
        var innerBoard = NewBoard();

        await using (var outer = new DbContext(orm))
        {
            _ = outer.Set<SessionRow>();

            await using (var inner = new DbContext(orm))
            {
                _ = inner.Set<SessionRow>();

                inner.Add(new SessionRow { Id = Guid.NewGuid(), Board = innerBoard, Label = "里" });
                await inner.SaveChangesAsync(ct);
            }

            // 里层已经先走了：槽、工厂、外层自己账本上的连接原封不动，这一句照常走会话复用。
            outer.Add(new SessionRow { Id = Guid.NewGuid(), Board = outerBoard, Label = "外" });
            await outer.SaveChangesAsync(ct);
        }

        Assert.Equal(1, StoredRows(innerBoard));
        Assert.Equal(1, StoredRows(outerBoard));

        ClearBoard(innerBoard);
        ClearBoard(outerBoard);
    }

    /// <summary>反序的那一面：里层先到、当了主人——它走时连自己账本上的连接一起收走（那是它的账），
    /// 外层后建时恰好槽已空出来，会重新装上自己的工厂，不因里层的离开而瘫。
    /// 修之前的外层第一格 <c>Set&lt;T&gt;()</c> 在里层先行时会把外层的工厂顶掉，两条流各算各的账。</summary>
    [Fact]
    public async Task An_inner_session_that_arrived_first_leaves_the_slot_free_for_the_outer()
    {
        var orm = await NewStoreAsync();
        var outerBoard = NewBoard();
        var innerBoard = NewBoard();

        await using (var inner = new DbContext(orm))
        {
            _ = inner.Set<SessionRow>();

            inner.Add(new SessionRow { Id = Guid.NewGuid(), Board = innerBoard, Label = "先" });
            await inner.SaveChangesAsync(ct);
        }

        await using (var outer = new DbContext(orm))
        {
            _ = outer.Set<SessionRow>();

            outer.Add(new SessionRow { Id = Guid.NewGuid(), Board = outerBoard, Label = "后" });
            await outer.SaveChangesAsync(ct);
        }

        Assert.Equal(1, StoredRows(innerBoard));
        Assert.Equal(1, StoredRows(outerBoard));

        ClearBoard(innerBoard);
        ClearBoard(outerBoard);
    }
}
