using System.Text;

namespace QuickSql.Metadata;

/// <summary>标识符策略。大小写与引号是国产化联调第一天就要定的事，
/// 因为它同时决定 DDL、查询、以及 doctor 的比对基准。
/// QuoteAlways = 每个标识符都加引号（SQL Server 取 true：方括号零成本，还能顺带避开
/// [User]、[Order] 这类保留字——它们恰恰是业务里最常见的表名）。
/// FoldToUpperCase = 发射之前先把名字折成大写：不带引号建的名字会被库折叠，与发出去的带引号名字
/// 指的不是同一张表（"查询报表不存在"那一类故障）。它的对称件"折向小写"没有取值——PG 系折小写，
/// 而那一系的现实做法是全部加引号、不折；Gbase8s 一族库确实折小写、但同样靠恒加引号对齐，
/// 所以这颗布尔表达不了那个方向（真状态要三值才表达得下）。折叠发生在 AppendQuoted 与 AppendPlain
/// 两支唯一的出口上，所以建表与查询不可能分岔。</summary>
public sealed record IdentifierPolicy(
    char OpenQuote,
    char CloseQuote,
    bool CaseSensitive,
    bool FoldToUpperCase,
    bool QuoteWhenMixedCase,
    bool QuoteAlways = false)
{
    public static IdentifierPolicy SqlServer { get; } = new('[', ']', CaseSensitive: false, FoldToUpperCase: false, QuoteWhenMixedCase: false, QuoteAlways: true);

    public static IdentifierPolicy PostgreSQL { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: false, QuoteWhenMixedCase: true);

    public static IdentifierPolicy OracleFamily { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: true, QuoteWhenMixedCase: true);

    /// <summary>MySQL 血统的预设：反引号、一律加引号、不折大小写。取 QuoteAlways 的理由与 SqlServer
    /// 同一格而更强：这一家的保留字表很长（ORDER、GROUP、RANK、ROWS、STATUS 都在线上），
    /// 而业务表名恰恰就叫前两个。不折大小写是因为它的敏感开关不在库里而在文件系统上
    /// （lower_case_table_names 建库时定死，Linux 默认区分、Windows 默认不区分），所以这里既不能赌
    /// "区分"也不能赌"不区分"，只能把契约里写的那个字形原样带引号发出去——与 PostgreSQL 同一个结论，
    /// 来路完全不同。另有一颗模式要一并记下：ANSI_QUOTES 会把双引号变成字符串的字面量引号，
    /// 而反引号在两种模式下都是标识符引号——这是这一家与 PG 系在同一个 SQL 标准档位上仍然分道的全部理由。</summary>
    public static IdentifierPolicy MySql { get; } = new('`', '`', CaseSensitive: false, FoldToUpperCase: false, QuoteWhenMixedCase: false, QuoteAlways: true);

    /// <summary>虚谷的预设：双引号、区分大小写、折向大写、混合大小写才加引号——与 OracleFamily
    /// 逐字同形，而四颗取值的来路不同一条，所以这一颗不复用而另立。
    /// 折向大写的依据是产物里那句按大写查目录的写法（不加引号的标识符进目录时已经被折成大写）；
    /// 文档侧把这颗折叠交给 COMPATIBLE_MODE（默认档折大写，取 POSTGRESQL 那一档折小写——
    /// 换档时整套折叠判断反号，这是"会话/兼容参数改变方言假设"的又一实例），这里取的是默认档；
    /// 读侧要绑同一颗折叠（与 Oracle 家的 reader 同一条纪律）。双引号取的是产物实际写出的形状
    /// （反引号合法但产物只用在建表外壳上）；区分大小写与 PostgreSQL 同一个结论。
    /// QuoteWhenMixedCase 跟着 OracleFamily 的形状抄（折向大写的库对大小写的处理必然成对出现），
    /// 是四颗里唯一没有独立出处的取值。
    /// 注意 QuoteAlways 为 false 并不等于"会走到 AppendPlain"：这颗为假而 CaseSensitive 为真，
    /// NeedsQuote 仍然对每个标识符都返回真，所以两支出口的分布与 OracleFamily 完全一致。</summary>
    public static IdentifierPolicy Xugu { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: true, QuoteWhenMixedCase: true);

    /// <summary>Informix 血统（GBase 8s）的预设：双引号、区分大小写、库折向小写而发射层不折
    /// （"不折"由恒加引号保证）。整条形状与 PostgreSQL 逐字相同而出处完全不同
    /// （那一颗的折法是厂商文档写明，这一颗是 SELECT 语法页上"未定界的标识符折成小写"那一句）。
    /// 取向与 PG 系同一条纪律：不带引号建的名字在库里存的是折叠后的字形，与我们发出去的带引号名字
    /// 指的不是同一张表——"忘加引号"在这一家与在 PG 系是同一类故障。
    /// CaseSensitive 为真让 NeedsQuote 对每个标识符都返回真，两支出口的分布是"只有前一支"；
    /// FoldToUpperCase 写成 false 不是"选了不折"，而是"这一家折的方向本来就是小写，
    /// 而这颗字段表达不了那个方向"（PG 血统靠加引号躲着同一个缺口）。</summary>
    public static IdentifierPolicy Gbase8s { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: false, QuoteWhenMixedCase: true);

    /// <summary>ClickHouse 的预设：双引号、区分大小写、不折名——四颗取值与 Gbase8s 逐字同形，
    /// 来路是官方 identifiers 页（标识符区分大小写，三副引号之任一，这里取 SQL 标准那一副与另九族同向）。
    /// 区分大小写取 true 的后果与另几族同一条：NeedsQuote 对每个标识符都返回真，所以发射层恒加引号、
    /// FoldToUpperCase 是一颗永远不被走到的取值（库里连折叠那条默认路都没有，真状态要等第一台引擎来问）。
    /// QuoteAlways 取默认 false 与 Gbase8s 同格：恒加引号那半由 CaseSensitive 达成，不需要再开一颗。</summary>
    public static IdentifierPolicy ClickHouse { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: false, QuoteWhenMixedCase: true);

    /// <summary>TDengine 的预设：反引号、一律加引号、不折大小写。反引号取自厂商的命名限制页
    ///（"为与 MySQL 兼容，引入反引号作为表/列名的转义符"），双引号在那边是字符串的字面量引号、
    /// 不当标识符引号用——这一格与 MySql 预设同一个形状而出处独立。不折大小写：不带引号的标识符
    /// 会被库内部归一（大小写不敏感），而反引号转义形"保留原样"——恒加引号把契约里写的字形原样
    /// 带过去，建表与查询读到的就是同一张表；CaseSensitive 取 false 与 MySQL 同格，
    /// QuoteAlways 为 true 才是恒加引号那半的真正出处。</summary>
    public static IdentifierPolicy TDengine { get; } = new('`', '`', CaseSensitive: false, FoldToUpperCase: false, QuoteWhenMixedCase: false, QuoteAlways: true);

    /// <summary>SQLite 的预设：双引号、一律加引号、不区分大小写。这是这一份表里第一颗
    /// "双引号、恒加引号、不敏感"三者同堂的组合，也是唯一一颗每颗取值的来路都是一台活库的策略。
    /// 双引号取 SQL 标准字形（方括号也认，本家不取）；不区分大小写是库里的事实
    /// （带引号的混合大小写能命中不带引号建的名字，连表身份本身也不区分大小写），
    /// 而它在本家第一次不承担行为：NeedsQuote 被 QuoteAlways 短路在前面，false 与 true 走同一支出口。
    /// 恒加引号的理由与另两家反着来——在这一家加引号是降低错误响度：不带引号而库里没有的标识符
    /// 会被解析器静默改判（投影位置落成常量字符串、WHERE 里恒假、算术里恒零、ORDER BY 里不改序），
    /// 四格都是"能绿着错"的一类；加引号把"名字写错了"这件事留在报错而不是留在空结果集，
    /// 不只为避开保留字。不折叠是实测事实：建表时写的字形就是目录里读回的字形。</summary>
    public static IdentifierPolicy Sqlite { get; } = new('"', '"', CaseSensitive: false, FoldToUpperCase: false, QuoteWhenMixedCase: false, QuoteAlways: true);

    /// <summary>加引号后的标识符。发射层不走这一条：它用 SqlWriter.AppendIdentifier 直接把
    /// 带引号的文本写进缓冲区，因为这条拼接路径每列都要过一次，而一次调用要 new 出两三个中间串。
    /// 两条路径共用 NeedsQuote 与 AppendQuoted，所以 DDL 与查询文本不可能写出两个不同的标识符——
    /// 折叠也在这一条上，"建出来的表与查的表必须是同一张"在 FoldToUpperCase 这一家同样成立。</summary>
    public string Quote(string identifier)
    {
        identifier = Normalize(identifier);

        if (!NeedsQuote(identifier))
            return identifier;

        var builder = new StringBuilder(identifier.Length + 2);
        AppendQuoted(builder, identifier);

        return builder.ToString();
    }

    /// <summary>要不要加引号——判据只有这一份。QuoteAlways 短路在最前，所以 SQL Server
    /// 永远不进朴素标识符那趟扫描。这一条不看 FoldToUpperCase：
    /// 折叠改的是字形而不是"是不是一个朴素标识符"，两边同判会让每一次取号都多跑一趟大写映射。</summary>
    public bool NeedsQuote(string identifier)
        => identifier.Length > 0 && (QuoteAlways || CaseSensitive || QuoteWhenMixedCase || !IsPlainIdentifier(identifier));

    /// <summary>把带引号的标识符直接落进 builder：除了缓冲区自身的容量与折叠过的新串
    /// （FoldToUpperCase 为真的那一家），一个字节都不多分配。引号内出现右引号
    /// 要翻倍，这是 SQL 标准做法，各家一致。</summary>
    internal void AppendQuoted(StringBuilder builder, string identifier)
    {
        identifier = Normalize(identifier);

        builder.Append(OpenQuote);

        var at = identifier.IndexOf(CloseQuote);

        if (at < 0)
        {
            builder.Append(identifier);
        }
        else
        {
            var start = 0;

            do
            {
                builder.Append(identifier, start, at - start + 1);
                builder.Append(CloseQuote);
                start = at + 1;
            }
            while ((at = identifier.IndexOf(CloseQuote, start)) >= 0);

            builder.Append(identifier, start, identifier.Length - start);
        }

        builder.Append(CloseQuote);
    }

    /// <summary>不加引号的那一支也要折叠：两支共用一个归一口，才不会出现"带引号的查得到、不带引号的查不到"。
    /// 今天没有任何一家走到这一支（各预设里 QuoteAlways 或 CaseSensitive 至少一颗为真），
    /// 它是 AppendQuoted 的对称件而不是现状。</summary>
    internal void AppendPlain(StringBuilder builder, string identifier) => builder.Append(Normalize(identifier));

    /// <summary>大小写折叠：Oracle 血统不带引号的名字会被库折成大写，而别的库折成小写或不折，
    /// 所以"折叠"这件事的方向也是方言知识。它只有 FoldToUpperCase 一个取值——折向小写的那一家的
    /// 现实形状是"全部加引号、不折"（PostgreSQL 就是那一支），今天没有需要折向小写的库种。</summary>
    string Normalize(string identifier) => FoldToUpperCase ? identifier.ToUpperInvariant() : identifier;

    static bool IsPlainIdentifier(string value)
    {
        if (!char.IsLetter(value[0]) && value[0] != '_')
            return false;

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '$')
                return false;
        }

        return true;
    }
}

public enum IndexKind { Unique, NonClustered, Clustered, FullText }

/// <summary>已解析的索引契约。ColumnNames 是库里的列名，不是 CLR 成员名——
/// 由解析链从 MemberNames 映射而来，所以它是 required：漏映射就是建出一个不存在的列。</summary>
public sealed record IndexContract(string Name, IndexKind Kind, IReadOnlyList<string> MemberNames, bool Descending = false)
{
    public required string[] ColumnNames { get; init; }
}
