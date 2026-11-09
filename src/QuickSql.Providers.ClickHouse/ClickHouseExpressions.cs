using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的函数表："缺"与"有"同等重要，只注册已逐条钉到厂商文档原文的格子，其余整批不注册。
/// length 注册的是 lengthUTF8（量码点）而不是 length（量字节）——C# 的 string.Length 量 UTF-16 码元
///（BMP 内=码点），一个 BMP 汉字在 length 上是 3 字节，用它实现 .Length 是静默错那一类；
/// upper/lower 只转 ASCII Latin（中文不转）；substring 是 1 起的字节索引，参数序换算只对"索引一不偏"正确，
/// 多字节里的换挡本来就不等价（未实测）；trim 族没有普通 trim，三键各落 trimBoth/trimLeft/trimRight；
/// concat 任一实参 NULL 则整串 NULL，与 C# 把 null 当空串不同。不注册的各批（math.*/date.*/agg.*/blank/
/// replace/indexOf/greatest/least）是文档没回读完，宁缺不错——在本家会红而另几族绿过，
/// "这一家更窄"是取向不是本事。整张表没有一行在活库上跑过。</summary>
public sealed class ClickHouseExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary>||【推断：string-functions 页 concat 那一行旁注过"||"是它的运算符形，没有单独的那一行可引】。
    /// 这一格在 src/ 里零消费者，这里填的不是行为而是台账。</summary>
    public string ConcatOperator => "||";

    /// <summary>反斜杠。共用层每次都显式发 ESCAPE 子句，所以这一位的真语义是"本家允许哪一个字符做转义符"
    /// 而不是"库里默认是哪一个"【文档没逐页回读】。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>false。本家找模式也走 SQL LIKE 那两颗通配（%、_），正则匹配是另一族函数
    ///（match()/extract 那一档），不是 LIKE 的语义。</summary>
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

    /// <summary>键的取值集见类注释。现场要补一颗函数时，注册进去的那一条也要带着同一条出处纪律——
    /// Register 在本家不是"随便填一支模板"的口子。</summary>
    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // 码点那一颗，不是 length 字节那一颗：C# 的 .Length 量 UTF-16 码元（BMP 内=码点），
        // 中文一个 BMP 汉字在 length 上是 3 字节，用它实现 .Length 是静默错那一类。
        [QuickFn.Key(QuickFn.Length, 1)] = new("lengthUTF8({0})"),

        // 三个键各落各的独立名，本家没有"普通 trim"那一颗。
        [QuickFn.Key(QuickFn.Trim, 1)] = new("trimBoth({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("trimLeft({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("trimRight({0})"),

        // + 1：CLR 的 startIndex 从 0 起，substring 从 1 起（这一条与另几族同一换算）。
        // RequiresNonNegativeLength：CLR 侧负长度的语义（截到末尾）与本家（报错）不同，门先在外侧挡住。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("substring({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("substring({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        // 任一实参 NULL 则整串 NULL，与 C# 的 string.Concat 把 null 当空串不同。
        // 括号：共用层把结果拼进 WHERE 时不带自己的括号，优先级钉住。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("concat({0}, {1})"),

        [QuickFn.Key(QuickFn.Upper, 1)] = new("upper({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("lower({0})"),

        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS Date)"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
