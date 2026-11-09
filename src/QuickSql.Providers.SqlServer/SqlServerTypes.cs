using System.Collections.Frozen;
using QuickSql.Metadata;

namespace QuickSql.Providers.SqlServer;

/// <summary>CLR ↔ T-SQL 类型名。三处偏保守的默认值都是实测换来的，不要"顺手改回去"：
/// string 默认 nvarchar（本机 LocalDB 的 SQL_Latin1_General_CP1_CI_AS 下 varchar 存中文是静默变 '?'，不是报错）；
/// DateTime 用 datetime2(3) 而不是遗留 datetime（后者按 3.33ms 取整且只到 1753 年）；
/// Guid 用原生 uniqueidentifier（代价是聚集索引热点与 Guid.CompareTo 排序不一致，跨库对齐要显式改成 char(36)）。</summary>
public sealed class SqlServerTypes : ITypeMap
{
    public static SqlServerTypes Instance { get; } = new();

    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["bigint"] = typeof(long),
        ["binary"] = typeof(byte[]),
        ["bit"] = typeof(bool),
        ["char"] = typeof(string),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["datetime2"] = typeof(DateTime),
        ["datetimeoffset"] = typeof(DateTimeOffset),
        ["decimal"] = typeof(decimal),
        ["float"] = typeof(double),
        ["image"] = typeof(byte[]),
        ["int"] = typeof(int),
        ["money"] = typeof(decimal),
        ["nchar"] = typeof(string),
        ["ntext"] = typeof(string),
        ["numeric"] = typeof(decimal),
        ["nvarchar"] = typeof(string),
        ["real"] = typeof(float),
        ["rowversion"] = typeof(byte[]),
        ["smalldatetime"] = typeof(DateTime),
        ["smallint"] = typeof(short),
        ["smallmoney"] = typeof(decimal),
        ["sql_variant"] = typeof(object),
        ["text"] = typeof(string),
        ["time"] = typeof(TimeSpan),
        ["tinyint"] = typeof(byte),
        ["uniqueidentifier"] = typeof(Guid),
        ["varbinary"] = typeof(byte[]),
        ["varchar"] = typeof(string),
    }.ToFrozenDictionary();

    /// <summary>存储类型名一律大写，与那张表逐字符对齐：表里写 NVARCHAR(n)、产出写 nvarchar(n)，
    /// 黄金样本就成了表的错别字而不是方言的。大小写对 T-SQL 无差别，反向查表本来就是 OrdinalIgnoreCase。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return String(column);

        if (clrType == typeof(byte[]))
            return Bytes(column);

        // enum → INT 是既定映射（T-SQL 没有原生枚举类型）。绑定侧 SqlServerValues
        // 同样按 Int32 落地：两头不一致就会出现"建表是 int、写值报转换错"。
        if (clrType.IsEnum)
            return "INT";

        return clrType switch
        {
            var t when t == typeof(bool) => "BIT",
            var t when t == typeof(byte) => "TINYINT",
            var t when t == typeof(sbyte) => "SMALLINT",
            var t when t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) => "INT",
            var t when t == typeof(int) => "INT",
            var t when t == typeof(uint) => "BIGINT",
            var t when t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "NUMERIC(20,0)",
            var t when t == typeof(float) => "REAL",
            var t when t == typeof(double) => "FLOAT",
            var t when t == typeof(decimal) =>
                $"DECIMAL({column.Precision ?? 18},{column.Scale ?? 4})",
            var t when t == typeof(DateTime) => "DATETIME2(3)",
            var t when t == typeof(DateTimeOffset) => "DATETIMEOFFSET(3)",
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) => "TIME(3)",
            var t when t == typeof(TimeSpan) => "TIME(3)",
            var t when t == typeof(Guid) => "UNIQUEIDENTIFIER",
            var t when t == typeof(char) => "NCHAR(1)",
            _ => throw new QuickSqlException(
                $"SQL Server 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"
            ),
        };
    }

    static string String(ColumnContract column)
    {
        // 无上限 → MAX。有上限时 n 就是字符数（nvarchar 是 UTF-16 code unit）。
        if (column.MaxChars is not { } chars)
            return column.Unicode ? "NVARCHAR(MAX)" : "VARCHAR(MAX)";

        return column.Unicode ? $"NVARCHAR({chars})" : $"VARCHAR({chars})";
    }

    static string Bytes(ColumnContract column) =>
        column.MaxBytes is { } bytes && bytes <= 8000 ? $"VARBINARY({bytes})" : "VARBINARY(MAX)";

    /// <summary>两次调用得同一个结果（SqlServerCodeFirst 会对已经算过的契约再调一次），所以这里的
    /// 规则只看算完之后的那几格，不看"这一格是不是我第一次见"。</summary>
    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var resolved = Resolve(column);

        // 「值归库给」是存储类型的事实，只有本家认得：共用层猜不到哪一型算"库自己递增"，
        // 而猜得到的那一半（整型主键）已经另有 IsIdentity。判据取 StoreTypeName 而不是 ClrType：
        // byte[] 在本家默认是 VARBINARY，那里头值归调用方给。
        if (
            !resolved.IsConcurrencyToken
            || !string.Equals(
                resolved.StoreTypeName,
                "rowversion",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return resolved;

        return resolved with
        {
            StoreFillsValue = true,
        };
    }

    ColumnContract Resolve(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        // 量纲由库决定：nvarchar 的 n 是字符（UTF-16，一字符 2 字节），varchar 的 n 是字节。
        // varchar 在 UTF-8 排序规则下"字节够而字符不够"，所以非 Unicode 列按最坏 3 字节/字符算。
        var unicode = column.Unicode || clrType == typeof(string);
        var width = unicode ? 2 : 3;

        return column with
        {
            Unicode = unicode,
            LengthSemantics = unicode ? LengthSemantics.Char : LengthSemantics.Byte,
            MaxBytes = column.MaxBytes ?? column.MaxChars * width,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) =>
        StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>SQL Server 是"看类型不看库"的那一家：nvarchar 是字符、varchar 是字节，
    /// 所以量纲直接跟着 ColumnContract.Unicode 走，而不是给一个库级常量。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) =>
        column.Unicode ? LengthSemantics.Char : LengthSemantics.Byte;
}
