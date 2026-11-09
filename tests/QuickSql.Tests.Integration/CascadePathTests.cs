using System.Globalization;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>级联插入与级联删除（§8.2 写侧）判在真库上。
///
/// <para><b>为什么这一半只能在库里看</b>：级联落的那颗键来自 <c>INSERT</c> 的回填（§12.4），
/// 而回填只有库会给 —— 离线的 <c>CascadeTests</c> 判到"谁被带进会话、边怎么解算、捞不到与推不出各怎么响"为止，
/// 那里连一句 SQL 都没上线。两边合起来才是这一格的完整形状：缺库里那一半，
/// "明细带得上父的键"就永远只是一句内存里的意图。</para>
///
/// <para><b>裁判是读回来的值，不是库加的约束</b>：<c>QuickSqlOrderItem</c> 上没有 <c>FOREIGN KEY</c>
/// （<c>CodeFirst</c> 不发约束，那一格记在 §14），所以键落错、落负、落成别人的父，库一句都不响。
/// 每条断言都从 <see cref="Database.Rows"/>（另一条裸 ADO 连接）读那一格里躺着的值。</para>
///
/// <para><b>写只写本次标签行</b>（与 <see cref="TpPathTests"/> 同一条纪律）：全份判据都带本次进程的随机标签，
/// 没有一句全表计数。删除那两条删的也是本用例自己刚造出来、自己读回主键的那几行。
/// 级联删除落在共享的 <c>QuickSqlOrder</c> 上之所以安全，靠的就是这一句：会话看不见别人的行，
/// 而那些行的 <c>Note</c> 不是本次标签。</para></summary>
public sealed class CascadePathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static DbContext Session() => new(Orm);

    // Batch 每用例一颗（static 会让同类各用例共一篇标签，隔离押在 xunit 方法序上，重编译可翻红；理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-cascade-" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 6, 1, 2, 3, 4, 567);

    string tagged(string name) => $"{Batch}/{name}";

    /// <summary>货架那一对用的短标签：<c>Rack.Label</c> 是 <c>nvarchar(32)</c> 而 <c>ShelfSlot.Bin</c> 只有
    /// <c>nvarchar(16)</c>，装不下整条 <c>quicksql-cascade-xxxxxxxx/…</c>。取本次那 8 位十六进制做前缀，
    /// 一次运行仍然独一份 —— 判据要的是"这一行的键落对了"，不是标签的长度。</summary>
    string slate(string name) => $"{Batch[16..]}/{name}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static string[] Written(SaveChangesReport report) => [.. report.Operations.Select(o => o.Table)];

    /// <summary>一张新单的现场：主键空着（让库给），明细也是空的键。</summary>
    Order Bill(string name, params string[] skus) => new()
    {
        CustomerName = tagged(name),
        CustomerId = 4242,
        Amount = 12.5m,
        State = OrderState.Placed,
        CreatedAt = Moment,
        Note = tagged(name),
        Items = [.. skus.Select(sku => new OrderItem { Sku = tagged(sku), Quantity = 1, Price = 2m })],
    };

    static int Num(Dictionary<string, string> row, string column) => int.Parse(row[column], CultureInfo.InvariantCulture);

    /// <summary>按标签读回某张表里那一列的值。标签是本次运行独有的，所以这句不会被别人留下的行撞中。
    /// <b>读的是文本列而不是主键</b>：主键正是这一格要验的东西，不能拿它当过滤条件。</summary>
    static int OneNumber(string table, string column, string filterColumn, string tag)
        => Num(Assert.Single(Database.Rows($"SELECT {column} AS V FROM {table} WHERE {filterColumn} = @tag", ("@tag", tag))), "V");

    static int Count(string table, string where, params (string Name, object? Value)[] args)
        => Num(Assert.Single(Database.Rows($"SELECT COUNT(*) AS V FROM {table} WHERE {where}", args)), "V");

    static int StoredOrderId(string note) => OneNumber("[dbo].[QuickSqlOrder]", "[Id]", "[Note]", note);

    static int StoredParentOf(string sku) => OneNumber("[dbo].[QuickSqlOrderItem]", "[OrderId]", "[Sku]", sku);

    // ── 级联插入 ────────────────────────────────────────────────────

    [Fact]
    public async Task Cascade_insert_lands_the_parents_key_into_each_child_row()
    {
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        var db = Session();
        var bill = Bill("单", "甲", "乙");

        db.Add(bill);

        // 级联带人：一句 Add 之后会话里有三只。这一句在离线也判得到，留在这里是因为下面那句要同一份现场。
        Assert.Equal(3, db.ChangeTracker.Count);

        var report = await db.SaveChangesAsync(ct);

        var tables = Written(report);

        // 先后：父那一档必须排在明细之前（集合边进拓扑，否则键还没回填就要建子的语句）。
        Assert.True(Array.IndexOf(tables, "QuickSqlOrder") < Array.LastIndexOf(tables, "QuickSqlOrderItem"),
            $"父行没有排在明细之前：{string.Join(" → ", tables)}");

        var parentId = StoredOrderId(bill.Note!);

        Assert.Equal(parentId, StoredParentOf(bill.Items![0].Sku!));
        Assert.Equal(parentId, StoredParentOf(bill.Items![1].Sku!));

        // 落键改的是<em>实体</em>而不只是参数：这只实例之后还要被读、被比对、被下一次保存写。
        Assert.Equal(parentId, bill.Items[0].OrderId);
        Assert.Equal(parentId, bill.Items[1].OrderId);

        // 「临时值不进库」那条不变量在级联这一路上同样成立：明细的外键里没有负数。
        Assert.Equal(0, Count("[dbo].[QuickSqlOrderItem]", "[OrderId] < 0 AND [Sku] = @sku", ("@sku", bill.Items[0].Sku)));
    }

    [Fact]
    public async Task Each_child_gets_its_own_parents_key_rather_than_the_last_one()
    {
        // 这一格判的是"落键按<em>行</em>对上"，不是"落成本批最后一个父的键"。
        // 两条级联链各带一只明细，两父同号（都是库里刚给的自增键）时它照样判得住：落错档就是甲的明细挂到乙父上。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        var db = Session();
        var first = Bill("甲父", "甲子");
        var second = Bill("乙父", "乙子");

        db.Add(first);
        db.Add(second);

        await db.SaveChangesAsync(ct);

        var left = StoredParentOf(first.Items![0].Sku!);
        var right = StoredParentOf(second.Items![0].Sku!);

        Assert.Equal(StoredOrderId(first.Note!), left);
        Assert.Equal(StoredOrderId(second.Note!), right);
        Assert.NotEqual(left, right);
    }

    [Fact]
    public async Task A_declared_foreign_key_member_carries_the_parent_key()
    {
        // 第二档：子端那一列叫 RackKey 而不是 RackId，靠集合导航上 [ForeignKey("RackKey")] 接上。
        // 库里那一格落的确实是父的键 —— 解算错了不会响，只会把键落错列（或干脆落成 0）。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        EnsureTable(typeof(Rack));
        EnsureTable(typeof(ShelfSlot));

        var db = Session();
        var rack = new Rack { Id = 0, Label = slate("架"), Slots = [new ShelfSlot { Bin = slate("位") }] };

        db.Add(rack);

        await db.SaveChangesAsync(ct);

        // 父的键以库里给的那一颗为准：实体手里那一份与库里那一格必须同一颗，否则落键只落在了参数上。
        Assert.True(rack.Id > 0, $"父行没有拿到库里给的主键：{rack.Id}");
        Assert.Equal(rack.Id, OneNumber("[dbo].[QuickSqlShelfSlot]", "[RackKey]", "[Bin]", slate("位")));
        Assert.Equal(rack.Slots![0].RackKey, rack.Id);
    }

    [Fact]
    public async Task NoCascade_on_the_navigation_leaves_the_children_out_of_the_database()
    {
        // 开关那一格落在库里：关了级联就一句 INSERT 都不多发，明细留在调用方手里。
        // 这一判离线也给得了（会话里没有那两只），在这里判的是"库里确实没有那两行"。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        EnsureTable(typeof(Board));
        EnsureTable(typeof(Card));

        var db = Session();
        var board = new Board { Title = tagged("看板"), Cards = [new Card { Text = tagged("卡片") }] };

        db.Add(board);

        Assert.Equal(1, db.ChangeTracker.Count);

        await db.SaveChangesAsync(ct);

        Assert.Equal(0, Count("[dbo].[QuickSqlCard]", "[Text] = @text", ("@text", tagged("卡片"))));
        Assert.Equal(1, Count("[dbo].[QuickSqlBoard]", "[Title] = @title", ("@title", tagged("看板"))));
    }

    // ── 级联删除 ────────────────────────────────────────────────────

    [Fact]
    public async Task Cascade_delete_removes_the_children_before_the_parent()
    {
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        // 现场用不跟踪的那条链造：级联删除的用例不该依赖级联插入的写口准备前置条件。
        var bill = Bill("删单", "丙", "丁");

        var parentId = (int) await Orm.Insert(bill).ExecuteIdentityAsync(ct);

        foreach (var item in bill.Items!)
        {
            item.OrderId = parentId;

            await Orm.Insert(item).ExecuteAffrowsAsync(ct);
        }

        var db = Session();

        var read = await db.Set<Order>().Where(o => o.Id == parentId).ToListAsync(ct);
        var stored = Assert.Single(read);

        // 裁决①的前置：子行要先进会话（Load 那一根导航），会话才看得见要删的是哪几行。
        db.Set<Order>().Load(o => o.Items!);

        Assert.NotNull(stored.Items);
        Assert.Equal(2, stored.Items!.Count);

        db.Remove(stored);

        Assert.Equal(3, db.ChangeTracker.Entries().Count(e => e.State == EntityState.Deleted));

        var report = await db.SaveChangesAsync(ct);

        var tables = Written(report);

        // 删除走的是逆序：先删明细再删父 —— 顺序反了就会被库里那条外键约束挡下来（而那是最难读的一种失败形状）。
        Assert.True(Array.LastIndexOf(tables, "QuickSqlOrderItem") < Array.IndexOf(tables, "QuickSqlOrder"),
            $"明细没有排在父之前删：{string.Join(" → ", tables)}");

        Assert.Equal(0, Count("[dbo].[QuickSqlOrderItem]", "[OrderId] = @id", ("@id", parentId)));
        Assert.Equal(0, Count("[dbo].[QuickSqlOrder]", "[Id] = @id", ("@id", parentId)));
    }

    [Fact]
    public async Task Removing_a_parent_is_refused_until_its_children_are_in_the_session()
    {
        // 这一条是"捞不到就拒"的真库形状，而它值钱的地方在后半句：拒了之后按消息给的出路 Load 一下，
        // 同一只实例就删得下去了。只判前半句的用例等于把一条死路当成判据。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        var bill = Bill("未捞", "戊");

        var parentId = (int) await Orm.Insert(bill).ExecuteIdentityAsync(ct);

        await Orm.Insert(new OrderItem { OrderId = parentId, Sku = tagged("戊"), Quantity = 1, Price = 2m }).ExecuteAffrowsAsync(ct);

        var db = Session();

        var stored = Assert.Single(await db.Set<Order>().Where(o => o.Id == parentId).ToListAsync(ct));

        var raised = Assert.Throws<QuickSqlException>(() => db.Remove(stored));

        Assert.Contains("可达才删", raised.Message);
        Assert.Equal(1, Count("[dbo].[QuickSqlOrderItem]", "[OrderId] = @id", ("@id", parentId)));

        db.Set<Order>().Load(o => o.Items!);

        db.Remove(stored);

        await db.SaveChangesAsync(ct);

        Assert.Equal(0, Count("[dbo].[QuickSqlOrderItem]", "[OrderId] = @id", ("@id", parentId)));
        Assert.Equal(0, Count("[dbo].[QuickSqlOrder]", "[Id] = @id", ("@id", parentId)));
    }

    [Fact]
    public async Task Loading_a_collection_already_in_the_session_stitches_the_tracked_instance()
    {
        // 级联删除那颗"同键两只实例在会话里没有定义"的响，来路就在补载这一步：明细先被单独查进会话
        // （身份映射里已经是那两只），之后 Load 又把库里同一行各物化一遍。补载的那一只必须换成会话里的那一只
        // （§8.2 同一行同一实例），否则父删不动 —— 样本 §4 走的正是这一条路。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        var bill = Bill("双", "甲", "乙");

        var parentId = (int) await Orm.Insert(bill).ExecuteIdentityAsync(ct);

        foreach (var item in bill.Items!)
        {
            item.OrderId = parentId;

            await Orm.Insert(item).ExecuteAffrowsAsync(ct);
        }

        var db = Session();

        var already = await db.Set<OrderItem>().Where(i => i.OrderId == parentId).ToListAsync(ct);
        Assert.Equal(2, already.Count);

        var stored = Assert.Single(await db.Set<Order>().Where(o => o.Id == parentId).ToListAsync(ct));

        db.Set<Order>().Load(o => o.Items!);

        // 判引用而不是判值：值两边本来就一样，只有引用分得出"会话里的那一只"与"刚物化那一只"。
        Assert.All(stored.Items!, stitched => Assert.Contains(already, known => ReferenceEquals(known, stitched)));

        // 这一句才是这一格的裁判：换了那一只之后它走得通，挂着双胞胎时它响。
        db.Remove(stored);

        await db.SaveChangesAsync(ct);

        Assert.Equal(0, Count("[dbo].[QuickSqlOrderItem]", "[OrderId] = @id", ("@id", parentId)));
        Assert.Equal(0, Count("[dbo].[QuickSqlOrder]", "[Id] = @id", ("@id", parentId)));
    }

    // ── 摘行：从集合里摘掉一只，库里那一行跟着走（裁决③）──────────────

    [Fact]
    public async Task Detaching_a_child_from_a_loaded_collection_deletes_only_that_row()
    {
        // 这一格只有库里看得见：离线判到"那一只标成了删除档"为止，
        // 而"摘掉一行、留下另一行、父行不动"这三件事同时成立，只有读回库里的行数才分得清。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        var bill = Bill("摘单", "甲", "乙");

        var parentId = (int) await Orm.Insert(bill).ExecuteIdentityAsync(ct);

        foreach (var item in bill.Items!)
        {
            item.OrderId = parentId;

            await Orm.Insert(item).ExecuteAffrowsAsync(ct);
        }

        var db = Session();

        var stored = Assert.Single(await db.Set<Order>().Where(o => o.Id == parentId).ToListAsync(ct));

        db.Set<Order>().Load(o => o.Items!);

        var gone = stored.Items!.Single(i => i.Sku == tagged("甲"));

        stored.Items!.Remove(gone);

        var report = await db.SaveChangesAsync(ct);

        var tables = Written(report);

        // 摘一行不碰父：这一批里只有明细那一档欠语句，父那一档一句都没有。
        Assert.Contains("QuickSqlOrderItem", tables);
        Assert.DoesNotContain("QuickSqlOrder", tables);

        Assert.Equal(0, Count("[dbo].[QuickSqlOrderItem]", "[Sku] = @sku", ("@sku", tagged("甲"))));
        Assert.Equal(1, Count("[dbo].[QuickSqlOrderItem]", "[Sku] = @sku", ("@sku", tagged("乙"))));
        Assert.Equal(1, Count("[dbo].[QuickSqlOrder]", "[Id] = @id", ("@id", parentId)));
    }

    [Fact]
    public async Task Detaching_an_unsaved_child_writes_no_row_at_all()
    {
        // 还没写出去的那一只（新增档）被摘掉 ⇒ 忘掉它，库里没有可对账的一行。
        // 判的是"库里只落了甲"：如果这一步把忘掉误走成删除，库里会响（删不存在的行）或者少落一行。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        var db = Session();
        var bill = Bill("摘新", "甲", "乙");

        db.Add(bill);

        Assert.Equal(3, db.ChangeTracker.Count);

        bill.Items!.Remove(bill.Items[1]);

        var report = await db.SaveChangesAsync(ct);

        var parentId = StoredOrderId(bill.Note!);

        Assert.Equal(1, Count("[dbo].[QuickSqlOrderItem]", "[OrderId] = @id", ("@id", parentId)));
        Assert.Equal(parentId, StoredParentOf(tagged("甲")));
        Assert.Equal(0, Count("[dbo].[QuickSqlOrderItem]", "[Sku] = @sku", ("@sku", tagged("乙"))));
        Assert.Equal(1, Written(report).Count(t => t == "QuickSqlOrderItem"));
    }

    [Fact]
    public async Task A_detached_child_takes_its_own_rows_out_of_the_database()
    {
        // 第三层（架 → 位 → 钩）在库里判的那一半：摘掉一位，它名下的两枚钩一起走，
        // 而且钩那一档排在位那一档之前 —— 逆序在级联删除里是同一个判据，摘行走的是同一条路。
        Database.Ready();
        Database.JudgeWrittenFor(Database.Target.SqlServer, "裁判 SQL 是 [dbo] 三段式 T-SQL");

        EnsureTable(typeof(Rack));
        EnsureTable(typeof(ShelfSlot));
        EnsureTable(typeof(SlotHook));

        var db = Session();
        var rack = new Rack
        {
            Id = 0,
            Label = slate("架"),
            Slots = [new ShelfSlot { Bin = slate("位"), Hooks = [new SlotHook { Name = slate("钩一") }, new SlotHook { Name = slate("钩二") }] }],
        };

        db.Add(rack);

        await db.SaveChangesAsync(ct);

        var rackId = rack.Id;
        var slotBin = slate("位");

        Assert.Equal(2, Count("[dbo].[QuickSqlSlotHook]", "[SlotId] IN (SELECT [Id] FROM [dbo].[QuickSqlShelfSlot] WHERE [Bin] = @bin)", ("@bin", slotBin)));

        var again = Session();

        // 现场手续：会话的跟踪档由 Set<T>() 建，而 IncludeMany 往会话里登记<em>子型</em>时不会自己建档
        // （会话里没见过的类型就没有档，那一句"内核不变量被破"是它给的响）。先摸一下位那一档，
        // 让下面那条 IncludeMany 登记得了子行 —— 这一格缺口本身是另一笔账，不在摘行这一轮里修。
        again.Set<ShelfSlot>();

        // 两道门各判一次：架→位这一级走 IncludeMany（那根边是 [ForeignKey] 点名的，第二档），
        // 位→钩这一级走 Load（同一根点名的边，另一条路）。两条路都必须认第二档 ——
        // 级联按第二档把键落了库，捞的时候认不出同一根边就是"插得进去、捞不上来"。
        var shelf = Assert.Single(await again.Set<Rack>()
            .IncludeMany(r => r.Slots!)
            .Where(r => r.Id == rackId)
            .ToListAsync(ct));

        again.Set<ShelfSlot>().Load(s => s.Hooks!);

        var pulled = shelf.Slots!.Single();

        Assert.NotNull(pulled.Hooks);
        Assert.Equal(2, pulled.Hooks!.Count);

        shelf.Slots!.Remove(pulled);

        var report = await again.SaveChangesAsync(ct);

        var tables = Written(report);

        Assert.True(Array.IndexOf(tables, "QuickSqlSlotHook") < Array.IndexOf(tables, "QuickSqlShelfSlot"),
            $"钩没有排在位之前删：{string.Join(" → ", tables)}");

        Assert.Equal(0, Count("[dbo].[QuickSqlShelfSlot]", "[Bin] = @bin", ("@bin", slotBin)));
        Assert.Equal(0, Count("[dbo].[QuickSqlSlotHook]", "[Name] = @name", ("@name", slate("钩一"))));
        Assert.Equal(1, Count("[dbo].[QuickSqlStorageRack]", "[Id] = @id", ("@id", rackId)));
    }

    /// <summary>IncludeMany 缝出来的子行<em>自己</em>进会话：调用方不必先摸一句 <c>Set&lt;子型&gt;()</c>。
    ///
    /// <para><b>这一格是补上的那一笔</b>：<c>IncludeMany</c> 往会话里登记子型时，子型那一档（跟踪档）此前
    /// 只有调用方自己先摸一句 <c>Set&lt;ShelfSlot&gt;()</c> 才存在 —— 否则登记那一步撞的是「内核的不变量被破」
    /// （档位只有泛型那一颗 <c>For&lt;T&gt;()</c> 能建，而编译期子型只在接链那一刻有）。
    /// 那一格曾是<em>调用方的现场手续</em>，不是「IncludeMany 缝出来的子行理应被跟住」这件事本身；
    /// 现在接链处把建档委托交给节点，执行期自己建。</para>
    ///
    /// <para><b>判据两条</b>：① 缝出来的子行确实在会话里（<c>Local</c> 看得见它们，且是同一只实例）；
    /// ② 摘掉一只 → <c>SaveChanges</c> 之后那只真的出库，而另一只与父都还在。
    /// 读回用<em>另一只会话</em>走 ORM 的读，不写裁判 SQL —— 于是这一条与目标库无关，各家都跑。</para>
    ///
    /// <para><b>为什么用托盘/明细而不用架/位、也不用板/卡片</b>：位那一档自己还挂着一串钩，而钩是<em>集合</em>导航 ——
    /// 摘掉位要连带钩一起走，钩没捞进会话时那一档按裁决①当场拒（会话看不见子行就不能替它判「没有要删的」）；
    /// 卡片那一档的导航上打了 <c>[NoCascade]</c>，级联本就不带它（卡片压根没进库）。
    /// 明细那一档只有一根<em>引用</em>导航（物资），引用边不参与级联删除，
    /// 于是这一条只判_include_many 缝出来的那一格</em>。</para>
    [Fact]
    public async Task An_include_many_child_is_tracked_without_the_caller_touching_the_child_set()
    {
        Database.Ready();

        EnsureTable(typeof(Pallet));
        EnsureTable(typeof(PalletLine));

        var db = Session();
        var pallet = new Pallet
        {
            Id = 0,
            Label = slate("托"),
            Lines =
            [
                new PalletLine { Name = slate("线甲"), MaterialId = 0 },
                new PalletLine { Name = slate("线乙"), MaterialId = 0 },
            ],
        };

        db.Add(pallet);

        await db.SaveChangesAsync(ct);

        var palletId = pallet.Id;
        var lineA = slate("线甲");
        var lineB = slate("线乙");

        var again = Session();

        // 这一句就是全部：IncludeMany 缝出来的子行自己进会话，调用方不碰子型那一档。
        var loaded = Assert.Single(await again.Set<Pallet>()
            .IncludeMany(p => p.Lines!)
            .Where(p => p.Id == palletId)
            .ToListAsync(ct));

        var pulled = loaded.Lines!.Single(l => l.Name == lineA);

        // ① 缝上来的子行在会话里，而且是缝上去的那一只（不是另物化一只）。
        Assert.Same(pulled, Assert.Single(again.Set<PalletLine>().Local, l => l.Name == lineA));

        loaded.Lines!.Remove(pulled);

        var report = await again.SaveChangesAsync(ct);

        Assert.Contains("QuickSqlPalletLine", Written(report));

        // ② 摘掉的那一只出库了，另一只与父都还在（读回走另一只会话，不写裁判 SQL）。
        var check = Session();

        Assert.Empty(await check.Set<PalletLine>().Where(l => l.Name == lineA).ToListAsync(ct));
        Assert.Single(await check.Set<PalletLine>().Where(l => l.Name == lineB).ToListAsync(ct));
        Assert.Single(await check.Set<Pallet>().Where(p => p.Id == palletId).ToListAsync(ct));
    }

    /// <summary>链式集合加载（<c>IncludeMany(…).ThenIncludeMany(…)</c>，§5.7）判在真库上：架→位一层、位→钩二层，
    /// 一条链三张表。
    ///
    /// <para><b>为什么单独一条，不并进上面那道两门的用例</b>：那条判的是"两道门各认得第二档点名的边"
    /// （架→位走 <c>IncludeMany</c>、位→钩走 <c>Load</c>），缝完位还要另起一句才够得着钩；这一条判的是
    /// <em>一句表达得完</em> —— 同一批行、同一根边、同一个第二档（<c>[ForeignKey]</c> 点名），
    /// 两条路读回来的值必须一致，否则"链能往下走"就只是一句编译器的承诺。</para>
    ///
    /// <para><b>用独立一台 ORM 而不是会话</b>：<c>IncludeMany</c> 往会话登记<em>子型</em>时不建档那一格是
    /// 已记名的缺口（上面那条用例为此先摸一句 <c>Set&lt;ShelfSlot&gt;()</c> 才走得通），而这里要判的是链本身，
    /// 两层都躲开那格；顺带 <c>Aop.Executing</c> 读回的三句文本也只属于这一次读取，不串到同场其他用例。</para></summary>
    [Fact]
    public async Task A_chained_include_many_loads_two_collection_levels()
    {
        Database.Ready();
        var ct = TestContext.Current.CancellationToken;

        EnsureTable(typeof(Rack));
        EnsureTable(typeof(ShelfSlot));
        EnsureTable(typeof(SlotHook));

        var db = Session();
        var rack = new Rack
        {
            Id = 0,
            Label = slate("链架"),
            Slots =
            [
                new ShelfSlot { Bin = slate("链位甲"), Hooks = [new SlotHook { Name = slate("甲一") }, new SlotHook { Name = slate("甲二") }] },
                new ShelfSlot { Bin = slate("链位乙"), Hooks = [new SlotHook { Name = slate("乙一") }] },
            ],
        };

        db.Add(rack);

        await db.SaveChangesAsync(ct);

        var rackId = rack.Id;
        var sent = new List<(string Text, int Parameters)>();
        var aop = new QuickSqlAop();

        aop.Executing = e => sent.Add((e.Sql.Text, e.Sql.Parameters.Count));

        var shelf = Assert.Single(await Database.OrmWith(aop)
            .Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .ThenIncludeMany(s => s.Hooks!)
            .Where(r => r.Id == rackId)
            .ToListAsync(ct));

        // 三句：主查询、架→位、位→钩。第二层的 IN 里躺的是刚缝出来的那两只位的键（两颗参数），
        // 不是架的键 —— 那一格错了的话钩会整批挂错父，而文本看不出来，所以参数个数也钉住。
        Assert.Equal(3, sent.Count);
        // 名字走 CatalogueName：虚谷的 IdentifierPolicy 是 FoldToUpperCase（发射前就折，两支出口都折），
        // 而这一格钉的是"这一句打的是哪张表"，不是"字面拼法长什么样"。其余四家那一句原样返回。
        Assert.Contains(Database.CatalogueName("QuickSqlStorageRack"), sent[0].Text);
        Assert.Contains(Database.CatalogueName("QuickSqlShelfSlot"), sent[1].Text);
        Assert.Contains(Database.CatalogueName("QuickSqlSlotHook"), sent[2].Text);
        Assert.Equal(1, sent[1].Parameters);
        Assert.Equal(2, sent[2].Parameters);

        var first = shelf.Slots!.Single(s => s.Bin == slate("链位甲"));
        var second = shelf.Slots!.Single(s => s.Bin == slate("链位乙"));

        Assert.Equal(2, shelf.Slots!.Count);
        Assert.Equal([slate("甲一"), slate("甲二")], first.Hooks!.Select(h => h.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(slate("乙一"), Assert.Single(second.Hooks!).Name);
    }

    /// <summary>链上「旁边那一根」（<c>IncludeMany(…).ThenInclude(…)</c>，§5.7 引用那一档）判在真库上：
    /// 托盘一句把明细带出来，明细手里那一份物资也一起带出来 —— 三张表、三句，主查询只有第一句。
    ///
    /// <para><b>为什么钉第三句的参数个数</b>：那一句的 IN 里躺的是那批明细的<em>外键值</em>，而四只明细
    /// 只有三种键（两只同物资、一只另一只、一只没有物资＝键 <c>0</c>）。发出去的是三颗参数而不是四颗：
    /// 这一句按键去重。"没去重"在结果里完全看不出来（缝对了就是缝对了），只在库多读一次、多绑一颗，
    /// 所以参数个数是唯一量得到它的读数。</para>
    ///
    /// <para><b>没物资的那一行必须留在结果里、导航留空</b>：<c>MaterialId = 0</c> 在库里配不到任何一行，
    /// 引用那一档按主键配，配不到就是 <c>null</c> —— 而不是把这只明细筛掉（与 §5.6 那条裁定同向：
    /// 可选边为空不是删行的理由）。</para></summary>
    [Fact]
    public async Task A_reference_leg_on_a_collection_level_loads_the_target()
    {
        Database.Ready();

        EnsureTable(typeof(Pallet));
        EnsureTable(typeof(PalletLine));
        EnsureTable(typeof(Material));

        var db = Session();
        var kept = new Material { Spec = slate("物资甲") };
        var sold = new Material { Spec = slate("物资乙") };

        db.Add(kept);
        db.Add(sold);

        await db.SaveChangesAsync(ct);

        var pallet = new Pallet
        {
            Id = 0,
            Label = slate("托盘"),
            Lines =
            [
                new PalletLine { Name = slate("行一"), MaterialId = kept.Id },
                new PalletLine { Name = slate("行二"), MaterialId = kept.Id },
                new PalletLine { Name = slate("行三"), MaterialId = sold.Id },
                new PalletLine { Name = slate("行四"), MaterialId = 0 },
            ],
        };

        db.Add(pallet);

        await db.SaveChangesAsync(ct);

        var palletId = pallet.Id;
        var sent = new List<(string Text, int Parameters)>();
        var aop = new QuickSqlAop();

        aop.Executing = e => sent.Add((e.Sql.Text, e.Sql.Parameters.Count));

        var stack = Assert.Single(await Database.OrmWith(aop)
            .Select<Pallet>()
            .IncludeMany(p => p.Lines!)
            .ThenInclude(l => l.Material)
            .Where(p => p.Id == palletId)
            .ToListAsync(ct));

        // 三句：主查询、托盘→明细、明细→物资。第二句的 IN 里是一颗托盘键，第三句是三种明细键。
        Assert.Equal(3, sent.Count);
        // 同上：折的是期望值那一边，判据不松成大小写不敏感（口径见 Database.CatalogueName）。
        Assert.Contains(Database.CatalogueName("QuickSqlPallet"), sent[0].Text);
        Assert.Contains(Database.CatalogueName("QuickSqlPalletLine"), sent[1].Text);
        Assert.Contains(Database.CatalogueName("QuickSqlMaterial"), sent[2].Text);
        Assert.Equal(1, sent[1].Parameters);
        Assert.Equal(3, sent[2].Parameters);

        Assert.Equal(4, stack.Lines!.Count);

        Assert.Equal(slate("物资甲"), stack.Lines!.Single(l => l.Name == slate("行一")).Material?.Spec);
        Assert.Equal(slate("物资甲"), stack.Lines!.Single(l => l.Name == slate("行二")).Material?.Spec);
        Assert.Equal(slate("物资乙"), stack.Lines!.Single(l => l.Name == slate("行三")).Material?.Spec);
        Assert.Null(stack.Lines!.Single(l => l.Name == slate("行四")).Material);
    }

    static void EnsureTable(Type clrType) => Database.EnsureTable(clrType);
}
