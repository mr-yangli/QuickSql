using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的函数表。本家特有的硬判据：位置形占位符没有名字可对号，
/// 模板里 {i} 必须按 0,1,2… 各出现一次（DialectBase.EmitFunction 的核对，本家是第一个走得到的家）。
/// 两类模板因此被挡下：一颗实参用两次的（str.blank 的 TRIM 形——文本两颗、表里一颗，值会绑到隔壁列，
/// 所以不注册，IsNullOrWhiteSpace 交 Untranslatable）与实参顺序与文本顺序相反的（T-SQL 式反序形写不出）。
/// 不注册的其余各颗也各有理由：str.indexOf（INSTR 不是 Informix 内建，MATCHES 给不出位置）、
/// cast.text（CAST 要带型名的目标型而"无宽度的文本型"不存在，SPI 没有宽度格）、
/// date.utcNow（DBINFO('utc_current') 交整数秒，两级拼装无库见证不发）、
/// math.ceiling/floor/power/greatest/least 与 str.replace（手册未逐页回读，"像 Oracle"不是出处；
/// Replace 在另五家全有，这条链在本家会红）。整张表没有任何一行在活库跑过。</summary>
public sealed class Gbase8sExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary>||。与 CONCAT 模板是同一条事实的两个出口，而它在 src 里零消费者（拼接一律走 CONCAT 模板）。</summary>
    public string ConcatOperator => "||";

    /// <summary>反斜杠。共用层每次都显式发 ESCAPE 子句，所以这一位的真语义是"本家允许哪个字符做转义符"
    /// 而不是"库里默认是哪个"。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>false：这一家找模式用 MATCHES，它的通配形状不是 POSIX 正则——报真会让共用层发一颗这一家不认的正则算子。</summary>
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

    /// <summary>键的取值集见类注释。每一行模板都过一遍"0,1,2… 各一次"的自查（本家特有）；
    /// 现场补函数时注册进去的那条也带着同一条约束。</summary>
    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // 括号必需：|| 的优先级与比较算子之间要靠括号钉住，共用层把结果再拼进 WHERE 时不带自己的括号。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("({0} || {1})"),
        [QuickFn.Key(QuickFn.Length, 1)] = new("LENGTH({0})"),
        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),
        [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

        // + 1：CLR 的 startIndex 从 0 起，SUBSTR 从 1 起（六家同一条换算）。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTR({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTR({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS DATE)"),

        [QuickFn.Key(QuickFn.DateYear, 1)] = new("YEAR({0})"),
        [QuickFn.Key(QuickFn.DateMonth, 1)] = new("MONTH({0})"),
        [QuickFn.Key(QuickFn.DateDay, 1)] = new("DAY({0})"),

        // HOURS/MINUTES/SECONDS 要求带相应字段的 DATETIME：本家 DDL 发的正是 YEAR TO FRACTION(3)，
        // 所以"能取到秒"是齐的；库里别人建的 DATE 列上这三颗会红——契约与库不一致，不是翻译错。
        [QuickFn.Key(QuickFn.DateHour, 1)] = new("HOURS({0})"),
        [QuickFn.Key(QuickFn.DateMinute, 1)] = new("MINUTES({0})"),
        [QuickFn.Key(QuickFn.DateSecond, 1)] = new("SECONDS({0})"),

        // 句首括号是必需的：CURRENT 不带括号，而共用层会把它拼进算术里。
        [QuickFn.Key(QuickFn.Now, 0)] = new("(CURRENT YEAR TO SECOND)"),

        // UNITS 是本家的日期算术算子（四派里唯一"算子在前、单位在词尾"的）。
        [QuickFn.Key(QuickFn.AddYears, 2)] = new("ADD_MONTHS({0}, ({1}) * 12)"),
        [QuickFn.Key(QuickFn.AddMonths, 2)] = new("ADD_MONTHS({0}, {1})"),
        [QuickFn.Key(QuickFn.AddDays, 2)] = new("({0} + {1} UNITS DAY)"),
        [QuickFn.Key(QuickFn.AddHours, 2)] = new("({0} + {1} UNITS HOUR)"),
        [QuickFn.Key(QuickFn.AddMinutes, 2)] = new("({0} + {1} UNITS MINUTE)"),
        [QuickFn.Key(QuickFn.AddSeconds, 2)] = new("({0} + {1} UNITS SECOND)"),

        // 这一家的 ROUND 是双参的：一元那一臂补字面量 0，不发 ROUND(x)——少一个实参的模板会静默少一截。
        [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0}, 0)"),
        [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),
        [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),
        [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

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
