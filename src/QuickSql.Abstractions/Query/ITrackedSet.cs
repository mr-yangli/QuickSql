using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace QuickSql.Query;

/// <summary>跟踪集合。名字不用 ISet：那与 System.Collections.Generic 的 ISet 同形，开着隐式 using 的
/// 每一个消费方一写就得到 CS0104"不明确的引用"——一个要靠调用方加别名才写得出来的公开契约，
/// 等于把改名没改完的账摊给所有人。它是 IQuery 而不是 IQueryable：链式全部可用、from-in 查询语法不可用。
/// 跟踪与否不是两套 API：本接口与 IQuery 共用同一个查询引擎与同一份 QueryModel，
/// 区别只在引擎上有没有挂会话的跟踪器——于是 orm.Select() 与 db.Set().AsNoTracking() 产出的模型文本逐字节相等。</summary>
public interface ITrackedSet<T> : IQuery<T> where T : class
{
    /// <summary>会话里已跟踪实例的内存视图（Local 属性）。
    /// 只反映本会话见过的实体，不等于库里有什么，也不含别的会话的实体。</summary>
    LocalView<T> Local { get; }

    /// <summary>给已跟踪实例补载导航。
    /// 发 SQL 把导航属性填上：引用导航与集合导航都走这一颗口。
    /// 返回 this 让链式继续。</summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    ITrackedSet<T> Load<TProp>(Expression<Func<T, TProp>> navigation);

    /// <summary>按主键找一个实体。
    /// 先查内存（身份映射里有就直接返回），再查库（发一条按主键的 SELECT，
    /// 找到就纳入会话并返回，找不到返回 null）。与 LocalView.Find 的分别是后者只查内存、不发 SQL。</summary>
    T? Find(params object?[] primaryKeyValues);

    /// <summary>Find 的异步版。</summary>
    Task<T?> FindAsync(CancellationToken ct = default, params object?[] primaryKeyValues);

    /// <summary>从库重新加载一个实体的所有列。
    /// 库值覆盖实体当前值，然后重拍快照、清脏。已标 Added 或 Modified 的实例不动
    /// （用户有未保存的改动，库值不该覆盖）；不在会话里的实例按 Unmodified 纳入。</summary>
    void Refresh(T entity);

    /// <summary>Refresh 的异步版。</summary>
    Task RefreshAsync(T entity, CancellationToken ct = default);
}

/// <summary>已跟踪实例的内存视图。
/// 它是快照而不是活集合：每次枚举取当时的那一份，所以 foreach 中途 Remove 不会炸，
/// 但也看不到自己刚删的那一步（Deleted 的实例仍在快照里直到写完成）。
/// 不实现 INotifyCollectionChanged：那是 UI 绑定的红利，换它要付的是每行一次通知——
/// 批量装载一万行的那条路上，这笔钱没有任何本项目内的消费者。</summary>
public sealed class LocalView<T> : IReadOnlyList<T> where T : class
{
    readonly Func<IReadOnlyList<T>> _snapshot;
    readonly Func<object?[], T?> _find;

    internal LocalView(Func<IReadOnlyList<T>> snapshot, Func<object?[], T?> find)
    {
        _snapshot = snapshot;
        _find = find;
    }

    /// <summary>当时那一份的引用，不是拷贝（每行拷一份数组等于给每次访问加一笔分配）。</summary>
    public IReadOnlyList<T> Entries => _snapshot();

    public int Count => Entries.Count;

    public T this[int index] => Entries[index];

    /// <summary>按主键在内存里找，不发 SQL，所以"找不到"只有两种含义：
    /// 本会话没读过/没 Add 过这个键，或者库里那一行已经不是这个键了。
    /// 想问"库里有没有"要用 ITrackedSet 的 Find（先查内存再查库）。</summary>
    public T? Find(params object?[] primaryKeyValues)
    {
        ArgumentNullException.ThrowIfNull(primaryKeyValues);

        return _find(primaryKeyValues);
    }

    public IEnumerator<T> GetEnumerator() => Entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
