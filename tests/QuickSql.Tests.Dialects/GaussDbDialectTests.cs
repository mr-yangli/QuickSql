using System.Data.Common;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.PostgreSqlBase;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>GaussDB 这颗叶子的 SQL <b>文本</b>（§9.1 的 openGauss/GaussDB 那一<em>共行</em>的第二颗兑现，
/// v0.36 第二笔「GaussDB 到场即照本行再挂」裁决的落地日套件）。
/// 全程 <see cref="SqlServer.NoDriverFactory"/>，与另外七处离线套件同一分工。
///
/// <para><b>这一族没有 golden/gaussdb/ 目录，判据与 <c>OpenGaussDialectTests</c> 同一条</b>：
/// §13.3 那句"同形交给对拍"在这里是<b>四方</b>逐字同形（血统基类、金仓壳、openGauss 壳、本家），
/// 再抄一份基线只会多一处要同步的地方。对拍链这一册从三段长成四段：leaf == openGauss 叶 == 金仓叶 == 血统，
/// 而本文件相对上一册<em>唯一新增的形状</em>是两颗分叉叶子互相对读
/// （<see cref="The_two_PG_forked_leaves_are_byte_identical_to_each_other_not_just_to_the_kingbase_leaf"/>）——
/// MySQL 血统的先例是 OceanBase 那册（v0.35 第四笔），PG 血统的"必然彼此同形"今天第一次落成可跑证据。</para>
///
/// <para><b>这份套件证明什么、不证明什么</b>：它证"本仓库没有为 GaussDB 重写一字"，
/// 不证"GaussDB 接受金仓或 openGauss 的那一字"——后者本机无一枚实例可问，而本家相对 openGauss 那册
/// 不是多一层【推断】而是<em>薄一层来路</em>：那颗 IDENTITY 的"9.2 内核不认"【推断】是共行对两家同立的，
/// 本册对它逐颗重裁、仍不落（判据沿三笔：文档明写的分岔才配覆写）；而 GaussDB 自己分发的托管驱动
/// 连名字都没有入账读数，"已适配"三句在这里比 openGauss 更立不起来。所以本文件一切断言停在
/// 「离线已钉」档；逐位出处记在 <see cref="CapabilityLedgerTests"/> 的第八本台账。</para>
///
/// <para>这里不进 <c>DialectParityTests</c>：opengauss↔gaussdb 今天<em>零处不同</em>（LEFT JOIN 写句的
/// 拒收文案只差 Kind 那颗词，由 <see cref="The_outer_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word"/>
/// 单独点名），把恒等式并进两两配对只会稀释对拍的判据——与 tidb/oceanbase/opengauss 三册同一条处置。</para></summary>
public sealed class GaussDbDialectTests
{
    static readonly IDbProvider Provider = GaussDb.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider OpenGaussLeaf = OpenGauss.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider KingbaseLeaf = Kingbase.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>血统基类的可实例化形态（与 <c>OpenGaussFamilyProbe</c> 同一写法）。
    /// <b>Kind 取 <see cref="DbKind.GaussDb"/></b>：探针要和叶逐字同形才有意义，而 Kind 会进
    /// <c>EmitJoinedSources</c> 的异常文案 —— 两侧同 Kind、比的恰好是"叶子有没有给基类添一字"。</summary>
    static readonly IDbProvider Family = new GaussDbFamilyProbe(DbKind.GaussDb, SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => GaussDb.Orm();

    static QuickSqlOrm openGauss() => OpenGauss.Orm();

    static QuickSqlOrm kingbase() => Kingbase.Orm();

    /// <summary>同一颗占位串、同一颗假工厂，provider 换人 —— 让 DDL 段能按 <see cref="IDbProvider"/> 参数化。</summary>
    static QuickSqlOrm ormFor(IDbProvider provider)
        => new(new QuickSqlOrmOptions { Provider = provider, ConnectionString = SqlServer.OfflineConnectionString });

    /// <summary>手搭链取契约的组装根按 provider 换人（本册的参战方是三家：本家、openGauss 邻叶、金仓叶）。</summary>
    static QuickSqlOrm ormOf(IDbProvider provider)
        => ReferenceEquals(provider, Provider) ? orm() : ReferenceEquals(provider, OpenGaussLeaf) ? openGauss() : kingbase();

    /// <summary>带模式的模型：与 <c>OpenGaussDialectTests</c> 那一份同一手法（Fluent 覆写而不是新建实体）——
    /// PG 血统的默认模式本来就是 <c>public</c>，这一格量的不是"限定不折叠"而是"契约写 public 时
    /// 产出与契约不写时逐字同"（同一份默认落进同一条发射），它随 DDL 三段一起进对拍。</summary>
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
        // 这一条钉的是"叶子到底带了什么进来"：一颗枚举值（v0.x 就占着位，本笔零 Core 改动）、
        // 一颗清单键，其余全部继承。清单键 "gaussdb" 的 assembly 与 opengauss 行逐字相同（Npgsql）——
        // 于是 PG 血统第一次出现"两颗键、同一份 assembly"，而两颗共享的是金仓行的兼容臂而不是
        // 同一族协议的真 primary（mysql/tidb/oceanbase 那三键共享的是后者）：来路与不对称性全文在
        // 清单 $comment 与 <c>GaussDbProvider</c> 类注释第四段。
        Assert.Equal(DbKind.GaussDb, Provider.Kind);
        Assert.Equal("gaussdb", QuickSql.Providers.GaussDb.GaussDbProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.OpenGauss.OpenGaussProvider.DriverKey, QuickSql.Providers.GaussDb.GaussDbProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.Kingbase.KingbaseProvider.DriverKey, QuickSql.Providers.GaussDb.GaussDbProvider.DriverKey);
    }

