using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s 的参数与字面量：六家里唯一"参数没有名字可带"的实现——占位符是位置形 ?，
/// AdoParameter.Name 仍要填（它是内核侧编号与 hint 的载体）。三处后果：按名回填那条路断了
/// （序是唯一对得上的依据，由方言层保证）；ToLogSql 恒等——文本里只有裸 ? 不跟名字，
/// "参数内联给 DBA"发不出带值文本；Size 不是装饰——量纲恒字节，hint 有 MaxBytes 时按它报，
/// 让驱动拿到与 DDL 同一个数。
/// 字符串内联取"发"（六家第三派）：位置形占位符本来不带名字，字面量与 ? 对服务端计划缓存
/// 是同一件事的两端。取向记在这里，等一台活库改判。</summary>
public sealed class Gbase8sValues : IValueBinder
{
    public static Gbase8sValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
        => new(name, Normalize(value), DbTypeOf(value, hint), SizeOf(value, hint), hint);

    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,

        // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 INTEGER。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        var typed => typed,
    };

    /// <summary>枚举按底层声明取对应宽度：byte 底不许被 widen 成 int，否则 DbType.Byte 与实参类型对不上，
    /// 报错的是驱动。这一份是六家第六次同形拷贝（最早那一族是判据的反例）。</summary>
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

    /// <summary>文本形参数按字节报（hint 的 MaxBytes 与 DDL 里的 n 是同一个数）；没有 hint 退回值自己的
    /// 字符长度——刻意不猜乘数：报小最多让驱动自己量，报大会让驱动按一颗不存在的预算截值。</summary>
    static int SizeOf(object? value, ColumnContract? hint) => value switch
    {
        null => 0,
        string text => hint?.MaxBytes ?? text.Length,
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

            // 与六家同取 DateTime2，但来路是本家的：列型是字段范围型，DbType.DateTime 不带精度声明
            // 也不影响文本——这一句留着，是"抄来源连理由一起抄"的反面教材。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(Guid) => DbType.Guid,
            var t when t == typeof(char) => DbType.String,

            // 到不了：列那一侧在 Gbase8sTypes 就抛了。写出来让"绑定层认得、缺的是列"在读代码时看得见。
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
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

                // 空串 = 拒（非有限值）。'NaN' 那种带引号的写法没有读到，裸 NaN 会被解析成列名。
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
    /// 这个错在被抄进别的族时又撞出来一次。本家 3 与 3.0 是否同档隐式转换未实测，取与五家一致。</summary>
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

    /// <summary>单引号成对转义，unicode: false——这一家没有带前缀的宽字符字面量那一形（codeset 建库时定），
    /// 与方言层 PrefixUnicodeLiterals 恒 false 是同一句事实的两个出口。</summary>
    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '?');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
