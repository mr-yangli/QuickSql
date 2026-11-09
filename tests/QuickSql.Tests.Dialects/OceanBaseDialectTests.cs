using System.Data.Common;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.MySqlBase;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>OceanBase（MySQL 模）这颗叶子的 SQL <b>文本</b>（§9.1 那一行"先定"裁决的兑现日套件）。
/// 全程 <see cref="SqlServer.NoDriverFactory"/>，与另外五处离线套件同一分工。
///
/// <para><b>这一族没有 golden/oceanbase/ 目录，而判据与 <c>TiDbDialectTests</c> 同一条而更硬</b>：
/// §13.3 那句"同形交给对拍"在这里是<b>四方</b>逐字同形（基类、mysql 本尊壳、TiDb 壳、本家），
/// 再抄一份基线只会多一处要同步的地方。先例是 mysql 台账的 <c>Paging_LimitOffset</c> 行
/// （"同形交对拍，故无基线"）与 TiDb 那一整族的处置，本文件把它们原样推广到第三颗叶子。</para>
///
/// <para><b>这份套件证明什么、不证明什么，比 TiDb 那份还要多一句</b>：它证"本仓库没有为 OceanBase 重写一字"，
/// 不证"OceanBase 接受 MySQL 的那一字"——后者本机无一枚实例可问。而这一家<em>独有</em>的第二层悬空是：
/// 就算哪天接上一台真库，第一件事也不是跑这些句子，而是确认<b>那条连接建在 MySQL 模</b>
/// （"模式在连接上决定"，§9.1 原话；SPI 问不出这一问，§14 那格参数名单的第二个实例）。
/// 所以本文件的一切断言都停在「离线已钉」档，且每一颗绿都自带一个前件："若此连接在 MySQL 模"。
/// 逐位出处记在 <see cref="CapabilityLedgerTests"/> 的第六本台账，语义级分岔（分区表与全局索引不进契约、
/// 自增不连续同 TiDb 一类）记在 <c>OceanBaseProvider</c> 类注释与 §14，<em>不</em>因文本同形而被抹平。</para>
///
/// <para>这里也不进 <c>DialectParityTests</c>：mysql↔oceanbase 与 tidb↔oceanbase 今天都是<em>零处不同</em>
/// （Kind 文案那颗例外由 <see cref="The_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word"/>
/// 单独点名），把恒等式并进两两配对只会稀释对拍的判据。"同形交对拍"由本文件的逐字相等断言直接兑现，
/// 而本家额外带来一格新形状：两颗<em>分叉</em>叶子互相对读（<see cref="The_two_forked_leaves_are_byte_identical_to_each_other_not_just_to_the_progenitor"/>）——
/// 分叉库之间也同形，这件事此前没有任何见证可指。</para></summary>
public sealed class OceanBaseDialectTests
{
    static readonly IDbProvider Provider = OceanBase.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider MysqlLeaf = MySql.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>血统基类的可实例化形态（与 <c>TiDbDialectTests</c> 那份探针同一颗基类，Kind 换成这一颗叶子用的值）。
    /// <b>Kind 取 <see cref="DbKind.OceanBase"/></b>：探针要和叶逐字同形才有意义，而 Kind 会进
    /// <c>EmitJoinedSources</c> 的异常文案 —— 两侧同 Kind、比的恰好是"叶子有没有给基类添一字"。</summary>
    static readonly IDbProvider Family = new OceanBaseFamilyProbe(DbKind.OceanBase, SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => OceanBase.Orm();

    static QuickSqlOrm mysql() => MySql.Orm();

    /// <summary>同一颗占位串、同一颗假工厂，provider 换人 —— 让 DDL 三段能按 <see cref="IDbProvider"/> 参数化。</summary>
    static QuickSqlOrm ormFor(IDbProvider provider)
        => new(new QuickSqlOrmOptions { Provider = provider, ConnectionString = SqlServer.OfflineConnectionString });

    /// <summary>带模式的模型：与 <c>MySqlDialectTests</c> 那一份同一手法（Fluent 覆写而不是新建实体），
    /// 差别只是这里要各造一份 —— "限定符出来了而没被折叠"这一格量在两家产出<em>相等</em>上。</summary>
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
        // 这一条钉的是"叶子到底带了什么进来"：一颗枚举值（v0.30 就占着位，本笔零 Core 改动）、
        // 一颗内建表键，其余全部继承。内建表键 "oceanbase" 与 "mysql"／"tidb" 共用同一份 assembly
        // （内建表 BuiltinDrivers 第一次三键一程序集）——键不同而 assembly 相同不是冗余，
        // 是"驱动按协议族装、库种按方言假设装"那格解开的第三个现场。
        Assert.Equal(DbKind.OceanBase, Provider.Kind);
        Assert.Equal("oceanbase", QuickSql.Providers.OceanBase.OceanBaseProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.MySql.MySqlProvider.DriverKey, QuickSql.Providers.OceanBase.OceanBaseProvider.DriverKey);
    }

