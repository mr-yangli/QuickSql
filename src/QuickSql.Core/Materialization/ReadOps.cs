using System.Data.Common;
using System.Runtime.CompilerServices;
using QuickSql.Metadata;
using QuickSql.Schema;

namespace QuickSql.Materialization;

/// <summary>「这一列能不能不装箱」的唯一判定处，与它同处的还有那批直读档的编号。
/// 探测按每场次一次而不是每值一次：每值付一次驱动调用的探针比装箱本身还贵。
/// 判据只有一条而且不猜：驱动交回的类型（GetFieldType）与成员声明的类型是同一个才直读——
/// 金仓把 boolean 回成 t/f、DM 把 uniqueidentifier 回成 byte[] 时那一列自动落回装箱口，
/// 不需要登记"哪家怎么回"的表（GetFieldType 与 GetValue 同型是 DbDataReader 的契约）。</summary>
public static class ReadOps
{
    /// <summary>不直读：进 ValueConverter.Read&lt;T&gt; 那扇装箱口。</summary>
    public const byte Boxed = 0;

    public const byte Boolean = 1;
    public const byte Byte = 2;
    public const byte Int16 = 3;
    public const byte Int32 = 4;
    public const byte Int64 = 5;
    public const byte Single = 6;
    public const byte Double = 7;
    public const byte Decimal = 8;
    public const byte Char = 9;
    public const byte DateTime = 10;
    public const byte String = 11;
    public const byte Guid = 12;

    static readonly Dictionary<Type, byte> ByType = new()
    {
        [typeof(bool)] = Boolean,
        [typeof(byte)] = Byte,
        [typeof(short)] = Int16,
        [typeof(int)] = Int32,
        [typeof(long)] = Int64,
        [typeof(float)] = Single,
        [typeof(double)] = Double,
        [typeof(decimal)] = Decimal,
        [typeof(char)] = Char,
        [typeof(DateTime)] = DateTime,
        [typeof(string)] = String,
        [typeof(Guid)] = Guid,
    };

    /// <summary>每列每场次一次：探驱动，落这一列的读法编号。</summary>
    public static byte Probe(DbDataReader reader, int ordinal, Type declared)
    {
        var underlying = Nullable.GetUnderlyingType(declared) ?? declared;

        // 枚举不在 ByType 里（成员型是 State 而驱动交回的是底层整数，两边不同型），
        // byte[] / DateOnly / TimeOnly / DateTimeOffset / TimeSpan 也留在外面：DbDataReader 没有
        // "一次调用一个值"的原始访问器（构建期实测撞出来的），写下去是编译错误而不是优化。
        if (underlying.IsEnum || !ByType.TryGetValue(underlying, out var op))
            return Boxed;

        return reader.GetFieldType(ordinal) == underlying ? op : Boxed;
    }

    /// <summary>建一张票：startOffset 起连续 contract.Columns.Length 列。实体投影的选列就是契约列序、
    /// Include 那一档是节点自带的偏移，两处都是"连续一段"，票上只带一颗起点、不带序号数组——
    /// 探测用的 startOffset + i 与读取委托用的那一式是同一个，错位空间只剩一个加法。
    /// 每场次落堆的只有那一颗 byte[]，票本身是值。</summary>
    public static ReadPlan Build(DbDataReader reader, EntityContract contract, int startOffset)
    {
        var columns = contract.Columns;
        var ops = new byte[columns.Length];

        for (var i = 0; i < columns.Length; i++)
            ops[i] = Probe(reader, startOffset + i, columns[i].ClrType);

        return new ReadPlan(startOffset, ops);
    }
}

/// <summary>直读臂：一条臂 = 一型的读法，两段在同一行里；每条臂的非直读分支就是 ValueConverter.Read&lt;T&gt;
/// 本身（不是复制来的等价实现），所以"这一列怎么读"仍只有一扇门在回答。非空值类型的臂不问 IsDBNull
/// （一次取值只付一趟驱动调用），代价是一句语义改判——库里往非空值类型的列塞 NULL 现在抛、不再静默给
/// default；string 臂与可空那一族照旧问 IsDBNull（NULL 是它们要表达的合法值）。可空值类型各有自己的臂
/// 是语义理由：非空臂交回 default 交给 DateTime? 成员是一次静默的摊平（0001-01-01 冒充"没有值"）。
/// LEFT JOIN 未命中的子实体在读任何字段之前就按 nullCheckOrdinal 返回，不进臂。编号与臂的对应只在本文件
/// 定义，生成器按名字调这里的公开方法，生成期挑错臂的方向是"没有变快"，不是"读错值"。</summary>
public static class DirectRead
{
    /// <summary>按声明类型原样取臂（int 与 int? 是两支不同的臂）。Delegate.Method 就是 Expression.Call
    /// 要的那个方法句柄，取自方法组转换，不是反射查找。null = 这一型没有直读臂，调用方继续走装箱口。</summary>
    static readonly Dictionary<Type, Delegate> Arms = new()
    {
        [typeof(bool)] = new Func<DbDataReader, int, byte, bool>(ReadBoolean),
        [typeof(byte)] = new Func<DbDataReader, int, byte, byte>(ReadByte),
        [typeof(short)] = new Func<DbDataReader, int, byte, short>(ReadInt16),
        [typeof(int)] = new Func<DbDataReader, int, byte, int>(ReadInt32),
        [typeof(long)] = new Func<DbDataReader, int, byte, long>(ReadInt64),
        [typeof(float)] = new Func<DbDataReader, int, byte, float>(ReadSingle),
        [typeof(double)] = new Func<DbDataReader, int, byte, double>(ReadDouble),
        [typeof(decimal)] = new Func<DbDataReader, int, byte, decimal>(ReadDecimal),
        [typeof(char)] = new Func<DbDataReader, int, byte, char>(ReadChar),
        [typeof(DateTime)] = new Func<DbDataReader, int, byte, DateTime>(ReadDateTime),
        [typeof(string)] = new Func<DbDataReader, int, byte, string>(ReadString),
        [typeof(Guid)] = new Func<DbDataReader, int, byte, Guid>(ReadGuid),
        [typeof(bool?)] = new Func<DbDataReader, int, byte, bool?>(ReadNullableBoolean),
        [typeof(byte?)] = new Func<DbDataReader, int, byte, byte?>(ReadNullableByte),
        [typeof(short?)] = new Func<DbDataReader, int, byte, short?>(ReadNullableInt16),
        [typeof(int?)] = new Func<DbDataReader, int, byte, int?>(ReadNullableInt32),
        [typeof(long?)] = new Func<DbDataReader, int, byte, long?>(ReadNullableInt64),
        [typeof(float?)] = new Func<DbDataReader, int, byte, float?>(ReadNullableSingle),
        [typeof(double?)] = new Func<DbDataReader, int, byte, double?>(ReadNullableDouble),
        [typeof(decimal?)] = new Func<DbDataReader, int, byte, decimal?>(ReadNullableDecimal),
        [typeof(char?)] = new Func<DbDataReader, int, byte, char?>(ReadNullableChar),
        [typeof(DateTime?)] = new Func<DbDataReader, int, byte, DateTime?>(ReadNullableDateTime),
        [typeof(Guid?)] = new Func<DbDataReader, int, byte, Guid?>(ReadNullableGuid),
    };