    [Fact]
    public void The_capability_mask_is_inherited_verbatim_15_bits_not_one_less_not_one_more()
    {
        // 四段链的掩码半：leaf == 邻家分叉叶 == 金仓叶 == 血统。金仓那 14 位里躺着 7 位 VendorDoc 乐观声明，
        // 本家今天一位都不翻案也不删减——那七位的收敛是 §4 的账，不是这颗叶子顺手能结的。
        // 第 15 位是本笔挂上去的 Bulk_Copy，同一条形状在本家还要多压一层前一问（"这台库与 openGauss 是不是
        // 同一套文本假设"之前先问"这台库的驱动里有没有导入器"），而两颗都是 TryResolve 与活库的事、不是掩码的事。
        Assert.Equal(OpenGaussLeaf.Capabilities, Provider.Capabilities);
        Assert.Equal(KingbaseLeaf.Capabilities, Provider.Capabilities);
        Assert.Equal(Family.Capabilities, Provider.Capabilities);

        Assert.Equal(
            DbCapabilities.Paging_LimitOffset | DbCapabilities.Returning | DbCapabilities.Upsert_OnConflict |
            DbCapabilities.Cte | DbCapabilities.WindowFunctions | DbCapabilities.Count_Over_Window |
            DbCapabilities.Boolean_Type | DbCapabilities.Guid_Raw16 | DbCapabilities.Schemas |
            DbCapabilities.CaseSensitive_Identifiers | DbCapabilities.Comment_On_Column |
            DbCapabilities.Alter_Column_Precision | DbCapabilities.TempTable | DbCapabilities.Bulk_Copy | DbCapabilities.Transactions,
            Provider.Capabilities);

        // 两颗"这一族刻意不声明"在本家同样不翻案：MultiStatement 被 §9.4 点名（扩展协议里一次塞多条
        // 不合法），而 openGauss 那一笔给它记的那层【推断】（fork 对简单协议多语句的实际边界没有读数）
        // 对本家连推断都算不上——GaussDB 的部署形态读数为零。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiStatement));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiResultSets));
    }

