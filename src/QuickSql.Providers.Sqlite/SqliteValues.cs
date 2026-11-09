using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Sqlite;

/// <summary>CLR 值 → 参数与字面量。这一份是七家里唯一一处"绑定侧自己就是数据的形状"：
/// 驱动完全不看 DbType 与 Size，值往库里落成什么形状由 CLR 值型本身决定，所以所有真正的定型动作
/// 只能在 Normalize 里做完，DbTypeOf 与 SizeOf 写出来只为让读代码的人看见我们的意图。
/// 定点与 ulong 必须绑不变形文本（驱动交出去会自己重排标度，而文本与数在这一族永不相等——
/// "谁去格式化定点"在别家是风格问题、在本家是那一行命中与否）；时刻交给驱动（ISO 文本保住
/// 100 纳秒刻度，Kind 被整颗丢掉），TimeSpan/TimeOnly 绑 Ticks（直接交会被写成文本、与 INTEGER
/// 列声明撞在同一格）；内联字面量必须与绑定侧逐字同形——文本列上的等号比字面串，差一齿是
/// 一句 WHERE 静默返回 0 行而不是报错。参数名带前缀与不带前缀都收；匿名 ? 驱动直接拒
/// （引擎自己认 ?1）；null 必须显式折成 DBNull，留 null 驱动报"Value must be set"。</summary>
public sealed class SqliteValues : IValueBinder
{
    public static SqliteValues Instance { get; } = new();

    /// <summary>三颗（值、DbType、Size）同源于一次归一：把原始值喂给后两颗，会让"值说是文本而
    /// DbType 说是定点"在两处读起来矛盾。归一之后的形状恰好就是驱动会自己推断出来的那个形状，
    /// 两侧一致是唯一诚实的写法。</summary>
    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        var bound = Normalize(value);

