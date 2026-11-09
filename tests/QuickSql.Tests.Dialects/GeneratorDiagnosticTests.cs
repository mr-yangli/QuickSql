using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.CodeAnalysis;
using QuickSql;
using QuickSql.Generators;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§14 那一格「编译期负样本（<c>QuickSql001</c>/<c>QuickSql002</c> 该红的写法）没有断言口」的封口。
///
/// <para><b>为什么这一格欠得比看上去重</b>：G1 那三条 Error 的消息是现拼的，拼的理由写在
/// <see cref="EntityContractGenerator"/> 的注释里 —— 「要与反射侧逐字对齐，两处各写一份模板就谈不上
/// 一致」。可这句话到今天只由反射侧的用例证过一半：坏形状在本项目里造一个真类型、跑一次
/// <see cref="ContractResolver"/>、看它抛不抛。编译期那一半只能靠人手工改一处源码、盯一眼构建输出、
/// 验完再还原（v0.20 的 QuickSql004 就是这么"验过"的，记在 §14 那一格）。于是规则本身没有网眼：
/// 谁把 <c>EntityFactsBuilder</c> 里那句模板改一个字，仓库里不会有任何一条用例变红。</para>
///
/// <para><b>手法</b>：把坏形状写成<b>源码字符串</b>，在内存里 <c>CSharpCompilation</c> 一颗一次性程序集，
/// 用 <see cref="CompileTimeProbe"/> 直接驱动 <see cref="EntityContractGenerator"/>，收它的诊断。
/// 那些源文本不进任何一次真实构建，所以这里的类型可以是坏的；而运行期那一半用的就是本文件末尾与
/// <c>RelationshipTests</c>/<c>MetadataEquivalenceTests</c> 里那几个真类型 —— 它们能活在本项目，
/// 是因为这一档没挂源生成器（挂上就编译不红，那正是 <c>QuickSql.Tests.Common</c> 的注释里那句"必须住在
/// 本项目"的全部内容）。</para>
///
/// <para><b>对拍靠什么成立</b>：消息里带着类型名与成员名，所以"两侧字符串相等"这一条同时钉住两件事 ——
/// 模板没分叉，而且内存里那颗编译单元与运行期那个真类型说的是<b>同一个</b>坏形状；名字对不上就红，
/// 不需要有人去比对两份源码。<see cref="Every_negative_sample_is_valid_c_sharp_before_the_generator_is_queried"/>
/// 是这一档的反虚证：源文本自己若有 CS 错，生成器可以一条诊断都不出，那"零诊断"就成了假绿。</para>
///
/// <para>全程不连库：<c>NoDriverFactory</c> 一被碰到就抛（§13.1）。</para></summary>
public sealed class GeneratorDiagnosticTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static IEntitySchemaRegistry Reflected() => new EntitySchemaRegistry(new ContractResolver(Provider, null));

    // ---------- QuickSql001：同层互斥 ----------

    [Fact]
    public void A_conflicting_length_pair_reds_with_the_startup_message_verbatim()
    {
        var red = Assert.Single(CompileTimeProbe.Probe(ConflictingLengthsSource).Errors);

        var thrown = Checks.Raised<MetadataConflictException>(() => Reflected().Contract<ConflictingLengths>());

        // 这一条两侧连主语都是同一份（属性 'Code'），所以它是「逐字」那两个字的正证。
        Assert.Equal("QuickSql001", red.Id);
        Assert.Equal(thrown.Message, red.GetMessage());
    }

    // ---------- QuickSql002 / QuickSql004：成员名换不成列 ----------

    [Fact]
    public void An_index_naming_an_unmapped_member_reds_with_the_startup_message_verbatim()
    {
        var red = Assert.Single(CompileTimeProbe.Probe(IndexOnUnmappedMemberSource).Errors);

        var thrown = Checks.Raised<QuickSqlException>(() => Reflected().Contract<IndexOnUnmappedMember>());

        Assert.Equal("QuickSql002", red.Id);
        Assert.Equal(DiagnosticSeverity.Error, red.Severity);
        Assert.Equal(thrown.Message, red.GetMessage());

        // 索引那条落在实体上（消息里只有索引名，没有可落的成员），与下面 QuickSql004 落在属性上
        // 是一对形状差 —— 位置也是断言的一部分，否则"说清楚了"这句没有验收。
        Assert.Contains("class IndexOnUnmappedMember", CompileTimeProbe.TextAt(red), StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_key_naming_an_unmapped_member_reds_with_the_startup_message_verbatim()
    {
        var red = Assert.Single(CompileTimeProbe.Probe(BadForeignKeySource).Errors);

        var thrown = Checks.Raised<QuickSqlException>(() => Reflected().Contract<BadFk>());

        Assert.Equal("QuickSql004", red.Id);
        Assert.Equal(thrown.Message, red.GetMessage());

        // 同一条判据落在更准的位置上：外键那句指回写错了的那个属性，而不是整只实体。
        var at = CompileTimeProbe.TextAt(red);

        Assert.Contains(nameof(BadFk.Partner), at, StringComparison.Ordinal);
        Assert.DoesNotContain("class BadFk", at, StringComparison.Ordinal);
    }

    // ---------- QuickSql003：不走 G1 不是错 ----------

    [Fact]
    public void An_entity_with_a_base_class_reports_the_info_diagnostic_and_no_error()
    {
        var run = CompileTimeProbe.Probe(DerivedEntitySource);

        var info = Assert.Single(run.Diagnostics, static d => d.Id == "QuickSql003");

        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Empty(run.Errors);
        Assert.Contains("有基类", info.GetMessage(), StringComparison.Ordinal);

        // 退路要真的是退路：这一条不产出那一型的事实代码，而位置落在实体上（G2 接得住的那句说明）。
        Assert.Contains("class ShipmentLine", CompileTimeProbe.TextAt(info), StringComparison.Ordinal);
    }

    // ---------- 反虚证 ----------

    [Fact]
    public void A_clean_entity_draws_no_diagnostic_and_does_draw_code()
    {
        var run = CompileTimeProbe.Probe(CleanEntitySource);

        Assert.Empty(run.Diagnostics);

        // 「零诊断」单独一条量不出「驱动根本没跑起来」，所以同一场里必须看到产出。
        // 产出的文件名不是类名，是 EntityFactsBuilder.GeneratedName 那套 QuickSqlGenerated{类型链}。
        Assert.Contains("QuickSqlGeneratedCleanOrder.g.cs", run.AddedSources);
        Assert.Contains("QuickSqlGeneratedMetadata.g.cs", run.AddedSources);
    }

    [Theory]
    [InlineData(ConflictingLengthsSource)]
    [InlineData(IndexOnUnmappedMemberSource)]
    [InlineData(BadForeignKeySource)]
    [InlineData(DerivedEntitySource)]
    [InlineData(CleanEntitySource)]
    public void Every_negative_sample_is_valid_c_sharp_before_the_generator_is_queried(string source)
    {
        // 上面那几条的红绿全都不出「生成器压根没跑」这个解释，除非源文本本身是合法 C#。
        // 缺 using、属性名拼错、别名对不上，都会先在这里红，而不是变成一条安静的零诊断。
        Assert.Empty(CompileTimeProbe.Probe(source).CsErrors);
    }

    // ---------- 内存里驱动生成器 ----------
    // 量具本体在 CompileTimeProbe（与 XmlDocCommentTests 共用一份 TPA 引用装配）。


    // ---------- 五份源文本：与运行期那五个真类型一一对应 ----------
    //
    // 类名必须与真类型相同（消息里带名字，「两侧字符串相等」靠这个成立），所以全部放进
    // CompileTimeSamples 命名空间 —— 简单名相同而全名不同，不会与本项目那几个真类型撞。

    const string ConflictingLengthsSource = CompileTimeProbe.Header + """
        [Table("ConflictingLengths")]
        public class ConflictingLengths
        {
            public int Id { get; set; }

            [Da.StringLength(10)]
            [Da.MaxLength(20)]
            public string? Code { get; set; }
        }
        """;

    const string IndexOnUnmappedMemberSource = CompileTimeProbe.Header + """
        [Table("QuickSqlUnmappedIndex")]
        [Index("Nobody")]
        public class IndexOnUnmappedMember
        {
            public int Id { get; set; }
        }
        """;

    const string BadForeignKeySource = CompileTimeProbe.Header + """
        [Table("QuickSqlBadFk")]
        public class BadFk
        {
            public int Id { get; set; }

            [DaSchema.ForeignKey("Nobody")]
            public Customer? Partner { get; set; }
        }
        """;

    const string DerivedEntitySource = CompileTimeProbe.Header + """
        public class ShipmentLineBase
        {
            public int Id { get; set; }
        }

        [Table("QuickSqlShipmentLine")]
        public class ShipmentLine : ShipmentLineBase
        {
            public string? Carrier { get; set; }
        }
        """;

    const string CleanEntitySource = CompileTimeProbe.Header + """
        [Table("QuickSqlCleanOrder")]
        public class CleanOrder
        {
            public int Id { get; set; }

            [Da.Required]
            [Da.StringLength(64)]
            public string? Name { get; set; }
        }
        """;
}

// ---------- 本文件要用的两个真类型 ----------
//
// 另外两个（ConflictingLengths / BadFk）已经在 MetadataEquivalenceTests 与
// RelationshipTests 里，这里不重复声明：那两个文件欠的那句「编译期负样本没有断言口」今天有了断言口，
// 而它们的类型仍是运行期那一半的唯一现场。下面两个是新形状，此前没有任何用例钉过。

/// <summary>索引点了一个换不成列的成员。<b>这一条此前只有编译期会红、运行期没人跑过</b>：
/// §14 那一格点名的 <c>QuickSql002</c> 在运行期那一半一直是空的，本版顺手补上。</summary>
[Table("QuickSqlUnmappedIndex")]
[Index("Nobody")]
public class IndexOnUnmappedMember
{
    public int Id { get; set; }
}
