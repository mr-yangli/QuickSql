namespace QuickSql.Sql;

/// <summary>CLR 形状到符号再到 provider 模板的三段式。
/// 翻译器只认识这些符号，绝不在表达式树里硬写方言函数（DATEADD、BTRIM 之类）：
/// 那等于把方言知识漏进共用层，金仓与达梦的复用价值就没了。</summary>
public static class QuickFn
{
    public const string Concat = "str.concat";
    public const string Length = "str.length";
    public const string Upper = "str.upper";
    public const string Lower = "str.lower";
    public const string Trim = "str.trim";
    public const string TrimStart = "str.trimStart";
    public const string TrimEnd = "str.trimEnd";
    public const string Blank = "str.blank";
    public const string Substring = "str.substring";
    public const string Replace = "str.replace";
    public const string IndexOf = "str.indexOf";
    public const string ToDate = "cast.date";
    public const string CastText = "cast.text";

    public const string DateYear = "date.year";
    public const string DateMonth = "date.month";
    public const string DateDay = "date.day";
    public const string DateHour = "date.hour";
    public const string DateMinute = "date.minute";
    public const string DateSecond = "date.second";
    public const string Now = "date.now";
    public const string UtcNow = "date.utcNow";
    public const string AddYears = "date.addYears";
    public const string AddMonths = "date.addMonths";
    public const string AddDays = "date.addDays";
    public const string AddHours = "date.addHours";
    public const string AddMinutes = "date.addMinutes";
    public const string AddSeconds = "date.addSeconds";

    public const string Round = "math.round";
    public const string Abs = "math.abs";
    public const string Ceiling = "math.ceiling";
    public const string Floor = "math.floor";
    public const string Power = "math.power";
    public const string Sqrt = "math.sqrt";
    public const string Greatest = "math.greatest";
    public const string Least = "math.least";

    public const string Coalesce = "coalesce";
    public const string NullIf = "nullif";

    public const string Count = "agg.count";
    public const string CountDistinct = "agg.countDistinct";
    public const string Sum = "agg.sum";
    public const string Average = "agg.average";
    public const string Min = "agg.min";
    public const string Max = "agg.max";

    /// <summary>注册表的键 = 符号 + 元数。同一个符号在不同实参个数下模板不同（一参与两参两式），
    /// 不带元数的话 string.Format 会静默丢掉多余实参，生成一条少了一截的 SQL。</summary>
    public static string Key(string symbol, int arity) => symbol + "/" + arity.ToString();

    /// <summary>把模板里的 {i} 填成已渲染的实参。约定实参已经带好了自己的引号与转义。</summary>
    public static string Apply(string template, IReadOnlyList<string> arguments) => string.Format(template, [.. arguments]);
}
