using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.Values;

namespace QuickSql.Providers;

/// <summary>驱动 SPI。每个子接口只回答一类问题，
/// provider 之间只能继承公共血统基类、不得互引内部实现。</summary>
public interface IDbProvider
{
    DbKind Kind { get; }

    DbCapabilities Capabilities { get; }

    /// <summary>ADO.NET 连接/命令/参数，反射绑定驱动（加载可以反射，调用不行）。</summary>
    IConnectorFactory Connectors { get; }

    ISqlDialect Dialect { get; }

    ITypeMap Types { get; }

    IValueBinder Values { get; }

    IExpressionRules Expressions { get; }

    ICodeFirstDialect CodeFirst { get; }

    ISchemaReader Schema { get; }

    IKeyStrategy Keys { get; }

    IConcurrencyStrategy Concurrency { get; }

    /// <summary>把驱动抛回来的完整性违例翻成"哪一档"——与 Concurrency 同一家的事：
    /// 只有本家知道自己报的是 SQLSTATE 还是错误号，也只有本家知道那个号在这家是不是"唯一键"
    /// （SQL Server 的 SqlException.SqlState 放的是 ERROR_STATE() 而不是 SQLSTATE，
    /// 照"通用层读 SqlState"写就会漏掉那一整家）。
    /// 只需要 DbException 基类上的面：SqlState 是它的虚属性，约束名在驱动的 Data 字典里——
    /// 所以这一格不与"provider 编译期不认识驱动"打架，判据是一张纯码表。
    /// 默认 null = 判不出，收口点原样抛驱动那颗、不包装不猜。这不是兜底而是这一格的主要安全属性：
    /// 误分类会把外键冲突说成"重复提交"，用户照着提示再点一次而库里根本没有那一行；
    /// 不分类只是消费者拿到今天同样的裸异常。</summary>
    IntegrityViolation? ClassifyIntegrityError(DbException error) => null;

    /// <summary>变更守卫的比较形状。合并语句靠它把"一行都没变的行"挡在 UPDATE 之外，
    /// 而"怎么挡"只有两形（一句 IS DISTINCT FROM 对三段 OR），今天的分布是一家对多数派（四家走三段），
    /// 所以它归 provider，与 Concurrency 同一个道理——共用层那个三段形状因此不需要为少数派开分支。</summary>
    IChangeGuard ChangeGuard { get; }

    /// <summary>为 null 时批量退回链自然从多值语句那一层起。</summary>
    IBulkStrategy? Bulk { get; }

    /// <summary>该 provider 是否能自动创建数据库（CREATE DATABASE 或等价操作）。
    /// 默认 false：大多数 provider 需要连接到一个已存在的服务器才能建库，而连接串里通常已经指定了库名。
    /// SQLite 是例外：打开连接即创建文件，所以它覆写为 true。</summary>
    bool CanCreateDatabase => false;

    /// <summary>确保目标数据库已存在；不存在则创建。只在 CanCreateDatabase 为 true 时有意义，
    /// 默认抛 NotSupportedException：不支持建库的 provider 不应被调用此方法。
    /// 调用时机：SchemaSyncer 在 Execute 模式下、打开主连接之前调用，
    /// 确保后续建表语句不会因库不存在而失败。</summary>
    Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"Provider {GetType().Name} 不支持自动创建数据库（{nameof(CanCreateDatabase)}=false）。" +
            "请先手动建库，或改用支持建库的 provider（如 SQLite）。");
}

/// <summary>连接与命令的创建。实现方允许在构造期反射定位驱动类型，之后一律走缓存委托。</summary>
public interface IConnectorFactory
{
    DbConnection CreateConnection(string connectionString);

    DbCommand CreateCommand(DbConnection connection);

    /// <summary>驱动是否已经就位。缺失时给出可执行的修复建议，而不是空引用异常。</summary>
    bool TryEnsureLoaded(out string? failure);
}

public interface ITypeMap
{
    string GetStoreType(ColumnContract column);

    /// <summary>填上 provider 才知道的事实：Unicode、LengthSemantics、MaxBytes、StoreTypeName。
    /// 量纲由库决定，不由注解决定。</summary>
    ColumnContract ApplyStorageDefaults(ColumnContract column);

