using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QuickSql.Generators;

/// <summary>解析链的编译期实现，逐条对着 ContractResolver 写：层优先级、每层的 note 文本、
/// 成员被跳过的条件都要一致——两侧一致性由逐字段相等断言兜住，这里改一句话术那一层就会红。
/// 唯一的刻意例外是 Build 的 xmlDocComments 那一档：源码注释只有编译器看得见，反射侧没有这份来源，
/// 它一旦开着两侧对同一张表就能给出不同备注，默认关正是为了让"开它"是一次签字而不是一次顺带。</summary>
sealed class BuildOutcome
{
    public EntityModel? Model;

    public List<Diagnostic> Diagnostics = [];
}

static class EntityFactsBuilder
{
    const string QuickSqlColumn = "QuickSql.Metadata.QuickColumnAttribute";
    const string QuickSqlIndex = "QuickSql.Metadata.IndexAttribute";

    const string QuickSqlClickHouseSkipIndex = "QuickSql.Metadata.ClickHouseSkipIndexAttribute";
    const string QuickSqlTDengineStable = "QuickSql.Metadata.TDengineStableAttribute";
    const string QuickSqlTDengineTag = "QuickSql.Metadata.TDengineTagAttribute";
    const string QuickSqlSplit = "QuickSql.Metadata.SplitTableAttribute";
    const string QuickSqlDataSource = "QuickSql.Metadata.DataSourceAttribute";
    const string QuickSqlConcurrency = "QuickSql.Metadata.ConcurrencyTokenAttribute";
    const string DataSchemaTable = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";
    const string DataSchemaColumn = "System.ComponentModel.DataAnnotations.Schema.ColumnAttribute";
    const string DataNotMapped = "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute";
    const string DataForeignKey = "System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute";
    const string QuickSqlNoCascade = "QuickSql.Metadata.NoCascadeAttribute";
    const string IEnumerable = "System.Collections.IEnumerable";
    const string DataRequired = "System.ComponentModel.DataAnnotations.RequiredAttribute";
    const string DataStringLength = "System.ComponentModel.DataAnnotations.StringLengthAttribute";
    const string DataMaxLength = "System.ComponentModel.DataAnnotations.MaxLengthAttribute";
    const string DataDisplay = "System.ComponentModel.DataAnnotations.DisplayAttribute";
    const string DataKey = "System.ComponentModel.DataAnnotations.KeyAttribute";
    const string ScDescription = "System.ComponentModel.DescriptionAttribute";
    const string ScDefaultValue = "System.ComponentModel.DefaultValueAttribute";

    static readonly SymbolDisplayFormat Qualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    static readonly HashSet<string> MappedByName =
    [
        "System.Decimal", "System.Guid", "System.DateTime", "System.DateTimeOffset",
        "System.DateOnly", "System.TimeOnly", "System.TimeSpan",
    ];

    /// <summary>xmlDocComments 是整条链上唯一一颗 G1 独有的来源：源码里的 /// summary 只有编译器看得见，
    /// 反射侧没有这一份。开着它，同一张表在两条启动路径上就能给出两个备注（对拍的 comment= 那一格会红），
    /// 所以默认关、且只在消费方点名后才生效——"开它"要是一次签字，不是一次顺带。</summary>
    public static BuildOutcome Build(GeneratorAttributeSyntaxContext context, bool xmlDocComments)
    {
        var outcome = new BuildOutcome();
        var entity = (INamedTypeSymbol)context.TargetSymbol;
        var at = context.TargetNode.GetLocation();
        var attributes = entity.GetAttributes();

        if (entity.IsGenericType || entity.IsUnboundGenericType)
            return Blocked(outcome, at, "泛型实体");

        if (entity.BaseType is { SpecialType: not SpecialType.System_Object })
            return Blocked(outcome, at, "有基类：继承来的属性列序与反射侧的 MetadataToken 序对不齐");

        // G1 的门槛留在公共这一格：生成的那一行是字面的 `new T()`，非公共的无参构造在生成侧编不出来
        // （而 G2 反射侧认非公共）——这条挡住的是"少一份编译期事实册"，不是"这颗实体不能物化"。
        if (!entity.InstanceConstructors.Any(static c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public))
            return Blocked(outcome, at, "无参构造器不是公共的，`new T()` 在生成侧编不出来");

        var dataTable = Find(attributes, DataSchemaTable);

        var model = new EntityModel
        {
            Namespace = entity.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            Entity = TypeExpression(entity),
            Name = GeneratedName(entity),
            TableName = Text(dataTable, 0, "Name") ?? entity.Name,
            TableSchema = Text(dataTable, -1, "Schema"),
            // 表备注：[Description]（System.ComponentModel）在前，文档注释排最末——
            // 注解写过就轮不到它（与下面列备注同一条取向）。
            TableComment = Text(Find(attributes, ScDescription), 0, "Description") ?? (xmlDocComments ? SummaryOf(entity) : null),
            Split = SplitOf(attributes),
            Source = SourceOf(attributes),
        };

        var conflicts = false;
        var navigations = new List<IPropertySymbol>();

        foreach (var property in entity.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer)
                continue;

            // 显式排除先判：一颗 [NotMapped] 打在导航属性上就只是"不映射",不是一条关系
            // （反射侧 ReadMembers 的 if-else 链同一次序，两侧必须同一条链）。
            if (IsExplicitlyIgnored(property))
                continue;

            // 没有写口 ⇒ 既落不回值也不是数据。这一格按"有没有设置器"问，而不是按"设置器公不公开"：
            // { get; protected set; } 与 { get; init; } 都落得回值，把它们一起摘掉
            // 会让整族 ABP 实体的键与明细列凭空消失。反射侧 ReadMembers 同一位置同一判据。
            if (property.SetMethod is null)
                continue;

            if (!IsColumnShape(property.Type))
            {
                // 两格导航都收，收在同一份列表里按声明序：反射侧那一格是按 MetadataToken 序
                // 一次走完的，而对拍比的是逐行相等（顺序也算内容），分成两份列表去产出会把
                // "先声明集合再声明引用"那一张实体判成分叉。
                if (IsReferenceNavigation(property.Type))
                    navigations.Add(property);
                else if (property.GetMethod is not null && CollectionElementOf(property.Type) is not null)
                    navigations.Add(property);

                continue;
            }

            var facts = ColumnOf(property, model, outcome, at, ref conflicts, xmlDocComments);

            if (facts is not null)
                model.Columns.Add(facts);
        }

