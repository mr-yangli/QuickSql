using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using QuickSql.Generators;

namespace QuickSql.Tests.Dialects;

/// <summary>把源生成器搬到内存里驱动一次，收它的诊断与产出 —— <see cref="GeneratorDiagnosticTests"/> 与
/// <see cref="XmlDocCommentTests"/> 共用的那一份量具（此前它长在前一个文件里，两处要用就只能抄一份
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> 的引用装配，而抄的那份每次加依赖都要跟着改，漏一条的症状恰好是
/// "生成器安静地不产出"，正是这一档最不能有的失效形状）。
///
/// <para><b>手法</b>：源文本写成字符串，在内存里 <c>CSharpCompilation</c> 一颗一次性程序集，用
/// <see cref="CSharpGeneratorDriver"/> 显式跑一遍 <see cref="EntityContractGenerator"/>。那些源文本
/// 不进任何一次真实构建，所以形状可以是坏的、类名可以和本项目那几个真类型重名（各自待在
/// <c>CompileTimeSamples</c> 命名空间）。</para>
///
/// <para><c>xmlDocComments</c> 那一颗交的是<b>开关的原样字符串</b>而不是布尔：开关的读数本身有一条要钉的规矩
/// （只认 <c>true</c>），代糖成布尔就量不到它。传 <c>null</c> = 全局配置里根本没有那一行，与真实项目
/// 没设那个 MSBuild 属性同一形状。</para>
///
/// <para><c>documentationMode</c> 那一颗是<b>编译器的档</b>，不是本项目的开关：<c>csc</c> 不带 <c>/doc</c> 时
/// 实测为 <c>None</c>（真实构建就是这一档），IDE 与这颗量具的默认值是 <c>Parse</c>。同一行 <c>///</c> 在前者
/// 只是普通注释 trivia、在后者才是文档注释 trivia，所以只按 <c>Parse</c> 量的用例给得出"量具全绿而字段读不到"
/// 那种假绿；而 G1 在两种编译里必须算出同一份元数据，否则编辑期看到的备注和落库的那句不是一句话。</para></summary>
static class CompileTimeProbe
{
    /// <summary>公共的头部：别名与 <c>using</c> 要和真项目一致，否则源文本先因为缺别名而红，
    /// 生成器一条诊断都轮不到出。</summary>
    public const string Header = """
        using QuickSql.Metadata;
        using QuickSql.TestInfra;
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations.Schema;
        using Da = System.ComponentModel.DataAnnotations;
        using DaSchema = System.ComponentModel.DataAnnotations.Schema;

        namespace CompileTimeSamples;

        """;

    /// <summary>一次运行的五样产出：诊断、其中 Error 级的那几条、源文本自己的 CS 错、生成的文件名、
    /// 生成文件的<em>文本</em>（<see cref="Run.Text"/> 按名取）。</summary>
    public sealed record Run(ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<Diagnostic> Errors,
        ImmutableArray<Diagnostic> CsErrors, ImmutableArray<string> AddedSources,
        ImmutableArray<(string Name, string Text)> Trees)
    {
        /// <summary>按文件名取产出的全文。没有那份文件时把已有的名单一起交出去——"断言的对象写错了"
        /// 与"生成器没产出"这两种失败得能一眼分开。</summary>
        public string Text(string name)
        {
            foreach (var tree in Trees)
                if (tree.Name == name)
                    return tree.Text;

            throw new InvalidOperationException(
                $"没有 {name} 这份产出。当场生成的文件：" + string.Join("、", AddedSources));
        }
    }

    public static Run Probe(string source, string? xmlDocComments = null,
        DocumentationMode documentationMode = DocumentationMode.Parse)
    {
        var tree = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Latest).WithDocumentationMode(documentationMode));

        var compilation = CSharpCompilation.Create(
            "QuickSqlCompileTimeSamples",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // 先要源文本自己站得住（CS 级），否则「没有诊断」这件事没有任何含义。
        var csErrors = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        var driver = CSharpGeneratorDriver.Create(
            [new EntityContractGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)tree.Options,
            optionsProvider: new Options(xmlDocComments));

        var result = driver.RunGenerators(compilation).GetRunResult();

        var diagnostics = result.Results.SelectMany(static r => r.Diagnostics).ToImmutableArray();

        return new Run(
            diagnostics,
            diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray(),
            csErrors,
            [.. result.GeneratedTrees.Select(static t => Path.GetFileName(t.FilePath))],
            [.. result.GeneratedTrees.Select(static t => (Path.GetFileName(t.FilePath), t.GetText().ToString()))]);
    }

    /// <summary>诊断落点上的那段源码文本。位置对不对，只能读它自己圈住的那一段。</summary>
    public static string TextAt(Diagnostic diagnostic)
        => diagnostic.Location.SourceTree!.GetText().GetSubText(diagnostic.Location.SourceSpan).ToString();

    static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    static ImmutableArray<MetadataReference> BuildReferences()
    {
        // 这个进程加载得到什么，内存里那颗编译单元就能看见什么：TPA 是最省事也最不会漏的一份，
        // 里面已经带着 QuickSql.Abstractions（[Table] / [Index]）、QuickSql.Tests.Common（Customer 这种
        // 引用目标）与全部框架程序集。不手写名单的理由见本类的类注释。
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var path in trusted.Split(Path.PathSeparator))
        {
            if (path.Length == 0 || !File.Exists(path) || !seen.Add(Path.GetFileName(path)))
                continue;

            builder.Add(MetadataReference.CreateFromFile(path));
        }

        return builder.ToImmutable();
    }

    /// <summary>只装这一颗开关的全局配置提供器。别的键一概不给：生成器读的就是这一颗，多给的那几颗
    /// 只会让"缺键"这一档量不出来。</summary>
    sealed class Options(string? xmlDocComments) : AnalyzerConfigOptionsProvider
    {
        static readonly Dictionary<string, string> None = [];

        readonly Dictionary<string, string> _global = xmlDocComments is null
            ? None
            : new Dictionary<string, string> { [EntityContractGenerator.XmlDocCommentsOption] = xmlDocComments };

        public override AnalyzerConfigOptions GlobalOptions => new Map(_global);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Map(_global);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Map(_global);

        sealed class Map(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                if (values.TryGetValue(key, out var found))
                {
                    value = found;
                    return true;
                }

                value = string.Empty;

                return false;
            }

            public override IEnumerable<string> Keys => values.Keys;
        }
    }
}
