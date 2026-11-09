using QuickSql.Providers;

namespace QuickSql.Metadata;

/// <summary>解析链在 provider 之前产出的列事实——G1 与 G2 的公共接口。
/// 反射侧由解析器填，G1 由源生成器把同一形状写成代码；
/// 之后 EntityContractFactory 只做一次收尾，所以 origin 文本模板、storage defaults
/// 与"整型主键即自增"约定全仓库只有一份实现。</summary>
public sealed record ColumnFacts
{
    public required string MemberName { get; init; }

    public required Type ClrType { get; init; }

    public required string ColumnName { get; init; }

    /// <summary>列名来自哪一层。整条 Origin 也取这一层（以最高层为准）。</summary>
    public required MetadataLayer ColumnNameLayer { get; init; }

    public required string ColumnNameNote { get; init; }

    /// <summary>字符上限；null = 未声明，由 provider 兜底。</summary>
    public required int? MaxChars { get; init; }

    public required string LengthNote { get; init; }

    public required bool IsNullable { get; init; }

    public required string NullableNote { get; init; }

    /// <summary>这一列的"非空"究竟是谁说的。默认 true（Fluent、自研 Column、Required、
    /// 可空上下文里的 string 都是表过态）。唯一落 false 的是可空上下文之外那一格：
    /// #nullable disable 或没带可空元数据的引用程序集（整份 ABP/DDD 框架代码天天在这一格上），
    /// 那里读出来的是"我没说过"，不是"我说了非空"。
    /// 它只摘掉必填尺子、不改列的形状：建表照旧发非空列，动那一格等于替现库批量
    /// DROP NOT NULL，而那不是这句话的本意。</summary>
    public bool NullabilityDeclared { get; init; } = true;

    public required bool IsPrimary { get; init; }

    /// <summary>null = 没有任何一层显式说过自增与否，交给"整型主键即自增"约定（要等 provider 定完类型才能判）。</summary>
    public bool? DeclaredIdentity { get; init; }

    public int? Precision { get; init; }

    public int? Scale { get; init; }

    public string? StoreTypeName { get; init; }

    public string? Comment { get; init; }

    /// <summary>列默认值（ColumnContract.DefaultValue 的声明侧）。Fluent 的 HasDefaultValue 与
    /// DataAnnotations 的 DefaultValue 两档，G1 生成期把字面量写成代码、G2 反射读注解——两侧同形，
    /// 门禁只有收尾那一格（值域、类型相容、与自增/主键的冲突），那里两侧共用一份实现。</summary>
    public object? DefaultValue { get; init; }

    /// <summary>并发列标记。缺省 false 而不是 required：这一格没有"没表过态"这一态
    /// （不像自增——那一个要等 provider 定完类型才能判），而两侧漏产出的后果由两侧逐字段对拍兜住。</summary>
    public bool IsConcurrencyToken { get; init; }

    /// <summary>应用给值那一族（ColumnContract.AppSetsValue 的声明侧）。
    /// 缺省 false；只有 IsConcurrencyToken 为真时才被采信。</summary>
    public bool AppSetsValue { get; init; }

    /// <summary>并发标记来自哪一层，只用于拼 Origin（打了标记才拼进去，
    /// 于是没打标记的实体那串文本一字不动——黄金样本与 Explain() 的既有断言不该为这一格重抄）。</summary>
    public string? ConcurrencyNote { get; init; }