    [Fact]
    public void The_two_PG_forked_leaves_are_byte_identical_to_each_other_not_just_to_the_kingbase_leaf()
    {
        // 本文件相对 OpenGaussDialectTests 唯一新增的形状：两颗<em>分叉</em>叶子互相对读。
        // 此前 PG 血统的所有同形断言都是"叶子对金仓叶/基类"，openGauss 与 GaussDB 之间没有任何一条
        // 见证——它们各自与血统同形，逻辑上必然彼此同形，而"必然"在这个仓库里从来不算断言
        // （§13.5 那句"拿字符串搜产物搜出来的断言会错"是同一条纪律的近亲）。这一条把那格"必然"
        // 落成可跑的证据，姿势与 OceanBase 那册的互读格逐颗相同，只是换了一根血统缝。
        var openGaussOrm = openGauss();
        var gaussDbOrm = orm();

        (string Label, Func<QuickSqlOrm, string> Chain)[] chains =
        [
            ("select", o => o.Select<Order>().ToSql()),
            ("paging", o => o.Select<Order>().OrderBy(x => x.Id).Skip(10).Take(5).ToSql()),
            ("key read-back", o => o.Insert(row(1)).ExplainKeys().ToSql()),
            ("update", o => o.Update<Order>().Set(x => x.Amount, x => x.Amount + 1m).Where(x => x.Id == 7).ToSql()),
        ];

        foreach (var (label, chain) in chains)
            Assert.True(chain(openGaussOrm) == chain(gaussDbOrm), $"{label} 这一格两颗分叉叶子不同形：opengauss = {chain(openGaussOrm)} ／ gaussdb = {chain(gaussDbOrm)}");
    }

    [Fact]
    public void The_read_side_is_byte_identical_to_the_kingbase_leaf_across_five_chains()
    {
        var none = Array.Empty<int>();

        // 五条链挑的全是 PG 血统站自家一边的格子：双引号（select）、LIMIT/OFFSET 尾巴、
        // 可光写 OFFSET 不补哨兵（MySQL 两家补 2^64−1、虚谷补 2147483647、这一族什么都不补）、
        // 恒假谓词写成布尔字面量 false（五家里只有这一族改写了 EmptySetPredicate）、
        // 真布尔谓词 IS TRUE（另四家吃 = 1）。逐字相等比"各自绿"多证一件事：
        // 这一家没有任何一处会跟着金仓那一份样本一起漂。
        AssertSame(o => o.Select<Order>().ToSql(), "plain select");

        AssertSame(o => o.Select<Order>().OrderBy(x => x.CreatedAt).Skip(10).Take(5).ToSql(), "paging limit-offset");

        AssertSame(o => o.Select<Order>().OrderBy(x => x.Id).Skip(30).ToSql(), "skip-only no sentinel");

        AssertSame(o => o.Select<Order>().Where(x => none.Contains(x.Id)).ToSql(), "empty-set false predicate");

        AssertSame(o => o.Select<Ticket>().Where(t => t.Deleted).ToSql(), "bool predicate IS TRUE");

        // 本家侧各钉一枚绝对形状（相等之外还要知道<em>是什么</em>，否则两边一起漂就量不出来）。
        var skipOnly = orm().Select<Order>().OrderBy(x => x.Id).Skip(30).ToSql();

        Checks.Has("OFFSET", skipOnly);
        Checks.Absent("18446744073709551615", skipOnly);
        Checks.Absent("2147483647", skipOnly);
        Checks.Has("WHERE false", orm().Select<Order>().Where(x => none.Contains(x.Id)).ToSql());

        var boolSql = orm().Select<Ticket>().Where(t => t.Deleted).ToSql();

        Checks.Has("\"Deleted\" IS TRUE", boolSql);
        Checks.Absent("\"Deleted\" = ", boolSql);
    }

