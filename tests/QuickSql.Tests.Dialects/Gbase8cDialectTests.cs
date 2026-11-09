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

/// <summary>GBase 8c 这颗叶子的 SQL <b>文本</b>（§9.1 行 905 的兑现，PG 血统的第五颗叶子）。
/// 全程 <see cref="SqlServer.NoDriverFactory"/>，与另外九处离线套件同一分工。
///
/// <para><b>这一族没有 golden/gbase8c/ 目录，判据与 <c>OpenGaussDialectTests</c>／<c>GaussDbDialectTests</c>／
/// <c>HighgoDialectTests</c> 同一条</b>：§13.3 那句"同形交给对拍"在这里是<b>六方</b>逐字同形（血统基类、
/// 金仓壳、openGauss 壳、GaussDB 壳、瀚高壳、本家），再抄一份基线只会多一处要同步的地方。对拍链这一册
/// 从五段长成六段，而本文件相对上一册新增的形状有两处：① 血统五叶的两两互读<b>一次补四条边</b>
/// （<see cref="The_pairwise_mutual_reads_across_the_PG_lineage_now_cover_ten_edges_of_ten"/>，
/// C(5,2)=10，上一册收满六条，本册交出 8c↔另四叶的四条，十条至此全部有见证）；
/// ② <b>厂商行点名的那颗分岔在文本层零可指认</b>被钉成断言
/// （<see cref="The_named_divergence_occupies_a_clause_this_repository_never_writes"/>）——
/// 那是本册的裁决格，见下面第三段。</para>
///
/// <para><b>本册的裁决格在量什么</b>：行 905 坑列两颗，「与 openGauss 高度相似」与「分片语法差异」，
/// 后一颗是 PG 血统五份厂商行里第一颗<em>明写的方言级分岔而它落在建表语句自身之上</em>。它落的位置是
/// 句内子句（分片是那张表的可选子句），而本仓库不发那一子句 —— 于是"分岔在发射面之外"这一档第一次分两样：
/// 整句不发（神通那笔的 <c>CREATE SCHEMA</c>）∶ 句内缺省（本家的分片子句）。两样的<em>重开条件</em>不同：
/// 前者等厂商语义变，后者等的是仓库自己哪天长出分片 DDL，那天这一格立刻从面外进到面内。可跑证据因此是
/// 一句否定式：本家那句 <c>CREATE TABLE</c> 里连 <c>DISTRIBUTED</c> 的一颗字母都没有，而同那一句与另四叶
/// 逐字同形——"相似"这一家被行说成相似，代码给出的却是逐字同；差异那一格行说明有，代码给不出任何字节。</para>
///
/// <para><b>这份套件证明什么、不证明什么</b>：它证"本仓库没有为 8c 重写一字"，不证"8c 接受金仓或另三家的
/// 那一字"——本机无一枚实例可问。而本家在 IDENTITY 那一问上占一种新档：它的前件是<em>借来的</em>。
/// 金仓行点过 <c>SERIAL</c> vs <c>IDENTITY</c>，openGauss／GaussDB 那共行自己写着 9.2 内核代数（那颗
/// 【推断】由本行直接支撑），瀚高行什么也没写（那一问对它不成立），而本行不答那一问、只把自己指向邻家
/// （<see cref="The_identity_clause_is_emitted_verbatim_on_a_line_that_borrows_its_precedent"/>）。
/// 所以真实例第一问是"那句'高度相似'像到哪一层"——像到内核代数的话本家就落进 openGauss 那一格的老问题；
/// 本册不替它引申（"8c 是哪个内核的发行版"是仓库外的知识，行里没有）。一切断言停在「离线已钉」档，
/// 逐位出处记在 <see cref="CapabilityLedgerTests"/> 的第十一本台账。</para>
///
/// <para>这里不进 <c>DialectParityTests</c>：gbase8c↔另四叶今天<em>零处不同</em>（LEFT JOIN 写句的拒收文案
/// 只差 Kind 那颗词，由 <see cref="The_outer_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word"/>
/// 单独点名），把恒等式并进两两配对只会稀释对拍的判据——与 tidb／oceanbase／opengauss／gaussdb／highgo
/// 五册同一条处置。</para></summary>
public sealed class Gbase8cDialectTests
{
    static readonly IDbProvider Provider = Gbase8c.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider HighgoLeaf = Highgo.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider OpenGaussLeaf = OpenGauss.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider GaussDbLeaf = GaussDb.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider KingbaseLeaf = Kingbase.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>血统基类的可实例化形态（与 <c>HighgoFamilyProbe</c> 同一写法）。
    /// <b>Kind 取 <see cref="DbKind.Gbase8c"/></b>：探针要和叶逐字同形才有意义，而 Kind 会进
    /// <c>EmitJoinedSources</c> 的异常文案 —— 两侧同 Kind、比的恰好是"叶子有没有给基类添一字"。</summary>
    static readonly IDbProvider Family = new Gbase8cFamilyProbe(DbKind.Gbase8c, SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => Gbase8c.Orm();

    static QuickSqlOrm highgo() => Highgo.Orm();

    static QuickSqlOrm openGauss() => OpenGauss.Orm();

    static QuickSqlOrm gaussDb() => GaussDb.Orm();

    static QuickSqlOrm kingbase() => Kingbase.Orm();

    /// <summary>同一颗占位串、同一颗假工厂，provider 换人 —— 让 DDL 段能按 <see cref="IDbProvider"/> 参数化。</summary>
    static QuickSqlOrm ormFor(IDbProvider provider)
        => new(new QuickSqlOrmOptions { Provider = provider, ConnectionString = SqlServer.OfflineConnectionString });

    /// <summary>手搭链取契约的组装根按 provider 换人（本册的参战方是五家：本家、瀚高、两颗分叉叶、金仓叶）。</summary>
    static QuickSqlOrm ormOf(IDbProvider provider)
        => ReferenceEquals(provider, Provider) ? orm()
            : ReferenceEquals(provider, HighgoLeaf) ? highgo()
            : ReferenceEquals(provider, OpenGaussLeaf) ? openGauss()
            : ReferenceEquals(provider, GaussDbLeaf) ? gaussDb()
            : kingbase();

    /// <summary>带模式的模型：与 <c>HighgoDialectTests</c> 那一份同一手法（Fluent 覆写而不是新建实体）——
    /// PG 血统的默认模式本来就是 <c>public</c>，这一格量的不是"限定不折叠"而是"契约写 public 时
    /// 产出与契约不写时逐字同"（同一份默认落进同一条发射），它随 DDL 五段一起进对拍。</summary>
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
        // 一颗清单键，其余全部继承。清单键 "gbase8c" 的 assembly 与另三颗 PG 键逐字相同（Npgsql），
        // 而来路与瀚高那一颗<em>同种</em>（行内"兼容 PG"直称血统协议自己）——两行第四列的原词逐字相同，
        // 这是清单里第一次"两行同一颗来路"。四键一 assembly 于是读作 2∶2 两种来路（兼容臂 ∶ 直称 primary），
        // 而 mysql 那三键是三颗同族真 primary：凑齐不等于同质。全文在 <c>Gbase8cProvider</c> 类注释第六段。
        Assert.Equal(DbKind.Gbase8c, Provider.Kind);
        Assert.Equal("gbase8c", QuickSql.Providers.Gbase8c.Gbase8cProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.Highgo.HighgoProvider.DriverKey, QuickSql.Providers.Gbase8c.Gbase8cProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.OpenGauss.OpenGaussProvider.DriverKey, QuickSql.Providers.Gbase8c.Gbase8cProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.GaussDb.GaussDbProvider.DriverKey, QuickSql.Providers.Gbase8c.Gbase8cProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.Kingbase.KingbaseProvider.DriverKey, QuickSql.Providers.Gbase8c.Gbase8cProvider.DriverKey);
    }

