using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.OracleBase;

/// <summary>Oracle 血统的参数绑定与字面量内联。四处分岔：不带 N 前缀——N'…' 是 NVARCHAR2 的字面量
/// 而映射走 VARCHAR2(n CHAR)，加前缀等于给一次比较塞进一次隐式转换；布尔内联成 1/0（没有原生布尔）；
/// 二进制可以内联为 HEXTORAW('CAFE')——它的入参只有十六进制这一种解释，会话里没有能改它含义的开关，
/// 这正是"能不能内联"按形状判而不是按族判的道理；日期时间必须写成函数调用 TO_TIMESTAMP(文本, 格式)——
/// TIMESTAMP '…' 那种 ANSI 字面量吃会话的 NLS_TIMESTAMP_FORMAT，格式串写死在语句里，
/// 会话说什么都不影响它读出什么。枚举转整数走 IConvertible 而不是 Convert.ChangeType（热路径纪律）。</summary>
public sealed class OracleValues : IValueBinder
{
    public static OracleValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // Size 在这一家由驱动自己算，带上只是为了参数对拍能比出"两边各要了几颗参数"。
        return new AdoParameter(name, Normalize(value), DbTypeOf(value, hint), SizeOf(value), hint);
    }

    static object? Normalize(object? value) =>
        value switch
        {
            null => DBNull.Value,

            // 列上是 NUMBER(10)，传枚举实例过去会被当成字符串或报错。
            { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

            var typed => typed,
        };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：绑宽的尺子不许是"我觉得 int 够了"——
    /// byte 底被 widen 成 int 之后 DbType.Byte 与实参类型对不上，报错的是驱动而不是这里。</summary>
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
            Guid => 36,
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

            // DbType.DateTime 上线的是 ADO 那个带毫秒的旧型，而列是 TIMESTAMP(3)；
            // TIMESTAMP(3) WITH TIME ZONE 那一列要的是 DateTimeOffset。
            // 前缀与 Type 都不许现场猜，都由值自己的型决定。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(Guid) => DbType.String,
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

                // 空串 = 这一格拒了（非有限值）。NaN 与无穷只有"带引号再 cast"那一种写法，
                // 而它依赖会话的 NLS_NUMERIC_CHARACTERS——文本的含义由会话决定时，宁可留回占位符。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            // CHAR(36)：文本形态就是这一列的物理形态，所以内联写字符串。
            case Guid id:
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql =
                    $"TO_TIMESTAMP({Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))},'YYYY-MM-DD HH24:MI:SS.FF3')";
                return true;

            case DateTimeOffset withOffset:
                // 文本里的 "+08:00" 与格式串里的 TZH:TZM 是一对：这一族读带时区的时间戳
                // 没有"按会话时区解释一个裸串"那一条退路，偏移量必须写在文本里。
                sql =
                    $"TO_TIMESTAMP_TZ({Literal(withOffset.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))},'YYYY-MM-DD HH24:MI:SS.FF3 TZH:TZM')";
                return true;

            case DateOnly day:
                sql =
                    $"TO_DATE({Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))},'YYYY-MM-DD')";
                return true;

            case byte[] bytes:
                sql = $"HEXTORAW({Literal(Convert.ToHexString(bytes))})";
                return true;

            // 其余类型（enum、TimeSpan、char、未知对象）交回参数路径：TimeSpan 的字面量要写成
            // INTERVAL '…' DAY TO SECOND，间隔文本的引号里是"带不带小数秒、几位"两个会话相关的事，不值当赌。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联在这一族只有一件事要做：拒掉非有限值（NaN 与 ∞ 那些非 ASCII 形状）。
    /// 不需要补 .0：NUMBER 没有整数与浮点之分，3 与 3.0 是同一个值的两种写法，
    /// 补那一个 .0 只会让黄金样本比出一次没有语义变化的改动。</summary>
    static string InlineNumber(string text)
    {
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        return text;
    }

    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], ':');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
