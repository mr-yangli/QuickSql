using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace QuickSql.Generators;

/// <summary>解析链的编译期实现（G1 级）。触发条件是实体带系统的 [Table]
/// （System.ComponentModel.DataAnnotations.Schema）——「标注过」正是 G1 与 G2 的分界线。
/// 没标注、有基类、属性不可公开写的实体不产出代码，运行期由 GeneratedEntitySchemaRegistry
/// 逐条退回 G2（并按 QuickSql003 说明为什么没走 G1）。</summary>
[Generator(LanguageNames.CSharp)]
public sealed class EntityContractGenerator : IIncrementalGenerator
{
    const string DataSchemaTable = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";

    /// <summary>把 C# 文档注释（/// summary）当成备注的最低优先档，默认关。
    /// 消费方开这一档要写两行：属性本身，加上 CompilerVisibleProperty 对 QuickSqlXmlDocComments 的
    /// Include——SDK 只把点名的属性折进 build_property. 那一份编辑器配置，光设属性生成器读不到。
    /// 键名公开是给测试抄的：抄一份字符串进测试，改这一处就成静默假绿。</summary>
    public const string XmlDocCommentsOption = "build_property.QuickSqlXmlDocComments";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 选项走管道而不是实例字段：生成器实例在 IDE 里跨编译复用，存字段会让上一次的读数
        // 留在下一次编译上，而增量缓存按输入算、根本不会重算那一条——症状是"改了开关而产出不动"。
        var xmlDocComments = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => Read(provider.GlobalOptions, XmlDocCommentsOption));

        var outcomes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DataSchemaTable,
                static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                static (capture, _) => capture)
            .Combine(xmlDocComments)
            .Select(static (both, _) => EntityFactsBuilder.Build(both.Left, both.Right))
            .Collect();

        context.RegisterSourceOutput(outcomes, static (spc, batch) =>
        {
            var models = new List<EntityModel>();

            foreach (var outcome in batch)
            {
                foreach (var diagnostic in outcome.Diagnostics)
                    spc.ReportDiagnostic(diagnostic);

                if (outcome.Model is not { } model)
                    continue;

                models.Add(model);
                spc.AddSource(model.Name + ".g.cs", EntityFactsWriter.Render(model));
            }

            if (models.Count > 0)
                spc.AddSource("QuickSqlGeneratedMetadata.g.cs", EntityFactsWriter.RenderLookup(models));
        });
    }

    /// <summary>开关读数：只有写着 true 才算开。缺键、空串、写成 1/yes 一律当关——
    /// 这一档会把源码里的开发期注释搬进库的备注，误开的代价比误关大，所以不接受"看起来像真"的写法。</summary>
    static bool Read(AnalyzerConfigOptions options, string key)
        => options.TryGetValue(key, out var value) && value is { Length: > 0 } && bool.TryParse(value, out var enabled) && enabled;
}

/// <summary>诊断。messageFormat 一律是 {0}：话术由 EntityFactsBuilder 现拼，
/// 因为要与反射侧 MetadataConflictException 的消息逐字对齐——两处各写一份模板就谈不上「一致」。</summary>
static class QuickSqlDiagnostics
{
    public static readonly DiagnosticDescriptor MetadataConflict = Define(
        "QuickSql001", "同一层元数据来源互斥", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor IndexMemberUnmapped = Define(
        "QuickSql002", "索引引用了未映射的属性", DiagnosticSeverity.Error);

    /// <summary>与 QuickSql002 同一形状：现拼的消息与反射侧 EntityContractFactory.MapToColumns
    /// 那句逐字相同，只是主语从索引换成引用导航。</summary>
    public static readonly DiagnosticDescriptor ForeignKeyMemberUnmapped = Define(
        "QuickSql004", "引用导航的外键指向了未映射的属性", DiagnosticSeverity.Error);

    /// <summary>与 QuickSql002 同一形状：现拼的消息与反射侧 EntityContractFactory 收尾那一格
    /// 同源（同一张值域表），所以两条路径拒的是同一批形状。</summary>
    public static readonly DiagnosticDescriptor DefaultValueNotLiteral = Define(
        "QuickSql005", "默认值不是常量字面量档", DiagnosticSeverity.Error);

    /// <summary>不是错：G2 接得住，只是这一条路径上没有「编译期零反射」的待遇。</summary>
    public static readonly DiagnosticDescriptor NotGenerated = Define(
        "QuickSql003", "实体不走 G1，退回反射解析", DiagnosticSeverity.Info);

    /// <summary>与 QuickSql002 同一形状：现拼的消息与反射侧 EntityContractFactory.MapSkipIndexes
    /// 那两句逐字相同（缺表达式、引用了不是列的成员各一句）。</summary>
    public static readonly DiagnosticDescriptor SkipIndexDeclarationInvalid = Define(
        "QuickSql005", "跳过索引声明不完整", DiagnosticSeverity.Error);

    static DiagnosticDescriptor Define(string id, string title, DiagnosticSeverity severity)
        => new(id, title, "{0}", "QuickSql.Metadata", severity, isEnabledByDefault: true);
}