    /// <summary>补上"provider 才知道的事实"之前的那一半：量纲与 Unicode 用中性默认值，
    /// 由 ITypeMap.ApplyStorageDefaults 覆写（量纲由库决定，不由注解决定）。
    /// 非正数的宽度在这里折成"未声明"：StringLength(-1) 这一类声明想说的是"不给上限"，
    /// 而负数一路走到发射侧就是过不了语法关的 DDL。BCL 不替我们收：StringLengthAttribute 的
    /// 构造函数照收 -1、只在有人真调用 IsValid 时才响——建契约这一路一路绿灯，直到发射 DDL
    /// 或首次校验才现形；两条路（源码生成与反射）在这里同形，所以收口只能收在这一个点上，
    /// 各家发射侧与校验侧因此都拿不到负数。</summary>
    /// <summary>字符上限的三态：&gt; 0 是声明的宽度；&lt;= 0 是显式"无宽度"（声明者明确说过不要上限，
    /// 因此不吃 orm 的默认长度）；null 是未表态——这一档才吃 <paramref name="defaultStringLength"/>。
    /// 只有字符串列吃默认，且点名了存储型（<see cref="StoreTypeName"/>）的列不吃：那一列的形状
    /// 已经由调用方自己钉死（例如 nvarchar(max)），再塞一个契约宽度只会让写侧长度尺子拒掉列装得下的值。
    /// 可空那一格与非正数收口都留在这一处，G1/G2 两条路因此拿到同一份结论。</summary>
    public ColumnContract ToProvisional(TableInfo table, int? defaultStringLength = null)
    {
        var fallback = ClrType == typeof(string) && string.IsNullOrEmpty(StoreTypeName) ? defaultStringLength : null;
        var width = MaxChars is > 0 ? MaxChars : MaxChars is null ? fallback : null;

        return new()
        {
            MemberName = MemberName,
            ClrType = ClrType,
            ColumnName = ColumnName,
            LengthSemantics = LengthSemantics.Char,
            Unicode = true,
            IsNullable = IsNullable,
            NullabilityDeclared = NullabilityDeclared,
            IsPrimary = IsPrimary,
            IsIdentity = false,
            IsConcurrencyToken = IsConcurrencyToken,
            AppSetsValue = IsConcurrencyToken && AppSetsValue,
            MaxChars = width,
            MaxBytes = null,
            Precision = Precision,
            Scale = Scale,
            StoreTypeName = StoreTypeName,
            Comment = Comment,
            DefaultValue = DefaultValue,
            Origin = new OriginTrace(ColumnNameLayer, $"{table.Name}.{ColumnName} ← {ColumnNameNote}; 长度 ← {LengthNote}"
                + (MaxChars is <= 0 ? "（非正数 ⇒ 显式无宽度，不吃默认长度）" : string.Empty)
                + (MaxChars is null && width is { } applied ? $"（未声明 ⇒ 默认长度 {applied}）" : string.Empty)
                + $"; 可空 ← {NullableNote}"
                + (IsConcurrencyToken ? $"; 并发 ← {ConcurrencyNote}" : string.Empty)),
        };
    }
}

/// <summary>索引在两侧的声明形状：Index 注解与 Fluent 的 HasIndex 都折成它，
/// 于是"同名覆盖、同名不同形抛"这条规则也只有一份。优先级由给出的顺序表达（低在前），
/// 而不是再加一个用不上的层字段。</summary>
public sealed record IndexFacts(
    string? Name,
    IReadOnlyList<string> MemberNames,
    bool Unique,
    bool Clustered = false,
    bool Descending = false);

/// <summary>导航在两侧的声明形状：反射侧由解析器填，G1 由源生成器写成代码。
/// 与 IndexFacts 同一取向——只抄声明，成员名到列名的换算与"外键指向没映射的属性"
/// 那条拒绝都只有 EntityContractFactory 一份。</summary>
public sealed record RelationshipFacts
{
    /// <summary>导航属性名（Order.Customer）。它自己不进列清单。</summary>
    public required string MemberName { get; init; }

    /// <summary>主端实体的 CLR 类型。只有类型、没有被引用侧的列名：那一半要另一份契约才算得出来，
    /// 而今天唯一的消费者（写侧的依赖排序）只要这一条边。</summary>
    public required Type TargetType { get; init; }

    /// <summary>依赖侧的外键成员名。一颗 ForeignKey("A,B") 折成两条，复合与单列走同一条判定。
    /// 集合导航那一档这里装的是子端的成员名（ForeignKey 打在集合导航上，EF 同一口径），
    /// 所以那一档不能拿本实体的列清单去换算，空数组说"没有声明、交给运行期解算"。</summary>
    public required IReadOnlyList<string> ForeignKeyMembers { get; init; }

    /// <summary>外键成员名来自哪一层：自研属性那一层今天没有等价注解，所以只有 DataAnnotations 与约定两档。</summary>
    public required MetadataLayer Layer { get; init; }

    public required string Note { get; init; }

