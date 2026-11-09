using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary><c>Tp</c> 临时主键的<em>回填</em>那一半，判在真库上（§8.2、v0.21）。
///
/// <para><b>为什么这一半只能在库里看</b>：换值那一步（<c>EntityTracker.ResolveTempKeys</c>）排在
/// <c>BeginTransaction</c> 之后，而离线注册表一碰驱动就抛 —— 于是"依赖方手里那颗负数被换成库里给回来的真实键"
/// 这条链在离线根本走不到。离线的 <c>TempKeyTests</c> 判的是它的<em>前</em>一半（临时值进得了实体、
/// 进不了身份映射，也进不了任何一句 <c>WHERE</c> 的合法性）。两边合起来才是这一格的完整形状，
/// 缺哪一半都会留出一个"看起来能用"的缝。</para>
///
/// <para>与 <see cref="OrderingPathTests"/> 的分工是：<b>那里判"先后排出来了没有"，这里判"排出来之后引用接上了没有"</b>。
/// 同一对靶子（<c>Order</c> 挂到新的 <c>Customer</c> 上）在那一份里是<em>反</em>例 —— 父行的键要等 <c>INSERT</c> 给回来，
/// 光有先后接不上引用；在这里它是正例。</para>
///
/// <para><b>裁判是读回来的值，不是库加的约束</b>：这一格要判的性质是"落库的那一格是不是真实键"，
/// 一句 <c>SELECT [CustomerId] … WHERE [Note] = 本次标签</c>（<see cref="Database.Rows"/>，另一条裸 ADO 连接）
/// 就直接把它判了。这里<em>不</em>给 <c>QuickSqlOrder</c> 加外键约束：那张表是共享的，别的用例往 <c>CustomerId</c>
/// 里写过 4242 这种没有父行的值（<c>TrackingPathTests</c>/<c>WritePathTests</c>），加上约束等于把别人的用例改成红 ——
/// 而 <see cref="OrderingPathTests"/> 那条约束加得下去是因为它只挂在 <c>QuickSqlMilestone</c> 这一张专供的表上。
/// 约束在这里也不是必需的前提：临时值一旦漏进库里就是<em>负数</em>，<see cref="NegativeKeys"/> 按标签数本次写过的那几行。
/// <b>全份判据都带标签，没有一句是全表计数</b>：这一格跑过一次证伪（把回填掐掉再跑），那些负数行至今还在库里，
/// 而全表计数只会把它们算成这一版的错。</para>
///
/// <para><see cref="TpPrincipal"/>/<see cref="TpDependent"/>/<see cref="TpPeer"/> 这三张表由本文件自己建
/// （<see cref="FixturesReady"/>，走 <c>CodeFirst</c> 的同一条产 DDL 路径）：往共享模型里加一张表要连着动种子的
/// 固定类型清单与整片黄金样本，而这三张只服务于这一格。</para></summary>
public sealed class TpPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static DbContext Session() => new(Orm);

    // Batch 每用例一颗（static 会让同类各用例共一篇标签，隔离押在 xunit 方法序上，重编译可翻红；理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-tp-" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 6, 1, 2, 3, 4, 567);

    string tagged(string name) => $"{Batch}/{name}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static List<SaveChangesOp> Ops(SaveChangesReport report, WriteOperation phase)
        => [.. report.Operations.Where(o => o.Operation == phase)];

    static string[] Inserted(SaveChangesReport report)
        => [.. Ops(report, WriteOperation.Insert).Select(o => o.Table)];

    /// <summary>一条挂在<em>本次标签</em>下的订单，外键先空着（0）：谁当它的父行，各用例在 <c>Add</c> 之后抄过去 ——
    /// 那一句赋值正是这一格的用户写法（<c>order.CustomerId = customer.Id</c>，而此刻 <c>Id</c> 还是临时键）。</summary>
    Order NewOrder(string name) => new()
    {
        CustomerName = tagged(name),
        Amount = 12.5m,
        State = OrderState.Placed,
        CreatedAt = Moment,
        Note = tagged(name),
    };

    Customer NewCustomer(string name) => new() { Name = tagged(name), Remark = tagged(name) };

    /// <summary>按标签读回某张表里那一列的值。标签是本次运行独有的，所以这句不会被别人留下的行撞中。
    /// <b>读的是文本列而不是主键</b>：主键正是这一格要验的东西，不能拿它当过滤条件。</summary>
    static int OneNumber(string table, string column, string filterColumn, string tag)
        => Num(Assert.Single(Database.Rows($"SELECT {column} AS V FROM {table} WHERE {filterColumn} = @tag", ("@tag", tag))), "V");

    static int StoredCustomerId(Customer customer)
        => OneNumber("[dbo].[QuickSqlCustomer]", "[Id]", "[Name]", customer.Name!);

    static int StoredParentOf(Order order)
        => OneNumber("[dbo].[QuickSqlOrder]", "[CustomerId]", "[Note]", order.Note!);

    static int Count(string table, string where, params (string Name, object? Value)[] args)
        => Num(Assert.Single(Database.Rows($"SELECT COUNT(*) AS V FROM {table} WHERE {where}", args)), "V");

    /// <summary>「临时值不进库」那句不变量的读口，<b>只数本次标签下的行</b>。
    /// 不做全表计数是因为它只会误响而判不住任何东西：库里已经躺着证伪跑留下的负数行（那一次正是为了确认
    /// 这几条断言<em>会</em>红），而这几张表是共享的。标签带本次进程的随机后缀，所以这一句只审判自己写过的那几行。</summary>
    static int NegativeKeys(string table, string column, string filterColumn, string tag)
        => Count(table, $"{column} < 0 AND {filterColumn} = @tag", ("@tag", tag));

    static int Num(Dictionary<string, string> row, string column) => int.Parse(row[column], CultureInfo.InvariantCulture);

    static readonly Type[] Fixtures = [typeof(TpPrincipal), typeof(TpDependent), typeof(TpPeer)];

    static bool _fixturesReady;

    /// <summary>每条用例的第一句：库没起来就 skip，起来了再确认那三张只给本文件用的表在位。
    /// <b>建表走 <c>CodeFirst.EmitCreateTable</c>，与本层 <see cref="Database"/> 的 <c>EnsureTable</c> 同一条产路</b>，
    /// 所以靶子的列清单写错（比如 <c>bigint</c> 那一列的 CLR 型）会在这里当场炸，而不是让断言去猜。
    /// 幂等：表在就不动，跨进程重复跑安全，也不需要 <c>DELETE</c>。</summary>
    static void FixturesReady()
    {
        Database.Ready();

        if (_fixturesReady)
            return;

        foreach (var fixture in Fixtures)
            EnsureTable(fixture);

        _fixturesReady = true;
    }

    /// <summary>建一张靶表。<b>本版起这一句直接交回 <see cref="Database.EnsureTable(System.Type)"/>，
    /// 本文件不再自带一份</b> —— 上一版这里是一份"同一条产路的手写副本"，而它<em>不同</em>的那一半恰好是
    /// 一颗只属于 T-SQL 的存在性判据（<c>OBJECT_ID(@name, N'U')</c>），于是第五家第一跑就是七条红，
    /// 红的还不是临时键那一格，是夹具自己。收成公开那一句之后：产 DDL 的路径<em>还是同一条</em>
    /// （<c>CodeFirst.EmitCreateTable</c>，注释上面那段"列清单写错会在这里当场炸"的理由一字不改地成立），
    /// 而"表在不在"这一问跟着 <see cref="Database"/> 的家族缝走（T-SQL 问 <c>OBJECT_ID</c>，
    /// 虚谷 join <c>all_tables</c> 与 <c>all_schemas</c>，凭据 <c>T-05</c>）。
    /// <b>这一颗删掉的同时也删掉了"两处判据各自漂移"的可能</b>：私有那一份不会有人去补家族分支。</summary>
    static void EnsureTable(Type clrType) => Database.EnsureTable(clrType);

    [Fact]
    public async Task A_dependent_added_before_its_parent_gets_the_key_the_database_handed_back()
    {
        FixturesReady();

        var db = Session();

        var customer = NewCustomer("父");
        var order = NewOrder("子");

        db.Add(order);
        db.Add(customer);

        var temp = customer.Id;

        order.CustomerId = temp;

        // 现场：抄进外键那一格的确实是一颗负数临时键 —— 发号发生在 Add 里，不是 SaveChanges 里。
        Assert.True(temp < 0, $"Add 之后父行没有拿到临时主键：{temp}");

        var report = await db.SaveChangesAsync(ct);

        // 先后是对的（v0.20 那一格），而这一格要的是接上之后引用落成了什么。
        Assert.Equal(["QuickSqlCustomer", "QuickSqlOrder"], Inserted(report));

        // 换值落在<em>实体</em>上而不只是落在参数上：这只实例之后还要被读、被下一次保存写。
        Assert.True(order.CustomerId > 0, $"依赖方手里还是那颗临时键：{order.CustomerId}");
        Assert.Equal(customer.Id, order.CustomerId);

        // 裁判在另一条连接上：库里那一行的外键 = 库里那位新客户刚拿到的主键。
        var realId = StoredCustomerId(customer);

        Assert.Equal(realId, StoredParentOf(order));
        Assert.Equal(0, NegativeKeys("[dbo].[QuickSqlOrder]", "[CustomerId]", "[Note]", order.Note!));

        // 父行那一侧同样要判：自增列不进 INSERT 的列清单，所以库里那一格只可能是库给的。
        Assert.Equal(0, NegativeKeys("[dbo].[QuickSqlCustomer]", "[Id]", "[Name]", customer.Name!));
    }

    [Fact]
    public async Task Each_dependent_gets_its_own_parents_key_rather_than_the_last_one()
    {
        // 这一格判的是"换值按<em>行</em>对上"，不是"换成了本批最后一个键"。
        // 两型同号（各档自己发号）时它照样判得住：换错档就是把甲子的外键换成了乙父的键。
        FixturesReady();

        var db = Session();

        var first = NewCustomer("甲父");
        var second = NewCustomer("乙父");
        var left = NewOrder("甲子");
        var right = NewOrder("乙子");

        db.Add(left);
        db.Add(first);
        db.Add(right);
        db.Add(second);

        left.CustomerId = first.Id;
        right.CustomerId = second.Id;

        Assert.True(left.CustomerId != right.CustomerId, "两只父行的临时键撞在同一颗上：发号那一档坏了");

        var report = await db.SaveChangesAsync(ct);

        // 两只父行的 INSERT 都排在子行之前 —— 换值跑得动的前提在这里，逐行对上才是下面的两条。
        var tables = Inserted(report);

        Assert.True(Array.IndexOf(tables, "QuickSqlOrder") > Array.LastIndexOf(tables, "QuickSqlCustomer"),
            $"父行没有全部排在子行之前：{string.Join(" → ", tables)}");

        Assert.Equal(StoredCustomerId(first), StoredParentOf(left));
        Assert.Equal(StoredCustomerId(second), StoredParentOf(right));
        Assert.NotEqual(StoredCustomerId(first), StoredCustomerId(second));
    }

    [Fact]
    public async Task A_bigint_parent_key_narrows_into_an_int_foreign_key()
    {
        // 主端 bigint、依赖方 int：装箱相等判的是"同型同值"，所以临时键查号必须先规范化成 long ——
        // 那一档要是回退成按装箱值查，这里得到的就是 NotTemp（"不是我发的号"），于是那颗负数被<em>静默写进库</em>，
        // 既不报错也不回填。读回来的值就是这条分叉的判据。
        FixturesReady();

        var db = Session();

        var parent = new TpPrincipal { Note = tagged("长键父") };
        var child = new TpDependent { Note = tagged("短键子") };

        db.Add(child);
        db.Add(parent);

        child.OwnerId = (int) parent.Id;

        Assert.True(child.OwnerId < 0);

        var report = await db.SaveChangesAsync(ct);

        Assert.Equal(["QuickSqlTpPrincipal", "QuickSqlTpDependent"], Inserted(report));

        var realId = OneNumber("[dbo].[QuickSqlTpPrincipal]", "[Id]", "[Note]", parent.Note!);

        Assert.True(realId > 0);
        Assert.Equal(realId, OneNumber("[dbo].[QuickSqlTpDependent]", "[OwnerId]", "[Note]", child.Note!));
        Assert.Equal((long) realId, parent.Id);
        Assert.Equal(0, NegativeKeys("[dbo].[QuickSqlTpDependent]", "[OwnerId]", "[Note]", child.Note!));
    }

    [Fact]
    public async Task An_update_carrying_a_temp_key_is_rewritten_before_the_set_is_built()
    {
        // 另一种形状：某只<em>已在库里</em>的行，外键那一格被赋成了主端还没换出去的临时键。
        // 三档固定次序（先插、再改、最后删）保证了换值跑得到：那一句 SET 排在本次唯一的 INSERT 之后。
        FixturesReady();

        var settled = new Order
        {
            CustomerName = tagged("旧子"),
            CustomerId = 4242,
            Amount = 3m,
            State = OrderState.Placed,
            CreatedAt = Moment,
            Note = tagged("旧子"),
        };

        // 现场用不跟踪的那条链准备：会话的用例不该依赖会话自己的写口（同 TrackingPathTests）。
        var seededId = (int) await Orm.Insert(settled).ExecuteIdentityAsync(ct);

        var db = Session();

        var fresh = NewCustomer("新父");

        db.Add(fresh);

        var moved = NewOrder("旧子");

        moved.Id = seededId;
        moved.CustomerId = fresh.Id;

        db.Update(moved);

        Assert.True(moved.CustomerId < 0);

        var report = await db.SaveChangesAsync(ct);

        Assert.Equal(["QuickSqlCustomer"], Inserted(report));
        Assert.Equal(["QuickSqlOrder"], [.. Ops(report, WriteOperation.Update).Select(o => o.Table)]);

        var tables = report.Operations.Select(o => o.Table).ToArray();

        Assert.True(Array.IndexOf(tables, "QuickSqlOrder") > Array.IndexOf(tables, "QuickSqlCustomer"),
            $"那句 UPDATE 排在了父行的 INSERT 之前：{report}");

        Assert.Equal(StoredCustomerId(fresh), StoredParentOf(moved));
        Assert.Equal(0, NegativeKeys("[dbo].[QuickSqlOrder]", "[CustomerId]", "[Note]", moved.Note!));
    }

    [Fact]
    public async Task A_parent_removed_after_a_child_copied_its_temp_key_refuses_the_write()
    {
        // 孤的那一档：父行离开会话（标删一只还没写出去的新增行等于忘掉它），而引用还留在子行手里。
        // 换不出真实键又不是"别人的键"，所以只有一条路可走 —— 拒，并且一行都不写。
        FixturesReady();

        var db = Session();

        var parent = NewCustomer("要撤的父");
        var child = NewOrder("孤儿引用");

        db.Add(child);
        db.Add(parent);

        child.CustomerId = parent.Id;

        db.Remove(parent);

        var raised = await Assert.ThrowsAsync<QuickSqlException>(async () => await db.SaveChangesAsync(ct));

        Assert.Contains("临时主键", raised.Message, StringComparison.Ordinal);
        Assert.Contains("离开会话", raised.Message, StringComparison.Ordinal);

        Assert.Equal(0, Count("[dbo].[QuickSqlOrder]", "[Note] = @tag", ("@tag", child.Note)));
        Assert.Equal(0, Count("[dbo].[QuickSqlCustomer]", "[Name] = @tag", ("@tag", parent.Name)));
    }

    [Fact]
    public async Task A_self_reference_inside_one_batch_is_refused_rather_than_guessed()
    {
        // 自引用同一批：主端就是自己这一档，而它自己的键此刻还是临时值 —— 类型序排不出同一类型内部的先后，
        // 所以这一档今天换不出（正解是"先插行、再补自己的上级"那两句，欠在 §14）。拒的是一整批，不是半行。
        FixturesReady();

        var db = Session();

        var boss = new TpPeer { Note = tagged("上级") };
        var minion = new TpPeer { Note = tagged("下级") };

        db.Add(minion);
        db.Add(boss);

        minion.UpId = boss.Id;

        Assert.True(minion.UpId < 0);

        var raised = await Assert.ThrowsAsync<QuickSqlException>(async () => await db.SaveChangesAsync(ct));

        Assert.Contains("临时主键", raised.Message, StringComparison.Ordinal);
        Assert.Contains("同一张表的同一批", raised.Message, StringComparison.Ordinal);

        // 拒的是一整批，不是半行：本文件的标签带进程级随机后缀，所以按前缀数就是"这一批一句都没发出去"的判据
        // （抛在语句之前与事务回滚两种来路都算这一条绿，而它判不住的是"少写了一半"）。
        Assert.Equal(0, Count("[dbo].[QuickSqlTpPeer]", "[Note] LIKE @prefix", ("@prefix", Batch + "%")));
    }

    [Fact]
    public async Task After_the_flush_the_session_holds_the_real_key_and_owes_nothing()
    {
        FixturesReady();

        var db = Session();

        var customer = NewCustomer("父");
        var order = NewOrder("子");

        db.Add(order);
        db.Add(customer);

        var temp = customer.Id;

        order.CustomerId = temp;

        await db.SaveChangesAsync(ct);

        var realId = customer.Id;

        // 身份映射登记的是真实键（回填之后才登记，§13.2 第 2 项 ①），那颗临时号从来不在映射里。
        Assert.True(realId > 0);
        Assert.Same(customer, db.Set<Customer>().Local.Find(realId));
        Assert.Null(db.Set<Customer>().Local.Find(temp));

        // 顺带一句这一格的前一半：换值发生在同一趟 Flush 里，所以会话结算之后依赖方手里也该是真实键。
        Assert.Equal(realId, order.CustomerId);

        // 换过值的那些行不该再被写一遍：第二次保存交空报告，否则"回填改动了实体"就会被侦测成一次改动。
        Assert.Empty((await db.SaveChangesAsync(ct)).Operations);
    }
}

