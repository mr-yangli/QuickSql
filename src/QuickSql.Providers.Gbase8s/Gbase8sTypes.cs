using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.Gbase8s;

/// <summary>CLR ↔ GBase 8s（Informix 血统）类型名。整族自写第一家：类型事实只有 IBM 手册一档出处
/// （没有厂商专页、没有产物对读、没有实测），每一行都到不了"实测"的高度。分岔六条：
/// 宽度量纲恒 Byte（唯一一型 VARCHAR 且 n 量字节——不是选了字节，是压根没有第二个候选；
/// DDL 里的 n 必须由字符数乘最坏字节数算出，与闸门那颗乘数必须同源）；没有"带字符集的列定义"槽
/// （codeset 是建库时定的服务端配置，SPI 无格接住）；自增写在型名上（SERIAL 族，只有起点没有步长——
/// 步长在契约有格子、在这一家无处发）；64 位取 ANSI 名（BIGINT/BIGSERIAL）；没有带偏移的时刻型
/// （库里压根没有第三型，错话不能与另两家同句）；没有原生 UUID 连取号函数都没有，Guid 只能落 CHAR(36)。
/// 本文件产出的每一条型名都没在活库上跑过且本机没有可跑的库——它是起点不是结论：等第一台库。</summary>
public sealed class Gbase8sTypes : ITypeMap
{
    public static Gbase8sTypes Instance { get; } = new();

    /// <summary>读侧类型名表，取 systypes.name 在手册里列出的内建型名。处境要说死：这是"库会报什么名字"
    /// 的清单，不是"驱动认什么名字"的清单（驱动从未解析成功，两件事在这里共用同一档）——
    /// doctor 的对账仍可能错。带 8 后缀一族与 INT64 都收进来（同义字，库里那一列叫什么取决于当初谁建的表）。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        // BOOLEAN（库侧）与 INTEGER（我们的 DDL）两行分开登记：目录怎么说我们怎么认，目录不说，不猜。
        ["boolean"] = typeof(bool),
        ["tinyint"] = typeof(sbyte),
        ["smallint"] = typeof(short),
        ["int"] = typeof(int),
        ["integer"] = typeof(int),
        ["bigint"] = typeof(long),
        ["int8"] = typeof(long),
        ["serial"] = typeof(int),
        ["serial8"] = typeof(long),
        ["bigserial"] = typeof(long),
        ["smallfloat"] = typeof(float),
        ["float"] = typeof(double),
        ["float8"] = typeof(double),
        ["double"] = typeof(double),
        ["precision"] = typeof(double),
        ["decimal"] = typeof(decimal),
        ["numeric"] = typeof(decimal),
        ["money"] = typeof(decimal),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["lvarchar"] = typeof(string),
        ["clob"] = typeof(string),
        ["text"] = typeof(string),
        ["blob"] = typeof(byte[]),
        ["byte"] = typeof(byte[]),
        ["image"] = typeof(byte[]),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["interval"] = typeof(TimeSpan),

