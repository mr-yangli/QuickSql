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

/// <summary>TiDB 这颗叶子的 SQL <b>文本</b>（§9.1 那一行的兑现日套件）。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与另外四族的离线套件同一分工。
///
/// <para><b>这一族没有 golden/tidb/ 目录，而这是判据不是省事</b>：§13.3 那句"同形交给对拍"在这一家走到极端——
/// 叶与血统基类与 MySQL 本尊三方<b>逐字</b>同形（连引号字形都不差），再抄一份基线只会多一处要同步的地方。
/// 先例是 mysql 自己那本台账里 <c>Paging_LimitOffset</c> 那一行（"同形交对拍，故无基线"），本文件把同一处置
/// 推广到整族。所以这里的形状是：一条<em>全family逐字相等</em>的断言链（读、写、DDL 三段各量一批链）
/// 加一段空壳证据（叶对基类），外加本家<em>自己</em>带进来的那两格事实（<c>DbKind.TiDb</c> 与 <c>"tidb"</c> 键）。</para>
///
/// <para><b>这些相等证明的是什么、不证明什么，必须先说清再读绿</b>：它证的是"本仓库没有为 TiDB 重写任何一字"，
/// 不证"TiDB 接受 MySQL 的那一字"。后者在本机没有一枚实例可问（§13.4 授权三台里没有它，官方镜像在 §13.1
/// 第 4 项点名过而未起），所以本文件的一切断言都停在「离线已钉」档，「真库跑过」一颗都没有 ——
/// 逐位的出处记在 <see cref="CapabilityLedgerTests"/> 的第五本台账，语义级分岔（自增批量预分配、
/// 表选项被忽略、LIMIT 大偏移退化）记在 <c>TiDbProvider</c> 类注释与 §14，<em>不</em>因为文本同形而被这份套件抹平。</para>
///
/// <para>这里也不进 <c>DialectParityTests</c>：那一份的判据是"不同处必须点名 + 相同处必须逐字"，而 mysql↔tidb
/// 这一对今天<em>零处不同</em>（Kind 文案那颗例外由 <see cref="The_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word"/>
/// 单独点名），把它并进六家两两十五对只会新增一对恒等式。"同形交对拍"在这里由本文件的逐字相等断言直接兑现。</para></summary>
public sealed class TiDbDialectTests
{
    static readonly IDbProvider Provider = TiDb.Provider(SqlServer.NoDriverFactory.Instance);

    static readonly IDbProvider MysqlLeaf = MySql.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>血统基类的可实例化形态（与 <c>MySqlDialectTests</c> 那份探针同一颗基类，Kind 换成这一颗叶子用的值）。
    /// <b>Kind 取 <see cref="DbKind.TiDb"/></b>：探针要和叶逐字同形才有意义，而 Kind 会进
    /// <c>EmitJoinedSources</c> 的异常文案 —— mysql 家那条空壳证据就是靠同 Kind 才量得出的，这里换一种量法
    /// （两侧都用 TiDb，比的恰好是"叶子有没有给基类添一字"）。</summary>
    static readonly IDbProvider Family = new TiDbFamilyProbe(DbKind.TiDb, SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => TiDb.Orm();

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
        // 这一条钉的是"叶子到底带了什么进来"：一颗枚举值、一颗内建表键，其余全部继承。
        // 内建表键 "tidb" 与 "mysql" 共用同一份 assembly（内建表 BuiltinDrivers 里第一次出现两键一程序集），
        // 所以 DriverKey 不同而 assembly 相同不是矛盾，是把"驱动"与"库种"解开的那一格露在了内建表上。
        Assert.Equal(DbKind.TiDb, Provider.Kind);
        Assert.Equal("tidb", QuickSql.Providers.TiDb.TiDbProvider.DriverKey);
        Assert.NotEqual(QuickSql.Providers.MySql.MySqlProvider.DriverKey, QuickSql.Providers.TiDb.TiDbProvider.DriverKey);
    }

