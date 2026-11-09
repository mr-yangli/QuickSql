using QuickSql.Sql;
using System.Collections.Frozen;

namespace QuickSql.Providers.Xugu;

/// <summary>C# 形状 → 虚谷模板表。键集一颗不能少：少一颗不是"少一条模板"，而是那一格在翻译器里抛
/// UntranslatableExpressionException，报错形状与"这一家没有这个函数"完全一样。
/// 出处按文档/产物/推断/实测四档标在各颗键旁，实测档（活库逐颗问出来的读数）压在前三档之上——
/// 产物会被实测推翻（日期加法那一批就是实例）。"血统"在一张模板表上站不住：每格各归一家，
/// 而归哪一家由这台库说了算。
/// 三处防坑：LENGTH 计字符，而"字符"由连接串的 CHARSET= 决定，同一段文本能交回两种数；
/// GETSECOND 与 EXTRACT(second FROM …) 都把小数秒带进整数槽，要整数秒只能用裸名 SECOND(x)，用错会静默漏行；
/// 聚合 AVG 在整数上截断与否未量。SupportsRegex 留 false：三档出处里一档都没有，共用层也没有消费者。</summary>
public sealed class XuguExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly Lock _gate = new();

    /// <summary>||，真库答过（'甲' || '乙' 得 "甲乙"）。这一家的 || 把 NULL 当空字符（'a' || NULL 得 'a' 而不是 NULL），
    /// 且函数形 CONCAT 与运算符同侧（与 MySQL 血统两形不同侧相反）；共用层没有 NULL 语义的槽位，今天不产出差异。
    /// 这一格没有消费方（拼接一律走 CONCAT 模板）。</summary>
    public string ConcatOperator => "||";

    /// <summary>反斜杠。真行实测：认 ESCAPE 子句（换别的转义符也认），不给 ESCAPE 子句时反斜杠是字面字符——
    /// 所以本仓库必须把 ESCAPE 子句显式写出来。判 LIKE 的语义要扫真行：常量比常量的写法会被收下而不理，那一读法作废。</summary>
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
        // 五家同形而没有一处产物出处的一颗；实测 CONCAT 在场，且遇 NULL 的语义与 || 同侧（都是空字符派）。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("CONCAT({0}, {1})"),

        // 【产物】裸 LENGTH。实测 LENGTH 计字符、OCTET_LENGTH 计字节；而"字符"怎么切由连接串 CHARSET= 决定——
        // 同一句 LENGTH('甲公司') 在 CHARSET=UTF8 的连接上是 3、不带的连接上是 5，provider 看不见那颗开关。
        [QuickFn.Key(QuickFn.Length, 1)] = new("LENGTH({0})"),
        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),

        // 三形真库逐颗全绿；不需要金仓那颗 BTRIM，也不需要 T-SQL 那种两层套。
        [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

        // 取 TRIM 形（对的是 IsNullOrWhiteSpace，不是产物里那种对 IsNullOrEmpty 的不带 TRIM 形——跟着产物的字形走会少一批行）。
        // 防坑：这一家有 EMPTY_STR_AS_NULL 会话参数，默认 FALSE（实测灌 '' 读回 LENGTH=0、IS NULL 数到 0、= '' 数到 1，
        // 本仓库这一形是对的）；参数一旦打开，纯空格串会被本形静默漏掉（TRIM 出的空串成 NULL），
        // 而那颗参数没有读法可验——换实例部署时要重验这一对（IS NULL 与 = '' 两数相加应为 1）。
        // 另实测 CHAR(10) 灌 'ab  ' 读回 LENGTH=2 且与无尾空格的 VARCHAR 列相等——去尾不补空格，不是 PAD SPACE。
        [QuickFn.Key(QuickFn.Blank, 1)] = new("({0} IS NULL OR TRIM({0}) = '')"),

        // SUBSTR 与 SQL 标准形 SUBSTRING…FROM…FOR 都认；仍发 SUBSTR，跟产物与 Oracle 血统那一形（换形收益为零）。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTR({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTR({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        // 元数含接收者：REPLACE 三槽在 /3、INSTR 两槽在 /2。抄错元数的后果是永远查不到而不是渲染错。
        [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // 四参 INSTR(串, 子串, 1, 1) 减一得 0 基位置；找不到交 0，减一后是 -1，与 C# 同值。
        // 中文子串的命中与否分岔过（utf8 连接命中、plain 不命中）——分岔的是 CHARSET，不是 INSTR。
        [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(INSTR({0}, {1}, 1, 1) - 1)"),

        // CAST(x AS date)：截到日、不报错、不改型——DATE 列上它是空操作（与多数派同一代价）。
        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS date)"),

        // 裸 NVARCHAR 不带宽度：裸形到 40000 字不截；而带宽度且窄于值长时整条查询交回空结果集——不抛异常、不进错误行，
        // 聚合 COUNT(*) 也交 0（钉死"是结果集空掉、不是行被滤掉"靠的就是聚合该有一行）。等宽与更宽的目标正常。
        // 所以这一格绝不补宽度："补个宽度更保险"在这一家是反的，等于把静默丢行接进链路。
        [QuickFn.Key(QuickFn.CastText, 1)] = new("CAST({0} AS NVARCHAR)"),

        // 五颗跟产物的 GET*（一颗一个函数，五家里的第五形）；实测值正确。
        // DateSecond 用裸名 SECOND(x)：GETSECOND 与 EXTRACT(second…) 都把小数秒带进整数槽（交 9.456 一类），
        // 谓词比"9.456 = 9"会静默漏行、不报错；SECOND 交 Int32。SECOND 打在 DATE 列上给 0 不报错（空操作，不是坏了）。
        [QuickFn.Key(QuickFn.DateYear, 1)] = new("GETYEAR({0})"),
        [QuickFn.Key(QuickFn.DateMonth, 1)] = new("GETMONTH({0})"),
        [QuickFn.Key(QuickFn.DateDay, 1)] = new("GETDAY({0})"),
        [QuickFn.Key(QuickFn.DateHour, 1)] = new("GETHOUR({0})"),
        [QuickFn.Key(QuickFn.DateMinute, 1)] = new("GETMINUTE({0})"),
        [QuickFn.Key(QuickFn.DateSecond, 1)] = new("SECOND({0})"),

        // SYSDATE，实测带毫秒、与 DATETIME(3) 正好对齐（"只到秒"那半句只对 Oracle 血统那一族成立，别跨族推广）。
        // 与另四家同一性质：它说的是库那台机器的此刻，放进逐行表达式整批同值。
        [QuickFn.Key(QuickFn.Now, 0)] = new("SYSDATE"),

        // 两颗未证（SYS_EXTRACT_UTC 与 SYSTIMESTAMP）被实测同时证实、交回的确实是 UTC。删掉这颗键会让所有
        // DateTime.UtcNow 链抛 Untranslatable，而那句"没有这个函数"是更不好核的假话。
        // 另：SYSTIMESTAMP 单发红在驱动侧 System.OutTypeException（带时区时刻型没有对应 CLR 型）——DateTimeOffset 抛的独立旁证。
        [QuickFn.Key(QuickFn.UtcNow, 0)] = new("(SYS_EXTRACT_UTC(SYSTIMESTAMP))"),


        // DATE_ADD(x, INTERVAL ({1}) UNIT) 被活库整批驳回（解析器在 INTERVAL 后不接受括号，且乘数一旦是
        // 表达式、参数或列名就红——{1} 在链式里就是一颗任意表达式）。全部换 Oracle 血统那两颗现成件：
        // 加减日时分秒走 NUMTODSINTERVAL（裸数字/参数/表达式/真列四档全绿），年与月只有 ADD_MONTHS 接得住
        // （年 = 乘 12，乘号在外面所以括号保得住表达式）。
        [QuickFn.Key(QuickFn.AddYears, 2)] = new("ADD_MONTHS({0}, ({1}) * 12)"),
        [QuickFn.Key(QuickFn.AddMonths, 2)] = new("ADD_MONTHS({0}, {1})"),
        [QuickFn.Key(QuickFn.AddDays, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'DAY'))"),
        [QuickFn.Key(QuickFn.AddHours, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'HOUR'))"),
        [QuickFn.Key(QuickFn.AddMinutes, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'MINUTE'))"),
        [QuickFn.Key(QuickFn.AddSeconds, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'SECOND'))"),

        // 裸形。包装形 CAST(… AS numeric) 也收但换值：ROUND("AMOUNT",2) 得 12.35、包装后得 12——
        // 裸 numeric 把标度折到 0，包装是静默改值，不是多一层保险（与金仓"不包就报错"同形而后果相反）。
        // 另：十进制函数结果经驱动读回是 String（POWER/SQRT 是 Double）。
        [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0})"),
        [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),
        [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),

        // CEIL：跟着 Oracle 倾向的那颗名字（T-SQL 族叫 CEILING）。
        [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEIL({0})"),
        [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR({0})"),
        [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
        [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

        // 原生 GREATEST/LEAST 在（四家里只有 T-SQL 要 CASE 展开）；NULL 传染与金仓/Oracle 同侧：GREATEST(NULL, 2) = NULL。
        [QuickFn.Key(QuickFn.Greatest, 2)] = new("GREATEST({0}, {1})"),
        [QuickFn.Key(QuickFn.Least, 2)] = new("LEAST({0}, {1})"),

        // 产物发 NVL，这里不跟：NVL 只证"有二参空值替换"，不证"没有 COALESCE"，而共用层这一颗是任意元的 2 元投影——
        // 跟多数派代价为零，跟产物是把一颗键的语义写窄。
        [QuickFn.Key(QuickFn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
        [QuickFn.Key(QuickFn.NullIf, 2)] = new("NULLIF({0}, {1})"),

        // 六颗聚合全取五家同形（这一家没有聚合函数的产物出处；WM_CONCAT 是字符串聚合，契约上没有那一格）。
        [QuickFn.Key(QuickFn.Count, 1)] = new("COUNT({0})"),
        [QuickFn.Key(QuickFn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
        [QuickFn.Key(QuickFn.Sum, 1)] = new("SUM({0})"),
        [QuickFn.Key(QuickFn.Average, 1)] = new("AVG({0})"),
        [QuickFn.Key(QuickFn.Min, 1)] = new("MIN({0})"),
        [QuickFn.Key(QuickFn.Max, 1)] = new("MAX({0})"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
