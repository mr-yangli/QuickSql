using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.ClickHouse;

/// <summary>CLR ↔ ClickHouse 类型名（整层厂商文档口径，本机没有引擎）。要点：Bool 是真类型，
/// 谓词侧 = true 与列型同边；整数有符号/无符号成对，byte 落 UInt8 而不是往宽里并——落宽档会把
/// 无符号语义静默丢掉一半；decimal 两档都要显式带（缺省 (18,4) 是推断）；string 落 String 无宽度，
/// MaxChars 只是仓库侧的尺子；时刻落 DateTime64(3)——不带精度默认是秒，落秒精度会静默丢毫秒；
/// 没有带偏移的时刻型、没有"由库递增的列"，TimeOnly/TimeSpan/byte[] 三档全部抛（折算成什么都要先做
/// 一次语义层定案，本层不替人选）。本文件产出的每一条型名都没有在一台活库上跑过。</summary>
public sealed class ClickHouseTypes : ITypeMap
{
    public static ClickHouseTypes Instance { get; } = new();

    /// <summary>读侧的类型名表：system.columns.type 那一列会报回来的基名。带 Nullable(…)/LowCardinality(…)
    /// 包裹的型由 ClickHouseSchemaReader 先剥皮再查这里；Date32/DateTime 与 Date 一档都收进来。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["int8"] = typeof(sbyte),
        ["int16"] = typeof(short),
        ["int32"] = typeof(int),
        ["int"] = typeof(int),
        ["int64"] = typeof(long),
        ["uint8"] = typeof(byte),
        ["uint16"] = typeof(ushort),
        ["uint32"] = typeof(uint),
        ["uint64"] = typeof(ulong),
        ["float32"] = typeof(float),
        ["float"] = typeof(float),
        ["float64"] = typeof(double),
        ["double"] = typeof(double),
        ["decimal"] = typeof(decimal),
        ["string"] = typeof(string),
        ["uuid"] = typeof(Guid),
        ["date"] = typeof(DateOnly),
        ["date32"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["datetime64"] = typeof(DateTime),
    }.ToFrozenDictionary();

    /// <summary>UTF-8 下一个字符最多 4 字节（代理对）。只给 FitsBytes 一把"永不误拒"的宽松尺
    ///（本家 String 那一档没有库侧字节上限可量）。</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return "String";

        // 枚举存整数，与另几族同一决定；CLR 底层型是 long 的枚举需要 [Column(TypeName=...)] 点名 Int64
        //（这一份窄档只有 Int32 一支）。
        if (clrType.IsEnum)
            return "Int32";

        return clrType switch
        {
            // 布尔是本家的真类型。落 Bool 直接兑现 Boolean_Type 那颗位，谓词侧 = true 与列型同边。
            var t when t == typeof(bool) => "Bool",

            // 成对的 8 颗：按位宽逐颗对齐。byte 落 UInt8 而不是往宽里并——
            // 落宽档丢掉的无符号语义才是静默退化，本家两列都原生、不需要借"往宽里放"那一手。
            var t when t == typeof(sbyte) => "Int8",
            var t when t == typeof(byte) => "UInt8",
            var t when t == typeof(short) => "Int16",
            var t when t == typeof(ushort) => "UInt16",
            var t when t == typeof(int) => "Int32",
            var t when t == typeof(uint) => "UInt32",
            var t when t == typeof(long) => "Int64",
            var t when t == typeof(ulong) => "UInt64",

            var t when t == typeof(float) => "Float32",
            var t when t == typeof(double) => "Float64",

            // Decimal 的两档都要显式带；缺省那档厂商没有给精确档位
            //【推断：取 (18,4)；厂商给的上限（precision 76）比 18 宽，缺省落在范围内成立】。
            var t when t == typeof(decimal) => $"Decimal({column.Precision ?? 18},{column.Scale ?? 4})",

            // 原生 16 字节——另一支候选（文本形 CHAR(36)）在本家不取，Guid_Raw16 能力位由此为真。
            var t when t == typeof(Guid) => "UUID",

            // 取 3 位（毫秒）那一档：DateTime64 不带精度默认是秒，CLR 时刻不落 3 位丢毫秒。
            var t when t == typeof(DateTime) => "DateTime64(3)",

            var t when t == typeof(DateOnly) => "Date",

            var t when t == typeof(char) => "String",

            // DateTimeOffset——本族没有带偏移的时刻型。降级 DateTime UTC + 列标记 + 读取端还原的
            // 退路本仓库今天都不存在，宁缺不错。
            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "ClickHouse 的 DateTimeOffset 列无处可发：这一族没有带时区偏移的时刻型【文档没逐页回读】。"
                + "偏移量在今天只有两条路：把 UTC 的 DateTime 存进 DateTime64(3) 列并自己记偏移，或把偏移拆成一列。"
                + "§9.3 给这一档写的退路那两半本仓库今天都不存在，所以宁缺不错（与 GBase 8s 同果不同因，记 §14）。"),

            // TimeOnly/TimeSpan——本族没有"一天的某个时刻"与"时间跨度"的独立型。
            // 抛而不猜：折算成 DateTime64(3) 或 Int64 微秒都要先做一次语义层定案。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => throw new QuickSqlException(
                "ClickHouse 的时刻/时间跨度列无处可发：这一族没有 TimeOnly/TimeSpan 的对应型【文档没逐页回读】。"
                + "折成 DateTime64(3) 或 Int64 微秒都要先做一次语义层定案，本仓库今天没有那两半，所以抛而不猜。"),

            // byte[]——本族没有大对象那一档的型。先编 Base64 再落 String 是现场自己的路，
            // 不是本层能替人选的（选了就是"每行字符数翻倍"的静默空间决策）。
            var t when t == typeof(byte[]) => throw new QuickSqlException(
                "ClickHouse 的 byte[] 列无处可发：这一族没有大对象那一档的型【文档没逐页回读】。"
                + "先编 Base64 再落 String 是现场自己的路（那一手会静默放大占用），本层不替人选，所以抛而不猜。"),

            _ => throw new QuickSqlException($"ClickHouse 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        // 与 DuckDbTypes 那一支同一格：这一家的字符串没有库侧第二把尺（String 不限长度），量纲落到
        // Char（仓库侧的字符数），MaxBytes 给一把"永不误拒"的宽松上界。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 Char：DDL 侧 String 型名里本来就没有那颗数，"库问得回第二把尺"的探测口在这里没有对象，
    /// 尺子完全在仓库侧——退化实现而不是探测实现。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