        // DATETIME 的两种字段范围在目录里都读作 datetime——"库里那列是时刻还是日期时刻"目录答不出，
        // 只能靠 precision 那一格（"型名不足分档"的一个具体形状）。
    }.ToFrozenDictionary();

    /// <summary>字符→字节乘数，取 4（UTF-8 代理对与 GB18030 的最坏档）。这一颗与量纲取向绑死：
    /// 另五家它是宽松尺子，这一家它是发射参数——DDL 里 VARCHAR(n) 的 n 就是 MaxChars × 本数，
    /// 所以要偏大：宁可建出比契约宽的列，也不能建出装不下契约值的列。
    /// 代价：string(100) 在这一家是 400 字节，行容量比量字符的几家小约 4 倍（无槽位表达）。</summary>
    const int WorstCaseBytesPerChar = 4;

    /// <summary>CLR 型 → 存储类型名；人已点名的那一格原样穿过。与 IsIdentity 无关不是取向而是读不到的约束：
    /// 共用层先定型、后判自增，SERIAL 那一族的名字由 CodeFirst 换出来——这里给出的是非自增那一支的名字。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return String(column);

        if (clrType == typeof(byte[]))
            return Blob;

        // INTEGER 是 32 位：底层型是 long 的枚举需要 [Column(TypeName=...)] 点名 BIGINT
        // （另五家的 INT 臂同样窄，只是没写这一句）。
        if (clrType.IsEnum)
            return "INTEGER";

        return clrType switch
        {
            // 布尔落 INTEGER 而不用 BOOLEAN：库里 BOOLEAN 的存储形状在两档版本里不一致
            // （早期读作单字符 'T'/'F'，后期报 TRUE/FALSE），两种读法都不是 CLR 的 bool，
            // 而 SPI 没有"读法随版本"那一格。位只跟存储型走。
            var t when t == typeof(bool) => "INTEGER",

            // 窄整数一律往宽里放；这一家没有 UNSIGNED 档，读侧表因此少一半键。
            var t when t == typeof(byte) || t == typeof(sbyte) => "SMALLINT",
            var t when t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INTEGER",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "DECIMAL(20,0)",

            // FLOAT 在这一家是 8 字节那颗，4 字节的叫 SMALLFLOAT——与另五家正好错开一格。
            // 照抄型名会得到错的宽度且不报错（每行多 4 字节没人发现）。
            var t when t == typeof(float) => "SMALLFLOAT",
            var t when t == typeof(double) => "FLOAT",

            // DECIMAL 精度上限 32 位（比 Oracle 血统那四家小）：默认 (18,4) 在范围内，
            // 但"宽的一侧"比别家窄而契约没有那一问。
            var t when t == typeof(decimal) => $"DECIMAL({column.Precision ?? 18},{column.Scale ?? 4})",

            // DATETIME 不带尾随括号，精度写在字段范围串里：取 3（毫秒）与五家同数。
            // FRACTION 的最高档没读到确切值——若上限其实是 2，CLR 的第 3 位在库里进不去，
            // 今天没有库能量出来。
            var t when t == typeof(DateTime) => "DATETIME YEAR TO FRACTION(3)",

            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "GBase 8s 的 DateTimeOffset 列无处可发：这一家没有带时区偏移的时刻型（与另两家的缺法都不同 —— "
                + "虚谷是「库有那一型而驱动的 CLR 表不认」，MySQL 血统是「那一型存的是会话偏移而不是值的偏移」， "
                + "这里是库里压根没有第三型）。偏移量在今天只有两条路：把 UTC 的 DateTime 存进 DATETIME 列并自己记 "
                + "偏移，或把偏移拆成一列。§9.3 给这一档写的退路（降级 DateTime UTC + 列标记 + 读取端还原）那两半"
                + "本仓库今天都不存在，所以宁缺不猜（缺口与另两家并排记 §14，三条判据三条来路）。"),

            var t when t == typeof(DateOnly) => "DATE",

            // 时刻在这一家不是独立一型而是 DATETIME 的字段范围：与上面 DateTime 列在目录里同名
            // （都读作 datetime）——"型名不足分档"的第一个实例。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "DATETIME HOUR TO SECOND(0)",

            // CHAR(n) 量的是字节：36 颗 ASCII 恰好 36 字节，所以这一格不需要乘 4——只有 GUID 的
            // 'D' 形（恒 ASCII）成立。想换成手写仍然够得到：[Column(TypeName = ...)] 在第一行短路。
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"GBase 8s 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    /// <summary>string → VARCHAR(字符数 × 4)，乘出来的数就是 DDL 里的 n。无宽度那一支发 CLOB 而不是
    /// "很宽的 VARCHAR"：VARCHAR 上限是服务端配置决定的一档，不写一颗数比写错一档诚实——
    /// 后果是"超过现场上限的声明会在库里被拒"。</summary>
    static string String(ColumnContract column)
        => column.MaxChars is { } chars
            ? $"VARCHAR({chars * WorstCaseBytesPerChar})"
            : "CLOB";

    /// <summary>byte[] → BLOB：契约里没有"二进制最多几字节"的格子，只有无上限那一档接得住"没说过宽度"
    /// （BLOB 与 BYTE 两档里 BYTE 有上限）。</summary>
    const string Blob = "BLOB";

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? Blob };

        // 三格是本文件与五家分岔最大处：Unicode=false（没有 N' 形，量纲已由库定，取 true 会让
        // "nvarchar 才是 Unicode 列"那套读法在本家变成假话）；LengthSemantics=Byte 与
        // GetLengthSemantics 同一颗数不许分岔；MaxBytes 取"已算出来的那个字节数"而不是再乘一把宽松尺——
        // 量纲本来就是字节的库里，MaxBytes 与 DDL 里的 n 必须是同一个数，否则闸门量的与建表写的差 4 倍。
        return column with
        {
            Unicode = false,
            LengthSemantics = LengthSemantics.Byte,
            MaxBytes = column.MaxChars is { } chars ? chars * WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 Byte，六家第一颗。不是选择：只有一型字符列而它的 n 量字节，"分档"那一支在这一家没有对象。
    /// 目录里问不出第二把尺，落在与虚谷、金仓同一档（退化实现）；但取 Byte 比那几家多承担一层后果——
    /// 量纲影响 DDL 里的数字。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Byte;
}
