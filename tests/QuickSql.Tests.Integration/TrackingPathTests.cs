using System.Globalization;
using QuickSql.Aop;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>会话 + 跟踪 + <c>SaveChanges</c> 在<b>真库</b>上跑一遍（§8.2 的三档写入、§13.2 第 2 项 ①③、§13.1 的写侧纪律）。
///
/// 离线的 <c>TrackingTests</c> 钉的是状态机（内存里的判断），这一份钉的是那三档<em>真发出去</em>的语句与
/// 物化期的身份映射 —— 后者在离线一条也判不了，因为它要的是"库把同一行递两次"这种现场。
/// 全份最值钱的三条：<b>回填键之后才登记身份</b>（登记不上就是同一行插两遍，这个判据只能真跑）、
/// <b>只写脏列</b>（未点名的列必须原样，而判据在裁判连接上读，不在会话里读）、
/// 以及<b>三档共用一条事务</b>（外层回滚要把插、改、删一起撤掉，连那个已经回填回来的主键）。
///
/// <b>写只写本次运行标签行</b>（与 <see cref="UpdateDeletePathTests"/> 同一条纪律）：种子的 <c>Note</c> 与
/// 改后的 <c>Note</c> 都带 <c>Batch</c>，删除只按本用例自己读到的主键发。裁判一律是
/// <see cref="Database.Rows"/>（裸 ADO、另一条连接），所以"写进去的是那一格"与"读回来的是那一格"
/// 不互相包庇；而事务块内复看必须走 ORM 自己的读出口 —— 裁判连接看不见未提交的东西，还会去敲事务那把 X 锁。</summary>
public sealed class TrackingPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static DbContext Session() => new(Orm);

    // Batch 每用例一颗（static 会让同类各用例共一篇标签，隔离押在 xunit 方法序上，重编译可翻红；理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-write/track-" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 6, 1, 2, 3, 4, 567);

    string tagged(int n) => $"{Batch}/{n}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static Order row(string name, decimal amount, string note, int customerId = 4242, OrderState state = OrderState.Placed) => new()
    {
        CustomerName = name,
        CustomerId = customerId,
        Amount = amount,
        State = state,
        CreatedAt = Moment,
        Note = note,
    };

    /// <summary>种子行走<b>不跟踪的那条链</b>：会话的用例不该依赖会话自己的写口准备现场，
    /// 否则"插入档"的缺陷会先把前置条件一起改掉。
    ///
    /// <para><b>v0.32 第二格：这一颗顺手成了本文件"键回填"那一道闸门的落点</b>。它要的就是
    /// <c>ExecuteIdentityAsync</c> —— 库把键发回来才有值可返，所以在第五家它<em>必然</em>红在
    /// <c>InsertEngine.ReadBackAsync</c>（那 14 颗红里凡是走这一句的都在此收口）。
    /// <b>【v0.34 第一笔改判：这一格退场】</b>单颗自增列的回填改走语句后回读，本文件 13 颗红里走这一句的
    /// 消费者随整族闸门一起解绑（闸门与账见 <see cref="Database"/> 的 <c>RequireConcurrencyColumn</c> 注释）。
    /// 当时"不放到类级别"的理由仍然成立：本文件另有<em>不</em>需要库给键的证人用例
    /// （<c>An_insert_of_a_client_generated_key_carries_the_key_in_the_column_list</c>，客户端造键）。
    /// 而 v0.32 那一句"它在第五家是<em>绿的</em> —— 不能因为'这一档都回填不了'被一起跳掉"记的是当时；
    /// 今天这一家回填得了，两条用例各跑各的。</para></summary>
    static async Task<int> Seed(Order order)
    {
        return (int) await Orm.Insert(order).ExecuteIdentityAsync(ct);
    }

    /// <summary>与 <c>WritePathTests</c>/<c>UpdateDeletePathTests</c> 同一颗转发（分岔与理由写在
    /// <see cref="Database.MillisText"/>）。<b>v0.34 第一笔补上这一处</b>：本文件的用例从前全数挂在
    /// 整族键回填闸门后面，这两颗 <c>CONVERT(varchar(23), …, 121)</c> 从没在虚谷上被执行过 ——
    /// 闸门退场后第一批 8 颗红里有 6 颗红在这一句（E19260 unexpected VARCHAR，v34-int-xugu-1/2.log），
    /// 而它们是"裁判 SQL 写给 SqlServer"那一族的又一个消费者。</summary>
    static string Millis(string column) => Database.MillisText(column);

    static List<Dictionary<string, string>> Orders(string where, params (string Name, object? Value)[] args)
        => Database.Rows(
            "SELECT [Id], [CustomerName], [CustomerId], [Amount], [State], [Note], " +
            $"{Millis("[CreatedAt]")} AS CreatedAt, {Millis("[PaidAt]")} AS PaidAt " +
            $"FROM [dbo].[QuickSqlOrder] WHERE {where}",
            args);

    static Dictionary<string, string> Row(int id) => Assert.Single(Orders("[Id] = @id", ("@id", id)));

    /// <summary>并发列那张表的那一格 —— 只给<see cref="A_tracked_instance_with_a_concurrency_token_is_never_merged"/>
    /// 用，所以不复用 <see cref="Orders"/>（它钉的是 <c>QuickSqlOrder</c> 的列清单）。理由同 <c>Database.Rows</c>：
    /// 判"发出去几句"要用会话之外的读口。</summary>
    static Dictionary<string, string> Stored(int id) => Assert.Single(Database.Rows(
        "SELECT [Quantity] FROM [dbo].[QuickSqlInventoryItem] WHERE [Id] = @id", ("@id", id)));

    static int Count(string where, params (string Name, object? Value)[] args)
        => Num(Assert.Single(Database.Rows($"SELECT COUNT(*) AS Cnt FROM [dbo].[QuickSqlOrder] WHERE {where}", args)), "Cnt");

    static int Num(Dictionary<string, string> stored, string column) => int.Parse(stored[column], CultureInfo.InvariantCulture);

    static decimal Money(Dictionary<string, string> stored, string column) => decimal.Parse(stored[column], CultureInfo.InvariantCulture);

    static DateTime When(Dictionary<string, string> stored, string column) => DateTime.Parse(stored[column], CultureInfo.InvariantCulture);

    static List<SaveChangesOp> Ops(SaveChangesReport report, WriteOperation phase)
        => [.. report.Operations.Where(o => o.Operation == phase)];

    /// <summary>按标签读回本次运行那几行，顺序按主键（= 插入序），所以 <c>rows[0]</c> 就是先灌的那一条。</summary>
    static async Task<List<Order>> ReadTagged(DbContext db, string note)
        => await db.Set<Order>().Where(o => o.Note == note).OrderBy(o => o.Id).ToListAsync(ct);

    // ── Insert 档：回填键与身份登记 ───────────────────────────────────

    /// <summary><c>QuickSqlAop.EntityWritten</c>（§10.1 观察类）：这一轮写出去的实体逐只点名，操作种类分三档。
    ///
    /// <para><b>它为什么是逐只而不是逐句</b>：一只实体在一轮里只被写一次，所以订阅者收到的事件与
    /// 「这一轮改了几行」一一对应 —— 调用方据此发领域事件、发实体变更事件，不必自己去数。</para>
    ///
    /// <para><b>它在语句建出来之前发</b>：订阅者改得动实体，而那一格改出来的值会被紧接着建出来的语句带出去
    /// （取值段读的是实体）。这一格是「改完就写出去」的那一半，与逐格的 <c>AuditValue</c> 分工。</para></summary>
    [Fact]
    public async Task The_written_hook_names_every_row_and_its_operation()
    {
        Database.Ready();

        var seen = new List<(string Kind, string? Note)>();

        var aop = new QuickSqlAop
        {
            EntityWritten = e => seen.Add((e.OperationKind, ((Order)e.Entity).Note)),
        };

        var db = new DbContext(Database.OrmWith(aop));
        var note = tagged(1);
        var order = row("甲公司", 12.3456m, note);

        db.Add(order);
        await db.SaveChangesAsync(ct);

        db.Add(row("乙公司", 1m, tagged(2)));
        await db.SaveChangesAsync(ct);

        var changed = (await db.Set<Order>().Where(o => o.Note == note).ToListAsync(ct)).Single();
        changed.Note = tagged(3);
        await db.SaveChangesAsync(ct);

        // 删之前要把明细捞进会话：Order 声明了到 OrderItem 的级联删除，而会话看不见子行时
        // 那一档按裁决①当场拒（"可达才删"）——那一条与本用例判的那件事无关，先照它的规矩走。
        // 顺序要紧：那一档是在 Remove 里面判的，先捞后删。
        db.Set<Order>().Load(o => o.Items!);

        db.Remove(changed);

        await db.SaveChangesAsync(ct);
        // 三档各一次，逐只点名；顺序是「先插的先被点到」。
        Assert.Equal(
            [("insert", tagged(1)), ("insert", tagged(2)), ("update", tagged(3)), ("delete", tagged(3))],
            seen);
    }

    /// <summary>订阅者在那一格里改得动实体，而改出来的值会被紧接着建出来的语句带出去。</summary>
    [Fact]
    public async Task A_subscriber_can_change_the_row_before_the_statement_is_built()
    {
        Database.Ready();

        // 改出来的那一格要带本次运行的标签：共享库上一轮留下的行还在，
        // 而这一列若是常量，那一轮的行会被这一轮一起读回来（断言就分不出是谁写的）。
        var rewritten = tagged(2);

        var aop = new QuickSqlAop
        {
            EntityWritten = e =>
            {
                if (e.Entity is Order order && e.OperationKind == "insert")
                    order.Note = rewritten;
            },
        };

        var db = new DbContext(Database.OrmWith(aop));

        db.Add(row("甲公司", 12.3456m, tagged(1)));

        await db.SaveChangesAsync(ct);

        // 那一格改出来的值真的随语句出去了（裁判连接读回，不是会话里读）。
        Assert.Equal(rewritten, Assert.Single(Orders("[Note] = @n", ("@n", rewritten)))["Note"]);
    }

    [Fact]
    public async Task SaveChanges_inserts_a_row_and_the_key_the_database_gives_back_is_what_gets_registered()
    {
        Database.Ready();

        var db = Session();
        var note = tagged(1);
        var order = row("甲公司", 12.3456m, note);

        db.Add(order);

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Insert));

        Assert.Equal("QuickSqlOrder", op.Table);
        Assert.Equal(1, op.Rows);
        Assert.Equal(1, op.StatementCount);

        // 自增列不在列清单里（那是 IDENTITY_INSERT 那条路），而主键回填之后必须 > 0 —— 两者是同一件事的两半。
        Assert.DoesNotContain("Id", op.Columns);
        Assert.True(order.Id > 0, $"回填后主键还是 {order.Id}，那一行没被认成库里已有的。");

        Assert.Equal(1, Count("[Note] = @note", ("@note", note)));
        Assert.Equal("甲公司", Row(order.Id)["CustomerName"]);

        // 键是这一句的全部理由：登记不上，下一次保存就会把同一行再插一遍。
        Assert.Same(order, db.Set<Order>().Local.Find(order.Id));
        Assert.Equal(EntityState.Unmodified, db.Entry(order).State);

        var second = await db.SaveChangesAsync(ct);

        Assert.Empty(second.Operations);
        Assert.Equal(1, Count("[Note] = @note", ("@note", note)));
    }

    [Fact]
    public async Task An_insert_of_a_client_generated_key_carries_the_key_in_the_column_list()
    {
        Database.Ready();

        var db = Session();
        var id = Guid.NewGuid();
        var value = tagged(2);

        db.Add(new SessionToken { Id = id, Value = value, ExpiresAt = Moment });

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Insert));

        // 没有自增列 ⇒ 主键必须进列清单（否则库里那一行的键是驱动自己给的另一个值，回填也无从谈起）。
        Assert.Contains("Id", op.Columns);
        Assert.Equal(id, Guid.Parse(Assert.Single(Database.Rows(
            "SELECT [Id] FROM [dbo].[QuickSqlSessionToken] WHERE [Value] = @v", ("@v", value)))["Id"]));

        Assert.Same(db.ChangeTracker.Entries<SessionToken>().Single().Entity, db.Set<SessionToken>().Local.Find(id));

        // 这张表只由测试写，所以留下的行自己清（会话没有级联，也没有软删）。
        Assert.Equal(1, await Orm.Delete<SessionToken>().Where(t => t.Value == value).ExecuteAffrowsAsync(ct));
    }

    // ── Update 档：只写脏列 ───────────────────────────────────────────

    [Fact]
    public async Task A_tracked_read_then_a_one_column_change_writes_only_that_column()
    {
        Database.Ready();

        var note = tagged(3);

        var id = await Seed(row("乙公司", 12.3456m, note));

        var db = Session();
        var order = Assert.Single(await ReadTagged(db, note));

        order.Note = note + "/改";

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        Assert.Equal(["Note"], op.Columns);
        Assert.Equal(1, op.Rows);
        Assert.Equal(1, op.StatementCount);

        // 单行更新的文本四家各一形：T-SQL 把别名挪进 FROM 而 SET 侧用别名，虚谷没有目标别名、SET/WHERE 都用表名自限定
        // （§12.5 那份 2∶2→3∶2 的分布在这里的现场版），第七家把别名留在 <c>UPDATE</c> 后面而 <b>SET 那一侧必须是裸列</b>
        // （别名限定那一形在本家是语法错，逐字见 <c>SqliteDialectTests</c> 引的 4521/4522）。逐字文本本来就是这一层的断言单位，分岔只开在"用哪一形"。
        //
        // <b>第四家（postgresql）与第七家同形，而理由不是同一句</b>：本家<em>收得下</em>别名限定的那一形
        // <c>SET "t0"."Note" = …</c>（不像第七家会报语法错），但它今天发的就是第七家那一串 ——
        // 逐字见 <c>obj/logs/v37-pg-int-1.log</c> 那一颗红里的 <c>String:</c> 侧。
        // 于是这一格是"两族同形"而不是"三家三形"，而把这一条并到第七家那一支里、不另开一支，恰好就是这条事实的记法。
        if (Database.Family == Database.Target.Xugu)
            Assert.StartsWith("UPDATE \"QUICKSQLORDER\" SET \"NOTE\" = :p0 WHERE \"QUICKSQLORDER\".\"ID\" = :p1", op.Sql, StringComparison.Ordinal);
        else if (Database.Family is Database.Target.Sqlite or Database.Target.PostgreSql)
            Assert.StartsWith("UPDATE \"QuickSqlOrder\" AS \"t0\" SET \"Note\" = @p0 WHERE \"t0\".\"Id\" = @p1", op.Sql, StringComparison.Ordinal);
        else if (Database.Family == Database.Target.MySql)
            // <b>第五族（mysql）与上面那两支同一<em>形状</em>，换的只是引号字形</b>：别名留在 <c>UPDATE</c>
            // 后面、<c>SET</c> 那一侧是裸列、<c>WHERE</c> 用别名限定 —— 逐字文本不是推断，它同时有
            // 离线册那份 <c>golden/mysql/update-guard-three-way.sql</c>（同一条 <c>UPDATE `QuickSqlOrder` AS `t0` SET …</c>）
            // 与真库上的执行读数（<c>P-20 ∶ P-20b ∶ P-21</c>，出处 obj/logs/v37-my-recon-3.log：
            // 那一形在靶表上改第一行与第三行各 affected=1，读回来是写进去的那一串）。
            // <b>为什么不并进上面那一支</b>：那两支比的是同一颗字符串常量，本家的常量差一层字形，
            // 而"形状同、字形不同"这件事一旦并支就会在下一版改字形时悄悄断掉。
            Assert.StartsWith("UPDATE `QuickSqlOrder` AS `t0` SET `Note` = @p0 WHERE `t0`.`Id` = @p1", op.Sql, StringComparison.Ordinal);
        else
            Assert.StartsWith("UPDATE [t0] SET [Note] = @p0 FROM [QuickSqlOrder] AS [t0] WHERE", op.Sql, StringComparison.Ordinal);

        var stored = Row(id);

        // 只有 Note 动了：其余六列才是这一格的判据 —— 实现若把整行摊平进 SET，CustomerId 会先变成 0。
        Assert.Equal(note + "/改", stored["Note"]);
        Assert.Equal("乙公司", stored["CustomerName"]);
        Assert.Equal(4242, Num(stored, "CustomerId"));
        Assert.Equal(12.3456m, Money(stored, "Amount"));
        Assert.Equal((int) OrderState.Placed, Num(stored, "State"));
        Assert.Equal("NULL", stored["PaidAt"]);
        Assert.Equal(Moment, When(stored, "CreatedAt"));

        // 结算之后快照重拍：同一句再保存一次不该有任何东西可写。
        Assert.Equal(EntityState.Unmodified, db.Entry(order).State);
        Assert.Empty((await db.SaveChangesAsync(ct)).Operations);
    }

    [Fact]
    public async Task A_chinese_value_written_through_the_session_survives_the_round_trip_untouched()
    {
        Database.Ready();

        var note = tagged(4);
        var id = await Seed(row("原名字", 1m, note));

        var db = Session();
        var order = Assert.Single(await ReadTagged(db, note));

        order.CustomerName = "戊戌科技（改）";

        Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // 排序规则决定 varchar 那一档的下场（§10.4），而这里 SET 位的参数是跟踪器亲手包的 ParameterValue：
        // 它与写链那条 binder 是不是同一段代码，只有真库说得上话，离线只能证明文本。
        Assert.Equal("戊戌科技（改）", Row(id)["CustomerName"]);
    }

    [Fact]
    public async Task Same_shaped_changes_on_two_rows_collapse_into_one_merged_statement()
    {
        Database.Ready();

        var note = tagged(5);

        var first = await Seed(row("丙公司", 1m, note));
        var second = await Seed(row("丁公司", 2m, note));
        var third = await Seed(row("戊公司", 3m, note));

        var db = Session();
        var rows = await ReadTagged(db, note);

        Assert.Equal(3, rows.Count);

        rows[0].Note = "同一种改动";
        rows[1].Note = "同一种改动";
        rows[2].Amount = 99.9999m;

        var report = await db.SaveChangesAsync(ct);
        var updates = Ops(report, WriteOperation.Update);

        // §12.5 的按列掩码分桶从 v0.19 起接上了合并：桶仍然是两条（掩码不同就不合），
        // 但语句从三条降到两条 —— 同掩码的那两行合成一条，另一行仍然自己一句。
        // 后一半是这一格的验收线：合并是加法，单行桶的文本必须与 v0.18 逐字相同。
        //
        // <b>v0.34 第一笔开分岔</b>：判据写成"这一家的 <c>SupportsJoinedWrites</c> 说了算"而不是
        // "虚谷说了算"——合并档的门是那颗声明（§12.5：false 的三家退逐条），这里只是把它接到真库现场。
        var merges = Database.Provider.Dialect.SupportsJoinedWrites;

        Assert.Equal(2, updates.Count);
        Assert.Equal(merges ? 2 : 3, report.StatementCount);

        var merged = Assert.Single(updates, static o => o.Columns.Count == 1 && o.Columns[0] == "Note");

        Assert.Equal(2, merged.Rows);
        Assert.Equal(merges ? 1 : 2, merged.StatementCount);

        if (merges)
        {
            if (Database.Family == Database.Target.PostgreSql)
            {
                // 本家合并档的两句结构性差别，都不是"翻译"出来的而是那一家语法逼出来的：
                // ① <c>UPDATE … FROM</c> 的目标表<em>不能</em>再挂进 JOIN（挂了就是 42P01 一类的解析错），
                //    所以联结值表那一侧没有 <c>INNER JOIN … ON</c>，<c>ON</c> 折进了 <c>WHERE</c> ——
                //    <c>FROM (VALUES …) AS "t1" (…)</c> 之后紧跟着的就是那句键谓词。
                // ② 可空列的守卫在本家是一式 <c>IS DISTINCT FROM</c>，不是 T-SQL 那三段 <c>OR</c> 展开
                //    （分岔点在 <c>IChangeGuard</c>：本家族走 <c>PostgreSqlChangeGuard</c> 那一句形，
                //    T-SQL 走 <c>ChangeGuardShapes.ThreeWay</c>。<c>Gbase8sChangeGuard</c> 的类注释记着那两份读数
                //    —— 按调用点 5 ∶ 1、按 provider 8 ∶ 5 —— 而§13.5 数"未实测"的位也按这一族的一颗算）。
                //    "键列不进守卫"这一半在两族是同一句判据：守卫里只有那一列改动。
                Assert.Contains("SET \"Note\" = \"t1\".\"Note\" FROM (VALUES ", merged.Sql, StringComparison.Ordinal);
                Assert.Contains(") AS \"t1\" (\"Id\", \"Note\") WHERE \"t0\".\"Id\" = \"t1\".\"Id\"", merged.Sql, StringComparison.Ordinal);
                Assert.Contains("\"t0\".\"Note\" IS DISTINCT FROM \"t1\".\"Note\"", merged.Sql, StringComparison.Ordinal);
                Assert.DoesNotContain(" INNER JOIN ", merged.Sql, StringComparison.Ordinal);
                Assert.DoesNotContain(" OR ", merged.Sql, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("SET [Note] = [t1].[Note] FROM [QuickSqlOrder] AS [t0] INNER JOIN (VALUES ", merged.Sql, StringComparison.Ordinal);
                Assert.Contains(") AS [t1] ([Id], [Note]) ON [t0].[Id] = [t1].[Id]", merged.Sql, StringComparison.Ordinal);

                // Note 是可空列，所以守卫是三段展开；这一句同时是"键列不进守卫"的现场（守卫里只有 Note）。
                Assert.Contains("WHERE [t0].[Note] <> [t1].[Note] OR ([t0].[Note] IS NULL AND [t1].[Note] IS NOT NULL)" +
                    " OR ([t0].[Note] IS NOT NULL AND [t1].[Note] IS NULL)", merged.Sql, StringComparison.Ordinal);
            }
        }
        else
        {
            // 退到逐条：同一个桶里的两句各带自己的值与守卫，键逐句点名 —— 语义等价由 §12.4 第 2 项那句撑，
            // 这里只钉"发出去的形状"确实是逐条（v0.34 起这一格有真库读数）。
            Assert.DoesNotContain("JOIN", merged.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("VALUES", merged.Sql, StringComparison.Ordinal);
        }

        var singular = Assert.Single(updates, static o => o.Columns.Count == 1 && o.Columns[0] == "Amount");

        Assert.Equal(1, singular.Rows);
        Assert.Equal(1, singular.StatementCount);

        if (Database.Family == Database.Target.Xugu)
            Assert.StartsWith("UPDATE \"QUICKSQLORDER\" SET \"AMOUNT\" = :p0 WHERE \"QUICKSQLORDER\".\"ID\" = :p1", singular.Sql, StringComparison.Ordinal);
        else if (Database.Family is Database.Target.Sqlite or Database.Target.PostgreSql)
            Assert.StartsWith("UPDATE \"QuickSqlOrder\" AS \"t0\" SET \"Amount\" = @p0 WHERE \"t0\".\"Id\" = @p1", singular.Sql, StringComparison.Ordinal);
        else if (Database.Family == Database.Target.MySql)
            // 与上面那颗 <c>Note</c> 同一格、同一条理由（同一颗形状、只差引号字形，执行读数在
            // <c>P-20 ∶ P-21</c>，离线册那份在 <c>golden/mysql/update-guard-three-way.sql</c>）——
            // 两颗各写一遍不是重复：这一条钉的是 <c>Amount</c> 那一列的文本，上面那条钉的是 <c>Note</c>，
            // 而"逐字文本本来就是这一层的断言单位"对两颗都各自成立。
            Assert.StartsWith("UPDATE `QuickSqlOrder` AS `t0` SET `Amount` = @p0 WHERE `t0`.`Id` = @p1", singular.Sql, StringComparison.Ordinal);
        else
            Assert.StartsWith("UPDATE [t0] SET [Amount] = @p0 FROM [QuickSqlOrder] AS [t0] WHERE", singular.Sql, StringComparison.Ordinal);

        Assert.Equal("同一种改动", Row(first)["Note"]);
        Assert.Equal("同一种改动", Row(second)["Note"]);
        Assert.Equal(99.9999m, Money(Row(third), "Amount"));
        Assert.Equal(note, Row(third)["Note"]);
        Assert.Equal(1m, Money(Row(first), "Amount"));
    }

    [Fact]
    public async Task A_tracked_instance_with_a_concurrency_token_is_never_merged()
    {
        Database.Ready();

        // 本条要动并发列（InventoryItem 带 [ConcurrencyToken]，读回来就要接那一格），所以挂的是
        // 新版那顆 targeted 闸门；旧版整族键回填闸门已在 v0.34 第一笔退场，两颗缺口逐格见
        // Database.RequireConcurrencyColumn 与 §14 的本版账。
        Database.RequireConcurrencyColumn();

        var sku = "quicksql-write/merge-" + Guid.NewGuid().ToString("N")[..6];

        var first = (int) await Orm.Insert(new InventoryItem { Sku = sku + "/a", Quantity = 1 }).ExecuteIdentityAsync(ct);
        var second = (int) await Orm.Insert(new InventoryItem { Sku = sku + "/b", Quantity = 2 }).ExecuteIdentityAsync(ct);

        var db = Session();
        var rows = await db.Set<InventoryItem>().Where(i => i.Sku == sku + "/a" || i.Sku == sku + "/b").OrderBy(i => i.Id).ToListAsync(ct);

        Assert.Equal(2, rows.Count);

        // 同一种改动、两行、列可合 —— 除了它们带并发列，而那一档正是"整型退回逐条"的那一条。
        rows[0].Quantity = 10;
        rows[1].Quantity = 20;

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        Assert.Equal(2, op.Rows);
        Assert.Equal(2, op.StatementCount);
        Assert.Contains("OUTPUT inserted.[RowVersion]", op.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("VALUES", op.Sql, StringComparison.Ordinal);

        // 逐行不同的新值与逐行不同的旧版本：一条多行语句给不了这个回包，所以这里必须真的是两句。
        // 读走裁判（不是会话）：这一格判的是"发出去了几句"，值有没有落对得问库里那一格。
        Assert.Equal("10", Stored(first)["Quantity"]);
        Assert.Equal("20", Stored(second)["Quantity"]);

        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task The_guard_leaves_out_the_row_that_already_held_the_value_and_the_count_says_so()
    {
        Database.Ready();

        var note = tagged(12);

        var first = await Seed(row("甲", 1m, note));
        var second = await Seed(row("乙", 2m, note));
        var already = await Seed(row("丙", 3m, note));

        var db = Session();
        var rows = await ReadTagged(db, note);

        // 会话读完之后，<b>会话之外的人</b>把第三行改成了"待会儿要写进去的那个值"。
        // 于是这一行在会话里仍然算脏（它的快照是旧值），而在库里<em>已经是</em>新值。
        Database.Execute("UPDATE [dbo].[QuickSqlOrder] SET [Note] = @v WHERE [Id] = @id",
            ("@v", "同一个改动"), ("@id", already));

        foreach (var row in rows)
            row.Note = "同一个改动";

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // 这一格的全部意义在这两个数上：候选三行、一句语句、而受影响<em>两</em>行。
        // 守卫没有它，批量保存就会把三行的审计列一起刷新一遍（§12.4 第 2 项点名的那个高频真实 bug）。
        Assert.Equal(Database.Provider.Dialect.SupportsJoinedWrites ? 1 : 3, op.StatementCount);

        // v0.34 真库读数（v34-int-xugu-3.log）：退逐条的一家这里交的是 3 而不是 2 —— 而这不是缺陷，
        // 是这一层本来就分开的两档口径：守卫是<em>合并档</em>的东西（一条语句服务多行，才需要把"值本来就对"
        // 的那行挡在 SET 之外），逐条档的 WHERE 只有键、数的是命中数。下面那颗"对照"用例（本文件后段
        // "逐条那一档数的始终是命中数"）钉的就是这一格，v0.34 起两档在同一台真库上各绿了一次。
        Assert.Equal(Database.Provider.Dialect.SupportsJoinedWrites ? 2 : 3, op.Rows);

        // 挡住不等于写坏：那一行的值仍然对得上（库里本来就有），另外两行也确实被写进去了。
        Assert.Equal("同一个改动", Row(already)["Note"]);
        Assert.Equal("同一个改动", Row(first)["Note"]);
        Assert.Equal("同一个改动", Row(second)["Note"]);

        // 而会话照样把三行都结算掉 —— 守卫少写的那一行没有欠账，再保存一次不该有东西可写。
        Assert.Empty((await db.SaveChangesAsync(ct)).Operations);

        // 对照：逐条那一档数的始终是命中数，同一种"值没变"的行它算一条（链式 UPDATE 那边
        // 已经有一条用例钉着，这里重钉一遍是为了让两个档的口径出现在同一个现场里）。
        var alone = await Seed(row("丁", 4m, note + "/solo"));

        var single = Session();
        var lonely = Assert.Single(await ReadTagged(single, note + "/solo"));

        Database.Execute("UPDATE [dbo].[QuickSqlOrder] SET [Note] = @v WHERE [Id] = @id",
            ("@v", "同一个改动"), ("@id", alone));

        lonely.Note = "同一个改动";

        var singular = Assert.Single(Ops(await single.SaveChangesAsync(ct), WriteOperation.Update));

        Assert.Equal(1, singular.StatementCount);
        Assert.Equal(1, singular.Rows);
    }

    [Fact]
    public async Task A_merged_bucket_splits_into_more_statements_when_it_runs_out_of_parameter_budget()
    {
        Database.Ready();

        // 预算 6、每行两颗参数（键 + Note）⇒ 2 行一块（§12.3 那一式）。真预算下的数字见 XML 文档。
        // <b>v0.32 第二格：被掰的那一颗包在内层是缝给的那一家的 provider</b>，连接串也跟着走 ——
        // 从前这两格写死 <c>SqlServer</c>，于是第五家那一跑里三行写在 SQL Server、按 <c>@tag</c> 的核对问的是 TESTDB。
        var orm = Database.OrmWith(provider: LowBudgetProvider.Around(Database.Provider, 6));

        var note = tagged(13);

        var ids = new List<int>();

        foreach (var (name, amount) in new[] { ("甲", 1m), ("乙", 2m), ("丙", 3m) })
            ids.Add(await Seed(row(name, amount, note)));

        var db = new DbContext(orm);
        var rows = await ReadTagged(db, note);

        Assert.Equal(3, rows.Count);

        rows[0].Note = note + "/改";
        rows[1].Note = note + "/改";
        rows[2].Note = note + "/改";

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // 跨块必须还是<em>一条报告</em>里的一个桶，而语句数跟着块数走；行数口径是真变化（三行都真变了）。
        // 合并档开时 6 颗参数装 2 行 ⇒ 两块 = 两句；不开时逐条 ⇒ 三句（预算那一式管的是"一块几行"，
        // 逐条的形状下每一块本来就装得下）—— 两个数都是这一格的读数，v0.34 起在两家各有真库见证。
        Assert.Equal(Database.Provider.Dialect.SupportsJoinedWrites ? 2 : 3, op.StatementCount);
        Assert.Equal(3, op.Rows);

        foreach (var id in ids)
            Assert.Equal(note + "/改", Row(id)["Note"]);
    }

    [Fact]
    public async Task Expanding_the_null_check_the_way_the_spec_first_wrote_it_is_a_syntax_error_on_the_target()
    {
        Database.Ready();

        // 对照的那一半两家都要在场：三段写法<em>执行得过</em>，否则下一段那句"过不了"只是"我们写错了 SQL"。
        // 取一条真存在的行，免得空结果集让这一句看起来只是没跑到。
        var probe = tagged(14);

        var id = await Seed(row("对照", 1m, probe));

        var threeWay = "SELECT CASE WHEN [Note] <> @v OR ([Note] IS NULL AND @v IS NOT NULL) OR ([Note] IS NOT NULL AND @v IS NULL) " +
            "THEN 1 ELSE 0 END AS Differs FROM [dbo].[QuickSqlOrder] WHERE [Id] = @id";

        var boolAsValue = "SELECT CASE WHEN ([Note] IS NULL) <> (@v IS NULL) THEN 1 ELSE 0 END AS Differs " +
            "FROM [dbo].[QuickSqlOrder] WHERE [Id] = @id";

        if (Database.Family == Database.Target.Xugu)
        {
            // 【v0.34 第一笔的真库新读数，v34-int-xugu-2.log】旧版这一格写"对照的那一半两家都要在场"，
            // 量的其实是推断：虚谷<em>不收</em>未定型的 NULL 绑定参数进 <>/IS NULL 比较 ——
            // E17007"不能将实例NULL与CHAR做转换"，两颗问句（三段形与 <> 形）同一个下场。
            // 于是"在场"那一半换<em>字面量形</em>跑（与 F-22/F-23 探针同文，那条量的是同一颗形状的字面量），
            // 绑定参数那两形改断"抛"：红点本身入账，两家各数各的。
            //
            // 上一版（v0.32 第二格）的改判记录照录：探针当时问的是字面量两形（v32-recon-funcs-9.log
            // F-22/F-23）—— <c>CASE WHEN (NULL IS NULL) <> (1 IS NULL)</c> 交 1、裸形交 <c>True &lt;Boolean&gt;</c>，
            // 即"布尔表达式当值"在这一家<em>合法</em>，§12.4"三段展开是唯一写法"的<em>理由</em>在这一家不成立
            // （三段展开本身仍是对的写法）。那一条留下的"绑定参数这一形没问过、红绿都算第一颗实测数"
            // 这一版量到了：缺的不是"布尔不能当值"，是"未定型 NULL 参数进不了比较"——两句话在 §12.4 里不混。
            // 【v0.34 第一笔的真库读数 v34-int-xugu-3.log 订正 v2】两颗绑定形<em>不同一个下场</em>：
            // 三段形在 [Note] <> @v 那一颗被驳（E17007"不能将实例NULL与CHAR做转换"——未定型 NULL
            // 参数进不了与 CHAR 列的直接比较），而 <> 形只把 @v 用在 IS NULL 两侧，这一家<em>收得下</em>、
            // 交出 1。也就是说 v0.32 那颗探针留下的"参数参与那颗布尔比较"缺口，这一版量到了它的<em>边界</em>：
            // 缺的是"未定型 NULL 参与<em>值</em>比较"，不是"参数参与 IS NULL"。两句话在 §12.4 里不混。
            Assert.NotNull(Record.Exception(() => Database.Rows(threeWay, ("@v", (string?) null), ("@id", id))));

            Assert.Equal("1", Assert.Single(Database.Rows(boolAsValue, ("@v", (string?) null), ("@id", id)))["Differs"]);

            Assert.Equal("1", Assert.Single(Database.Rows(
                threeWay.Replace("@v", "NULL"), ("@id", id)))["Differs"]);

            Assert.Equal("1", Assert.Single(Database.Rows(
                boolAsValue.Replace("@v", "NULL"), ("@id", id)))["Differs"]);
        }
        else if (Database.Family == Database.Target.MySql)
        {
            // <b>第五族（mysql）两句都收、两句都给 1，而"过不了"那一半在本家<em>没有对象</em></b>：
            // 上一版这一支不存在，本家因此落到 <c>else</c> 那一支、红在 <c>Assert.NotNull(raised)</c>
            // （obj/logs/v37-my-int-1.log 第一条该用例的红：<c>Assert.NotNull() Failure: Value is null</c>）——
            // 那句红读起来像"我们的夹具没造出语法错"，真相是本家<em>收得下</em>那一形。三颗读数：
            // 三段形带绑定 NULL 参数交 <c>Differs=1 (Int64)</c>【实测·P-17，同一句问 NULL 那一行交 0，
            // 【实测·P-17b】—— 那才是"守卫语义对"的对照组，参数与列同为空时不该判成"变了"】、
            // <c><></c> 形交 <c>1</c>【实测·P-18】、裸形四颗 <c>(Title IS NULL) ∶ (1=0) ∶ (2>1) ∶ NOT (2>1)</c>
            // 交 <c>1 ∶ 0 ∶ 1 ∶ 0</c>【实测·P-19，出处 obj/logs/v37-my-recon-3.log】。
            // <b>所以本家与第七家同判（§12.4 那句"三段展开是唯一写法"在本家不成立），而两支仍各写一遍</b>：
            // 第七家那一支的凭据是 3101/3102/3103/3112 那一组、本家这一颗是 P 段那一组，
            // 两组的<em>形</em>相同是巧合而不是同一颗测量（同 <c>MaterializationAllocationTests.RowOn</c>
            // 分页那一支的理由）。<b>写法本身照旧不变</b>：这一层仍然发三段展开，因为它是跨家唯一同形的形状。
            Assert.Equal("1", Assert.Single(Database.Rows(threeWay, ("@v", (string?) null), ("@id", id)))["Differs"]);

            Assert.Equal("1", Assert.Single(Database.Rows(boolAsValue, ("@v", (string?) null), ("@id", id)))["Differs"]);
        }
        else if (Database.Family == Database.Target.Sqlite)
        {
            // <b>第七家两句都收、两句都给 1，而"过不了"那一半在本家没有对象</b>。
            // 本家的布尔表达式<em>就是值</em>：同一形的手工三颗 <c>(1 = 0)</c> ∶ <c>(0 OR 1)</c> ∶ <c>NOT (2 &gt; 1)</c>
            // 交回 <c>0</c> ∶ <c>1</c> ∶ <c>0</c>【实测·3112】，而三段式整颗放进 <c>SELECT</c> 的投影位也交得回数
            // （3101/3102 两臂，参数为 <c>DBNull.Value</c> 那一臂同样跑得通【实测·3103】）。
            // 于是 §12.4 那句"三段展开是唯一写法"在本家<em>不成立</em>——它是 T-SQL 那一句
            // <c>Msg 102 … Incorrect syntax near '&lt;'</c> 的推论，而那颗前提在本家没有。
            // <b>但写法本身不变</b>：本仓库仍然发三段展开，因为它是七家里唯一跨家同形的形状
            // （<c>update-guard-three-way</c> 那批黄金样本按家存逐字形，共用层只发一颗 AST），
            // 这里两条各钉一句"本家两种写法给出同一个数"，正是那一句理由的可执行形式。
            Assert.Equal("1", Assert.Single(Database.Rows(threeWay, ("@v", (string?) null), ("@id", id)))["Differs"]);

            Assert.Equal("1", Assert.Single(Database.Rows(boolAsValue, ("@v", (string?) null), ("@id", id)))["Differs"]);
        }
        else
        {
            Assert.Equal("1", Assert.Single(Database.Rows(threeWay, ("@v", (string?) null), ("@id", id)))["Differs"]);

            // §12.4 第 2 项原稿给的展开是 <c>(a <> s.a OR (a IS NULL) <> (s.a IS NULL))</c>。后半句的直译
            // 就是下面这一句，而 <b>T-SQL 里布尔表达式不是值：它连语法都过不了</b>。三段展开因此不是
            // "另一种写法"而是唯一写法 —— 这一条用例要做的就是把这句话从读出来的推断变成跑出来的见证。
            var raised = Record.Exception(() => Database.Execute(boolAsValue, ("@v", (string?) null), ("@id", id)));

            // 只断"抛了"，不断消息原文：两家实例的语言与措辞不一样（102 是中文库），而这一格要的是"过不了"这件事。
            // 2026-09-22 在 LocalDB 上单独跑那一句，交回的是 <c>Msg 102, Level 15: Incorrect syntax near '<'</c> ——
            // 记在这里是为了让"过不了"有原文可查，而不是只有一句转述。
            Assert.NotNull(raised);
        }
    }

    [Fact]
    public async Task A_change_made_through_the_tracked_instance_is_written_without_being_marked()
    {
        Database.Ready();

        var note = tagged(6);

        await Seed(row("己公司", 5m, note));

        var db = Session();

        // 读一次、改一次、保存：全程没有 Attach、没有 Entry(...).State，判的就是 §8.2 那句自动侦测。
        var order = Assert.Single(await ReadTagged(db, note));

        order.State = OrderState.Shipped;
        order.PaidAt = Moment;

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Update));

        // 列序是契约列序而不是赋值序：State 在第 5 列、PaidAt 在第 7 列。
        Assert.Equal(["State", "PaidAt"], op.Columns);

        var stored = Row(order.Id);

        Assert.Equal((int) OrderState.Shipped, Num(stored, "State"));
        Assert.Equal(Moment, When(stored, "PaidAt"));
    }

    // ── Delete 档 ────────────────────────────────────────────────────

    [Fact]
    public async Task A_delete_goes_out_by_key_and_reports_no_columns()
    {
        Database.Ready();

        var note = tagged(7);

        var kept = await Seed(row("庚公司", 1m, note + "/keep"));
        var doomed = await Seed(row("辛公司", 1m, note + "/drop"));

        var db = Session();

        await ReadTagged(db, note + "/keep");
        var toDelete = Assert.Single(await ReadTagged(db, note + "/drop"));

        // 裁决①（可达才删）的前置：删一只声明了级联的父之前，先把它的明细捞进会话。
        // 这一格的现场里没有明细行，Load 带回来的是空串 —— 那句"没有要删的"从此是<em>说过</em>的，不是猜的。
        db.Set<Order>().Load(o => o.Items!);

        db.Remove(toDelete);

        Assert.True(db.ChangeTracker.HasChanges);

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Delete));

        // 那条语句里没有 SET：把主键列填进 Columns 会让人以为它能像 UPDATE 一样挑列。
        Assert.Empty(op.Columns);
        Assert.Equal(1, op.Rows);
        Assert.Equal(1, op.StatementCount);
        Assert.StartsWith("DELETE FROM", op.Sql, StringComparison.Ordinal);

        Assert.Empty(Orders("[Id] = @id", ("@id", doomed)));
        Assert.Equal(1, Count("[Id] = @id", ("@id", kept)));

        // 写完成才摘出会话：那一只在报告交回来之后既不在 Local 里、也不再是 ChangeTracker 的实例。
        Assert.Equal(1, db.ChangeTracker.Count);
        Assert.Null(db.Set<Order>().Local.Find(doomed));
        Assert.False(db.ChangeTracker.HasChanges);
    }

    // ── 物化期的身份映射（离线判不到的那一半） ────────────────────────

    [Fact]
    public async Task Reading_the_same_row_twice_gives_the_same_instance_while_a_projection_gives_nothing()
    {
        Database.Ready();

        var note = tagged(8);

        var first = await Seed(row("壬公司", 1m, note + "/a"));
        var second = await Seed(row("癸公司", 2m, note + "/b"));

        var db = Session();

        var a = Assert.Single(await ReadTagged(db, note + "/a"));
        var b = Assert.Single(await ReadTagged(db, note + "/b"));

        Assert.Equal(2, db.ChangeTracker.Count);

        var again = Assert.Single(await db.Set<Order>().Where(o => o.Id == first).ToListAsync(ct));

        Assert.Same(a, again);
        Assert.Equal(2, db.ChangeTracker.Count);

        // 投影不是实体：标量那一档一条都不进身份映射，否则"改了这个整数就改了一行"是假的。
        var ids = await db.Set<Order>().Where(o => o.Note == note + "/a" || o.Note == note + "/b")
            .Select(o => o.Id).ToListAsync(ct);

        Assert.Equal([first, second], ids);
        Assert.Equal(2, db.ChangeTracker.Count);

        // AsNoTracking 是同一份模型的无跟踪视图：那一行它照样读得到，但交的是另一只实例，会话没为它多记账。
        var untracked = Assert.Single(await db.Set<Order>().AsNoTracking().Where(o => o.Id == second).ToListAsync(ct));

        Assert.NotSame(b, untracked);
        Assert.Equal(b.Id, untracked.Id);
        Assert.Equal(2, db.ChangeTracker.Count);
        Assert.Throws<QuickSqlException>(() => { _ = db.Entry(untracked); });
    }

    [Fact]
    public async Task Two_sessions_reading_the_same_row_each_get_their_own_instance_and_never_collide()
    {
        Database.Ready();

        var note = tagged(9);

        var id = await Seed(row("子公司", 1m, note));

        var first = Session();
        var second = Session();

        var fromFirst = Assert.Single(await ReadTagged(first, note));
        var fromSecond = Assert.Single(await ReadTagged(second, note));

        Assert.NotSame(fromFirst, fromSecond);

        fromFirst.Note = note + "/first";
        await first.SaveChangesAsync(ct);

        fromSecond.Note = note + "/second";
        await second.SaveChangesAsync(ct);

        // 后写的赢：这一版没有并发令牌（§8.2 的 IsConcurrencyToken 那一档没接线），
        // 所以"两个会话改同一行"的下场是静默覆盖，而不是 DbUpdateConcurrencyException。
        Assert.Equal(note + "/second", Row(id)["Note"]);
    }

    // ── 三档进事务（§6 (3) 的 DbContext 那半边） ──────────────────────

    [Fact]
    public async Task SaveChanges_enlists_in_the_ambient_transaction_and_a_rollback_undoes_every_phase()
    {
        Database.Ready();

        var note = tagged(10);

        await Seed(row("卯公司", 1m, note + "/keep"));
        await Seed(row("辰公司", 1m, note + "/drop"));
        var editedId = (await Seed(row("巳公司", 1m, note + "/edit")));

        var db = Session();

        var keep = Assert.Single(await ReadTagged(db, note + "/keep"));
        var drop = Assert.Single(await ReadTagged(db, note + "/drop"));
        var edit = Assert.Single(await ReadTagged(db, note + "/edit"));
        var fresh = row("午公司", 7m, note + "/new");

        db.Add(fresh);

        // 同上一条的理由：Remove 一只声明了级联的父要先让子行可达（裁决①），这里带回来的是空串。
        db.Set<Order>().Load(o => o.Items!);

        db.Remove(drop);
        edit.Amount = 42m;

        using (var ambient = Orm.BeginAmbientTransaction())
        {
            var report = await db.SaveChangesAsync(ct);

            // 三档都挂在这条被独占的连接上：块内复看走同一连接，于是一次能看见三种效果。
            Assert.Single(Ops(report, WriteOperation.Insert));
            Assert.Single(Ops(report, WriteOperation.Update));
            Assert.Single(Ops(report, WriteOperation.Delete));

            Assert.NotNull(await Orm.Select<Order>().Where(o => o.Note == note + "/new").FirstOrDefaultAsync(ct));
            Assert.Null(await Orm.Select<Order>().Where(o => o.Id == drop.Id).FirstOrDefaultAsync(ct));
            Assert.Equal(42m, (await Orm.Select<Order>().Where(o => o.Id == editedId).FirstOrDefaultAsync(ct))!.Amount);

            ambient.Rollback();

            Assert.True(ambient.Completed);
        }

        // 出块即撤销，三档一起回 —— 包括那句 INSERT 连它已经被回填回来的主键。
        Assert.Equal(0, Count("[Note] = @note", ("@note", note + "/new")));
        Assert.Equal(1, Count("[Id] = @id", ("@id", drop.Id)));
        Assert.Equal(1, Count("[Id] = @id", ("@id", keep.Id)));
        Assert.Equal(1m, Money(Row(editedId), "Amount"));

        // ── 已知缺口，钉在这里而不是粉饰掉 ──
        // SaveChanges 的结算只看"三档都发出去了"，它不知道那条事务后来被撤销：于是会话以为写过了，
        // 而库里没有；回填回来的主键此刻指向一个不存在的行。要丢弃这批意图得调用方自己 Clear()。
        Assert.Equal(EntityState.Unmodified, db.Entry(edit).State);
        Assert.Equal(42m, edit.Amount);
        Assert.Same(fresh, db.Set<Order>().Local.Find(fresh.Id));
        Assert.False(db.ChangeTracker.HasChanges);

        db.ChangeTracker.Clear();

        Assert.Equal(0, db.ChangeTracker.Count);
    }

    [Fact]
    public async Task A_batch_refused_by_validation_before_the_first_statement_leaves_no_row_and_keeps_the_intent()
    {
        Database.Ready();

        var note = tagged(11);

        var db = Session();

        db.Add(row("好的一行", 1m, note + "/a"));
        db.Add(row(new string('字', 70), 1m, note + "/b"));

        var failure = await Assert.ThrowsAsync<ValidationFailedException>(async () => await db.SaveChangesAsync(ct));

        Assert.Equal("length", Assert.Single(failure.Failures).Rule);

        // §10.6 的位置：校验在第一条语句上线之前，所以这一批一条都没走。
        Assert.Equal(0, Count("[Note] = @note", ("@note", note + "/a")));
        Assert.Equal(0, Count("[Note] = @note", ("@note", note + "/b")));

        // 失败不结算：那两行<em>还</em>握着新增意图 —— 这就是"为什么不在异常路径上顺手回滚会话"的判据。
        Assert.True(db.ChangeTracker.HasChanges);

        // ── 这里从前开着一颗<em>放在中间</em>的闸门，而这是本层唯一一处这样的写法 ──
        // 放在开头会顺手把上面那四句的证人一起丢掉：第五家今天恰好把 §10.6 那一句"<em>校验在第一条语句上线之前</em>"
        // 跑绿了一次（v0.32 第二格第四跑：70 个 <c>字</c> 的 <c>CustomerName</c> 被 <c>Rule="length"</c> 拒了，
        // 两条 <c>Count == 0</c> 同时成立，而那颗 <c>MaxBytes = chars × 4</c> 的宽松尺子没有放它过去）。
        // 【v0.34 第一笔改判】从这里往下从前要跳过（键回填跑不了），现在不再跳过：单颗自增列改走
        // 语句后回读，这一条的后半截连同上面那四句证人一起在虚谷上真跑。跑过的留下证人，跑不到的记进 §14，两笔各归各。
        // 后半截若红，红不回上面那四句 —— 第一次 <c>SaveChanges</c> 一条语句都没发的现场判据照旧成立。

        db.ChangeTracker.Entries<Order>().Last().Property(o => o.CustomerName).CurrentValue = "改短了";

        var op = Assert.Single(Ops(await db.SaveChangesAsync(ct), WriteOperation.Insert));

        Assert.Equal(2, op.Rows);
        Assert.Equal(1, Count("[Note] = @note", ("@note", note + "/a")));
        Assert.Equal(1, Count("[Note] = @note", ("@note", note + "/b")));

        db.ChangeTracker.Clear();
    }
}
