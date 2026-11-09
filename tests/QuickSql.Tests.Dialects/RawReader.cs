using System.Collections;
using System.Data;
using System.Data.Common;

namespace QuickSql.Tests.Dialects;

/// <summary>一片<b>真的会走原始访问器</b>的假驱动。<see cref="DataTableReader"/> 顶不了这个位：
/// 它的每一条 <c>GetXxx</c> 都是"从 <see cref="DataRow"/> 拿 object 再转"，所以装箱一分不省 ——
/// 台账记的就是这条性质（§12.7 C 末尾那句"假驱动更差：它没实现强类型路径，装箱一分不省"，
/// 以及 §12.8 第 3 行至今仍是 736 B/行的原因）。
///
/// <para>于是"直读臂到底省没省装箱"这一格，在离线册里只能由一个<b>存储本身就是类型化数组</b>的替身来判：
/// <see cref="GetValue"/> 现场装箱（每列一颗盒子，那是装箱口的代价），
/// <see cref="GetInt32"/> 这类只从数组里取值类型（0 B，那是原始访问器的定义），
/// 与真驱动（Microsoft.Data.SqlClient 在 LocalDB 上实测 89.0 ns / 0 B 对 95.2 ns / 24 B）同形。
/// <b>这里没有为了"让用例好看"而改的东西</b>：列类型由 <see cref="GetFieldType"/> 原样回答，
/// 空值由 <see cref="IsDBNull"/> 回答，两条都与真驱动的口径一致。</para>
///
/// <para><see cref="FieldTypeAsks"/> 是量具而不是产品口：它是替身自己记的账，
/// 产品代码里没有任何计数器（§12.7 C 的"不为测试开观测口"那条）。
/// 有了它，"探测是按场次还是按值"这件事才能被<em>直接数</em>出来 ——
/// 而不是只能从分配的字节数去反推（那条路在 <see cref="DataTableReader"/> 上永远是平的）。</para></summary>
public sealed class RawReader : DbDataReader
{
    readonly string[] names;
    readonly Type[] types;
    readonly Array[] values;
    readonly bool[][] nulls;
    readonly int rows;

    int row = -1;

    bool closed;

    public RawReader(params (string Name, Type Type, Array Values, bool[]? Null)[] columns)
    {
        names = [.. columns.Select(c => c.Name)];
        types = [.. columns.Select(c => c.Type)];
        values = [.. columns.Select(c => c.Values)];
        nulls = [.. columns.Select(c => c.Null ?? new bool[c.Values.Length])];
        rows = columns.Length == 0 ? 0 : columns[0].Values.Length;
    }

    /// <summary>驱动被问过 <c>GetFieldType</c> 的序号，按询问顺序记。
    /// 判"探测是每场次一次还是每值一次"就看这一列的长度与内容。</summary>
    public List<int> FieldTypeAsks { get; } = [];

    /// <summary>被问过 <c>IsDBNull</c> 的<b>次数</b>（与 <see cref="FieldTypeAsks"/> 同一件量具，不是产品口）。
    /// 第八笔给标量出口加的那一截就问在这里：那一档的"这一列能不能不装箱"由出口的票答，
    /// 而"这一格是不是空值"在<b>非空值类型</b>那一族臂上没人问（第七笔丁跳掉了），所以问它的只剩出口本身 ——
    /// 于是这一颗数正好把"空值守归谁问"量成可数的东西：有臂 ⇒ 每值一颗，落门 ⇒ 一颗也没有。
    ///
    /// <para><b>为什么它是一颗数而不是一张清单</b>（与 <see cref="FieldTypeAsks"/> 不同形）：这一问是
    /// <em>每值</em>一次，而 <c>GetFieldType</c> 是每场次一次。做成 <c>List</c> 的记账会在
    /// <c>ZeroBoxingTests</c> 与 <c>ScalarExitTests</c> 那批"每值零装箱"的量法里自己长出分配，
    /// 把要量的东西量成量具的私货；一颗 <c>int</c> 不自增堆，所以两侧读数仍然只算产品的。</para></summary>
    public int NullAsks;