    [Fact]
    public void The_capability_mask_is_inherited_verbatim_15_bits_not_one_less_not_one_more()
    {
        // 六路链的掩码半：leaf == 瀚高叶 == 两颗分叉叶 == 金仓叶 == 血统。金仓那 14 位里躺着 7 位 VendorDoc
        // 乐观声明，本家今天一位都不翻案也不删减——那七位的收敛是 §4 的账，不是这颗叶子顺手能结的。
        // 第 15 位是本笔挂上去的 Bulk_Copy，而它是本家那一格"前件是转引来的"的第二次兑现：这一位问的是
        // "这台驱动里有没有导入器"，而行 905 对驱动只说"与 openGauss 高度相似"——问句要两跳才落到本家头上，
        // 且两跳之后仍然没有一台活库替它答。
        Assert.Equal(HighgoLeaf.Capabilities, Provider.Capabilities);
        Assert.Equal(OpenGaussLeaf.Capabilities, Provider.Capabilities);
        Assert.Equal(GaussDbLeaf.Capabilities, Provider.Capabilities);
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
        // 不合法）；瀚高那一笔说"多语句边界这一问在本家连被问的资格都还没有"，本家比它多有的一颗前件
        // 是<em>借来的</em>（行 905 只说像 openGauss），而借来的前件不够格翻一颗位——翻它要先答"像到哪一层"。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiStatement));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.MultiResultSets));
    }

