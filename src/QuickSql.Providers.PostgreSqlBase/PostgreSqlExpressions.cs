using System.Collections.Frozen;
using QuickSql.Sql;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>C# 形状 → PG 系模板表。三处刻意多包一层 CAST(… AS numeric)（ROUND/CEIL/FLOOR）：
/// 那些函数的单参数版在 PG 14 之前只吃 numeric，而金仓 V8 与 openGauss 内核都是 9.2 一线——按最老内核写。
/// 语义对拍查不出来的两处：str.length（T-SQL 的 LEN 不计尾部空格，LENGTH 计）；
/// agg.average（T-SQL 的 AVG(int) 截断，这里返回 numeric——同一个 DTO 槽位一家拿 3 一家拿 3.3333）。</summary>
public sealed class PostgreSqlExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    public string ConcatOperator => "||";

    /// <summary>这一家 LIKE 默认转义符本来就是反斜杠，把 ESCAPE '\' 写出来仍正确，
    /// 且让与 T-SQL 的 LIKE 文本逐字同形（黄金样本对读时少一处噪音）。
    /// 前提：standard_conforming_strings 为 on（PG 9.1 起默认），否则 '\' 会被当成转义串而报错。</summary>
    public char LikeEscapeCharacter => '\\';

    public bool SupportsRegex => false;

    public bool TryGetFunction(string key, out FunctionMapping mapping) =>
        _map.TryGetValue(key, out mapping!);

    public void Register(string key, FunctionMapping mapping)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, FunctionMapping>(_map, StringComparer.Ordinal);

            next[key] = mapping;
            _map = next.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    static FrozenDictionary<string, FunctionMapping> Build() =>
        new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
        {
            [QuickFn.Key(QuickFn.Concat, 2)] = new("CONCAT({0}, {1})"),
            [QuickFn.Key(QuickFn.Length, 1)] = new("LENGTH({0})"),
            [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
            [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),
            [QuickFn.Key(QuickFn.Trim, 1)] = new("BTRIM({0})"),
            [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
            [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),
            [QuickFn.Key(QuickFn.Blank, 1)] = new("({0} IS NULL OR BTRIM({0}) = '')"),

            // C# 的 Substring 是 0 基、这一家是 1 基，所以 +1；单参重载有原生写法（T-SQL 要伪造"够长的长度"）。
            [QuickFn.Key(QuickFn.Substring, 2)] = new(
                "SUBSTRING({0} FROM {1} + 1)",
                RequiresNonNegativeLength: true
            ),
            [QuickFn.Key(QuickFn.Substring, 3)] = new(
                "SUBSTRING({0} FROM {1} + 1 FOR {2})",
                RequiresNonNegativeLength: true
            ),
            // 元数含接收者：REPLACE 三槽在 /3、STRPOS 两槽在 /2。这两格原本照 T-SQL 旧键抄成 /2 与 /1，
            // 于是永远查不到（不是渲染错，是这两个函数从来没被翻译出来）；修复与黄金样本同版落地。
            [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

            // STRPOS 返回 1 基且找不到给 0（IndexOf 是 0 基给 -1）；参数顺序与 CHARINDEX 相反，实参是"串在前、子串在后"。
            [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(STRPOS({0}, {1}) - 1)"),
            [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS date)"),
            [QuickFn.Key(QuickFn.CastText, 1)] = new("CAST({0} AS text)"),

            [QuickFn.Key(QuickFn.DateYear, 1)] = new("EXTRACT(year FROM {0})"),
            [QuickFn.Key(QuickFn.DateMonth, 1)] = new("EXTRACT(month FROM {0})"),
            [QuickFn.Key(QuickFn.DateDay, 1)] = new("EXTRACT(day FROM {0})"),
            [QuickFn.Key(QuickFn.DateHour, 1)] = new("EXTRACT(hour FROM {0})"),
            [QuickFn.Key(QuickFn.DateMinute, 1)] = new("EXTRACT(minute FROM {0})"),
            [QuickFn.Key(QuickFn.DateSecond, 1)] = new("EXTRACT(second FROM {0})"),

            // 两个都是"语句/事务起点的时刻"而不是 CLR 进程的此刻：放进逐行表达式里整批同值。
            // UTC 那一档用 AT TIME ZONE 换掉时区，得到不带时区的 UTC 时刻。
            [QuickFn.Key(QuickFn.Now, 0)] = new("LOCALTIMESTAMP"),
            [QuickFn.Key(QuickFn.UtcNow, 0)] = new("(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')"),

            // 没有 DATEADD：时间加法是 timestamp + interval，interval 可以乘一个数，
            // 所以"加 n 个单位"是一句而不是一个函数。括号包着乘数：{1} 若是表达式，没有它就成了 a - b * interval。
            [QuickFn.Key(QuickFn.AddYears, 2)] = new("({0} + ({1}) * INTERVAL '1 year')"),
            [QuickFn.Key(QuickFn.AddMonths, 2)] = new("({0} + ({1}) * INTERVAL '1 month')"),
            [QuickFn.Key(QuickFn.AddDays, 2)] = new("({0} + ({1}) * INTERVAL '1 day')"),
            [QuickFn.Key(QuickFn.AddHours, 2)] = new("({0} + ({1}) * INTERVAL '1 hour')"),
            [QuickFn.Key(QuickFn.AddMinutes, 2)] = new("({0} + ({1}) * INTERVAL '1 minute')"),
            [QuickFn.Key(QuickFn.AddSeconds, 2)] = new("({0} + ({1}) * INTERVAL '1 second')"),

            [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND(CAST({0} AS numeric))"),
            [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND(CAST({0} AS numeric), {1})"),
            [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),
            [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEIL(CAST({0} AS numeric))"),
            [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR(CAST({0} AS numeric))"),
            [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
            [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

            // 原生 GREATEST/LEAST：T-SQL 那一族要用 CASE 展开，两处黄金样本的差就是这一格。
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