        // 关系要等列收齐（约定那一档问的是"这张表上有没有 XxxId 那一列",而导航属性常写在前面）。
        RelationshipsOf(navigations, entity, model, outcome, at, conflicts);

        IndexesOf(entity, model, outcome, at, conflicts);

        SkipIndexesOf(entity, model, outcome, at, conflicts);

        StableOf(entity, model);

        if (conflicts)
            // 报错时不产出模型，但诊断必须留着——丢掉它等于「编译不过却没人说为什么」。
            return outcome;

        if (model.Columns.Count == 0)
            return Blocked(outcome, at, "没有可映射的公共属性");

        outcome.Model = model;

        return outcome;
    }

    /// <summary>调用点已经判过"显式排除"与"这一格是不是列的形状"，所以这里只做逐层取值。</summary>
    static ColumnModel? ColumnOf(
        IPropertySymbol property, EntityModel model, BuildOutcome outcome, Location entityLocation, ref bool conflicts, bool xmlDocComments)
    {
        var declared = property.GetAttributes();
        var column = Find(declared, QuickSqlColumn);
        var dataColumn = Find(declared, DataSchemaColumn);
        var type = property.Type;

        var required = Find(declared, DataRequired);
        var stringLength = Find(declared, DataStringLength);
        var maxLength = Find(declared, DataMaxLength);

        // 同层冲突：StringLength 与 MaxLength 都在 DataAnnotations 层。取严格者会把
        // 「建表按 20、校验按 50」藏三个月，所以同样拒绝——只是拒绝发生在编译期。
        if (stringLength is not null && maxLength is not null)
        {
            outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.MetadataConflict, At(property, entityLocation),
                $"属性 '{property.Name}' 的同一层元数据来源互斥：[StringLength({Number(stringLength, 0, "MaximumLength") ?? 0})] vs [MaxLength({Number(maxLength, 0, "Length") ?? 0})]。QuickSql 不自动取更严的那条。"));

            conflicts = true;

            return null;
        }

        var writable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public } && !IsInitOnly(property);

        if (property.GetMethod is not { DeclaredAccessibility: Accessibility.Public } || !writable)
        {
            outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.NotGenerated, entityLocation,
                $"实体 {model.Name} 的属性 {property.Name} 不是公共可读写的（init 或受限 set），G1 不生成该实体，退回 G2（§12.7）。"));

            conflicts = true;

            return null;
        }

        var name = property.Name;
        var declaredLength = Number(column, -1, "Length");

        var (columnName, columnNameLayer, columnNameNote) =
            Text(dataColumn, 0, "Name") is { } dataName ? (dataName, "DataAccessAnnotation", $"[Column(\"{dataName}\")]")
            : (name, "Convention", "约定：属性名");

        // 三态与反射侧同一口径：写了的（含 0 与负数）原样进事实，"未声明"那一档才是 null。
        // 非正数 = 声明者明确说过"不要上限"，收尾时不吃 orm 的默认长度。
        var (lengthValue, lengthNote) =
            declaredLength is { } quick ? ((int?)quick, $"[QuickColumn(Length={quick})]")
            : stringLength is not null ? (Number(stringLength, 0, "MaximumLength") ?? 0, $"[StringLength({Number(stringLength, 0, "MaximumLength") ?? 0})]")
            : maxLength is not null ? (Number(maxLength, 0, "Length") ?? 0, $"[MaxLength({Number(maxLength, 0, "Length") ?? 0})]")
            : ((int?)null, "未声明，交给 provider 默认");

        var (isNullable, nullableNote, nullabilityDeclared) = NullableOf(type, property, column, required);

        // 没打标记就不写来源：G2 侧那一格同样是 null，而对拍比的是整个 ColumnFacts——
        // 这里多写一句常量就是「两侧各自都合理、但文本不一样」。
        // 名字约定与反射侧同一条：ConcurrencyStamp 直接认成应用给值的并发列。
        var tokenAttribute = Find(declared, QuickSqlConcurrency);
        var conventionToken = name == "ConcurrencyStamp";
        var concurrency = tokenAttribute is not null || conventionToken;

        // 应用给值那一族（第三族）：AppSetsValue 显式声明，或 ConcurrencyStamp 名字约定
        //（与反射侧同一条判据）。Fluent 那一档生成期整体上看不见。
        var appSets = false;

        if (concurrency)
        {
            if (tokenAttribute is not null)
                appSets = Tri(tokenAttribute, "AppSetsValue") == 1;
            else
                appSets = conventionToken;
        }

        return new ColumnModel
        {
            Member = name,
            ClrType = TypeExpression(type),
            ColumnName = columnName,
            ColumnNameLayer = columnNameLayer,
            ColumnNameNote = columnNameNote,
            MaxChars = lengthValue,
            LengthNote = lengthNote,
            IsNullable = isNullable,
            NullableNote = nullableNote,
            NullabilityDeclared = nullabilityDeclared,
            IsPrimary = Tri(column, "IsPrimary") == 1 || Find(declared, DataKey) is not null,
            DeclaredIdentity = Tri(column, "IsIdentity") switch { 1 => true, 2 => false, _ => null },
            IsConcurrencyToken = concurrency,
            AppSetsValue = concurrency && appSets,
            ConcurrencyNote = concurrency ? "[ConcurrencyToken]" : null,
            Precision = Positive(Number(column, -1, "Precision")),
            Scale = Positive(Number(column, -1, "Scale")),
            StoreType = Text(dataColumn, -1, "TypeName"),
            // 备注的链与反射侧同一条（[Description] > [Display(Description)] > [Display(Name)]），
            // 文档注释接在链尾兜底：写过显式备注就不轮到它。
            Comment = Text(Find(declared, ScDescription), 0, "Description")
                      ?? Text(Find(declared, DataDisplay), -1, "Description")
                      ?? Text(Find(declared, DataDisplay), -1, "Name")
                      ?? (xmlDocComments ? SummaryOf(property) : null),
            // 默认值的字面量档在生成期先拦一遍：生成器物理上产不出"任意对象"的字面量；
            // DateTime/byte[]/枚举由反射侧收尾那一格拒，两侧的话术同源、判据同表。
            DefaultValue = DefaultValueOf(property, declared, outcome, entityLocation),
            IsString = Core(type).SpecialType == SpecialType.System_String,
            DirectArm = DirectArmOf(type),
            CanBeNull = !(type.IsValueType && !IsNullableValue(type)),
        };
    }

    /// <summary>默认值那一格的生成期半边：字面量档（string/bool/整型/浮点/decimal）把值原样带回去，
    /// 其余档（生成器产不出字面量的对象、值域不要的 DateTime/byte[]/枚举）当场出 QuickSql005
    /// 并照常生成其余各格——反射侧用同一张值域表拒，两侧的话术要点名同一个理由。</summary>
    static object? DefaultValueOf(
        IPropertySymbol property, IEnumerable<AttributeData> declared, BuildOutcome outcome, Location entityLocation)
    {
        if (Find(declared, ScDefaultValue) is not { } attribute)
            return null;

        var value = Argument(attribute, 0)?.Value;

        if (value is null)
            return null;

        if (value is not (string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal))
        {
            outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.DefaultValueNotLiteral, At(property, entityLocation),
                $"属性 '{property.Name}' 的 [DefaultValue] 是 {value.GetType().Name}：" +
                "默认值只收常量字面量档（string ∶ bool ∶ 整型 ∶ 浮点 ∶ decimal）。DateTime 会把首次建表那一刻冻进 DDL，" +
                "byte[] 没有字面量形，枚举的字面量形各家不同。"));

            return null;
        }

        return value;
    }

    /// <summary>这一列有没有直读臂。这里只出方法名，不出编号：
    /// 编号是运行期按驱动交回的实际类型落的，名单与臂本体在 ReadOps 那一个文件里，
    /// 两侧唯一的连接就是这个名字——名字写错是编译错误，挑错臂是编号对不上、静默退回装箱口，
    /// 所以能错的方向只有"没变快"。枚举不在名单里；byte[]、DateOnly、TimeOnly 等也不在
    /// （DbDataReader 对这几型没有"一次调用一个值"的原始访问器）。</summary>
    // 直读臂名单：GetTimeSpan 不在——第一版写了它，构建期编译失败撞出来的缺席，不是没想过。
    static string? DirectArmOf(ITypeSymbol type)
    {
        var arm = FullName(Core(type)) switch
        {
            "System.Boolean" => "Boolean",
            "System.Byte" => "Byte",
            "System.Int16" => "Int16",
            "System.Int32" => "Int32",
            "System.Int64" => "Int64",
            "System.Single" => "Single",
            "System.Double" => "Double",
            "System.Decimal" => "Decimal",
            "System.Char" => "Char",
            "System.DateTime" => "DateTime",
            "System.String" => "String",
            "System.Guid" => "Guid",
            _ => null,
        };

        if (arm is null)
            return null;

        // 可空值类型换一首臂：非空臂的返回类型是底层型，NULL 那一格由门给 default(T)，
        // 交给 DateTime? 成员就是静默摊平（0001-01-01 冒充"没有值"）；可空那一族的非直读分支
        // 是 Read<T?>，所以 NULL 仍是 null。字符串不换——string? 与 string 是同一个运行期类型。
        return IsNullableValue(type) ? "ReadNullable" + arm : "Read" + arm;
    }

    /// <summary>可空性的五档判定，顺序与 ContractResolver.ResolveNullable 一字不差
    /// （第一档 Fluent 在编译期不存在，所以从自研属性起）。第三颗是"这句非空到底有没有人说过"——
    /// 可空上下文之外（NullableAnnotation.None）说的是"我没表过态"，它把必填尺子摘掉，而不改列的形状。</summary>
    static (bool, string, bool) NullableOf(ITypeSymbol type, IPropertySymbol property, AttributeData? column, AttributeData? required)
    {
        if (Tri(column, "IsPrimary") == 1)
            return (false, "[QuickColumn(IsPrimary=Yes)] ⇒ 非空", true);

        if (Tri(column, "IsNullable") is 1 or 2)
        {
            var nullable = Tri(column, "IsNullable") == 1;

            return (nullable, nullable
                ? required is not null
                    ? "WARN: [Required] 与 [QuickColumn(IsNullable=Yes)] 矛盾，按优先级取自研属性"
                    : "[QuickColumn(IsNullable=Yes)]"
                : "[QuickColumn(IsNullable=No)]", true);
        }

        if (required is not null)
            return (false, "[Required]", true);

        if (type.IsValueType && !IsNullableValue(type))
            return (false, "值类型非 Nullable ⇒ 非空列", true);

        // 反射侧读的是 NullabilityInfoContext 的 WriteState（三态），而编译期这一侧的
        // NullableAnnotation.None 正是 WriteState=Unknown 那一格：可空上下文之外
        // （#nullable disable、没带可空元数据的引用程序集）。两侧在这一格必须同形（逐字段对拍）。
        if (property.NullableAnnotation == NullableAnnotation.None)
            return (false, "C# 没表过态（可空上下文之外）⇒ 建表按非空列，不落必填尺子", false);

        var annotated = IsNullableValue(type) || property.NullableAnnotation == NullableAnnotation.Annotated;

        return (annotated, annotated ? "C# 可空 ⇒ 可空列" : "C# 非空 ⇒ 非空列", true);
    }

    /// <summary>「被显式排除」= [NotMapped]（系统注解）。反射侧同一句判定，且在链条上同一位置：
    /// 排在类型判定之前，所以打在导航属性上的那颗 [NotMapped] 只是"不映射"，不会被当成关系。</summary>
    static bool IsExplicitlyIgnored(IPropertySymbol property)
    {
        var declared = property.GetAttributes();

        return Find(declared, DataNotMapped) is not null;
    }

    /// <summary>能进列清单的形状：byte[] 单独放行，其余要落在可映射闭包里。反射侧同一句。</summary>
    static bool IsColumnShape(ITypeSymbol type)
        => type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } || IsMappable(type);

    /// <summary>引用导航 = 类类型、不是 string、不是集合形态（byte[] 与 List〈T〉都在这条上
    /// 落到"不可映射而跳过"）。判定不要求目标是实体：Uri 配 UriId 也会成一条边，
    /// 而这条边唯一的用法（写序）对没有跟踪档的类型天然无效。反射侧同一条。</summary>
    static bool IsReferenceNavigation(ITypeSymbol type)
    {
        var core = Core(type);

        return core.TypeKind == TypeKind.Class
               && core.SpecialType != SpecialType.System_String
               && !ImplementsEnumerable(core);
    }

    /// <summary>IEnumerable（非泛型那一颗）是所有集合形态的公共祖先，与反射侧
    /// typeof(IEnumerable).IsAssignableFrom 同一问句。自算闭包而不调 Compilation.GetSpecialType：
    /// 生成器手里没有 Compilation，而多引一份上下文只为这一问不值。</summary>
    static bool ImplementsEnumerable(ITypeSymbol type)
    {
        if (FullName(type) == IEnumerable)
            return true;

        // AllInterfaces 已经含继承来的那些，所以不必自己走基类链。
        foreach (var iface in type.AllInterfaces)
        {
            if (FullName(iface) == IEnumerable)
                return true;
        }

        return false;
    }

    static void RelationshipsOf(List<IPropertySymbol> navigations, INamedTypeSymbol entity, EntityModel model, BuildOutcome outcome, Location at, bool alreadyFailed)
    {
        if (alreadyFailed)
            return;

        foreach (var property in navigations)
        {
            var declared = ForeignKeyOf(property);

            // 集合那一格：外键成员在子端那张表上，这里换算不了也不该换算（子端契约此刻未必存在）。
            // 声明只带着成员名走，真到了级联要用那一刻才解，解不出就响。约定那一档同理不在编译期判。
            if (CollectionElementOf(property.Type) is { } child)
            {
                model.Relationships.Add(new RelationshipModel
                {
                    Member = property.Name,
                    TargetType = TypeExpression(child),
                    Members = [.. declared ?? []],
                    Layer = declared is null ? "Convention" : "DataAccessAnnotation",
                    Note = declared is null ? $"集合导航：{property.Name} ⇒ {child.Name}" : $"[ForeignKey(\"{string.Join(",", declared)}\")]",
                    IsCollection = true,
                    Cascade = !IsNoCascade(property, entity),
                });

                continue;
            }

            // 显式声明：写了却没那一列 = 配置错，编译期就得说（反射侧是启动期抛同一句话）。
            if (declared is not null)
            {
                foreach (var member in declared)
                {
                    if (!model.Columns.Any(c => c.Member == member))
                    {
                        outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.ForeignKeyMemberUnmapped, At(property, at),
                            $"引用导航 {property.Name} 引用了未映射的属性 {member}（可能被 [NotMapped] 排除）"));

                        return;
                    }
                }

                model.Relationships.Add(new RelationshipModel
                {
                    Member = property.Name,
                    TargetType = TypeExpression(Core(property.Type)),
                    Members = [.. declared],
                    Layer = "DataAccessAnnotation",
                    Note = $"[ForeignKey(\"{string.Join(",", declared)}\")]",
                });

                continue;
            }

            // 约定：导航属性名 + "Id"。命不中就没有这条关系（不是错误）。
            var convention = property.Name + "Id";

            if (!model.Columns.Any(c => c.Member == convention))
                continue;

            model.Relationships.Add(new RelationshipModel
            {
                Member = property.Name,
                TargetType = TypeExpression(Core(property.Type)),
                Members = [convention],
                Layer = "Convention",
                Note = $"约定：{property.Name} ⇒ {convention}",
            });
        }
    }

    /// <summary>集合导航的元素型：收下 IEnumerable（非泛型那一颗）又不是 string 的形状，
    /// 取它 IEnumerable〈T〉 的那个 T，只有类形态的才算子行（List〈string〉是标签、Dictionary 的元素
    /// 是结构体，给它们出一条边等于让 Add 在根本没有子行的地方找外键）。反射侧同一条。</summary>
    static ITypeSymbol? CollectionElementOf(ITypeSymbol type)
    {
        var core = Core(type);

        if (core.SpecialType == SpecialType.System_String || !ImplementsEnumerable(core))
            return null;

        foreach (var iface in core.AllInterfaces)
        {
            if (iface is { IsGenericType: true, OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T })
            {
                var element = iface.TypeArguments[0];

                return element.TypeKind == TypeKind.Class && element.SpecialType != SpecialType.System_String ? element : null;
            }
        }

        return null;
    }

    /// <summary>[NoCascade] 的两档：打在实体上这一张表的导航都不带人，打在那根集合导航上只有它不带。
    /// 级联缺省为开，所以这里只判退出（反射侧同一条）。</summary>
    static bool IsNoCascade(IPropertySymbol property, INamedTypeSymbol entity)
        => Find(property.GetAttributes(), QuickSqlNoCascade) is not null
           || Find(entity.GetAttributes(), QuickSqlNoCascade) is not null;

    static string[]? ForeignKeyOf(IPropertySymbol property)
        => Find(property.GetAttributes(), DataForeignKey) is { } declared ? Split(Text(declared, 0, "Name")) : null;

    /// <summary>逗号分隔 = 复合外键。空串按"没声明"处理（反射侧同一个答案，于是两侧都会退回约定那一档）。
    /// netstandard2.0 没有 StringSplitOptions.TrimEntries，所以逐条 Trim——结果与反射侧那份一致。</summary>
    static string[]? Split(string? names)
    {
        if (names is null || names.Length == 0)
            return null;

        var members = names.Split(',').Select(static name => name.Trim()).Where(static name => name.Length > 0).ToArray();

        return members.Length > 0 ? members : null;
    }

    static void IndexesOf(INamedTypeSymbol entity, EntityModel model, BuildOutcome outcome, Location at, bool alreadyFailed)
    {
        if (alreadyFailed)
            return;

        foreach (var index in entity.GetAttributes().Where(a => Name(a) == QuickSqlIndex))
        {
            // EF 同形：构造参数就是成员名单，没有名字那一格——索引名一律留给运行期按约定给。
            // 那条约定与反射侧同一句：表名没模板用表名，分表（表名是 {year} 样模板）用类名——
            // 对拍连错误话术一起管。
            var members = Texts(index, 0);
            var indexName = (model.TableName.Contains('{') ? entity.Name : model.TableName)
                            + "_" + string.Join("_", members);

            foreach (var member in members)
            {
                if (!model.Columns.Any(c => c.Member == member))
                {
                    // 反射侧是启动期抛 QuickSqlException；编译期同样只能是错，不能默默少一个索引。
                    outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.IndexMemberUnmapped, at,
                        $"索引 IX_{indexName} 引用了未映射的属性 {member}（可能被 [NotMapped] 排除）"));

                    return;
                }
            }

            model.Indexes.Add(new IndexModel
            {
                Members = [.. members],
                Unique = Bool(index, "IsUnique"),
                Clustered = Bool(index, "Clustered"),
                Descending = Bool(index, "Descending"),
            });
        }
    }

    /// <summary>跳过索引声明抄进模型：类挂在前、列挂按属性次序在后（与反射侧同一份次序）。
    /// 只抄声明：成员 → 列换算、约定名、撞名判定全在运行期那一份实现里。两件事编译期就要红
    /// （与反射侧同一句话）：类挂没写表达式（那一档没有"缺省成员"可言）与
    /// 声明打在没进列清单的属性上（漏映射：导航、[NotMapped] 都算）。</summary>
    static void SkipIndexesOf(INamedTypeSymbol entity, EntityModel model, BuildOutcome outcome, Location at, bool alreadyFailed)
    {
        if (alreadyFailed)
            return;

        foreach (var declared in entity.GetAttributes().Where(a => Name(a) == QuickSqlClickHouseSkipIndex))
            if (Named(declared, "Expression")?.Value is not string { Length: > 0 } expression)
            {
                outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.SkipIndexDeclarationInvalid, at,
                    $"实体 {entity.Name} 有一颗表级跳过索引声明但没有表达式："
                    + "类挂的那一路没有\"缺省成员\"可言，请把表达式写进声明（属性挂那一档才默认用自己的列）"));
            }
            else
            {
                AddSkipIndex(declared, model, null, expression);
            }

        foreach (var property in entity.GetMembers().OfType<IPropertySymbol>())
        foreach (var declared in property.GetAttributes().Where(a => Name(a) == QuickSqlClickHouseSkipIndex))
        {
            // G2 的成员枚举带可访问性/静态/索引器三把筛子（列侧同一链），编译期对齐那一半。
            if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer)
                continue;

            if (!model.Columns.Any(c => c.Member == property.Name))
            {
                outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.SkipIndexDeclarationInvalid, at,
                    $"跳过索引引用了不是列的成员 {property.Name}（实体 {entity.Name}，可能被 [NotMapped] 排除）"));

                continue;
            }

            AddSkipIndex(declared, model, property.Name, Named(declared, "Expression")?.Value as string);
        }
    }

    static void AddSkipIndex(AttributeData declared, EntityModel model, string? member, string? expression)
    {
        model.SkipIndexes.Add(new SkipIndexModel
        {
            Member = member,
            // 列挂且没写表达式 = 就是那颗列：写手不写 Expression 那一格，交缺省 null，
            // 换算（成员 → 列名）留给运行期的同一份实现。
            Expression = expression,
            Name = Named(declared, "Name")?.Value as string,
            Kind = EnumExpression(declared, 0),
            Granularity = Number(declared, -1, "Granularity") ?? 0,
            SetMaxRows = Number(declared, -1, "SetMaxRows") ?? 0,
            NgramSize = Number(declared, -1, "NgramSize") ?? 0,
            BucketSize = Number(declared, -1, "BucketSize") ?? 0,
            HashFunctions = Number(declared, -1, "HashFunctions") ?? 0,
            Seed = Number(declared, -1, "Seed") ?? 0,
        });
    }

    /// <summary>超级表声明抄进模型：类特性给 Subtable 模板、属性特性按次序给 TAG 成员名
    ///（与反射侧同一份次序，TAGS 子句的列序就是它）。只抄声明：成员 → 列换算、占位符校验
    /// 全在运行期 EntityContractFactory 那一份——TAG 引用了不是列的成员在两条路径上
    /// 都是收口那句话，编译期不再另立一份诊断（与 SkipIndex 的编译期拦截不同档，记 notes）。</summary>
    static void StableOf(INamedTypeSymbol entity, EntityModel model)
    {
        var stable = entity.GetAttributes().FirstOrDefault(a => Name(a) == QuickSqlTDengineStable);

        if (stable is null)
            return;

        var tagMembers = new List<string>();

        foreach (var property in entity.GetMembers().OfType<IPropertySymbol>())
        {
            // G2 的成员枚举带可访问性/静态/索引器三把筛子（列侧同一链），编译期对齐那一半。
            if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer)
                continue;

            if (property.GetAttributes().Any(a => Name(a) == QuickSqlTDengineTag))
                tagMembers.Add(property.Name);
        }

        model.Stable = new StableModel
        {
            Subtable = Named(stable, "Subtable")?.Value as string,
            TagMembers = tagMembers,
        };
    }

    /// <summary>Roslyn 的符号层面查不到 init，只能回到语法树。之所以要单独问一句：
    /// init-only 属性写不了，G1 的 SetValue 就无从生成，与其生成一个半残的 schema，不如整条实体退回 G2。</summary>
    static bool IsInitOnly(IPropertySymbol property)
        => property.DeclaringSyntaxReferences.Any(static reference
            => reference.GetSyntax(default) is PropertyDeclarationSyntax { AccessorList: { } accessors }
               && accessors.Accessors.Any(static accessor => accessor.IsKind(SyntaxKind.InitAccessorDeclaration)));

    /// <summary>属性自己的位置更好定位；合成成员没有位置时退回实体。</summary>
    static Location At(ISymbol symbol, Location fallback) => symbol.Locations.FirstOrDefault() ?? fallback;

    /// <summary>取那一格 summary 作备注（只在开关开着时被调用）。自己扫前导 trivia，
    /// 不走 GetDocumentationCommentXml：csc 批编译没装 DocumentationProvider，同一段注释
    /// 在真实 MSBuild 编译里交回空串——"注释在、备注为空、构建还全绿"正是这一档最不能有的失效形状。
    /// 筛子是 kind 与前缀两把并起来，单用一把都缺一档；只截 summary 段来解析、畸形交回 null 而不是抛：
    /// 编译器已有 CS1570 那句，再说一遍只是把一条警告升成一次构建失败。</summary>
    static string? SummaryOf(ISymbol symbol)
    {
        var node = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(default);

        if (node is null)
            return null;

        var source = new StringBuilder();

        foreach (var trivia in node.GetLeadingTrivia())
        {
            var text = trivia.ToString();

            // 相邻两行 /// 是两条 trivia，中间那个换行不属于任何一条；这里补一个分隔，否则
            // 「上一句」与「下一句」会粘成「上一句下一句」（下面的 Fold 会把一段空白折成一个空格）。
            if (text.StartsWith("///", StringComparison.Ordinal) || text.StartsWith("/**", StringComparison.Ordinal)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                source.Append(text).Append(' ');
        }

        // 注释外框（/// 与 /* */ 那一圈、以及多行注释里的 HTML 包壳）不是 XML，摘掉才谈得上解析。
        var body = source.ToString().Replace("///", "").Replace("<!--", "").Replace("-->", "")
                         .Replace("/**", "").Replace("*/", "");

        var from = body.IndexOf("<summary>", StringComparison.Ordinal);

        if (from < 0)
            return null;

        var to = body.IndexOf("</summary>", from, StringComparison.Ordinal);

        if (to < 0)
            return null;

        try
        {
            // 截的那一段必须连着关标签：只取到 </summary> 之前就是 XDocument 眼里的"标签没合上"，
            // 一句写得好好的备注会被 XmlException 判成畸形。
            var fragment = body.Substring(from, to - from + "</summary>".Length);

            return Fold(XDocument.Parse(fragment).Root!.Value);
        }
        catch (XmlException)
        {
            return null;
        }
    }
    /// <summary>折成一行并去首尾空：注释按 C# 惯例是换行缩进写的，而备注要落进一条 COMMENT ON 的
    /// 字符串字面量里——换行留在库里，读回比对就永远差那一格。全空的 summary 与只有 inheritdoc
    /// 而没有 summary 的那一种都交回 null = 没声明，而不是空串。</summary>
    static string? Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pending = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                // 只在已经有内容时才记"欠一个空格"：注释首行紧跟在 summary 标签之后的那段换行不该变成前导空格。
                pending = builder.Length > 0;
                continue;
            }

            if (pending)
            {
                builder.Append(' ');
                pending = false;
            }

            builder.Append(ch);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    static BuildOutcome Blocked(BuildOutcome outcome, Location location, string reason)
    {
        outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.NotGenerated, location,
            $"该实体不走 G1（{reason}），运行期由 G2 反射解析兜住（§12.7 的三级路径）。"));

        return outcome;
    }

    static string GeneratedName(INamedTypeSymbol entity)
    {
        var chain = new List<string>();

        for (ISymbol? current = entity; current is not null; current = current.ContainingType)
            chain.Insert(0, current.Name);

        return "QuickSqlGenerated" + string.Join("_", chain);
    }

    static string TypeExpression(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => TypeExpression(array.ElementType) + "[]",
            INamedTypeSymbol { IsGenericType: true, ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable
                => "global::System.Nullable<" + TypeExpression(nullable.TypeArguments[0]) + ">",
            _ => "global::" + type.ToDisplayString(Qualified),
        };

    static bool IsNullableValue(ITypeSymbol type)
        => type is INamedTypeSymbol { IsGenericType: true, ConstructedFrom.SpecialType: SpecialType.System_Nullable_T };

    static ITypeSymbol Core(ITypeSymbol type) => IsNullableValue(type) ? ((INamedTypeSymbol)type).TypeArguments[0] : type;

    /// <summary>默认格式的 ToDisplayString 把内建类型写成关键字（decimal），那份名单是按全名匹配的，
    /// 于是 decimal 会被判成导航属性并静默少一列。用 Qualified 格式是为了拿到 System.Decimal。</summary>
    static string FullName(ITypeSymbol type) => type.ToDisplayString(Qualified);

    static bool IsMappable(ITypeSymbol type)
    {
        var core = Core(type);

        return core.SpecialType switch
        {
            SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Char or SpecialType.System_IntPtr or SpecialType.System_UIntPtr => true,
            _ => core.TypeKind == TypeKind.Enum || MappedByName.Contains(FullName(core)),
        };
    }

    static string? Name(AttributeData attribute) => attribute.AttributeClass is { } cls ? FullName(cls) : null;

    /// <summary>[SplitTable] 的抄本。档名存的是可直接落进代码的表达式而不是成员名，
    /// 因为写注解的人也可以写 (SplitKind)99——那种值不该在生成期被咽掉：让它在运行期撞到
    /// SplitRouting 那句「未知的分片档」，与反射路径同一句话（而不是靠这里再校一遍）。</summary>
    static SplitModel? SplitOf(IEnumerable<AttributeData> attributes)
    {
        var split = Find(attributes, QuickSqlSplit);

        if (split is null)
            return null;

        return new SplitModel
        {
            Kind = EnumExpression(split, 0),
            Member = Text(split, 1, "Field") ?? "",
            Buckets = (int)(Number(split, -1, "Buckets") ?? 0),
        };
    }

    /// <summary>[DataSource] 的抄本：只抄集群名那一个字符串，没打注解才是 null。
    /// 打了注解却交出空值（[DataSource(null)]）要落成 "" 而不是 null：反射侧那种写法交回的是
    /// DataSourceFacts(null)，两侧撞的是同一句「名字是空的」。在这里咽成 null 就成了
    /// 「G1 当没声明而照跑、G2 启动期抛」——那正是两侧不一致里最难查的那一种。</summary>
    static string? SourceOf(IEnumerable<AttributeData> attributes)
    {
        var source = Find(attributes, QuickSqlDataSource);

        return source is null ? null : Text(source, 0, "Cluster") ?? "";
    }

    /// <summary>把枚举实参写成 global::…SplitKind.Month；按数值找不到成员名时退回强制转换。
    /// 前缀在这里加、不在写手那里加：三种退路（成员名、(枚举)数值、default）里只有前两种
    /// 该带 global::，把它们各自写成完整形态，写手那一格就只管抄。</summary>
    static string EnumExpression(AttributeData attribute, int position)
    {
        var argument = Argument(attribute, position);

        if (argument?.Value is null)
            return "default";

        // 两次 .Value：第一颗摘掉 TypedConstant? 的可空壳，第二颗才是实参那颗装箱的整数
        // （枚举实参在 Roslyn 里存的就是底层整数值，与 Tri 同一件事）。
        var raw = Convert.ToInt64(argument.Value.Value);

        if (argument?.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
                if (member.HasConstantValue && member.ConstantValue is not null && Convert.ToInt64(member.ConstantValue) == raw)
                    return "global::" + FullName(enumType) + "." + member.Name;

            return "(global::" + FullName(enumType) + ")" + raw;
        }

        return raw.ToString();
    }

    static AttributeData? Find(IEnumerable<AttributeData> attributes, string metadataName)
        => attributes.FirstOrDefault(a => Name(a) == metadataName);

    static TypedConstant? Argument(AttributeData? attribute, int position)
        => attribute is not null && position >= 0 && attribute.ConstructorArguments.Length > position ? attribute.ConstructorArguments[position] : null;

    static TypedConstant? Named(AttributeData? attribute, string name)
        => attribute?.NamedArguments.FirstOrDefault(kv => kv.Key == name).Value;

    /// <summary>先按位置读构造参数，读不到再按名字读命名参数——[Column("x")] 与
    /// [Column(Name = "x")] 两种写法因此走同一条判定，与反射侧读到的属性值一致。</summary>
    static string? Text(AttributeData? attribute, int position, string name)
        => Argument(attribute, position)?.Value as string ?? Named(attribute, name)?.Value as string;

    static int? Number(AttributeData? attribute, int position, string name)
        => (int?)Argument(attribute, position)?.Value ?? (int?)Named(attribute, name)?.Value;

    static bool Bool(AttributeData? attribute, string name) => (bool)(Named(attribute, name)?.Value ?? false);

    /// <summary>自研属性的三态：Unset=0 / Yes=1 / No=2。枚举实参在 TypedConstant 里就是底层整数。</summary>
    static int Tri(AttributeData? attribute, string name) => (int)(Named(attribute, name)?.Value ?? 0);

    static List<string> Texts(AttributeData attribute, int position)
        => Argument(attribute, position) is { Kind: TypedConstantKind.Array } array
            ? [.. array.Values.Select(v => (string?)v.Value ?? "")]
            : [];

    static int? Positive(int? value) => value > 0 ? value : null;
}