    [Fact]
    public void The_pairwise_mutual_reads_across_the_PG_lineage_now_cover_ten_edges_of_ten()
    {
        // 本文件相对 HighgoDialectTests 新增的形状之一：血统内两两互读<b>一次补四条边</b>。
        // 上一册收满 C(4,2)=6，第五颗叶子到场把总边数推到 C(5,2)=10，于是本册要交的不是"再补一条"
        // 而是 8c↔另四叶的四条——一册补四边（此前一册最多补两条，见第四笔那格）。
        // "必然彼此同形"这句逻辑推论在这个仓库里从来不算断言（§13.5 那条纪律的近亲），十条至此各有见证。
        var kingbaseOrm = kingbase();
        var openGaussOrm = openGauss();
        var gaussDbOrm = gaussDb();
        var highgoOrm = highgo();
        var gbase8cOrm = orm();

        (string Label, Func<QuickSqlOrm, string> Chain)[] chains =
        [
            ("select", o => o.Select<Order>().ToSql()),
            ("paging", o => o.Select<Order>().OrderBy(x => x.Id).Skip(10).Take(5).ToSql()),
            ("key read-back", o => o.Insert(row(1)).ExplainKeys().ToSql()),
            ("update", o => o.Update<Order>().Set(x => x.Amount, x => x.Amount + 1m).Where(x => x.Id == 7).ToSql()),
        ];

        foreach (var (label, chain) in chains)
        {
            // 金仓叶同时进圈（虽然下面 AssertSame 五链族已经钉着它对本家），是为了让四条链在同一格互相咬合：
            // 若哪天有人只改一颗叶子，至少两条边会红而不是一条。四边对四叶 = 本册交出的那四条。
            Assert.True(chain(gbase8cOrm) == chain(openGaussOrm), $"{label} 这一格 8c 叶对 openGauss 叶不同形：gbase8c = {chain(gbase8cOrm)} ／ opengauss = {chain(openGaussOrm)}");
            Assert.True(chain(gbase8cOrm) == chain(gaussDbOrm), $"{label} 这一格 8c 叶对 GaussDB 叶不同形：gbase8c = {chain(gbase8cOrm)} ／ gaussdb = {chain(gaussDbOrm)}");
            Assert.True(chain(gbase8cOrm) == chain(highgoOrm), $"{label} 这一格 8c 叶对瀚高叶不同形：gbase8c = {chain(gbase8cOrm)} ／ highgo = {chain(highgoOrm)}");
            Assert.True(chain(gbase8cOrm) == chain(kingbaseOrm), $"{label} 这一格 8c 叶对金仓叶不同形：gbase8c = {chain(gbase8cOrm)} ／ kingbase = {chain(kingbaseOrm)}");
        }
    }

