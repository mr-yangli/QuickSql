using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.Xugu;

/// <summary>CLR ↔ 虚谷类型名。出处有文档与产物两档（本仓库第一份"类型事实有两个独立出处"的 provider），
/// 分岔五条：宽度量纲恒字符（厂商明写，实测按字符落闸）；VARCHAR 单列上限 60000 字符、超出换 CLOB；
/// 无宽度也发 CLOB——TEXT 只是 VARCHAR 的同义字（文档明写），无宽度档是"行记录剩余空间（64K）"，
/// 实测 6 万字符即拒，当不了大文本；
/// 有原生 BOOLEAN 仍把 bool 落 TINYINT——位只跟存储型走，bool 落 TINYINT 期间 Boolean_Type 永不声明；
/// DATETIME 取 (3)（库能收 6，但 (3) 是五家同一条纪律，且驱动读回本来就只到毫秒）；CHAR/VARCHAR 存入时去尾空格，
/// "值变了没有"要看去尾之后。
/// 更隐蔽的一格：同一段文本的字符数由连接串 CHARSET= 决定（库的 CHAR_SET 是 GBK，带不带 CHARSET=UTF8
/// 能差出两种数法），本文件每一颗宽度数字因此都有两种含义，而 SPI 没有槽位接住那颗开关。</summary>
public sealed class XuguTypes : ITypeMap
{
    public static XuguTypes Instance { get; } = new();

    /// <summary>读侧类型名表：照抄驱动自带的"存储名 ↔ CLR"映射，再加三颗文档有、驱动表没有的名字
    /// （VARCHAR/TEXT/INTEGER——驱动表没有 VARCHAR 这一行是"表不全"还是"库改名后存目录"，两解释分不开）。
    /// 带时区那三型刻意不登记：驱动的 CLR 键里没有 DateTimeOffset，读成 DateTime 会静默丢偏移；
    /// 未登记的后果是查不到而报"这一型不认识"，不是悄悄折值。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        // 驱动表里 bool 与 tinyint 是两行，这里也分两行：目录怎么说我们怎么认，目录不说，不猜。
        ["bool"] = typeof(bool),
        ["boolean"] = typeof(bool),
        ["tinyint"] = typeof(sbyte),
        ["smallint"] = typeof(short),
        ["int"] = typeof(int),
        ["integer"] = typeof(int),
        ["bigint"] = typeof(long),
        ["float"] = typeof(float),
        ["real"] = typeof(float),
        ["double"] = typeof(double),
        ["numeric"] = typeof(decimal),
        ["decimal"] = typeof(decimal),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["clob"] = typeof(string),
        ["text"] = typeof(string),
        ["blob"] = typeof(byte[]),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["timestamp"] = typeof(DateTime),
        ["time"] = typeof(TimeSpan),
        ["guid"] = typeof(Guid),

