using System.Data;
using System.Data.Common;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.DuckDb;
using QuickSql.Providers.PostgreSqlBase;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>DuckDB 这颗叶子的 SQL <b>文本</b>（§9.1 的 DuckDB 那一行的兑现日套件；PG 血统的第七颗叶子，
/// 也是这一族<b>第一颗与血统不同形</b>的一颗）。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与另外七族的离线套件同一分工。
///
/// <para><b>本册走 golden/duckdb/ 的磁盘基线而不走对拍</b>，判据与金仓那一册同一条而理由不同：
/// 六颗 PG 血统叶与血统逐字同形（同形交对拍），本家偏偏<b>不是</b> —— 类型表四格换名、身份列换形态
/// （句外 CREATE SEQUENCE + DEFAULT nextval）、"建库"整格不存在。分岔落在厂商文档明写的格上
///【文档·DuckDB，方言与 PG 兼容见
/// <see href="https://duckdb.org/docs/current/sql/dialect/postgresql_compatibility.html"/>】，落哪格记 §13.5
/// 台账。照金仓册那颗烧过的钉子抄一遍：<see cref="Family"/> 常量<b>不能省</b> —— 两族按形状起的基线名是
/// 同一批，忘了它就把 <c>golden/sqlserver/</c> 那批 T-SQL 基线改写成了本家的文本（金仓册注释里那句
/// "复跑整套离线套件才发现，已按 git 还原"）。</para>
///
/// <para><b>这些样本证明的是回归，不是"DuckDB 接受这条 SQL"</b>。本机没有任何一台引擎、驱动从未解析
/// 成功过（§13.5 整行「未实测」），所以这一批 <c>.sql</c> 是自产基线：它们抓住方言层的每一次意外改动，
/// 而一句都没有在真正的解析器上跑过。正式记账在 §13.5 的第十六本台账。</para></summary>
public sealed class DuckDbDialectTests
{
    static readonly IDbProvider Provider = DuckDb.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名（与金仓册的同类常量同一颗钉子，只因为他处已有一颗
    /// <c>Family</c> 探针占名，这里换 <c>Gold</c> 这一写）。<b>这一层不能省</b>：形状名与另七族是
    /// 同一批（<c>select-entity</c>、<c>paging-order-skip-take</c>、<c>ddl-order</c>…），
    /// 第一次生成时没带它就等于把 <c>golden/sqlserver/</c> 里那批 T-SQL 基线改写成了本家文本。</summary>
    const string Gold = "duckdb";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Gold);

    static QuickSqlOrm orm() => DuckDb.Orm();

    /// <summary>只有分页用例需要改选项：<c>Skip/Take</c> 无 <c>OrderBy</c> 在共用层默认是抛（§9.4），
    /// 而只写 <c>OFFSET</c> 在这一家合法（血统那一格的原状【推断·照 PG 兼容层收，未实测】）。</summary>
    static QuickSqlOrm orm(PagingOrderingPolicy policy)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider,
            ConnectionString = SqlServer.OfflineConnectionString,
            PagingOrdering = policy,
        });

    static QuickSqlOrm ormWithSchema(IDbProvider provider)
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().ToTable("QuickSqlOrder", "app");

        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(provider, model)));
    }

    static Order row(int n) => new()
    {
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, 890),
    };

    [Fact]
    public void Kind_and_driver_key_are_the_only_two_things_this_leaf_brings_of_its_own()
    {
        // 这一条钉的是"叶子到底带什么进来"：一颗枚举值、一颗清单键、（以及血统那两道缝交给它的）
        // 一份换名类型表与一份换形态 DDL —— 其余全部继承。清单键 "duckdb" 的 assembly
        // （DuckDB.NET.Data）与 PG 那六颗键（Kdbndp/Npgsql）互不相同：本家不借邻家的臂、不借血统协议
        // 自己的名字，官方 ADO.NET 驱动有自己的名字【文档·DuckDB，货源与解析状态记 §13.5】。
        Assert.Equal(DbKind.DuckDb, Provider.Kind);
        Assert.Equal("duckdb", DuckDbProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.PostgreSql.PostgreSqlProvider.DriverKey, DuckDbProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.Kingbase.KingbaseProvider.DriverKey, DuckDbProvider.DriverKey);
    }

    [Fact]
    public void The_capability_mask_is_inherited_verbatim_15_bits_not_one_less_not_one_more()
    {
        // 本家一位不改血统那 15 位：分页/回读/布尔/Comment/Alter… 全照 FamilyCapabilities；
        // 即便"标识符大小写这一格与库行为有偏差"（官方兼容层写的就是不区分、保留原样【文档·DuckDB】），
        // 那是血统能力位判据到本家要<b>另读</b>的事，不翻位 —— 台账与 §14 记偏差，不改位（§13.5 十六本）。
        Assert.Equal(
            DbCapabilities.Paging_LimitOffset | DbCapabilities.Returning | DbCapabilities.Upsert_OnConflict |
            DbCapabilities.Cte | DbCapabilities.WindowFunctions | DbCapabilities.Count_Over_Window |
            DbCapabilities.Boolean_Type | DbCapabilities.Guid_Raw16 | DbCapabilities.Schemas |
            DbCapabilities.CaseSensitive_Identifiers | DbCapabilities.Comment_On_Column |
            DbCapabilities.Alter_Column_Precision | DbCapabilities.TempTable | DbCapabilities.Bulk_Copy | DbCapabilities.Transactions,
            Provider.Capabilities);

        // 与血统同碰不声明的两格：MultiStatement 一次塞多条在本家也照血统不声明（一次性多句的执行面
        // 与 §9.4 那一笔同一格），MultiResultSets 未建模。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiStatement));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiResultSets));
    }

    /// <summary>血统基类的可实例化形态（与 <c>OpenGaussFamilyProbe</c> 同一写法：空壳，Kind 给本家的值）。
    /// 这颗探针存在的唯一理由是"缝开在哪"那一半要拿血统的<em>原状</em>来对：本家在血统
    /// <c>CreateCodeFirst / CreateDialect</c> 两道虚缝上落了自己的货，而这份基类直接实例化时走的是
    /// 血统的缺省行为（<see cref="PostgreSqlCodeFirst"/> + <see cref="PostgreSqlTypes"/>），
    /// 正好当那一半的对照面。</summary>
    static readonly IDbProvider Family = new DuckDbFamilyProbe(DbKind.DuckDb, SqlServer.NoDriverFactory.Instance);

    [Fact]
    public void The_blood_seams_land_where_this_leaf_first_diverges()
    {
        // 血统那两道缝（CreateCodeFirst / CreateDialect，【本笔开】）在本家的落地两格：
        // DDL 换成 <see cref="DuckDbCodeFirst"/>（身份列换形态那一份），类型表换成
        // <see cref="DuckDbTypes.Instance"/>。血统此前六颗叶的产出全部出自
        // <see cref="PostgreSqlCodeFirst"/> 一份（金仓/openGauss/GaussDB/瀚高/GBase 8c/PG 本尊），
        // 而本家这两格都依血统开出的缝逐格取货、不是空壳 —— 缝在基类、货在叶子。
        Assert.Same(typeof(DuckDbCodeFirst), Provider.CodeFirst.GetType());
        Assert.Same(typeof(QuickSql.Providers.PostgreSqlBase.PostgreSqlCodeFirst), Family.CodeFirst.GetType());
        Assert.Same(DuckDbTypes.Instance, Provider.Types);
        Assert.NotSame(Provider.Types, Family.Types);

        // 血统与新叶的 GetStoreType 一格面
        var deltaType = SqGetStoreType(typeof(double));
        Assert.Equal("DOUBLE", deltaType);
        Assert.NotEqual("DOUBLE PRECISION", deltaType);
        Assert.Equal("DOUBLE PRECISION", SqGetStoreType(typeof(double), Family));
    }

    [Fact]
    public void Store_type_names_follow_the_duckdb_column_of_the_type_map()
    {
        // 类型表四格换名【文档·DuckDB，浮点/时间两组出处 URL 在 DuckDbTypes 类抬头】：
        // double → DOUBLE（血统 DOUBLE PRECISION）、DateTime → TIMESTAMP（血统 TIMESTAMP(3)）、
        // DateTimeOffset → TIMESTAMPTZ（血统 TIMESTAMPTZ(3)）、TimeSpan/TimeOnly → TIME（血统 TIME(3)）。
        // 与换名那四格相对照：decimal / bool / Guid / bytea / TEXT 这五格走血祖原话，一个字没改。
        Assert.Equal("DOUBLE", SqGetStoreType(typeof(double)));
        Assert.Equal("TIMESTAMP", SqGetStoreType(typeof(DateTime)));
        Assert.Equal("TIMESTAMPTZ", SqGetStoreType(typeof(DateTimeOffset)));
        Assert.Equal("TIME", SqGetStoreType(typeof(TimeSpan)));
        Assert.Equal("TIME", SqGetStoreType(typeof(TimeOnly)));

        // 血祖原话的五格（同一份换算口径下本家与血统同文的两半）
        Assert.Equal("NUMERIC(18,4)", SqGetStoreType(typeof(decimal)));
        Assert.Equal("BOOLEAN", SqGetStoreType(typeof(bool)));
        Assert.Equal("UUID", SqGetStoreType(typeof(Guid)));
        Assert.Equal("BYTEA", SqGetStoreType(typeof(byte[])));
        Assert.Equal("TEXT", SqGetStoreType(typeof(string), maxChars: null));

        Assert.Equal("VARCHAR(64)", SqGetStoreType(typeof(string), maxChars: 64));

        // 血统那只表的四格名在本家产出里一格不落：那句"血统名不漏到本家的产出"由下面的扫描落成断言。
        duck_check_absent(Provider, "DOUBLE PRECISION");
        duck_check_absent(Provider, "TIMESTAMP(3)");
        duck_check_absent(Provider, "TIMESTAMPTZ(3)");
        duck_check_absent(Provider, "TIME(3)");
    }

    /// <summary>血统那只表里的一个型名在本家的<em>全部出口</em>上发不出来：面板一套（契约里的列）、
    /// 采样一套（类型表那串 Switch 覆盖得到的代表 CLR 型），两半都扫。型名表既然换了人，
    /// 血统缺省那几格名字就没有藏身处。</summary>
    void duck_check_absent(IDbProvider p, string banned)
    {
        var contract = orm().Schemas.Contract<Order>();

        foreach (var column in contract.Columns)
            Assert.NotEqual(banned, p.Types.GetStoreType(column));

        foreach (var probe in new[] { typeof(double), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(TimeOnly), typeof(decimal), typeof(string) })
            Assert.NotEqual(banned, SqGetStoreType(probe, p));
    }

    static string SqGetStoreType(Type clrType, IDbProvider? family = null, int? maxChars = null)
    {
        var contract = orm().Schemas.Contract<Order>();
        var baseColumn = contract.Columns.First(c => !c.IsIdentity);

        var probe = baseColumn with
        {
            ClrType = clrType,
            StoreTypeName = null,
            MaxChars = maxChars,
        };

        return (family ?? Provider).Types.GetStoreType(probe);
    }

    // ── CodeFirst：类型名与身份列形态 ────────────────────────────────

    [Fact]
    public void Create_table_emits_this_family_s_type_names_and_the_sequence_first_identity_shape()
    {
        var contract = orm().Schemas.Contract<Order>();
        var writer = NewWriter();

        Provider.CodeFirst.EmitCreateTable(writer, contract);

        var sql = writer.ToSqlString();

        Verify("ddl-order", sql);

        // 身份列的官方形态【文档·DuckDB，create_table 页】：句外一句 CREATE SEQUENCE，列上挂 DEFAULT nextval；
        // 序列名照官方示例（t3_key 那一行的形状）按 表_列 后缀拼。两句的先后是这一格的骨架，
        // 只让 nextval 出现而不让 CREATE SEQUENCE 先走一发的话，第一句 INSERT 就红在序列不存在上。
        Assert.True(sql.IndexOf("CREATE SEQUENCE 'QuickSqlOrder_Id_seq'", StringComparison.Ordinal)
            < sql.IndexOf("CREATE TABLE", StringComparison.Ordinal),
            "CREATE SEQUENCE 必须先于 CREATE TABLE 发出（官方形态就是那句先行）");
        Checks.Has("CREATE SEQUENCE 'QuickSqlOrder_Id_seq' ; CREATE TABLE \"public\".\"QuickSqlOrder\"", sql);
        Checks.Has("\"Id\" INTEGER DEFAULT nextval('QuickSqlOrder_Id_seq') NOT NULL", sql);

        // 血统那一格的三种名字在此家整档不出现：GENERATED … AS IDENTITY 是 PG 10+ 的 SQL 标准那一支，
        // SERIAL 是 PG 的捷径，IDENTITY(1,1) 是 T-SQL 的 —— 三棵在本家都没有对应物。
        Checks.Absent("GENERATED BY DEFAULT AS IDENTITY", sql);
        Checks.Absent("SERIAL", sql);
        Checks.Absent("IDENTITY(1,1)", sql);

        // 类型名四格换名后的面板（换名前那四格以血统字形一张不出）
        Checks.Has("CREATE TABLE \"public\".\"QuickSqlOrder\"", sql);
        Checks.Has("\"CustomerName\" VARCHAR(64) NULL", sql);
        Checks.Has("\"Amount\" NUMERIC(18,4) NOT NULL", sql);
        Checks.Has("\"CreatedAt\" TIMESTAMP NOT NULL", sql);
        Checks.Has("\"State\" INTEGER NOT NULL", sql);
        Checks.Has("\"Note\" TEXT NULL", sql);
        Checks.Absent("TIMESTAMP(3)", sql);
        Checks.Absent("DATETIME2", sql);
    }

    [Fact]
    public void Add_column_for_an_identity_column_leads_with_the_same_sequence_sentence()
    {
        // 加列那一路也要走同一颗前件：身份列的 DEFAULT nextval 引的序列不建好，那一列的全部写入就红
        // 在序列缺失上 —— 句头照发、同一颗名字。分发这一格今天没有调用方（结构同步只往已建表加普通列），
        // 但形状要先钉住，不留"建表才发序列"的那一条裂缝。
        var contract = orm().Schemas.Contract<Order>();
        var id = contract.Columns.Single(c => c.MemberName == nameof(Order.Id));

        var sql = Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, id));

        Checks.Has("CREATE SEQUENCE 'QuickSqlOrder_Id_seq' ; ALTER TABLE \"public\".\"QuickSqlOrder\" ADD COLUMN", sql);
        Checks.Has("DEFAULT nextval('QuickSqlOrder_Id_seq') NOT NULL", sql);
    }

    [Fact]
    public void Column_changes_and_comments_are_this_family_s_shapes()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        // 三条各自一句、各自一个书写器（与金仓同一格：塞进同一个书写器会得到连体文本）。
        var sql = string.Join(" ; ",
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, note)),
            Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, amount)),
            Statement(w => Assert.True(Provider.CodeFirst.TryEmitColumnComment(w, contract.Table, name))));

        Verify("ddl-alter-and-comment", sql);

        // 精度那一档是两句串联（血统同格，一判原话）：ALTER COLUMN … TYPE 不碰可空性。
        // 第二句写不写，差出一颗"改完精度顺带改了约束"的静默回退。
        Checks.Has("ALTER TABLE \"public\".\"QuickSqlOrder\" ADD COLUMN \"Note\" TEXT NULL", sql);
        Checks.Has("ALTER COLUMN \"Amount\" TYPE NUMERIC(18,4) ; ALTER TABLE \"public\".\"QuickSqlOrder\" ALTER COLUMN \"Amount\" SET NOT NULL", sql);

        // 列注释是原生语法（血统同文），文本不带 N 前缀（本家没有"窄字面量装不下中文"的那一格）。
        Checks.Has("COMMENT ON COLUMN \"public\".\"QuickSqlOrder\".\"CustomerName\" IS ''", sql);
        Checks.Absent("sp_addextendedproperty", sql);
        Checks.Absent("N'", sql);
    }

    [Fact]
    public void An_explicit_schema_qualifies_the_table_exactly_as_the_contract_writes_it()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema(Provider).Schemas.Contract<Order>()));

        Verify("ddl-qualified", sql);

        // 默认模式 public 与显式模式都走同一颗限定：血统同格原话在加了换名后依然成立。
        Checks.Has("CREATE TABLE \"app\".\"QuickSqlOrder\" (\"", sql);
        Checks.Has("CREATE SEQUENCE 'QuickSqlOrder_Id_seq' ; ", sql);
    }

    // ── 读侧：双引号与分页两子句 ─────────────────────────────────────

    [Fact]
    public void Every_identifier_is_double_quoted_so_the_written_and_read_forms_are_the_same_object()
    {
        var sql = orm().Select<Order>().ToSql();

        Verify("select-entity", sql);

        // 双引号策略照血统的 IdentifierPolicy.PostgreSQL 原样复用 —— 官方方言页点名 PG 兼容【文档·DuckDB】。
        // 与血统那半格的<b>偏差</b>[文档·DuckDB]：本家标识符大小写不敏感而保留原样，"加了引号换来的区分"
        // 在血统那边是判等纪律，在本家防的是"名字里有没有保留字"那一类（判据换语义的全文记 §14）。
        Checks.Has("FROM \"QuickSqlOrder\" AS \"t0\"", sql);
        Checks.Has("SELECT \"t0\".\"Id\", \"t0\".\"CustomerName\"", sql);
        Checks.Absent("[", sql);
    }

    [Fact]
    public void Limit_and_offset_are_two_independent_clauses_and_the_parameter_order_follows_the_emission()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.CreatedAt).Skip(10).Take(5).ToSql(out var parameters);

        Verify("paging-order-skip-take", sql);

        // 两子句各自一颗参数，参数序即发射序 ＝ LIMIT 吃 @p0、OFFSET 吃 @p1：两颗值落袋是
        // Skip(10) 比 Take(5) 大的那一侧，谁在前由子句先后定（与血统同形，一判原话）。
        Assert.Equal(["@p0", "@p1"], parameters.Select(p => p.Name).ToArray());
        Assert.Equal([5, 10], parameters.Select(p => (int) p.Value!).ToArray());
        Checks.Has("ORDER BY \"t0\".\"CreatedAt\" ASC LIMIT @p0 OFFSET @p1", sql);
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWS ONLY", sql);
    }

    [Fact]
    public void Take_without_skip_emits_no_offset_clause_at_all()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Take(5).ToSql(out var parameters);

        Verify("paging-take-only", sql);

        // 少一段就是少一段：没有"必须补 OFFSET 0"那一颗血税收不上去的准数（T-SQL 侧独有），也没有
        // 那颗"无上限"哨兵（2^64−1 是 MySQL 两家的、2147483647 是虚谷的，本家与血统什么都不补）。
        Assert.Single(parameters);
        Checks.Has("ORDER BY \"t0\".\"Id\" ASC LIMIT @p0", sql);
        Checks.Absent("OFFSET", sql);
    }

    [Fact]
    public void Skip_only_needs_no_limit_sentinel_here()
    {
        var sql = orm(PagingOrderingPolicy.Allow).Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql();

        Verify("paging-skip-only", sql);

        // 与血统同一格：缺上限不补哨兵。DuckDB 吃不吃"只有 OFFSET"这一档【推断·照 PG 兼容层收，
        // 厂商 compat 清单没有单独数这一格，给实走那一天先问的就是它】。
        Checks.Has("OFFSET @p0", sql);
        Checks.Absent("LIMIT", sql);
        Checks.Absent("18446744073709551615", sql);
        Checks.Absent("2147483647", sql);
    }

    // ── 谓词形状 ─────────────────────────────────────────────────────

    [Fact]
    public void An_empty_set_is_a_bare_false_because_where_wants_a_boolean()
    {
        var none = Array.Empty<int>();

        var sql = orm().Select<Order>().Where(o => none.Contains(o.Id)).ToSql();

        Verify("where-false-empty-set", sql);

        Checks.Has("WHERE false", sql);
        Checks.Absent("1 = 0", sql);
    }

    [Fact]
    public void A_bool_operand_uses_IS_TRUE_rather_than_comparing_to_a_bit()
    {
        var paid = true;

        var sql = orm().Select<Ticket>().Where(t => paid && t.Deleted).ToSql(out var parameters);

        Verify("where-boolean-flag", sql);

        // 布尔是真类型：谓词两侧各成一个 IS TRUE（血统同格，官方兼容页这一句照收【文档·DuckDB】），
        // = 1 那个写法在这儿直接红在类型上。两半只装一颗参数：字面量 true 装袋成 @p0，
        // 成员列那一侧 IS TRUE 不装袋 —— IS TRUE 是"列自身即谓词"，不是比较。
        Assert.Single(parameters);
        Checks.Has("@p0 IS TRUE AND \"t0\".\"Deleted\" IS TRUE", sql);
        Checks.Absent("@p0 = 1", sql);
    }

    // ── 主键回填：RETURNING 落在句尾 ─────────────────────────────────

    [Fact]
    public void The_key_clause_is_a_trailing_returning_and_a_plain_insert_has_no_returning_at_all()
    {
        var plain = orm().Insert(row(1)).ToSql();

        Verify("insert-plain", plain);

        Checks.Absent("RETURNING", plain);

        var sql = orm().Insert(row(1)).ExplainKeys().ToSql();

        Verify("insert-with-keys", sql);

        // 官方文档那一-form【文档·DuckDB，insert 页】写的就是 RETURNING 在句尾（含官方示例的 RETURNING *）。
        // LAST_INSERT_ID / OUTPUT 那两个名字属于另两家的形状，这一格一个不为：血统同形。
        Checks.Has("RETURNING \"Id\"", sql);
        Assert.True(sql.IndexOf("VALUES", StringComparison.Ordinal) < sql.IndexOf("RETURNING", StringComparison.Ordinal), "RETURNING 要只在句尾出现一次: 与血统同形");
        Checks.Absent("OUTPUT", sql);
        Checks.Absent("INSERTED", sql);
        Checks.Absent("LAST_INSERT_ID", sql);
    }

    // ── "建库"整格不存在 ─────────────────────────────────────────────

    [Fact]
    public async Task The_create_database_question_has_no_object_here()
    {
        // 与 SQLite 那一档同形的一个牌子：内嵌单文件引擎，"建库"这一操作没有对象 —— 闸在
        // <c>CanCreateDatabase=false</c>（schema 同步那一头按这颗位绕开），而绕过闸来问的那条路
        // 走不通：虚口直接抛 <see cref="NotSupportedException"/>（消息写明"本家的库就是那颗文件"）。
        Assert.False(Provider.CanCreateDatabase);

        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            () => Provider.EnsureDatabaseExistsAsync("Database=some-db", TestContext.Current.CancellationToken));

        Checks.Has("没有\"建库\"这一操作", failure.Message);
    }

    // ── 血统与叶子的共享实例 ─────────────────────────────────────────

    [Fact]
    public void Shared_slots_are_held_by_the_blood_not_rebuilt_by_the_leaf()
    {
        // "各槽位共用同一批实例"那条纪律在本家成立的第一条机器读法：values / expressions / keys
        // 共同槽位仍由血统持有（CreateDialect 那道缝把它们逐格递入，本家没有为任何一颗重造别名类）
        // —— 父开缝、叶取货（与 OracleProviderBase 同一条纪律：共享者只共用、不各有一份）。
        // 方言那几格（参数前缀、恒假谓词、LIKE 转义）经血统同一份传入，读数照血统原值。
        Assert.IsType<PostgreSqlExpressions>(Provider.Expressions);
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);
        Assert.Equal('@', Provider.Dialect.ParameterPrefix);
        Assert.Equal("false", Provider.Dialect.EmptySetPredicate);
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>血统基类的可实例化形态（与 SQLServer 家那本册的写法同：空壳，Kind 给本家的值）。
    /// 另一颗探针 <see cref="Family"/> 存在的唯一理由是"缝开在哪"那一半要拿血统的原状来对。</summary>
    sealed class DuckDbFamilyProbe(DbKind kind, DbProviderFactory factory) : PostgreSqlProviderBase(kind, factory);

    static SqlWriter NewWriter() => new(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }
}
