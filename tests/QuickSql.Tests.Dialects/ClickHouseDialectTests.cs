using System.Data;
using System.Data.Common;
using System.ComponentModel;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.ClickHouse;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>ClickHouse 这颗库种的 SQL <b>文本</b>（§9.1 的 ClickHouse 那一行兑现里离线的那一半；
/// 单层 provider 的第二颗【文档口径行】）。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与另九族的离线套件同一分工：碰驱动即红（§3），没有"顺带把驱动拉进来"的口。
///
/// <para><b>本册走 golden/clickhouse/ 的磁盘基线而不走对拍</b>，判据与 DuckDB 那一册同一条：
/// 单层的两颗【文档口径行】产出与任何一族都不同形 —— 可空不写在 DDL 修饰那一格而在型包裹
///（<c>Nullable(T)</c>，没有第三形）、主键不发约束子句而发进 ENGINE 的存储子句（
/// <c>MergeTree ORDER BY … PRIMARY KEY …</c>）、UPDATE/DELETE 两整格拒发
///【文档·ClickHouse：alter 页与 https://clickhouse.com/docs/sql-reference/statements/delete 】。
/// 照金仓册那颗烧过的钉子抄一遍：<see cref="Gold"/> 常量<b>不能省</b> —— 形状名与另几族是
/// 同一批（<c>select-entity</c>、<c>paging-order-skip-take</c>、<c>ddl-order</c>…），
/// 第一次生成时没带它就等于把 <c>golden/duckdb/</c> 那批基线改写成了本家的文本。</para>
///
/// <para><b>这些样本证明的是回归，而不是孤零零的"自产基线"</b>：2026-10-20 第一台引擎（server
/// 26.9.21.9 ∶ 驱动 <c>ClickHouse.Client</c> 7.14.0）到场，golden 十份里九份被逐字执行过（见
/// §13.5 第十七本台账的本笔回写与 <c>obj/logs/v39-ch-*.md</c>），仍留在本册的只是"别再漂"那一半职能。
/// insert-with-keys 在本家
/// <em>不落盘</em> —— <see cref="ClickHouseKeys.EmitReturning"/> 整格抛，基线改判为"断言抛"，
/// 所以本家 10 份。主键样本用 <see cref="SessionToken"/>（Guid 主键、无索引）代替
/// <see cref="Order"/>：<c>Order</c> 带 <c>[Index(nameof(CreatedAt))]</c>，而本家的建表
/// 遇契约索引整格拒 —— 带索引实体那一条另立一格点名（见
/// <see cref="A_contract_with_indexes_is_refused_in_one_piece"/>）。</para></summary>
public sealed class ClickHouseDialectTests
{
    static readonly IDbProvider Provider = ClickHouse.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名（与 DuckDB 册的 <c>Gold</c> 同一颗钉子）。<b>这一层不能省</b>：
    /// 形状名与另几族是同一批，第一次生成时没带它就等于把别家的基线整目录改写成 ClickHouse 的文本
    ///（金仓册注释里那句"复跑整套离线套件才发现，已按 git 还原"）。</summary>
    const string Gold = "clickhouse";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Gold);

    static QuickSqlOrm orm() => ClickHouse.Orm();

    /// <summary>只有分页用例需要改选项：<c>Skip/Take</c> 无 <c>OrderBy</c> 在共用层默认是抛（§9.4），
    /// 而只写 <c>OFFSET</c> 在本家合法（LIMIT/OFFSET 两子句独立、不要求 ORDER BY
    ///【文档·ClickHouse：SELECT 页】）。</summary>
    static QuickSqlOrm orm(PagingOrderingPolicy policy)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider,
            ConnectionString = SqlServer.OfflineConnectionString,
            PagingOrdering = policy,
        });

    /// <summary>ddl-qualified 那一格的组装根：本家没有默认 public 那颗兜底 —— <see cref="TableInfo.Schema"/>
    /// 为 null 时发不出限定名，读侧按 <c>currentDatabase()</c> 问。这一档的"表放在场外的 database"
    /// 由 <see cref="ModelConfiguration"/> 自建（与 DuckDB 册 <c>ormWithSchema</c> 同一写法，
    /// 实体因带索引换成 <see cref="SessionToken"/>）。</summary>
    static QuickSqlOrm ormWithSchema()
    {
        var model = new ModelConfiguration();

        model.Entity<SessionToken>().ToTable("QuickSqlSessionToken", "app");

        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));
    }

    // ── 注册面：库种与内建表的那颗键 ────────────────────────────────

    [Fact]
    public void Kind_and_driver_key_are_the_only_two_things_this_family_brings_of_its_own()
    {
        // 单层 provider 没有"血统"可继承：它带给这份账的是一颗枚举值、一颗清单键、一套自写的
        // 11 个文件（§2 那条"禁止跨 provider 引用"的代价就是这一份拷贝）。清单键 "clickhouse" 的
        // assembly（ClickHouse.Client）按官方 ADO.NET 客户端页写【文档·ClickHouse】，与
        // duckdb 那颗（DuckDB.NET.Data）互不相同：本家不借邻家的臂。
        Assert.Equal(DbKind.ClickHouse, Provider.Kind);
        Assert.Equal("clickhouse", ClickHouseProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.DuckDb.DuckDbProvider.DriverKey, ClickHouseProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.PostgreSql.PostgreSqlProvider.DriverKey, ClickHouseProvider.DriverKey);
    }

    // ── 能力位：七位（beta.26 批起列注释那颗进列）、写路径一组一颗都不声明 ──

    [Fact]
    public void The_capability_mask_is_seven_bits_and_declares_none_of_the_write_path()
    {
        // 七位逐颗对 §4 那句"代码今天真的走到那一条语句"（产出文本与断言略列在下面的别格里）：
        // 分页两子句独立（Paging）、布尔原生型（Boolean_Type + 谓词 = true）、UUID 16 字节（Raw16）、
        // database 限定（Schemas）、MODIFY COLUMN 那一句（Alter_Column_Precision）、官方 identifiers
        // 页点名大小写（CaseSensitive）、列注释（Comment_On_Column：COMMENT COLUMN 那一句 + 读侧
        // system.columns.comment 两半 —— 发射与读回都在活库逐字回放过，出处 replay-5/replay-6，
        // 记 §13.5 第十七本）。除之外一位都不声明。
        Assert.Equal(
            DbCapabilities.Paging_LimitOffset | DbCapabilities.Boolean_Type | DbCapabilities.Guid_Raw16 |
            DbCapabilities.Schemas | DbCapabilities.Alter_Column_Precision | DbCapabilities.CaseSensitive_Identifiers |
            DbCapabilities.Comment_On_Column,
            Provider.Capabilities);

        // 本家刻意不声明的那几格，格位要点出"为什么不是漏记"：
        // —— Transactions：内核写路径逐句 autocommit 直发、多语句不原子，公开 BeginTransaction 无位即抛
        //    （ClickHouse 重开·笔二的那颗表态格，行为在 LLM 册的迟发那一条并不例外）；
        // —— Returning：EmitReturning 整格抛（点击那一路官方语句清单没有 RETURNING/OUTPUT 任何一支【文档没逐页回读】)；
        // —— Upsert_*：MERGE/ON CONFLICT/ON DUPLICATE 都没有【文档没逐页回读】；
        // —— MultiStatement/MultiResultSets/TempTable/Bulk_Copy：src/ 一行没发过。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Transactions));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Returning));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Upsert_OnConflict));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiStatement));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiResultSets));

        // "建库"整格不可走：本家连接串里那颗 database 当场就要在场，没有"锚点库再补建"的路
        //（CanCreateDatabase=false；绕过这一颗来问走接口虚口那句 NotSupportedException）。
        Assert.False(Provider.CanCreateDatabase);
    }

    // ── 类型表：整数成对、时刻带精度、四档抛 ─────────────────────────

    [Fact]
    public void Store_type_names_follow_the_clickhouse_column_of_the_type_map()
    {
        // 成对的整数【文档·ClickHouse：数值型那几行】：byte 落 UInt8 而不是往宽里并 ——
        // 落宽档丢掉无符号语义那一半才是静默退化（§10.5 的反面）。
        Assert.Equal("Bool", SqGetStoreType(typeof(bool)));
        Assert.Equal("Int8", SqGetStoreType(typeof(sbyte)));
        Assert.Equal("UInt8", SqGetStoreType(typeof(byte)));
        Assert.Equal("Int16", SqGetStoreType(typeof(short)));
        Assert.Equal("UInt16", SqGetStoreType(typeof(ushort)));
        Assert.Equal("Int32", SqGetStoreType(typeof(int)));
        Assert.Equal("UInt32", SqGetStoreType(typeof(uint)));
        Assert.Equal("Int64", SqGetStoreType(typeof(long)));
        Assert.Equal("UInt64", SqGetStoreType(typeof(ulong)));
        Assert.Equal("Float32", SqGetStoreType(typeof(float)));
        Assert.Equal("Float64", SqGetStoreType(typeof(double)));

        // 显式两档的 Decimal 与字符串那格的"无宽度"【文档·ClickHouse：Decimal/String 两页】：
        // MaxChars 只是仓库侧的尺子，DDL 的型名里本来就没有那颗数。
        Assert.Equal("Decimal(18,4)", SqGetStoreType(typeof(decimal)));
        Assert.Equal("String", SqGetStoreType(typeof(string), maxChars: null));
        Assert.Equal("String", SqGetStoreType(typeof(string), maxChars: 64));
        Assert.Equal("UUID", SqGetStoreType(typeof(Guid)));
        Assert.Equal("DateTime64(3)", SqGetStoreType(typeof(DateTime)));
        Assert.Equal("Date", SqGetStoreType(typeof(DateOnly)));

        // 四档抛（各档的因由写在 ClickHouseTypes 那四支里）：本族没有带偏移的时刻型、
        // 没有时刻/跨度型、没有大对象那一档。
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(DateTimeOffset))).Message);
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(TimeOnly))).Message);
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(TimeSpan))).Message);
        Assert.Contains("无处可发", Checks.Raised<QuickSqlException>(() => SqGetStoreType(typeof(byte[]))).Message);
    }

    /// <summary>注释两槽与跳过索引那几格的夹具：表注释（[Description] 打类上）、列注释（打属性上）、
    /// 跳过索引三颗（两颗列挂、一颗类挂表达式式）。名字故意不入 TestInfra 全家福 —— 这一册走 G2
    /// 反射解析那一侧就够（生成器不认它，未标注实体退 G2，§12.7 同一条）；两颗管线（G1 ∶ G2）的
    /// 对拍纪律由共用收尾器兜住，夹具不进那套对拍阵容。</summary>
    [Description("点击事件表，probe 夹具一颗")]
    // 类挂那一形：必须给表达式的形状 —— 表达式原样抄（"toMinute(EventAt)"，收尾器两边同一句管）。
    // 表注释与表级声明两颗都挂类头，GetCustomAttributes 按声明序取，与反射侧次序一致。
    [ClickHouseSkipIndex(SkipIndexKind.BloomFilter, Expression = "toMinute(EventAt)", Granularity = 2)]
    sealed class ProbeClick
    {
        public int Id { get; set; }

        // 列挂缺省那一形：表达式就是这颗列（"EventAt"），名字约定给（QSI_ProbeClick_EventAt_minmax）。
        [ClickHouseSkipIndex(SkipIndexKind.MinMax)]
        public DateTime EventAt { get; set; }

        // 列挂带参数那一形：GRANULARITY 随声明走（2），名字约定给。
        [ClickHouseSkipIndex(SkipIndexKind.BloomFilter, Granularity = 2)]
        public string Title { get; set; } = "";

        [Description("备注文字，probe 一颗")]
        public string Note { get; set; } = "";
    }

    static string SqGetStoreType(Type clrType, IDbProvider? family = null, int? maxChars = null)
    {
        var contract = orm().Schemas.Contract<SessionToken>();
        var baseColumn = contract.Columns.First(c => !c.IsIdentity);

        var probe = baseColumn with
        {
            ClrType = clrType,
            StoreTypeName = null,
            MaxChars = maxChars,
        };

        return (family ?? Provider).Types.GetStoreType(probe);
    }

    // ── CodeFirst：MergeTree 存储子句、Nullable 包裹、无第三形 ───────

    [Fact]
    public void Create_table_puts_the_primary_key_into_the_engine_storage_clause()
    {
        var contract = orm().Schemas.Contract<SessionToken>();
        var writer = NewWriter();

        Provider.CodeFirst.EmitCreateTable(writer, contract);

        var sql = writer.ToSqlString();

        Verify("ddl-order", sql);

        // 主键不发列内也不发表级约束子句，而发进 ENGINE 那一段【文档·ClickHouse：CREATE TABLE 页，
        // PRIMARY KEY 必须与 ORDER BY 的开头对齐】：ORDER BY (cols) PRIMARY KEY (cols)。
        Checks.Has("CREATE TABLE \"QuickSqlSessionToken\"", sql);
        Checks.Has("ENGINE = MergeTree ORDER BY (\"Id\") PRIMARY KEY (\"Id\")", sql);

        // 可空性的两形互斥【文档·ClickHouse：CREATE TABLE 页 —— Nullable 型再上 NULL/NOT NULL 修饰当场报错】：
        // 可空发 Nullable(T) 不带修饰、非空发裸型 + NOT NULL，此外没有第三形。
        Checks.Has("\"Id\" UUID NOT NULL", sql);
        Checks.Has("\"Value\" Nullable(String)", sql);
        Checks.Has("\"ExpiresAt\" DateTime64(3) NOT NULL", sql);
        Checks.Absent(" CONST", sql);
        Checks.Absent(" Nullable(String) NULL", sql);
        Checks.Absent("SERIAL", sql);
    }

    [Fact]
    public void An_explicit_database_qualifies_the_table_exactly_as_the_contract_writes_it()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema().Schemas.Contract<SessionToken>()));

        Verify("ddl-qualified", sql);

        // database 那一层是本家的"模式"（Schemas 能力位在本家的落点）：具名时发 "db".."表"，
        // 未具名时发裸表名（由 currentDatabase() 那支部答，读侧那一半在 SchemaReader 注释里）。
        Checks.Has("CREATE TABLE \"app\".\"QuickSqlSessionToken\"", sql);
        Checks.Has("ENGINE = MergeTree ORDER BY (\"Id\") PRIMARY KEY (\"Id\")", sql);
    }

    [Fact]
    public void A_contract_with_indexes_is_refused_in_one_piece()
    {
        // 本家没有 CREATE INDEX 语句级格：索引是建表语句里列/表级 INDEX … GRANULARITY … 的数据骨架声明
        //【文档·ClickHouse：CREATE TABLE 页】，契约里的 IndexContract 是"表建成之后另有对象"那一格的
        // 形状，进不去。所以 EmitCreateTable 遇契约索引非空整格拒（不逐条递给 EmitCreateIndex 那一颗），
        // 带索引实体（如 Order 带 [Index(nameof(CreatedAt))]）在这一族建不出表 —— 这格是改判点：
        // 生成的 ddl-order/ddl-qualified 两盘基线因此换成了无索引的 SessionToken。
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

        Assert.Contains("CREATE INDEX", indexRaised.Message);
    }

    [Fact]
    public void Column_changes_bind_the_same_nullable_shape()
    {
        var contract = orm().Schemas.Contract<SessionToken>();
        var value = contract.Columns.Single(c => c.MemberName == nameof(SessionToken.Value));
        var expires = contract.Columns.Single(c => c.MemberName == nameof(SessionToken.ExpiresAt));

        // 两条各自一句、各自一个书写器（塞进同一颗会得到连体文本）。
        var sql = string.Join(" ; ",
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, value)),
            Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, expires)));

        Verify("ddl-alter-and-comment", sql);

        // 改列走 MODIFY COLUMN 完整列定义【文档·ClickHouse：alter 页】；可空那一半读 Definition 里
        // 同一份判据（NullableInStore），所以"改一次精度"不会把形状改岔。
        // 列注释那一格 beta.26 批起接上了（下一格断言它），本格只管改列两形。
        Checks.Has("ALTER TABLE \"QuickSqlSessionToken\" ADD COLUMN \"Value\" Nullable(String)", sql);
        Checks.Has("ALTER TABLE \"QuickSqlSessionToken\" MODIFY COLUMN \"ExpiresAt\" DateTime64(3) NOT NULL", sql);
    }

    [Fact]
    public void Column_and_table_comments_go_by_the_two_alter_shapes()
    {
        var contract = orm().Schemas.Contract<ProbeClick>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(ProbeClick.Note));

        // 列注释：ALTER TABLE … COMMENT COLUMN "列" '注'【实测·2026-10-20 活库 26.9.21.9 ∶ 驱动 7.14.0，
        // replay-6：发射、幂等重发、system.columns.comment 原字读回三件事都过】。
        var writer = NewWriter();

        Assert.True(Provider.CodeFirst.TryEmitColumnComment(writer, contract.Table, note));
        Verify("ddl-column-comment", writer.ToSqlString());
        Checks.Has("ALTER TABLE \"ProbeClick\" COMMENT COLUMN \"Note\" '备注文字，probe 一颗'", writer.ToSqlString());

        // 表注释：ALTER TABLE … MODIFY COMMENT '注'【同出处：裸 COMMENT 形被服务端拒回 Expected COLUMN，
        // MODIFY 形收】。读回那一半在 reader（system.tables.comment 表注半 / system.columns.comment 列注半）。
        var tableWriter = NewWriter();

        Assert.True(Provider.CodeFirst.TryEmitTableComment(tableWriter, contract.Table, contract.Table.Comment));
        Verify("ddl-table-comment", tableWriter.ToSqlString());
        Checks.Has("ALTER TABLE \"ProbeClick\" MODIFY COMMENT '点击事件表，probe 夹具一颗'", tableWriter.ToSqlString());
    }

    [Fact]
    public void Skip_index_declarations_land_in_the_create_table_skeleton()
    {
        var contract = orm().Schemas.Contract<ProbeClick>();
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, contract));

        Verify("ddl-skip-index", sql);

        // 声明这一档（[ClickHouseSkipIndex]）进建表括号清单，与列同一份逗号表——活库兼收列式
        // 与表达式式两半（replay-5/6 均收，读回见 system.data_skipping_indices）。
        // 列挂缺省名 QSI_{表}_{列}_{档}、缺省表达式就是那颗列；GRANULARITY 没写发本家缺省 1。
        Checks.Has("INDEX \"QSI_ProbeClick_EventAt_minmax\" \"EventAt\" TYPE minmax GRANULARITY 1", sql);

        // 格位参数随声明走（Title 的 [ClickHouseSkipIndex(…, Granularity = 2)]）：
        Checks.Has("INDEX \"QSI_ProbeClick_Title_bloom\" \"Title\" TYPE bloom_filter GRANULARITY 2", sql);

        // 类挂那一档：表达式原样抄、名字按约定序号给（QSI_{表}_{序号}）。
        Checks.Has("INDEX \"QSI_ProbeClick_1\" toMinute(EventAt) TYPE bloom_filter GRANULARITY 2", sql);

        // 表注释不发建表尾（SPI 单路 TryEmitTableComment 管）——实体的 [Description] 不内联。
        Checks.Absent(" COMMENT", sql);

        // 建成之后后补一颗：ALTER … ADD INDEX（replay-5/6 收，读回 system.data_skipping_indices）。
        // Core 的结构同步不找这一句（索引差按 IndexContract 算、本家整格拒；跳过索引在 schema 读侧
        // 没有形状）—— 这是给宿主的手工口，不假装"同步会带它"。
        var addWriter = NewWriter();

        // Cast 本家的实类：EmitAddSkipIndex 是这一家的手工口，不在 SPI 上（同上段的活库出处）。
        var codeFirst = (ClickHouseCodeFirst)Provider.CodeFirst;

        codeFirst.EmitAddSkipIndex(addWriter, contract.Table,
            contract.SkipIndexes.Single(s => s.MemberName == nameof(ProbeClick.EventAt)));

        Verify("ddl-add-skip-index", addWriter.ToSqlString());
        Checks.Has("ALTER TABLE \"ProbeClick\" ADD INDEX \"QSI_ProbeClick_EventAt_minmax\" \"EventAt\" TYPE minmax GRANULARITY 1", addWriter.ToSqlString());
    }

    [Fact]
    public void A_set_grade_skip_index_without_max_rows_refuses_at_the_emission_point()
    {
        // 档位参数是型名的一部分：set 缺 max_rows 发出去就是一句库不认的 TYPE —— 发射侧当场红，
        // 不把这条 ClickHouse 语义塞进中性契约（§10.3）。
        var contract = orm().Schemas.Contract<ProbeClick>();

        var raised = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = NewWriter();

            ((ClickHouseCodeFirst)Provider.CodeFirst).EmitAddSkipIndex(writer, contract.Table, contract.SkipIndexes[0] with
            {
                Kind = SkipIndexKind.Set,
            });

            return string.Empty;
        });

        Assert.Contains("SetMaxRows（set 档的 max_rows）", raised.Message);
    }

    // ── 读侧：双引号、独立的两子句分页 ─────────────────────────────

    [Fact]
    public void Every_identifier_is_double_quoted_so_the_written_and_read_forms_are_the_same_object()
    {
        var sql = orm().Select<SessionToken>().ToSql();

        Verify("select-entity", sql);

        //IdentifierPolicy.ClickHouse 与 Gbase8s 那颗四颗取值逐字同形（双引号 + 判等区分 + 不折），
        // 而来路句句另立：双引号合法【文档·ClickHouse：syntax 页 identifiers 段】、"判等区分"取 true
        // 是照_SQLFluff 一类第三方口径与通用认知收的【推断·官方语法页只写了引号与 keywords 规则，
        // 没有一句逐字写"identifiers are case-sensitive"，差额记 §14，第一台引擎验证】。
        // 判等区分取 true 让 NeedsQuote 对每个标识符都返回真，所以这一册里没有一个裸 name 的标识符。
        Checks.Has("FROM \"QuickSqlSessionToken\" AS \"t0\"", sql);
        Checks.Has("SELECT \"t0\".\"Id\", \"t0\".\"Value\", \"t0\".\"ExpiresAt\"", sql);
        Checks.Absent("[", sql);
        Checks.Absent("`", sql);
    }

    [Fact]
    public void Limit_and_offset_are_two_independent_clauses_and_the_parameter_order_follows_the_emission()
    {
        var sql = orm().Select<SessionToken>().OrderBy(o => o.ExpiresAt).Skip(10).Take(5).ToSql(out var parameters);

        Verify("paging-order-skip-take", sql);

        // 两子句各自独立、可不带 ORDER BY【文档·ClickHouse：SELECT 页的 LIMIT/OFFSET 两行】。
        // 参数序即发射序 ＝ LIMIT 吃 @p0、OFFSET 吃 @p1（与另几族 @ 派同一格）。
        Assert.Equal(["@p0", "@p1"], parameters.Select(p => p.Name).ToArray());
        Assert.Equal([5, 10], parameters.Select(p => (int) p.Value!).ToArray());
        Checks.Has("ORDER BY \"t0\".\"ExpiresAt\" ASC LIMIT @p0 OFFSET @p1", sql);
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWS ONLY", sql);
    }

    [Fact]
    public void Take_without_skip_emits_no_offset_clause_at_all()
    {
        var sql = orm().Select<SessionToken>().OrderBy(o => o.Id).Take(5).ToSql(out var parameters);

        Verify("paging-take-only", sql);

        // 少一段就是少一段：没有"必须补 OFFSET 0"那颗哨兵（T-SQL 侧独有），也没有那颗"无上限"哨兵
        //（2^64−1 是 MySQL 两家的、2147483647 是虚谷那颗厂商写死的，本家与 DuckDB 什么都不补）。
        Assert.Single(parameters);
        Checks.Has("ORDER BY \"t0\".\"Id\" ASC LIMIT @p0", sql);
        Checks.Absent("OFFSET", sql);
    }

    [Fact]
    public void Skip_only_needs_no_limit_sentinel_here()
    {
        var sql = orm(PagingOrderingPolicy.Allow).Select<SessionToken>().OrderBy(o => o.Id).Skip(30).ToSql();

        Verify("paging-skip-only", sql);

        // 官方的"if OFFSET is set without LIMIT..."那条说明【文档·ClickHouse：SELECT 页】:缺上限不补哨兵。
        // 吃不吃"只有 OFFSET"这一档据官方原文读是收的，未实测（第一台引擎先验那条）。
        Checks.Has("OFFSET @p0", sql);
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

        // 布尔是本家的原生型【文档·ClickHouse：Bool 数据类型页】，谓词两侧都落与列同一边的比较：
        // = true。IS TRUE 那一形是 PG 血统的 —— 那一颗在本家没有第二出去处（Bool 就是可比的那一型）。
        Assert.Single(parameters);
        Checks.Has("@p0 = true AND \"t0\".\"Deleted\" = true", sql);
        Checks.Absent("IS TRUE", sql);
        Checks.Absent("= 1", sql);
    }

    [Fact]
    public void An_empty_set_is_a_bare_false_because_where_wants_a_boolean()
    {
        var none = Array.Empty<Guid>();

        var sql = orm().Select<SessionToken>().Where(t => none.Contains(t.Id)).ToSql();

        Verify("where-false-empty-set", sql);

        // 恒假落布尔字面量【文档·ClickHouse：Bool 数据类型页 true/false 两颗字面量】：空集合 IN () 的
        // 特化不发 1 = 0 那一族 —— 与 PG 血统同形而不同因（那边是类型那一档，这边是原生型）。
        Checks.Has("WHERE false", sql);
        Checks.Absent("1 = 0", sql);
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

        // 主键是契约列的一员（本家没有自增列，那一个"第 Id"的缺口住在别一格 —— 内核把自增列
        // 剔出 INSERT 列清单那一手只对整型主键起作用，Guid 主键走到的是这里这一盘）。
        Checks.Has("INSERT INTO \"QuickSqlSessionToken\" (\"Id\", \"Value\", \"ExpiresAt\") VALUES (@p0, @p1, @p2)", sql);
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
        // 官方语句清单里没有 RETURNING/OUTPUT 任何一支【文档没逐页回读】），所以
        // golden/clickhouse/ 里没有 insert-with-keys 那一份 —— 本家断言抛、不落盘
        //（beta.26 批基线另添注释两槽与跳过索引四份，与这一格互不相干）。
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

        // UPDATE 的官方形状是 ALTER TABLE … UPDATE … WHERE（后台异步 mutation）
        //【文档·ClickHouse：alter 页】—— 不在"一次往返改完"的写路径上，整格抛。
        Assert.Contains("UPDATE 不在写路径上", update.Message);

        var deletion = Checks.Raised<QuickSqlException>(() =>
            orm().Delete<SessionToken>().Where(t => t.Id == Guid.Empty).ToSql());

        // DELETE 的官方 lightweight（DELETE FROM t WHERE …，
        // https://clickhouse.com/docs/sql-reference/statements/delete ）同属变异那一档，整格抛。
        Assert.Contains("lightweight DELETE", deletion.Message);
    }

    // ── "建库"整格不可走 ─────────────────────────────────────────────

    [Fact]
    public async Task The_create_database_question_has_no_route_here()
    {
        // 本家连接串里那颗 database 当场就要在场 —— 没有"连一个不带库名的锚点库再补建"的路可走。
        // 闸与 DuckDb 同案（CanCreateDatabase=false），绕过闸来问的那条路走接口虚口（NotSupportedException）。
        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            () => Provider.EnsureDatabaseExistsAsync("Database=some-db", TestContext.Current.CancellationToken));

        Checks.Has("请先手动建库", failure.Message);
    }

    // ── 槽位的形状读数 ───────────────────────────────────────────────

    [Fact]
    public void The_slot_shapes_are_the_ones_the_ledger_never_must_not_rely_on_memory_for()
    {
        // 五颗形状读数（与另几族同档，各颗的判据写在其所属的 provider 文件里）：
        // 参数前缀 '@'（官方 ADO.NET 客户端页）、恒假/恒真落 true/false、'||' 那一格标【推断】、
        // LIKE 转义符取标准形。这些格不在基线里逐颗发文本，但台账与 §14 都挂在这颗读数上。
        Assert.Equal('@', Provider.Dialect.ParameterPrefix);
        Assert.Equal("false", Provider.Dialect.EmptySetPredicate);
        Assert.Equal("true", Provider.Dialect.AlwaysTruePredicate);
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);
        Assert.Equal("||", Provider.Expressions.ConcatOperator);
        Assert.False(Provider.Expressions.SupportsRegex);
    }

    [Fact]
    public void A_datetime_parameter_goes_across_as_the_millis_preserving_string()
    {
        // 【实测·2026-10-20 活库 26.9.21.9 ∶ 驱动 7.14.0】DateTime 参数（DbType.DateTime / DateTime2 /
        // DateTimeOffset 三种声明都试过）在驱动的序列化里一律不带小数秒，毫秒只在字符串形里保真
        //（.222/.555 两手都按原样落库、读回也原样）—— 所以 Normalize 把 DateTime 化成那一串，与内联
        // 那一支同一形状。这颗钉住两半：参数侧（DbType.String + .fff 不变式 + 尺寸取该串字符数）
        // 与内联侧（同一串的单引号字面量）。
        var moment = new DateTime(2026, 10, 20, 9, 0, 0, 123);
        var parameter = ClickHouseValues.Instance.CreateParameter("@p0", moment, null);

        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal("2026-10-20 09:00:00.123", parameter.Value);
        Assert.Equal(23, parameter.Size);

        Assert.True(ClickHouseValues.Instance.TryInlineLiteral(moment, null, out var literal));
        Assert.Equal("'2026-10-20 09:00:00.123'", literal);
    }

    static SqlWriter NewWriter() => new(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }
}