    /// <summary>true = 这是一根集合导航（父端到子端，如 Order.Items）。它不进 Relationships
    /// 而是进 Collections：两根边的外键住在不同那张表上，混在一格里会让每一个只认 to-one 的消费者
    /// （写序、Include、外键列改写）拿子端的名字去查本实体的列清单。</summary>
    public bool IsCollection { get; init; }

    /// <summary>级联开关，缺省开。NoCascade 打在实体或这一根导航上时折成 false。</summary>
    public bool Cascade { get; init; } = true;

    /// <summary>集合导航的读口（父实体到集合位当时的值），只有集合那一档带。
    /// 级联走这把委托而不是现场反射：G1 由生成器写成 lambda，G2 由反射侧在启动期编译一次
    /// （加载可以反射，调用不行）。</summary>
    public Func<object, object?>? NavigationReader { get; init; }

    /// <summary>级联的入口那一格，同样只有集合那一档带：它拿会话交出的 ICascadeSink
    /// 把这一根导航里的子实体送去建档，而子类型在写这一格的代码里是编译期已知的。
    /// 为什么不能由会话自己 MakeGenericType，理由在 ICascadeSink 的注释上。</summary>
    public Action<QuickSql.Tracking.ICascadeSink, object>? CascadeEnter { get; init; }
}

/// <summary>事实到 EntityContract 的唯一收尾路径。</summary>
public static class EntityContractFactory
{
    /// <summary>把两侧声明收成一份契约。indexes 按优先级从低到高给出（注解在前、Fluent 在后），
    /// 同名时后者覆盖前者；relationships 的成员名到列名在这里换算、与索引同一个出口，
    /// 所以"外键指向了一个没映射的属性"在两条路径上是同一句话。
    /// integralPrimaryKeyAutoIncrement 是约定那一档给不给"整型主键即自增"——false 是给
    /// "主键由客户端生成"那一族库（雪花编号）留的装配声明，它只挪约定，注解与 Fluent
    /// 表过态的列照旧赢过它。defaultStringLength 是 orm 级的"未声明宽度"兜底（三态规则见
    /// <see cref="ColumnFacts.ToProvisional"/>），显式宽度、显式非正数与点名了存储型的列都不吃它。
    /// skipIndexes 的声明里没有名字的走约定名，换算与撞名判定
    /// 都只有 MapSkipIndexes 一份。stable（TDengine 超级表声明）的成员到列换算与
    /// 子表名模板的占位符校验同格只有 MapStable 一份。</summary>
    public static EntityContract Build(Type clrType, TableInfo table, IReadOnlyList<ColumnFacts> columns, IReadOnlyList<IndexFacts> indexes,
        IReadOnlyList<RelationshipFacts> relationships, ITypeMap types, bool integralPrimaryKeyAutoIncrement = true,
        IReadOnlyList<SkipIndexFacts>? skipIndexes = null, int? defaultStringLength = null, StableFacts? stable = null)
    {
        var resolved = new ColumnContract[columns.Count];

        for (var i = 0; i < columns.Count; i++)
            resolved[i] = Complete(columns[i], table, types, integralPrimaryKeyAutoIncrement, defaultStringLength);

        if (!Array.Exists(resolved, static c => c.IsPrimary))
            ApplyKeyConvention(resolved, clrType.Name, integralPrimaryKeyAutoIncrement);

        CheckDefaultAgainstKeys(resolved);

        // 分表这一格要等列算完才判得动（"分片键指向了一颗没映射的属性"要拿列清单比），
        // 而它一改就得改 table 本身，所以放在这里、在契约成形之前。
        if (table.Split is { } split)
            table = table with { Split = CompleteSplit(split, resolved, table, clrType.Name) };

        // 集群归属只有名字要判（连接串的事判不到这一层，那是组装根的启动判据），
        // 而名字判完就得落回 table：两侧（G1 生成的事实、G2 反射读到的注解）从这里同一处收口。
        if (table.Source is { } source)
            table = table with { Source = source with { Name = DataSourceRouting.ValidateName(source.Name, clrType.Name) } };

        var (edges, collections) = MapRelationships(relationships, resolved);

        // 约定名的底子与 [Index] 同一颗：名字没有模板的表用表名，分表的表名是 {year} 样模板，
        // 按它算名物理表还没成形，取类名（镜像 ShardLog 的名字）。
        var skip = MapSkipIndexes(skipIndexes, resolved,
            table.Split is null ? table.Name : clrType.Name, clrType.Name);

        var stableDeclaration = MapStable(stable, resolved, clrType.Name);

        // 稳定表与分表不并用——这一格必须在契约期红而不是等发射：SplitTablesScript/建片链
        // 对分表契约逐片调 EmitCreateTable，而那一格只给片契约（ForShardDdl 的形状），
        // 稳定格若留到发射才判，脚本路会先折进"普通表"那一支发出一张形状说错的 CREATE TABLE
        // —— "看似建好了"的假绿，比当场红严重一档。
        if (stableDeclaration is not null && table.Split is not null)
            throw new QuickSqlException($"实体 {clrType.Name} 同时声明了超级表与分表：两套\"一个实体对多张表\"不并用 —— "
                + "分表的片名模板落在 [Table] 名上，而稳定表实体那里那颗名字是稳定名，"
                + "子表命名走 [TDengineStable(Subtable = …)] 的模板那档（插入时用行内 TAG 值解析）。请摘掉 [SplitTable]。");

        return new EntityContract(clrType, table, resolved, MapIndexes(indexes, resolved,
            table.Split is null ? table.Name : clrType.Name),
            edges, collections, skip, stableDeclaration);
    }

