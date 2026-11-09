using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§13.4 的硬要求：集成层必须<b>记录并断言</b>当前实例的排序规则，任何字符集结论都要挂着这两个值说。
///
/// <b>为什么这条不是"环境细节"</b>：§10.4 的一半结论是同一条 SQL 在两类库上<b>含义相反</b> ——
/// <c>SQL_Latin1_General_CP1_CI_AS</c>（代码页 1252）下 <c>CAST(N'中文' AS varchar)</c> 把汉字<b>静默换成 '?'</b>，
/// 而 <c>Chinese_PRC_CI_AS</c>（936/GBK）下一个字节都不丢、只是每个汉字占 2 字节。
/// 在 v0.8 之前这句话只是文档里的笔记：全仓库 <c>Collation</c>/<c>SERVERPROPERTY</c> 零命中，
/// 于是"在 GBK 上成立"和"在 cp1252 上成立"这两种说法谁也没有被断言过。
///
/// <b>这里的写法</b>：不写死"期望哪个排序规则"（那会把用例钉死在一个实例上），
/// 而是<b>读回代码页、由代码页算出预测、再拿实测比对</b>。所以同一份代码在两类实例上都是有效断言，
/// 而它红了首先说明"这个实例的代码页与我们以为的不是那个"，正是本条要暴露的那件事。
///
/// <b>两类实例都真跑过</b>（2026-09-21）：192.168.0.102 的 SQL Server 2016 SP2 企业版是
/// <c>Chinese_PRC_CI_AS</c>、代码页 936 → 预测 2 字节/汉字，实测 <c>DATALENGTH</c> 8 而 <c>LEN</c> 4、且原样存下；
/// 本机 LocalDB 是 SQL Server 2019 Express 的 <c>SQL_Latin1_General_CP1_CI_AS</c>、代码页 1252 → 4 字节、整串塌成 <c>????</c>。
/// 表里 <c>65001</c> 那一行<b>这两个库都覆盖不到</b>（它们的默认排序规则都不是 UTF-8），它是从 §10.4 的列级实测搬来的，
/// 在那儿它是"已实测"、在这儿它是"未跑过"，两处不同级，所以写在这里。</summary>
public class CollationTests
{
    const string Han = "中文测试";

    const string HanLiteral = "N'" + Han + "'";

    /// <summary>代码页 → <c>varchar</c> 里一个汉字占几字节，也就是本文件全部的预测能力。
    /// 2 是 GBK 系四个 Windows 双字节页（936 GBK / 950 Big5 / 932 Shift-JIS / 949 EUC-KR，这四个页都收得下"中文测试"这四个字），
    /// 3 是 §10.4 实测过的 UTF-8 页（<c>…_SC_UTF8</c>，那边是列级 <c>COLLATE</c>，本文件读的是库级默认，同一张代码页两件事）；
    /// 表里没有的一律按"装不下、逐字塌成一个 <c>?</c>"预测（1252/1250/874… 对汉字都是这种）。
    /// <b>真遇到没列出的双字节页时红的是这里</b>，补一个数字就是它的全部代价 —— 而不是红在某个下游用例里让人猜。</summary>
    static int BytesPerHanCharacter(int codePage) => codePage switch
    {
        936 or 950 or 932 or 949 => 2,
        65001 => 3,
        _ => 1,
    };

    record Facts(
        string ServerCollation,
        string DatabaseCollation,
        int CodePage,
        int AnsiBytes,
        int AnsiChars,
        bool AnsiLossless,
        int UnicodeBytes)
    {
        public int PredictedBytes => BytesPerHanCharacter(CodePage);

        /// <summary>每条断言的失败消息都带这句 —— "结论要挂着排序规则说"在代码里的落点就是这个属性。</summary>
        public string Context
            => $"实例 {ServerCollation} / 当前库 {DatabaseCollation}，ANSI 代码页 {CodePage}（预测每个汉字 {PredictedBytes} 字节）";
    }

    static Facts Probe()
    {
        Database.Ready();

        var row = Database.Rows(
            "DECLARE @server nvarchar(128) = CONVERT(nvarchar(128), SERVERPROPERTY('Collation')); " +
            "DECLARE @database nvarchar(128) = CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')); " +
            "SELECT ISNULL(@server, N'<null>') AS server_collation, " +
            "       ISNULL(@database, N'<null>') AS database_collation, " +
            "       ISNULL(CAST(COLLATIONPROPERTY(@database, 'CodePage') AS int), -1) AS database_code_page, " +
            $"       DATALENGTH(CAST({HanLiteral} AS varchar(50))) AS ansi_bytes, " +
            $"       LEN(CAST({HanLiteral} AS varchar(50))) AS ansi_chars, " +
            $"       CASE WHEN CONVERT(nvarchar(50), CONVERT(varchar(50), {HanLiteral})) = {HanLiteral} THEN 1 ELSE 0 END AS ansi_lossless, " +
            $"       DATALENGTH(CAST({HanLiteral} AS nvarchar(50))) AS unicode_bytes")
            .Single();

        return new Facts(
            row["server_collation"],
            row["database_collation"],
            int.Parse(row["database_code_page"]),
            int.Parse(row["ansi_bytes"]),
            int.Parse(row["ansi_chars"]),
            row["ansi_lossless"] == "1",
            int.Parse(row["unicode_bytes"]));
    }