    /// <summary>存储类型名到 CLR 类型。读结构（ISchemaReader）与 doctor 用。</summary>
    bool TryGetClrType(string storeTypeName, out Type? clrType);

    /// <summary>该库上这个列的长度量纲。</summary>
    LengthSemantics GetLengthSemantics(ColumnContract column);
}

public sealed record FunctionMapping(string Template, bool RequiresNonNegativeLength = false)
{
    /// <summary>把实参填进模板。{0} 是第一个实参。</summary>
    public string Format(params string[] arguments) => string.Format(Template, arguments);
}

/// <summary>C# 方法/运算符到 SQL 函数的映射。映射不到就抛 UntranslatableExpressionException，
/// 绝不静默客户端求值。</summary>
public interface IExpressionRules
{
    /// <summary>key 有两种形态，都由 Core 侧统一生成、不接受调用方手拼：
    /// 符号键是 Fn.Key 出来的"符号/元数"（元数是键的一部分，否则一参与两参会共用一个模板，
    /// 多余实参被 string.Format 静默丢掉）；覆写键是方法全名（如 System.String.ToUpper）。</summary>
    bool TryGetFunction(string key, out FunctionMapping mapping);

    /// <summary>覆写点：现场遇到某库的函数名不一样时注册，不改代码。</summary>
    void Register(string key, FunctionMapping mapping);

    string ConcatOperator { get; }

    /// <summary>LIKE 的转义符。SQL Server 只有 ESCAPE 子句、无反斜杠默认转义。</summary>
    char LikeEscapeCharacter { get; }

    bool SupportsRegex { get; }
}

/// <summary>建表/改表/注释/大小写。P0 只产出 DDL 文本与黄金样本比对，不执行。</summary>
public interface ICodeFirstDialect
{
    IdentifierPolicy Identifiers { get; }

    void EmitCreateTable(SqlWriter writer, EntityContract contract);

    void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column);

    void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column);

    /// <summary>返回 false 表示这一家没有列注释这一档：Core 不重试、不改走别的形状，而是把那一列记进
    /// 同步结果的"判不了"清单并指名（TryEmitTableComment 同一取向）。
    /// 它必须幂等：备注同步会在"备注已经存在而值变了"的列上重发这一句，
    /// 只发 ADD 的那一家在第二趟会撞"属性已存在"。</summary>
    bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column);

    /// <summary>表级备注。默认 false = 这一家不接（新增 SPI 槽位一律带默认值，否则接一家要改六家）。
    /// 返回 true 的家在 Core 那里同时得到"能读回表备注"的义务（SchemaTable.Comment），
    /// 否则写进去的备注下一趟判不出差别——那是单向发射，不是同步。</summary>
    bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment) => false;

    void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index);

    /// <summary>删掉库里读回来的那一条索引。默认 false = 这一家不接（同一手法）。
    /// Core 只在调用方显式开了"允许删索引"那一档时才问这一颗：默认形态下结构同步
    /// 永不 DROP INDEX——它会当场拖慢正在跑的查询、是这一档里最难回滚的动作，而契约里没有这条索引
    /// 可能只是"这一册实体没覆盖那张表的全部查询"，不代表它没人用；没开的时候库里多出的那一条只进
    /// 同步结果的"要人工"清单并指名。
    /// 接了这一颗的家同时欠着"读得回索引清单"的义务（SchemaTable.Indexes）：
    /// 读不回来的那一家里"库里多出一条"根本判不出来，而按"契约里没有"去删就是把读不出读成"没有"、
    /// 删掉全部索引。那条形状由 SchemaTable.Indexes 的 null 挡在计划层，不靠这里。</summary>
    bool TryEmitDropIndex(SqlWriter writer, TableInfo table, SchemaIndex index) => false;
}

