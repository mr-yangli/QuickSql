using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.TDengine;

/// <summary>CLR ↔ TDengine 类型名（整层厂商文档口径，本机没有引擎）。要点：BOOL 是真类型，
/// 谓词侧 = true 与列型同边；整数有符号/无符号成对（TINYINT/SMALLINT/INT/BIGINT 各带 UNSIGNED 半边），
/// byte 落 UNSIGNED 半边而不是往宽里并——落宽档会把无符号语义静默丢掉一半；
/// decimal 的 DECIMAL 型是 3.3.6.0 才进场的（VendorDoc·TDengine：类型页的版本标注），旧引擎上这一格当场红；
/// string 落 VARCHAR(n) 且 n 量字节（本家 VARCHAR 是 BINARY 的别名，没有第二个量字符的候选——
/// NCHAR 量字符但内部 UTF-32 固定 4 字节/字符，宽窄取舍记 notes）；
/// 时刻落 TIMESTAMP（不带精度后缀，精度由建库 PRECISION 参数定，缺省档下 CLR 的刻度是否保真
/// 只有引擎答得了）；本家没有 DATE/TIME 独立型（DateOnly/TimeOnly/TimeSpan 全抛）、
/// 没有 UUID 型（Guid 落 VARCHAR(36) 文本形，'D' 形 36 颗 ASCII 恰好 36 字节）、
/// 没有带偏移的时刻型（DateTimeOffset 抛）。本文件产出的每一条型名都没有在一台活库上跑过。</summary>
public sealed class TDengineTypes : ITypeMap
{
    public static TDengineTypes Instance { get; } = new();

