using QuickSql.Metadata;

namespace QuickSql.Tracking;

/// <summary>EntityEntry 的落地。它只是一个视图，不持有任何状态：全部读写都落在 Node 与 EntityTracker 上，
/// Entry(o) 拿两次、或拿完之后又 DetectChanges 一次，看到的都是当时的事实，没有第二份要同步的副本。</summary>
sealed class TrackedEntry<T>(EntityTracker<T> tracker, Node node) : EntityEntry<T> where T : class
{
    /// <summary>基类那一个抽象成员（公开的 Entity 两档都由它转交）。</summary>
    protected override object EntityInstance => node.Entity;

    public override Type EntityType => typeof(T);

    /// <summary>赋值走 ApplyState，连带效果一件不少：Unmodified 会重拍快照，Modified 会把可写列全标脏，
    /// Detached 会真的把它从身份映射里摘出去。直接写 node.State = value 才是那个"看起来能用其实只做了一半"的版本。</summary>
    public override EntityState State
    {
        get => node.State;
        set => tracker.ApplyState(node, value);
    }

    public override bool IsModified => tracker.WillWrite(node);

    public override IReadOnlyList<string> ModifiedMembers => tracker.ModifiedMembers(node);

    public override PropertyEntry Property(string memberName) => tracker.Property(node, memberName);
}

/// <summary>一列的读写入口。三样东西各有各的出处，这正是这一格容易写错的地方：
/// CurrentValue 在实体上、OriginalValue 在节点的快照里、IsModified 在"覆盖 + 侦测结论"的合成结果里。
/// 前两者是数据，最后一个是意图。</summary>
sealed class TrackedProperty<T>(EntityTracker<T> tracker, Node node, int ordinal, ColumnContract column) : PropertyEntry
    where T : class
{
    public override string MemberName => column.MemberName;

    /// <summary>读写的是实体本身那一格，不是快照。写它之后 DetectChanges 才看得见这次改动——
    /// 「改 CurrentValue 就等于改实体」是这一档存在的理由，否则它就成了一个改了没用的 setter。</summary>
    public override object? CurrentValue
    {
        get => tracker.Value((T) node.Entity, ordinal);
        set => tracker.Write((T) node.Entity, ordinal, value);
    }

    public override object? OriginalValue => node.Snapshot[ordinal];

    /// <summary>读的是合成结论（覆盖优先，否则比较），写的是覆盖——两者不是一回事，
    /// 所以 IsModified = IsModified 这句看起来无害的写法会把"没表过态"变成"表过态"，从此这一列不再参与比较。
    /// 要退回比较只有一个办法：把整行的状态重设（Entry(o).State = Unmodified 会清掉所有覆盖）。</summary>
    public override bool IsModified
    {
        get => node.Dirty[ordinal];
        set
        {
            node.Overrides[ordinal] = value;
            node.Dirty[ordinal] = value;

            if (value)
                tracker.MarkWrites();
        }
    }
}
