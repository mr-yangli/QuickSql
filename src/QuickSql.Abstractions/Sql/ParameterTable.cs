using QuickSql.Metadata;
using QuickSql.Values;
using System.Collections;
using System.Data;

namespace QuickSql.Sql;

/// <summary>SQL 书写器与 <see cref="DialectSql"/> 共用的参数表：<b>每格一个 struct entry、每语句一次懒物化</b>。
/// 公开形状仍是 <see cref="IReadOnlyList{T}"/> of <see cref="AdoParameter"/>——消费方（钩子、日志、Explain、
/// 驱动绑定退路）一字不改；但热路径（发射 → 执行绑定）只碰 struct entry，不为每格 new 一颗 record
/// （1000×30 = 30000 颗 ≈ 1.4 MB/批，增/改/删/读参数四路共付的那笔）。
///
/// <para>存储是 <b>Entry[] + 手动计数</b>而不是 List&lt;Entry&gt;：宽 struct 的 List 倍增扩容垃圾比窄引用表
/// 贵一个量级（净室探针 obj/factspath 实测：零容量 List&lt;Entry&gt; 145.8 B/格 vs 旧 List&lt;AdoParameter&gt;+record
/// 88.0 B/格），且每语句多一颗 List 对象。热路径构造口给准容量（块行数×列数）即零扩容；
/// 冷路径不给容量时倍增仍比 List 省一颗对象。</para>
///
/// <para>物化时机＝第一次有人按 <see cref="AdoParameter"/> 读这张表（索引器或枚举）：整表一次建齐、
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 发布（良性竞态：并发首读各建一份、值逐字相同、
/// 一份赢其余回收，与 AtParameterNames 名缓存同一纪律）。执行层走 internal 的 <see cref="Entries"/>
/// 直绑 struct entry，不触发物化；ParameterBinding / CommandTerminating / 日志 / Explain 订阅或诊断时才付 record 钱。</para>
///
/// <para>entry 的值半来自 <see cref="IValueBinder.FactsOf"/>（或 record 登记时的原样抄字段）；
/// record 登记的那一格物化时交回<b>原实例</b>（钩子拿到的与登记的是同一颗，identity 不丢）。</para></summary>
public sealed class ParameterTable : IReadOnlyList<AdoParameter>
{
    internal readonly struct Entry
    {
        public Entry(string name, string? driverName, ColumnContract? hint, object? value, DbType dbType, int size, AdoParameter? record)
        {
            Name = name;
            DriverName = driverName;
            Hint = hint;
            Value = value;
            DbType = dbType;
            Size = size;
            Record = record;
        }

        public string Name { get; }
        public string? DriverName { get; }
        public ColumnContract? Hint { get; }
        public object? Value { get; }
        public DbType DbType { get; }
        public int Size { get; }
        public AdoParameter? Record { get; }

        public AdoParameter ToRecord() => Record ?? new AdoParameter(Name, Value, DbType, Size, Hint, DriverName);
    }

    Entry[] _entries;
    int _count;
    AdoParameter[]? _materialized;

    public ParameterTable(int capacity = 0)
    {
        _entries = capacity > 0 ? new Entry[capacity] : [];
    }

    public int Count => _count;

    public AdoParameter this[int index] => Materialize()[index];

    /// <summary>执行层直绑口：struct entry span，不物化 record。编号 = 下标 = 发射序 = 绑定序（SqlWriter 同一纪律）。</summary>
    internal ReadOnlySpan<Entry> Entries => _entries.AsSpan(0, _count);

    internal void Add(AdoParameter record)
        => AddCore(new Entry(record.Name, record.DriverName, record.Hint, record.Value, record.DbType, record.Size, record));

    internal void Add(string name, in ParameterFacts facts, ColumnContract? hint)
        => AddCore(new Entry(name, facts.DriverName, hint, facts.Value, facts.DbType, facts.Size, null));

    void AddCore(Entry entry)
    {
        if (_count == _entries.Length)
            Array.Resize(ref _entries, Math.Max(4, _entries.Length * 2));

        _entries[_count++] = entry;
    }

    AdoParameter[] Materialize()
    {
        if (_materialized is { } ready)
            return ready;

        var array = new AdoParameter[_count];

        for (var i = 0; i < array.Length; i++)
            array[i] = _entries[i].ToRecord();

        Interlocked.CompareExchange(ref _materialized, array, null);

        return _materialized!;
    }

    public IEnumerator<AdoParameter> GetEnumerator() => ((IEnumerable<AdoParameter>)Materialize()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}