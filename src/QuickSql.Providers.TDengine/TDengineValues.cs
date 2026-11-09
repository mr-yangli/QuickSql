using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.TDengine;

/// <summary>CLR 值 → 参数与字面量。文本里占位符是位置形 ?（名字不进文本），但驱动侧参数名
/// 必须以 $/@/# 打头——stmt2 按 ParameterName 的前缀给值分类（$→tags、@→data、#→表名，
/// VendorDoc·TDengine·驱动源码 TDengineCommand.Statement），非 @ 前缀的参数名当场
/// ArgumentException。所以 binder 在这颗唯一的翻译点上把驱动名翻成 @pN：文本、日志、编号
/// 都还是方言那一套，只有跨进驱动对象那一步换名。Guid 归一成 'D' 形字符串：列型是
/// VARCHAR(36) 文本形，而驱动对 Guid 对象认不认没有读数，字符串是两家中性值。
/// 【未实测】DateTime 直绑原值——ClickHouse"参数化丢毫秒"那颗教训是那一家驱动的序列化问题，
/// 不预支到这一家；TIMESTAMP 的刻度保真重开条件与整层同一条。</summary>
public sealed class TDengineValues : IValueBinder
{
    public static TDengineValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // 型号与尺寸都跟着归一化后的形状走（Guid 化串之后型号就该是 String）；
        // DriverName 携带驱动侧名字：'?p0' → '@p0'，编号不动。
        var normalized = Normalize(value);

        return new(name, normalized, DbTypeOf(normalized, hint), SizeOf(normalized, hint), hint,
            DriverName: '@' + name.TrimStart('?'));
    }

    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,

        // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 INT。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        // Guid → 'D' 形字符串：列型 VARCHAR(36)，驱动对 Guid 的原生绑定没有读数，
        // 字符串是两家中性值（读侧 Guid_Char36 家族的 materializer 走文本形，同一结论）。
        Guid id => id.ToString("D", CultureInfo.InvariantCulture),

        var typed => typed,
    };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：byte 底不许被 widen 成 int，
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

    /// <summary>按值的形状取宽度（字符数或字节数）。本家没有"参数最长几字节要跟 DDL 同数"那一档
    ///（stmt2 按列型收值），hint 不参与尺寸：尺子是仓库侧的，驱动侧只需要值自己的形状。</summary>
    static int SizeOf(object? value, ColumnContract? hint) => value switch
    {
        null => 0,
        string text => text.Length,
        byte[] bytes => bytes.Length,
        char => 1,
        _ => 0,
    };

    static DbType DbTypeOf(object? value, ColumnContract? hint)
    {
        // null 经 Normalize 归一化成 DBNull.Value 进来：型号不跟 DBNull 走，按"真 null + hint"判。
        if (value is DBNull)
            value = null;

        var clrType = value?.GetType() ?? hint?.ClrType;

        if (clrType is null)
            return DbType.Object;

        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (clrType == typeof(string))
            return DbType.String;

        if (clrType.IsEnum)
            return DbType.Int32;

        return clrType switch
        {
            var t when t == typeof(sbyte) => DbType.SByte,
            var t when t == typeof(byte) => DbType.Byte,
            var t when t == typeof(short) => DbType.Int16,
            var t when t == typeof(ushort) => DbType.UInt16,
            var t when t == typeof(int) => DbType.Int32,
            var t when t == typeof(uint) => DbType.UInt32,
            var t when t == typeof(long) => DbType.Int64,
            var t when t == typeof(ulong) => DbType.UInt64,
            var t when t == typeof(bool) => DbType.Boolean,
            var t when t == typeof(float) => DbType.Single,
            var t when t == typeof(double) => DbType.Double,
            var t when t == typeof(decimal) => DbType.Decimal,

            // 列型 TIMESTAMP，本家时刻型只有这一颗。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(char) => DbType.String,

            // 到不了：列那一侧在 TDengineTypes 就抛了（DateOnly/TimeOnly/TimeSpan/DateTimeOffset）。
            // 仍然写出来而不是留默认分支，让"这一型在绑定层是被认得的、缺的是列"这件事在读代码时看得见。
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) => DbType.Time,
            var t when t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(byte[]) => DbType.Binary,
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
                // 与谓词那侧同一字形（EmitBooleanEquality 落 = true/ = false）。
                sql = flag ? "true" : "false";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值，NaN/Infinity 裸写会被解析成列名）。
                // 有没有 'NaN' 那种带引号的写法没有读数，能证之前不发。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                // 'D' 形只有十六进制与短横，与 VARCHAR(36) 那一列同形。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                // 本家时刻字面量的标准形：带引号的 'yyyy-MM-dd HH:mm:ss.fff'
                //（VendorDoc·TDengine：TIMESTAMP 字面量页；毫秒三档以内的字面量被收）。
                sql = Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return true;

            case char letter:
                sql = Literal(letter.ToString(CultureInfo.InvariantCulture));
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

    /// <summary>浮点内联：纯整数形式要补小数点，指数式不许补（1E-08.0 是语法错，几族同源的判据）。
    /// 本家 FLOAT/DOUBLE 列上 3 与 3.0 是不是同一档隐式转换未实测，取与另几族一致。</summary>
    static string InlineNumber(string text)
    {
        // 判整串字符集而不是判首字符：`-∞` 打头那颗 `-` 是合法的浮点首字符，只判首字符会放它过来。
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        if (!text.Contains('.') && !text.Contains('E', StringComparison.OrdinalIgnoreCase))
            text += ".0";

        return text;
    }

    /// <summary>单引号成对转义。本家没有 N' 宽字符字面量那一形（PrefixUnicodeLiterals 保持接口默认 false），
    /// 字符串的编码由库按 UTF-8 存取。</summary>
    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '?');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
