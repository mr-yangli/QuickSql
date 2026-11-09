using QuickSql.Aop;
using QuickSql.TestInfra;
using QuickSql.Transactions;
using System.Data;
using System.Globalization;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>写路径与事务三形态在真库上跑一遍（§5.4 写侧、§6 (1)(2)(3)、§12.4 第 1/4/8 项）。
///
/// 黄金样本能证明"文本对"，证明不了这几件事：<c>OUTPUT inserted.[Id]</c> 的回包到底按什么顺序、
/// 库是不是接受 <c>VALUES</c> 前面那一段 OUTPUT、事务里未提交的行在另一条连接上看不看得见、
/// 中文与毫秒与 <c>decimal(18,4)</c> 写进去是不是原样回来。所以这里断的全是"文本层面看不见的东西"。
///
/// <b>只插不删</b>，与 <see cref="Database"/> 同一套姿势：每条用例带一个本次运行独占的标签（<see cref="Batch"/>
/// 加上用例名），所有断言按标签或按唯一的 <c>CustomerName</c> 过滤，因此库里别人——包括上一轮的自己——
/// 留下的行都不影响本用例，重复跑也不会因为"计数变了"变红。人工清理按 <c>LIKE 'quicksql-write/%'</c>。</summary>
public sealed class WritePathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    /// <summary>本次运行独占的批次前缀。它必须每次跑都不同，否则第二轮的计数会把第一轮的行算进来。</summary>
    // Batch 每用例一颗（static 会让同类各用例共一篇标签，隔离押在 xunit 方法序上，重编译可翻红；理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-write/" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 5, 6, 7, 8, 9, 123);

    /// <summary>带毫秒的时间列一律取成一份<em>看得见小数秒的文本</em>再比：<c>reader.GetValue().ToString()</c>
    /// 在 T-SQL 那一家会丢掉小数秒，而"毫秒有没有被 1/300 秒取整"正是这里要看的东西（§9.3）。
    /// <b>v0.32 第二格起这一句交给 <see cref="Database.MillisText"/>，两颗本地的 <c>Millis</c> 也并成那一颗</b>：
    /// 上一版它是 <c>WritePathTests</c> 与 <c>UpdateDeletePathTests</c> 各一份 <c>const string</c>，
    /// 而"打不出毫秒"这一件事在第五家是<em>整族</em>的（<c>CAST</c> 与 <c>TO_CHAR</c> 四形全丢，F-24/F-31/F-36/F-44），
    /// 分岔要开在缝上才只开一次 —— 详见那一颗的注释：这一家改的是"谁去打这一串字"。</summary>
    static string Millis(string column) => Database.MillisText(column);

    string scope(string part) => $"{Batch}/{part}";

    string tagged(int n) => $"{Batch}/{n}";

    static Order row(string name, decimal amount, string note) => new()
    {
        CustomerName = name,
        CustomerId = 7,
        Amount = amount,
        State = OrderState.Shipped,
        CreatedAt = Moment,
        Note = note,
    };

    static int storedOrders(string name)
        => int.Parse(Database.Rows(
            "SELECT COUNT(*) AS Cnt FROM [dbo].[QuickSqlOrder] WHERE [CustomerName] = @name",
            ("@name", name))[0]["Cnt"]);

    // ── CRUD 的纵向切片 ───────────────────────────────────────────────

    [Fact]
    public async Task A_single_insert_lands_every_column_and_reads_back_unchanged()
    {
        Database.Ready();

        // 名字里带中文：写侧全部按 nvarchar 传参（§10.4 的下半场），所以在 cp1252 实例上也不该塌成 '?'。
        // 这一句和 CollationTests 那条是两件事：那条测的是库自己怎么处理 varchar，这条测的是我们的绑定有没有降级。
        var name = tagged(1) + "/甲公司";

        var order = new Order
        {
            CustomerName = name,
            CustomerId = 4242,
            Amount = 12.3456m,
            State = OrderState.Paid,
            CreatedAt = new DateTime(2026, 5, 6, 7, 8, 9, 456),
            PaidAt = null,
            Note = Batch,
        };

        Assert.Equal(1, await Orm.Insert(order).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        var read = await Orm.Select<Order>()
            .Where(o => o.CustomerName == name)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // 五类值各有其塌法：中文（排序规则）、毫秒（1/300 秒取整）、decimal 标度、枚举（存 INT）、
        // 可空列（读成 default 而不是 null）。文本层面全都看不出问题。
        Assert.NotNull(read);
        Assert.Equal(4242, read!.CustomerId);
        Assert.Equal(12.3456m, read.Amount);
        Assert.Equal(OrderState.Paid, read.State);
        Assert.Equal(new DateTime(2026, 5, 6, 7, 8, 9, 456), read.CreatedAt);
        Assert.Null(read.PaidAt);

        // 行数是数出来的，不回填主键 ⇒ 实体上的 Id 仍然是 0。这一条钉的是出口的分工：
        // 要键必须走 ExecuteReturnKeysAsync/ExecuteIdentityAsync，而回填要多付一个结果集（§12.4 的代价面）。
        Assert.Equal(0, order.Id);
        Assert.True(read.Id > 0);
        Assert.Equal(1, storedOrders(name));
    }

    [Fact]
    public async Task The_backfilled_identity_is_the_identity_of_the_row_that_landed()
    {
        Database.Ready();
        var name = tagged(2);
        var order = row(name, 10.5m, Batch);
        var identity = await Orm.Insert(order).ExecuteIdentityAsync(TestContext.Current.CancellationToken);

        Assert.True(identity > 0);

        // 回填写的是传进去的那个实例（schema.SetValue），不是新建一个对象：调用方手里的引用照样能用。
        Assert.Equal(order.Id, identity);

        // 按 payload 认定，而不是"拿到了某个非零数"：库里那一号必须就是这次插入的行。
        var landed = Assert.Single(Database.Rows(
            $"SELECT [CustomerName], [Amount], {Millis("[PaidAt]")} AS PaidAt FROM [dbo].[QuickSqlOrder] WHERE [Id] = @id",
            ("@id", (int)identity)));

        Assert.Equal(name, landed["CustomerName"]);
        Assert.Equal(10.5m, decimal.Parse(landed["Amount"], CultureInfo.InvariantCulture));
        Assert.Equal("NULL", landed["PaidAt"]);
    }

    [Fact]
    public async Task A_client_generated_key_is_stored_as_is_and_the_key_exit_refuses()
    {
        Database.Ready();

        var id = Guid.NewGuid();

        Assert.Equal(1, await Orm.Insert(new SessionToken { Id = id, Value = scope("token"), ExpiresAt = Moment })
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        var landed = Assert.Single(Database.Rows(
            $"SELECT [Id], {Millis("[ExpiresAt]")} AS ExpiresAt FROM [dbo].[QuickSqlSessionToken] WHERE [Value] = @tag",
            ("@tag", scope("token"))));

        // Guid 不文本化、不翻字节序：SQL Server 的 uniqueidentifier 是原生类型（跨库对齐才需要 GuidAsText）。
        // <b>第七家多一句：那一格里躺着的是驱动自己写的那一串字，而它是大写形</b>（4710 逐字量到），
        // 所以期望值按缝给（<see cref="Database.StoredGuidText"/>）—— 同颗键的绑定比较在本家仍然打得中
        // （4711），换的只是"读回来的那一串长什么样"。
        Assert.Equal(Database.StoredGuidText(id), landed["Id"]);
        Assert.Equal(Moment, DateTime.Parse(landed["ExpiresAt"], CultureInfo.InvariantCulture));

        // 没有自增列 ⇒ 回填那一档必须拒，而不是产出一条"看着像在回填"的普通 INSERT。
        var failure = await Assert.ThrowsAsync<QuickSqlException>(
            async () => await Orm.Insert(new SessionToken { Id = Guid.NewGuid(), Value = scope("token2") })
                .ExecuteReturnKeysAsync(TestContext.Current.CancellationToken));

        Assert.Contains("没有自增列", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bulk_key_retrieval_maps_every_returned_row_back_to_the_entity_it_came_from()
    {
        Database.Ready();
        var note = scope("bulk");

        // 600 行 ⇒ 3 块（§12.3 的公式），所以这条同时是"跨块映射"的证明：第 2、3 块的回包必须落到
        // first + index 那个下标上。漏掉偏移的话，前 60 行的键会被后两块的值覆盖 —— 而每一条都仍然
        // 是个真实存在的 id，只有按 payload 对账才看得见。
        var rows = new Order[600];

        for (var i = 0; i < rows.Length; i++)
            rows[i] = row(tagged(3000 + i), i + 0.25m, note);

        var backfilled = await Orm.InsertRange(rows).ExecuteReturnKeysAsync(TestContext.Current.CancellationToken);

        Assert.Equal(rows.Length, backfilled.Count);
        Assert.All(backfilled, one => Assert.True(one.Id > 0, "有实体的主键没被回填，还是 0"));
        Assert.Equal(backfilled.Count, backfilled.Select(one => one.Id).Distinct().Count());

        // 键严格递增且与入参同序：OUTPUT 的回包按源行序，这是 L3 批量回填的全部前提。
        for (var i = 1; i < backfilled.Count; i++)
            Assert.True(backfilled[i].Id > backfilled[i - 1].Id, $"第 {i} 个键不比第 {i - 1} 个大：回包顺序与入参顺序不一致");

        // 逐行按 payload 对账：库里那一号的客户名必须正好是声称写进去的那一条。
        // 错位（同行串列、跨行、跨块）在这里都会变成"某个 id 上名字对不上"。
        var stored = Database.Rows(
                "SELECT [Id], [CustomerName] FROM [dbo].[QuickSqlOrder] WHERE [Note] = @note",
                ("@note", note))
            .Select(r => (Id: int.Parse(r["Id"], CultureInfo.InvariantCulture), Name: (string?)r["CustomerName"]))
            .OrderBy(x => x.Id)
            .ToArray();

        var claimed = backfilled.Select(one => (one.Id, one.CustomerName)).OrderBy(x => x.Id).ToArray();

        Assert.Equal(claimed, stored);
    }

    [Fact]
    public async Task The_row_count_exit_counts_what_the_database_changed_not_what_was_asked()
    {
        Database.Ready();

        var remark = scope("cust");
        var rows = Enumerable.Range(1, 3).Select(i => new Customer { Name = tagged(40 + i), City = "城", Remark = remark }).ToArray();

        Assert.Equal(3, await Orm.InsertRange(rows).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, await Orm.Select<Customer>()
            .Where(c => c.Remark == remark)
            .CountAsync(TestContext.Current.CancellationToken));

        // 违反契约的第 4 行让整批不执行：affrows 不是"成功行数"，而是"要么全写、要么一行都没写"。
        var mixed = rows.Append(new Customer { Name = new string('字', 200), City = "城", Remark = remark }).ToArray();

        await Assert.ThrowsAsync<ValidationFailedException>(
            async () => await Orm.InsertRange(mixed).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, await Orm.Select<Customer>()
            .Where(c => c.Remark == remark)
            .CountAsync(TestContext.Current.CancellationToken));
    }

    // ── 钩子的真库面（§12.4 第 4 项） ────────────────────────────────

    [Fact]
    public async Task Audit_fills_the_same_value_either_way_and_the_refresh_count_does_not_depend_on_the_shape()
    {
        Database.Ready();
        var stamp = new DateTime(2026, 7, 8, 9, 10, 11, 123);
        var batch = 0;
        var each = 0;

        // <b>v0.32 第二格起这两颗由缝给</b>：从前这一颗本地 <c>ormWith</c> 写死
        // <c>SqlServer.Provider(...)</c> + <c>SqlServer.TestConnectionString</c>，于是
        // <c>QUICKSQL_TEST_PROVIDER=xugu</c> 那一跑里<em>写</em>落在 192.168.0.102、下面那两句
        // <c>Database.Rows</c> 的<em>读</em>落在 TESTDB，这一条用例红成 <c>Expected: 6 / Actual: 0</c> ——
        // 那既不是钩子链的问题也不是这一家的问题，是这一行连错了库。独立一台 ORM 的需求照旧成立
        // （钩子是进程内状态），改的只是"哪一家"不再由用例点名。
        static QuickSqlOrm ormWith(QuickSqlAop aop) => Database.OrmWith(aop);

        // 这里用 SetValue 而不是改实体：钩子是在取值之后触发的，写实体不会影响已经取到的那一格，
        // 只有 SetValue 会同时改参数与实体（§10.1 的填充语义）。
        static void Fill(AuditValueEvent e, DateTime when)
        {
            if (e.Column.MemberName is "CreatedAt" or "PaidAt")
                e.SetValue(when);
        }

        var batchAop = new QuickSqlAop();

        batchAop.AuditValue = e =>
        {
            batch++;
            Fill(e, stamp);
        };

        await ormWith(batchAop).InsertRange([row(tagged(51), 1m, scope("audit")), row(tagged(52), 2m, scope("audit")), row(tagged(53), 3m, scope("audit"))])
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        var eachAop = new QuickSqlAop();

        eachAop.AuditValue = e =>
        {
            each++;
            Fill(e, stamp);
        };

        foreach (var one in new[] { row(tagged(54), 1m, scope("audit")), row(tagged(55), 2m, scope("audit")), row(tagged(56), 3m, scope("audit")) })
            await ormWith(eachAop).Insert(one).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        // 一条 3 行的 L3 与三条单行：填充次数都是 3 行 × 7 列。批量若"顺手少刷一次审计列"，
        // 差异只会体现在库里的值上，所以次数与值两句都要断。
        Assert.Equal(3 * 7, batch);
        Assert.Equal(3 * 7, each);

        var stored = Database.Rows(
            $"SELECT [CustomerName], {Millis("[CreatedAt]")} AS CreatedAt, {Millis("[PaidAt]")} AS PaidAt " +
            "FROM [dbo].[QuickSqlOrder] WHERE [Note] = @note",
            ("@note", scope("audit")));

        Assert.Equal(6, stored.Count);
        Assert.All(stored, r =>
        {
            Assert.Equal(stamp, DateTime.Parse(r["CreatedAt"], CultureInfo.InvariantCulture));
            Assert.Equal(stamp, DateTime.Parse(r["PaidAt"], CultureInfo.InvariantCulture));
        });
    }

    [Fact]
    public async Task The_row_update_chain_fills_its_audit_column_and_the_database_sees_the_filled_value()
    {
        Database.Ready();

        var stamp = new DateTime(2026, 7, 8, 9, 10, 11, 123);
        var note = scope("audit-update");
        var fired = 0;
        var kinds = new List<string>();
        var columns = new List<string>();
        var aop = new QuickSqlAop();

        // 种类是分派键（§10.1 的那句"一条订阅可以同时管创建列与修改列"在真库上的读数）：
        // "每次写都刷新"的那一列只在该 <c>update</c> 一词时补，而它补出来的值必须落进库里那一行。
        aop.AuditValue = e =>
        {
            fired++;
            kinds.Add(e.OperationKind);
            columns.Add(e.Column.MemberName);

            if (e.OperationKind == "update" && e.Column.MemberName == "PaidAt")
                e.SetValue(stamp);
        };

        // 落库走的是没有钩子的那只 orm：这一条用例量的只有更新那一侧，插入的读数已在上面那条结清。
        await Orm.InsertRange([row(tagged(81), 1m, note), row(tagged(82), 2m, note), row(tagged(83), 3m, note)])
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        var held = await Orm.Select<Order>()
            .Where(o => o.Note == note)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, held.Count);
        Assert.All(held, o => Assert.Null(o.PaidAt));

        // 一只链对象、三个出口（一份文本 + 一次执行 + 中间那份回放）：格子只算一次，
        // 所以次数仍是 3 行 × 7 个可写列，与上面那条插入用例同一个尺子。
        var bulk = Database.OrmWith(aop).UpdateRange(held);

        var plan = bulk.Explain();
        var replayAfterText = plan.HookReplay.Count;

        await bulk.ExecuteBulkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3 * 7, fired);
        Assert.All(kinds, kind => Assert.Equal("update", kind));

        // 主键在这一格是 WHERE 的判据而不是写出去的列，所以它不进格子（离线那条同名句子的真库对照）。
        Assert.DoesNotContain(nameof(Order.Id), columns);

        // 只有真被改写的那几格进回放，而回放排在发射段那些步之前（§10.1 硬规则 1 的顺序那一半）。
        Assert.Equal(3, replayAfterText);
        Assert.All(plan.HookReplay, trace => Assert.StartsWith("PaidAt = ", trace.Before));
        Assert.Equal([0, 1, 2], plan.HookReplay.Select(t => t.Step).ToArray());

        // SetValue 写穿实体：手里那三只的 PaidAt 已经变了，调用方不必回读就知道库里现在是什么。
        Assert.All(held, o => Assert.Equal(stamp, o.PaidAt));

        var stored = Database.Rows(
            $"SELECT [CustomerName], {Millis("[PaidAt]")} AS PaidAt FROM [dbo].[QuickSqlOrder] WHERE [Note] = @note",
            ("@note", note));

        Assert.Equal(3, stored.Count);
        Assert.All(stored, r => Assert.Equal(stamp, DateTime.Parse(r["PaidAt"], CultureInfo.InvariantCulture)));
    }

    // ── 事务三形态（§6 (1)(2)(3)） ──────────────────────────────────

    [Fact]
    public async Task Leaving_the_block_without_committing_rolls_the_row_back()
    {
        Database.Ready();

        var name = tagged(61);

        using (var tran = Orm.BeginTransaction())
        {
            Assert.Equal(1, await Orm.Insert(row(name, 1m, scope("tran"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

            // 同一条连接才看得见未提交的自己：读侧若走另一条连接，这一句就是 0，
            // 那说明"环境事务"是假的（§6 (3) 的自动 enlist）。
            Assert.Equal(1, await Orm.Select<Order>()
                .Where(o => o.CustomerName == name)
                .CountAsync(TestContext.Current.CancellationToken));

            Assert.False(tran.Completed);
        }

        Assert.Equal(0, storedOrders(name));
    }

    [Fact]
    public async Task A_committed_transaction_lands_the_row_and_survives_a_second_dispose()
    {
        Database.Ready();

        var name = tagged(62);
        var tran = Orm.BeginTransaction();

        Assert.Equal(1, await Orm.Insert(row(name, 2m, scope("tran"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        tran.Commit();

        Assert.True(tran.Completed);

        // Commit 之后再 Dispose 既不能回滚也不能抛：using 与显式 Commit 并存是常见写法。
        tran.Dispose();
        tran.Dispose();

        Assert.Equal(1, storedOrders(name));
    }

    [Fact]
    public async Task A_nested_transaction_uses_savepoint_and_only_rolls_back_inner_changes()
    {
        Database.Ready();

        var outerName = tagged(70);
        var innerRolledBack = tagged(71);
        var innerCommitted = tagged(72);

        using var outer = Orm.BeginTransaction();

        await Orm.Insert(row(outerName, 1m, scope("outer"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        using (var inner = Orm.BeginTransaction())
        {
            Assert.True(inner.IsSavepoint);

            await Orm.Insert(row(innerRolledBack, 2m, scope("inner"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

            inner.Rollback();
        }

        using (var inner2 = Orm.BeginTransaction())
        {
            Assert.True(inner2.IsSavepoint);

            await Orm.Insert(row(innerCommitted, 3m, scope("inner2"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

            inner2.Commit();
        }

        outer.Commit();

        Assert.Equal(1, storedOrders(outerName));
        Assert.Equal(0, storedOrders(innerRolledBack));
        Assert.Equal(1, storedOrders(innerCommitted));
    }

    /// <summary>保存点路径的隔离级别门禁（2026-11-01，规格 §14 那笔「level 传了不生效、无警告无门禁」的销账）：
    /// 嵌套（Requires）传与根不同的级别当场拒 —— SAVEPOINT 没有自己的隔离级别（继承外层是 SQL 标准），
    /// 此前是静默忽略、调用方读不出来。传 Unspecified（没有表态）或与根相同的级别照常放行；
    /// 拒的时候保存点没有发出去，外层事务照常可用。"要别的级别"的正当出口 RequiresNew
    /// 在上一颗测试里钉（新开一条独立事务，各开各的）。</summary>
    [Fact]
    public async Task A_savepoint_with_a_different_isolation_level_is_refused()
    {
        Database.Ready();

        var name = tagged(73);

        using (var outer = Orm.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            await Orm.Insert(row(name, 1m, scope("outer"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

            // 与根相同 → 放行，照旧是保存点。
            using (var same = Orm.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                Assert.True(same.IsSavepoint);
                same.Commit();
            }

            // 没有表态（Unspecified）→ 放行。
            using (var unspecified = Orm.BeginTransaction(IsolationLevel.Unspecified))
            {
                Assert.True(unspecified.IsSavepoint);
                unspecified.Commit();
            }

            // 与根不同 → 当场拒。同步口调用保证还在外层的帧里（环境槽位可见、走保存点路径）；
            // Task.Run 会换一帧，那一路走到的是 RequiresNew 根事务，不是这一格。
            Assert.Throws<QuickSqlException>(
                () => Orm.BeginTransaction(IsolationLevel.Serializable).Dispose());

            // 门禁没有弄坏外层：嵌套保存点照常能开，外层提交照常落库。
            using (var after = Orm.BeginTransaction())
            {
                Assert.True(after.IsSavepoint);
                after.Commit();
            }

            outer.Commit();
        }

        Assert.Equal(1, storedOrders(name));
    }

    [Fact]
    public async Task RequiresNew_suspends_ambient_and_creates_independent_transaction()
    {
        Database.Ready();

        // 家族闸门（§13.4 的"没有对应物才是 skip"）：sqlite 是单写者文件库——实测 journal_mode=delete、
        // busy_timeout=0（SqliteConcurrency 那两行 118/118b），外层写事务的 RESERVED 锁持到提交为止，
        // 内层"挂起 ambient 另开独立写事务"在第一句 BEGIN 就撞 SQLITE_BUSY（Error 5，红点一直在 BeginTransaction 本身）。
        // 这条裁判写给多连接并发事务那一族：sqlserver 照跑，xugu 照跑（T-07/T-99 量过它的事务分岔），本家带理由跳而不是删。
        Assert.SkipWhen(Database.Family == Database.Target.Sqlite,
            "sqlite 本家没有'两个同时打开的写事务'这一格：单写者模型下 RequiresNew 的 BEGIN 当场 SQLITE_BUSY。" +
            "缺的那一格要么按本家可表达的形状另写裁判（WAL/busy_timeout 的可行变体），要么记 §14 欠账——不是把这一句删掉。");

        var outerName = tagged(80);
        var innerName = tagged(81);

        using var outer = Orm.BeginTransaction();

        await Orm.Insert(row(outerName, 1m, scope("outer"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        using (var inner = Orm.BeginTransaction(IsolationLevel.ReadCommitted, TransactionPropagation.RequiresNew))
        {
            Assert.False(inner.IsSavepoint);

            await Orm.Insert(row(innerName, 2m, scope("inner"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

            inner.Commit();
        }

        outer.Rollback();

        Assert.Equal(0, storedOrders(outerName));
        Assert.Equal(1, storedOrders(innerName));
    }

    [Fact]
    public async Task The_structured_form_comes_back_as_a_result_object_and_the_rollback_is_not_a_mere_claim()
    {
        Database.Ready();

        var name = tagged(63);

        var failed = await Orm.ExecuteInTransactionAsync(
            async _ =>
            {
                await Orm.Insert(row(name, 3m, scope("tran"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

                throw new InvalidOperationException("业务判定不通过");
            },
            ct: TestContext.Current.CancellationToken);

        Assert.False(failed.Success);
        Assert.Contains("业务判定不通过", failed.ErrorMessage, StringComparison.Ordinal);
        // v0.39 第二笔旁路：业务异常是唯一一篇原文，回滚顺利时旁路空着。
        Assert.Null(failed.RollbackFailure);

        // Success=false 必须由库里真的没有这一行来背书：只看结果对象的话，"回滚失败但报告失败"也能绿。
        Assert.Equal(0, storedOrders(name));

        var committed = tagged(64);

        var ok = await Orm.ExecuteInTransactionAsync(
            async _ =>
            {
                await Orm.Insert(row(committed, 4m, scope("tran"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

                return 42;
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(ok.Success);
        Assert.Equal(42, ok.Value);
        Assert.Equal(1, storedOrders(committed));
    }

    [Fact]
    public async Task The_ambient_form_enlists_reads_and_an_explicit_rollback_need_not_be_followed_by_a_commit()
    {
        Database.Ready();

        var name = tagged(65);

        using (var ambient = Orm.BeginAmbientTransaction())
        {
            Assert.Equal(1, await Orm.Insert(row(name, 5m, scope("tran"))).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

            // 形态 (3) 与 (1) 是同一个机制，所以这里断的是"读侧也进来了"：查询出口不认环境槽位的话，
            // 这条语句会在另一条连接上执行，看不见未提交的行。
            Assert.Equal(1, await Orm.Select<Order>()
                .Where(o => o.CustomerName == name)
                .CountAsync(TestContext.Current.CancellationToken));

            ambient.Rollback();

            Assert.True(ambient.Completed);
        }

        // Rollback 之后出 using 块不能二次回滚也不能抛（Dispose 见已完成的槽位就直接返回）。
        Assert.Equal(0, storedOrders(name));
    }
}
