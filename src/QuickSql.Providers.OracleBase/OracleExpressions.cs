using QuickSql.Sql;
using System.Collections.Frozen;

namespace QuickSql.Providers.OracleBase;

/// <summary>C# 形状 → Oracle 血统模板表。键与各族同一套 Fn.Key(符号, 元数)，少一个键的形状也相同——
/// 那一格在翻译器里抛 UntranslatableExpressionException，与"这一家没有这个函数"是同一条文案。
/// 关键分岔：str.blank 发 (TRIM(x) IS NULL) 而不是 (x IS NULL OR TRIM(x) = '')——这一家 '' 就是 NULL，
/// 后一种写法在这里永远不成立（UNKNOWN 不是 TRUE），不报错、不红、只是少一批行；日期加法带 NUMTODSINTERVAL
/// 而不是裸加号——TIMESTAMP + NUMBER 是类型错误（DATE + NUMBER 才是天数），而契约上"这列是 DATE 还是 TIMESTAMP"
/// 发射层不知道；cast.date 用 TRUNC(x)——这一族 CAST(… AS DATE) 会把小数秒连同时区一起按会话规则处理，
/// TRUNC 说的就是"截到当天零点"；math.round 族不需要金仓那层 CAST(… AS numeric)——NUMBER 不分档，抄过来是多此一 cast。
/// str.length 计字符数、agg.average 在 NUMBER 上不截断，与 T-SQL 同符号不同解，对拍查不出来。
/// SupportsRegex 刻意留 false：这一族有 REGEXP_LIKE，但正则没有消费者，声明一个没有消费者的"支持"没有意义。</summary>
public sealed class OracleExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    public string ConcatOperator => "||";

    /// <summary>LIKE 默认转义符也是反斜杠，把 ESCAPE '\' 一起写出来仍然正确；
    /// 唯一的分岔是 MySQL 血统那颗 ESCAPE '~'。</summary>
    public char LikeEscapeCharacter => '\\';

    public bool SupportsRegex => false;

    public bool TryGetFunction(string key, out FunctionMapping mapping) => _map.TryGetValue(key, out mapping!);

    public void Register(string key, FunctionMapping mapping)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, FunctionMapping>(_map, StringComparer.Ordinal);

            next[key] = mapping;
            _map = next.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // CONCAT 在这一族只有二参数版本（与 T-SQL 同）：三参数串接在翻译器那一侧本来就是两两嵌套。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("CONCAT({0}, {1})"),
        [QuickFn.Key(QuickFn.Length, 1)] = new("LENGTH({0})"),
        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),
        [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

        // '' 就是 NULL，所以"空或空白"一句判 IS NULL 就够：加一段 = '' 的比较不是多余而是错
        // （UNKNOWN 不是 TRUE，那一行不会被选中）。
        [QuickFn.Key(QuickFn.Blank, 1)] = new("(TRIM({0}) IS NULL)"),

        // C# 的 Substring 是 0 基、这一族是 1 基，所以 +1；写法用 SUBSTR(x, s, len)。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTR({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTR({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // INSTR 返回 1 基且找不到给 0，与 IndexOf 差一格；实参顺序是"串在前、子串在后"。
        [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(INSTR({0}, {1}) - 1)"),
        [QuickFn.Key(QuickFn.ToDate, 1)] = new("TRUNC({0})"),
        [QuickFn.Key(QuickFn.CastText, 1)] = new("TO_CHAR({0})"),

        [QuickFn.Key(QuickFn.DateYear, 1)] = new("EXTRACT(year FROM {0})"),
        [QuickFn.Key(QuickFn.DateMonth, 1)] = new("EXTRACT(month FROM {0})"),
        [QuickFn.Key(QuickFn.DateDay, 1)] = new("EXTRACT(day FROM {0})"),
        [QuickFn.Key(QuickFn.DateHour, 1)] = new("EXTRACT(hour FROM {0})"),
        [QuickFn.Key(QuickFn.DateMinute, 1)] = new("EXTRACT(minute FROM {0})"),
        [QuickFn.Key(QuickFn.DateSecond, 1)] = new("EXTRACT(second FROM {0})"),

        // 两个都是"库那台机器上的此刻"而不是 CLR 进程的此刻：放进逐行表达式里整批同值。
        // SYSTIMESTAMP 与 CURRENT_TIMESTAMP 都带会话时区（与金仓那一族的 LOCALTIMESTAMP 差在这一格）。
        [QuickFn.Key(QuickFn.Now, 0)] = new("CURRENT_TIMESTAMP"),
        [QuickFn.Key(QuickFn.UtcNow, 0)] = new("(SYS_EXTRACT_UTC(SYSTIMESTAMP))"),

        // 年月走 ADD_MONTHS：它是这一族唯一把"月末对齐"写进规则的写法（1 月 31 日 + 1 月 = 2 月 28/29 日）。
        [QuickFn.Key(QuickFn.AddYears, 2)] = new("ADD_MONTHS({0}, 12 * ({1}))"),
        [QuickFn.Key(QuickFn.AddMonths, 2)] = new("ADD_MONTHS({0}, {1})"),
        [QuickFn.Key(QuickFn.AddDays, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'DAY'))"),
        [QuickFn.Key(QuickFn.AddHours, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'HOUR'))"),
        [QuickFn.Key(QuickFn.AddMinutes, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'MINUTE'))"),
        [QuickFn.Key(QuickFn.AddSeconds, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'SECOND'))"),

        [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0})"),
        [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),
        [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),
        [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEIL({0})"),
        [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR({0})"),
        [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
        [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

        // GREATEST/LEAST 在这一族是原生算子，与金仓那一格逐字同形。
        [QuickFn.Key(QuickFn.Greatest, 2)] = new("GREATEST({0}, {1})"),
        [QuickFn.Key(QuickFn.Least, 2)] = new("LEAST({0}, {1})"),
        [QuickFn.Key(QuickFn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
        [QuickFn.Key(QuickFn.NullIf, 2)] = new("NULLIF({0}, {1})"),

        [QuickFn.Key(QuickFn.Count, 1)] = new("COUNT({0})"),
        [QuickFn.Key(QuickFn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
        [QuickFn.Key(QuickFn.Sum, 1)] = new("SUM({0})"),
        [QuickFn.Key(QuickFn.Average, 1)] = new("AVG({0})"),
        [QuickFn.Key(QuickFn.Min, 1)] = new("MIN({0})"),
        [QuickFn.Key(QuickFn.Max, 1)] = new("MAX({0})"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
