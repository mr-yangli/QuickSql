using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>级联插入与级联删除（§8.2 写侧的前半段）在<b>离线</b>能判的那一半。
/// 判的是「谁被带进会话、边怎么解算、捞不到与推不出各怎么响、开关落在哪一格」—— 这四件事都不需要一句 SQL 上线。
/// 落库那一半（明细真的带上父回填的键、删除真的按逆序发语句）在 <c>CascadePathTests</c>：
/// 键的回填要 <c>OUTPUT</c>，而 <see cref="SqlServer.NoDriverFactory"/> 一被碰到就抛。
///
/// <b>与 <c>TrackingTests</c> 共用同一份离线组装根</b>（<c>SqlServer.Orm()</c>），所以这里的红和那里的红
/// 是同一把尺子量的。</summary>
public sealed class CascadeTests
{
    static DbContext session() => new(SqlServer.Orm());

    static Order order(int id, params OrderItem[] items) => new()
    {
        Id = id,
        CustomerName = "甲",
        CustomerId = 3,
        Amount = 12.5m,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0),
        Items = [.. items],
    };

    /// <summary>还没进过库的明细：主键空着，等着级联把父的键落到 <c>OrderId</c> 上。</summary>
    static OrderItem fresh(string sku) => new() { Sku = sku, Quantity = 1, Price = 2m };

    /// <summary>库里已有的一行（有主键）：<c>Include</c>/<c>Load</c> 缝进父手里的那一种形状。</summary>
    static OrderItem stored(int id, int orderId, string sku) => new() { Id = id, OrderId = orderId, Sku = sku, Quantity = 1, Price = 2m };

    static int Count(DbContext db, EntityState state) => db.ChangeTracker.Entries().Count(e => e.State == state);

    // ── 级联插入：Add 顺集合边把人带进会话 ─────────────────────────

    [Fact]
    public void Adding_the_parent_brings_its_children_into_the_session()
    {
        var db = session();
        var a = fresh("A");
        var b = fresh("B");

        db.Add(order(7, a, b));

        // 旗舰形状：一句 Add 之后会话里有三只（1 父 + 2 子），而不是只看得见父。
        Assert.Equal(3, db.ChangeTracker.Count);
        Assert.Equal(3, Count(db, EntityState.Added));

        // 键还没落：级联的键要等父那一句 INSERT 回填之后才落进子行 —— 离线判到这一步为止。
        Assert.Equal(0, a.OrderId);
        Assert.Equal(0, b.OrderId);
    }

    [Fact]
    public void A_null_collection_adds_nothing_and_says_nothing()
    {
        var db = session();

        // 插入档的 null = "手里没有要带的人"，与删除档那句"捞不到就拒"是两条相反的判据（裁决①的另一半）。
        db.Add(new Order { Id = 7, CustomerName = "甲", CustomerId = 3, Amount = 1m, CreatedAt = new DateTime(2026, 3, 1) });

        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void An_already_tracked_child_is_not_brought_in_twice()
    {
        var db = session();
        var a = fresh("A");

        db.Add(a);
        db.Add(order(7, a, fresh("B")));

        // 递归的终止判据是「只有新登记才往下走」，不是深度上限 —— 父子各有一根导航（图里有环）时也是它收敛。
        Assert.Equal(3, db.ChangeTracker.Count);
        Assert.Equal(3, Count(db, EntityState.Added));
    }

    [Fact]
    public void The_temporary_key_of_the_parent_is_not_written_into_the_child()
    {
        var db = session();

        // 父的键也要等库给（整型自增主键 ⇒ Add 发临时键 Tp）。级联不许把那颗负数落进子行的外键 ——
        // 落键那一步在 SaveChanges 里、父写出去之后才做，离线判的是"此刻子行手里还是空的"。
        var parent = order(0, fresh("A"));
        var child = parent.Items![0];

        db.Add(parent);

        Assert.Equal(0, child.OrderId);
    }

    // ── 边怎么解算：三档，推不出就响 ───────────────────────────────

    [Fact]
    public void A_declared_foreign_key_member_resolves_when_convention_misses()
    {
        var db = session();

        // 子端那一列叫 RackKey 而不是 RackId：第一档（子端回指的引用导航）与第三档（约定名）都命不中，
        // 靠集合导航上 [ForeignKey("RackKey")] 那一档接上。
        db.Add(new Rack { Id = 1, Slots = [new ShelfSlot { Bin = "a" }] });

        Assert.Equal(2, db.ChangeTracker.Count);
    }

    [Fact]
    public void An_edge_that_no_tier_can_resolve_is_refused_where_cascade_needs_it()
    {
        var db = session();

        // 契约建得出来（那张图确实有一根集合边），但子端没有任何列能指回父。
        // 响在 Add 那一刻 —— 级联需要这颗键的正是这一格（裁决④：不静默不级联，也不留到 SaveChanges 才说）。
        var raised = Checks.Raised<QuickSqlException>(() =>
            db.Add(new Folder { Id = 1, Papers = [new Paper { Title = "草稿" }] }));

        Checks.Has("三档都命不中", raised.Message);
        Checks.Has("Paper", raised.Message);
        Checks.Has("[ForeignKey", raised.Message);
        Checks.Has("[NoCascade]", raised.Message);
    }

    [Fact]
    public void A_composite_parent_key_against_a_single_foreign_key_column_is_refused()
    {
        var db = session();

        var raised = Checks.Raised<QuickSqlException>(() =>
            db.Add(new Ledger { Book = 1, Line = 2, Notes = [new LedgerNote { Memo = "批注" }] }));

        // 复合主键要逐列点名：一列对两键，级联不替它猜"哪个键进哪一列"（猜错就是一个库里不属于任何父行的键）。
        Checks.Has("基数", raised.Message);
    }

    // ── 开关：默认开 + 注解可关（裁决②：没有会话级开关）────────────

    [Fact]
    public void NoCascade_on_the_navigation_keeps_the_children_out()
    {
        var db = session();

        db.Add(new Board { Id = 1, Title = "看板", Cards = [new Card { Text = "卡片" }] });

        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void NoCascade_on_the_entity_keeps_every_navigation_out()
    {
        var db = session();

        db.Add(new Crate { Id = 1, Parcels = [new Parcel(), new Parcel()] });

        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void The_contract_records_which_navigation_opted_out()
    {
        var schemas = SqlServer.Orm().Schemas;

        // 开关落在契约上而不是会话上，所以两侧元数据带着同一份结论（§13.11 的逐字段相等也在那里比这一格）。
        Assert.False(schemas.Contract<Board>().Collections.Single().Cascade);
        Assert.False(schemas.Contract<Crate>().Collections.Single().Cascade);
        Assert.True(schemas.Contract<Order>().Collections.Single().Cascade);
    }

    // ── 级联删除：可达才删，捞不到就拒 ─────────────────────────────

    [Fact]
    public void Removing_the_parent_marks_the_reachable_children_deleted()
    {
        var db = session();
        var bill = order(7, stored(11, 7, "A"), stored(12, 7, "B"));

        db.Attach(bill);
        db.Remove(bill);

        Assert.Equal(3, Count(db, EntityState.Deleted));
    }

    [Fact]
    public void Removing_a_parent_whose_children_were_never_loaded_is_refused()
    {
        var db = session();

        db.Attach(new Order { Id = 7, CustomerName = "甲", CustomerId = 3, Amount = 1m, CreatedAt = new DateTime(2026, 3, 1) });

        var raised = Checks.Raised<QuickSqlException>(() => db.Remove(db.Set<Order>().Local.Single()));

        // 消息要说得出路（§5.8 那条纪律）：Include 那一根，或把明细 Load 进会话。
        Checks.Has("可达才删", raised.Message);
        Checks.Has("Include", raised.Message);
        Checks.Has("Load", raised.Message);
    }

    [Fact]
    public void An_empty_collection_is_a_claim_that_there_are_no_children()
    {
        var db = session();
        var bill = order(7);

        db.Attach(bill);
        db.Remove(bill);

        // 空集合不是 null：那是"这一串我看过，确实没有"的一句声明。声明错了由库里那条外键约束响，
        // 不替它在这里编一份账，也不静默去库里补捞。
        Assert.Equal(EntityState.Deleted, db.Entry(bill).State);
        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void NoCascade_on_the_navigation_means_removal_does_not_reach_the_children()
    {
        var db = session();
        var board = new Board { Id = 1, Title = "看板", Cards = [new Card { Id = 5, Text = "卡片" }] };

        db.Attach(board);

        // 关了级联就连"捞不到就拒"都不问：这一根导航本来就不带人，null 与有人都不关会话的事。
        var quiet = Record.Exception(() => db.Remove(board));

        Assert.Null(quiet);
        Assert.Equal(1, Count(db, EntityState.Deleted));
    }

    // ── 摘行：从集合里摘掉一只，就是要删那一行（裁决③）──────────────

    [Fact]
    public void Detaching_a_child_from_a_tracked_collection_marks_that_row_deleted()
    {
        var db = session();
        var kept = stored(11, 7, "A");
        var moved = stored(12, 7, "B");
        var bill = order(7, kept, moved);

        db.Attach(bill);
        bill.Items!.Remove(moved);

        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        // 摘掉的那一只进会话、并且是删除档；留着的那一只<em>不</em>被顺手带进来（会话对它没有账）。
        Assert.Equal(EntityState.Deleted, db.Entry(moved).State);
        Assert.Equal(2, db.ChangeTracker.Count);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => ReferenceEquals(e.Entity, kept));
    }

    [Fact]
    public void The_next_detect_round_does_not_delete_the_same_row_twice()
    {
        var db = session();
        var moved = stored(12, 7, "B");
        var bill = order(7, stored(11, 7, "A"), moved);

        db.Attach(bill);
        bill.Items!.Remove(moved);

        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        // 基线跟着那一串往前走。不往前走的话，每侦测一轮就"发现"一次同一只被摘掉，
        // 而重复标删在这里是同一句 DELETE 发两遍的前置（SaveChanges 的判脏也走这一颗口）。
        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.Equal(1, Count(db, EntityState.Deleted));
    }

    // ── 到位:往集合里新添一只,就是要插那一行(裁决③的镜像)──────────────

    [Fact]
    public void Adding_a_child_to_a_tracked_collection_marks_that_row_inserted()
    {
        var db = session();
        var kept = stored(11, 7, "A");
        var bill = order(7, kept);

        db.Attach(bill);
        bill.Items!.Add(stored(0, 7, "N"));

        // 新到的那一整只(不是成员集合的差,而是实例)进会话、并且是插入档;
        // 原来就在基线里的 kept 不被顺手判成任何档。
        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        var added = db.ChangeTracker.Entries().Single(e => e.State == EntityState.Added);

        Assert.Equal("N", ((OrderItem) added.Entity).Sku);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => ReferenceEquals(e.Entity, kept) && e.State != EntityState.Unmodified);
    }

    [Fact]
    public void An_already_tracked_child_added_to_the_collection_is_not_arrived_twice()
    {
        var db = session();
        var kept = stored(11, 7, "A");
        var bill = order(7, kept);

        // kept 已被物化路径带进会话(这里用 Attach 模拟),再挂进集合不算到位:
        // 第三道筛(会话还没跟过)把它挡下 —— Add 一遍等于替调用方改主意。
        db.Attach(kept);
        db.Attach(bill);
        bill.Items!.Add(kept);

        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State == EntityState.Added);
    }

    [Fact]
    public void The_next_detect_round_does_not_insert_the_same_row_twice()
    {
        var db = session();
        var bill = order(7, stored(11, 7, "A"));

        db.Attach(bill);
        bill.Items!.Add(new OrderItem { Id = 0, OrderId = 7, Sku = "N", Quantity = 1, Price = 2m });

        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        // 基线跟着那一串往前走(与摘行同一颗纪律):第二轮不重复发现同一只新到。
        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.Equal(1, Count(db, EntityState.Added));
    }

    [Fact]
    public void A_child_that_was_in_the_collection_before_the_parent_got_tracked_is_not_arrived()
    {
        var db = session();
        var existing = stored(12, 7, "B");
        var bill = order(7, stored(11, 7, "A"), existing);

        // 会话第一次看见这一串之前它就在里面:基线里有它,不算新到 ——
        // 这是裁决①在到位档的镜像,看不见的不判,判得着的才动手。
        db.Attach(bill);

        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State == EntityState.Added);
    }

    [Fact]
    public void A_child_that_was_pulled_out_before_the_parent_got_tracked_deletes_nothing()
    {
        var db = session();
        var bill = order(7, stored(11, 7, "A"), stored(12, 7, "B"));

        // 会话第一次看见这一串之前就先摘掉：它从没见过 B，也就说不出"刚被摘掉的是 B"。
        // 这不是漏判，是裁决①的另一面 —— 看不见的不判，判得着的才动手。
        bill.Items!.RemoveAt(1);

        db.Attach(bill);

        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void An_unsaved_child_pulled_out_of_the_collection_only_leaves_the_session()
    {
        var db = session();
        var a = fresh("A");
        var b = fresh("B");
        var bill = order(0, a, b);

        db.Add(bill);

        Assert.Equal(3, db.ChangeTracker.Count);

        bill.Items!.Remove(b);

        db.ChangeTracker.DetectChanges();

        // 库里没有那一行 ⇒ 没有 DELETE 可发：走的是跟踪器那句"标删一只还没写出去的新增行就是忘掉它"。
        Assert.Equal(2, db.ChangeTracker.Count);
        Assert.Equal(2, Count(db, EntityState.Added));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => ReferenceEquals(e.Entity, b));
    }

    [Fact]
    public void A_detached_child_takes_its_own_children_with_it()
    {
        var db = session();
        var hook1 = new SlotHook { Id = 91, SlotId = 5, Name = "钩一" };
        var hook2 = new SlotHook { Id = 92, SlotId = 5, Name = "钩二" };
        var slot = new ShelfSlot { Id = 5, RackKey = 1, Bin = "a", Hooks = [hook1, hook2] };
        var rack = new Rack { Id = 1, Label = "架", Slots = [slot] };

        db.Attach(rack);
        rack.Slots!.Remove(slot);

        db.ChangeTracker.DetectChanges();

        // 摘掉一只走的是级联删除那同一条路（同一个 tracker.Remove），所以第三层一起标删 ——
        // 两层模型判不出这一格，那条链在第二层就断了。
        Assert.Equal(3, Count(db, EntityState.Deleted));
        Assert.Equal(EntityState.Deleted, db.Entry(hook1).State);
    }

    [Fact]
    public void Detaching_a_child_that_never_had_its_own_children_loaded_is_refused()
    {
        var db = session();
        var slot = new ShelfSlot { Id = 5, RackKey = 1, Bin = "a" };
        var rack = new Rack { Id = 1, Label = "架", Slots = [slot] };

        db.Attach(rack);
        rack.Slots!.Remove(slot);

        // 摘掉一只与"父被删"走的是同一条路，所以裁决①那一格跟着来：这只位自己手里那一串钩从没捞过，
        // 会话就不能替它判「没有要删的钩」—— 静默放行留下的是一串挂在已删位上的孤儿行。
        var raised = Checks.Raised<QuickSqlException>(() => db.ChangeTracker.DetectChanges());

        Checks.Has("可达才删", raised.Message);
        Checks.Has("Load", raised.Message);
    }

    [Fact]
    public void A_child_handed_to_another_parent_is_refused_rather_than_deleted()
    {
        var db = session();
        var moved = stored(12, 7, "B");
        var left = order(7, stored(11, 7, "A"), moved);
        var right = order(8, stored(21, 8, "C"));

        db.Attach(left);
        db.Attach(right);

        // 从甲那一串摘掉、同时进乙那一串：那是<em>改父</em>，要写的是一句 SET 外键（那一档没接线）。
        // 这里若按"摘掉就删"办，就是把一行搬走变成了删掉 —— 那种形状只有对账时才现形。
        left.Items!.Remove(moved);
        right.Items!.Add(moved);

        var raised = Checks.Raised<QuickSqlException>(() => db.ChangeTracker.DetectChanges());

        Checks.Has("改父", raised.Message);
        Checks.Has("SET 外键", raised.Message);
        Assert.Equal(0, Count(db, EntityState.Deleted));
    }

    [Fact]
    public void Replacing_the_collection_with_null_withdraws_the_claim()
    {
        var db = session();
        var bill = order(7, stored(11, 7, "A"), stored(12, 7, "B"));

        db.Attach(bill);
        bill.Items = null;

        // null 不是"清空了"，是"这一串我不知道"：那句收回去了，会话就不替它判缺席（裁决①同一颗分岔）。
        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.Equal(0, Count(db, EntityState.Deleted));

        bill.Items = [stored(13, 7, "C")];

        // 换成 null 之后再递一串新的：那一格重新按"没见过"起步，不拿旧基线去判新串里少了谁。
        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.Equal(0, Count(db, EntityState.Deleted));
    }

    [Fact]
    public void NoCascade_on_the_navigation_detaches_nothing()
    {
        var db = session();
        var card = new Card { Id = 5, BoardId = 1, Text = "卡片" };
        var board = new Board { Id = 1, Title = "看板", Cards = [card] };

        db.Attach(board);
        board.Cards!.Remove(card);

        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void A_detached_child_makes_a_save_due()
    {
        var db = session();
        var moved = stored(12, 7, "B");
        var bill = order(7, stored(11, 7, "A"), moved);

        db.Attach(bill);
        bill.Items!.Remove(moved);

        // 摘行不是只改内存：它要让这一批"欠一句语句"，否则 SaveChanges 会走那条零语句的短路直接交空报告。
        Assert.True(db.ChangeTracker.HasChanges);

        var raised = Checks.Raised<DriverNotAvailableException>(() => db.SaveChanges());

        Checks.Absent("排不出", raised.Message);
    }

    // ── 写序：集合边也要进拓扑 ─────────────────────────────────────

    [Fact]
    public void A_collection_edge_orders_the_child_type_after_its_parent()
    {
        var db = session();

        // 明细先 Add、父后 Add（首次出现序是子在前）。会话必须按集合边把父那一档排到前面，
        // 否则级联的键落不下去。排不出顺序会在开事务之前抛"排不出写入先后"，
        // 所以这里判的是：走到驱动跟前（DriverNotAvailable），而不是走到拓扑那一步就响。
        db.Add(fresh("A"));
        db.Add(order(7));

        var raised = Checks.Raised<DriverNotAvailableException>(() => db.SaveChanges());

        Checks.Absent("排不出", raised.Message);
    }
}
