using System.ComponentModel.DataAnnotations.Schema;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>内存泄漏的两颗钉子。托管侧"泄漏"只有两族：<b>对象被根引用钉死</b>（会话没还、委托经闭包
/// 钉住实体图）与<b>静态集合无界增长</b>（缓存键空间随调用数线性长开）。这一层各落一颗：
///
/// <para>① <b>WeakReference 探针</b>：一只用完即弃的 <see cref="DbContext"/> 在跑过一次真往返、Dispose
/// 之后必须能被回收。探针本体拆在 <c>[NoInlining]</c> 的辅助方法里 —— Debug 下局部变量活到方法尾，
/// 探针与被测对象同方法时它永远"活着"，那是假绿；Release 下 JIT 又可能延长生命周期，同一个形状两边都稳。
/// 双 <c>GC.Collect</c> 夹一次 <c>WaitForPendingFinalizers</c> 是终结队列清干净的最低配置。</para>
///
/// <para>② <b>同形状重复执行不涨缓存</b>：<see cref="Materializer"/> 里那颗
/// <c>static ConcurrentDictionary&lt;ReaderKey, Delegate&gt;</c> 的键空间是否有限，取决于投影形状会不会被
/// 动态造出来。钉法是反过来的：<b>同一形状跑一万遍，缓存计数一格不涨</b>。计数走反射（字段私有，
/// 借 <c>ICollection.Count</c> 绕开键类型私有这一格）——反射钉私有字段是这一颗用例的故意为之：
/// 字段哪天改了名，这颗会以 <see cref="InvalidOperationException"/> 红，那正是要的信号（口径变了必须有人改这里）。</para>
///
/// <para>判据同 <c>MaterializationAllocationTests</c> 那条口径：<b>只取计数不取时间</b> —— 计数与机器负载无关，
/// 能断"等于"；窗口前后夹的是同一个进程、同一次测试执行（<c>parallelizeTestCollections</c> 已关），
/// 别的类不会在窗口里替缓存添格。</para></summary>
public sealed class LeakPathTests
{
    /// <summary>① 会话探针：跑一次真往返、Dispose，然后问 GC 要人。
    /// <para>查询用 <see cref="StressProbeRow"/> 的形状 —— 若哪天缓存的读取委托经闭包钉住了会话
    /// （或会话的连接账本没随 Dispose 清空），这颗就会红。</para></summary>
    [Fact]
    public void A_disposed_context_is_collected()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StressProbeRow));

        WeakReference probe = QueryThenDispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(probe.IsAlive,
            "Dispose 过的 DbContext 仍然被根引用着：会话没还、或某个静态缓存经委托钉住了它。");
    }

    /// <summary>② 缓存有界：同一形状一万遍，<see cref="Materializer"/> 的读取委托缓存一格不涨。
    /// <para>首建发生在预热那一遍（记在窗口之前），窗口内只应命中、不应再写。</para></summary>
    [Fact]
    public void The_same_reader_shape_does_not_grow_the_materializer_cache()
    {
        Database.Ready();
        Database.EnsureTable(typeof(StressProbeRow));

        QueryOnce();
        var before = DtoReaderCount();

        for (var i = 0; i < 10_000; i++)
            QueryOnce();

        Assert.Equal(before, DtoReaderCount());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference QueryThenDispose()
    {
        var ctx = new DbContext(Database.Orm);
        _ = ctx.Set<StressProbeRow>().Where(r => r.Sku == "leak-probe").ToList();
        var probe = new WeakReference(ctx);
        ctx.Dispose();
        return probe;
    }

    static void QueryOnce()
    {
        using var ctx = new DbContext(Database.Orm);
        _ = ctx.Set<StressProbeRow>().Where(r => r.Sku == "leak-probe").ToList();
    }

    static int DtoReaderCount()
    {
        var field = typeof(Materializer).GetField("DtoReaders", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Materializer.DtoReaders 改名或换宿主了：LeakPathTests 的缓存口径要跟着改。");

        return ((ICollection) field.GetValue(null)!).Count;
    }
}

/// <summary>泄漏与并发两份共用的一张靶表：列型全是各家通吃的那几格（整型自增主键、定长串、整数），
/// 没有并发列、没有分表、没有哪一家的方言专属形状 —— 这两张表存在的意义就是"在任何一家都能建、
/// 能并发写、能逐行对账"。表名与种子表同前缀，建表走 <see cref="Database.EnsureTable"/>（幂等，缺才建）。</summary>
[Table("QuickSqlStressProbe")]
public class StressProbeRow
{
    public int Id { get; set; }

    [QuickColumn(Length = 64)]
    public string? Sku { get; set; }

    public int Quantity { get; set; }

    /// <summary>写下它的工人号：并发对账按它分桶，谁的账短了当场点名。</summary>
    public int Worker { get; set; }
}

/// <summary>并发首建专用的第二张形状：只被"十六线程同时首读"那一颗用例物化，保证撞的是
/// <see cref="Materializer"/> 惰性初始化的第一次 —— 而不是别的用例已经焐热的缓存。</summary>
[Table("QuickSqlStressFirst")]
public class StressProbeFirstRow
{
    public int Id { get; set; }

    [QuickColumn(Length = 64)]
    public string? Sku { get; set; }
}
