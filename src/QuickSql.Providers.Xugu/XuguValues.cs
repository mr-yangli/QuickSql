using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的参数绑定与字面量内联。TryInlineLiteral 只有日志一个消费方——本文件所有取舍改变的是
/// "日志里那条能直接复制去跑的 SQL"，执行侧走参数。
/// 字符串内联取单引号翻倍（文档给了"翻倍或转义"却没说转义符是哪颗，产物也只做翻倍——翻倍是两边共同认可的那支）；
/// 布尔内联 1/0：列是 TINYINT、谓词比整数，内联若发 TRUE 就是"列/谓词/字面量"三处各说一句话，猜错那半边是
/// INSERT 类型不匹配；二进制不内联（'\x' 前缀在会话参数下解释不明，猜错是语法错或改值）；
/// 时刻字面量分隔符是空格不是 T（两份产物写的同一个串）。</summary>
public sealed class XuguValues : IValueBinder
{
    public static XuguValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // Size 让驱动按值自己算；仍然填它，是为了让各家参数清单人工对读时"每颗参数带了什么"可比。
        return new AdoParameter(name, Normalize(value), DbTypeOf(value, hint), SizeOf(value), hint);
    }

    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,

        // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 INT。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        var typed => typed,
    };

    /// <summary>枚举的底层类型由声明决定，按它取对应宽度：byte 底不许被 widen 成 int，
    /// 否则 DbType.Byte 与实参类型对不上，报错的是驱动。</summary>
    static object EnumAsIntegral(object value)
    {
        var convertible = (IConvertible) value;

        return Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
        {
            TypeCode.Byte => convertible.ToByte(null),
            TypeCode.SByte => convertible.ToSByte(null),
            TypeCode.Int16 => convertible.ToInt16(null),
            TypeCode.UInt16 => convertible.ToUInt16(null),
            TypeCode.UInt32 => convertible.ToUInt32(null),
            TypeCode.Int64 => convertible.ToInt64(null),
            TypeCode.UInt64 => convertible.ToUInt64(null),
            _ => convertible.ToInt32(null),
        };
    }

    static int SizeOf(object? value) => value switch
    {
        string text => text.Length,
        byte[] bytes => bytes.Length,
        char => 1,
        _ => 0,
    };

    static DbType DbTypeOf(object? value, ColumnContract? hint)
    {
        var clrType = value?.GetType() ?? hint?.ClrType;

        if (clrType is null)
            return DbType.Object;

        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (clrType == typeof(string))
            return DbType.String;

        if (clrType == typeof(byte[]))
            return DbType.Binary;

        if (clrType.IsEnum)
            return DbType.Int32;

        return clrType switch
        {
            var t when t == typeof(bool) => DbType.Boolean,
            var t when t == typeof(byte) || t == typeof(sbyte) => DbType.Byte,
            var t when t == typeof(short) || t == typeof(ushort) => DbType.Int16,
            var t when t == typeof(int) || t == typeof(uint) => DbType.Int32,
            var t when t == typeof(long) || t == typeof(ulong) => DbType.Int64,
            var t when t == typeof(float) => DbType.Single,
            var t when t == typeof(double) => DbType.Double,
            var t when t == typeof(decimal) => DbType.Decimal,

            // DbType.DateTime 上线的是驱动里的旧型，而列是 DATETIME(3)。非 null 仍取 DateTime2（五家同取，
            // 来路各不同）：毫秒保真要它，且这条非 null 路在活库上一直绿。
            // <b>null 改绑旧型</b>【实测·obj/logs/v42-xugu-fix-probe-2.log P01/P09 对 E2/P16、-3.log F1】：
            // InsertOrUpdate 的 MERGE 在 USING (SELECT … UNION ALL …) 源臂里把每一格都绑成参数，
            // 同一列位置上"非 null 的 DateTime2"与"绑成 DateTime2 的 DBNull"混排时，驱动对那颗空值做
            // DATETIME→BINARY 的型推，当场 [E34501]/[E17049] 没有实现DATETIME到BINARY的转换 ——
            // 三行全 null（E2）与整臂全绑旧型（P16）都绿，唯独混排那颗红。空值没有精度可失，旧型是安全臂：
            // 落库读回 NULL 原样、同臂非 null 侧毫秒 567 保真（F1 落库核验），
            // INSERT/UPDATE/谓词三侧同臂无殃及（F2/F4/F5/F6）。
            var t when t == typeof(DateTime) => value is null ? DbType.DateTime : DbType.DateTime2,

            // 这一行到不了：列那一侧在 XuguTypes 就抛了。写出来是为了让"绑定层认得、缺的是列"在读代码时看得见。
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(Guid) => DbType.Guid,
            var t when t == typeof(char) => DbType.String,
            _ => DbType.Object,
        };
    }

    public bool TryInlineLiteral(object? value, ColumnContract? hint, out string sql)
    {
        switch (value)
        {
            case null:
                sql = "NULL";
                return true;

            case string text:
                sql = Literal(text);
                return true;

            case bool flag:
                sql = flag ? "1" : "0";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 拒（非有限值）。'NaN' 那种带引号的写法没有出处，而裸 NaN 会被解析成列名。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                // 'D' 形只有十六进制与短横，与 CHAR(36) 那一列同形。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            // 不内联的三类：byte[]（没有可信的十六进制函数）、DateTimeOffset（列那一侧根本不存在）、
            // TimeSpan/枚举/未知对象（一律交回参数路径）。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联：纯整数形式要补小数点，指数式不许补——1E-08.0 是语法错，
    /// 这个错在被抄进别的族时又撞出来一次。这一家 3 与 3.0 是否同档隐式转换未实测，取与另四族一致。</summary>
    static string InlineNumber(string text)
    {
        // 判整串字符集而不是判首字符：负数打头的 '-' 是合法的浮点首字符，只判首字符会放它过来。
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        if (!text.Contains('.') && !text.Contains('E', StringComparison.OrdinalIgnoreCase))
            text += ".0";

        return text;
    }

    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], ':');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
