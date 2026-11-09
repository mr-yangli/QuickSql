using System.Collections.Frozen;
using QuickSql.Sql;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的函数表，四十五键全登记且每一行都有活库读数。有一条形状纪律比任何单键都重要：
/// 所有产出时刻的模板（Now/UtcNow、六颗 Add*、ToDate）都套同一层 Stored 归一——这一家的时刻是文本、
/// 等号比的是字面串，不套的话引擎交回定长三位小数而库里是驱动写进去的"去尾零"整秒形，两形相遇恒不等；
/// 归一救不了的只剩 %f 只到毫秒那一格（列是 100 纳秒而算式只到毫秒）。其他要点：月与年的算术不进位到
/// "当月最后一天"而是溢出到下一个月（本家与另六家唯一一处算术结果本身不同——能修的写法存在但代价更大，
/// 修正本身会把 100 纳秒刻度降级成毫秒，所以取向是发短的那一形）；年与月发 %+d（CLR 只用整数部分）、
/// 日与时分秒发 %+f，判据是 CLR 那一侧截不截断；decimal 落 TEXT 使比较与排序退回词法（MAX 与
/// Greatest/Least 在定点列上比的是词法，且文本在这一家的类型序里恒大于数）；参数是具名形 @p0，
/// 所以 Blank 那颗同一参数出现两次的键登得进来。ToDate 交回的是零点那一形而不是裸日期
/// （与另六家同语义："截到日而仍是时刻"）；日期字段族打在带偏移的文本上读的是 UTC 那份字段；
/// 引擎在进程内，Now 与 UtcNow 不是"库那台机器的钟"，比的是本机时间截到毫秒。</summary>
public sealed class SqliteExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary>|| 算子。它在 src/ 里零消费者——共用层发出去的是 Fn.Concat/2 那颗模板
    /// （Build 里带着 COALESCE 折叠），所以这一位填的是台账：裸算子自己任一实参为 NULL
    /// 则整式 NULL，与另六家同形；这一档不是本仓库的拼接产出形状。</summary>
    public string ConcatOperator => "||";

    /// <summary>反斜杠。本家没有"默认转义符"这一颗东西（裸下划线当通配用、没有任何字符被预留），
    /// 共用层每次都显式发 ESCAPE 子句，于是这一位的真语义与另六家一样是"允许哪一个字符做转义符"。
    /// 这一格在没有 ESCAPE 子句的手写文本上是另一回事，而本仓库不发那种文本。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>false，而且这一位在本家是问出来的而不是推出来的：REGEXP 这一族默认根本没有
    /// 那颗函数（操作符与函数两形都报 no such function），它要宿主进程自己注册
    /// （Microsoft.Data.Sqlite 没有注册）。场上有的是 GLOB 与 LIKE，而 GLOB 是文件通配那一族、
    /// 不是正则。把 SupportsRegex 报真是让共用层发一颗这一家没有的算子。</summary>
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

    /// <summary>键的取值集见类注释第一段（四十五颗全在）。本家不需要 DialectBase.EmitFunction
    /// 那道位置形核对（参数前缀是具名的 @，那颗核对在本家恒不触发），而模板里出现两次 {0} 的
    /// 那一颗（str.blank）正是靠这一格才登得进来。现场补一颗函数时，产出时刻的那一类要过 Stored。</summary>
    static FrozenDictionary<string, FunctionMapping> Build() =>
        new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
        {
            // 括起来是必需的：`1 || 2 = 12` 交回 0（算子被 `=` 抢了先），`(1 || 2) = '12'` 才交回 1。
            //
            // 两颗 COALESCE 不是保险，是字符串拼接契约在本家的落地形：C# 的 a + b（字符串）把
            // null 当空串，而本家的 || 任一实参为 NULL 则整式 NULL——不折就是"库里原本为 NULL 的
            // 那一行改完还是 NULL"（静默丢值，只是不丢行）。折在本族模板里而不是共用层，因为七家
            // 在这一格分三档：有的家 CONCAT 模板自带跳 NULL（共用层再补就是重复），有的家按文档传播
            // NULL 而没人量过（不能替它们改），本家量到了"不折就错"，所以折在这里。
            // 折叠用的那颗问过了：二参与三参的 COALESCE 都交回右支，取 COALESCE 是因为本家另有
            // Fn.Coalesce 那颗模板，两处同一拼法。
            [QuickFn.Key(QuickFn.Concat, 2)] = new("(COALESCE({0}, '') || COALESCE({1}, ''))"),
            [QuickFn.Key(QuickFn.Concat, 2)] = new("(COALESCE({0}, '') || COALESCE({1}, ''))"),

            // 颗数而不是字节：'中文' → 2、'😀' → 1（代理对算一颗）。打在文本化的定点上无损。
            [QuickFn.Key(QuickFn.Length, 1)] = new("LENGTH({0})"),

            // 两颗只折 ASCII：'éa' → 'éA'（é 原样），'İ' 一颗都不动。与 COLLATE NOCASE 同一条边界
            // （'ä' 与 'Ä' 在那一档下也不等），所以 UPPER 与 LOWER 只到 ASCII 是两格而同一因。
            // CLR 的 ToUpper 走当前区域性——差在哪一颗字符上要现场问，本表仍登这一形（六家同形）。
            [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
            [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),

            // 只去空格：TRIM(char(9) || 'x' || char(9)) = 'x' 交回 0，而两臂带空格的交回 1。
            // 两颗带字符集形也在场，本表不用它——CLR 的 Trim() 语义是"全部空白"，那一档在这里发不出来。
            [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
            [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
            [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

            // 类注释第五格：这一形在本家登得进来，因为 @p0 在同一句文本里出现两次是驱动的合法用法。
            // "空格串算不算空白"这一问的答案与 Fn.Trim 同边，一颗 '\t' 在这一形下不成立——
            // 那是六家共有的语义差而不是本家新添的。
            [QuickFn.Key(QuickFn.Blank, 1)] = new("({0} IS NULL OR TRIM({0}) = '')"),

            // CLR 的 startIndex 从 0 起而 SUBSTR 从 1 起（七家同一条换算）。
            // 越界与零长那一档本家比另六家宽（不报错而是给出宽容结果），所以 RequiresNonNegativeLength
            // 那道闸不是防这一家报错，是防我们把 CLR 会抛的那一档静默发下去。
            [QuickFn.Key(QuickFn.Substring, 2)] = new(
                "SUBSTR({0}, {1} + 1)",
                RequiresNonNegativeLength: true
            ),
            [QuickFn.Key(QuickFn.Substring, 3)] = new(
                "SUBSTR({0}, {1} + 1, {2})",
                RequiresNonNegativeLength: true
            ),

            // 三处空串与重叠那一档也一起量了：REPLACE('aaa','aa','z') 交回 'za'（不回头扫），
            // 这一条与 CLR String.Replace 同。
            [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

            // INSTR 同序（串在前、子串在后）。1 基换算成 CLR 的 0 基，找不到交回 -1、NULL 传播 NULL。
            [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(INSTR({0}, {1}) - 1)"),

            // 零点那一形，两臂的取舍写在类注释里。里外两层都是必需的：内层 DATE() 截到日，
            // 外层把那一形展开成时刻文本再归一，否则会差一段 ' 00:00:00'。
            [QuickFn.Key(QuickFn.ToDate, 1)] = new(
                Stored("STRFTIME(" + Timestamp + ", DATE({0}))")
            ),

            // 本家的目标型 TEXT 是不带宽度的合法型名，所以"缺的不是函数而是一个宽度数"在这里根本不成立。
            // 打在 REAL 上交回引擎的浮点转文本。
            [QuickFn.Key(QuickFn.CastText, 1)] = new("CAST({0} AS TEXT)"),

            // 六颗都过 STRFTIME 的掩码再折整数。三处要说清：%S 只到整秒；掩码打在读不懂的文本上
            // 交回 NULL 而不是报错——一句 WHERE Year(x)=2026 在一列混着脏数据的文本上会静默漏行；
            // 带偏移的文本读的是 UTC 那份字段。打在裸整数上是 NULL——引擎只认那几种时刻文本，
            // 而本家 TimeSpan/TimeOnly 落的是 Ticks，那两列上这六颗都不该用（共用层按 CLR 型就不会走到）。
            [QuickFn.Key(QuickFn.DateYear, 1)] = new("CAST(STRFTIME('%Y', {0}) AS INTEGER)"),
            [QuickFn.Key(QuickFn.DateMonth, 1)] = new("CAST(STRFTIME('%m', {0}) AS INTEGER)"),
            [QuickFn.Key(QuickFn.DateDay, 1)] = new("CAST(STRFTIME('%d', {0}) AS INTEGER)"),
            [QuickFn.Key(QuickFn.DateHour, 1)] = new("CAST(STRFTIME('%H', {0}) AS INTEGER)"),
            [QuickFn.Key(QuickFn.DateMinute, 1)] = new("CAST(STRFTIME('%M', {0}) AS INTEGER)"),
            [QuickFn.Key(QuickFn.DateSecond, 1)] = new("CAST(STRFTIME('%S', {0}) AS INTEGER)"),

            // 'now' 是 UTC，所以 Now 要带 'localtime'：驱动写入 DateTime 时丢掉 Kind
            // （Unspecified/Utc/Local 落成同一串裸时刻），而 Local 那一档写的就是本地墙上时间——
            // 与"库里最近的那一行"可比的只有本地档。UtcNow 与 Kind=Utc 写进去的裸串也对得上，
            // 因为写的时候已经是那一格的墙上时间。
            [QuickFn.Key(QuickFn.Now, 0)] = new(
                Stored("STRFTIME(" + Timestamp + ", 'now', 'localtime')")
            ),
            [QuickFn.Key(QuickFn.UtcNow, 0)] = new(Stored("STRFTIME(" + Timestamp + ", 'now')")),

            // 六颗 Add*：类注释第三格（月/年溢出）与第二格（%+d 与 %+f 的分工）都在这一组上。
            // 年与月走 %+d（CLR 只用整数部分）；日与时分秒走 %+f。
            // 修饰符的单复数两形都收，本表统一发复数（+1.5 days 才是"一天半"，单数形没有量过，不赌）。
            // 月末那一档（月/年溢出到下一个月）是本家与另六家唯一一处算术结果本身不同的格。
            [QuickFn.Key(QuickFn.AddYears, 2)] = new(
                Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+d years', {1}))")
            ),
            [QuickFn.Key(QuickFn.AddMonths, 2)] = new(
                Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+d months', {1}))")
            ),
            [QuickFn.Key(QuickFn.AddDays, 2)] = new(
                Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f days', {1}))")
            ),
            [QuickFn.Key(QuickFn.AddHours, 2)] = new(
                Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f hours', {1}))")
            ),
            [QuickFn.Key(QuickFn.AddMinutes, 2)] = new(
                Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f minutes', {1}))")
            ),
            [QuickFn.Key(QuickFn.AddSeconds, 2)] = new(
                Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f seconds', {1}))")
            ),

            // 一元那一臂补字面量 0 而不是发 ROUND(x)：ROUND 在这一家确实认一元，而它到 0 位
            // 而不是 CLR 那一档是巧合，写成两臂一致更可读。三颗要记的：
            // 半数是远离零进的（2.5 → 3、-2.5 → -3）而 CLR Math.Round 是银行家——六家同然；
            // 负标度被忽略（CLR 压根不接受负标度会抛，所以这一格不会错值而是"少一步"）；
            // 定点那一列上它是折了文本再算。
            [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0}, 0)"),
            [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),

            // CEIL 与 CEILING 都在场且同值，本表跟 MySQL 血统那颗的短形 CEIL；交回的是 REAL，
            // 所以 CLR 的 decimal 在这一家过这两颗会退成浮点——那是算式的返回值而不是存储。
            [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),
            [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEIL({0})"),
            [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR({0})"),

            // 实参序与 CLR Math.Pow(x, y) 同。POW 与 POWER 两形都在，跟另六家那一颗长的；
            // SQRT(9) 交回的是 Double，所以本家这两颗出来的数落不进定点那一列而不报错。
            // 旁边两颗在场而本表没有对应的键，记一句免得下一版重新发现：EXP(1) 与 LOG(1000) 也在，
            // 而 CLR 的 Math.Log 一档是自然对数——真要加 math.log 那一颗，先答"这一家的 LOG 是不是
            // 那一档"（LOG(1000) = 3，也就是说不是）。
            [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
            [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

            // 本家没有 GREATEST/LEAST，场上有的就是变参的 MAX()/MIN()。
            // 两格代价都写在类注释第四格：定点那一列上它比的是词法，且文本在这一家的类型序里
            // 恒大于数（MAX("m", 100) 交回文本）。NULL 那一档与 Math.Max 不同：MIN(NULL, 1) 交回 NULL
            // 而不是跳过——本表不补 COALESCE，因为补了就把"参与比较的是 NULL"这件事藏起来。
            [QuickFn.Key(QuickFn.Greatest, 2)] = new("MAX({0}, {1})"),
            [QuickFn.Key(QuickFn.Least, 2)] = new("MIN({0}, {1})"),

            // 含 IIF 那一形（本表不用）。
            [QuickFn.Key(QuickFn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
            [QuickFn.Key(QuickFn.NullIf, 2)] = new("NULLIF({0}, {1})"),

            // 六颗聚合。两颗本家特有的：COUNT(DISTINCT x) 与 COUNT(x) 同一档，GROUP_CONCAT 要带
            // 分隔符才可控（本表不发——CLR 侧没有对应符号）；SUM 在全 NULL 时交回 NULL 而 TOTAL
            // 交回 0.0，本表跟 SUM（六家同）——CLR 侧的 Sum() 对空集交的正是 null。
            // Min/Max 在定点列上是词法，与 Greatest/Least 同一格同一代价。
            [QuickFn.Key(QuickFn.Count, 1)] = new("COUNT({0})"),
            [QuickFn.Key(QuickFn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
            [QuickFn.Key(QuickFn.Sum, 1)] = new("SUM({0})"),
            [QuickFn.Key(QuickFn.Average, 1)] = new("AVG({0})"),
            [QuickFn.Key(QuickFn.Min, 1)] = new("MIN({0})"),
            [QuickFn.Key(QuickFn.Max, 1)] = new("MAX({0})"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>STRFTIME 那颗掩码串（带单引号，所以拼进模板时不用再补）。本表九处用到它，
    /// 一处写死是为了不让九行各自漂移成两种精度。</summary>
    const string Timestamp = "'%Y-%m-%d %H:%M:%f'";

    /// <summary>把引擎交回的定长毫秒形归一回驱动自己写进库里的那一形：先削尾零，再削那颗因此悬空的点。
    /// 用两颗 RTRIM 而不是 TrimEnd 那一族：RTRIM(x,'0') 削到尾零为止就停在 '.' 上，
    /// 那颗点正是"秒位上的零"与"小数位上的零"的分界，不会把 10:20:00 削成 10:20:0。
    /// 两处它救不回来：%f 只到毫秒，以及带偏移的输入会被折成 UTC 裸时刻——
    /// 后者是本家的 DateTimeOffset 列不能过日期算术的一条硬约束。</summary>
    static string Stored(string expression) => "RTRIM(RTRIM(" + expression + ", '0'), '.')";
}
