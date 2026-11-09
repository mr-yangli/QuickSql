using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using QuickSql.Materialization;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;
using Sc = System.ComponentModel;

namespace QuickSql.Metadata;

/// <summary>注解解析链实现：Fluent > 自研属性 > DataAnnotations > 约定 > provider 兜底。
/// 这是 G2 兜底路径（启动期反射一次、结果冻结）；G1 源生成器产出同样的 EntityContract，
/// 两条路径由对拍用例断言逐字段相等。</summary>
public sealed class ContractResolver(IDbProvider provider, ModelConfiguration? configuration = null,
    bool integralPrimaryKeyAutoIncrement = true, int? defaultStringLength = null)
{
    readonly ConditionalWeakTable<Type, EntityContract> _cache = new();

    public IDbProvider Provider { get; } = provider;

    public ModelConfiguration Configuration { get; } = configuration ?? new ModelConfiguration();

    /// <summary>约定那一档给不给"整型主键 ⇒ 自增"（QuickSqlOrmOptions.IntegralPrimaryKeyAutoIncrement）。
    /// 它只挪约定：注解与 Fluent 表过态的列照旧赢过它。</summary>
    public bool IntegralPrimaryKeyAutoIncrement { get; } = integralPrimaryKeyAutoIncrement;

    /// <summary>orm 级的"未声明宽度"兜底（QuickSqlOrmOptions.DefaultStringLength）：只作用于没有宽度声明
    /// 的 string 列。显式宽度赢过它；显式非正数（[QuickColumn(Length = -1)]、Fluent HasMaxLength(-1)、
    /// [StringLength(-1)]）表示"这一列明确不要上限"，同样不吃它；点名了存储型的列也不吃。
    /// 0 与负数在这里是配置错误而不是"关掉"——关掉就是不设这一格。</summary>
    public int? DefaultStringLength { get; } = defaultStringLength is null or > 0
        ? defaultStringLength
        : throw new ArgumentOutOfRangeException(nameof(defaultStringLength), defaultStringLength,
            "DefaultStringLength 只收正整数：null 表示不设默认（维持未声明 ⇒ provider 大文本），"
            + "要某一列不受默认约束请显式给非正数（[QuickColumn(Length = -1)] 或 Fluent HasMaxLength(-1)）。");

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；AOT 场景要用 G1 生成的契约（§12.7）。")]
    [RequiresDynamicCode("G2 兜底路径为集合导航编译读口委托；AOT 场景要用 G1 生成的契约（§12.7）。")]
    public EntityContract Resolve(Type clrType) => _cache.GetValue(clrType, Build);

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；AOT 场景要用 G1 生成的契约（§12.7）。")]
    [RequiresDynamicCode("G2 兜底路径为集合导航编译读口委托；AOT 场景要用 G1 生成的契约（§12.7）。")]
    public EntityContract Resolve<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        where T : class => Resolve(typeof(T));

    /// <summary>那一根集合边上点名的外键成员（[ForeignKey] 的第二档）。
    /// 找不到那一根边（导航没被契约收录）就给空串，调用方照旧走第一、第三档——这一格不替配置错误编一个"没有声明"。</summary>
    static IReadOnlyList<string> DeclaredFkMembers(EntityContract parent, string memberName)
    {
        foreach (var edge in parent.Collections)
            if (edge.MemberName == memberName)
                return edge.DeclaredForeignKeyMembers;

        return [];
    }

    /// <summary>第二档：把集合导航上点名的外键成员换算成子端的列名；这根导航没有点名就返回 null。
    /// 三处反推外键的路（DbSet.Load、IncludeMany、子查询里的集合导航）与级联落键那一条共用这一颗：
    /// 同一根边不许在两条路径上得到两个答案，否则"级联插得进去、Load 捞不上来"
    /// 会把级联删除那条出路（先捞进会话再删）走成死路。</summary>
    internal static IReadOnlyList<string>? ResolveDeclaredFkColumns(EntityContract parent, EntityContract child, string memberName)
    {
        var declared = DeclaredFkMembers(parent, memberName);

        if (declared.Count == 0)
            return null;

        var keys = parent.PrimaryKey;

        if (declared.Count != keys.Length)
            throw new QuickSqlException(
                $"{child.ClrType.Name} 到 {parent.ClrType.Name} 的导航 {memberName} 点了 {declared.Count} 个外键成员，" +
                $"而父实体有 {keys.Length} 个主键列，数量不匹配 —— 复合主键要逐列点名。");

        var columns = new List<string>(declared.Count);

        foreach (var member in declared)
        {
            if (!child.ByMember.TryGetValue(member, out var column))
                throw new QuickSqlException(
                    $"{child.ClrType.Name} 上没有名为 {member} 的列（{parent.ClrType.Name}.{memberName} 点名的外键成员）：" +
                    "外键成员点了名而子端没有那一列，是配置错误。");

            columns.Add(column.ColumnName);
        }

        return columns;
    }

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；主路径是 G1 源生成。")]
    [RequiresDynamicCode("G2 兜底路径为集合导航编译读口委托；主路径是 G1 源生成。")]
    EntityContract Build(Type clrType)
    {
        var fluent = Configuration.Find(clrType);
        var table = ResolveTable(clrType, fluent);
        var members = ReadMembers(clrType);
        var columns = new List<ColumnFacts>();

        foreach (var member in members)
        {
            if (member.Ignored is null)
                columns.Add(ResolveColumn(member, fluent));
        }

        // 收尾（provider 的 storage defaults、自增约定、索引与外键的列名换算）在 EntityContractFactory 里，
        // 与 G1 生成的事实共用同一段代码：这条链的正确性由两侧逐字段相等的对拍断言兜住。
        return EntityContractFactory.Build(clrType, table, [.. columns], ResolveIndexFacts(clrType, fluent),
            ResolveRelationships(members, columns), Provider.Types, IntegralPrimaryKeyAutoIncrement,
            ResolveSkipIndexes(clrType), DefaultStringLength, ResolveStable(clrType));
    }

    TableInfo ResolveTable(Type clrType, FluentEntity? fluent)
    {
        var data = clrType.GetCustomAttribute<DaSchema.TableAttribute>();

        var name =
            fluent?.TableName is { } fn ? fn
            : data?.Name is { } dn ? dn
            : clrType.Name;

        // 分表只认自研属性那一层（DataAnnotations 里没有等价注解，Fluent 侧今天不落这一格，
        // 理由写在 SplitTableAttribute 的注释上）。名字是哪一层给的不影响这一格：模板名与分片声明
        // 是两件事，把它们绑在同一层会让 [Table(DataAnnotations)] + [SplitTable] 这种组合没法写。
        var split = clrType.GetCustomAttribute<SplitTableAttribute>();

        // 集群归属与分表同一取向：只认自研属性那一层、不落 Fluent。落 Fluent 的那一条路会让
        // "同一张表在两条路径上属于两座库"有能发生的形状，而那一格的代价是语句发到另一座库里去。
        var source = clrType.GetCustomAttribute<DataSourceAttribute>();

        // 表备注与表名同一条优先级链（Fluent > 注解 > 文档兜底），只是最后一层只长在 G1（xmlDoc 开关）。
        // 系统的 TableAttribute 只有 Name/Schema 两格，备注那一格由组件模型的 [Description]
        // （System.ComponentModel）提供，座次排在 Fluent 之后。
        var comment = fluent?.Comment ?? clrType.GetCustomAttribute<Sc.DescriptionAttribute>()?.Description;

        return new TableInfo(fluent?.Schema ?? data?.Schema, name,
            split is null ? null : new SplitFacts(split.Kind, split.Field, split.Buckets),
            source is null ? null : new DataSourceFacts(source.Cluster),
            comment);
    }

    [RequiresUnreferencedCode("按 Type 扫描公共属性。")]
    [RequiresDynamicCode("集合导航的读口要编译委托。")]
    List<MemberFacts> ReadMembers(Type clrType)
    {
        var result = new List<MemberFacts>();
        var nullability = new NullabilityInfoContext();

        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var facts = new MemberFacts(property)
            {
                QuickColumn = property.GetCustomAttribute<QuickColumnAttribute>(),
                DataColumn = property.GetCustomAttribute<DaSchema.ColumnAttribute>(),
            };

            if (property.GetCustomAttribute<DaSchema.NotMappedAttribute>() is not null)
                facts.Ignored = "[NotMapped]";
            else if (!property.CanWrite)
                // 没有写口的成员既落不回值（读取器给它赋值会在编译当场 ArgumentException:
                // Expression must be writeable），也不是数据：public bool X => Type == … 那一形是算法。
                // 按非公开写口问一次（CanWrite）而不是按公共写口：{ get; protected set; } 与 { get; init; }
                // 都是能落的形状（编译后的表达式树不受可访问性限制），
                // 把它们一起摘掉会让整族实体的键与明细列凭空消失。
                facts.Ignored = "没有写口（计算属性，不是列）";
            else if (property.PropertyType == typeof(byte[]) || IsMappable(property.PropertyType))
                facts.Nullability = nullability.Create(property).WriteState;
            else if (IsReferenceNavigation(property.PropertyType))
            {
                // 引用导航不是列，但它是关系：外键列在依赖侧这一张表上。把它连同别的不可映射类型
                // 一起丢掉的话，写侧连"谁依赖谁"都无从知道。
                facts.Ignored = "引用导航（不是列）";
                facts.Reference = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                facts.ForeignKeyMembers = ForeignKeyOf(property);
            }
            else if (property.CanRead && CollectionElementOf(property.PropertyType) is { } element)
            {
                // 集合导航也不是列，但它是朝反了的那一根关系（父端 → 子端，外键在子端那张表上）。
                // 丢掉它 = 级联没有边可走，于是 Add 不带子实体、Remove 留一串孤行。
                facts.Ignored = "集合导航（不是列）";
                facts.Collection = element;
                facts.ForeignKeyMembers = ForeignKeyOf(property);
            }
            else
                facts.Ignored = "非可映射类型";

            result.Add(facts);
        }

        return result;
    }

    static bool IsMappable(Type type)
    {
        var core = Nullable.GetUnderlyingType(type) ?? type;
        return core.IsPrimitive
               || core == typeof(string)
               || core == typeof(decimal)
               || core == typeof(Guid)
               || core == typeof(DateTime)
               || core == typeof(DateTimeOffset)
               || core == typeof(DateOnly)
               || core == typeof(TimeOnly)
               || core == typeof(TimeSpan)
               || core.IsEnum;
    }

    /// <summary>引用导航 = 类类型、不是 string、不是集合形态（数组与 List 走 CollectionElementOf 那一格，
    /// 成为朝反了的边）。判定不要求目标类型是实体——一个 Uri 属性配上 UriId 列也会算出一条边，
    /// 而这条边今天唯一的用法（写序）对没有跟踪档的类型天然无效。G1 侧同一条判据。</summary>
    static bool IsReferenceNavigation(Type type)
    {
        var core = Nullable.GetUnderlyingType(type) ?? type;

        return core.IsClass && core != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(core);
    }

    /// <summary>集合导航的元素型：收下了 IEnumerable（非泛型）又不是 string、也不是 byte[]（那两格在上面
    /// 那条链里已经是列）的形状，取它 IEnumerable&lt;T&gt; 的那个 T。只有类形态的元素才算集合导航：
    /// List&lt;string&gt;、List&lt;int&gt;、Dictionary（元素是 KeyValuePair 结构体）这些都是标签或配置，
    /// 不是一串子行，给它们出一条边会让 Add 在根本不该级联的地方找外键。两侧同一判据（G1 侧同名）。</summary>
    [RequiresUnreferencedCode("按运行期 Type 反射判集合元素型。")]
    [RequiresDynamicCode("按运行期 Type 反射判集合元素型。")]
    static Type? CollectionElementOf(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
            return null;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var element = iface.GetGenericArguments()[0];

                return element.IsClass && element != typeof(string) ? element : null;
            }
        }

        return null;
    }

    /// <summary>[NoCascade] 的两档：打在实体上是"这一张表的导航都不带人"，打在具体那根集合导航上
    /// 是"只有这一根不带"。级联缺省为开，所以这里只判退出（G1 侧同一条，注解读的是源码符号）。</summary>
    static bool IsNoCascade(MemberFacts member)
        => member.Property.GetCustomAttribute<NoCascadeAttribute>() is not null
           || member.Property.DeclaringType!.GetCustomAttribute<NoCascadeAttribute>() is not null;

    /// <summary>[ForeignKey] 只读导航属性上那一颗（反向标注在属性上、指向导航的那一种形态没有实现）。
    /// 逗号分隔 = 复合外键，两侧同一句拆分。</summary>
    static string[]? ForeignKeyOf(PropertyInfo property)
        => property.GetCustomAttribute<DaSchema.ForeignKeyAttribute>() is { } declared ? Split(declared.Name) : null;

    /// <summary>空串按"没声明"处理（G1 侧同一个答案，于是两侧都退回约定那一档）。</summary>
    static string[]? Split(string? names)
    {
        if (string.IsNullOrEmpty(names))
            return null;

        var members = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return members.Length > 0 ? members : null;
    }

    /// <summary>声明折成 RelationshipFacts，与索引同一取向：列名换算、「外键指向未映射属性」那条拒绝
    /// 都在 EntityContractFactory，只有一份。约定那一档要先自己确认属性在场：命不中就根本没有关系（不是错误），
    /// 而 [ForeignKey] 写了却没那一列 = 配置错误，交给收尾那条消息抛出去。集合那一格另算：
    /// 它的外键成员在子端那张表上，这里既换算不了、也不该拿本实体的列清单去比，所以声明只带着成员名
    /// 原样走（没声明就是空数组，运行期级联那一处再解），而级联要的那把读口在这里编译一次——
    /// 启动期反射、级联每行只调委托。</summary>
    [RequiresUnreferencedCode("集合导航的读口按运行期类型与成员名定位属性。")]
    [RequiresDynamicCode("集合导航的读口是编译出来的表达式树。")]
    static IReadOnlyList<RelationshipFacts> ResolveRelationships(List<MemberFacts> members, List<ColumnFacts> columns)
    {
        var result = new List<RelationshipFacts>();

        foreach (var member in members)
        {
            if (member.Collection is { } child)
            {
                result.Add(new RelationshipFacts
                {
                    MemberName = member.Property.Name,
                    TargetType = child,
                    ForeignKeyMembers = member.ForeignKeyMembers ?? [],
                    Layer = member.ForeignKeyMembers is null ? MetadataLayer.Convention : MetadataLayer.DataAccessAnnotation,
                    Note = member.ForeignKeyMembers is { } named
                        ? $"[ForeignKey(\"{string.Join(",", named)}\")]"
                        : $"集合导航：{member.Property.Name} ⇒ {child.Name}",
                    IsCollection = true,
                    Cascade = !IsNoCascade(member),
                    NavigationReader = MemberAccessors.Getter(member.Property.DeclaringType!, member.Property.Name,
                        $"实体 {member.Property.DeclaringType!.Name} 上没有属性 {member.Property.Name}"),
                    CascadeEnter = MemberAccessors.CascadeEnter(member.Property.DeclaringType!, child, member.Property.Name,
                        $"实体 {member.Property.DeclaringType!.Name} 上没有可读的集合属性 {member.Property.Name}"),
                });

                continue;
            }

            if (member.Reference is not { } target)
                continue;

            if (member.ForeignKeyMembers is { } declared)
            {
                result.Add(new RelationshipFacts
                {
                    MemberName = member.Property.Name,
                    TargetType = target,
                    ForeignKeyMembers = declared,
                    Layer = MetadataLayer.DataAccessAnnotation,
                    Note = $"[ForeignKey(\"{string.Join(",", declared)}\")]",
                });

                continue;
            }

            // 约定：导航属性名 + "Id"。G1 侧同一句（EntityFactsBuilder.RelationshipsOf）。
            var convention = member.Property.Name + "Id";

            if (!columns.Exists(c => string.Equals(c.MemberName, convention, StringComparison.Ordinal)))
                continue;

            result.Add(new RelationshipFacts
            {
                MemberName = member.Property.Name,
                TargetType = target,
                ForeignKeyMembers = [convention],
                Layer = MetadataLayer.Convention,
                Note = $"约定：{member.Property.Name} ⇒ {convention}",
            });
        }

        return [.. result];
    }

    ColumnFacts ResolveColumn(MemberFacts member, FluentEntity? fluent)
    {
        var property = member.Property;
        var name = property.Name;
        var fluentMember = fluent?.Members.GetValueOrDefault(name);
        var quickColumn = member.QuickColumn;
        var dataColumn = member.DataColumn;

        var required = property.GetCustomAttribute<Da.RequiredAttribute>();
        var stringLength = property.GetCustomAttribute<Da.StringLengthAttribute>();
        var maxLength = property.GetCustomAttribute<Da.MaxLengthAttribute>();

        // 同层冲突：StringLength 与 MaxLength 都在 DataAnnotations 层。取严格者会把
        // 「建表按 20、校验按 50」这种三个月后才现形的 bug 藏起来，所以直接抛。
        if (stringLength is not null && maxLength is not null)
            throw new MetadataConflictException(name, [$"[StringLength({stringLength.MaximumLength})]", $"[MaxLength({maxLength.Length})]"]);

        (string Value, MetadataLayer Layer, string Note) columnName =
            fluentMember?.ColumnName is { } fc ? (fc, MetadataLayer.Fluent, "Fluent HasColumnName")
            : dataColumn?.Name is { } dc ? (dc, MetadataLayer.DataAccessAnnotation, $"[Column(\"{dc}\")]")
            : (name, MetadataLayer.Convention, "约定：属性名");

        // 宽度的三态在这里成形：写了的（含 0 与负数）原样进事实，"未声明"的那一档才是 null。
        // 非正数 = 声明者明确说过"这一列不要上限"，收尾时因此不吃 orm 的默认长度。
        (int Value, MetadataLayer Layer, string Note) length =
            fluentMember?.MaxLength is { } fl ? (fl, MetadataLayer.Fluent, $"Fluent HasMaxLength({fl})")
            : quickColumn?.Length is { } qcl ? (qcl, MetadataLayer.QuickSqlAttribute, $"[QuickColumn(Length={qcl})]")
            : stringLength is not null ? (stringLength.MaximumLength, MetadataLayer.DataAccessAnnotation, $"[StringLength({stringLength.MaximumLength})]")
            : maxLength is not null ? (maxLength.Length, MetadataLayer.DataAccessAnnotation, $"[MaxLength({maxLength.Length})]")
            : (0, MetadataLayer.ProviderFallback, "未声明，交给 provider 默认");

        var nullable = ResolveNullable(member, required, quickColumn, fluentMember);

        var isKey = fluentMember?.IsKey == true
                    || quickColumn?.IsPrimary == TriState.Yes
                    || property.GetCustomAttribute<Da.KeyAttribute>() is not null;

        var identity = fluentMember?.IsIdentity
                       ?? quickColumn?.IsIdentity switch
                       {
                           TriState.Yes => true,
                           TriState.No => false,
                           _ => null,
                       };

        int? precision = fluentMember?.Precision ?? (quickColumn is { Precision: > 0 } p ? p.Precision : null);
        int? scale = fluentMember?.Scale ?? (quickColumn is { Scale: > 0 } s ? s.Scale : null);

        var storeType = fluentMember?.StoreTypeName ?? dataColumn?.TypeName;

        // 备注的两档注解：组件模型的 [Description]（System.ComponentModel）座次排在 Fluent 之后；
        // DataAnnotations 的 [Display]（描述/显示名）接在它后面作兜底。
        var description = property.GetCustomAttribute<Sc.DescriptionAttribute>()?.Description;
        var display = property.GetCustomAttribute<Da.DisplayAttribute>();
        var comment = fluentMember?.Comment ?? description ?? display?.Description ?? display?.Name;

        // 默认值的两档：Fluent 的 HasDefaultValue 在上，组件模型的 [DefaultValue]（System.ComponentModel）
        // 在下 —— 与备注那条链同一个座次。值域与类型相容的门禁不在这一格跑：它在 EntityContractFactory
        // 收尾那一格（G1 ∶ G2 两侧共用一份），这里只把声明抄进事实。
        var defaultValue = fluentMember?.DefaultValue ?? property.GetCustomAttribute<Sc.DefaultValueAttribute>()?.Value;

        // 并发列的来源：自研属性与 Fluent（DataAnnotations 那一层没有等价注解——带这个语义的注解
        // 住在 EF Core 的程序集里，引它等于引 EF Core），再加一条成员名约定：ConcurrencyStamp 那一族
        // 的现成拼写直接认成应用给值的并发列，那一族实体一个注解都不用加。
        // 显式声明压在约定之上：打了 [ConcurrencyToken] 的列按声明那一档算（默认只比较不书写），
        // 要第三族就明说（AppSetsValue 参数 / Fluent 同名口）。
        var tokenAttribute = property.GetCustomAttribute<ConcurrencyTokenAttribute>();
        var token = fluentMember?.IsConcurrencyToken
                    ?? (tokenAttribute is null ? (bool?) null : true)
                    ?? (name == "ConcurrencyStamp" ? (bool?) true : null);

        // 应用给值那一族（第三族）：显式声明（特性的 AppSetsValue 参数或 Fluent 的同名口）优先；
        // 没显式说过时按同一条名字约定 —— 约定认出来的那一列就是应用给值的那一族。
        var appSets = fluentMember?.AppSetsValue
                      ?? tokenAttribute?.AppSetsValue
                      ?? (name == "ConcurrencyStamp" ? (bool?) true : null);

        return new ColumnFacts
        {
            MemberName = name,
            ClrType = property.PropertyType,
            ColumnName = columnName.Value,
            ColumnNameLayer = columnName.Layer,
            ColumnNameNote = columnName.Note,
            MaxChars = length.Layer == MetadataLayer.ProviderFallback ? null : length.Value,
            LengthNote = length.Note,
            IsNullable = nullable.Value,
            NullableNote = nullable.Note,
            // 只有"约定"这一档且读回来是没表过态（Unknown）时，这句话不算声明 —— 见 ColumnFacts 那一格。
            NullabilityDeclared = nullable.Declared,
            IsPrimary = isKey,
            DeclaredIdentity = identity,
            Precision = precision,
            Scale = scale,
            StoreTypeName = storeType,
            Comment = comment,
            DefaultValue = defaultValue,
            IsConcurrencyToken = token == true,
            AppSetsValue = token == true && appSets == true,
            ConcurrencyNote = token == true
                ? fluentMember?.IsConcurrencyToken == true ? "Fluent IsConcurrencyToken" : "[ConcurrencyToken]"
                : null,
        };
    }

    static (bool Value, MetadataLayer Layer, string Note, bool Declared) ResolveNullable(
        MemberFacts member, Da.RequiredAttribute? required, QuickColumnAttribute? quickColumn, FluentMember? fluentMember)
    {
        if (fluentMember?.Required is { } fr)
            return (!fr, MetadataLayer.Fluent, $"Fluent IsRequired({fr})", true);

        if (quickColumn?.IsPrimary == TriState.Yes)
            return (false, MetadataLayer.QuickSqlAttribute, "[QuickColumn(IsPrimary=Yes)] ⇒ 非空", true);

        if (quickColumn?.IsNullable is TriState.Yes or TriState.No)
        {
            var isNullable = quickColumn.IsNullable == TriState.Yes;

            return (isNullable, MetadataLayer.QuickSqlAttribute, isNullable
                ? required is not null
                    ? "WARN: [Required] 与 [QuickColumn(IsNullable=Yes)] 矛盾，按优先级取自研属性"
                    : "[QuickColumn(IsNullable=Yes)]"
                : "[QuickColumn(IsNullable=No)]", true);
        }

        if (required is not null)
            return (false, MetadataLayer.DataAccessAnnotation, "[Required]", true);

        var type = member.Property.PropertyType;

        if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            return (false, MetadataLayer.Convention, "值类型非 Nullable ⇒ 非空列", true);

        // Unknown = 这段代码根本不在可空上下文里（#nullable disable、没带可空元数据的引用程序集——
        // 整份框架代码天天在这一格）。它说的是"我没表过态"，不是"我说了非空"，所以列的形状照旧按非空建
        // （改那一格等于替现库批量 DROP NOT NULL），而必填尺子不量它。
        // 编译期那侧的同一格是 NullableAnnotation.None，两侧同形由对拍钉住。
        if (member.Nullability == NullabilityState.Unknown)
            return (false, MetadataLayer.Convention, "C# 没表过态（可空上下文之外）⇒ 建表按非空列，不落必填尺子", false);

        return (member.Nullability == NullabilityState.Nullable, MetadataLayer.Convention,
            member.Nullability == NullabilityState.Nullable ? "C# 可空 ⇒ 可空列" : "C# 非空 ⇒ 非空列", true);
    }

    /// <summary>只把声明抄成 IndexFacts：名字/量纲的换算、同名冲突判定都在 EntityContractFactory，
    /// 与 G1 生成的那条共用一份。顺序即优先级，所以 Fluent 必须排在注解后面。</summary>
    [RequiresUnreferencedCode("读取 [Index] 注解。")]
    IReadOnlyList<IndexFacts> ResolveIndexFacts(Type clrType, FluentEntity? fluent)
    {
        var facts = new List<IndexFacts>();

        foreach (var index in clrType.GetCustomAttributes<IndexAttribute>())
            facts.Add(new IndexFacts(null, [.. index.MemberNames], index.IsUnique, index.Clustered, index.Descending));

        foreach (var index in fluent?.Indexes ?? [])
            facts.Add(new IndexFacts(index.Name, index.MemberNames, index.Unique, Descending: index.Descending));

        return [.. facts];
    }

    /// <summary>TDengine 超级表声明抄进事实：类特性给 Subtable 模板、属性特性给 TAG 成员名
    /// （声明序即 TAGS 子句的列序）。成员枚举的筛子与 ResolveSkipIndexes 同一把
    ///（公开实例非索引器），只抄声明——换算与占位符校验收在 EntityContractFactory 一份。</summary>
    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；主路径是 G1 源生成。")]
    StableFacts? ResolveStable(Type clrType)
    {
        var stable = clrType.GetCustomAttribute<TDengineStableAttribute>();

        if (stable is null)
            return null;

        var members = new List<string>();

        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetCustomAttribute<TDengineTagAttribute>() is not null && property.GetIndexParameters().Length == 0)
                members.Add(property.Name);

        return new StableFacts { SubtableTemplate = stable.Subtable, TagMemberNames = [.. members] };
    }

    /// <summary>跳过索引声明只认自研注解那一层（Fluent 侧今天不落这一格）。类挂与属性挂两条来路
    /// 收进同一份名单按声明序排：表级在前、列级在后，与 G1 生成器的次序一致——顺序也算对拍的内容。
    /// 抄声明而已：成员换算与约定名都在收尾那一份里。</summary>
    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；主路径是 G1 源生成。")]
    IReadOnlyList<SkipIndexFacts> ResolveSkipIndexes(Type clrType)
    {
        var facts = new List<SkipIndexFacts>();

        foreach (var declared in clrType.GetCustomAttributes<ClickHouseSkipIndexAttribute>())
        {
            // 类挂没有"缺省成员"可言：那一档没写表达式让收尾去红，比在这里悄悄丢掉声明诚实
            // —— 收尾那一句话对两侧是同一句。
            facts.Add(new SkipIndexFacts
            {
                Expression = declared.Expression,
                Name = declared.Name,
                Kind = declared.Kind,
                Granularity = declared.Granularity,
                SetMaxRows = declared.SetMaxRows,
                NgramSize = declared.NgramSize,
                BucketSize = declared.BucketSize,
                HashFunctions = declared.HashFunctions,
                Seed = declared.Seed,
            });
        }

        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        foreach (var declared in property.GetCustomAttributes<ClickHouseSkipIndexAttribute>())
        {
            facts.Add(new SkipIndexFacts
            {
                MemberName = property.Name,
                Expression = declared.Expression,
                Name = declared.Name,
                Kind = declared.Kind,
                Granularity = declared.Granularity,
                SetMaxRows = declared.SetMaxRows,
                NgramSize = declared.NgramSize,
                BucketSize = declared.BucketSize,
                HashFunctions = declared.HashFunctions,
                Seed = declared.Seed,
            });
        }

        return [.. facts];
    }

    sealed class MemberFacts(PropertyInfo property)
    {
        public PropertyInfo Property { get; } = property;

        public QuickColumnAttribute? QuickColumn { get; init; }

        public DaSchema.ColumnAttribute? DataColumn { get; init; }

        public string? Ignored { get; set; }

        /// <summary>反射侧读的是 NullabilityInfoContext 的 WriteState，三态原样留着：
        /// Unknown（可空上下文之外，没表过态）与 NotNull（表过态说非空）在必填尺子上是两个答案。
        /// 缺省取 NotNull——走不到这一格的成员本来就不是列。</summary>
        public NullabilityState Nullability { get; set; } = NullabilityState.NotNull;

        /// <summary>非 null = 这一格是引用导航，值为主端实体的 CLR 类型。它与 Ignored 同时存在是故意的：
        /// 「不是列」与「是一条关系」是两个问题，各自有各自的答案。</summary>
        public Type? Reference { get; set; }

        /// <summary>非 null = 这一格是集合导航，值为元素型（子端 CLR 型）。与 Reference 互斥：
        /// 一根导航的型要么是引用要么是集合，判定链上先问引用。</summary>
        public Type? Collection { get; set; }

        /// <summary>只有 [ForeignKey] 给过名字才非 null；约定那一档在 ResolveRelationships 里判。</summary>
        public string[]? ForeignKeyMembers { get; set; }
    }
}
