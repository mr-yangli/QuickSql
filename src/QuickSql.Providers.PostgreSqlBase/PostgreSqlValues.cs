using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的参数绑定与字面量内联。三处与 T-SQL 不同：字符串字面量绝不带 N 前缀（编码在集群级定死，
/// 前缀只会被当成不认识的语法）；bool 内联 TRUE/FALSE 而不是 1/0（boolean = integer 是类型错误，不是隐式转换）；
/// byte[] 拒绝内联（bytea 字面量写法取决于 bytea_output 会话参数，猜错比留占位符更糟）。
/// 枚举转整数走 IConvertible 而不是 Convert.ChangeType（少一处要显式记账的热路径形状）。</summary>
public sealed class PostgreSqlValues : IValueBinder
{
    public static PostgreSqlValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // Size 在这一家没有意义（驱动按值自己算），但 AdoParameter 的形状要与 T-SQL 那一侧一致，
        // 否则参数对拍会比不出"两边各要了几颗参数"。
        //
        // DbType 跟着换形之后那一颗走，但只在那一形真的换了的时候：时刻那一格可能把 DateTime 换成
        // DateTimeOffset（带时区的列），型别必须跟着换——否则交出去的是偏移而对驱动说 DateTime2。
        var shaped = Normalize(value, hint);

        return new AdoParameter(
            name,
            shaped,
            DbTypeOf(value is DateTime ? shaped : value, hint),
            SizeOf(value),
            hint
        );
    }

    static object? Normalize(object? value, ColumnContract? hint) =>
        value switch
        {
            null => DBNull.Value,

            // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 INTEGER。
            { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

            // 带偏移的时刻要与列同型（下面那一句），裸时刻要与列同型（同一句的返）。
            DateTime moment => BareOrOffset(moment, hint),

            var typed => typed,
        };

    /// <summary>只改交出去的那一形的说法，不改它表示的时刻。Npgsql 6 起按 DateTime.Kind 分型：
    /// Kind=Utc 只肯进带时区那一型，写进裸 timestamp 在绑定当场抛（Cannot write DateTime with Kind=UTC）。
    /// 裸列摘掉 Utc 的说法（SpecifyKind(Unspecified)，逐字节同文的改写；换 Local 才是数据改写）；
    /// 带时区的列按 DbType 认型，要的是换成 DateTimeOffset 的同一瞬时——Utc/Unspecified 记零偏移，
    /// Local 保留本机偏移。两形的分界只看列声明出来的那个型，不看 CLR 型自己怎么说。</summary>
    static object? BareOrOffset(DateTime moment, ColumnContract? hint)
    {
        var store = hint?.StoreTypeName;

        var wantsOffset =
            store is not null
            && (
                store.Contains("timestamptz", StringComparison.OrdinalIgnoreCase)
                || store.Contains("with time zone", StringComparison.OrdinalIgnoreCase)
            );

        if (!wantsOffset)
            return moment.Kind == DateTimeKind.Utc
                ? DateTime.SpecifyKind(moment, DateTimeKind.Unspecified)
                : moment;

        return moment.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(moment.Ticks, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(
                moment.Ticks,
                TimeZoneInfo.Local.GetUtcOffset(moment)
            ),
            _ => new DateTimeOffset(
                DateTime.SpecifyKind(moment, DateTimeKind.Utc).Ticks,
                TimeSpan.Zero
            ),
        };
    }

    /// <summary>枚举的底层类型由声明决定，按它取对应宽度：byte 底不许被 widen 成 int，
    /// 否则 DbType.Byte 与实参类型对不上，报错的是驱动而不是这里。</summary>
    static object EnumAsIntegral(object value)
    {
        var convertible = (IConvertible)value;

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

    static int SizeOf(object? value) =>
        value switch
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

            // 与 T-SQL 同一格理由：DbType.DateTime 上线的是那一家的旧型，而列是 TIMESTAMP(3)。
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

            case string text:
                sql = Literal(text);
                return true;

            case bool flag:
                sql = flag ? "TRUE" : "FALSE";
                return true;

            case byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong:
                sql = Invariant(value);
                return true;

            case float
            or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。这一家有 'NaN'::double precision 的写法，但它是
                // "带引号再 cast"的形状，而裸 NaN 会被解析成列名，宁可不内联。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(
                    moment.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)
                );
                return true;

            case DateTimeOffset withOffset:
                sql = Literal(
                    withOffset.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)
                );
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            // 其余类型（enum、TimeSpan、byte[]、未知对象）交回参数路径。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联只有一件事要做对：纯整数形式要补小数点（3 会被当成 integer 常量），
    /// 指数式不许补（1E-08 本来就是合法的 float8 常量，补成 1E-08.0 是语法错）。
    /// 非有限值交回空串：这一家有 'NaN'::double precision 那种"带引号再 cast"的写法，
    /// 裸 NaN 会被解析成列名，而它没有"哪种设置下都成立"的读数。</summary>
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
