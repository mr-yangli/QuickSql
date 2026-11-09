using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.SqlServer;

/// <summary>T-SQL 的参数绑定与字面量内联。
/// 两条硬事实决定了这里的默认值：一是 varchar 在 cp1252 排序规则下会把中文静默写成 ?，
/// 所以字符串一律按 Unicode 传参、字面量带 N''；二是 uniqueidentifier 是原生类型，
/// 所以 Guid 不做文本化（跨库对齐的场景才需要 GuidAsText）。</summary>
public sealed class SqlServerValues : IValueBinder
{
    public static SqlServerValues Instance { get; } = new();

    public BindingProfile Profile { get; } =
        new(StringsAsUnicode: true, GuidAsText: false, DateAsUtc: false, DecimalAsDouble: false);

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        var dbType = DbTypeOf(value, hint);

        // Size 只按实参长度给：按契约的 MaxChars 给会截断（SqlParameter 在 Size 小于内容时是截断而不是报错），
        // 那是比"多分配一点"严重得多的错。hint 原样带上：值级钩子要按列改写参数，出处不能丢。
        return new AdoParameter(name, Normalize(value), dbType, SizeOf(value), hint);
    }

    static object? Normalize(object? value) =>
        value switch
        {
            null => DBNull.Value,

            // enum 存 INT 是既定映射，而驱动不会替我们转：直接传枚举实例会被当成字符串或报错。
            { } typed when typed.GetType().IsEnum => Convert.ChangeType(
                typed,
                Enum.GetUnderlyingType(typed.GetType()),
                CultureInfo.InvariantCulture
            ),

            var typed => typed,
        };

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
            return hint is { Unicode: false } ? DbType.AnsiString : DbType.String;

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

            // DateTime 必须给 DateTime2：默认的 DbType.DateTime 上线的是遗留 datetime，会先把值取整到
            // 1/300 秒（.456 → .457）才进那个 datetime2(3) 的列，等值查询就此永远差那几毫秒。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(Guid) => DbType.Guid,

            // nchar(1) 用 String + Size=1，不用 StringFixedLength：后者要求 Size 精确等于内容长度，
            // 而空 char 会绑成 Size=0 直接报错。
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

            // 字面量必须带 N 前缀，否则字串在解析期就被降级成排序规则的代码页。
            case string text:
                sql = Literal(text, hint is not { Unicode: false });
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
                sql = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                return true;

            case float
            or double:
                sql = InlineNumber(value);

                // 空串 = 这一格拒了（非有限值）。T-SQL 的 float 类型压根存不下 NaN 与无穷（实测：TRY_CAST('NaN' AS float) 为 NULL），
                // 所以这里宁可留占位符，也不产出一句贴进 SSMS 就报错的文本。
                // 交回参数路径之后由驱动决定它怎么报 —— 那是库里那一列自己的口径，不是我们的字符串形状。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                sql = Literal(id.ToString("D"), unicode: false);
                return true;

            case DateTime moment:
                sql = Literal(
                    moment.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                    unicode: false
                );
                return true;

            case DateTimeOffset withOffset:
                sql = Literal(
                    withOffset.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    unicode: false
                );
                return true;

            case DateOnly day:
                sql = Literal(
                    day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    unicode: false
                );
                return true;

            case byte[] raw:
                sql = "0x" + Convert.ToHexString(raw);
                return true;

            // 其余类型（enum、TimeSpan、未知对象）交回参数路径：内联一条猜错的比参数更糟。
            default:
                sql = string.Empty;
                return false;
        }
    }

    /// <summary>浮点内联只有一件事要做对：纯整数形式要补小数点（3 贴进 SSMS 会按 int 常量处理、
    /// 小数位丢失），指数式不许补（SELECT 1E-08 是合法的 float 常量，补成 1E-08.0 报「.0 附近有语法错误」）。
    /// 非有限值（NaN / ∞ / -∞）交回空串，由调用方退回参数路径——判据是「整串都是浮点字面量允许的字符」
    /// 而不是「首字符像数字」：-∞ 打头那颗 - 是合法的浮点首字符，只判首字符会把它放过来并补成 -∞.0。</summary>
    static string InlineNumber(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;

        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        if (!text.Contains('.') && !text.Contains('E', StringComparison.OrdinalIgnoreCase))
            text += ".0";

        return text;
    }

    static string Literal(string value, bool unicode)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '@');

        writer.AppendStringLiteral(value, unicode);

        return writer.ToString();
    }
}
