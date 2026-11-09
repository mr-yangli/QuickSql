using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>装配口那颗「整型主键归客户端给号」的活库半（离线五臂在 <c>ApplicationKeyConventionTests</c>）。
///
/// <para><b>事故的形状</b>（本轮的证人，真库 PostgreSQL）：ABP 那一族的 <c>long</c> 主键由 <c>IIdGenerator</c>
/// （雪花）在实体构造时赋值，而现库 11 座业务库里的 <c>Id</c> 列没有一颗是 identity 或序列 ——
/// 全是 <c>bigint DEFAULT 0</c>。库按「整型主键 ⇒ 自增」发 INSERT 时把 Id 留在列清单外，
/// 于是每一行都取那颗默认值：第一行落库是 0，第二行撞 <c>pkey</c> 以 <c>23505</c> 收尾，
/// 症状是"登录写一条安全日志就 500"。</para>
///
/// <para><b>为什么这一半只能在库里看</b>：离线那册能钉列清单里有没有 Id，但"两行各带着自己那个号进去了"
/// 这一句只有库能作证 —— 尤其是<b>第二行没有撞键</b>这件事：默认值那一格只在有人真插第二行时才露出来，
/// 单行插入在两种契约下都能绿。</para>
///
/// <para><b>裁判一律问裸 ADO 与目录</b>，与 <see cref="NullabilityTighteningPathTests"/> 同一条纪律：
/// 写的一侧是被审的 ORM，读的一侧不能再是它。目录那一句答的是"库里这一列今天到底是不是自增"。</para></summary>
public sealed class ApplicationKeyPathTests
{
    /// <summary>靶实体：一颗没有任何一层表过态的 <c>long</c> 主键 —— 正是 <c>AggregateRoot&lt;long&gt;</c>
    /// 那一族的形状。两行都带同一个批次标签，读侧按标签过滤，不碰库里别人的数据。</summary>
    [Table("QuickSqlAppKeyPath")]
    public sealed class AppKeyRow
    {
        public long Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Tag { get; set; }
    }

    const string TableName = "QuickSqlAppKeyPath";

    static string Quote(string identifier) => Database.Provider.CodeFirst.Identifiers.Quote(identifier);

    static string Table => Quote(TableName);

    static CancellationToken ct => TestContext.Current.CancellationToken;

    /// <summary>装配口关掉那一半的一台：注册表走库里那颗"由外部提供"的干净口，
    /// 于是宿主等价的那一趟 <c>SyncStructureAsync</c> 建出来的 <c>Id</c> 列不带自增那一格。</summary>
    static QuickSqlOrm AppKeyOrm()
        => new(
            new QuickSqlOrmOptions
            {
                Provider = Database.Provider,
                ConnectionString = Database.ConnectionString,
                SchemaSync = SchemaSyncPolicy.Execute,
                ForceSchemaSync = true,
            },
            new EntitySchemaRegistry(new ContractResolver(Database.Provider, null, integralPrimaryKeyAutoIncrement: false)));