    /// <summary>被问过 <c>GetName</c> 的<b>次数</b>（同一件量具，第三颗）。它判的是原生 SQL 那条出口
    /// 「名字对位按场次还是按行」：<c>RawRowReader</c> 只该在第一行把整张列名表读一遍，
    /// 于是 N 列 × 三行的读数恰好是 N。形状与 <see cref="NullAsks"/> 同格（一颗数，不是清单）。</summary>
    public int NameAsks;

    public override Type GetFieldType(int ordinal)
    {
        FieldTypeAsks.Add(ordinal);

        return types[ordinal];
    }

    public override int FieldCount => names.Length;

    public override int VisibleFieldCount => names.Length;

    public override int Depth => 0;

    public override bool HasRows => rows > 0;

    public override bool IsClosed => closed;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => Raw(ordinal);

    public override object this[string name] => Raw(Array.IndexOf(names, name));

    public override bool Read()
    {
        row++;

        return row < rows;
    }

    public override bool NextResult() => false;

    public override void Close() => closed = true;

    public override string GetName(int ordinal)
    {
        NameAsks++;

        return names[ordinal];
    }

    public override int GetOrdinal(string name) => Array.IndexOf(names, name);

    public override string GetDataTypeName(int ordinal) => types[ordinal].Name;

    public override object GetValue(int ordinal) => Raw(ordinal);

    public override int GetValues(object[] values)
    {
        Array.Copy(this.values.Select((_, i) => Raw(i)).ToArray(), values, Math.Min(values.Length, names.Length));

        return Math.Min(values.Length, names.Length);
    }

    public override bool IsDBNull(int ordinal)
    {
        NullAsks++;

        return nulls[ordinal][row];
    }

    public override bool GetBoolean(int ordinal) => Typed<bool>(ordinal);

    public override byte GetByte(int ordinal) => Typed<byte>(ordinal);

    public override char GetChar(int ordinal) => Typed<char>(ordinal);

    public override DateTime GetDateTime(int ordinal) => Typed<DateTime>(ordinal);

    public override decimal GetDecimal(int ordinal) => Typed<decimal>(ordinal);

    public override double GetDouble(int ordinal) => Typed<double>(ordinal);

    public override float GetFloat(int ordinal) => Typed<float>(ordinal);

    public override Guid GetGuid(int ordinal) => Typed<Guid>(ordinal);

    public override short GetInt16(int ordinal) => Typed<short>(ordinal);

    public override int GetInt32(int ordinal) => Typed<int>(ordinal);

    public override long GetInt64(int ordinal) => Typed<long>(ordinal);

    public override string GetString(int ordinal) => Typed<string>(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw NotSupported(nameof(GetBytes));

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => throw NotSupported(nameof(GetChars));

    public override IEnumerator GetEnumerator() => throw NotSupported(nameof(GetEnumerator));

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => Task.FromResult(IsDBNull(ordinal));

    public override T GetFieldValue<T>(int ordinal) => (T) Raw(ordinal);

    protected override void Dispose(bool disposing)
    {
        closed = true;

        base.Dispose(disposing);
    }

    /// <summary>装箱那一条路：<b>每次都新造一颗盒子</b>（<c>Array.GetValue</c> 的返回类型是 <see cref="object"/>）。
    /// 这正是 §12.7 C 那张表里 24 B 一格的来源，不是替身的私货。</summary>
    object Raw(int ordinal) => nulls[ordinal][row] ? DBNull.Value : values[ordinal].GetValue(row)!;

    /// <summary>原始访问器那一条路：从类型化数组里取值类型，<b>零装箱</b>。
    /// 空值上调用是抛而不是给 <c>default</c> —— 与真驱动一致。第七笔之前臂先问 <c>IsDBNull</c> 所以这里
    /// 到不了；现在非空值类型那一族臂直接调它，库里给非空列塞 NULL 就在这一行炸，
    /// 症状由「静默给 0」变成「抛」，那正是改判要的形状（见 <c>A_null_keeps_the_nullability_of_the_member</c>）。</summary>
    T Typed<T>(int ordinal)
    {
        if (nulls[ordinal][row])
            throw new InvalidCastException($"列 {names[ordinal]} 的这一行是空值：原始访问器不该被调用（臂先问 IsDBNull）。");

        return ((T[]) values[ordinal])[row];
    }

    static NotSupportedException NotSupported(string member)
        => new($"这片假驱动不实现 {member}：物化路径的读法只用 GetValue 与按类型的 GetXxx（§12.7 C）。");
}
