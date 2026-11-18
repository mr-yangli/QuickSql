using System.Threading;

namespace QuickSql.Sql;

/// <summary>'@pN' 具名参数名的跨方言共享缓存——SqlServer / MySQL 血统 / PG 血统同用 '@' 前缀，共用这一颗。
/// <para>默认发号方案 <c>prefix + "p" + count</c> 每格造<em>三颗</em>串："@p" 中间串 + <c>count.ToString()</c> + 最终拼接，
/// 批量插入 30000 格 ≈ 86 B/格 × 30000 = 2.5 MB（门禁 SqlServer 形状实测）。缓存命中后只造缓存深度那几颗。
/// 文本与默认 "@pN" 逐字相同（只共享串实例），绑定按名不变。</para>
/// <para><b>分段惰性增长</b>：64 段 × 1024 槽，覆盖到每块 65536 颗参数（MySQL 预算 65535 在内）。
/// 段按需分配——小批量只用前几段（几 KB），大批量才长到几十段（~240 KB 工作集），不预占大片静态内存。
/// 超出 65536 退化回现场造，不丢正确性。良性竞态无锁：<see cref="Interlocked.CompareExchange"/> 装段、
/// 串不可变所以同槽两线程各造一份赢的那份留下，产物逐字相同。</para>
/// <para><b>复用动态</b>：SqlServer/SQLite 小块、@p0..@pN 跨块重复，首轮即省；MySQL/PG 预算超大、千行塞一块、
/// 块内名各用一次，首轮缓存 miss 现造、第二轮起复用才省（BDN 多轮稳态吃得到）。只挂批量插入发射口
/// （InsertEngine.EmitChunk 读 dialect.ParameterNamer），读侧/单条写仍走默认发号。</para></summary>
public static class AtParameterNames
{
    const int SegmentBits = 10;
    const int SegmentSize = 1 << SegmentBits;
    const int SegmentMask = SegmentSize - 1;

    static readonly string?[][] Segments = new string?[64][];

    /// <summary>缓存版 "@pN" 发名委托，方言 ParameterNamer 直接交这颗（静态缓存，发射口每块只读不造委托）。</summary>
    public static readonly Func<int, string> Namer = static ordinal =>
    {
        var seg = ordinal >> SegmentBits;

        if ((uint)seg >= (uint)Segments.Length)
            return "@p" + ordinal;

        var segment = Volatile.Read(ref Segments[seg]);

        if (segment is null)
        {
            Interlocked.CompareExchange(ref Segments[seg], new string?[SegmentSize], null);
            segment = Volatile.Read(ref Segments[seg]);
        }

        var idx = ordinal & SegmentMask;
        return segment[idx] ??= "@p" + ordinal;
    };
}