    [Fact]
    public void The_capability_mask_is_inherited_verbatim_no_bit_added_no_bit_dropped()
    {
        Assert.Equal(MysqlLeaf.Capabilities, Provider.Capabilities);

        Assert.Equal(
            DbCapabilities.Paging_LimitOffset | DbCapabilities.Schemas | DbCapabilities.Guid_Char36 |
            DbCapabilities.Comment_On_Column | DbCapabilities.Alter_Column_Precision | DbCapabilities.Bulk_Copy | DbCapabilities.Transactions,
            Provider.Capabilities);

        // 六位里有五位有离线文本，第六位是本笔随血统基类一起继承下来的 Bulk_Copy——它到本家<em>不改写一个字</em>
        // （继承的就是那颗反射口），而本家的 LOAD DATA 侧今天连驱动解析都没跑过，位因此与 mysql 本家同档不同因：
        // 那一家有驱动API 的真库读数而这一家一份都没有，"没加一颗没丢一颗"这一句照旧成立，只是它现在量的是六颗。
        // 四位"库里大概有而这一家不声明"在 TiDB 一家都不翻案：判据与 MySqlProviderBase 类注释逐条同源，
        // 而 BOOLEAN / 大小写敏感两位在 TiDB 侧的处境【推断：MySQL 兼容层】比 MySQL 本尊更不会翻。
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

        // v0.34 那条语句后回读在这一家逐字同形：LAST_INSERT_ID() 是会话级函数，两家的形状理由同一条。
        // 差别在<em>批次</em>那一头 —— 自增批量预分配让"多行 VALUES 的 id 连续"在 TiDB 更不成立，
        // 而 Core 今天恰好只发逐条 + 回读，所以这一形在两家同文；那一天改批量形状要先回来翻这条（§14）。
        AssertSame(o => o.Insert(row(1)).ExplainKeys().ToSql(), "insert with post-statement key read");

        Checks.Has(" ; SELECT LAST_INSERT_ID()", orm().Insert(row(1)).ExplainKeys().ToSql());
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
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("CREATE TABLE `QuickSqlOrder` (", sql);
        Checks.Absent("`APP`.", sql);
        Checks.Has("CREATE TABLE `app`.`QuickSqlOrder` (",
            Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema(Provider).Schemas.Contract<Order>())));
        Checks.Absent("`APP`",
            Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema(Provider).Schemas.Contract<Order>())));
    }

    [Fact]
    public void The_auto_increment_clause_survives_the_leaf_without_override()
    {
        // §9.1 点名的第一颗覆写点，对完了结论是"不覆写"：TiDB 的自增写法就是 AUTO_INCREMENT【推断，未实测】，
        // 而 MySqlCodeFirst.IdentityClause 那颗零覆写者的虚口在第二颗叶子到场<em>之后</em>仍然零覆写者 ——
        // 这一句是本条断言的全部意义，重开触发写在 §14 那一格。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has("`Id` INT AUTO_INCREMENT NOT NULL", sql);
        Checks.Absent("IDENTITY", sql);
        Checks.Absent("GENERATED BY DEFAULT", sql);
    }

    [Fact]
    public void The_create_table_tail_inherits_the_const_table_options_and_that_is_a_ruling_not_an_accident()
    {
        // §9.1 点名的第二颗覆写点："等 TiDB 那一层来那天再虚化" —— 那天来了，裁决是<em>仍不虚化</em>
        // （理由全文写在 TiDbProvider 类注释：不留一颗没人覆写的虚函数，而这一颗叶子需要的值是同一颗）。
        // 所以这一条钉的是 const 仍然原样出现在本家产出里；它<em>不</em>钉"TiDB 接受这颗选项"——
        // 接受度是【推断】，重开条件同 §13.1 第 4 项的官方镜像，到场后第一件事就是量这颗。
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Checks.Has(") ENGINE = InnoDB DEFAULT CHARSET = utf8mb4", sql);
    }

    [Fact]
    public void The_joined_write_refusal_differs_from_the_sibling_leaf_only_in_the_kind_word()
    {
        // "全family逐字同形"有且只有一处已知的例外：发射层拒收文案里的那颗 {Kind}。
        // 这一条把例外本身钉死 —— 它证明两件事：闸门两家同开（本仓库没有为 TiDB 另立分岔），
        // 而差异恰好是一颗词，不是两份文案。
        var mysqlFailure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount), MysqlLeaf));
        var tidbFailure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount), Provider));

        Assert.StartsWith("TiDb ", tidbFailure.Message, StringComparison.Ordinal);
        Assert.Equal(mysqlFailure.Message.Replace("MySql", "TiDb", StringComparison.Ordinal), tidbFailure.Message);
        Checks.Has("不接受目标表之外的表源", tidbFailure.Message);
        Checks.Has("SupportsJoinedWrites", tidbFailure.Message);
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
        // 空壳证据，与 mysql 家那条同名异责：那一条证"叶子=本尊"，这一条证"分叉库=血统"——
        // 两侧 Kind 同为 TiDb，比的恰好是叶子有没有往基类那 1 557 行上添一字。
        var leaf = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormFor(Provider).Schemas.Contract<Order>()));
        var family = Statement(w => Family.CodeFirst.EmitCreateTable(w, ormFor(Family).Schemas.Contract<Order>()));

        Assert.Equal(family, leaf);
        Assert.Equal(Family.Dialect.ParameterPrefix, Provider.Dialect.ParameterPrefix);
        Assert.Equal(Family.Capabilities, Provider.Capabilities);
    }

    [Fact]
    public void Guid_columns_map_to_CHAR36_here_too_because_the_binary_option_stays_unprovable()
    {
        // 与 mysql 那一条（"A lineage with a binary option can still pick the text one"）同一判据：
        // BINARY(16) 那颗选择要连接串参数与字节置换一致才成立，而 SPI 里没有那一格（§14）——
        // 这一格在分叉库上没有变得更可证，只多了一句"TiDB 大概率同样"的【推断】。
        var id = orm().Schemas.Contract<SessionToken>().Columns
            .Single(c => c.MemberName == nameof(SessionToken.Id));

        Assert.Equal("CHAR(36)", Provider.Types.GetStoreType(id));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Guid_Raw16));
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>同一条链在两家各跑一遍、逐字对读。失败消息带 label 与两侧全文：一条用例量一批链的代价
    /// 是红了要自己找是哪颗，这里用 label 将那个代价抵掉。<b>不许折任何字形</b>——引号都不差才是这一对的断言。</summary>
    static void AssertSame(Func<QuickSqlOrm, string> chain, string label)
    {
        var mysqlSql = chain(mysql());
        var tidbSql = chain(orm());

        Assert.True(mysqlSql == tidbSql, $"{label} 这一格同形破了：mysql = {mysqlSql} ／ tidb = {tidbSql}");
    }

    static void AssertSameProvider(Func<IDbProvider, string> chain, string label)
    {
        var mysqlSql = chain(MysqlLeaf);
        var tidbSql = chain(Provider);

        Assert.True(mysqlSql == tidbSql, $"{label} 这一格同形破了：mysql = {mysqlSql} ／ tidb = {tidbSql}");
    }

    /// <summary>血统基类的可实例化形态（与 <c>MySqlDialectTests</c> 里 <c>MySqlFamilyProbe</c> 同一写法：
    /// 空表达式类，探针存在的唯一理由是上面那条空壳证据）。</summary>
    sealed class TiDbFamilyProbe(DbKind kind, DbProviderFactory factory) : MySqlProviderBase(kind, factory);

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
