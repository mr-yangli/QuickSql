using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>v0.21「临时主键（<c>Tp</c>）」在<b>离线</b>能判的那一半：号发不发、发给谁、临时值进不进身份映射、
/// 拿它当定位符的那几句拒不拒。
///
/// <para><b>回填本身判不到，而且判不到是有原因的</b>：换值那一步（<c>EntityTracker.ResolveTempKeys</c>）
/// 在 <c>WriteInsertsAsync</c> 里、而它排在 <c>BeginTransaction</c> 之后 —— 离线在这一句之前就先撞上驱动探针。
/// 于是"依赖方手里那颗负数被换成库里给的真实键"只能在真库上看（集成层 <c>TpPathTests</c>），
/// 而这里判的正好是它的前一半：<b>临时值进得了实体、进不了身份映射，也进不了任何一句 WHERE 的合法性</b>。
/// 两边合起来才是这一格的完整形状，缺哪一半都会留出一个"看起来能用"的缝。</para>
///
/// <para>靶子分两层：<see cref="Customer"/>/<see cref="Project"/> 是 Common 里的真模型（int 自增键 /
/// 客户端 <c>Guid</c> 键两种形态），<see cref="LongKey"/> 是本文件自建的 <c>long</c> 自增键
/// （号要装得回自己那一列；它与 <c>int</c> 外键那一对"装箱后不相等"的型差在集成层真跑）。
/// 注册表一律走 G2 反射那一份：本文件没有源生成器，而这一格问的是跟踪器的行为，不是两侧元数据是否同源
/// （对拍归 §13.11 与 <c>MetadataEquivalenceTests</c>）。</para></summary>
public sealed class TempKeyTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static IEntitySchemaRegistry Reflected() => new EntitySchemaRegistry(new ContractResolver(Provider, null));

    static QuickSqlOrm Offline() => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = "offline://no-driver" }, Reflected());

    static DbContext Session() => new(Offline());

    [Fact]
    public void An_added_row_with_an_unset_identity_key_leaves_a_negative_temp_behind()
    {
        var db = Session();

        var first = db.Add(new Customer { Name = "甲" });
        var second = db.Add(new Customer { Name = "乙" });

        // 负数 = 库里那一列不可能有的值（IDENTITY 种子不是 0 也不是负数），所以它能在手里当引用用而不会被当成键。
        Assert.True(first.Id < 0, $"没发临时键：Id 还是 {first.Id}");
        Assert.True(second.Id < 0, $"没发临时键：Id 还是 {second.Id}");

        // 两只都没填键的实例各领一个号 —— 这正是 v0.20 之前那句「按引用跟、同键判不出」的正面解：
        // 之前它们都登记成 Registered = null，于是第二次保存把两行都插出去，而身份映射里一个都没有。
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void The_temp_key_is_in_the_session_but_not_in_the_identity_map()
    {
        var db = Session();

        var customer = db.Add(new Customer { Name = "还没进库" });

        // 它确实在会话里：Local 那份列表看得得到（同一只引用）。
        Assert.Contains(db.Set<Customer>().Local, c => ReferenceEquals(c, customer));

        // 但它<em>查不到</em>：身份映射里没有那颗负数，所以 Find 不会把一个"还没进库的意图"当成"库里的一行"。
        Assert.Null(db.Set<Customer>().Local.Find(customer.Id));

        // 快照那一格仍是"没填"：发号在登记之后，所以 OriginalValue 说的还是「库里没有这一行」而不是「库里那行是 -1」。
        Assert.Equal(0, db.Entry(customer).Property(c => c.Id).OriginalValue);
    }

    [Fact]
    public void A_key_the_caller_filled_in_is_left_alone()
    {
        var db = Session();

        var given = db.Add(new Customer { Id = 7, Name = "键我自己填的" });

        Assert.Equal(7, given.Id);
        Assert.Same(given, db.Set<Customer>().Local.Find(7));

        // 临时键恒为负，所以调用方自己用的正数键既不会被抢号、也不会被改写路径碰一下。
        var negative = db.Add(new Customer { Id = -5, Name = "负数键也是键" });

        Assert.Equal(-5, negative.Id);
        Assert.Same(negative, db.Set<Customer>().Local.Find(-5));
    }

    [Fact]
    public void A_client_generated_key_gets_no_temp_because_nothing_would_hand_one_back()
    {
        var db = Session();

        var project = db.Add(new Project());

        // 这一格钉的是"没实现的那一格别看起来像实现了"：Guid 主键不是自增列，库不会在回填时把键给回来，
        // 所以会话不替它编一个号 —— 空着就是空着，写完库里那一行的键就是 Guid.Empty（§14 的 P1 余项那一格）。
        Assert.Equal(Guid.Empty, project.Id);
        Assert.Null(db.Set<Project>().Local.Find(project.Id));
    }

    [Fact]
    public void A_temp_key_is_refused_as_a_locator_by_Attach_Update_and_Remove()
    {
        var db = Session();

        var tracked = db.Add(new Customer { Name = "父" });
        var temp = tracked.Id;

        // 三句都是"拿这颗值去定位库里那一行"，而那一句会命中 0 行却报告"0 行" —— 静默丢写（§12.2）。
        // 三只冒牌实例各自进一条入口，互不干扰：Attach 认引用、Update 走 Attach、Remove 走 RequireDefinite。
        foreach (var entry in new Func<object>[]
                 {
                     () => db.Attach(new Customer { Id = temp }),
                     () => db.Update(new Customer { Id = temp }),
                     () => db.Remove(new Customer { Id = temp }),
                 })
        {
            var raised = Checks.Raised<QuickSqlException>(() => entry());

            Assert.Contains("临时主键", raised.Message, StringComparison.Ordinal);
            Assert.Contains(temp.ToString(), raised.Message, StringComparison.Ordinal);
        }

        // 显式把状态推到 Modified 同一条判据（它内部也走 RequireDefinite）。
        var marked = Checks.Raised<QuickSqlException>(() => db.Entry(tracked).State = EntityState.Modified);

        Assert.Contains("临时主键", marked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Marking_an_added_row_for_deletion_forgot_it_rather_than_deleting_it()
    {
        var db = Session();

        var customer = db.Add(new Customer { Name = "反悔了" });

        // 入口先取：Entry 是视图不是拷贝，而 Detach 会把这只实例摘出引用表 —— 之后再 Entry(那只)是要抛的。
        var entry = db.Entry(customer);

        db.Remove(customer);

        // 库里没有这一行，删它的正确执行方式是忘掉它（v0.16 那条判据在 Tp 之后仍然对）。
        Assert.Equal(EntityState.Detached, entry.State);
        Assert.Empty(db.Set<Customer>().Local);
    }

    [Fact]
    public void A_long_identity_key_gets_a_long_temp()
    {
        var db = Session();

        var row = db.Add(new LongKey { Name = "bigint 键" });

        // 键型由契约决定而不是被折成 int：回填时要按数值认号，而那一颗号得装得回它自己那一列。
        Assert.True(row.Id < 0, $"bigint 主键没发临时键：{row.Id}");
        Assert.IsType<long>(db.Entry(row).Property(r => r.Id).CurrentValue);
    }

    [Fact]
    public void Attaching_a_row_that_already_has_a_key_does_not_allocate_a_temp()
    {
        var db = Session();

        // 物化/Attach 那一档不发号：那一行本来就在库里，给它编一个负键等于替它否认已有的键，
        // 而且之后任何一次改写路径都可能把别人的引用挪到这一行上。
        var attached = db.Attach(new LongKey { Id = 42, Name = "库里已有" });

        Assert.Equal(42, attached.Id);
        Assert.Same(attached, db.Set<LongKey>().Local.Find(42L));
    }
}

/// <summary><c>long</c> 自增主键。这里只用它"键型是 long"那一半：发出去的号得装得回自己那一列，
/// 而"<c>long</c> 主键配 <c>int</c> 外键"那一对装箱后不相等的型差在集成层真跑（<c>TpPathTests</c>）。</summary>
[Table("QuickSqlLongKey")]
public class LongKey
{
    public long Id { get; set; }

    [QuickColumn(Length = 64)]
    public string? Name { get; set; }
}
