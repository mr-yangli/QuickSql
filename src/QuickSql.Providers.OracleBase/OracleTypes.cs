using QuickSql.Metadata;
using System.Collections.Frozen;

namespace QuickSql.Providers.OracleBase;

/// <summary>CLR ↔ Oracle 血统类型名。要点：字符串的量纲要靠写法自己钉死——VARCHAR2(n) 的 n 在 Oracle 里由
/// NLS_LENGTH_SEMANTICS 决定（默认 BYTE）、在达梦里由建库参数决定，同一份契约在两个会话设置下
/// 能装下不同长度的中文，所以这里发 VARCHAR2(n CHAR)，把量纲写在列上、不赌会话默认值；
/// 没有原生布尔，bool 落 NUMBER(1)，WHERE flag = 1 是正常写法，可空布尔列上 = 0 匹配不到 NULL 的事故也跟着回来；
/// 没有原生 uuid，Guid 落 CHAR(36)；大文本与二进制是 LOB 而 LOB 不能用等号比（守卫因此整批退回逐条）；
/// 时间跨度没有 TIME 型，TimeSpan 落 INTERVAL DAY(0) TO SECOND(3)。本类的产出没有在一台真库上跑过，
/// 逐位出处是厂商文档口径。类不 sealed：唯一的可变点是 VarCharType 那颗字符串类型名，留给第一个现场点验它的库种。</summary>
public class OracleTypes : ITypeMap
{
    public static OracleTypes Instance { get; } = new();

    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["varchar2"] = typeof(string),
        ["nvarchar2"] = typeof(string),
        ["char"] = typeof(string),
        ["nchar"] = typeof(string),
        ["clob"] = typeof(string),
        ["nclob"] = typeof(string),
        ["number"] = typeof(decimal),
        ["float"] = typeof(double),
        ["binary_float"] = typeof(float),
        ["binary_double"] = typeof(double),
        ["long"] = typeof(string),
        ["raw"] = typeof(byte[]),
        ["blob"] = typeof(byte[]),
        ["bfile"] = typeof(string),
        ["date"] = typeof(DateTime),
        ["timestamp"] = typeof(DateTime),
        ["interval day(0) to second(3)"] = typeof(TimeSpan),
        ["rowid"] = typeof(string),
        ["urowid"] = typeof(string),
        ["binary_integer"] = typeof(int),
        ["pls_integer"] = typeof(int),
    }.ToFrozenDictionary();

    /// <summary>AL32UTF8 下一个字符最多 4 字节（代理对）。这一族的 VARCHAR2(n CHAR) 里 n 是字符数，
    /// 字节上限由编码决定，列上没有一个字节能问出来，所以 FitsBytes 拿到的是一条永不误拒的边界而不是库里的约束。</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return column.MaxChars is { } chars ? VarCharType(chars) : "CLOB";

        if (clrType == typeof(byte[]))
            return "BLOB";

        // 枚举存整数，与两族同一决定（这一族的 CREATE TYPE … AS ENUM 不存在，CHECK 约束又是另一套跨库更难迁的东西）；
        // 绑定侧 OracleValues 同样按整数落地。
        if (clrType.IsEnum)
            return "NUMBER(10)";

        return clrType switch
        {
            var t when t == typeof(bool) => "NUMBER(1)",
            var t when t == typeof(byte) || t == typeof(sbyte) => "NUMBER(3)",
            var t when t == typeof(short) || t == typeof(ushort) => "NUMBER(5)",
            var t when t == typeof(int) || t == typeof(uint) => "NUMBER(10)",
            var t when t == typeof(long) || t == typeof(ulong) => "NUMBER(19)",
            var t when t == typeof(float) => "BINARY_FLOAT",
            var t when t == typeof(double) => "BINARY_DOUBLE",
            var t when t == typeof(decimal) =>
                $"NUMBER({column.Precision ?? 18},{column.Scale ?? 4})",
            var t when t == typeof(DateTime) => "TIMESTAMP(3)",
            var t when t == typeof(DateTimeOffset) => "TIMESTAMP(3) WITH TIME ZONE",
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) =>
                "INTERVAL DAY(0) TO SECOND(3)",
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException(
                $"Oracle 血统（达梦/神通）未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"
            ),
        };
    }

    /// <summary>字符串的类型名，这一颗是覆写点：把量纲写成 CHAR 是 Oracle 的语法，
    /// 达梦那一行只说 VARCHAR/VARCHAR2，认不认这个限定词没有活库可证。若现场报语法错而覆写成 VARCHAR2(n)，
    /// 等于把量纲交回给会话默认值——那时量纲这一档在这一家重新变成两个答案，不是这里的退路能顺手抹平的事。</summary>
    protected virtual string VarCharType(int chars) => $"VARCHAR2({chars} CHAR)";

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BLOB" };

        // 与金仓那一族同一形状的结论、不同的来路：那一家是"编码在集群级定死所以只有一个答案"，
        // 这一家是"我们把答案写在了列上"。两者都让 MaxBytes 只剩一个永不误拒的宽松上界。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType)
    {
        // 带限定的写法先剥到基名：TIMESTAMP(3) WITH TIME ZONE 这类名字在 ALL_TAB_COLUMNS 里
        // 带精度与括号，按整串查表永远查不到。
        var baseName = TrimBaseType(storeTypeName);

        if (TryLookup(baseName, out clrType))
            return true;

        // NUMBER 的标度与精度决定它是不是整数：契约侧存的是 CLR 型的形状，所以这里只把"整数档"认出来。
        if (baseName == "number")
        {
            var scale = ScaleOf(storeTypeName);

            clrType = scale switch
            {
                null => typeof(decimal),
                0 => typeof(long),
                _ => typeof(decimal),
            };

            return true;
        }

        clrType = null;
        return false;
    }

    bool TryLookup(string baseName, out Type? clrType) =>
        StoreToClr.TryGetValue(baseName, out clrType);

    /// <summary>这一族有两个答案（VARCHAR2(n) 看会话、VARCHAR2(n CHAR) 看写法），VarCharType 选了把答案写在列上那一种。
    /// 真正的判据在 ISchemaReader.ProbeLengthSemanticsAsync 的读数里——那里量的才是库里那一列实际是字节还是字符。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;

    static string TrimBaseType(string storeTypeName)
    {
        var name = storeTypeName.Trim();
        var paren = name.IndexOf('(');
        var space = name.IndexOf(' ');

        var cut =
            paren < 0 ? space
            : space < 0 ? paren
            : Math.Min(paren, space);

        // INTERVAL DAY(0) TO SECOND(3) 的基名跨两个词，所以它单独立一条而不是按第一个空格切。
        if (name.StartsWith("interval ", StringComparison.OrdinalIgnoreCase))
            return name.ToLowerInvariant();

        return (cut < 0 ? name : name[..cut]).Trim().ToLowerInvariant();
    }

    static int? ScaleOf(string storeTypeName)
    {
        var open = storeTypeName.IndexOf('(');
        var comma = storeTypeName.IndexOf(',', open < 0 ? 0 : open);

        if (open < 0 || comma < 0)
            return null;

        var close = storeTypeName.IndexOf(')', comma);

        return
            close > comma + 1
            && int.TryParse(storeTypeName.AsSpan(comma + 1, close - comma - 1), out var scale)
            ? scale
            : null;
    }
}
