using System.Collections.Frozen;

namespace QuickSql.Schema;

/// <summary>按 Type 惰性填充的无锁读表：读走冻结表快照（一次易失读 + 一次查找，无锁、无 CAS、无分桶），
/// 写时先在锁内更新种子表、再把整张新表一次性换掉指针（copy-on-write）。写入不是运行期的事，
/// 是每个实体第一次被问到的那一次、一生每型一次；每写一次重建整表的钱按实体总数付（几十到几百的量级），
/// 换到的是此后每一次读都没有锁。快照的发布只有一个方向：写侧永远在锁内基于当前种子表重建，
/// 后写的那一份一定包含先写的那一份，读侧只会看到"少一个键"或"全都有"，不会看到半张表。</summary>
sealed class TypeTable<TValue>
{
    readonly Dictionary<Type, TValue> _seed = [];
    readonly object _gate = new();

    FrozenDictionary<Type, TValue> _table = new Dictionary<Type, TValue>().ToFrozenDictionary();

    /// <summary>无锁读路径：命中就直接给。</summary>
    public bool TryGet(Type key, out TValue? value) => Volatile.Read(ref _table).TryGetValue(key, out value);

    /// <summary>取，未见过这一型就当场建。工厂的额外参数照 ConcurrentDictionary 那一份形状留着：
    /// 两个注册表都靠它避免"每次调用 alloc 一个闭包"。</summary>
    public TValue GetOrAdd<TArg>(Type key, Func<Type, TArg, TValue> factory, TArg arg)
    {
        if (TryGet(key, out var hit))
            return hit!;

        lock (_gate)
        {
            if (_seed.TryGetValue(key, out var existing))
                return existing;

            var value = factory(key, arg);

            _seed[key] = value;
            Publish();

            return value;
        }
    }

    /// <summary>记账用的插入（已存在则不动）：注册表用它维护"解析过哪些实体"。</summary>
    public void Add(Type key, TValue value)
    {
        if (TryGet(key, out _))
            return;

        lock (_gate)
        {
            if (_seed.ContainsKey(key))
                return;

            _seed[key] = value;
            Publish();
        }
    }

    /// <summary>已登记的键。读的是快照，所以调用方拿到的是当时那一刻的集合，不会边读边变。</summary>
    public IReadOnlyCollection<Type> Keys => Volatile.Read(ref _table).Keys.ToArray();

    public int Count => Volatile.Read(ref _table).Count;

    void Publish() => Volatile.Write(ref _table, _seed.ToFrozenDictionary());
}