    /// <summary>声明类型 → 臂。不做 Nullable 摊平（摊平正是可空臂要防的事），枚举与 Nullable&lt;枚举&gt;
    /// 都不在表里：前者成员型与列型永远不同型，后者要在门里按 Enum.ToObject 落（未登记的数值也读得出来）。</summary>
    public static Delegate? ArmFor(Type declared) => Arms.GetValueOrDefault(declared);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBoolean(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Boolean ? reader.GetBoolean(ordinal) : ValueConverter.Read<bool>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadByte(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Byte ? reader.GetByte(ordinal) : ValueConverter.Read<byte>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ReadInt16(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int16 ? reader.GetInt16(ordinal) : ValueConverter.Read<short>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int32 ? reader.GetInt32(ordinal) : ValueConverter.Read<int>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadInt64(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int64 ? reader.GetInt64(ordinal) : ValueConverter.Read<long>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadSingle(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Single ? reader.GetFloat(ordinal) : ValueConverter.Read<float>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadDouble(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Double ? reader.GetDouble(ordinal) : ValueConverter.Read<double>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal ReadDecimal(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Decimal ? reader.GetDecimal(ordinal) : ValueConverter.Read<decimal>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char ReadChar(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Char ? reader.GetChar(ordinal) : ValueConverter.Read<char>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTime ReadDateTime(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.DateTime ? reader.GetDateTime(ordinal) : ValueConverter.Read<DateTime>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ReadString(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.String && !reader.IsDBNull(ordinal) ? reader.GetString(ordinal) : ValueConverter.Read<string>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid ReadGuid(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Guid ? reader.GetGuid(ordinal) : ValueConverter.Read<Guid>(reader, ordinal);

    // ── 可空值类型那一族：非直读分支交回 Read<T?>，所以 NULL 是 null 而不是 default(T) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool? ReadNullableBoolean(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Boolean && !reader.IsDBNull(ordinal) ? reader.GetBoolean(ordinal) : ValueConverter.Read<bool?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte? ReadNullableByte(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Byte && !reader.IsDBNull(ordinal) ? reader.GetByte(ordinal) : ValueConverter.Read<byte?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short? ReadNullableInt16(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int16 && !reader.IsDBNull(ordinal) ? reader.GetInt16(ordinal) : ValueConverter.Read<short?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int? ReadNullableInt32(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int32 && !reader.IsDBNull(ordinal) ? reader.GetInt32(ordinal) : ValueConverter.Read<int?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long? ReadNullableInt64(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int64 && !reader.IsDBNull(ordinal) ? reader.GetInt64(ordinal) : ValueConverter.Read<long?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float? ReadNullableSingle(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Single && !reader.IsDBNull(ordinal) ? reader.GetFloat(ordinal) : ValueConverter.Read<float?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double? ReadNullableDouble(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Double && !reader.IsDBNull(ordinal) ? reader.GetDouble(ordinal) : ValueConverter.Read<double?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal? ReadNullableDecimal(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Decimal && !reader.IsDBNull(ordinal) ? reader.GetDecimal(ordinal) : ValueConverter.Read<decimal?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char? ReadNullableChar(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Char && !reader.IsDBNull(ordinal) ? reader.GetChar(ordinal) : ValueConverter.Read<char?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTime? ReadNullableDateTime(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.DateTime && !reader.IsDBNull(ordinal) ? reader.GetDateTime(ordinal) : ValueConverter.Read<DateTime?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid? ReadNullableGuid(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Guid && !reader.IsDBNull(ordinal) ? reader.GetGuid(ordinal) : ValueConverter.Read<Guid?>(reader, ordinal);
}
