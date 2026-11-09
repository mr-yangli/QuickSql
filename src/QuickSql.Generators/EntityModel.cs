using System.Collections.Generic;

namespace QuickSql.Generators;

/// <summary>一个实体的 G1 产出计划。存的是语义值，不是代码文本：转义与排版全在 EntityFactsWriter，
/// 判定全部在 EntityFactsBuilder，「规则写在哪一处」这个问题只有一个答案。</summary>
sealed class EntityModel
{
    public string Namespace = "";

    /// <summary>实体类型表达式，如 global::QuickSql.TestInfra.Order。</summary>
    public string Entity = "";

    /// <summary>生成类名前缀：嵌套链折成一个标识符，保证同编译期内不撞名。</summary>
    public string Name = "";

    public string TableName = "";

    public string? TableSchema;

    /// <summary>表备注：来自 [Table(Comment=…)] 或 [Description]；开关 QuickSqlXmlDocComments 开着时
    /// 取类型上的 /// summary 兜底（注解写过就不轮到它）。Fluent 那一层生成期读不到。
    /// null = 注解没写这一格。</summary>
    public string? TableComment;

    public List<ColumnModel> Columns = [];

    public List<IndexModel> Indexes = [];

    /// <summary>跳过索引声明（[ClickHouseSkipIndex]）。次序：类挂在前、列挂按列清单次序在后，
    /// 与反射侧同一份次序——顺序也算对拍的内容。</summary>
    public List<SkipIndexModel> SkipIndexes = [];

    /// <summary>超级表声明（[TDengineStable]），null = 实体不是稳定表。TAG 成员按属性次序收，
    /// 与反射侧同一份次序——顺序也算对拍的内容。</summary>
    public StableModel? Stable;

    public List<RelationshipModel> Relationships = [];

    /// <summary>分表声明，null = 这张表不分表。与 TableName 分开存：名字是模板还是实名，
    /// 两侧都由运行期的同一份校验判，生成期不重复那一遍。</summary>
    public SplitModel? Split;

    /// <summary>集群归属，存的是注解里那个原样字符串；null 只表示"没打 [DataSource]"。
    /// 空串与空白不在这里判、也不 Trim：那一格由运行期收口（两侧同一句 ValidateName），
    /// 与 Split 同一分工。</summary>
    public string? Source;
}

/// <summary>[SplitTable] 的编译期抄本：档名（Kind 是 SplitKind 的成员名）、分片键成员名、桶数。</summary>
sealed class SplitModel
{
    public string Kind = "";

    public string Member = "";

    public int Buckets;
}

sealed class ColumnModel
{
    public string Member = "";

    /// <summary>属性声明类型（含 Nullable〈T〉），与反射侧的 PropertyType 同一个。</summary>
    public string ClrType = "";

    public string ColumnName = "";

    /// <summary>MetadataLayer 的成员名。整条 origin 取这一层。</summary>
    public string ColumnNameLayer = "Convention";

    public string ColumnNameNote = "";

    public int? MaxChars;

    public string LengthNote = "";

    public bool IsNullable;

    public string NullableNote = "";

    /// <summary>"非空"这句话有没有人说过。false = 可空上下文之外（NullableAnnotation.None），
    /// 与反射侧 NullabilityState.Unknown 同一格；只摘必填尺子，不改列的形状。</summary>
    public bool NullabilityDeclared = true;

    public bool IsPrimary;

    /// <summary>null ⇒ 交给「整型主键 ⇒ 自增」约定，由运行期判定。</summary>
    public bool? DeclaredIdentity;

    public int? Precision;

    public int? Scale;

    public string? StoreType;

    /// <summary>列备注。开关 QuickSqlXmlDocComments 开着时，注解那一层全没写的那一列拿它自己的
    /// /// summary 兜底（折成一行、摘掉标签）；关着时与反射侧逐字同链。</summary>
    public string? Comment;

    /// <summary>列默认值（[DefaultValue]，System.ComponentModel）。生成期只看得见注解
    /// （Fluent 那一档运行期才在），字面量档（string/bool/整型/浮点/decimal）在生成期先拦一遍，
    /// 值域与类型相容的门禁在运行期收尾那一格——与反射侧同一份。null = 注解没写这一格。</summary>
    public object? DefaultValue;

