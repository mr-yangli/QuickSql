using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 系的参数绑定与字面量内联。三处分岔：字符串一律留回占位符——这一家默认把 \ 当字符串转义符
/// （除非会话开了 NO_BACKSLASH_ESCAPES），而 AppendStringLiteral 只做单引号翻倍那一半，
/// 猜错的这一半是改值不是报错，代价只在 ToLogSql() 的可读性（执行走参数，一个字节没变）；
/// bool 内联成 TRUE/FALSE（它们是 1/0 的同义词）；byte[] 拒绝内联——内联长度不受参数预算管、
/// 只受 max_allowed_packet 管，一张 LONGBLOB 表能把"预算内"的批量变成"包外"的失败。
/// 时刻字面量的分隔符是空格不是 T：文档规范形（认不认 T 取决于解析器版本，而日志 SQL 是给人复制去跑的）。
/// 枚举转整数走 IConvertible 而不是 Convert.ChangeType。</summary>
public sealed class MySqlValues : IValueBinder
{
    public static MySqlValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // Size 由驱动按值自己算；这里仍填它，让各家的参数清单人工对读时"每颗参数带了什么"形状可比。
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
    /// 否则 DbType.Byte 与实参类型对不上，报错的是驱动而不是这里。</summary>
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

            // 与另两族同一格理由：DbType.DateTime 上线的是驱动里的旧型，而列是 DATETIME(3)。
            var t when t == typeof(DateTime) => DbType.DateTime2,
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

            // 字符串不内联（反斜杠与 sql_mode 两条：见类注释第一条）。
            case string:
                sql = string.Empty;
                return false;

            case bool flag:
                sql = flag ? "TRUE" : "FALSE";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。裸 NaN 会被解析成列名，宁可不内联。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                // 'D' 形只有十六进制与短横，不含反斜杠，可以走 AppendStringLiteral 那一支。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            // DateTimeOffset 不内联：这一家根本没有那一型可落（MySqlTypes 在那一格是抛的），
            // 把偏移量写进字面量再等库按会话时区折掉是另一种错。其余类型同样交回参数路径。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联判据（四家同源）：纯整数形式要补小数点，指数式不许补（1E-08.0 是语法错）。
    /// 这一家的 FLOAT/DOUBLE 列上 3 与 3.0 是不是同一档隐式转换未实测，取"与另两族一致"。</summary>
    static string InlineNumber(string text)
    {
        // 判整串字符集而不是判首字符：-∞ 打头那颗 - 是合法的浮点首字符，只判首字符会放它过来。
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        if (!text.Contains('.') && !text.Contains('E', StringComparison.OrdinalIgnoreCase))
            text += ".0";

        return text;
    }

    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '@');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
