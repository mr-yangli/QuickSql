using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.DuckDb;

/// <summary>CLR ↔ DuckDB 类型名：血统那份 PostgreSqlTypes 的克隆而不是继承——克隆的原因是血统类型表的产出
///（TIMESTAMP(3)/DOUBLE PRECISION/TIME(3)）在本家一格都不成立，继承会把血统的缺省型名偷渡进本家。
/// 与 PG 的分岔（厂商文档明写）：double precision 这个名字没有（现行型名是 DOUBLE，别名 FLOAT8）；
/// 时间戳不带精度后缀（TIMESTAMP/TIMESTAMPTZ，TIMESTAMP 的别名是 DATETIME）；TIME 不吃精度；
/// VARCHAR(n)/TEXT 的宽度被引擎忽略——照发那几个名字，尺子是仓库侧的（FitsChars 照用），不是库侧的约束；
/// bool 原生、枚举仍落 INTEGER、Guid 落 UUID、字节落 BYTEA 四格与血统逐字同形。
/// 本类与血统那份表一样没有在一台真库上验过（本机没有 DuckDB 也没有驱动包）。</summary>
public sealed class DuckDbTypes : ITypeMap
{
    public static DuckDbTypes Instance { get; } = new();

    /// <summary>血统那份 StoreToClr 的原样克隆（读侧按"血统认什么"的最大集保留：information_schema 那一族
    /// 照 PG 的行数与列名报，深度目录以外怎么报本仓库没有读数）。PG 的 double precision、name 那几个
    /// DuckDB 文档没数的名字留在索引里不等于声明它们会出现在本家产出里——GetStoreType 是写侧的出口，
    /// 出哪些名字由它说了算。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["smallint"] = typeof(short),
        ["int2"] = typeof(short),
        ["integer"] = typeof(int),
        ["int4"] = typeof(int),
        ["bigint"] = typeof(long),
        ["int8"] = typeof(long),
        ["boolean"] = typeof(bool),
        ["bool"] = typeof(bool),
        ["real"] = typeof(float),
        ["float4"] = typeof(float),
        ["double precision"] = typeof(double),
        ["float8"] = typeof(double),
        ["numeric"] = typeof(decimal),
        ["decimal"] = typeof(decimal),
        ["money"] = typeof(decimal),
        ["character varying"] = typeof(string),
        ["varchar"] = typeof(string),
        ["character"] = typeof(string),
        ["bpchar"] = typeof(string),
        ["text"] = typeof(string),
        ["name"] = typeof(string),
        ["uuid"] = typeof(Guid),
        ["timestamp"] = typeof(DateTime),
        ["timestamp without time zone"] = typeof(DateTime),
        ["timestamptz"] = typeof(DateTimeOffset),
        ["timestamp with time zone"] = typeof(DateTimeOffset),
        ["date"] = typeof(DateOnly),
        ["time"] = typeof(TimeSpan),
        ["time without time zone"] = typeof(TimeSpan),
        ["bytea"] = typeof(byte[]),
        ["json"] = typeof(string),
        ["jsonb"] = typeof(string),
    }.ToFrozenDictionary();

    /// <summary>UTF-8 下一个字符最多 4 字节（代理对）。与血统同一格：这一家没有"字节上限"这扇闸门，
    /// 这个乘数只是给 FitsBytes 一个永不误拒的宽松尺子</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return column.MaxChars is { } chars ? $"VARCHAR({chars})" : "TEXT";

        if (clrType == typeof(byte[]))
            return "BYTEA";

        // 枚举存 INTEGER，与血统同一决定（PG 的 CREATE TYPE … AS ENUM 是"每库一套类型名"的路子，
        // 跨库对齐时它比 int 更难迁）；绑定侧 PostgreSqlValues 同样按整数落地。
        if (clrType.IsEnum)
            return "INTEGER";

        return clrType switch
        {
            var t when t == typeof(bool) => "BOOLEAN",
            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INTEGER",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "NUMERIC(20,0)",
            var t when t == typeof(float) => "REAL",
            var t when t == typeof(double) => "DOUBLE",
            var t when t == typeof(decimal) => $"NUMERIC({column.Precision ?? 18},{column.Scale ?? 4})",
            var t when t == typeof(DateTime) => "TIMESTAMP",
            var t when t == typeof(DateTimeOffset) => "TIMESTAMPTZ",
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "TIME",
            var t when t == typeof(Guid) => "UUID",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"DuckDB 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BYTEA" };

        // 与血统同一格：这一家的字符串只有一种量纲：字符。字节数由编码决定、由内容决定，
        // MaxBytes 只能给一个"永不误拒"的宽松上界。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 Char：与血统同一句形状——varchar(n) 与 char(n) 的 n 都是字符数；而 DuckDB 对这个 n
    /// 本身不设约束（宽度声明在引擎侧被忽略，那一串名照发、只是别把它读成库侧的约束）。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
