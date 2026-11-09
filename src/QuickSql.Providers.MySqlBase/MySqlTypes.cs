using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.MySqlBase;

/// <summary>CLR ↔ MySQL 系类型名。四处不是换个型名的分岔：量纲是字符（VARCHAR(n) 的 n 从 4.1 起是字符数）
/// 而字节闸门真的存在——utf8mb4 一字符最多 4 字节，单列上限 16383，且它是行级预算的一部分（契约只有列宽槽位，没有行预算格）；
/// 没有带时区的时刻类型（TIMESTAMP 存 UTC 而按会话时区显示，装不下 DateTimeOffset 的偏移，宁抛不静默改值）；
/// 大文本与大二进制分档选各自最大（TEXT/LONGBLOB，选小了是静默截断）；
/// Guid 默认 CHAR(36)（BINARY(16) 要连接串参数与字节置换一致，SPI 没有"连接串参数改方言假设"那一格）。
/// 本文件的产出文本没有在一台真库上跑过，每一处"有/没有"都是厂商文档口径。</summary>
public sealed class MySqlTypes : ITypeMap
{
    public static MySqlTypes Instance { get; } = new();

    /// <summary>读侧类型名表只按 data_type 折：tinyint(1) 与 tinyint(4) 在目录视图这一层分不开，
    /// 保守认成 sbyte——真判据在 column_type 那一列而本层读不到，不靠"看到 tinyint 就当 bool"猜
    /// （猜错方向是把 255 装进 bool）。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["tinyint"] = typeof(sbyte),
        ["tinyint unsigned"] = typeof(byte),
        ["smallint"] = typeof(short),
        ["smallint unsigned"] = typeof(ushort),
        ["mediumint"] = typeof(int),
        ["int"] = typeof(int),
        ["integer"] = typeof(int),
        ["int unsigned"] = typeof(uint),
        ["bigint"] = typeof(long),
        ["bigint unsigned"] = typeof(ulong),
        ["decimal"] = typeof(decimal),
        ["numeric"] = typeof(decimal),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["bit"] = typeof(ulong),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["tinytext"] = typeof(string),
        ["text"] = typeof(string),
        ["mediumtext"] = typeof(string),
        ["longtext"] = typeof(string),
        ["enum"] = typeof(string),
        ["set"] = typeof(string),
        ["json"] = typeof(string),
        ["binary"] = typeof(byte[]),
        ["varbinary"] = typeof(byte[]),
        ["tinyblob"] = typeof(byte[]),
        ["blob"] = typeof(byte[]),
        ["mediumblob"] = typeof(byte[]),
        ["longblob"] = typeof(byte[]),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["timestamp"] = typeof(DateTime),
        ["time"] = typeof(TimeSpan),
        ["year"] = typeof(short),
    }.ToFrozenDictionary();

    /// <summary>utf8mb4 下一颗字符最多 4 字节。这一家的行预算真的按字节算，
    /// 所以它不只是给 FitsBytes 一把宽松尺子；由此得出单列的字符上限 MaxVarcharChars。</summary>
    const int Utf8mb4WorstCaseBytesPerChar = 4;

    /// <summary>单列 VARCHAR 装得下的字符数上限：65535 / 4（utf8mb4 一字符 4 字节）。
    /// 超过它就不是"这一列写宽一点"而是换一档型。</summary>
    const int MaxVarcharChars = 16383;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return column.MaxChars is { } chars
                ? chars > MaxVarcharChars ? "TEXT" : $"VARCHAR({chars})"
                : "TEXT";

        if (clrType == typeof(byte[]))
            return "LONGBLOB";

        // 枚举存整数：这一家也有原生 ENUM 型，但那是"每库一套取值清单"的路子，跨库对齐时比 int 更难迁。
        // 绑定侧 MySqlValues 同样按整数落地。
        if (clrType.IsEnum)
            return "INT";

        return clrType switch
        {
            // 布尔落 TINYINT(1)（厂商口径：BOOL 是 TINYINT(1) 的同义词）。它不是只能装 0/1 的那一档
            // （1..127 也放得下），所以谓词侧的 = 1 与 IS TRUE 在这里不等价——见 MySqlDialect 那格仍取 = 1。
            var t when t == typeof(bool) => "TINYINT(1)",

            // 窄整数一律往宽里放：不用 UNSIGNED，那会把"读回来该配哪种 CLR 型"变成 provider 的事而不是 CLR 型的事。
            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INT",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "DECIMAL(20,0)",
            var t when t == typeof(float) => "FLOAT",
            var t when t == typeof(double) => "DOUBLE",
            var t when t == typeof(decimal) => $"DECIMAL({column.Precision ?? 18},{column.Scale ?? 4})",

            // DATETIME 而不是 TIMESTAMP：后者范围只到 2038、按会话时区解释，
            // 还有"第一列可能被隐式挂上 ON UPDATE CURRENT_TIMESTAMP"的历史行为。
            // 3 位小数秒与另两族对齐。
            var t when t == typeof(DateTime) => "DATETIME(3)",
            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "MySQL 系没有能装下 DateTimeOffset 偏移量的时刻类型：DATETIME 与 TIMESTAMP 存的都是不带偏移的时刻，"
                + "而后者按会话时区解释。§9.3 给这一档写的退路是「降级 DateTime UTC + 列标记，读取端还原」，"
                + "那两半（绑定侧的时区换算、契约上那颗「本列存 UTC」标记）今天都不存在，所以宁缺不猜（缺口在 §14）。"
                + "今天要这一列：显式给 [Column(TypeName = ...)] 自己选一个型并自己负责换算。"),
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "TIME(3)",
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"MySQL 系（MySQL/MariaDB/TiDB）未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "LONGBLOB" };

        // 与 PG 那一族同形而不同因：这里的 MaxBytes 对应一个真实存在的字节闸门（在行上不在列上，
        // 按列比对量不到它）。给 FitsBytes 用是安全的，给"这一列最多几字节"用会偏大——
        // 库里问得出的那一版在 SchemaReader 的 reader 里。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8mb4WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 Char：与 PG 系同一答案，而这一家问得出字节（CHARACTER_OCTET_LENGTH）——
    /// 两把尺子都可读的那一格里它由字段名定死两颗字段的身份，不需要第三颗字段判哪把是声明的。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