    [Fact]
    public void The_read_side_is_byte_identical_to_the_kingbase_leaf_across_five_chains()
    {
        var none = Array.Empty<int>();

        // 五条链挑的全是 PG 血统站自家一边的格子：双引号（select）、LIMIT/OFFSET 尾巴、
        // 可光写 OFFSET 不补哨兵（MySQL 两家补 2^64−1、虚谷补 2147483647、这一族什么都不补）、
        // 恒假谓词写成布尔字面量 false（六家里只有这一族改写了 EmptySetPredicate）、
        // 真布尔谓词 IS TRUE（另五家吃 = 1）。逐字相等比"各自绿"多证一件事：
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
        // <c>SupportsJoinedWrites</c> 为 true 的一族才发得出这一句）。openGauss 那一笔交了第一份、
        // GaussDB 与瀚高两笔各交了一份；本行把它对第五家也交出来——同形交对拍，无基线，判据逐字沿前三笔。
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

        // 绝对形状两枚：默认模式 public 写进限定（MySQL/Oracle 两家不写模式名）、CONSTRAINT 主键。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("CREATE TABLE \"public\".\"QuickSqlOrder\"", sql);
        Checks.Has("CONSTRAINT \"PK_QuickSqlOrder\" PRIMARY KEY", sql);
    }