/// <summary>系统表读结构（各家差异最大的部分）。</summary>
public interface ISchemaReader
{
    Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default);

    /// <summary>量纲探测：只读，不建临时表、不写数据（生产库常禁 DDL 权限）。</summary>
    Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default);

    /// <summary>"库里这一列"与"契约说的这一列"是不是同一个形状，列级结构同步的判据位。
    /// 它归 provider 而不是 Core，只因为一件事：类型名怎么折、量纲按字符还是字节、精度藏在哪颗
    /// 目录字段里，全是这一家自己的目录说了算——Core 拿发射形文本与目录文本直接比必然假红
    /// （发射侧一律大写而目录一律小写、标度与别名各是岔口），跨家比文本更是没有这回事。
    /// 三态里的 null 是"这一家今天判不了"，不是"判过了、一样"：那一格进同步结果的"判不了"清单并指名，
    /// 既不响也不假装同步过。可空性不在这一颗的职责里：那是一格布尔、家与家同义，Core 自己比
    /// （比的是 NullableInStore，不是 IsNullable）。</summary>
    bool? ColumnShapeMatches(ColumnContract expected, SchemaColumn actual) => null;

    /// <summary>库里这一列现在有没有 NULL 行。只有结构同步要把一列收紧成 NOT NULL 时才问。
    /// 归 provider 而不是 Core 自己发那句 SELECT：这一句要的是这一家的标识符字形（双引号 / 反引号 /
    /// 方括号）与这一家的"取第一行"写法（LIMIT / TOP 1 / FETCH FIRST），Core 手里没有这两格，
    /// 纯猜出来的文本在六家之一是语法错；与量纲探测同一个处境，只读、不写。
    /// 三态的 null 是"这一家没接"，不是"没有 NULL"：那一格 Core 照常把 ALTER 发出去，并在同步结果的
    /// "判不了"清单里点名这一列——脏表上那一发仍然会以库的拒绝收尾，那是没接这一颗的家的既有行为；
    /// 接了一家的默认值就不该是 false：那等于对所有未接的库宣布"这张表干净"，而这句话只有库答得起。</summary>
    Task<bool?> ColumnHasNullsAsync(
        DbConnection connection,
        TableInfo table,
        string columnName,
        CancellationToken ct = default) => Task.FromResult<bool?>(null);
}

/// <summary>库里读回的一列。后头那几格全可空，而空的意思是"这一家读不出"、不是"库里没有"——
/// 所以每一格在 reader 那一家支持时必须给真值（列备注没有就是空串，自增不是就是 false），
/// 不给就是放弃那一档 diff。Name 列名；StoreType 是归一到本家建侧字形的类型文本
/// （各家 reader 自己折长名，所以它只保证同家自比，跨家不作数）；MaxChars 字符上限（读不出给 null）；
/// MaxBytes 字节上限（只有一家用另一把尺量它，其余留 null）；IsNullable 库里这一列能不能为空；
/// OrdinalPosition 列序。</summary>
public sealed record SchemaColumn(
    string Name,
    string StoreType,
    int? MaxChars,
    int? MaxBytes,
    bool IsNullable,
    int? OrdinalPosition)
{
    /// <summary>列备注。null = 这一家没有列注释这个概念（SQLite、GBase 8s），空串 = 有这概念而库里没写。
    /// 两者分不开的话，"备注同步"就永远只有单向发射：写进去的备注下一趟读不回来，判不出差别。</summary>
    public string? Comment { get; init; }

    /// <summary>这一列是不是库给值的那几列（自增 / rowversion）。null = 读不出。</summary>
    public bool? IsIdentity { get; init; }

    /// <summary>这一列是不是主键的一员。null = 读不出。</summary>
    public bool? IsPrimaryKey { get; init; }

    /// <summary>标度（小数的位数，也是 datetime2 那一族的时间精度）。null = 本家没有这一格或没读。</summary>
    public int? Scale { get; init; }
}

/// <summary>库里读回的一张表。扩出来的两格同样以 null = 这一家读不出 说话。
/// Table 表名；Columns 列清单、按列序排列。</summary>
public sealed record SchemaTable(TableInfo Table, IReadOnlyList<SchemaColumn> Columns)
{
    /// <summary>表备注。null = 读不出；空串 = 这一家有这概念而库里没写。</summary>
    public string? Comment { get; init; }

    /// <summary>这张表上现有的索引（含唯一性）。null = 读不出，补建索引那一档在这一家判不了；
    /// 空清单 = 读得出而这张表确实一个索引都没有。主键不在这一格里（它不是"补建"的对象）。</summary>
    public IReadOnlyList<SchemaIndex>? Indexes { get; init; }
}