    /// <summary>跳过索引声明到已换算的契约。三件事只有这里一份（两侧同一出口）：
    /// 类挂没有表达式当场红（G1 在编译期以同一句话先拦一遍）；属性挂的成员可以表中无主
    /// （那条也要红：漏映射就是建出一个不存在的列，与 MapToColumns 同一条拒绝）；
    /// 名字撞车当场红——同一张表上两条同名 INDEX，第二条会把第一条顶掉，静默过去就是"看似建好"。
    /// 档位参数的合法性（Set 要 max_rows 之类）不在这里判：那是发射那一家 CodeFirst 的
    /// 方言语义，中性契约只背声明。</summary>
    static IReadOnlyList<SkipIndexContract> MapSkipIndexes(IReadOnlyList<SkipIndexFacts>? facts, ColumnContract[] columns,
        string nameBase, string entityName)
    {
        if (facts is null || facts.Count == 0)
            return [];

        var result = new SkipIndexContract[facts.Count];
        var classOrdinary = 0;

        for (var i = 0; i < facts.Count; i++)
        {
            var fact = facts[i];
            string expression;
            string? memberName = null;
            string name;

            if (fact.MemberName is { } member)
            {
                var column = Array.Find(columns, c => string.Equals(c.MemberName, member, StringComparison.Ordinal))
                    ?? throw new QuickSqlException($"跳过索引引用了不是列的成员 {member}（实体 {entityName}，可能被 [NotMapped] 排除）");

                memberName = column.MemberName;

                // 属性挂且没写表达式 = 就是那颗列：发的是 plain 列名（发射侧加方言引号），
                // MemberName 非 null 就是那把"要加引号"的信号。
                expression = fact.Expression ?? column.ColumnName;
                name = fact.Name ?? $"QSI_{nameBase}_{column.ColumnName}_{ProviderAttributes.KindToken(fact.Kind)}";
            }
            else
            {
                expression = fact.Expression
                    ?? throw new QuickSqlException($"实体 {entityName} 有一颗表级跳过索引声明但没有表达式："
                        + "类挂的那一路没有\"缺省成员\"可言，请把表达式写进声明（属性挂那一档才默认用自己的列）");

                classOrdinary++;
                name = fact.Name ?? $"QSI_{nameBase}_{classOrdinary}";
            }

            for (var j = 0; j < i; j++)
                if (string.Equals(result[j].Name, name, StringComparison.Ordinal))
                    throw new QuickSqlException($"跳过索引的名字撞车：这一张表上两条声明都要叫 {name}（实体 {entityName}）。"
                        + "显式点名或换一条声明，让每颗索引有自己的名字");

            result[i] = new SkipIndexContract(name, fact.Kind, fact.Granularity, expression, memberName,
                fact.SetMaxRows, fact.NgramSize, fact.BucketSize, fact.HashFunctions, fact.Seed);
        }

        return result;
    }

