namespace QuickSql.Tracking;

/// <summary>身份映射的键：主键各列的值组成的序列（类型那一半长在 EntityTracker〈T〉 上，这里不带 Type）。
/// 手写相等而不是包一个 record：object?[] 作为记录成员时合成的相等走 StructuralEquality，
/// 每比较一次就把序列拷进一个新数组再逐项处理；这里哈希一次算完存字段、Equals 逐项比较，不分配。
/// 值本身就是装箱的（schema.GetValue 返回 object），装箱发生在取值那一步，不是这里加的。
/// byte[] 特判按内容判：varbinary 主键是合法 DDL，而 object.Equals 对数组是引用相等，
/// 两次物化的同键行判不成同一身份、Find 永远不中；哈希用前 8 字节 + 长度折进去，相等用 SequenceEqual。</summary>
readonly struct IdentityKey(object?[] values) : IEquatable<IdentityKey>
{
    readonly object?[] _values = values;

    public readonly int HashCode = ComputeHash(values);

    public bool Equals(IdentityKey other)
    {
        var values = _values;

        if (values.Length != other._values.Length)
            return false;

        for (var i = 0; i < values.Length; i++)
        {
            var left = values[i];
            var right = other._values[i];

            if (left is byte[] bytes && right is byte[] otherBytes)
            {
                if (!bytes.AsSpan().SequenceEqual(otherBytes))
                    return false;

                continue;
            }

            if (!Equals(left, right))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is IdentityKey key && Equals(key);

    public override int GetHashCode() => HashCode;

    /// <summary>逐项混合。顺序参与哈希：复合主键 (A, B) 与 (B, A) 是两个不同的键，
    /// xor 折叠会把它们算成同一个（相等判据仍然对，只是碰撞变多）。</summary>
    static int ComputeHash(object?[] values)
    {
        var hash = new HashCode();

        foreach (var value in values)
        {
            // byte[] 的哈希按内容算：HashCode 对数组走引用哈希，与上面的内容相等判据必须同源，
            // 否则同键两实例哈希不同、字典永远查不中。
            if (value is byte[] bytes)
            {
                hash.Add(bytes.Length);

                for (var i = 0; i < bytes.Length && i < 8; i++)
                    hash.Add(bytes[i]);

                continue;
            }

            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
