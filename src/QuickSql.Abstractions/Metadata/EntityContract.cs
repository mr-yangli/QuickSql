using System.Collections.Frozen;

namespace QuickSql.Metadata;

/// <summary>一张表的已解析事实。Split 非 null 说的是这一格是一个分表模板、不是一个能直接发出去的名字：
/// Name 里带着 {year} 一类的占位符，要经 SplitRouting 换算成某一张物理表才能发射——
/// 发射口按这一格在场与否决定要不要报错，"忘了换算就把模板原样发出去"的事故有结构性兜底。
/// Source 的 null 说的是"没声明"而不是"没有归属"：没声明即缺省集群，就是 QuickSqlOrmOptions.ConnectionString
/// 那条串；留着 null 而不是填上 main，为的是让"用户一个字没写"与"用户显式写了 main"在换算前后留得住区别。
/// Comment 的 null 与空串不是一回事：null = 没有任何一层说过（含 [Description]、Fluent 的 HasComment 与
/// 源生成器路径上的 /// summary 兜底三处来源），空串 = 说过而说的是"没有备注"；结构同步比的就是这一格，
/// 折成一格会让"清掉备注"与"从来没写过备注"在计划里长同一个样。</summary>
public sealed record TableInfo(
    string? Schema,
    string Name,
    SplitFacts? Split = null,
    DataSourceFacts? Source = null,
    string? Comment = null);

/// <summary>一条引用导航（to-one 关系）的已解析事实，只有依赖侧：外键列就在这张表上，
/// 而主端那一半要另一份契约才算得出来（TargetType 是唯一的跨实体线索）。
/// 消费者：SaveChanges 那句"先插被依赖的、再删依赖方"的写序、Include 的 JOIN、
/// 以及临时主键改写时那一格外键列。级联不看这一格，它看的是父端那根集合导航（EntityContract.Collections）。
/// MemberNames 是依赖侧的外键属性名（与列契约的 MemberName 同一口径）；
/// ColumnNames 是换算后的列名、顺序与之对齐（复合外键才有多条）。</summary>
public sealed record RelationshipContract(
    string MemberName,
    Type TargetType,
    IReadOnlyList<string> MemberNames,
    IReadOnlyList<string> ColumnNames)
{
    public required OriginTrace Origin { get; init; }
}

/// <summary>一根集合导航（父端到子端）解析出来的事实，级联唯一的来源。
/// 与 RelationshipContract 分两格是因为外键住在不同那张表上：这一格的 DeclaredForeignKeyMembers
/// 说的是子端的成员名，要换算成列得拿子端的契约，而子端的契约在这一格建立时未必已经存在
/// （注册表按需解析），所以换算推迟到级联真要用那一刻，推不出就响。
/// MemberName 是父实体上那根集合导航的属性名（它自己不是列）；ChildType 是子端实体的 CLR 类型；
/// DeclaredForeignKeyMembers 是 ForeignKey 打在集合导航上时给的那几个子端成员名（空 = 没有声明，
/// 运行期按"子端的 to-one 边、约定父类型名加 Id"两档推）；NavigationReader 读父实体那一格集合位
/// （编译一次、级联每行只调用）；CascadeEnter 是级联的入口，子类型在写这一格的代码里是编译期已知的，
/// 会话才不必 MakeGenericType（理由见 ICascadeSink）。</summary>
public sealed record CollectionEdgeContract(
    string MemberName,
    Type ChildType,
    IReadOnlyList<string> DeclaredForeignKeyMembers,
    Func<object, object?> NavigationReader,
    Action<QuickSql.Tracking.ICascadeSink, object> CascadeEnter)
{
    public required OriginTrace Origin { get; init; }

    /// <summary>级联开关，缺省开；NoCascade 注解折在这一格。</summary>
    public bool Cascade { get; init; } = true;
}

/// <summary>一个实体的解析结果。启动期一次性构建，之后不可变。</summary>
public sealed class EntityContract
{
    public EntityContract(Type clrType, TableInfo table, ColumnContract[] columns, IReadOnlyList<IndexContract>? indexes,
        IReadOnlyList<RelationshipContract> relationships, IReadOnlyList<CollectionEdgeContract>? collections = null,
        IReadOnlyList<SkipIndexContract>? skipIndexes = null, StableDeclaration? stable = null)
    {
        ClrType = clrType;
        Table = table;
        Columns = columns;
        Indexes = indexes ?? [];
        Relationships = relationships;
        Collections = collections ?? [];
        SkipIndexes = skipIndexes ?? [];
        Stable = stable;
        ByMember = columns.ToFrozenDictionary(c => c.MemberName, StringComparer.Ordinal);
        ByColumn = columns.ToFrozenDictionary(c => c.ColumnName, StringComparer.Ordinal);
        PrimaryKey = Array.FindAll(columns, c => c.IsPrimary);
    }