    [Fact]
    public void The_capability_mask_is_inherited_verbatim_no_bit_added_no_bit_dropped()
    {
        Assert.Equal(MysqlLeaf.Capabilities, Provider.Capabilities);

        Assert.Equal(
            DbCapabilities.Paging_LimitOffset | DbCapabilities.Schemas | DbCapabilities.Guid_Char36 |
            DbCapabilities.Comment_On_Column | DbCapabilities.Alter_Column_Precision | DbCapabilities.Bulk_Copy | DbCapabilities.Transactions,
            Provider.Capabilities);

        // 第六颗同样是本笔随血统基类继承的 Bulk_Copy，而它在本家多压一层这一家独有的前件（"先问模式"）：
        // LOAD DATA 那一条在 OceanBase 上是不是也在 MySQL 模才成立，今天连推断都排不到——它比 tidb 那本
        // 更接近"位是继承来的、问句还没被问"。本家照旧没加一颗没丢一颗，只是那颗数从今天起是六。
        // 四位"库里大概有而这一家不声明"在 OceanBase 一家都不翻案：判据与 MySqlProviderBase 类注释逐条同源，
        // 而这一家还压着前两家没有的一层"先问模式"——连"按 MySQL 文档翻案"的资格都要排在连接级确认之后。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Boolean_Type));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.CaseSensitive_Identifiers));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Returning));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Upsert_OnDuplicate));
    }

    [Fact]
    public void The_read_side_is_byte_identical_to_the_mysql_leaf_across_five_chains()
    {
        // 五条链挑的是这一族各站一边的格子：反引号（select）、尾巴两子句（paging）、2^64−1 哨兵（skip-only）、
        // 共用层布尔谓词（= 1）、LIKE 的波浪转义。逐字相等在这里比"各自绿"多证一件事：
        // 这一家没有任何一处会跟着 mysql 那一份样本一起漂。
        AssertSame(o => o.Select<Order>().ToSql(), "plain select");

        AssertSame(o => o.Select<Order>().OrderBy(x => x.Id).Skip(10).Take(5).ToSql(), "paging limit-offset");

        AssertSame(o => o.Select<Order>().OrderBy(x => x.Id).Skip(30).ToSql(), "skip-only sentinel");

        AssertSame(o => o.Select<Order>().Where(x => x.State == OrderState.Paid && x.Amount > 1m).ToSql(), "predicate shared form");

        AssertSame(o => o.Select<Order>().Where(x => x.CustomerName!.Contains("50%_off")).ToSql(out _), "like escape tilde");
    }

    [Fact]
    public void The_write_side_is_byte_identical_to_the_mysql_leaf_including_the_key_read_back()
    {
        AssertSame(o => o.Update<Order>().Set(x => x.Amount, x => x.Amount + 1m).Where(x => x.Id == 7).ToSql(), "update with target alias");

        AssertSame(o => o.Delete<Order>().Where(x => x.Id == 7).ToSql(), "delete");

        AssertSame(o => o.Insert(row(1)).ToSql(), "plain insert");

        // v0.34 那条语句后回读在这一家逐字同形：LAST_INSERT_ID() 是会话级函数，MySQL 模按兼容层走同一颗形状【推断】。
        // 差别在<em>批次</em>那一头 —— OceanBase 的自增同样是带缓存的预分配（§9.1 那一行点名的 AUTO_ID_CACHE 类系统变量），
        // "多行 VALUES 的 id 连续"在这一家同样不成立，而 Core 今天恰好只发逐条 + 回读，所以这一形同文。
        AssertSame(o => o.Insert(row(1)).ExplainKeys().ToSql(), "insert with post-statement key read");

        Checks.Has(" ; SELECT LAST_INSERT_ID()", orm().Insert(row(1)).ExplainKeys().ToSql());
    }

    [Fact]
    public void The_two_forked_leaves_are_byte_identical_to_each_other_not_just_to_the_progenitor()
    {
        // 本文件相对 TiDbDialectTests 唯一新增的形状：两颗分叉叶子<em>互相对读</em>。
        // 此前所有同形断言都是"叶子对mysql/基类"，两颗分叉库之间没有任何一条见证——
        // 它们各自与本尊同形，逻辑上必然彼此同形，而"必然"在这个仓库里从来不算断言（§13.5 那句
        // "拿字符串搜产物搜出来的断言会错"是同一条纪律的近亲）。这一条把那格"必然"落成可跑的证据。
        var tidb = TiDb.Orm();
        var oceanBase = orm();

        (string Label, Func<QuickSqlOrm, string> Chain)[] chains =
        [
            ("select", static o => o.Select<Order>().ToSql()),
            ("paging", static o => o.Select<Order>().OrderBy(x => x.Id).Skip(10).Take(5).ToSql()),
            ("key read-back", static o => o.Insert(row0()).ExplainKeys().ToSql()),
            ("update", static o => o.Update<Order>().Set(x => x.Amount, x => x.Amount + 1m).Where(x => x.Id == 7).ToSql()),
        ];

        foreach (var (label, chain) in chains)
            Assert.True(chain(tidb) == chain(oceanBase), $"{label} 这一格两颗分叉叶子不同形：tidb = {chain(tidb)} ／ oceanbase = {chain(oceanBase)}");

        Assert.Equal(TiDb.Provider(SqlServer.NoDriverFactory.Instance).Capabilities, Provider.Capabilities);
    }

