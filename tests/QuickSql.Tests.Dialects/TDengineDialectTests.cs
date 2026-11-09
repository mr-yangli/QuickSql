using System.Data;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.TDengine;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>TDengine 这颗库种的 SQL <b>文本</b>（§9.1 的 TDengine 那一行兑现里离线的那一半；
/// 单层 provider 的第三颗【文档口径行】，时序库这一档的第一颗）。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与另几族的离线套件同一分工：碰驱动即红（§3），没有"顺带把驱动拉进来"的口。
///
/// <para><b>本册走 golden/tdengine/ 的磁盘基线而不走对拍</b>，判据与 ClickHouse/DuckDB 那两册同一条：
/// 单层的【文档口径行】产出与任何一族都不同形 —— 列定义只有 "名" 型名一段（官方 column_definition
/// 文法里没有 NULL/NOT NULL、没有 DEFAULT、没有列注释）、主键不发任何子句（首列 TIMESTAMP
/// 由系统自动设为主键，发射前先做键形状校验）、占位符是位置形 ?（GBase 8s 同款而驱动侧参数名
/// 翻成 @pN 走 stmt2 分类绑定）、UPDATE/DELETE 两整格拒发【文档·TDengine：3.x 无传统 UPDATE，
/// DELETE 只认首列时间窗】。照金仓册那颗烧过的钉子抄一遍：<see cref="Gold"/> 常量<b>不能省</b> ——
/// 形状名与另几族是同一批（<c>select-entity</c>、<c>paging-order-skip-take</c>、<c>ddl-order</c>…），
/// 第一次生成时没带它就等于把 <c>golden/clickhouse/</c> 那批基线改写成了本家的文本。</para>
///
/// <para><b>查询面的样本用共用夹具</b>（SessionToken/Ticket，与另几族同一批实体、同一批形状名）：
/// 查询文本不经过键形状校验，跨家可比的那一半留在这边；<b>DDL 面的样本用本册自己的夹具</b>
///（首列必须是 TIMESTAMP、int Id 血统的实体在这一家建不出表），键形状的三条拒收各自点名。
/// insert-with-keys 在本家<em>不落盘</em> —— 回填那一路被内核先拦（没有自增列），方言侧
/// EmitReturning 整格抛，基线改判为"断言抛"。</para></summary>
public sealed class TDengineDialectTests
{
    static readonly IDbProvider Provider = TDengine.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名（与 ClickHouse/DuckDB 册的 <c>Gold</c> 同一颗钉子）。<b>这一层不能省</b>：
    /// 形状名与另几族是同一批，第一次生成时没带它就等于把别家的基线整目录改写成 TDengine 的文本。</summary>
    const string Gold = "tdengine";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Gold);

    static QuickSqlOrm orm() => TDengine.Orm();

    /// <summary>只有分页用例需要改选项：<c>Skip/Take</c> 无 <c>OrderBy</c> 在共用层默认是抛（§9.4），
    /// 而只写 <c>OFFSET</c> 在本家合法（LIMIT/OFFSET 两子句独立【文档·TDengine：SELECT 页】）。</summary>
    static QuickSqlOrm orm(PagingOrderingPolicy policy)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider,
            ConnectionString = SqlServer.OfflineConnectionString,
            PagingOrdering = policy,
        });

    /// <summary>ddl-qualified 那一格的组装根：本家没有默认 public 那颗兜底 —— <see cref="TableInfo.Schema"/>
    /// 为 null 时读侧按 <c>DATABASE()</c> 问。这一档的"表放在场外的 database"由
    /// <see cref="ModelConfiguration"/> 自建（与 ClickHouse 册 <c>ormWithSchema</c> 同一写法；
    /// 实体用 <see cref="ProbeMeter"/> —— SessionToken 首列不是 TIMESTAMP，在这一家建不出表）。</summary>
    static QuickSqlOrm ormWithSchema()
    {
        var model = new ModelConfiguration();

        model.Entity<ProbeMeter>().ToTable("QuickSqlMeter", "app");

        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));
    }

    // ── 夹具（G2 反射解析那一侧，名字故意不入 TestInfra 全家福 —— 与 ClickHouse 册 ProbeClick 同一立场）──

    /// <summary>本家合法形状的样板：首列 DateTime 声明为主键（[Key]），本家的隐含事实（首列 TIMESTAMP
    /// 系统自动设为主键）在契约侧就长这一形。表注释挂类头（[Description]），表注释那半能读回
    ///（INS_TABLES.table_comment）、能发（TryEmitTableComment），列注释没有这一格。</summary>
    [Description("电表读数表，probe 夹具一颗")]
    sealed class ProbeMeter
    {
        [Key]
        public DateTime Ts { get; set; }

        public double Current { get; set; }

        public string? Note { get; set; }

        public int Voltage { get; set; }
    }

    /// <summary>无主键那一形：insert-only 日志表的合法形状（首列时间戳仍然必须，写路径要主键的
    /// 地方内核本来就拒）。</summary>
    sealed class ProbeLog
    {
        public DateTime Ts { get; set; }

        public string Message { get; set; } = "";
    }

    /// <summary>复合主键那一形：两条 [Key]，在本家没有落点。</summary>
    sealed class ProbeCompositeKey
    {
        [Key]
        public DateTime Ts { get; set; }

        [Key]
        public string Code { get; set; } = "";
    }

    /// <summary>主键不在首列那一形：键点在第二颗成员上，与本家的隐含事实错位。</summary>
    sealed class ProbeMisplacedKey
    {
        public DateTime Ts { get; set; }

        [Key]
        public string Code { get; set; } = "";
    }

    /// <summary>int Id 血统那一形：首列整型、且被 Id 命名约定点成自增主键 —— 本家建不出表的那一类。</summary>
    sealed class ProbeIntId
    {
        public int Id { get; set; }

        public double Value { get; set; }
    }

    // ── 注册面：库种与内建表的那颗键 ────────────────────────────────

    [Fact]
    public void Kind_and_driver_key_are_the_only_two_things_this_family_brings_of_its_own()
    {
        // 单层 provider 没有"血统"可继承：它带给这份账的是一颗枚举值、一颗清单键、一套自写的
        // 10 个文件。清单键 "tdengine" 的 assembly（TDengine）按官方源仓的 csproj 写 ——
        // 包名 TDengine.Connector 不是程序集名（src/TDengine.csproj 没设 AssemblyName，
        // SDK 默认取项目文件名），这一格是本行最贵的坑。
        Assert.Equal(DbKind.TDengine, Provider.Kind);
        Assert.Equal("tdengine", TDengineProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.ClickHouse.ClickHouseProvider.DriverKey, TDengineProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.DuckDb.DuckDbProvider.DriverKey, TDengineProvider.DriverKey);
    }

    // ── 能力位：四位，写路径一组一颗都不声明 ─────────────────────────

    [Fact]
    public void The_capability_mask_is_four_bits_and_declares_none_of_the_write_path()
    {
        // 四位逐颗对"代码今天真的走到那一条语句"（产出文本与断言略列在下面的别格里）：
        // 分页两子句独立（Paging）、布尔原生型（Boolean_Type + 谓词 = true）、Guid 文本形 36 字符
        //（Guid_Char36 —— 本家没有 UUID 型，'D' 形恰好 36 字节）、database 限定（Schemas）。
        // 除之外一位都不声明。
        Assert.Equal(
            DbCapabilities.Paging_LimitOffset | DbCapabilities.Boolean_Type | DbCapabilities.Guid_Char36 |
            DbCapabilities.Schemas,
            Provider.Capabilities);

        // 本家刻意不声明的那几格，格位要点出"为什么不是漏记"：
        // —— Transactions：驱动 BeginDbTransaction 当场 throw，内核写路径逐句 autocommit 直发、
        //    公开 BeginTransaction 无位即抛（严禁静默假事务）；
        // —— Returning：EmitReturning 整格抛（写入那一路官方语句清单没有 RETURNING/OUTPUT 任何一支）；
        // —— Upsert_*：INSERT 同 ts 覆盖确实像天然 upsert，但重复时间戳的确切语义要活库钉死才敢接；
        // —— Comment_On_Column：本家没有列注释这一格（官方 column_definition 文法里没有 COMMENT 项）；
        // —— Alter_Column_Precision：MODIFY COLUMN 的官方句在，改型的行为边界没有读数，活库复核再翻；
        // —— MultiStatement/MultiResultSets/TempTable/Bulk_Copy：src/ 一行没发过。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Transactions));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Returning));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Upsert_OnConflict));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Comment_On_Column));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Alter_Column_Precision));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiStatement));

        // "建库"整格不可走：CREATE DATABASE 语句存在，但"连接串不带 db 直连再补建"那条锚点路
        // 没有读数（CanCreateDatabase=false；绕过这一颗来问走接口虚口那句 NotSupportedException）。
        Assert.False(Provider.CanCreateDatabase);
    }

    // ── 类型表：整数成对、时刻一型、五档抛 ───────────────────────────

    [Fact]
    public void Store_type_names_follow_the_tdengine_column_of_the_type_map()
    {
        // 成对的整数【文档·TDengine：数值型那几行】：byte 落 UNSIGNED 半边而不是往宽里并 ——
        // 落宽档丢掉无符号语义那一半才是静默退化（§10.5 的反面）。
        Assert.Equal("BOOL", SqGetStoreType(typeof(bool)));
        Assert.Equal("TINYINT", SqGetStoreType(typeof(sbyte)));
        Assert.Equal("TINYINT UNSIGNED", SqGetStoreType(typeof(byte)));
        Assert.Equal("SMALLINT", SqGetStoreType(typeof(short)));
        Assert.Equal("SMALLINT UNSIGNED", SqGetStoreType(typeof(ushort)));
        Assert.Equal("INT", SqGetStoreType(typeof(int)));
        Assert.Equal("INT UNSIGNED", SqGetStoreType(typeof(uint)));
        Assert.Equal("BIGINT", SqGetStoreType(typeof(long)));
        Assert.Equal("BIGINT UNSIGNED", SqGetStoreType(typeof(ulong)));
        Assert.Equal("FLOAT", SqGetStoreType(typeof(float)));
        Assert.Equal("DOUBLE", SqGetStoreType(typeof(double)));

        // 显式两档的 DECIMAL（3.3.6.0 才进场的型【文档·TDengine：类型页版本标注】）与字符串那格：
        // VARCHAR 的 n 量字节（GBase 8s 同一把尺），没说宽度取型上限（可变长存储下上限不预占空间）。
        Assert.Equal("DECIMAL(18,4)", SqGetStoreType(typeof(decimal)));
        Assert.Equal("VARCHAR(65517)", SqGetStoreType(typeof(string), maxChars: null));
        Assert.Equal("VARCHAR(256)", SqGetStoreType(typeof(string), maxChars: 64));
        Assert.Equal("VARCHAR(36)", SqGetStoreType(typeof(Guid)));
        Assert.Equal("TIMESTAMP", SqGetStoreType(typeof(DateTime)));
        Assert.Equal("NCHAR(1)", SqGetStoreType(typeof(char)));
        Assert.Equal("VARBINARY(65517)", SqGetStoreType(typeof(byte[])));

        // 四档抛（各档的因由写在 TDengineTypes 那几支里）：本族没有带偏移的时刻型、
        // 没有 DATE/TIME 独立型。
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(DateTimeOffset))).Message);
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(DateOnly))).Message);
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(TimeOnly))).Message);
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(TimeSpan))).Message);
    }

    static string SqGetStoreType(Type clrType, IDbProvider? family = null, int? maxChars = null)
    {
        var contract = orm().Schemas.Contract<ProbeMeter>();
        var baseColumn = contract.Columns.First(c => !c.IsPrimary);

        var probe = baseColumn with
        {
            ClrType = clrType,
            StoreTypeName = null,
            MaxChars = maxChars,
        };

        return (family ?? Provider).Types.GetStoreType(probe);
    }

    // ── CodeFirst：键形状校验面、列定义一段形 ───────────────────────

    [Fact]
    public void Create_table_emits_bare_definitions_and_lets_the_engine_own_the_primary_key()
    {
        var contract = orm().Schemas.Contract<ProbeMeter>();
        var writer = NewWriter();

        Provider.CodeFirst.EmitCreateTable(writer, contract);

        var sql = writer.ToSqlString();

        Verify("ddl-order", sql);

        // 主键不发任何子句【VendorDoc·TDengine：建表页——"表的第一个字段必须是 TIMESTAMP，
        // 系统会自动将其设为主键"】：发射前先校验键形状（恰一颗、是首列、型名 TIMESTAMP），
        // 发出来的 DDL 里不出现 PRIMARY KEY/CONSTRAINT 那一族。
        Checks.Has("CREATE TABLE `ProbeMeter` (`Ts` TIMESTAMP, `Current` DOUBLE, `Note` VARCHAR(65517), `Voltage` INT)", sql);

        // 列定义只有 "名" 型名一段：官方 column_definition 文法里没有 NULL/NOT NULL、没有 DEFAULT、
        // 没有列注释——可空列与非空列在 DDL 上同形（可空性在库里是事实而不是声明）。
        Checks.Absent(" NOT NULL", sql);
        Checks.Absent(" NULL", sql);
        Checks.Absent(" DEFAULT", sql);
        Checks.Absent("PRIMARY KEY", sql);
        Checks.Absent("CONSTRAINT", sql);
        Checks.Absent("COMMENT", sql);
    }

    [Fact]
    public void A_table_without_a_declared_key_still_requires_the_timestamp_first_column()
    {
        // 无主键放行（insert-only 日志表的合法形状）：DDL 照发，主键的隐含事实归引擎。
        var contract = orm().Schemas.Contract<ProbeLog>();
        var writer = NewWriter();

        Provider.CodeFirst.EmitCreateTable(writer, contract);

        Verify("ddl-no-primary-key", writer.ToSqlString());

        Checks.Has("CREATE TABLE `ProbeLog` (`Ts` TIMESTAMP, `Message` VARCHAR(65517))", writer.ToSqlString());
    }

    [Fact]
    public void An_explicit_database_qualifies_the_table_exactly_as_the_contract_writes_it()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema().Schemas.Contract<ProbeMeter>()));

        Verify("ddl-qualified", sql);

        // database 那一层是本家的"模式"（Schemas 能力位在本家的落点）：具名时发 `db`.`表`，
        // 未具名时发裸表名（由 DATABASE() 那支部答，读侧那一半在 SchemaReader 注释里）。
        Checks.Has("CREATE TABLE `app`.`QuickSqlMeter`", sql);
    }

    [Fact]
    public void A_contract_with_indexes_is_refused_in_one_piece()
    {
        // 本家没有用户索引那一档：查询面全靠引擎自建的数据骨架，契约里的 IndexContract 是
        // "表建成之后另有对象"那一格的形状，进不去。EmitCreateTable 遇契约索引非空整格拒
        //（不逐条递给 EmitCreateIndex 那一颗），带索引实体（如 Order 带 [Index(nameof(CreatedAt))]）
        // 在这一族建不出表。
        var raised = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<Order>());

            return string.Empty;
        });

        Assert.Contains("拒收契约索引", raised.Message);

        var indexRaised = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateIndex(writer, orm().Schemas.Contract<Order>().Table, orm().Schemas.Contract<Order>().Indexes[0]);

            return string.Empty;
        });

        Assert.Contains("没有用户索引", indexRaised.Message);
    }

    [Fact]
    public void The_key_shape_is_validated_before_any_ddl_leaves_the_house()
    {
        // 三条键形状拒收，各有一形夹具：复合键（两条 [Key]）、键不在首列、int Id 血统。
        var composite = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<ProbeCompositeKey>());

            return string.Empty;
        });

        Assert.Contains("拒收复合主键", composite.Message);

        var misplaced = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<ProbeMisplacedKey>());

            return string.Empty;
        });

        Assert.Contains("主键必须是首颗数据列", misplaced.Message);

        var intId = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<ProbeIntId>());

            return string.Empty;
        });

        // int Id 血统的实体在这一家建不出表：首列必须 TIMESTAMP，这一条与有没有声明主键无关。
        Assert.Contains("第一列都必须是 TIMESTAMP", intId.Message);
        Assert.Contains("int Id 血统的模型在这一家建不出表", intId.Message);
    }

    [Fact]
    public void An_identity_column_is_refused_at_the_emission_point()
    {
        // identity 那一格的拒收面在发射点：契约里手工带一颗 IsIdentity 列（G2 的自增约定只认整型主键，
        // 本册夹具走不到那颗路），EmitCreateTable 的 Definition 在那一列当场红。
        var contract = orm().Schemas.Contract<ProbeMeter>();
        var columns = contract.Columns.ToArray();

        columns[1] = columns[1] with { IsIdentity = true };

        var modified = new EntityContract(contract.ClrType, contract.Table, columns,
            contract.Indexes, contract.Relationships, contract.Collections, contract.SkipIndexes);

        var raised = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateTable(writer, modified);

            return string.Empty;
        });

        Assert.Contains("不建 identity 列", raised.Message);
    }

    [Fact]
    public void A_declared_column_default_is_refused_at_the_emission_point()
    {
        // DEFAULT 不在官方 column_definition 文法里：契约里带默认值的列在发射点拒收，
        // 缺省语义放到应用侧。
        var contract = orm().Schemas.Contract<ProbeMeter>();
        var columns = contract.Columns.ToArray();

        columns[3] = columns[3] with { DefaultValue = 220 };

        var modified = new EntityContract(contract.ClrType, contract.Table, columns,
            contract.Indexes, contract.Relationships, contract.Collections, contract.SkipIndexes);

        var raised = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateTable(writer, modified);

            return string.Empty;
        });

        Assert.Contains("拒收列默认值", raised.Message);
    }

    [Fact]
    public void Column_changes_and_the_table_comment_go_by_the_two_alter_shapes()
    {
        var contract = orm().Schemas.Contract<ProbeMeter>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(ProbeMeter.Note));
        var ts = contract.Columns.Single(c => c.MemberName == nameof(ProbeMeter.Ts));

        // 两条各自一句、各自一个书写器（塞进同一颗会得到连体文本）。
        var sql = string.Join(" ; ",
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, note)),
            Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, ts)));

        Verify("ddl-alter", sql);

        // 加列走 ADD COLUMN（本家加列一次一条、只能加在尾部）、改列走 MODIFY COLUMN 完整列定义
        //【VendorDoc·TDengine：改可变长类型长度的官方形】；两形都只有 "名" 型名一段。
        Checks.Has("ALTER TABLE `ProbeMeter` ADD COLUMN `Note` VARCHAR(65517)", sql);
        Checks.Has("ALTER TABLE `ProbeMeter` MODIFY COLUMN `Ts` TIMESTAMP", sql);

        // 列注释没有这一格（官方 column_definition 文法里没有 COMMENT 项，INS_COLUMNS 也没有备注列
        // —— 写不进也读不回），交 false 让那一格进"判不了"清单并指名。
        Assert.False(Provider.CodeFirst.TryEmitColumnComment(NewWriter(), contract.Table, note));

        // 表注释：ALTER TABLE … COMMENT '注'（VendorDoc·TDengine：表注释可用于普通表，置值形幂等）。
        // 读回那一半在 reader（INS_TABLES.table_comment）。
        var tableWriter = NewWriter();

        Assert.True(Provider.CodeFirst.TryEmitTableComment(tableWriter, contract.Table, contract.Table.Comment));
        Verify("ddl-table-comment", tableWriter.ToSqlString());
        Checks.Has("ALTER TABLE `ProbeMeter` COMMENT '电表读数表，probe 夹具一颗'", tableWriter.ToSqlString());
    }

    // ── 读侧：反引号、独立的两子句分页 ─────────────────────────────

    [Fact]
    public void Every_identifier_is_backtick_quoted_so_the_written_and_read_forms_are_the_same_object()
    {
        var sql = orm().Select<SessionToken>().ToSql();

        Verify("select-entity", sql);

        // IdentifierPolicy.TDengine 与 MySql 那颗四颗取值逐字同形（反引号 + 恒加引号 + 不折），
        // 而来路句句另立：反引号是本家的转义符【文档·TDengine：命名限制页——"为与 MySQL 兼容，
        // 引入反引号作为转义符"】，双引号在那边是字符串的字面量引号；不带引号的标识符会被库内部
        // 归一而转义形"保留原样"，所以恒加引号把契约里写的字形原样带过去。
        Checks.Has("FROM `QuickSqlSessionToken` AS `t0`", sql);
        Checks.Has("SELECT `t0`.`Id`, `t0`.`Value`, `t0`.`ExpiresAt`", sql);
        Checks.Absent("[", sql);
        Checks.Absent("\"", sql);
    }

    [Fact]
    public void Limit_and_offset_are_two_independent_clauses_and_parameters_stay_positional()
    {
        var sql = orm().Select<SessionToken>().OrderBy(o => o.ExpiresAt).Skip(10).Take(5).ToSql(out var parameters);

        Verify("paging-order-skip-take", sql);

        // 两子句各自独立【文档·TDengine：SELECT 页 LIMIT [n OFFSET m]】。参数是位置形 ?：
        // 文本里只有两颗 ?、编号序 = 文本序 = 绑定序（stmt2 按 ParameterCollection 的顺序 BindRow），
        // 驱动侧名字由 binder 翻成 @pN（stmt2 按 ParameterName 前缀分类，@ → data）。
        Assert.Equal(["?p0", "?p1"], parameters.Select(p => p.Name).ToArray());
        Assert.Equal(["@p0", "@p1"], parameters.Select(p => p.DriverName).ToArray());
        Assert.Equal([5, 10], parameters.Select(p => (int)p.Value!).ToArray());
        Checks.Has("ORDER BY `t0`.`ExpiresAt` ASC LIMIT ? OFFSET ?", sql);
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWS ONLY", sql);
    }

    [Fact]
    public void Take_without_skip_emits_no_offset_clause_at_all()
    {
        var sql = orm().Select<SessionToken>().OrderBy(o => o.Id).Take(5).ToSql(out var parameters);

        Verify("paging-take-only", sql);

        // 少一段就是少一段：没有"必须补 OFFSET 0"那颗哨兵（T-SQL 侧独有），也没有那颗"无上限"哨兵
        //（2^64−1 是 MySQL 两家的、2147483647 是虚谷那颗厂商写死的，本家与 ClickHouse/DuckDB 什么都不补）。
        Assert.Single(parameters);
        Checks.Has("ORDER BY `t0`.`Id` ASC LIMIT ?", sql);
        Checks.Absent("OFFSET", sql);
    }

    [Fact]
    public void Skip_only_needs_no_limit_sentinel_here()
    {
        var sql = orm(PagingOrderingPolicy.Allow).Select<SessionToken>().OrderBy(o => o.Id).Skip(30).ToSql();

        Verify("paging-skip-only", sql);

        // 官方语法 LIMIT [n OFFSET m] 里 OFFSET 是可缺省的独立子句【文档·TDengine：SELECT 页】：
        // 缺上限不补哨兵。吃不吃"只有 OFFSET"这一档据官方原文读是收的，未实测（第一台引擎先验那条）。
        Checks.Has("OFFSET ?", sql);
        Checks.Absent("LIMIT", sql);
        Checks.Absent("18446744073709551615", sql);
        Checks.Absent("2147483647", sql);
    }

    // ── 谓词形状 ─────────────────────────────────────────────────────

    [Fact]
    public void A_bool_operand_compares_against_the_true_literal()
    {
        var paid = true;

        var sql = orm().Select<Ticket>().Where(t => paid && t.Deleted).ToSql(out var parameters);

        Verify("where-boolean-flag", sql);

        // 布尔是本家的原生型【文档·TDengine：BOOL 型】，谓词两侧都落与列同一边的比较：
        // = true。IS TRUE 那一形是 PG 血统的 —— 那一颗在本家没有第二出去处（Bool 就是可比的那一型）。
        Assert.Single(parameters);
        Checks.Has("? = true AND `t0`.`Deleted` = true", sql);
        Checks.Absent("IS TRUE", sql);
        Checks.Absent("= 1", sql);
    }

    [Fact]
    public void An_empty_set_falls_back_to_the_arithmetic_always_false_predicate()
    {
        var none = Array.Empty<Guid>();

        var sql = orm().Select<SessionToken>().Where(t => none.Contains(t.Id)).ToSql();

        Verify("where-false-empty-set", sql);

        // 恒假保持基类默认 1 = 0：布尔字面量 true/false 在本家 SELECT 骨架里收不收没有读数，
        // 算术谓词是哪一家都收的那一形 —— 与 ClickHouse 那边（= 原生字面量）同形不同因，
        // 这边是"没有读数"不是"有原生型"。
        Checks.Has("WHERE 1 = 0", sql);
        Checks.Absent("WHERE false", sql);
    }

    // ── 写侧：INSERT 老老实实，UPDATE/DELETE 两整格拒发 ─────────────

    [Fact]
    public void A_plain_insert_takes_the_whole_column_list_and_never_a_returning()
    {
        var token = new SessionToken
        {
            Id = Guid.Empty,
            Value = "v1",
            ExpiresAt = new DateTime(2026, 3, 4, 5, 6, 7, 890),
        };

        var sql = orm().Insert(token).ToSql();

        Verify("insert-plain", sql);

        // 主键是契约列的一员（本家没有自增列，"把自增列剔出 INSERT 列清单"那一手对它不起作用）。
        // 占位符是位置形 ?：值按发射序进集合，名字不进文本。
        Checks.Has("INSERT INTO `QuickSqlSessionToken` (`Id`, `Value`, `ExpiresAt`) VALUES (?, ?, ?)", sql);
        Checks.Absent("RETURNING", sql);
        Checks.Absent("OUTPUT", sql);
    }

    [Fact]
    public void The_key_readback_has_whole_no_shape_and_says_so()
    {
        var token = new SessionToken
        {
            Id = Guid.NewGuid(),
            Value = "v1",
            ExpiresAt = new DateTime(2026, 3, 4, 5, 6, 7, 890),
        };

        var raised = Checks.Raised<QuickSqlException>(() => orm().Insert(token).ExplainKeys().ToSql());

        // 内核在问方言之前先查"有没有东西可回"：本家连自增列那一格都没有，Guid 主键由调用方
        // 自己生成 —— 整条问键的路先被内核（InsertEngine 回填 Preparation 那支）拦下。
        Assert.Contains("没有自增列", raised.Message);

        // 方言那颗整格拒不在上面那条路径上，单独点名：EmitReturning 抛而不是发（写入那一路
        // 官方语句清单里没有 RETURNING/OUTPUT 任何一支），所以 golden/tdengine/ 里没有
        // insert-with-keys 那一份 —— 本家断言抛、不落盘。
        var slot = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.Keys.EmitReturning(writer, orm().Schemas.Contract<SessionToken>().Columns);

            return string.Empty;
        });

        Assert.Contains("RETURNING/OUTPUT", slot.Message);
    }

    [Fact]
    public void Mutation_statements_are_refused_in_one_piece()
    {
        var update = Checks.Raised<QuickSqlException>(() =>
            orm().Update<SessionToken>().Set(t => t.Value, "x").Where(t => t.Id == Guid.Empty).ToSql());

        // 3.x 没有传统 UPDATE 语句【文档·TDengine：数据变更只有 INSERT 的 upsert 覆盖与 ALTER 那两族】
        // —— 不在写路径上，整格抛。
        Assert.Contains("没有 UPDATE 语句", update.Message);

        var deletion = Checks.Raised<QuickSqlException>(() =>
            orm().Delete<SessionToken>().Where(t => t.Id == Guid.Empty).ToSql());

        // DELETE 的官方形状只认"首列时间窗收束成一个确定闭区间"的条件
        //（https://docs.tdengine.com/tdengine-sql/08-delete/）—— 时间窗分析这一格本层没有，整格抛。
        Assert.Contains("时间窗", deletion.Message);
    }

    // ── "建库"整格不可走 ─────────────────────────────────────────────

    [Fact]
    public async Task The_create_database_question_has_no_route_here()
    {
        // CREATE DATABASE 语句存在，但"连接串不带 db 直连再补建"那条锚点路没有读数
        //（ws 驱动能不能空库连接只有引擎答得了）。闸与 ClickHouse 同案（CanCreateDatabase=false），
        // 绕过闸来问的那条路走接口虚口（NotSupportedException）。
        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            () => Provider.EnsureDatabaseExistsAsync("Database=some-db", TestContext.Current.CancellationToken));

        Checks.Has("请先手动建库", failure.Message);
    }

    // ── 槽位的形状读数 ───────────────────────────────────────────────

    [Fact]
    public void The_slot_shapes_are_the_ones_the_ledger_never_must_not_rely_on_memory_for()
    {
        // 六颗形状读数（与另几族同档，各颗的判据写在其所属的 provider 文件里）：
        // 参数前缀 '?'（位置形，GBase 8s 同款）、恒假/恒真保持基类算术谓词、'CONCAT' 那一格
        //（本家没有 || 运算符那一形）、LIKE 转义符取标准形。
        Assert.Equal('?', Provider.Dialect.ParameterPrefix);
        Assert.Equal("1 = 0", Provider.Dialect.EmptySetPredicate);
        Assert.Equal("1 = 1", Provider.Dialect.AlwaysTruePredicate);
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);
        Assert.Equal("CONCAT", Provider.Expressions.ConcatOperator);
        Assert.False(Provider.Expressions.SupportsRegex);
    }

    [Fact]
    public void A_parameter_crosses_the_driver_boundary_with_a_dollar_free_data_prefixed_name()
    {
        // stmt2 按 ParameterName 的前缀给值分类（$ → tags、@ → data、# → 表名，非这三档当场
        // ArgumentException【VendorDoc·TDengine·驱动源码 TDengineCommand.Statement】）——
        // 所以 binder 把驱动名翻成 @pN：文本、编号、日志都还是位置形那一套，只有跨进驱动对象
        // 那一步换名。DateTime 直绑原值（ClickHouse"参数化丢毫秒"那颗教训是那一家驱动的
        // 序列化问题，不预支到这一家）；Guid 归一成 'D' 形字符串（列型 VARCHAR(36)，驱动对
        // Guid 对象认不认没有读数，字符串是两家中性值）。
        var moment = new DateTime(2026, 11, 5, 9, 0, 0, 123);
        var parameter = TDengineValues.Instance.CreateParameter("?p0", moment, null);

        Assert.Equal("?p0", parameter.Name);
        Assert.Equal("@p0", parameter.DriverName);
        Assert.Equal(DbType.DateTime2, parameter.DbType);
        Assert.Equal(moment, parameter.Value);
        Assert.Equal(0, parameter.Size);

        Assert.True(TDengineValues.Instance.TryInlineLiteral(moment, null, out var literal));
        Assert.Equal("'2026-11-05 09:00:00.123'", literal);

        var guid = TDengineValues.Instance.CreateParameter("?p1", new Guid("0123456789abcdef0123456789abcdef"), null);

        Assert.Equal("@p1", guid.DriverName);
        Assert.Equal(DbType.String, guid.DbType);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", guid.Value);
        Assert.Equal(36, guid.Size);
    }

    // ── 稳定表面（v0.39 第六笔）：声明位、CREATE STABLE、INSERT USING 自动建表 ──

    /// <summary>占位符引用了没声明的 TAG 成员：契约期红（EntityContractFactory.MapStable 那一份，
    /// 两侧管线同一句）。模板在插入时用行内 TAG 值填空，占位符落到数据列上就是
    /// "值每行都在变而子表名只能有一个"的语义错。</summary>
    [TDengineStable(Subtable = "meters_{missing}")]
    sealed class ProbeBadTemplate
    {
        [Key]
        public DateTime Ts { get; set; }

        [TDengineTag]
        public string Device { get; set; } = "";
    }

    /// <summary>DECIMAL 的 TAG：厂商明文"仅支持普通列、暂不支持标签列"，发射点红。</summary>
    [TDengineStable]
    sealed class ProbeDecimalTag
    {
        [Key]
        public DateTime Ts { get; set; }

        [TDengineTag]
        public decimal Weight { get; set; }
    }

    /// <summary>一颗 TAG 都没有：稳定表至少要一颗 TAG（厂商硬性事实），契约期红。</summary>
    [TDengineStable]
    sealed class ProbeStableNoTags
    {
        [Key]
        public DateTime Ts { get; set; }

        public double Current { get; set; }
    }

    /// <summary>稳定表与分表并用：两套"一个实体对多张表"，发射点红。</summary>
    [DaSchema.Table("QuickSqlSplitStable_{year}{month}")]
    [SplitTable(SplitKind.Month, nameof(Ts))]
    [TDengineStable(Subtable = "QuickSqlSplitStable_{device}")]
    sealed class ProbeSplitStable
    {
        [Key]
        public DateTime Ts { get; set; }

        [TDengineTag]
        public string Device { get; set; } = "";
    }

    /// <summary>没声明 Subtable 模板：DDL 与查询面照常，插入面没有子表名的来路，发射点指路。</summary>
    [TDengineStable]
    sealed class ProbeStableNoTemplate
    {
        [Key]
        public DateTime Ts { get; set; }

        [TDengineTag]
        public string Device { get; set; } = "";
    }

    [Fact]
    public void A_stable_declaration_emits_create_stable_with_the_tags_clause()
    {
        var contract = orm().Schemas.Contract<StableMeter>();
        var writer = NewWriter();

        Provider.CodeFirst.EmitCreateTable(writer, contract);

        var sql = writer.ToSqlString();

        Verify("ddl-stable", sql);

        // TAGS 子句在列定义之后【VendorDoc·TDengine：超级表页】；TAG 列的声明序就是 TAGS 的列序；
        // 首列 TIMESTAMP 校验收窄到数据列（Device/GroupId 声明在后面，数据列第一颗仍是 Ts）。
        // 变长 TAG 的库侧上限 16 382 字节这一格在这里读不到（64 chars × 4 = 256 没到线）。
        Checks.Has("CREATE STABLE `QuickSqlStableMeter` (`Ts` TIMESTAMP, `Current` DOUBLE, `Voltage` INT) TAGS (`Device` VARCHAR(256), `GroupId` INT)", sql);
        Checks.Absent("CREATE TABLE", sql);
        Checks.Absent(" NOT NULL", sql);
        Checks.Absent("PRIMARY KEY", sql);
    }

    [Fact]
    public void A_stable_insert_resolves_the_subtable_from_the_row_tags_and_lifts_tags_into_the_using_clause()
    {
        var meter = new StableMeter
        {
            Ts = new DateTime(2026, 11, 6, 9, 0, 0),
            Current = 10.5,
            Voltage = 220,
            Device = "d001",
            GroupId = 2,
        };

        var chunk = orm().Insert(meter).Explain().Chunks[0];

        Verify("insert-using-tags", chunk.Sql.Text);

        // 子表名 = Subtable 模板填行内 TAG 值；TAG 值内联（STMT2 的参数化自动建表有明文行为缺口，
        // 子表不存在时报错【VendorDoc·TDengine：stmt 页】——文本形把自动建表留在 shell 同侧）；
        // 数据列清单摘掉 TAG 两颗，参数照常位置形 ?（文本不进参数名）。
        Checks.Has("INSERT INTO `QuickSqlStableMeter_d001` USING `QuickSqlStableMeter` TAGS ('d001', 2) (`Ts`, `Current`, `Voltage`) VALUES (?, ?, ?)", chunk.Sql.Text);
        Assert.Equal(["?p0", "?p1", "?p2"], chunk.Sql.Parameters.Select(p => p.Name).ToArray());
        Assert.Equal(["@p0", "@p1", "@p2"], chunk.Sql.Parameters.Select(p => p.DriverName).ToArray());
        Checks.Absent("'d001'", chunk.Sql.Text[(chunk.Sql.Text.IndexOf("VALUES", StringComparison.Ordinal))..]);
    }

    [Fact]
    public void A_null_tag_goes_by_the_partial_tag_list_form()
    {
        var meter = new StableMeter
        {
            Ts = new DateTime(2026, 11, 6, 9, 0, 0),
            Current = 10.5,
            Voltage = 220,
            Device = "d002",
            GroupId = null,
        };

        var sql = orm().Insert(meter).ToSql();

        Verify("insert-using-partial-tags", sql);

        // 部分标签列形式【VendorDoc·TDengine：插入页——"可以只指定部分标签列，其余标签列为 NULL"】：
        // 名清单在 USING 之后、TAGS 之前，只列非空的那颗；"TAGS(null) 字面量"那一形没有明文，不走。
        Checks.Has("INSERT INTO `QuickSqlStableMeter_d002` USING `QuickSqlStableMeter` (`Device`) TAGS ('d002') (`Ts`, `Current`, `Voltage`) VALUES (?, ?, ?)", sql);
    }

    [Fact]
    public void A_mixed_tag_batch_is_refused_in_one_piece()
    {
        // 一条 INSERT … USING 只服务一颗子表；对已存在子表厂商会忽略语句里的 TAG 值（不报错），
        // 所以混 TAG 的批必须先红，不能靠"发出去再看"。
        var raised = Checks.Raised<QuickSqlException>(() =>
            orm().InsertRange(
            [
                new StableMeter { Ts = new DateTime(2026, 11, 6, 9, 0, 0), Current = 10.5, Device = "d001", GroupId = 2 },
                new StableMeter { Ts = new DateTime(2026, 11, 6, 9, 0, 1), Current = 10.6, Device = "d002", GroupId = 2 },
            ]).ToSql());

        Assert.Contains("TAG 值不一致", raised.Message);
    }

    [Fact]
    public void The_stable_declaration_gates_fire_at_the_two_layers_where_they_belong()
    {
        // 契约期两颗：占位符引用了没声明的 TAG 成员（MapStable 那一份，两侧管线同一句）、
        // 一颗 TAG 都没有（稳定表至少要一颗 TAG，厂商硬性事实）。
        var badTemplate = Checks.Raised<QuickSqlException>(() => orm().Schemas.Contract<ProbeBadTemplate>());

        Assert.Contains("不是已声明的 TAG 成员", badTemplate.Message);

        var noTags = Checks.Raised<QuickSqlException>(() => orm().Schemas.Contract<ProbeStableNoTags>());

        Assert.Contains("一颗 TAG 都没有", noTags.Message);

        // 契约期第三颗：稳定表与分表不并用——这一格在 EntityContractFactory.Build 收口时就红
        //（不等到发射：SplitTablesScript/建片链对分表契约逐片调 EmitCreateTable，留到发射才判
        // 会让脚本路先折进"普通表"那一支，发出一张形状说错的 CREATE TABLE）。
        // CodeFirst 那一侧的同款守卫因此是虚口保真（防手工拼契约），测试点的是契约期这颗。
        var splitStable = Checks.Raised<QuickSqlException>(() => orm().Schemas.Contract<ProbeSplitStable>());

        Assert.Contains("同时声明了超级表与分表", splitStable.Message);

        // 发射点一颗：DECIMAL 不可作 TAG（厂商明文"仅支持普通列"）。
        var decimalTag = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<ProbeDecimalTag>());

            return string.Empty;
        });

        Assert.Contains("DECIMAL 仅支持普通列", decimalTag.Message);
    }

    [Fact]
    public void A_stable_insert_without_a_subtable_template_points_at_the_declaration()
    {
        // 没声明 Subtable 模板：DDL 与查询面照常（ProbeStableNoTemplate 的契约建得出来），
        // 插入面没有子表名的来路 —— 发射点指路，不发"插稳定名"那种非法对象。
        var raised = Checks.Raised<QuickSqlException>(() =>
            orm().Insert(new ProbeStableNoTemplate
            {
                Ts = new DateTime(2026, 11, 6, 9, 0, 0),
                Device = "d001",
            }).ToSql());

        Assert.Contains("没有声明子表名模板", raised.Message);
    }

    [Fact]
    public void The_subtable_script_mouth_emits_the_create_table_using_form()
    {
        // 子表建表脚本口（宿主手工口，照 ClickHouse EmitAddSkipIndex 的先例：不在 SPI 上，
        // Core 的结构同步不走这一句）。官方形 CREATE TABLE 子表 USING 稳定 TAGS (…)；
        // TAG 值按声明序给、一律内联（脚本口不发参数）。
        var contract = orm().Schemas.Contract<StableMeter>();
        var writer = NewWriter();

        ((TDengineCodeFirst)Provider.CodeFirst).EmitCreateSubtable(writer, contract, "QuickSqlStableMeter_d003", "d003", 7);

        Verify("ddl-subtable", writer.ToSqlString());

        Checks.Has("CREATE TABLE `QuickSqlStableMeter_d003` USING `QuickSqlStableMeter` TAGS ('d003', 7)", writer.ToSqlString());

        // 颗数对齐：给少一颗当场指路，不猜缺省。
        var count = Checks.Raised<QuickSqlException>(() =>
        {
            var shortWriter = NewWriter();

            ((TDengineCodeFirst)Provider.CodeFirst).EmitCreateSubtable(shortWriter, contract, "QuickSqlStableMeter_d004", "d004");

            return string.Empty;
        });

        Assert.Contains("一颗不多一颗不少", count.Message);
    }

    static SqlWriter NewWriter() => new(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }
}