    /// <summary>非泛型路径（跟踪器、批量、物化委托）按它取读取器。契约自带 Type，
    /// 才不会出现"实体类型与列契约来自两处"的分叉。</summary>
    public Type ClrType { get; }

    public TableInfo Table { get; }

    /// <summary>顺序即物化与批量绑定时的列序，由 G1 生成代码依赖，不得按名字排序。</summary>
    public ColumnContract[] Columns { get; }

    public IReadOnlyList<IndexContract> Indexes { get; }

    /// <summary>跳过索引声明（provider 专属声明位那颗，今天只有 ClickHouse 消费）。
    /// 与 Indexes 分两格：IndexContract 是"表建成之后另有对象"的 OLTP 形状（唯一/聚集那一族），
    /// 而跳过索引是发射进建表骨架的声明，两格不换算——混进一格会让拒收整族索引的那一家
    /// （ClickHouse）连自家合法的声明一起拒。</summary>
    public IReadOnlyList<SkipIndexContract> SkipIndexes { get; }

    /// <summary>非 null = 这个实体是 TDengine 的稳定表（STABLE）：声明与换算见
    /// <see cref="StableDeclaration"/>。null = 普通表，发射侧的稳定分岔对这一格不生效。</summary>
    public StableDeclaration? Stable { get; }

    /// <summary>引用导航解析出来的关系边（写序读它）。顺序 = 实体里的声明序，
    /// 与 Columns 同一纪律：两侧生成的是同一份顺序，对拍才比得出"谁漏了一条"。</summary>
    public IReadOnlyList<RelationshipContract> Relationships { get; }

    /// <summary>集合导航解析出来的父端边（级联的来源）。顺序 = 实体里的声明序，同一纪律。
    /// 空 = 这个实体没有集合导航，于是级联那两步（Add 带人、Remove 删子行）一次都不跑。</summary>
    public IReadOnlyList<CollectionEdgeContract> Collections { get; }

    public IReadOnlyDictionary<string, ColumnContract> ByMember { get; }

    public IReadOnlyDictionary<string, ColumnContract> ByColumn { get; }

    public ColumnContract[] PrimaryKey { get; }

    public ColumnContract this[int ordinal] => Columns[ordinal];

    /// <summary>把模板契约绑到一张换算好的分片表上，供建片 DDL 用：列与关系两格原样带走，
    /// 索引那一格交回空的。存在的理由是 EmitCreateTable 收的是契约而不是表名，而按范围建片要逐张发一句
    /// CREATE TABLE；给它一颗口，省得 SchemaSyncer 自己复制一份构造参数表——那份抄本会随契约的每一次加字段而漏改一处。
    /// 索引不带过来是因为索引名在 PG 与 Oracle 那一族是模式内唯一的（SQL Server 才是表内唯一），
    /// 同一个模板的十二张片各发一句建索引、到第二张就撞；按片改名会多出一份命名口径，只给第一张发则是
    /// 一句"看起来建好了"的假绿，所以这里不发，SplitTablesResult.IndexesNotCreated 每次点名模板上有几条索引没随片建。</summary>
    public EntityContract ForShardDdl(TableInfo shard)
        => new(ClrType, shard, Columns, [], Relationships, Collections, []);
}

/// <summary>超级表声明里的一颗 TAG 列：成员名（C# 侧，子表名模板的占位符用它）与列名（库侧，TAGS 子句用）。
/// 声明序即 TAGS 子句的列序。</summary>
public sealed record TagColumnContract(string MemberName, string ColumnName);

/// <summary>TDengine 超级表声明换算后的契约（v0.39 第六笔）。实体是稳定表：[Table] 名即稳定名
///（DDL 与查询面都指它），SubtableTemplate 是插入面的子表名模板（占位符 {成员名} 引用 Tags 里的成员，
/// null = 没声明插入面的命名，插入面在发射点没有子表名的来路）；Tags 声明序即 TAGS 子句的列序。
/// TAG 列本身仍在 <see cref="EntityContract.Columns"/> 里（查询/物化/写值全通），把它们从数据列
/// 摘出去进 TAGS 子句是发射侧（那一家 CodeFirst/Dialect）的事——中性契约只背"哪些列是 TAG"这一格事实。</summary>
public sealed record StableDeclaration(string? SubtableTemplate, IReadOnlyList<TagColumnContract> Tags);