    [Fact]
    public void The_varchar_side_of_the_instance_matches_its_collation_code_page()
    {
        // v0.32 第二格：本文件的整副量具都是 T-SQL 的（下面 Probe 那一句里五颗函数一颗都不是通用语法），
        // 所以按缝的纪律在第一句点名，而不是把这一条改成一条"什么也没问"的空断言。
        // <b>而"跳到下一版"的那一格在这里有明确的内容</b>：这一家今天答不出"字符/字节"这两个量纲
        // （F-27/F-32 量到 <c>LENGTH('甲公司')=5</c> 与 <c>OCTET_LENGTH('甲公司')=9</c>，
        // 那颗 5 既不是字符数也不是字节数，走绑定参数仍是 5/9 —— F-34/F-35，所以<em>不是</em>编码折的），
        // 缺的是"这一家的 <c>varchar</c> 与 <c>nvarchar</c> 到底存不存在、各自的 n 数什么"那一格。
        Database.JudgeWrittenFor(Database.Target.SqlServer, "SERVERPROPERTY / DATABASEPROPERTYEX / COLLATIONPROPERTY / DATALENGTH / varchar↔nvarchar 那一整套量纲问句");

        var facts = Probe();

        // ① 记录本身就是硬要求：拿不到值，后面所有"挂着它说"的结论都无从说起。
        That(facts, facts.ServerCollation != "<null>", "SERVERPROPERTY('Collation') 必须给得出值");
        That(facts, facts.DatabaseCollation != "<null>", "DATABASEPROPERTYEX(DB_NAME(),'Collation') 必须给得出值");
        That(facts, facts.CodePage > 0, "数据库排序规则要有一个代码页；-1 是二进制排序规则的返回值，那意味着库该换个说法测");

        // ② 量纲（§10.4 真正的坑）：字符数与实例无关，字节数才是 varchar(n) 的那个 n 在数的东西。
        That(facts, facts.AnsiChars == Han.Length, $"varchar 侧的字符数应恒为 {Han.Length}（LEN 按字符数）");
        That(facts, facts.AnsiBytes == Han.Length * facts.PredictedBytes,
            $"字节数必须由代码页说了算（这里预测每个汉字 {facts.PredictedBytes} 字节）—— varchar(64) 与 nvarchar(64) 装的因此不是同一个量");
        That(facts, facts.UnicodeBytes == Han.Length * 2, "nvarchar 是 UTF-16，与排序规则无关，恒 2 字节/字符");

        // ③ 只有两种形态，没有"丢一半"：要么原样，要么整串变 '?'（后者不报错，所以必须靠断言抓）。
        That(facts, facts.AnsiLossless == (facts.PredictedBytes > 1),
            "能否原样存下由代码页决定：装得下就一字不差，装不下就整串 '?'，中间态说明我们对这个库的理解错了");
    }

    /// <summary>参数绑定这一侧两类库<b>行为一致</b>：字符串按 <see cref="System.Data.DbType.String"/>（Unicode）
    /// 上线，所以 <c>cast 成 nvarchar 再比</c> 永远等值；而一旦 cast 成 varchar，就走与字面量同一个代码页、
    /// 同一个结果。<b>后半句才是这条用例的意义</b>：它证明"用参数"不等于"绕开了排序规则"，
    /// §10.4 要求默认 <c>nvarchar</c> 的理由在绑定层同样成立。</summary>
    [Fact]
    public void Parameters_reach_the_server_as_unicode_and_their_ansi_cast_follows_the_same_rule()
    {
        Database.JudgeWrittenFor(Database.Target.SqlServer, "CAST(@t AS varchar/nvarchar) 与 DATALENGTH 这一组量纲问句（同上一颗的理由）");

        var facts = Probe();

        var row = Database.Rows(
            "SELECT CASE WHEN CAST(@t AS nvarchar(50)) = @t THEN 1 ELSE 0 END AS via_unicode, " +
            "       CASE WHEN CAST(@t AS varchar(50))  = @t THEN 1 ELSE 0 END AS via_ansi, " +
            "       DATALENGTH(CAST(@t AS varchar(50))) AS ansi_bytes",
            ("@t", Han)).Single();

        That(facts, row["via_unicode"] == "1", "参数按 Unicode 上线，cast 成 nvarchar 再比较必须等值（没有隐式降级）");
        That(facts, row["via_ansi"] == (facts.PredictedBytes > 1 ? "1" : "0"), "参数 cast 成 varchar 与字面量同一条路：能不能装下只看代码页");
        That(facts, row["ansi_bytes"] == facts.AnsiBytes.ToString(), "参数侧的字节数与字面量侧必须一致，否则就是两套量纲");
    }

    static void That(Facts facts, bool holds, string expectation)
        => Assert.True(holds, $"{facts.Context}：{expectation}");
}