    [Fact]
    public void The_write_side_is_byte_identical_to_the_kingbase_leaf_including_the_update_from_fold()
    {
        AssertSame(o => o.Update<Order>().Set(x => x.Amount, x => x.Amount + 1m).Where(x => x.Id == 7).ToSql(), "update");

        AssertSame(o => o.Delete<Order>().Where(x => x.Id == 7).ToSql(), "delete");

        AssertSame(o => o.Insert(row(1)).ToSql(), "plain insert");

        // 合并 UPDATE 的成功侧见证（UPDATE … FROM 逗号表源 + JOIN 条件折进 WHERE，
        // <c>SupportsJoinedWrites</c> 为 true 的一族才发得出这一句）。openGauss 那一笔已替 PG 血统
        // 交出过第一份；本行把它对第三家也交出来——同形交对拍，无基线，判据与上一笔逐字同。
        AssertSameProvider(p => JoinedUpdate(nameof(Order.Amount), p, JoinKind.Inner).Text, "update from fold (INNER)");

        // 键回读落在句尾（RETURNING），与 MySQL 血统的"语句后另发一句"正对面。
        AssertSame(o => o.Insert(row(1)).ExplainKeys().ToSql(), "insert trailing returning");

        var returning = orm().Insert(row(1)).ExplainKeys().ToSql();

        Checks.Has("RETURNING \"Id\"", returning);
        Checks.Absent("SELECT LAST_INSERT_ID", returning);
    }

    [Fact]
    public void The_DDL_side_is_byte_identical_to_the_kingbase_leaf_create_alter_and_comment()
    {
        AssertSameProvider(
            p => Statement(w => p.CodeFirst.EmitCreateTable(w, ormFor(p).Schemas.Contract<Order>()), p),
            "create table");

        AssertSameProvider(
            p => Statement(w => p.CodeFirst.EmitCreateTable(w, ormWithSchema(p).Schemas.Contract<Order>()), p),
            "create table with explicit schema");

        AssertSameProvider(
            p =>
            {
                var contract = ormFor(p).Schemas.Contract<Order>();
                var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

                return Statement(w => p.CodeFirst.EmitAddColumn(w, contract.Table, note), p);
            },
            "add column");

        AssertSameProvider(
            p =>
            {
                var contract = ormFor(p).Schemas.Contract<Order>();
                var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

                return Statement(w => p.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, amount), p);
            },
            "alter column precision（本族是唯一发两句串联的一家）");

        AssertSameProvider(
            p =>
            {
                var contract = ormFor(p).Schemas.Contract<Order>();
                var commented = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName)) with { Comment = "客户名" };