/// <summary>主键是 <c>bigint</c> 自增的靶子：<em>键由库给</em>，而那一列比依赖方宽一型。
/// 住在本文件而不是共享模型：加一张表要连着动种子的固定类型清单与整片黄金样本，而它只服务于这一格。</summary>
[Table("QuickSqlTpPrincipal")]
public class TpPrincipal
{
    public long Id { get; set; }

    [QuickColumn(Length = 64)]
    public string? Note { get; set; }
}

/// <summary><see cref="TpPrincipal"/> 的子行：外键列是 <c>int</c>，比主端那一列<em>窄</em>一型。
/// 导航名 <c>Owner</c> ⇒ 约定命中 <c>OwnerId</c>，于是一根边（§10.3）。</summary>
[Table("QuickSqlTpDependent")]
public class TpDependent
{
    public int Id { get; set; }

    public int OwnerId { get; set; }

    [QuickColumn(Length = 64)]
    public string? Note { get; set; }

    public TpPrincipal? Owner { get; set; }
}

/// <summary>自引用的靶子：导航 <c>Up</c> ⇒ 约定外键 <c>UpId</c>，边的目标就是自己这一型（排序时不算边，
/// 但回填要问它 —— 于是"同一张表的同一批"这一档有了现场）。
///
/// <para><b>为什么不用共享模型里那只 <c>SelfRef</c></b>：判"整批被拒"要数库里这一次写了没有，
/// 而它只有 <c>Id</c> 与 <c>ManagerId</c> 两列、没有一个能按标签过滤的文本列 —— 全表计数会把别人
/// （包括本文件自己上一次证伪跑）留下的行算进来。共享那只留给离线层判契约形状。</para></summary>
[Table("QuickSqlTpPeer")]
public class TpPeer
{
    public int Id { get; set; }

    public int? UpId { get; set; }

    [QuickColumn(Length = 64)]
    public string? Note { get; set; }

    public TpPeer? Up { get; set; }
}