    /// <summary>默认档那一台：同一份实体、同一座库，装配口一个字没写。</summary>
    static QuickSqlOrm DefaultOrm()
        => new(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
        });

    /// <summary>把靶表摆回"这一列归客户端给号"那一份形状：先拆，再由装配过的那一台建。</summary>
    static QuickSqlOrm RebuildStore()
    {
        Database.Execute($"DROP TABLE IF EXISTS {Table}");

        return AppKeyOrm();
    }

    [Fact]
    public async Task Two_rows_with_client_generated_keys_both_land_and_neither_takes_the_store_default()
    {
        Database.Ready();

        // 两档的契约确实分岔：同一份实体、同一座库，装配口说没说那句话给出两个答案。
        // 这一句先放在这里，是因为下面那一行一旦落库，"默认档"那一台就只能在一张非自增列上发句子，
        // 而那一句会红成什么形状不是本案要判的东西（离线那册钉着列清单）。
        Assert.True(DefaultOrm().Schemas.Contract<AppKeyRow>().Columns
            .Single(c => c.MemberName == nameof(AppKeyRow.Id)).IsIdentity);

        var orm = RebuildStore();

        Assert.False(orm.Schemas.Contract<AppKeyRow>().Columns
            .Single(c => c.MemberName == nameof(AppKeyRow.Id)).IsIdentity);

        await orm.SyncStructureAsync(ct);

        // 目录裁判：库里这一列今天不是库自己发号的那一型。本层 Columns() 交的那四颗字段没有这一格，
        // 所以按家各问一次 —— 问不出的家宁可抛，也不给"没问"编一个"没有"。
        Assert.False(TheStoreHandsOutThisKeyItself());

        var tag = "quicksql-appkey-" + Guid.NewGuid().ToString("N")[..8];

        // 两颗相邻的雪花号：旧那一族契约下第二行会撞 pkey（两行都取默认值 0）。
        var first = new AppKeyRow { Id = 7_642_523_574_469L, Tag = tag };
        var second = new AppKeyRow { Id = 7_642_523_574_470L, Tag = tag };

        var written = await orm.InsertRange([first, second]).ExecuteAffrowsAsync(ct);

        Assert.Equal(2, written);

        // 裁判是裸 ADO：库里此刻有这两行，键逐字对得上，而 0 那一行根本不存在。
        var rows = Database.Rows(
            $"SELECT {Quote(nameof(AppKeyRow.Id))}, {Quote(nameof(AppKeyRow.Tag))} FROM {Table} WHERE {Quote(nameof(AppKeyRow.Tag))} = @tag ORDER BY {Quote(nameof(AppKeyRow.Id))}",
            ("@tag", tag));

        Assert.Equal(["7642523574469", "7642523574470"], rows.Select(r => r[nameof(AppKeyRow.Id)]).ToArray());

        var readBack = await orm.Select<AppKeyRow>().Where(r => r.Tag == tag).ToListAsync(ct);

        Assert.Equal([7_642_523_574_469L, 7_642_523_574_470L], readBack.Select(r => r.Id).ToArray());
    }

    /// <summary>"库里这一列是不是库自己发号"——按家问一次目录。<b>问不出的家当场抛</b>：
    /// 这一句是本用例的主断言，拿"判不了"换"没有"就是给缺陷发通行证。</summary>
    static bool TheStoreHandsOutThisKeyItself()
        => Database.Family switch
        {
            // 本层把 DBNull 打成 <c>"NULL"</c> 这四个字（不是 <c>null</c>、也不是空串），而 PG 的目录
            // 对非自增列交的是空串 —— 两种"没有"都得读成"库不发号"，否则这一判据在换一家时恒真。
            Database.Target.PostgreSql => Database.Rows(
                "SELECT identity_generation FROM information_schema.columns " +
                "WHERE table_name = @table AND column_name = @column",
                ("@table", TableName), ("@column", nameof(AppKeyRow.Id)))
                .Single()["identity_generation"] is { } generation
                && generation is not ("" or "NULL" or "NONE"),

            Database.Target.Sqlite => Database.Rows(
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name", ("@name", TableName))
                .Single()["sql"].Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase),

            Database.Target.SqlServer => Database.Rows("SELECT COLUMNPROPERTY(OBJECT_ID(@table), @column, 'IsIdentity') AS is_identity",
                ("@table", TableName), ("@column", nameof(AppKeyRow.Id)))
                .Single()["is_identity"] == "1",

            // MySQL 走的是 information_schema 那颗 EXTRA 位：库发号那一列写的是 auto_increment，
            // 人发号那一列是空串（DEFAULT 0 的 bigint）—— 空串与 NULL 两种"没有"照样都读成"不发号"。
            // 取值那颗带 UPPER 并钉了别名：回包列名在这家按查询原样还，而本层 Rows 的键字典在这一家
            // 是 Ordinal（不折大小写），与其赌目录里那颗名字的拼法，不如让别名自己保证。
            Database.Target.MySql => Database.Rows(
                "SELECT UPPER(extra) AS ExtraFlag FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column",
                ("@table", TableName), ("@column", nameof(AppKeyRow.Id)))
                .Single()["ExtraFlag"] is { } flag && flag.Contains("AUTO_INCREMENT", StringComparison.Ordinal),

            // 虚谷的目录里"库发号"就是 ALL_COLUMNS.IS_SERIAL 那颗布尔位（[实测·M2-03]，布尔取值形）。
            // join 形状与 XuguSchemaReader.ProbeSql 同源——三 张 表按 TABLE_ID/SCHEMA_ID 挂接，不 join 连表名都问不到；
            // 目录里存的是折叠后的大写（与 Fold 同一条纪律），所以表名、列名都要自己折成大写给进参数。
            // 模式绑 NULL 走 NVL 的右支 = 当前会话用户（[实测·M2-04]），与建侧"模式为空就不限定"同一颗默认。
            Database.Target.Xugu => Database.Rows(
                "SELECT c.is_serial FROM all_columns c, all_tables t, all_schemas s " +
                "WHERE t.table_name = :tbl AND c.table_id = t.table_id AND c.col_name = :col " +
                "AND s.schema_id = t.schema_id AND s.schema_name = NVL(:sch, UPPER(USER))",
                ("@tbl", TableName.ToUpperInvariant()), ("@col", nameof(AppKeyRow.Id).ToUpperInvariant()), ("@sch", null))
                .Single()["is_serial"] is "True" or "1",

            _ => throw new InvalidOperationException($"目录问句还没有 {Database.Family} 这一家：宁可红，不要把\"没问\"读成\"没有\"。"),
        };
}