    /// <summary>超级表声明到已换算的契约（两侧同一出口）。三件事只有这里一份：TAG 成员必须是列
    ///（漏映射就是 TAGS 子句里出现一颗不存在的列，与 MapSkipIndexes 同一条拒绝）；一颗 TAG 都没有
    /// 当场红（TDengine 的稳定表至少要一颗 TAG，这是厂商的硬性事实）；子表名模板的占位符只能引用
    /// 已声明的 TAG 成员——模板在插入时用行内 TAG 值填空，占位符落到数据列上就是"值每行都在变
    /// 而子表名只能有一个"的语义错。TAG 列的类型门禁（DECIMAL 不可作 TAG 等）不在这里判：
    /// 那是发射那一家 CodeFirst 的方言语义，中性契约只背声明。</summary>
    static StableDeclaration? MapStable(StableFacts? facts, ColumnContract[] columns, string entityName)
    {
        if (facts is null)
            return null;

        if (facts.TagMemberNames.Count == 0)
            throw new QuickSqlException($"实体 {entityName} 声明了超级表却一颗 TAG 都没有：TDengine 的稳定表至少要一颗 TAG"
                + "（[TDengineTag] 打在属性上，至少一颗；厂商上限 128 颗）");

        var tags = new TagColumnContract[facts.TagMemberNames.Count];

        for (var i = 0; i < facts.TagMemberNames.Count; i++)
        {
            var member = facts.TagMemberNames[i];

            var column = Array.Find(columns, c => string.Equals(c.MemberName, member, StringComparison.Ordinal))
                ?? throw new QuickSqlException($"超级表的 TAG 引用了不是列的成员 {member}（实体 {entityName}，可能被 [NotMapped] 排除）");

            tags[i] = new TagColumnContract(column.MemberName, column.ColumnName);
        }

        if (facts.SubtableTemplate is { } template)
            ValidateSubtableTemplate(template, tags, entityName);

        return new StableDeclaration(facts.SubtableTemplate, tags);
    }

    /// <summary>子表名模板的占位符校验：每颗 {成员名} 都得是已声明的 TAG 成员。
    /// 只扫形状（配对、命中），不在这里管"值能不能当文件名"——那是插入面发射点的事。</summary>
    static void ValidateSubtableTemplate(string template, TagColumnContract[] tags, string entityName)
    {
        for (var at = template.IndexOf('{'); at >= 0; at = template.IndexOf('{', at + 1))
        {
            var close = template.IndexOf('}', at + 1);

            if (close < 0)
                throw new QuickSqlException($"子表名模板 {template} 的 '{{' 没有配对的 '}}'（实体 {entityName}）：占位符形如 {{成员名}}");

            var placeholder = template[(at + 1)..close];

            // 占位符比对大小写不敏感：模板是写给人看的那个面（{device} 比 {Device} 顺眼），
            // 而发射侧（TDengineDialect.EmitSubtableName）用同一把尺解析——两处不同尺就是
            // "契约期放行、插入期红"的那类分叉，这里与那里必须一字不差地同判。
            if (Array.FindIndex(tags, t => string.Equals(t.MemberName, placeholder, StringComparison.OrdinalIgnoreCase)) < 0)
                throw new QuickSqlException($"子表名模板 {template} 的占位符 {{{placeholder}}} 不是已声明的 TAG 成员（实体 {entityName}）："
                    + "占位符只能引用 [TDengineTag] 点出的成员——TAG 是子表的元数据、值在同一个子表里恒定，"
                    + "拿数据列填子表名会让同一批行落到说不出是哪张表的模板上");
        }
    }