    /// <summary>读侧的类型名表：INFORMATION_SCHEMA.INS_COLUMNS.col_type 那一列会报回来的基名。
    /// 厂商类型页的型名是这一份清单的唯一出处；TIMESTAMP/BOOL 两颗与 DDL 侧同形。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["timestamp"] = typeof(DateTime),
        ["bool"] = typeof(bool),
        ["tinyint"] = typeof(sbyte),
        ["smallint"] = typeof(short),
        ["int"] = typeof(int),
        ["integer"] = typeof(int),
        ["bigint"] = typeof(long),
        ["tinyint unsigned"] = typeof(byte),
        ["smallint unsigned"] = typeof(ushort),
        ["int unsigned"] = typeof(uint),
        ["integer unsigned"] = typeof(uint),
        ["bigint unsigned"] = typeof(ulong),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["decimal"] = typeof(decimal),
        ["varchar"] = typeof(string),
        ["binary"] = typeof(string),
        ["nchar"] = typeof(string),
        ["char"] = typeof(string),
        ["varbinary"] = typeof(byte[]),
    }.ToFrozenDictionary();

    /// <summary>UTF-8 下一个字符最多 4 字节（代理对）。本家 VARCHAR 的 n 量字节，
    /// 这一颗不是宽松尺而是发射参数——DDL 里 VARCHAR(n) 的 n 就是 MaxChars × 本数，
    /// 所以要偏大：宁可建出比契约宽的列，也不能建出装不下契约值的列（GBase 8s 同一把尺同一份论证）。</summary>
    const int WorstCaseBytesPerChar = 4;

    /// <summary>VARCHAR/VARBINARY 的型上限（厂商类型页：BINARY/VARBINARY 最大 65,517 字节）。
    /// 契约没说宽度时的缺省档：本家没有 CLOB 那一支"不写一颗数"的退路——型名必须带 n，
    /// 而可变长存储下 n 只是上限不预占空间，取型上限比写一颗小数诚实。</summary>
    const int VarTypeMaxBytes = 65_517;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return String(column);

        if (clrType == typeof(byte[]))
            return column.MaxBytes is { } bytes && bytes > 0 ? $"VARBINARY({bytes})" : $"VARBINARY({VarTypeMaxBytes})";

        // 枚举存整数，与另几族同一决定；CLR 底层型是 long 的枚举需要 [Column(TypeName=...)] 点名 BIGINT
        //（这一份窄档只有 INT 一支）。
        if (clrType.IsEnum)
            return "INT";

        return clrType switch
        {
            // 布尔是本家的真类型。落 BOOL 直接兑现 Boolean_Type 那颗位，谓词侧 = true 与列型同边。
            var t when t == typeof(bool) => "BOOL",

            // 成对的 8 颗：按位宽逐颗对齐，UNSIGNED 半边照厂商类型页的原名带空格发
            //（"TINYINT UNSIGNED"——读侧 INS_COLUMNS 的 col_type 也是这一形）。
            var t when t == typeof(sbyte) => "TINYINT",
            var t when t == typeof(byte) => "TINYINT UNSIGNED",
            var t when t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) => "SMALLINT UNSIGNED",
            var t when t == typeof(int) => "INT",
            var t when t == typeof(uint) => "INT UNSIGNED",
            var t when t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "BIGINT UNSIGNED",

            var t when t == typeof(float) => "FLOAT",
            var t when t == typeof(double) => "DOUBLE",

            // DECIMAL 是 3.3.6.0 才进场的型（VendorDoc·TDengine：类型页版本标注），两档都要显式带。
            var t when t == typeof(decimal) => $"DECIMAL({column.Precision ?? 18},{column.Scale ?? 4})",

            // 不带精度后缀：TIMESTAMP 的精度由建库 PRECISION 参数定（ms/us/ns），
            // 列级后缀会把它钉死在列上、把"库精度"那颗参数架空——本层不替人选。
            var t when t == typeof(DateTime) => "TIMESTAMP",

            // 本家没有 UUID 型。'D' 形只有十六进制与短横，36 颗 ASCII 恰好 36 字节，
            // 所以这一格不需要乘 4——想换手写仍然够得到：[Column(TypeName=...)] 在第一行短路。
            var t when t == typeof(Guid) => "VARCHAR(36)",

            // NCHAR(1)：char 是一颗 UTF-16 码元，NCHAR 的 n 量字符、多字节安全。
            var t when t == typeof(char) => "NCHAR(1)",

            // DateTimeOffset——本族没有带偏移的时刻型。降级 DateTime UTC + 列标记 + 读取端还原的
            // 退路本仓库今天都不存在，宁缺不错。
            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "TDengine 的 DateTimeOffset 列无处可发：这一族没有带时区偏移的时刻型【文档·TDengine：类型页清单】。"
                + "偏移量在今天只有两条路：把 UTC 的 DateTime 存进 TIMESTAMP 列并自己记偏移，或把偏移拆成一列。"
                + "§9.3 给这一档写的退路那两半本仓库今天都不存在，所以宁缺不错（与 ClickHouse 同果，记 §14）。"),

            // DateOnly/TimeOnly/TimeSpan——本族没有 DATE/TIME 独立型，时刻之外只有 TIMESTAMP 一型。
            // 抛而不猜：DateOnly 折成 TIMESTAMP 会把"一天"变成"那一天零点"（值语义变了），
            // TimeOnly/TimeSpan 折成 BIGINT 微秒要先做一次语义层定案，本层不替人选。
            var t when t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan) => throw new QuickSqlException(
                "TDengine 的日期/时刻/时间跨度列无处可发：这一族没有 DATE/TIME 独立型，时刻之外只有 TIMESTAMP 一型"
                + "【文档·TDengine：类型页清单】。DateOnly 折成 TIMESTAMP 是把值语义改了（那一天零点），"
                + "TimeOnly/TimeSpan 折成 BIGINT 微秒要先做一次语义层定案——两半本仓库今天都没有，所以抛而不猜。"),

            _ => throw new QuickSqlException($"TDengine 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    /// <summary>string → VARCHAR(字符数 × 4)。n 必须带：本家 VARCHAR 是带长度的型，
    /// 没有"不写一颗数"的档——契约没说宽度时取型上限（可变长存储下上限不预占空间）。</summary>
    static string String(ColumnContract column)
        => column.MaxChars is { } chars
            ? $"VARCHAR({chars * WorstCaseBytesPerChar})"
            : $"VARCHAR({VarTypeMaxBytes})";

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        // 三格与 GBase 8s 同形同理由：Unicode=false（没有 N' 字面量那一形，量纲已由库定，
        // 取 true 会让"nvarchar 才是 Unicode 列"那套读法在本家变成假话）；LengthSemantics=Byte
        // 与 GetLengthSemantics 同一颗数不许分岔；MaxBytes 取"已算出来的那个字节数"而不是再乘一把宽松尺
        // ——量纲本来就是字节的库里，MaxBytes 与 DDL 里的 n 必须是同一个数，否则闸门量的与建表写的差 4 倍。
        return column with
        {
            Unicode = false,
            LengthSemantics = LengthSemantics.Byte,
            MaxBytes = column.MaxChars is { } chars ? chars * WorstCaseBytesPerChar : VarTypeMaxBytes,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 Byte：只有一型字符列而它的 n 量字节，"分档"那一支在这一家没有对象。
    /// 目录里问不出第二把尺，落在与虚谷、金仓同一档（退化实现）；量纲影响 DDL 里的数字。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Byte;
}