        return new(name, bound, DbTypeOf(bound, hint), SizeOf(bound, hint), hint);
    }

    /// <summary>见类注释前三条。逐支：null → DBNull（驱动那一侧的硬要求）；枚举 → 底层整数
    /// （不折的话驱动按 ToString 落成文本，而列声明是 INTEGER）；decimal → 不变形文本、不抹尾零
    /// （decimal 自己保标度；直接交给驱动它写的也是精确文本、只是重排标度，而文本与数在这一族
    /// 永不相等，所以内联侧与绑定侧必须同一条归一）；ulong → 文本（直接交出读回 -1）；
    /// TimeSpan/TimeOnly → Ticks（原形那一路驱动写的是 .NET 往返文本，与 INTEGER 列声明撞格，
    /// 取的是与列一致的那一档）；char → string（两支同形，折与不折在库里没有差别，
    /// 写出来是把意图交给读这份文件的人）。其余一律原样交出。两颗要在交出之前知道：
    /// DateTime 的 Kind 会整颗丢掉（三种 Kind 交进去读回同一串），本家没有把"这是哪一种钟"
    /// 送进库的路；DateOnly 交出去得到的是裸日期而不是零点那一形——那正是 Fn.ToDate 两臂之差的另一半。</summary>
    static object? Normalize(object? value) =>
        value switch
        {
            null => DBNull.Value,
            { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),
            decimal money => money.ToString(CultureInfo.InvariantCulture),
            ulong wide => Invariant(wide),
            TimeSpan span => span.Ticks,
            TimeOnly clock => clock.Ticks,
            char letter => letter.ToString(),
            var typed => typed,
        };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：byte 底不许被 widen 成 int，
    /// 否则 DbType 与实参类型对不上。本家的拷贝比另六家更必要：这里 DbType 不参与，
    /// "落成什么"完全由这一颗返回的 CLR 型决定，宽窄选错会静默改变存储文本。</summary>
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

    /// <summary>尺寸。本家它是七家里唯一一颗实测证明完全不被读的，这里填的数不改变任何行为
    /// （只在 SqlLogging 打印参数时露一次脸）。之所以仍然按 hint 报而不是恒 0：
    /// 将来若换一颗认 Size 的驱动，这里不需要重学一遍"该报哪颗数"。</summary>
    static int SizeOf(object? value, ColumnContract? hint) =>
        value switch
        {
            null => 0,
            string text => hint?.MaxChars ?? text.Length,
            byte[] bytes => bytes.Length,
            _ => 0,
        };

    /// <summary>与 Normalize 同源，所以本方法看到的 decimal/ulong/TimeOnly/TimeSpan 已经是
    /// 字符串或整数了——下面那几臂与 SqliteTypes.GetStoreType 逐颗对得上，两处不许分岔。
    /// 本表里最没用的九行与最有用的第一行是同一件事：驱动不看它，而"没用的那颗填错也不会红"
    /// 正是它要写对的理由——它是本家绑定意图唯一可读的地方。</summary>
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
            var t when t == typeof(long) => DbType.Int64,

            // 定点与无符号归一后已经是文本，所以本家永远不会带着 DbType.Decimal 进来——
            // 那两臂留在这里是"如果谁把 Normalize 那一支删了，这里会先露出矛盾"的形状。
            var t when t == typeof(decimal) => DbType.Decimal,
            var t when t == typeof(ulong) => DbType.Int64,

            var t when t == typeof(float) => DbType.Single,
            var t when t == typeof(double) => DbType.Double,

            // DateTime2 与另六家同数而本家的理由最弱：DbType 那位不参与。取 DateTime2 而不是
            // DateTime，是为了不把"7 位小数会被截到 3 位"那一族语义写进本仓库的意图里——
            // 那一条在 T-SQL 是实测，在本家只是一句自述。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,

            // TimeOnly/TimeSpan 归一后是 long，所以这两臂今天到不了；写出来说的是
            // "列是整数、跨度按 Ticks"这一格契约。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Int64,
            var t when t == typeof(Guid) => DbType.Guid,
            var t when t == typeof(char) => DbType.String,
            _ => DbType.Object,
        };
    }

    /// <summary>字面量内联。本家取"发"，而它的判据与另六家没有一条相同：那六家在这格上争的是
    /// 计划缓存与 N' 前缀，本家争的是正确性——库里的值是文本，内联的形状只要与绑定侧差一齿，
    /// 一句 WHERE 就静默空集而不是报错，所以下面每一支都必须与 Normalize 逐字同形。
    /// 不发的两档：byte[]（X'…' 那一形没有点位实测，不发没问过的写法）、非有限浮点与未知对象。
    /// DateTimeOffset 这一档本家发，它是七家里唯一一颗"内联了带偏移文本"的实现，
    /// 理由与残余风险见 InlineOffset。</summary>
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

            // 库里那一格是 1/0，所以内联也只能是 1/0。
            case bool flag:
                sql = flag ? "1" : "0";
                return true;

            case byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long:
                sql = Invariant(value);
                return true;

            // ulong 与定点同一支：加引号（带形状入库的文本）。
            case ulong
            or decimal:
                sql = Literal(Invariant(value));
                return true;

            // 跨度落整数（列声明与绑定两侧都是 INTEGER）。
            case TimeSpan span:
                sql = Invariant(span.Ticks);
                return true;

            case TimeOnly clock:
                sql = Invariant(clock.Ticks);
                return true;

            case float
            or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。本家有 'Inf'/'NaN' 那一族 CAST 写法，
                // 而它落在 REAL 那一臂、与我们的文本档不同一条——没量过就不发。
                return sql.Length > 0;

            case Guid id:
                // 'D' 形小写，与驱动写入的那一形连大小写都一样。这一句在本家不是洁癖：
                // 文本等号区分大小写而 LIKE 不区分，大写形在 = 上是一场静默空集。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(InlineDateTime(moment));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            case DateTimeOffset withOffset:
                sql = Literal(InlineOffset(withOffset));
                return true;

            case char letter:
                sql = Literal(letter.ToString());
                return true;

            default:
                sql = string.Empty;
                return false;
        }
    }

    /// <summary>时刻内联形：yyyy-MM-dd HH:mm:ss 加一段去掉尾零的小数，全零时连小数点一起不留。
    /// 这一颗方法是本文件唯一一处"抄驱动的私有形状"，已知小数宽度（0/2/3/4/7 位）都各有一颗
    /// 读数逐字对过。之所以不写成定宽 7 位：那样内联出的串与库里存的串不相等（文本等号是
    /// 逐字符的），正是要防的静默空集；也不写成 3 位（另六家的常见做法）：本家存的是 100 纳秒，
    /// 截到毫秒会让一句真命中变空集。残余风险：跨零那一侧（23:59:59.9999999 过驱动之后
    /// 是不是还是那一串）没量过，属于"文本序 vs 时刻序"那一族口子。</summary>
    static string InlineDateTime(DateTime value) =>
        Stamp(value.ToString(SecondFormat, CultureInfo.InvariantCulture), value.Ticks);

    /// <summary>带偏移那一形：时刻与小数同一条归一，后面直接接 zzz。零偏移驱动写的是 +00:00、
    /// 不是 Z 也不是不留，所以只能取 zzz（z 得 +0、K 得 Z，两形与库里都不等）。本家发这一支
    /// 而另六家都不发，来路是"两侧同源"：绑定侧交出的就是驱动自己格式化的那一颗 DateTimeOffset，
    /// 内联侧复用同一条格式化规则，所以等号两侧可比；另六家不发是因为它们的库里那一格由
    /// 服务端归一、客户端猜不出形。真正的缺口在排序：带偏移文本的词法序不等于时刻序——
    /// 同一串钟面配两个偏移，按真实瞬间早的那颗在词法上排在后面（'+0' 排在 '+8' 前面）；
    /// 无偏移的串是带偏移那一串的前缀，同样比不出真实先后。所以本家的 DateTimeOffset 列
    /// 能等值而不能排序——那是排序语义的缺口而不是等号的缺口。</summary>
    static string InlineOffset(DateTimeOffset value) =>
        Stamp(value.ToString(SecondFormat, CultureInfo.InvariantCulture), value.Ticks)
        + value.ToString("zzz", CultureInfo.InvariantCulture);

    /// <summary>秒那一段后面接什么：非零小数就接（去掉尾零），整秒什么都不接。
    /// 之所以按 Ticks 算而不是一条 fffffff 格式串再 TrimEnd：偏移那一支的串以数字结尾，
    /// 整串 TrimEnd('0') 会把偏移也削掉——所以两颗分开拼、小数只属于自己的那一段。</summary>
    static string Stamp(string seconds, long ticks)
    {
        var fraction = (ticks % TimeSpan.TicksPerSecond)
            .ToString("D7", CultureInfo.InvariantCulture)
            .TrimEnd('0');

        return fraction.Length == 0 ? seconds : seconds + "." + fraction;
    }

    const string SecondFormat = "yyyy-MM-dd HH:mm:ss";

    static string Invariant(object? value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联的那一条判据六家同源，这里是第七份：纯整数形式要补小数点，指数式不许补。
    /// 本家这一格第一次有了可问的对象：整数字面量打进声明 REAL 的列，库自己会折（typeof 交回 real），
    /// 所以补不补那颗小数点在浮点那一臂不落进存储；它落进的是文本比较那一侧——同一条字面量
    /// 打进声明 TEXT 的列交回 '3'，而本家的定点列正是 TEXT。</summary>
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

    /// <summary>单引号成对转义，unicode: false。这一家取假不是"没决定"而是被正面量过：
    /// N'…' 在本族是语法错，而文本一律 UTF-8，"宽窄两型"这句话在这里没有对象。
    /// 另一颗只在本家成立的：双引号在这一族不是标识符引号而是"字符串字面量的第二形"，
    /// 那一格正是本家"标识符写错成静默字符串"那颗陷阱的成因——这里只发单引号，
    /// 而那颗陷阱的防线在 IdentifierPolicy 与 SqliteDialect 两处。</summary>
    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '@');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