/// <summary>库里读回的一个非主键索引。列的顺序是内容的一部分：(a,b) 与 (b,a)
/// 是两个索引，名字一样也不能当成同一个。Name 是索引名（库里那一颗，不是契约里那颗）。</summary>
public sealed record SchemaIndex(string Name, bool IsUnique, IReadOnlyList<string> Columns)
{
    /// <summary>这一颗其实是约束（唯一约束那一形，不是 CREATE UNIQUE INDEX 的产物）。
    /// 它和主键同一个处境：不是"补建"的对象，也不是 DROP INDEX 能删的对象——那一句在
    /// SQL Server 上直接报错，要拆它得走 ALTER TABLE DROP CONSTRAINT。所以开了删索引那一档时
    /// 这一条不进删除清单，只进"要人工"清单并指名它是约束。</summary>
    public bool IsConstraint { get; init; }
}

public sealed record LengthSemanticsReading(string CollationName, int DeclaredLength, int DefinitionByteLength, bool CharSemanticsProven);

public interface IKeyStrategy
{
    /// <summary>能否在批量插入后一次取回全部主键（OUTPUT / RETURNING）。
    /// DM/Oracle 系为 false，这是"批量回取键"那格能力的代价来源。</summary>
    bool SupportsBulkKeyRetrieval { get; }

    bool SupportsExplicitIdentityInsert { get; }

    /// <summary>把 OUTPUT inserted 或 RETURNING 清单写进 writer。</summary>
    void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning);

    /// <summary>打开/关闭 SET IDENTITY_INSERT 这类会话开关。</summary>
    string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on);

    /// <summary>"语句之后回读"那一档在不在。为 true 的含义窄而硬：当批量回取为 false、
    /// 且回填集恰好是一颗自增列时，Core 把插入逐条到每语句一行，并调 EmitPostStatementKeyRead
    /// 把回读句接在同一句命令文本的尾巴上（同一条命令 = 同一个会话，LAST_INSERT_ID() 这类
    /// 会话级函数才问得着刚插的那一颗）。并发列不在这一档的覆盖里（回读句给不了它们的值），
    /// 多颗自增列同理——两种都落回 EmitReturning 的拒绝路径。建模时不碰"多行 id 连续性"：
    /// 那条路要连自增锁模式一起建模，而逐条加每句带回读对单颗键不依赖任何锁模式。</summary>
    bool SupportsPostStatementKeyRead { get; }

    /// <summary>把"取回刚才那条单行 INSERT 由库生成的那一颗键"的回读句（如 SELECT LAST_INSERT_ID()）
    /// 写进 writer；调用方负责前面的分号分隔，与 EmitReturning 的空格纪律同一族。
    /// 只在 SupportsPostStatementKeyRead 为 true 时才会被调用。</summary>
    void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key);
}

public enum ConcurrencyMode { None, RowVersion, FullColumnCompare, TimestampColumn }

public interface IConcurrencyStrategy
{
    ConcurrencyMode Mode { get; }

    /// <summary>给一列生成"库里这一列还是我读到的那一版"的比较式。调用点在发射之前：内核把它合取进
    /// AST 的 WHERE，随后 AST 成文，才轮到 CudBuilding。早先那句"追加到文本尾巴、所以在 CudBuilding 之后"
    /// 不成立：两家的收尾位置不同（T-SQL 的 OUTPUT 在 FROM 之前，PG 系的 RETURNING 在整句之后），
    /// 文本级追加必在一家写出语法错。</summary>
    SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue);
}

/// <summary>"库里这一格与行集里那一格是不是不同"的比较形状（变更守卫）。
/// 归 provider 是因为只有它知道两件事：哪一家有 IS DISTINCT FROM，以及哪一型在它那儿连等号都不接受。</summary>
public interface IChangeGuard
{
    /// <summary>这一列能不能进守卫。假是一等公民而不是异常：返回 false 时调用方必须把整批退回逐条，
    /// 而不是把这一列从守卫里悄悄省掉——省掉一列的守卫会把"只改了那一列"的行也挡掉，那是丢写。</summary>
    bool Compares(ColumnContract column);

    /// <summary>"不同则真"的比较式。stored 是表侧、incoming 是行集侧：
    /// 两侧的 NULL 语义不对称（表侧可能是 NULL，行集侧是这次要写的值），所以形状不能只看一个类型。</summary>
    SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming);
}