    /// <summary>分片声明到已换算的声明，顺手把四条启动期判据比一遍：分片键得是一列、
    /// 表名模板得配得上那一档、哈希档得有桶、主键不许自增。
    /// 自增那一条是硬的：跨片唯一由库自己给的话，两张表能各自发出
    /// 同一个号，而"谁冲突"这件事要到合并统计时才看得见。</summary>
    static SplitFacts CompleteSplit(SplitFacts split, ColumnContract[] columns, TableInfo table, string entityName)
    {
        var column = Array.Find(columns, c => string.Equals(c.MemberName, split.MemberName, StringComparison.Ordinal))
            ?? throw new QuickSqlException($"分表声明的分片键 {split.MemberName} 不是实体 {entityName} 的一列（可能被 [NotMapped] 排除）");

        SplitRouting.ValidateTemplate(split.Kind, table.Name);

        if (split.Kind == SplitKind.Hash && split.Buckets < 2)
            throw new QuickSqlException($"分表声明了 {SplitKind.Hash} 档却只给 {split.Buckets} 个桶（实体 {entityName}），少于 2 个桶等于没分");

        if (Array.Find(columns, c => c.IsPrimary && c.IsIdentity) is { } identity)
            throw new QuickSqlException($"分表实体 {entityName} 的主键 {identity.MemberName} 是自增列：跨片唯一性库给不了。要么由客户端生成主键（[QuickColumn(IsIdentity=TriState.No)]），要么这一张表不分表");

        return split with { ColumnName = column.ColumnName };
    }

    static ColumnContract Complete(ColumnFacts facts, TableInfo table, ITypeMap types, bool integralPrimaryKeyAutoIncrement,
        int? defaultStringLength)
    {
        var withStorage = types.ApplyStorageDefaults(facts.ToProvisional(table, defaultStringLength));

        // 自增要等类型定下来才能判定：主键 + 整型，且没有任何一层显式说过不是。
        // 装配口说"这一族库的整型主键归客户端给号"时，约定那一半让位给声明，声明那一半原样保留。
        var identity = facts.DeclaredIdentity
                       ?? (integralPrimaryKeyAutoIncrement && facts.IsPrimary && IsIntegral(withStorage.ClrType));

        return withStorage with
        {
            IsIdentity = identity,
            // 值域与类型相容两格在这里判（此时只认得声明侧事实）；自增/主键那一格要等
            // ApplyKeyConvention 跑完（约定命中的 Id/{Entity}Id 在 Complete 时还不是主键），见 Build。
            DefaultValue = CheckDefault(facts, withStorage),
            Origin = facts.DeclaredIdentity is null && identity
                ? withStorage.Origin with { Note = withStorage.Origin.Note + "; 自增 ← 约定：整型主键" }
                : withStorage.Origin,
        };
    }

    /// <summary>默认值门禁的第三格：自增/主键列不收默认值。它必须跑在
    /// ApplyKeyConvention 之后——约定命中（成员名 Id/{Entity}Id 形态）的主键在
    /// Complete 那一趟还不是主键，门禁放那里就漏掉"约定自增列配默认"这一形。</summary>
    static void CheckDefaultAgainstKeys(ColumnContract[] columns)
    {
        foreach (var column in columns)
        {
            if (column.DefaultValue is null || (!column.IsIdentity && !column.IsPrimary))
                continue;

            throw new QuickSqlException(
                $"列 {column.MemberName} 是{(column.IsIdentity ? "自增" : "主键")}列，不收默认值：库给值的列配默认是自相矛盾的配置" +
                "（发出去要么被库忽略、要么撞语法）。要客户端给号就撤掉自增声明，要默认值就换一列。");
        }
    }

    /// <summary>默认值那一格的门禁（两侧共用的唯一一份）。三格全在声明期响：
    /// 值域只收常量字面量档——DateTime 会把"首次建表那一刻"冻进 DDL、byte[] 没有字面量形、
    /// 其余对象根本不是常量（枚举也不收：各家字面量形不同）；类型相容——默认值的运行期类型
    /// 要与列的 CLR 型（剥掉 Nullable 之后）精确相等，宽窄转换在 DDL 里是库自己做的，
    /// 与"收窄要显式"的口径在这里再对不齐就是静默错型；自增/主键不收默认值——库给值的列配默认
    /// 是自相矛盾的配置，发出去要么被库忽略、要么撞语法，两种都比当场报难查。</summary>
    static object? CheckDefault(ColumnFacts facts, ColumnContract provisional)
    {
        if (facts.DefaultValue is not { } value)
            return null;

        static string Describe(object v) => v is string s ? $"\"{s}\"" : v.ToString() ?? v.GetType().Name;

