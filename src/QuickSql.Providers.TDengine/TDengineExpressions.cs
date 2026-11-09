using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的函数表："缺"与"有"同等重要，只注册已逐条钉到厂商文档原文的格子，其余整批不注册。
/// length 注册的是 CHAR_LENGTH（量字符）而不是 LENGTH（量字节）——C# 的 string.Length 量 UTF-16 码元
///（BMP 内=码点），一个 BMP 汉字在 LENGTH 上是 3 字节，用它实现 .Length 是静默错那一类
///（VendorDoc·TDengine：函数页两颗的对照示例原话"多字节字符 CHAR_LENGTH 算 1、LENGTH 算字节数"）；
/// substring 是 1 起的字符索引且 pos=0/负 len 都回空串，参数序换算只对"索引一不偏"正确
///（VendorDoc·TDengine：pos 是 1-base 的，若 pos 为 0 返回空串）；CONCAT 任一实参 NULL 则整串 NULL，
/// 与 C# 把 null 当空串不同（本家没有钉过 IFNULL，不包——包了就是没读数的那颗函数）；
/// trim 三键各落 LTRIM/TRIM/RTRIM（VendorDoc·TDengine：三颗都在，TRIM 缺省双向裁空格）；
/// ToDate 落 CAST AS TIMESTAMP——本家没有 DATE 型，时刻那一族只有 TIMESTAMP 一颗
///（VendorDoc·TDengine：CAST 页，目标型不含 DATE）。不注册的各批（replace/indexOf/blank/日期分量族/
/// math 族/greatest/least）是文档没回读完，宁缺不错——在本家会红而另几族绿过，
/// "这一家更窄"是取向不是本事。整张表没有一行在活库上跑过。</summary>
public sealed class TDengineExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary>CONCAT——本家没有 || 运算符那一形（与 MySQL 血统同一取向：函数名就是"运算符"那一格）。
    /// 这一格在 src/ 里零消费者，这里填的不是行为而是台账。</summary>
    public string ConcatOperator => "CONCAT";

    /// <summary>反斜杠。共用层每次都显式发 ESCAPE 子句，所以这一位的真语义是"本家允许哪一个字符做转义符"
    /// 而不是"库里默认是哪一个"【文档没逐页回读】。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>false。本家找模式走 LIKE 那两颗通配（%、_），正则匹配不是 LIKE 的语义。</summary>
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
        // 字符那一颗，不是字节那一颗：C# 的 .Length 量 UTF-16 码元（BMP 内=码点），
        // 中文一个 BMP 汉字在 LENGTH 上是 3 字节，用它实现 .Length 是静默错那一类。
        [QuickFn.Key(QuickFn.Length, 1)] = new("CHAR_LENGTH({0})"),

        // 三个键各落各的独立名（LTRIM/TRIM/RTRIM 三颗都在，VendorDoc·TDengine）。
        [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

        // + 1：CLR 的 startIndex 从 0 起，SUBSTRING 从 1 起（这一条与另几族同一换算）。
        // RequiresNonNegativeLength：CLR 侧负长度的语义（截到末尾）与本家（len<1 回空串）不同，门先在外侧挡住。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTRING({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTRING({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        // 任一实参 NULL 则整串 NULL，与 C# 的 string.Concat 把 null 当空串不同。
        // 括号：共用层把结果拼进 WHERE 时不带自己的括号，优先级钉住。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("CONCAT({0}, {1})"),

        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),

        // 本家没有 DATE 型：日期那一格落 TIMESTAMP（CAST 页目标型不含 DATE）。
        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS TIMESTAMP)"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