/// <summary>批量高速层（装载通道那一档）。P0 的 provider 一律返回 null，退回链自然从多值语句起。
/// 装载通道的落地三家同一手法：反射绑定、第一次取用时才找类型、缺成员整家退多值档——装载类不在
/// DbProviderFactory 的抽象面上、编译期驱动引用纪律也不许直接 new 它们；"缺成员整家退"是看不见的失败
/// （形状与"这一家根本没有装载类"逐字同形，MySQL 血统第一版就静默退过一整版——驱动在 C# 调用面与
/// 反射面之间有四格形状差），每一家都要在真库上跑一次才算接上。三家的差别只记在两格里：
/// 闸门（只有 MySQL 家欠连接串一颗参数）与载体（两家交 DataTable、一家逐行推）。
/// 各槽位默认值就是"不接"，接一家不必改其余各家。</summary>
public interface IBulkStrategy
{
    int ParameterBudget { get; }

    string Describe { get; }

    /// <summary>装载通道的取用口。交回 null = 这一家不接（或这一支驱动里没有装载类），
    /// 阶梯自然从多值语句起——默认实现让不接的每一家一个字节都不必改。
    /// 它是方法而不是属性：RequiresUnreferencedCode 的特性目标里没有属性（CS0592），而这一颗必须带
    /// 那句标注——取它的过程是一次按类型名的反射查找，被裁剪的驱动会让它交回 null，差异是行为级的：
    /// 同一份代码在裁剪后的产物上报多值档、在完整运行时上报装载通道，税因此要往上冒到 ExplainBulk
    /// 与 ExecuteBulkAsync 两条公开出口上，而不是藏在 provider 里当没人负责的实现细节。
    /// 与驱动工厂同一手法：反射只在第一次取用时走一次，结果（连"没有"这一种）缓存。</summary>
    [RequiresUnreferencedCode("装载通道按类型名在驱动程序集里找（SqlBulkCopy 那一族没有 ADO.NET 抽象面），被裁剪的驱动会交回 null。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    IBulkCopyChannel? ResolveCopyChannel() => null;

    /// <summary>这一家能不能把多条写语句塞进一条命令文本、一次往返发出去（批量写链的 Merged 档，
    /// UpdateRange 的整行更新走的那一档）。默认 false = 没有读数证明这一家收得下多语句批，
    /// UpdateRange 的 Auto 因此落逐条——同一手法：新增 SPI 槽位一律带默认值，表态是覆写者的事，不表态就是 false。
    /// 今天覆写为 true 的两家各有读数（SQL Server 的分号批是公开契约；MySQL 血统已在一条命令文本里发过
    /// 插入带回读并在授权实例上跑通）；PG 血统的 Npgsql 同样收批，但本仓库今天没有任何一条产出的批
    /// 在它那儿跑过，按"代码今天真的走到"不声明——那一次绿了再把 false 翻成 true，不是在这一句里先猜。</summary>
    bool SupportsMergedStatements => false;

    /// <summary>这一家"一句把整批改完"的那一段句尾（Upsert 档，点名才走）。交回 null = 这一家不接，
    /// 与 SupportsMergedStatements 同一手法：新增 SPI 槽位一律带默认值。
    /// 交回的是句尾而不是整句：内核已经按方言的发出口拼好了 INSERT 的列与 VALUES，这一颗只补它后面那一段
    /// （MySQL 血统的行别名加 ON DUPLICATE KEY UPDATE、PG 血统的 ON CONFLICT DO UPDATE）——拆成句尾为的是
    /// 让列清单、VALUES 的形状与参数表只有一处真相，provider 不许自己再拼一遍 VALUES（它一旦拼了，
    /// 分块与取号就跟着有第二个出处）。两种形状都是量出来的，不是从文档抄的：MySQL 8.4 上老的 VALUES(col)
    /// 写法仍然收下但每句报"后续版本移除"的告警，所以接 8.0.19+ 的行别名写法；PG 的 ON CONFLICT 三值批同样收下。
    /// 缺键就插是这一档与 Merged 档的语义差而不是速度差，所以 Auto 永远不取它。
    /// 两道拒收（全局过滤器在场、主键由库给值而调用方没给键）不在这一颗里：那一半是这条链的形状，由内核拒。</summary>
    string? UpsertClause(UpsertColumns columns) => null;
}
