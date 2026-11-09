using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>P1 第一段（会话 + 跟踪 + <c>SaveChanges</c> 编排，§8）在<b>离线</b>能判的那一半。
/// 分界线跟写侧前两条链一样：<b>这一份里没有任何一句 SQL 真的上线</b>，全程 <see cref="SqlServer.NoDriverFactory"/> ——
/// 于是「状态机只改内存，只有 <c>SaveChanges</c> 那一档才碰驱动」这条设计约束是可执行断言：
/// 哪个用例把驱动探针碰响了而它本来只该判状态，就是状态机上混进了连接依赖（§3）。
///
/// <b>这里判不到的三件事都归集成层</b>（<c>TrackingPathTests</c>）：物化期的身份映射去重（要真行）、
/// 三档发出去的语句文本与顺序（要真连接）、回填键之后才登记得上身份那一串（§8.2 的 <c>OUTPUT</c>）。
/// 而 §13.2 第 2 项 ②「<c>orm.Select&lt;T&gt;()</c> 与 <c>db.Set&lt;T&gt;().AsNoTracking()</c> 的模型文本逐字节相等」
/// 正好只能在这里判：它问的是模型，不是库。</summary>
public sealed class TrackingTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    static DbContext session() => new(orm());

    static Order row(int id = 7, string? note = "原来的备注") => new()
    {
        Id = id,
        CustomerName = "甲",
        CustomerId = 3,
        Amount = 12.5m,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0),
        PaidAt = new DateTime(2026, 3, 2, 9, 0, 0),
        Note = note,
    };

    /// <summary>可写列的期望序：契约列序去掉主键（§10.3 的列序在这里第二次当判据用）。</summary>
    static readonly string[] Writable =
        ["CustomerName", "CustomerId", "Amount", "State", "CreatedAt", "PaidAt", "Note"];

    // ── §13.2 ②：跟踪与否不在模型里 ─────────────────────────────────

    [Fact]
    public void A_tracked_read_and_an_untracked_read_build_the_same_model()
    {
        var plain = orm().Select<Order>();
        var db = session();

        var viaSet = db.Set<Order>();
        var open = viaSet.Where(o => o.CustomerId == 3);
        var untracked = viaSet.AsNoTracking().Where(o => o.CustomerId == 3);

        Assert.Equal(plain.Explain().ModelText, viaSet.Explain().ModelText);
        Assert.Equal(plain.ToSql(), viaSet.ToSql());
        Assert.Equal(orm().Select<Order>().Where(o => o.CustomerId == 3).Explain().ModelText, open.Explain().ModelText);
        Assert.Equal(open.Explain().ModelText, untracked.Explain().ModelText);
        Assert.Equal(open.ToSql(), untracked.ToSql());

        // 标记位挪到引擎上（v0.16）之后，模型文本里连"跟踪"这个词都不该出现 —— 它以前是 "  NO TRACKING" 一行。
        // 留着那一行的话，上面的相等只是"两边都打了同一行"，而不是"模型里没有这一位"。
        Checks.Absent("TRACK", plain.Explain().ModelText);
    }

    [Fact]
    public void The_set_of_a_type_is_one_object_while_its_operators_are_new_ones()
    {
        var db = session();

        Assert.Same(db.Set<Order>(), db.Set<Order>());

        var open = db.Set<Order>().Where(o => o.Id > 0);

        Assert.NotSame(db.Set<Order>(), open);
        Assert.False(open is DbSet<Order>, "链式算子交出的是 IQuery<Order>：会话入口只到 Set<T>() 为止，往下每一步都是新值（§5.11）。");
    }

    // ── 身份映射与建档 ───────────────────────────────────────────────

    [Fact]
    public void Same_key_twice_in_one_session_raises_on_the_second_instance()
    {
        var db = session();

        db.Attach(row(7));

        var attach = Checks.Raised<QuickSqlException>(() => db.Attach(row(7, "后来的")));

        Checks.Has("Attach 不会替换会话里的那一只", attach.Message);
        Assert.Equal(1, db.ChangeTracker.Count);

        var add = Checks.Raised<QuickSqlException>(() => db.Add(row(7, "新增的")));

        Checks.Has("同键两只实例", add.Message);
        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void Two_sessions_never_share_an_identity_map()
    {
        var first = session();
        var second = session();

        first.Attach(row(7));
        second.Attach(row(7));

        Assert.Equal(1, first.ChangeTracker.Count);
        Assert.Equal(1, second.ChangeTracker.Count);
    }

    [Fact]
    public void Adding_the_same_instance_twice_is_one_entry_not_a_collision()
    {
        var db = session();
        var order = row(7);

        db.Add(order);
        db.Add(order);

        Assert.Equal(1, db.ChangeTracker.Count);
        Assert.Equal(EntityState.Added, db.Entry(order).State);
    }

    [Fact]
    public void A_row_whose_key_has_no_value_is_in_the_session_but_invisible_to_Find()
    {
        var db = session();
        var order = new Order { CustomerName = "还没有键" };

        db.Set<Order>().Add(order);

        Assert.Equal(1, db.ChangeTracker.Count);
        Assert.Same(order, Assert.Single(db.Set<Order>().Local));
        Assert.Same(order, db.Set<Order>().Local.Entries[0]);

        // 自增列的种子不是 0，所以 0 一律当"没填"（§8.2）：它不进映射，因此按 0 查不到，
        // 而它确实在会话里 —— 那一句欠着的 INSERT 说的就是它。
        Assert.Null(db.Set<Order>().Local.Find(0));
    }

    [Fact]
    public void A_guid_key_of_all_zeroes_counts_as_no_key_while_a_real_one_does_not()
    {
        var db = session();

        Checks.Has("主键现在没有值", Checks.Raised<QuickSqlException>(() => db.Attach(new SessionToken())).Message);
        Assert.Equal(0, db.ChangeTracker.Count);

        var token = new SessionToken { Id = Guid.Parse("8f4d3c2b-1a09-4736-2518-09abcdef0123"), Value = "v" };

        db.Attach(token);

        Assert.Same(token, db.Set<SessionToken>().Local.Find(token.Id));
    }

    [Fact]
    public void Attaching_a_row_with_no_key_refuses_and_leaves_the_session_alone()
    {
        var db = session();

        Checks.Has("主键现在没有值", Checks.Raised<QuickSqlException>(() => db.Attach(new Order { CustomerName = "没有键" })).Message);
        Assert.Equal(0, db.ChangeTracker.Count);
    }

    [Fact]
    public void A_table_without_a_primary_key_refuses_to_be_tracked_and_says_why()
    {
        var db = session();

        var failure = Checks.Raised<QuickSqlException>(() => db.Set<AuditRow>().Attach(new AuditRow { Actor = "系统", At = new DateTime(2026, 3, 1) }));

        // §8.2 原稿那句「无 PK 自动不跟踪」今天的形态：拒绝并指路，不是静默不跟（§12.2）。
        Checks.Has("没有主键", failure.Message);
        Checks.Has("orm.Select<AuditRow>()", failure.Message);
        Assert.Equal(0, db.ChangeTracker.Count);

        // 同一张表在读路径上完全正常：主键只被身份映射与 WHERE 需要。
        Assert.Contains("[Actor]", orm().Select<AuditRow>().ToSql(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_derived_instance_taken_through_the_base_bucket_uses_the_base_contract()
    {
        var db = session();
        var rush = new RushOrder { Id = 11, CustomerName = "加急" };

        db.Set<Order>().Add(rush);

        // 键是调用点上的静态类型，不是 GetType()：这只实例写出去的是基类那张表的列，所以入口也在基类那一档。
        Assert.Equal(EntityState.Added, db.Set<Order>().Entry(rush).State);
        Assert.Equal(typeof(Order), db.Set<Order>().Entry(rush).EntityType);
        Assert.Same(rush, Assert.Single(db.Set<Order>().Local));
        Assert.Empty(db.ChangeTracker.Entries<RushOrder>());

        // db.Entry(rush) 的 T 推出来是 RushOrder —— 那一档压根没建过。拒绝可以，但话不能说错：
        // 指人去 Attach 一只"从来没进过会话"的实例，会让调用方去找一条不存在的双跟踪 bug。
        var failure = Checks.Raised<QuickSqlException>(() => db.Entry(rush));

        Checks.Has("这个实例是按 Order 纳入会话的", failure.Message);
        Assert.Equal(1, db.ChangeTracker.Count);
    }

    // ── 脏列合成 ─────────────────────────────────────────────────────

    [Fact]
    public void A_field_change_becomes_exactly_one_dirty_column()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);

        Assert.Equal(0, db.ChangeTracker.DetectChanges());
        Assert.False(db.ChangeTracker.HasChanges);

        order.Note = "只改了备注";

        Assert.Equal(1, db.ChangeTracker.DetectChanges());
        Assert.Equal(["Note"], db.Entry(order).ModifiedMembers);
        Assert.True(db.Entry(order).IsModified);
        Assert.Equal(EntityState.Modified, db.Entry(order).State);
        Assert.True(db.ChangeTracker.HasChanges);

        // 第二次没有东西可动了：返回 0 是"这一轮没有任何状态迁移"，不是"没写"。
        Assert.Equal(0, db.ChangeTracker.DetectChanges());
    }

    [Fact]
    public void A_change_reverted_before_saving_leaves_no_debt()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);

        order.Note = "改了又改主意";
        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        order.Note = "原来的备注";
        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        Assert.Equal(EntityState.Unmodified, db.Entry(order).State);
        Assert.Empty(db.Entry(order).ModifiedMembers);
        Assert.False(db.Entry(order).IsModified);
        Assert.False(db.ChangeTracker.HasChanges);
    }

    [Fact]
    public void Turning_off_auto_detection_hides_a_change_until_someone_asks()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);
        db.AutoDetectChangesEnabled = false;

        order.Note = "没人比过";

        Assert.False(db.ChangeTracker.HasChanges);
        Assert.False(db.Entry(order).IsModified);

        Assert.Equal(1, db.ChangeTracker.DetectChanges());
        Assert.True(db.ChangeTracker.HasChanges);
        Assert.True(db.Entry(order).IsModified);
    }

    [Fact]
    public void A_forced_column_enters_the_statement_even_when_the_value_matches_the_snapshot()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);

        db.Entry(order).Property(o => o.Note).IsModified = true;

        // 状态还停在 Unmodified（要等下一次侦测才推上去），而"这一行欠一条语句"已经是事实：
        // 判据只看状态的话，关掉自动侦测的 `if (HasChanges) SaveChanges()` 会把这次强制写入静默跳过。
        Assert.Equal(EntityState.Unmodified, db.Entry(order).State);
        Assert.True(db.Entry(order).IsModified);
        Assert.True(db.ChangeTracker.HasChanges);
        Assert.Equal(["Note"], db.Entry(order).ModifiedMembers);

        db.ChangeTracker.DetectChanges();

        Assert.Equal(EntityState.Modified, db.Entry(order).State);
    }

    [Fact]
    public void A_column_forced_clean_stays_out_even_after_comparison_found_it()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);
        order.Note = "改了但不写";

        db.Entry(order).Property(o => o.Note).IsModified = false;
        db.ChangeTracker.DetectChanges();

        var note = db.Entry(order).Property(o => o.Note);

        Assert.False(note.IsModified);
        Assert.Equal("原来的备注", note.OriginalValue);
        Assert.Equal("改了但不写", note.CurrentValue);
        Assert.Empty(db.Entry(order).ModifiedMembers);
        Assert.False(db.ChangeTracker.HasChanges);
    }

    [Fact]
    public void Writing_CurrentValue_changes_the_entity_and_leaves_the_snapshot_alone()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);

        db.Entry(order).Property(o => o.CustomerName).CurrentValue = "乙";

        Assert.Equal("乙", order.CustomerName);
        Assert.Equal("甲", db.Entry(order).Property(o => o.CustomerName).OriginalValue);

        db.ChangeTracker.DetectChanges();

        Assert.Equal(["CustomerName"], db.Entry(order).ModifiedMembers);
        Assert.True(db.ChangeTracker.HasChanges);
    }

    [Fact]
    public void Marking_the_row_modified_lands_every_writable_column_but_the_key()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);
        db.Entry(order).State = EntityState.Modified;

        Assert.Equal(Writable, db.Entry(order).ModifiedMembers);

        // 主键与自增列都不在里面：那两列进 SET 的语义不是"写这一列"（拒绝的话写在 Unwritable 那里）。
        db.Attach(row(12));
        var identity = db.ChangeTracker.Entries<Order>().Last();

        identity.State = EntityState.Modified;

        Assert.DoesNotContain("Id", identity.ModifiedMembers);
    }

    [Fact]
    public void A_dirty_primary_key_never_reaches_the_column_list()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);
        order.Id = 8;

        Assert.Equal(1, db.ChangeTracker.DetectChanges());
        Assert.Empty(db.Entry(order).ModifiedMembers);

        // v0.18 起这一句是真。判据从"有没有列进得了 SET"换成了"有没有任何一列被判定改过"：
        // 按旧的答法这一行算干净，而 SaveChanges 先看这一句决定要不要开事务 —— 于是那句"主键进不了 SET"的拒绝
        // 永远走不到，「改了主键保存三次没生效」从一个查得出来的 bug 变成一次静默丢弃。
        // 两格各答各的：会写出去的列由上面那一格回答（它仍然是空的），这一格回答的是"这一行欠着一次处理"。
        Assert.True(db.Entry(order).IsModified);
        Assert.True(db.ChangeTracker.HasChanges);

        // 摘与查都按当初登记的那个键，不按当前值：否则改过主键的实例会把别人的那一条摘掉。
        Assert.Same(order, db.Set<Order>().Local.Find(7));
        Assert.Null(db.Set<Order>().Local.Find(8));
    }

    [Fact]
    public void A_navigation_or_unmapped_member_has_no_dirty_slot_to_toggle()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);

        Checks.Has("没有名为 Customer 的列", Checks.Raised<QuickSqlException>(() => db.Entry(order).Property("Customer")).Message);
        Checks.Has("没有名为 CustmerName 的列", Checks.Raised<QuickSqlException>(() => db.Entry(order).Property("CustmerName")).Message);

        // 收 lambda 的那一条在编译期就把笔误挡住了（o => o.CustmerName 编译不过），所以这一格只要名字对得上。
        Assert.Equal("Note", db.Entry(order).Property(o => o.Note).MemberName);
    }

    // ── 状态机的边角 ─────────────────────────────────────────────────

    [Fact]
    public void Update_marks_every_column_and_refuses_to_retarget_an_added_row()
    {
        var db = session();

        db.Update(row(7));

        Assert.Equal(Writable, db.ChangeTracker.Entries<Order>().Single().ModifiedMembers);

        var fresh = new Order { CustomerName = "还没进库" };

        db.Add(fresh);

        Checks.Has("已经在会话里被标为新增", Checks.Raised<QuickSqlException>(() => db.Update(fresh)).Message);
    }

    [Fact]
    public void Marking_a_row_the_session_thinks_is_in_the_database_as_added_raises()
    {
        var db = session();

        db.Attach(row(7));

        Checks.Has("已经被当成库里已有的一行", Checks.Raised<QuickSqlException>(() => db.ChangeTracker.Entries<Order>().Single().State = EntityState.Added).Message);
    }

    [Fact]
    public void Removing_an_added_row_forgets_it_instead_of_deleting_it()
    {
        var db = session();
        var fresh = new Order { CustomerName = "还没进库" };

        db.Add(fresh);
        var entry = db.Entry(fresh);

        db.Remove(fresh);

        // 库里没有这一行，删它的正确执行方式是忘掉它。此刻手里那只入口看到的是 Detached，
        // 而会话不再欠它一句 DELETE —— 那才是这条的判据。
        Assert.Equal(EntityState.Detached, entry.State);
        Assert.Equal(0, db.ChangeTracker.Count);
        Assert.Empty(db.Set<Order>().Local);
        Assert.False(db.ChangeTracker.HasChanges);
        Checks.Has("不在会话里", Checks.Raised<QuickSqlException>(() => db.Entry(fresh)).Message);
    }

    [Fact]
    public void A_removed_row_stays_in_the_session_until_the_statement_goes_out()
    {
        var db = session();
        var order = row(7);

        // 级联删除看得见子行才动手：这一格写空数组是"我看过这张单的明细，确实没有"的一句声明。
        // 留 null 会被判成"还没捞出来"而拒（裁决①），那是另一条用例的形状。
        order.Items = [];

        db.Attach(order);
        db.Remove(order);

        Assert.Equal(EntityState.Deleted, db.Entry(order).State);
        Assert.True(db.Entry(order).IsModified);
        Assert.Same(order, Assert.Single(db.Set<Order>().Local));
        Assert.True(db.ChangeTracker.HasChanges);
    }

    [Fact]
    public void Detached_is_the_state_a_stale_entry_reports_after_the_session_is_cleared()
    {
        var db = session();
        var order = row(7);

        db.Attach(order);
        order.Note = "还没写出去";

        var entry = db.Entry(order);

        db.ChangeTracker.Clear();

        Assert.Equal(0, db.ChangeTracker.Count);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(EntityState.Detached, entry.State);
        Assert.False(entry.IsModified);

        // 清的是数据、留的是档：Set<T>() 还是同一只对象，会话照样能用。
        Assert.Same(db.Set<Order>(), db.Set<Order>());

        db.Add(row(21));

        Assert.Equal(1, db.ChangeTracker.Count);
    }

    [Fact]
    public void Entries_reports_each_bucket_in_first_seen_order()
    {
        var db = session();

        db.Set<Order>().Attach(row(7));
        db.Set<Customer>().Attach(new Customer { Id = 3, Name = "甲" });

        Assert.Equal([typeof(Order), typeof(Customer)], db.ChangeTracker.Entries().Select(static e => e.EntityType).ToArray());
        Assert.Same(db.ChangeTracker.Entries().First().Entity, db.ChangeTracker.Entries<Order>().Single().Entity);
    }

    [Fact]
    public void Load_does_not_throw_when_no_entities_are_tracked()
    {
        var db = session();

        var result = db.Set<Order>().Load(o => o.Items);

        Assert.Same(db.Set<Order>(), result);
    }

    [Fact]
    public void A_derived_context_sees_the_session_the_orm_handed_it()
    {
        var db = orm().CreateDbContext<OrderSession>();

        Assert.Same(db.Orders, db.Set<Order>());

        var order = row(7);

        db.Orders.Add(order);

        Assert.Same(order, Assert.Single(db.ChangeTracker.Entries<Order>()).Entity);
        Assert.Contains(order, db.Orders.Local);
    }

    [Fact]
    public void InsertRaw_writes_the_database_without_telling_the_session()
    {
        var db = session();

        var sql = db.InsertRaw(row(0)).ToSql();

        Assert.Equal(0, db.ChangeTracker.Count);
        Checks.Has("INSERT INTO [QuickSqlOrder]", sql);
    }

    [Fact]
    public void Saving_a_session_that_owes_no_statement_never_reaches_the_driver()
    {
        var db = session();

        // 只建过快照、没欠语句的会话：SaveChanges 连事务都不该开，所以探针一次都不该响（§6）。
        db.Attach(row(7));

        var quiet = Record.Exception(() => db.SaveChanges());

        Assert.Null(quiet);

        // 级联删除要先看得见子行（裁决①）：这一格同样是"没有明细"的声明，不是留 null 让会话替它猜。
        var stale = row(21);
        stale.Items = [];

        db.Remove(stale);

        // 探针的响法在这里被加载层吞了一次：SqlServerConnectorFactory.TryEnsureLoaded 探测连接时 catch 一切，
        // 那句"不该碰驱动"于是以 detail 的身份出现在 DriverNotAvailableException 里。断言看消息，不看类型。
        var touched = Checks.Raised<DriverNotAvailableException>(() => db.SaveChanges());

        Checks.Has("只生成 SQL 的测试不该碰驱动", touched.Message);
    }

    /// <summary>派生实例：它<em>不是</em>一张自己的表，所以按基类纳入会话时写出去的就是基类那些列
    /// （派生类型各占一份契约要等继承映射，P2/P3）。</summary>
    sealed class RushOrder : Order;

    /// <summary>§8.1 那个派生会话的等价写法：表达式体属性 + <c>orm.CreateDbContext&lt;&gt;()</c> 认的这个构造器形状。</summary>
    public sealed class OrderSession(QuickSqlOrm orm) : DbContext(orm)
    {
        public DbSet<Order> Orders => Set<Order>();

        public DbSet<Customer> Customers => Set<Customer>();
    }
}