        // 带时区三型刻意不登记（见上）：读成 DateTime 会静默丢偏移。
    }.ToFrozenDictionary();

    /// <summary>宽度→字节乘数，取 4：覆盖 UTF-8 与 GB18030 两档里较宽的那侧，只会偏大。
    /// 它是宽松尺子而不是闸门换算——字符集是会话级选的而 SPI 没有格接它，这一家的行预算（64K）在这里量不到。</summary>
    const int WorstCaseBytesPerChar = 4;

    /// <summary>单列 VARCHAR 字符上限 60000，直接抄厂商给的列定义范围（单位明写是字符）。
    /// 超过它的处理是换一档型，不是报"再宽一点"。"超过 60000"与"不写宽度"在库那边不是同一件事
    /// （不写长度时 VARCHAR 吃掉本行剩余空间）。</summary>
    const int MaxVarcharChars = 60_000;

    /// <summary>CLR 型 → 存储型名；人已点名的那一格原样穿过（不校验、不翻译）。唯一的例外不在这一格：
    /// rowversion 由 EmitColumnDefinition 在 DDL 发射点拒收——那里管"要不要建它"，这里管"这一格叫什么"；
    /// 读侧目录对账还要能说出别人建的表里有什么，所以这颗口保持无闸门。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        // 超过列级上限就不是"写宽一点"而是换一档型。无宽度发 CLOB：TEXT 在这一家是 VARCHAR 的同义字，
        // 不写长度只吃行记录剩余空间（64K，实测 6 万字符即拒），存不下"大文本"。CLOB 2GB——代价是不能
        // ORDER BY／不能建索引／不进合并写（ChangeGuard 的 Incomparable 两颗），这一档的使用面本来就只有
        // "存取正文"，不承诺可比。
        if (clrType == typeof(string))
            return column.MaxChars switch
            {
                null => "CLOB",
                var chars when chars > MaxVarcharChars => "CLOB",
                var chars => $"VARCHAR({chars})",
            };

        // BINARY 上限 64KB 且没有 VARBINARY 型，byte[] 在契约上没有宽度槽位——只有 2GB 的 BLOB 接得住"没上限"。
        if (clrType == typeof(byte[]))
            return "BLOB";

        // 枚举存整数，与另四族同一决定（驱动类型表没有枚举行，两家产物一律发 INT）。
        if (clrType.IsEnum)
            return "INT";

        return clrType switch
        {
            // 布尔落 TINYINT 而不用原生 BOOLEAN：位只跟存储型走（bool 落 TINYINT 期间 Boolean_Type 永不声明），
            // 列是整数、谓词比整数、字面量是整数，三处落在同一个形状上。重开点 = 布尔改落原生 BOOLEAN 的那一天。
            var t when t == typeof(bool) => "TINYINT",

            // 窄整数一律往宽里放；这一家没有 UNSIGNED 档，读侧表因此少一半键。
            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INT",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "NUMERIC(20,0)",
            var t when t == typeof(float) => "FLOAT",
            var t when t == typeof(double) => "DOUBLE",

            // NUMERIC 精度上限 38、库默认 (12,0)：这里给的 (18,4) 比库默认宽——默认必须映射到宽的一侧。
            var t when t == typeof(decimal) => $"NUMERIC({column.Precision ?? 18},{column.Scale ?? 4})",

            // DATETIME 取 (3)：库能收到 (6)、(7) 被拒（E2051，裸写默认 6），但 (3) 是五家同一条纪律——
            // 单把这一家放到 6 会让"同一份 CLR 值在五家留几位"变成逐家问。已知代价：CLR 第 4~7 个十进制位
            // 进不去，且驱动读回本来就只到毫秒。TIMESTAMP 在这一家与 DATETIME 是同一型的两个名字
            // （目录同名、同串并读一字不差），取名字更直白的那个。
            var t when t == typeof(DateTime) => "DATETIME(3)",

            // 抛：库侧有 TIMESTAMP WITH TIME ZONE 型（建表都收得下），缺的是把偏移带进去的绑定路——
            // 驱动的 CLR 登记表里没有 System.DateTimeOffset。错话别说成"这一家没有带时区的类型"：
            // 那会让人去找那个型并以为找到了退路。
            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "虚谷的 DateTimeOffset 列无法绑定：库侧有 TIMESTAMP WITH TIME ZONE 这一型（厂商文档），但驱动的 CLR 类型表里"
                + "没有 System.DateTimeOffset，而 §4 的 SPI 里没有任何一处做时区换算。§9.3 给这一档写的退路"
                + "（降级 DateTime UTC + 列标记 + 读取端还原）那两半今天都不存在，所以宁缺不猜（缺口记 §14，与 MySQL 那一格"
                + "同一条判据而缺的东西不同一条）。今天要这一列：显式给 [Column(TypeName = ...)] 自己选型并自己负责换算。"),

            var t when t == typeof(DateOnly) => "DATE",

            // TIME 的精度 p∈[0,3] 是厂商明写的：3 既是最宽档也是允许档——本文件唯一不需要赌的时刻型。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "TIME(3)",

            // Guid 落 CHAR(36) 而不用原生 16 字节 GUID 型：跟跨库默认 CHAR(36) 以便手工查询——
            // 二进制的字节置换要与驱动约定一致，不靠"猜驱动的置换"省四个字节。
            // 顺带：厂商给了 SYS_UUID() 等取号函数，而契约没有"值归库给的 Guid"那一格。
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"虚谷未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BLOB" };

        // 恒 Char 量纲。MaxBytes = MaxChars × 4 是一把纯宽松的尺子：字符集由会话决定，
        // 列上没有厂商字段记着"这一列最多几字节"。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 Char（五家里四家恒 Char、一家按 Unicode 分档；这一家"单位是字符"由厂商文档正面写明）。
    /// 目录里只有一个宽度且与精度/标度共用编码，第二把尺子（字节）问不出，所以量纲探测是退化实现而不是探测。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