    [Fact]
    public void The_named_divergence_occupies_a_clause_this_repository_never_writes()
    {
        // 本册的裁决格，也是本册唯一一枚此前五册没有过的断言形状：一颗<em>明写在发射语句自身之上</em>的
        // 分岔，在文本层零可指认。行 905 坑列那句"分片语法差异"说的是分片子句怎么书写——而本仓库发的
        // 那句 CREATE TABLE 不写任何分片子句，所以差异不产出任何字节：下面三颗否定式钉的就是"不产出"，
        // 而上一段五叶逐字同钉的是"因此也无从分辨"。神通那一笔的第三落点量的是整句不发（CREATE SCHEMA
        // 在本仓库根本没有出口），本家量的是句内缺省——同一档判据的两种位置，重开条件一个在厂商、一个在自己。
        // 对读对象取<em>行内点名的那一家</em>（openGauss）：那一行说"高度相似"，代码在这一格给的是逐字同；
        // 而"分片语法差异"正是它与 openGauss 之间<em>唯一</em>被明写出来的不同，它恰好不在发射面上。
        var gbase8cCreate = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));
        var openGaussCreate = Statement(w => OpenGaussLeaf.CodeFirst.EmitCreateTable(w, ormFor(OpenGaussLeaf).Schemas.Contract<Order>()));

        Assert.Equal(openGaussCreate, gbase8cCreate);

        // 五叶同建表句逐字同（本家对另四叶各一枚；金仓那一对由上面的 AssertSameProvider 钉着，这里补齐另三叶）。
        Assert.Equal(Statement(w => HighgoLeaf.CodeFirst.EmitCreateTable(w, ormFor(HighgoLeaf).Schemas.Contract<Order>())), gbase8cCreate);
        Assert.Equal(Statement(w => GaussDbLeaf.CodeFirst.EmitCreateTable(w, ormFor(GaussDbLeaf).Schemas.Contract<Order>())), gbase8cCreate);
        Assert.Equal(Statement(w => KingbaseLeaf.CodeFirst.EmitCreateTable(w, ormFor(KingbaseLeaf).Schemas.Contract<Order>())), gbase8cCreate);

        // 那颗被点名的分岔在本家产出的那一句里连一颗字母都没有（三颗否定式各挡一种写法：
        // openGauss 那一行写的 DISTRIBUTED BY、通用 SQL 的 PARTITION BY、厂商列里"分片"可能落成的 SHARD）。
        Checks.Absent("DISTRIBUTED", gbase8cCreate);
        Checks.Absent("PARTITION", gbase8cCreate);
        Checks.Absent("SHARD", gbase8cCreate);
    }

    [Fact]
    public void The_identity_clause_is_emitted_verbatim_on_a_line_that_borrows_its_precedent()
    {
        // 本册措辞与另三册都不同档的一格：对 openGauss 与 GaussDB，"IDENTITY 要 PG 10+ 而 9.2 内核不认"
        // 是本行自己支撑的【推断】、逐笔重裁仍不落；对瀚高，那颗问题<em>不成立</em>（行里连代数都没有）；
        // 对 8c，那一问的前件是<em>借来的</em>——行 905 不答 IDENTITY，只写了一句"与 openGauss 高度相似"，
        // 而 openGauss 那一行写着 9.2 内核代数。两跳（本行→邻家行→代数）所以比邻家那一跳软一档，
        // 而比瀚高那一档硬：瀚高连可转引的对象都没有。判据不因此改：文档明写的分岔才配覆写，
        // 而"像到哪一层"这一格行里没给读数 → 产出仍是血统的 IDENTITY 那一形，既不钉"认得"也不钉"不认得"。
        // 真实例第一问因此是"那句'高度相似'像到哪一层"（§14 行 2044 本笔的换主语处，第六次换主语、
        // 第一次换在参照物上）。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("\"Id\" INTEGER GENERATED BY DEFAULT AS IDENTITY NOT NULL", sql);
        Checks.Absent("SERIAL", sql);
        Checks.Absent("IDENTITY(1,1)", sql);
    }

    [Fact]
    public void The_blood_has_no_code_first_seam_and_five_leaves_have_now_each_tested_it()
    {
        // §14 行 2044 那格"三种零覆写者"的 PG 侧读数，本笔复点而不新增：血统在构造里直 new 具体类
        // （PostgreSqlProviderBase.cs:42），三族血统里只有 Oracle 那一族留了 CreateCodeFirst 虚口——
        // 所以 PostgreSqlCodeFirst.IdentityClause 那颗虚口到今天仍然<b>零可达覆写者</b>，而到场验过
        // 这句话的叶子从四颗涨到五颗（金仓 v0.22 起在场，openGauss／GaussDB／瀚高／本家各验一次），
        // "连被覆写的路都没有"这一句一个字不需要改。本笔多出的那一格在两句判据<em>相遇</em>：
        // 裁决有前件（借来的那颗）而表达无形状（无缝）——第五笔的"分岔在发射面之外"与第三笔的
        // "没有缝就没有可表达的形状"此前各量各的，今天第一次落在同一颗壳上。
        // 这条钉法不反射读 IL——反射不出"构造里没有缝"，能钉的是产出仍出自血统（叶子对探针）
        // 与五家拿着同一份 CodeFirst 类型。
        var leaf = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormFor(Provider).Schemas.Contract<Order>()));
        var family = Statement(w => Family.CodeFirst.EmitCreateTable(w, ormFor(Family).Schemas.Contract<Order>()));

        Assert.Equal(family, leaf);
        Assert.Same(typeof(PostgreSqlCodeFirst), Provider.CodeFirst.GetType());
        Assert.Same(typeof(PostgreSqlCodeFirst), HighgoLeaf.CodeFirst.GetType());
        Assert.Same(typeof(PostgreSqlCodeFirst), OpenGaussLeaf.CodeFirst.GetType());
        Assert.Same(typeof(PostgreSqlCodeFirst), GaussDbLeaf.CodeFirst.GetType());
        Assert.Same(typeof(PostgreSqlCodeFirst), KingbaseLeaf.CodeFirst.GetType());
    }

    [Fact]
    public void The_outer_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word()
    {
        // PG 血统的合并档接受 INNER（逗号表源），LEFT 才被 PostgreSqlDialect.EmitJoinedSources 拒收
        // ——这一格与 MySQL 血统那三家的"任何 JOIN 都拒"是对面而不是同形，所以这里的 Kind 例外
        // 钉的是拒收文案那一颗词：两家同闸、差异恰好一个词，不是两份文案。对读对象本册换成<em>瀚高</em>叶
        // ——与前两册取 GaussDB 叶不同，因为本册要让"两种来路的两颗键"在拒收文案上也咬一次：
        // 直称血统协议的那两颗（瀚高 ∶ 8c）彼此只差 Kind 一颗词，而文案里"8c"这颗词本身是
        // DbKind.Gbase8c 的原样 ToString（含数字），所以替换按整词做一次。
        var highgoFailure = Checks.Raised<QuickSqlException>(() => JoinedUpdate(nameof(Order.Amount), HighgoLeaf, JoinKind.Left));
        var gbase8cFailure = Checks.Raised<QuickSqlException>(() => JoinedUpdate(nameof(Order.Amount), Provider, JoinKind.Left));

        Assert.StartsWith("Gbase8c ", gbase8cFailure.Message, StringComparison.Ordinal);
        Assert.Equal(highgoFailure.Message.Replace("Highgo", "Gbase8c", StringComparison.Ordinal), gbase8cFailure.Message);
        Checks.Has("不接受 LEFT JOIN", gbase8cFailure.Message);
    }

    [Fact]
    public void Boolean_literals_and_backslash_escape_and_bulk_budget_come_from_the_family_unchanged()
    {
        // 血统在这一家的形状半：恒假/恒真是布尔字面量（六家里唯一改写这两颗的一家）、LIKE 转义是
        // 反斜杠（MySQL 两家的波浪与虚谷的那颗都在别处）、高速层是预算件不是通道。
        // 最后这一档在本家多一句读法：分片表上的 COPY 语义行里没写，而它落在高速层而不是文本层，
        // 与那颗分片子句同属"行点了名而发射面不接"的一族格子。
        Assert.Equal("false", Provider.Dialect.EmptySetPredicate);
        Assert.Equal("true", Provider.Dialect.AlwaysTruePredicate);
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);
        Assert.IsType<PostgreSqlBulk>(Provider.Bulk);
        Assert.Equal('@', Provider.Dialect.ParameterPrefix);
    }

    [Fact]
    public void Guid_maps_to_the_native_uuid_here_too_as_the_fifth_provider_of_that_claim()
    {
        // 金仓那两条 Guid 用例从 v0.22 起曾是这一格在全仓库唯一的一份主张，v0.36 四笔各来一个复件，
        // 本笔是第五个：同一个类型名、同一个位、来自同一份血统类型表——"4∶4" 的分布里 PG 血统
        // 这边从四家变五家，比值翻成 5∶4，而五边说的仍然同一句话：有原生 uuid，char(36) 是给
        // 没有的库留的退路。它不证"8c 的 uuid 是 16 字节存"——那一半与另四家同样欠着，
        // 而且第五份复件仍然量不出那一问的答案（"复件涨到第五本仍然量不出新事实"是同一格的形状本身）。
        var id = orm().Schemas.Contract<SessionToken>().Columns
            .Single(c => c.MemberName == nameof(SessionToken.Id));

        Assert.Equal("UUID", Provider.Types.GetStoreType(id));
        Assert.True(Provider.Capabilities.HasFlag(DbCapabilities.Guid_Raw16));
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>同一条链在两家各跑一遍、逐字对读（对拍对象是金仓叶子——六路链里血统侧的另几段
    /// 由掩码、两两补满与无缝三格分别量，见文件头第一段为什么不是"本尊"）。
    /// <b>不许折任何字形</b>——引号都不差才是这一对的断言。</summary>
    static void AssertSame(Func<QuickSqlOrm, string> chain, string label)
    {
        var kingbaseSql = chain(kingbase());
        var gbase8cSql = chain(orm());

        Assert.True(kingbaseSql == gbase8cSql, $"{label} 这一格同形破了：kingbase = {kingbaseSql} ／ gbase8c = {gbase8cSql}");
    }

    static void AssertSameProvider(Func<IDbProvider, string> chain, string label)
    {
        var kingbaseSql = chain(KingbaseLeaf);
        var gbase8cSql = chain(Provider);

        Assert.True(kingbaseSql == gbase8cSql, $"{label} 这一格同形破了：kingbase = {kingbaseSql} ／ gbase8c = {gbase8cSql}");
    }

    /// <summary>血统基类的可实例化形态（与 <c>HighgoFamilyProbe</c> 同一写法：空壳，
    /// 探针存在的唯一理由是上面那条"叶子没给基类添一字"的空壳证据）。</summary>
    sealed class Gbase8cFamilyProbe(DbKind kind, DbProviderFactory factory) : PostgreSqlProviderBase(kind, factory);

    static SqlWriter NewWriter(IDbProvider? provider = null)
        => new(new StringBuilder(), [], (provider ?? Provider).Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit, IDbProvider? provider = null)
    {
        var writer = NewWriter(provider);

        emit(writer);

        return writer.ToSqlString();
    }

    /// <summary>手搭带 JOIN 的合并 UPDATE（形状与 <c>HighgoDialectTests</c> 里那份同构，只差参战五方：
    /// 本家、瀚高、两颗分叉叶、金仓叶。INNER 折得动、LEFT 才被 <c>EmitJoinedSources</c> 拒收，
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
