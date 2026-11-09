using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using QuickSql;
using QuickSql.Generated;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Microsoft.CodeAnalysis;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>G1 侧那颗 <c>QuickSqlXmlDocComments</c> 开关（把 C# 文档注释当备注的最后一条兜底）。
///
/// <para><b>为什么默认关、为什么这一档只长在 G1 上</b>：<c>/// &lt;summary&gt;</c> 只有编译器看得见，
/// 反射侧（G2）没有这份来源。同一张实体在两条启动路径上给出两个备注，正是 §13.11 逐字段相等要拦的那种分叉
/// （它比的是 <c>comment=</c> 那一格），所以这一档默认关、由消费方在自己的项目里点名，而 <c>src</c> 与
/// <c>tests</c> 两侧都不点——本文件的每一条都由 <see cref="CompileTimeProbe"/> 在内存里造一颗开了开关的
/// 编译单元来量，真实构建一次都不受影响。</para>
///
/// <para><b>断言读的是产出的文本</b>，不是产物的运行时值：G1 那份事实要经 <c>GeneratedEntitySchemaRegistry</c>
/// 才成为契约，而内存里生成的代码没有编译进本进程，读不到实例。产出的 <c>Comment = "…"</c> 那一行就是
/// <c>ColumnFacts</c> 里那一格的原文，也是 §13.11 拿去比的那一格的来路。</para>
///
/// <para>全程不连库：<c>NoDriverFactory</c> 一被碰到就抛（§13.1）。</para></summary>
public sealed class XmlDocCommentTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static IEntitySchemaRegistry Generated()
        => new GeneratedEntitySchemaRegistry(Provider, QuickSqlGeneratedMetadata.TryGet);

    static IEntitySchemaRegistry Reflected() => new EntitySchemaRegistry(new ContractResolver(Provider, null));

    const string Facts = "QuickSqlGeneratedDocOrder.g.cs";

    // ---------- 两档：关着不引、开着才引 ----------

    [Fact]
    public void Off_by_default_the_doc_comments_stay_out_of_the_contract()
    {
        var facts = CompileTimeProbe.Probe(DocOrderSource).Text(Facts);

        // 关着时两个备注槽都是"没声明"，而不是空串：null 与空串在 §9.5 是两档（清掉备注 / 从来没写过）。
        Assert.Contains(", null)", facts);
        Assert.DoesNotContain("订单主表", facts, StringComparison.Ordinal);
        Assert.Equal(2, Count(facts, "Comment = null,"));
    }

    [Fact]
    public void Switched_on_the_summary_fills_both_remark_slots()
    {
        var facts = CompileTimeProbe.Probe(DocOrderSource, "true").Text(Facts);

        Assert.Contains(", \"订单主表：一张表一个类\")", facts);
        Assert.Contains("Comment = \"约定命中的主键\",", facts);
        Assert.Contains("Comment = \"客户名\",", facts);
        Assert.DoesNotContain("Comment = null,", facts);
    }

    // ---------- 链上的位置：显式备注永远压过注释 ----------

    [Fact]
    public void An_explicit_comment_beats_the_doc_comment_on_both_slots()
    {
        var facts = CompileTimeProbe.Probe(AnnotatedSource, "true").Text("QuickSqlGeneratedDocAnnotated.g.cs");

        Assert.Contains(", \"注解给的表备注\")", facts);
        Assert.DoesNotContain("类摘要不该盖住注解", facts, StringComparison.Ordinal);
        Assert.Contains("Comment = \"注解给的列备注\",", facts);
        Assert.DoesNotContain("属性摘要不该盖住注解", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Display_annotation_beats_the_doc_comment()
    {
        var facts = CompileTimeProbe.Probe(DisplaySource, "true").Text("QuickSqlGeneratedDocDisplay.g.cs");

        // 备注链在反射侧是 [Description]（System.ComponentModel）→ [Display(Description/Name)]，注释接在链尾：
        // 注释接在它后面：换档序会让同一份源码在两侧给出两个备注，而那是 §13.11 抓得住的最轻的一种。
        Assert.Contains("Comment = \"显示用的备注\",", facts);
        Assert.DoesNotContain("摘要让位", facts, StringComparison.Ordinal);
    }

    // ---------- 折行、去标签、还原实体 ----------

    [Fact]
    public void Markup_line_breaks_and_entities_fold_into_one_plain_line()
    {
        var facts = CompileTimeProbe.Probe(FoldedSource, "true").Text("QuickSqlGeneratedDocFold.g.cs");

        // 三段换行折成一行（库里那条 COMMENT ON 的字面量里不该有换行），<c> 连标签摘掉、
        // &lt; 还原成 <，而 <see cref> 那一格本来就没有文本。
        Assert.Contains("Comment = \"金额， 上限 9999.99， 且要小于 < 余额\",", facts);
    }

    // ---------- 编译器那一档：真实构建实测是 None，量具默认是 Parse ----------

    [Theory]
    [InlineData(DocumentationMode.Parse)]
    [InlineData(DocumentationMode.None)]
    public void Either_documentation_mode_of_the_compiler_gives_the_same_remarks(DocumentationMode mode)
    {
        // csc 不带 /doc 时实测 DocumentationMode = None（真实构建就是这一档），那几行 /// 只是普通注释 trivia；
        // IDE 与本量具的默认值是 Parse。以前量具只有 Parse 一档，于是"备注全空而构建全绿"这条它量不出来
        // （2026-10-08 就是端到端那一次才露出来的）。换档不换手：同一份源码在两种编译里必须交出同一句备注。
        var facts = CompileTimeProbe.Probe(DocOrderSource, "true", mode).Text(Facts);

        Assert.Contains(", \"订单主表：一张表一个类\")", facts);
        Assert.Contains("Comment = \"约定命中的主键\",", facts);
        Assert.Contains("Comment = \"客户名\",", facts);

        // 多行那一条在 None 档更要单独量：那里相邻两行 /// 是两条独立 trivia，中间那个换行哪一条都不属于，
        // 拼错了就是「金额，上限 <c>9999.99</c>…」折成一句不存在的话。
        var folded = CompileTimeProbe.Probe(FoldedSource, "true", mode).Text("QuickSqlGeneratedDocFold.g.cs");

        Assert.Contains("Comment = \"金额， 上限 9999.99， 且要小于 < 余额\",", folded);
    }

    // ---------- 三种"没写"的形状 ----------

    [Fact]
    public void An_empty_summary_is_no_declaration_rather_than_an_empty_remark()
    {
        var facts = CompileTimeProbe.Probe(EmptySummarySource, "true").Text("QuickSqlGeneratedDocEmpty.g.cs");

        // 只有 <summary></summary> 的一格交回 null（没声明），不是 ""（说过"没有备注"）——后者会让
        // §9.5 的比对把"清掉备注"与"从来没写过"折成同一档。
        Assert.Contains("Comment = null,", facts);
        Assert.DoesNotContain("Comment = \"\",", facts);
    }

    [Fact]
    public void An_inheritdoc_without_a_summary_draws_no_remark()
    {
        var facts = CompileTimeProbe.Probe(InheritDocSource, "true").Text("QuickSqlGeneratedDocInherit.g.cs");

        // <inheritdoc/> 不在这版的来源清单里：Roslyn 那颗 API 不做继承解析，解析要引一份文档文件、
        // 而那是运行期反射的活儿（路 B），生成期跟着做不了。
        Assert.Contains("Comment = null,", facts);
    }

    [Fact]
    public void A_malformed_doc_comment_neither_reds_the_build_nor_becomes_a_remark()
    {
        var run = CompileTimeProbe.Probe(MalformedSource, "true");

        // 作者写了裸 & 时编译器自己有 CS1570 那句（这里是 warning 级，不进 CsErrors），
        // 生成器再说一遍只是把一条警告升成一次构建失败。
        Assert.Empty(run.Errors);
        Assert.Contains("Comment = null,", run.Text("QuickSqlGeneratedDocMalformed.g.cs"));
    }

    // ---------- 开关的读数：只认写成真的那几种 ----------

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("")]
    public void Anything_but_a_literal_true_leaves_the_switch_off(string value)
        => Assert.Contains("Comment = null,", CompileTimeProbe.Probe(DocOrderSource, value).Text(Facts));

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void A_literal_true_opens_it_whatever_the_case(string value)
        => Assert.Contains("Comment = \"客户名\",", CompileTimeProbe.Probe(DocOrderSource, value).Text(Facts));

    // ---------- G2 侧没有这份来源 ----------

    [Fact]
    public void The_reflection_path_has_no_such_source_and_the_project_is_quiet_about_it()
    {
        // 本项目没挂源生成器，DocShadow 只由反射侧解析：开着开关的那个项目里，退回 G2 的那张实体
        // 就是这样拿不到备注的。这一格分叉 §13.11 看得见（comment= 那一行），所以开关默认关。
        var contract = Reflected().Contract<DocShadow>();

        Assert.Null(contract.Table.Comment);
        Assert.Null(contract.Columns.Single(c => c.MemberName == "Id").Comment);

        // 生成器注册表那份也一样：没生成过这一型就整个退回 G2，而不是半格有备注。
        Assert.Equal(MetadataLayer.Convention, Generated().Contract<DocShadow>().Columns[0].Origin.Layer);
    }

    static int Count(string text, string needle)
    {
        var total = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            total++;

        return total;
    }

    // ---------- 源文本 ----------

    const string DocOrderSource = CompileTimeProbe.Header + """
        /// <summary>订单主表：一张表一个类</summary>
        [Table("QuickSqlDocOrder")]
        public class DocOrder
        {
            /// <summary>约定命中的主键</summary>
            public int Id { get; set; }

            /// <summary>客户名</summary>
            public string? Name { get; set; }
        }
        """;

    const string AnnotatedSource = CompileTimeProbe.Header + """
        /// <summary>类摘要不该盖住注解</summary>
        [Table("QuickSqlDocAnnotated")]
        [Description("注解给的表备注")]
        public class DocAnnotated
        {
            /// <summary>属性摘要不该盖住注解</summary>
            [Description("注解给的列备注")]
            public int Id { get; set; }
        }
        """;

    const string DisplaySource = CompileTimeProbe.Header + """
        [Table("QuickSqlDocDisplay")]
        public class DocDisplay
        {
            /// <summary>摘要让位给 Display</summary>
            [Da.Display(Description = "显示用的备注")]
            public string? Remark { get; set; }
        }
        """;

    const string FoldedSource = CompileTimeProbe.Header + """
        [Table("QuickSqlDocFold")]
        public class DocFold
        {
            /// <summary>金额，
            /// 上限 <c>9999.99</c>，
            /// 且要小于 &lt; 余额 <see cref="int"/></summary>
            public decimal Amount { get; set; }
        }
        """;

    const string EmptySummarySource = CompileTimeProbe.Header + """
        [Table("QuickSqlDocEmpty")]
        public class DocEmpty
        {
            /// <summary></summary>
            public int Id { get; set; }
        }
        """;

    const string InheritDocSource = CompileTimeProbe.Header + """
        [Table("QuickSqlDocInherit")]
        public class DocInherit
        {
            /// <inheritdoc />
            public int Id { get; set; }
        }
        """;

    const string MalformedSource = CompileTimeProbe.Header + """
        [Table("QuickSqlDocMalformed")]
        public class DocMalformed
        {
            /// <summary>裸 & 是畸形注释</summary>
            public int Id { get; set; }
        }
        """;
}

/// <summary>反射侧的那个负样本：本项目没挂源生成器，所以它只由 <c>ContractResolver</c> 解析，
/// 而那一侧没有"源码注释"这条来源。</summary>
[Table("QuickSqlDocShadow")]
public class DocShadow
{
    /// <summary>反射侧读不到的属性摘要</summary>
    public int Id { get; set; }
}