    [Fact]
    public void The_DDL_side_is_byte_identical_to_the_mysql_leaf_create_alter_and_comment()
    {
        AssertSameProvider(
            p => Statement(w => p.CodeFirst.EmitCreateTable(w, ormFor(p).Schemas.Contract<Order>()), p),
            "create table");

        AssertSameProvider(
            p => Statement(w => p.CodeFirst.EmitCreateTable(w, ormWithSchema(p).Schemas.Contract<Order>()), p),
            "create table with schema");

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
            "alter column precision");

        // 注释这一格在本家同样只是"跟着列定义走的那一句 MODIFY COLUMN"（本仓库没为它另立分岔）。
        AssertSameProvider(
            p =>
            {
                var contract = ormFor(p).Schemas.Contract<Order>();
                var commented = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName)) with { Comment = "客户名" };

                return Statement(w => Assert.True(p.CodeFirst.TryEmitColumnComment(w, contract.Table, commented)), p);
            },
            "column comment");

        // 本家侧再各钉一枚绝对形状（相等之外还要知道<em>是什么</em>，否则两边一起漂就量不出来）：
        // 反引号、无模式裸表名、限定不折叠 —— 后两格正是 Schemas 那一位在本家的见证。
        // 而 create table 这一份产出里<em>没有</em> PARTITION BY：分区是这一家真正的形状分岔，
        // 本仓库不在契约里表达它（§5 的取舍），缺席本身由下面的 Absent 钉成断言而不是巧合。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("CREATE TABLE `QuickSqlOrder` (", sql);
        Checks.Absent("`APP`.", sql);
        Checks.Absent("PARTITION BY", sql);
        Checks.Has("CREATE TABLE `app`.`QuickSqlOrder` (",
            Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema(Provider).Schemas.Contract<Order>())));
        Checks.Absent("`APP`",
            Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema(Provider).Schemas.Contract<Order>())));
    }

    [Fact]
    public void The_auto_increment_clause_survives_the_leaf_without_override()
    {
        // §9.1 点名的第一颗覆写点，对完了结论仍是"不覆写"：MySQL 模的自增写法按兼容层就是 AUTO_INCREMENT
        // 【推断，未实测】，MySqlCodeFirst.IdentityClause 那颗零覆写者的虚口撑过了<b>第二颗</b>分叉叶子的到场。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("`Id` INT AUTO_INCREMENT NOT NULL", sql);
        Checks.Absent("IDENTITY", sql);
        Checks.Absent("GENERATED BY DEFAULT", sql);
    }

    [Fact]
    public void The_create_table_tail_inherits_the_const_table_options_and_that_is_a_ruling_not_an_accident()
    {
        // §9.1 点名的第二颗覆写点："等 TiDB 那天再虚化"在第三笔被改判为"仍不虚化"，本条是那条裁决的
        // <em>第二次适用</em>：OceanBase MySQL 模认这颗尾巴【推断，未实测】，需要的是同一颗值，所以不为它虚化。
        // 它<em>不</em>钉"OceanBase 接受这颗选项"——接受度与"这条连接在 MySQL 模"是同一天要问的两件事（§14）。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has(") ENGINE = InnoDB DEFAULT CHARSET = utf8mb4", sql);
    }

    [Fact]
    public void The_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word()
    {
        // "全family逐字同形"有且只有一处已知的例外：发射层拒收文案里的那颗 {Kind}。
        // 这一条把例外本身钉死 —— 它证明两件事：闸门三家同开（本仓库没为这颗叶子另立分岔），
        // 而差异恰好是一颗词，不是两份文案。
        var mysqlFailure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount), MysqlLeaf));
        var oceanBaseFailure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount), Provider));

        Assert.StartsWith("OceanBase ", oceanBaseFailure.Message, StringComparison.Ordinal);
        Assert.Equal(mysqlFailure.Message.Replace("MySql", "OceanBase", StringComparison.Ordinal), oceanBaseFailure.Message);
        Checks.Has("不接受目标表之外的表源", oceanBaseFailure.Message);
        Checks.Has("SupportsJoinedWrites", oceanBaseFailure.Message);
    }

    [Fact]
    public void Like_escape_and_bulk_budget_come_from_the_family_unchanged()
    {
        Assert.Equal('~', Provider.Expressions.LikeEscapeCharacter);
        Assert.IsType<MySqlBulk>(Provider.Bulk);
        Assert.Equal('@', Provider.Dialect.ParameterPrefix);
    }

    [Fact]
    public void The_leaf_overrides_nothing_so_its_entire_output_is_the_family_s()
    {
        // 空壳证据，与 mysql 家那条同名异责：那一条证"叶子=本尊"，这一条证"第二颗分叉库=血统"——
        // 两侧 Kind 同为 OceanBase，比的恰好是叶子有没有往基类上添一字。
        var leaf = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormFor(Provider).Schemas.Contract<Order>()));
        var family = Statement(w => Family.CodeFirst.EmitCreateTable(w, ormFor(Family).Schemas.Contract<Order>()));

        Assert.Equal(family, leaf);
        Assert.Equal(Family.Dialect.ParameterPrefix, Provider.Dialect.ParameterPrefix);
        Assert.Equal(Family.Capabilities, Provider.Capabilities);
    }

    [Fact]
    public void Guid_columns_map_to_CHAR36_here_too_because_the_binary_option_stays_unprovable()
    {
        // 与 mysql／tidb 那两条同一判据：BINARY(16) 那颗选择要连接串参数与字节置换一致才成立，
        // 而 SPI 里没有那一格（§14）——这一格在第二颗分叉叶子上没有变得更可证，
        // 只多了一句"MySQL 模大概率同样"的【推断】，而那句还压着"先确认模式"的前件。
        var id = orm().Schemas.Contract<SessionToken>().Columns
            .Single(c => c.MemberName == nameof(SessionToken.Id));

        Assert.Equal("CHAR(36)", Provider.Types.GetStoreType(id));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Guid_Raw16));
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>两颗分叉叶子互读那条用的固定行（静态 lambda 里够不着实例方法 <see cref="row"/>）。</summary>
    static Order row0() => new()
    {
        CustomerName = "客户1",
        CustomerId = 1,
        Amount = 11m,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, 890),
    };

    /// <summary>同一条链在两家各跑一遍、逐字对读。失败消息带 label 与两侧全文：一条用例量一批链的代价
    /// 是红了要自己找是哪颗，这里用 label 将那个代价抵掉。<b>不许折任何字形</b>——引号都不差才是这一对的断言。</summary>
    static void AssertSame(Func<QuickSqlOrm, string> chain, string label)
    {
        var mysqlSql = chain(mysql());
        var oceanBaseSql = chain(orm());

        Assert.True(mysqlSql == oceanBaseSql, $"{label} 这一格同形破了：mysql = {mysqlSql} ／ oceanbase = {oceanBaseSql}");
    }

    static void AssertSameProvider(Func<IDbProvider, string> chain, string label)
    {
        var mysqlSql = chain(MysqlLeaf);
        var oceanBaseSql = chain(Provider);

        Assert.True(mysqlSql == oceanBaseSql, $"{label} 这一格同形破了：mysql = {mysqlSql} ／ oceanbase = {oceanBaseSql}");
    }

    /// <summary>血统基类的可实例化形态（与 <c>TiDbDialectTests</c> 里 <c>TiDbFamilyProbe</c> 同一写法：
    /// 空表达式类，探针存在的唯一理由是上面那条空壳证据）。</summary>
    sealed class OceanBaseFamilyProbe(DbKind kind, DbProviderFactory factory) : MySqlProviderBase(kind, factory);

    static SqlWriter NewWriter(IDbProvider? provider = null)
        => new(new StringBuilder(), [], (provider ?? Provider).Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit, IDbProvider? provider = null)
    {
        var writer = NewWriter(provider);

        emit(writer);

        return writer.ToSqlString();
    }

    static DialectSql EmitUpdate(UpdateStatement ast, IDbProvider owner)
    {
        var writer = NewWriter(owner);

        owner.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    /// <summary>手搭合并 UPDATE，形状与 <c>MySqlDialectTests</c> 里那一份同构（用途也只有一颗：抛得准不准，
    /// 而"准"的内容由上面那条 Kind 用例钉）。表源在就够了，键与行集值不需要真实。</summary>
    static DialectSql MergedUpdate(string member, IDbProvider owner)
    {
        var contract = (owner == Provider ? orm() : mysql()).Schemas.Contract<Order>();

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

        return EmitUpdate(new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(set, new ColumnExpression(side, set))],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, key), new ParameterValue(7, key)),
            Joins = [new JoinClause(JoinKind.Inner, new SourceRows(side, columns, rows), on)],
        }, owner);
    }
}
