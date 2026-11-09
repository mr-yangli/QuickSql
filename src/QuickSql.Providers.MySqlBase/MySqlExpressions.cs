using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.MySqlBase;

/// <summary>C# 形状 → MySQL 系模板表。键集与金仓那一族逐一对齐，两处刻意同形而不取"更地道的写法"
/// （EXTRACT(year FROM x)、SUBSTRING(x FROM p FOR n) 都认标准语法；LOCALTIMESTAMP 不带括号）；
/// 做不到同形的三处：TRIM（金仓是 BTRIM）、UTC_TIMESTAMP()（没有 AT TIME ZONE）、DATE_ADD 各写一颗单位（没有"间隔乘一个数"）。
/// 语义对拍查不出来的两处：str.length 落 CHAR_LENGTH（LENGTH 在 utf8mb4 下数的是字节，一个汉字算 3）；
/// CONCAT/GREATEST/LEAST 任一实参为 NULL 则整式 NULL（金仓跳过 NULL）——所以 CONCAT 模板逐颗实参包 IFNULL。
/// LikeEscapeCharacter 取 ~ 而不是 \：这一家默认反斜杠在字符串里就是转义符，ESCAPE '\' 是未闭合的串（五家唯一换转义符的一家）。</summary>
public sealed class MySqlExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary>写 || 是错的：这一家默认把它当逻辑 OR（PIPES_AS_CONCAT 不在默认 sql_mode），
    /// 两颗字符串会被折成数值再进 OR——连实参是什么都不看（8.4.8 实测 'a'||'b' ∶ 1||0 ∶ ''||'b' 交回 0∶1∶0）。
    /// 今天没有消费方（拼接一律走 CONCAT 模板），防的是哪天有人接它。</summary>
    public string ConcatOperator => "CONCAT";

    public char LikeEscapeCharacter => '~';

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
        // 本家 CONCAT 传染 NULL（实测），而共用层折成函数拼接的理由是"C# 把 null 当空串"，
        // 所以模板逐颗实参自己包 IFNULL——包装而不是换函数（ConcatOperator 仍是 CONCAT）。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("CONCAT(IFNULL({0}, ''), IFNULL({1}, ''))"),

        // LENGTH 在这一家是字节数（utf8mb4 下一个汉字 3），量纲要跟着契约走。
        [QuickFn.Key(QuickFn.Length, 1)] = new("CHAR_LENGTH({0})"),
        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),
        [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

        // 与金仓那一族同形（那一家的 TRIM 叫 BTRIM）。这里的 '' 是真空串：
        // Oracle 血统相反，那一家只能写 (TRIM(x) IS NULL)，因为它的 '' 就是 NULL。
        [QuickFn.Key(QuickFn.Blank, 1)] = new("({0} IS NULL OR TRIM({0}) = '')"),

        // C# 的 Substring 是 0 基、这一家是 1 基，所以 +1。取 FROM/FOR 标准支（也认逗号形）。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTRING({0} FROM {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTRING({0} FROM {1} + 1 FOR {2})", RequiresNonNegativeLength: true),

        // 元数含接收者：REPLACE 三槽在 /3、LOCATE 两槽在 /2——抄错元数的后果是永远查不到而不是渲染错。
        [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // LOCATE 跟 STRPOS 的实参顺序（子串在后），所以模板里 {0} 与 {1} 交换；
        // 返回值 1 基且找不到给 0，-1 换算与另四家一致。
        [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(LOCATE({1}, {0}) - 1)"),
        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS date)"),
        [QuickFn.Key(QuickFn.CastText, 1)] = new("CAST({0} AS char)"),

        [QuickFn.Key(QuickFn.DateYear, 1)] = new("EXTRACT(year FROM {0})"),
        [QuickFn.Key(QuickFn.DateMonth, 1)] = new("EXTRACT(month FROM {0})"),
        [QuickFn.Key(QuickFn.DateDay, 1)] = new("EXTRACT(day FROM {0})"),
        [QuickFn.Key(QuickFn.DateHour, 1)] = new("EXTRACT(hour FROM {0})"),
        [QuickFn.Key(QuickFn.DateMinute, 1)] = new("EXTRACT(minute FROM {0})"),
        [QuickFn.Key(QuickFn.DateSecond, 1)] = new("EXTRACT(second FROM {0})"),

        // 与金仓那一族同形（这一家的 LOCALTIMESTAMP 不带括号，与 NOW() 同义）。
        // 没有 AT TIME ZONE，用原生 UTC_TIMESTAMP()——给的是不带时区的 UTC 时刻。
        [QuickFn.Key(QuickFn.Now, 0)] = new("LOCALTIMESTAMP"),
        [QuickFn.Key(QuickFn.UtcNow, 0)] = new("UTC_TIMESTAMP()"),

        // 有原生 DATE_ADD，但不吃"间隔乘一个数"（那是 PG 系的形状），六档各写一颗单位。
        // 括号包着乘数：{1} 若是表达式，没有它就变成 DATE_ADD(x, INTERVAL a - b YEAR)。
        [QuickFn.Key(QuickFn.AddYears, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) YEAR)"),
        [QuickFn.Key(QuickFn.AddMonths, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) MONTH)"),
        [QuickFn.Key(QuickFn.AddDays, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) DAY)"),
        [QuickFn.Key(QuickFn.AddHours, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) HOUR)"),
        [QuickFn.Key(QuickFn.AddMinutes, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) MINUTE)"),
        [QuickFn.Key(QuickFn.AddSeconds, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) SECOND)"),

        // 不需要金仓那一层 CAST(… AS numeric)：那一层是"9.x 之前只吃 numeric"的版本原因，
        // 这一家的 ROUND/CEIL/FLOOR 从 Double 起就一直在——减一层包装是真实分岔，不是漏抄。
        [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0})"),
        [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),
        [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),
        [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEIL({0})"),
        [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR({0})"),
        [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
        [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

        // 原生 GREATEST/LEAST（T-SQL 要用 CASE 展开）。NULL 传染那一档与金仓相反（见类注释）。
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