        if (value is not (string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal))
            throw new QuickSqlException(
                $"列 {facts.MemberName} 的默认值 {Describe(value)} 是 {value.GetType().Name}：" +
                "默认值只收常量字面量档（string ∶ bool ∶ 整型 ∶ 浮点 ∶ decimal）。DateTime 会把首次建表那一刻冻进 DDL，" +
                "byte[] 没有字面量形，枚举的字面量形各家不同 —— 都不在这一格的值域里。");

        var clrType = Nullable.GetUnderlyingType(provisional.ClrType) ?? provisional.ClrType;

        if (clrType != value.GetType())
            throw new QuickSqlException(
                $"列 {facts.MemberName} 的默认值 {Describe(value)}（{value.GetType().Name}）与列型 {provisional.ClrType.Name} 对不上：" +
                "默认值按精确同型收（可空列剥掉 Nullable 之后比对），宽窄转换不经这一格 —— 要别的型就在实体侧改成员类型。");

        return value;
    }

    static void ApplyKeyConvention(ColumnContract[] columns, string entityName, bool integralPrimaryKeyAutoIncrement)
    {
        var index = Array.FindIndex(columns, c =>
            string.Equals(c.MemberName, "Id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.MemberName, entityName + "Id", StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return;

        var found = columns[index];

        columns[index] = found with
        {
            IsPrimary = true,
            // 命中 Id/{Entity}Id 形态只补「谁是键」这一半；自增那一半不许把高优先层的声明盖掉 ——
            // 上面那一档（Complete）已经按声明算过一次，这里只允许约定往"归库给"的方向添，
            // 装配口说这一族库归客户端给号时，只有真表过态自增的列才留得下来。
            IsIdentity = found.IsIdentity || (integralPrimaryKeyAutoIncrement && IsIntegral(found.ClrType)),
            Origin = new OriginTrace(MetadataLayer.Convention, $"{found.MemberName} 命中 Id/{{Entity}}Id 形态"),
        };
    }

    static bool IsIntegral(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(int) || type == typeof(long) || type == typeof(short);
    }

    static IReadOnlyList<IndexContract> MapIndexes(IReadOnlyList<IndexFacts> facts, ColumnContract[] columns, string entityName)
    {
        var result = new Dictionary<string, IndexContract>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            var name = fact.Name ?? DefaultIndexName(entityName, fact.MemberNames);
            var kind = fact.Clustered ? IndexKind.Clustered : fact.Unique ? IndexKind.Unique : IndexKind.NonClustered;

            Add(result, new IndexContract(name, kind, [.. fact.MemberNames], fact.Descending)
            {
                ColumnNames = MapToColumns(fact.MemberNames, columns, "索引 " + name),
            });
        }

        return [.. result.Values];
    }

    /// <summary>导航声明按朝向分成两格。to-one 那一格才换算列名：外键列就在这张表上，
    /// 而集合那一格的名字属于子端那张表（换算要另一份契约，于是挪到运行期解算那一处，
    /// 见 ChangeTracker 的级联）。不查主端：被引用侧的列要另一份契约才算得出来，
    /// 而这一格的消费者（写序）只需要"谁依赖谁"那一条边。</summary>
    static (IReadOnlyList<RelationshipContract> Edges, IReadOnlyList<CollectionEdgeContract> Collections)
        MapRelationships(IReadOnlyList<RelationshipFacts> facts, ColumnContract[] columns)
    {
        var edges = new List<RelationshipContract>(facts.Count);
        var collections = new List<CollectionEdgeContract>(0);

        foreach (var fact in facts)
        {
            if (!fact.IsCollection)
            {
                edges.Add(new RelationshipContract(fact.MemberName, fact.TargetType, [.. fact.ForeignKeyMembers],
                    MapToColumns(fact.ForeignKeyMembers, columns, $"引用导航 {fact.MemberName}"))
                {
                    Origin = new OriginTrace(fact.Layer, fact.Note),
                });

                continue;
            }

            // 集合那一格没有可读的列名，但两把委托必须在：读口拿父实体手里那一串子实体，
            // 入口把取到的子实体送进子端的跟踪器 —— 都是静态型的东西，只能由两侧各自编好带过来。
            collections.Add(new CollectionEdgeContract(fact.MemberName, fact.TargetType,
                [.. fact.ForeignKeyMembers],
                fact.NavigationReader ?? throw new QuickSqlException(MissingAccessor(fact, "读口")),
                fact.CascadeEnter ?? throw new QuickSqlException(MissingAccessor(fact, "级联入口")))
            {
                Origin = new OriginTrace(fact.Layer, fact.Note),
                Cascade = fact.Cascade,
            });
        }

        return (edges, collections);
    }

    /// <summary>集合边的委托缺口的消息：把「哪一层声明的这条边」一起写进去，因为两侧生成器与反射解析器
    /// 只有一侧漏带时，报错的那次运行可能走的是另一侧，光看成员名分不出是谁的锅。</summary>
    static string MissingAccessor(RelationshipFacts fact, string what)
        => $"集合导航 {fact.MemberName} 没有{what}（声明来自 {fact.Layer}：{fact.Note}）：级联要靠它取子实体并送进会话，两侧生成器与反射解析器都必须带这把委托。";

    /// <summary>同名且同形 = 两层声明了同一件事，留高优先级的最后那条；同名不同形 = 配置错误，抛。</summary>
    static void Add(IDictionary<string, IndexContract> result, IndexContract index)
    {
        if (result.TryGetValue(index.Name, out var existing)
            && !existing.MemberNames.SequenceEqual(index.MemberNames, StringComparer.Ordinal))
        {
            throw new MetadataConflictException(index.Name,
            [
                $"索引 {index.Name} 被声明为 ({string.Join(", ", index.MemberNames)})",
                $"又被声明为 ({string.Join(", ", existing.MemberNames)})",
            ]);
        }

        result[index.Name] = index;
    }

    static string DefaultIndexName(string entityName, IReadOnlyList<string> memberNames)
        => "IX_" + entityName + "_" + string.Join("_", memberNames);

    /// <summary>成员名换算成列名。declaration 说这句话是谁的（索引 IX_x / 引用导航 Customer）——
    /// 换不成列名时，消息要指回声明本身，而不是只报一个属性名。</summary>
    static string[] MapToColumns(IReadOnlyList<string> memberNames, ColumnContract[] columns, string declaration)
    {
        var result = new string[memberNames.Count];

        for (var i = 0; i < memberNames.Count; i++)
        {
            result[i] = Array.Find(columns, c => string.Equals(c.MemberName, memberNames[i], StringComparison.Ordinal))
                        ?.ColumnName
                        ?? throw new QuickSqlException($"{declaration} 引用了未映射的属性 {memberNames[i]}（可能被 [NotMapped] 排除）");
        }

        return result;
    }
}

/// <summary>ContractOnly 档的两条尺子。
/// G1 生成的校验器与 G2 的反射校验器都调这里，所以失败消息的文本也只有一份——
/// 否则"同一行数据两条路径报出两句话"这种偏差只能靠人眼发现。</summary>
public static class ContractRules
{
    public static void Required(ColumnContract column, List<ValidationFailure> failures)
    {
        // "没表过态"不是一句"可以说非空"的许可（尺子量的是声明）：可空上下文之外读回来的
        // 非空形状不挡写入，否则 ABP/DDD 那一族实体的合法 null 会被一次没说过的话拒掉。
        // 判据只在这一处，G1 生成的校验器与 G2 的反射校验器因此不会各说一句。
        if (!column.NullabilityDeclared)
            return;

        failures.Add(new ValidationFailure(column.MemberName, "required", $"{column.MemberName} 不可为空（列 {column.ColumnName}，来源 {column.Origin}）"));
    }

    /// <summary>量纲用列自己声明的那把尺子，不是 string.Length。</summary>
    public static void Length(ColumnContract column, string value, List<ValidationFailure> failures)
    {
        var (fits, ruler) = column.LengthSemantics == LengthSemantics.Byte
            ? (column.FitsBytes(value), $"{column.MaxBytes} 字节")
            : (column.FitsChars(value), $"{column.MaxChars} 字符");

        if (!fits)
            failures.Add(new ValidationFailure(column.MemberName, "length", $"{column.MemberName} 超出列 {column.ColumnName} 的 {ruler}"));
    }
}