    /// <summary>并发列（[ConcurrencyToken]）。与其余各格同一口径：生成期只看得见注解，
    /// Fluent 那一层 G1 整体上就吃不到。</summary>
    public bool IsConcurrencyToken;

    /// <summary>应用给值那一族（[ConcurrencyToken(AppSetsValue = true)] 或成员名 ConcurrencyStamp 的约定）。
    /// 与 IsConcurrencyToken 同一限制：生成期只看得见注解，Fluent 那一层 G1 吃不到。</summary>
    public bool AppSetsValue;

    public string? ConcurrencyNote;

    /// <summary>CLR 侧是不是 string：只有字符串列有长度尺子。</summary>
    public bool IsString;

    /// <summary>这一列的直读臂方法名（DirectRead.ReadInt32 那一批），null = 没有臂、进装箱口。
    /// 判据与编号都在运行期那一个文件里，这里只挑名字：挑错臂（比如给 short 列挑了 ReadInt32）
    /// 则两侧编号对不上、静默退回装箱口——能错的方向是"没变快"，不是"读错值"。</summary>
    public string? DirectArm;

    /// <summary>非空值类型装箱后不可能是 null ⇒ 必填检查是死代码，生成器不写。</summary>
    public bool CanBeNull;
}

sealed class IndexModel
{
    public List<string> Members = [];

    public bool Unique;

    public bool Clustered;

    public bool Descending;
}

/// <summary>[ClickHouseSkipIndex] 的编译期抄本。Kind 存的是
/// SkipIndexKind.成员 的完整表达式，写手只管抄；
/// Member 为 null = 类挂（那一路必须有表达式，否则编译期诊断）。</summary>
sealed class SkipIndexModel
{
    /// <summary>属性挂 = 该属性名；类挂 = null。</summary>
    public string? Member;

    /// <summary>显式表达式；null = 用成员的列（列挂缺省那一档）。</summary>
    public string? Expression;

    /// <summary>显式索引名；null = 约定名在运行期收口（两侧同一句）。</summary>
    public string? Name;

    public string Kind = "";

    public int Granularity;

    public int SetMaxRows;

    public int NgramSize;

    public int BucketSize;

    public int HashFunctions;

    public int Seed;
}

/// <summary>[TDengineStable] 的编译期抄本（v0.39 第六笔）。Subtable 为 null = 声明里没写子表模板；
/// TagMembers 按属性次序收（TAGS 子句的列序就是它），只抄声明——成员到列换算与占位符校验
/// 在运行期 EntityContractFactory 那一份。</summary>
sealed class StableModel
{
    public string? Subtable;

    public List<string> TagMembers = [];
}

/// <summary>一条导航边的声明（to-one 或集合，按 IsCollection 分）。列名换算不在生成期做——
/// 那是运行期的活，与索引同一份实现。</summary>
sealed class RelationshipModel
{
    /// <summary>导航属性名，它自己不是列。</summary>
    public string Member = "";

    /// <summary>对面那一端的实体类型表达式。IsCollection 为真时那是子端。</summary>
    public string TargetType = "";

    /// <summary>外键成员名，复合（[ForeignKey("A,B")]）有多条。to-one 那一档是本端的成员名，
    /// 生成期就能对着列清单验；集合那一档是子端的，本端清单里没有，留空即"没声明、运行期再解"。</summary>
    public List<string> Members = [];

    /// <summary>MetadataLayer 的成员名：这一格只有两档，
    /// DataAnnotations 的 [ForeignKey] 与「导航名 + Id」的约定。</summary>
    public string Layer = "Convention";

    public string Note = "";

    /// <summary>true = 这是一根集合导航（父端 → 子端），折进 RelationshipFacts.IsCollection。</summary>
    public bool IsCollection;

    /// <summary>级联开关，缺省开；[NoCascade]（打在实体或这根导航上）折成 false。</summary>
    public bool Cascade = true;
}