                return Statement(w => Assert.True(p.CodeFirst.TryEmitColumnComment(w, contract.Table, commented)), p);
            },
            "column comment");

        // 绝对形状三枚：默认模式 public 写进限定（MySQL/Oracle 两家不写模式名）、双引号、CONSTRAINT 主键。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("CREATE TABLE \"public\".\"QuickSqlOrder\"", sql);
        Checks.Has("CONSTRAINT \"PK_QuickSqlOrder\" PRIMARY KEY", sql);
    }

    [Fact]
    public void The_identity_clause_survives_as_a_ruling_now_repeated_on_the_second_forked_leaf()
    {
        // "叶子到场 ≠ 覆写到场"在这一族的第二次适用：v0.36 第二笔对 openGauss 裁过的那颗分岔
        // （IDENTITY 要 PG 10+ 而共行写的是 9.2 内核、"不认"是从内核代数推的【推断】），
        // 对本家逐颗重裁、仍然不落——共行的厂商读数对两家是同一句，两颗叶子的裁决因此不分叉。
        // 这一条钉的是"今天发出的是 IDENTITY 那一形"，而它<em>不</em>钉"这一家认得这一形"——
        // 真实例到场先问"这台库与 openGauss 是不是同一套文本假设"，再问那句 CREATE TABLE 收不收（§14）。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("\"Id\" INTEGER GENERATED BY DEFAULT AS IDENTITY NOT NULL", sql);
        Checks.Absent("SERIAL", sql);
        Checks.Absent("IDENTITY(1,1)", sql);
    }

    [Fact]
    public void The_blood_has_no_code_first_seam_and_two_forked_leaves_have_now_each_tested_it()
    {
        // §14 行 2044 那格"两种零覆写者"的 PG 侧读数，本笔复点而不新增：血统在构造里直 new 具体类
        // （PostgreSqlProviderBase.cs:42），三族血统里只有 Oracle 那一族留了 CreateCodeFirst 虚口——
        // 所以 PostgreSqlCodeFirst.IdentityClause 那颗虚口到今天仍然<b>零可达覆写者</b>，而数它的叶子
        // 从一颗涨到两颗：openGauss 与 GaussDB 各到场一次，各裁一次"不落"，路都没有这一点两次都没变。
        // 这条钉法不反射读 IL——反射不出"构造里没有缝"，能钉的是产出仍出自血统（叶子对探针）
        // 与三家拿着同一份 CodeFirst 类型。
        var leaf = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormFor(Provider).Schemas.Contract<Order>()));
        var family = Statement(w => Family.CodeFirst.EmitCreateTable(w, ormFor(Family).Schemas.Contract<Order>()));

        Assert.Equal(family, leaf);
        Assert.Same(typeof(PostgreSqlCodeFirst), Provider.CodeFirst.GetType());
        Assert.Same(typeof(PostgreSqlCodeFirst), OpenGaussLeaf.CodeFirst.GetType());
        Assert.Same(typeof(PostgreSqlCodeFirst), KingbaseLeaf.CodeFirst.GetType());
    }

    [Fact]
    public void The_outer_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word()
    {
        // PG 血统的合并档接受 INNER（逗号表源），LEFT 才被 PostgreSqlDialect.EmitJoinedSources 拒收
        // ——这一格与 MySQL 血统那三家的"任何 JOIN 都拒"是对面而不是同形，所以这里的 Kind 例外
        // 钉的是拒收文案那一颗词：两家同闸、差异恰好一个词，不是两份文案。对读对象从金仓叶换成
        // 同分叉行的邻叶——两颗分叉叶互读的拒绝侧，与上面成功侧的互读格配成对。
        var openGaussFailure = Checks.Raised<QuickSqlException>(() => JoinedUpdate(nameof(Order.Amount), OpenGaussLeaf, JoinKind.Left));
        var gaussDbFailure = Checks.Raised<QuickSqlException>(() => JoinedUpdate(nameof(Order.Amount), Provider, JoinKind.Left));

        Assert.StartsWith("GaussDb ", gaussDbFailure.Message, StringComparison.Ordinal);
        Assert.Equal(openGaussFailure.Message.Replace("OpenGauss", "GaussDb", StringComparison.Ordinal), gaussDbFailure.Message);
        Checks.Has("不接受 LEFT JOIN", gaussDbFailure.Message);
    }

    [Fact]
    public void Boolean_literals_and_backslash_escape_and_bulk_budget_come_from_the_family_unchanged()
    {
        // 血统在这一家的形状半：恒假/恒真是布尔字面量（五家里唯一改写这两颗的一家）、LIKE 转义是
        // 反斜杠（MySQL 两家的波浪与虚谷的那颗都在别处）、高速层是预算件不是通道。
        Assert.Equal("false", Provider.Dialect.EmptySetPredicate);
        Assert.Equal("true", Provider.Dialect.AlwaysTruePredicate);
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);
        Assert.IsType<PostgreSqlBulk>(Provider.Bulk);
        Assert.Equal('@', Provider.Dialect.ParameterPrefix);
    }

    [Fact]
    public void Guid_maps_to_the_native_uuid_here_too_as_the_third_provider_of_that_claim()
    {
        // 金仓那两条 Guid 用例从 v0.22 起是这一格在全仓库<em>唯一</em>的一份主张，v0.36 第二笔来了
        // 第一个复件，本笔来了第二个：同一个类型名、同一个位、来自同一份血统类型表——
        // "2∶4" 的分布里 PG 血统这边从两家变三家，而三边说的仍然同一句话：有原生 uuid，
        // char(36) 是给没有的库留的退路。它不证"GaussDB 的 uuid 是 16 字节存"——那一半与另两家同样欠着。
        var id = orm().Schemas.Contract<SessionToken>().Columns
            .Single(c => c.MemberName == nameof(SessionToken.Id));

        Assert.Equal("UUID", Provider.Types.GetStoreType(id));
        Assert.True(Provider.Capabilities.HasFlag(DbCapabilities.Guid_Raw16));
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>同一条链在两家各跑一遍、逐字对读（对拍对象是金仓叶子——四段链里血统侧的那三段
    /// 由掩码、互读与无缝三格分别量，见文件头第一段为什么不是"本尊"）。
    /// <b>不许折任何字形</b>——引号都不差才是这一对的断言。</summary>
    static void AssertSame(Func<QuickSqlOrm, string> chain, string label)
    {
        var kingbaseSql = chain(kingbase());
        var gaussDbSql = chain(orm());

        Assert.True(kingbaseSql == gaussDbSql, $"{label} 这一格同形破了：kingbase = {kingbaseSql} ／ gaussdb = {gaussDbSql}");
    }

    static void AssertSameProvider(Func<IDbProvider, string> chain, string label)
    {
        var kingbaseSql = chain(KingbaseLeaf);
        var gaussDbSql = chain(Provider);

        Assert.True(kingbaseSql == gaussDbSql, $"{label} 这一格同形破了：kingbase = {kingbaseSql} ／ gaussdb = {gaussDbSql}");
    }

    /// <summary>血统基类的可实例化形态（与 <c>OpenGaussFamilyProbe</c> 同一写法：空壳，
    /// 探针存在的唯一理由是上面那条"叶子没给基类添一字"的空壳证据）。</summary>
    sealed class GaussDbFamilyProbe(DbKind kind, DbProviderFactory factory) : PostgreSqlProviderBase(kind, factory);

    static SqlWriter NewWriter(IDbProvider? provider = null)
        => new(new StringBuilder(), [], (provider ?? Provider).Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit, IDbProvider? provider = null)
    {
        var writer = NewWriter(provider);

        emit(writer);

        return writer.ToSqlString();
    }

    /// <summary>手搭带 JOIN 的合并 UPDATE（形状与 <c>OpenGaussDialectTests</c> 里那份同构，只差参战三方：
    /// 本家、openGauss 邻叶、金仓叶。INNER 折得动、LEFT 才被 <c>EmitJoinedSources</c> 拒收，
    /// 所以同一颗 helper 供两家用：写侧对拍喂 INNER（发得出，比文本），Kind 用例喂 LEFT（抛得出，比文案）。
    /// 键与行集值不需要真实，够发射层走完就行。</summary>
    static DialectSql JoinedUpdate(string member, IDbProvider owner, JoinKind kind)
    {
        var contract = ormOf(owner).Schemas.Contract<Order>();

        var stored = new TableRef(0, owner.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var key = contract.PrimaryKey[0];
        var set = contract.Columns.Single(c => c.MemberName == member);
        ColumnContract[] columns = [key, set];

        var rows = new IReadOnlyList<SqlExpression>[2];

        for (var r = 0; r < rows.Length; r++)
            rows[r] = [new ParameterValue(7 + r, key), new ParameterValue("值", set)];

        var on = new SqlBinaryExpression(BinaryOperator.Equal,
            new ColumnExpression(stored, key), new ColumnExpression(side, key));

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(set, new ColumnExpression(side, set))],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, key), new ParameterValue(7, key)),
            Joins = [new JoinClause(kind, new SourceRows(side, columns, rows), on)],
        };

        var writer = NewWriter(owner);

        owner.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }
}
