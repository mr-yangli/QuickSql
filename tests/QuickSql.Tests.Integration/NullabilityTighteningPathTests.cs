using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§9.5 收紧 <c>NOT NULL</c> 那一档的活库半（离线三臂在 <c>NullabilityTighteningTests</c>，
/// 计划子集在 <c>StructureSyncTests</c>）。本册只跑 <c>QUICKSQL_TEST_PROVIDER=sqlite</c> 那一场：
/// 这是七家里唯一一座<em>不需要别人批连接串</em>的靶库，而这一档要证的三件事——宿主那一趟
/// <c>SyncStructureAsync</c> 不抛、目录里的可空性原样、结果里欠着一句点名——在文件库上都能当场读回来。
///
/// <para><b>事故的形状</b>（本轮的证人，真库 PostgreSQL）：消费方在宿主启动跑 Execute 档同步，
/// 同步要把一列从可空收紧成 NOT NULL，而存量行里有 NULL，库以 <c>SqlState 23502</c>（<c>ATRewriteTable</c>）
/// 撞死在 <c>ExecuteDdlAsync</c> 上。修完一颗撞下一颗（<c>Navigation.ConcurrencyStamp</c> ∶
/// <c>Navigation.Icon</c>），所以这是一族而不是一颗列。<b>修法是先问库再发</b>：探针答"有空值"就整句不发、
/// 进 <see cref="SchemaSyncResult.SkippedNullabilityTightening"/>；本册第一案钉的就是这一臂在活库上的读数。</para>
///
/// <para><b>第二案（净表）在本家落不成 NOT NULL，而这不是本用例的松散</b>：SQLite 没有改列的语句
/// （<c>ALTER TABLE … ALTER COLUMN</c> 是语法错，【实测·F4 ∶ 2815】），那一族的等价物是"建新表、拷数据、
/// 换名"，其形状在 <c>SqliteCodeFirst</c> 类注释第三条末写死为<em>本版不建模</em>（§14）。以前这一形会把
/// 启动撞死在发射点的兜底抛上（<c>Alter_Column_Precision</c> 那颗能力位今天有了读者：<c>SchemaSyncer</c>
/// 在计划清单这一格就把本家的改列一句挡下）。所以本案断言的是：<em>不抛</em>、跳过清单为空
/// （探针答"没有空值"，没有东西被挡）、目录里仍可空（收紧在本家发不出去）、而报告里 <c>NotJudged</c>
/// 点着这一列的名——"落不成"是一句要有人看见的话，不是一次静默。净表真落 NOT NULL 的那一臂由离线
/// 孪生册（PG 家的 <c>SET NOT NULL</c> 字面）钉住。</para>
///
/// <para><b>裁判一律问目录</b>，与 <see cref="StructureSyncPathTests"/> 同一条纪律：返回值只能说"我发过/没发过"，
/// 而这一档要的是"库里现在是什么"。<c>Database.Columns</c> 在本家读 <c>pragma_table_info</c>，
/// <c>notnull</c> 那颗折成 <c>is_nullable</c> 的方向由本层那一句负责【实测·3501 ∶ 3603 ∶ 3605】。</para></summary>
public sealed class NullabilityTighteningPathTests
{
    /// <summary>靶实体：<c>Stamp</c> 在 CLR 侧非空（就是 <c>ConcurrencyStamp</c> 那一颗的形状），
    /// 而摆出来的表里它是 <c>NULL</c> 列——收紧那一格在场。<c>Icon</c> 两侧都可空，用来证明探针
    /// 没有将差之外的列也逐列问一遍（它答"库里没有差"，因为压根不发问）。</summary>
    [Table("QuickSqlTightPath")]
    public sealed class TightPath
    {
        public long Id { get; set; }

        [QuickColumn(Length = 32)]
        public string Stamp { get; set; } = null!;

        [QuickColumn(Length = 32)]
        public string? Icon { get; set; }
    }

    const string Table = "\"QuickSqlTightPath\"";

    /// <summary>本用例的 arrange 是裸 SQLite 语法（<c>INTEGER … AUTOINCREMENT</c> 内联键那一形，
    /// 【实测·2802】），换一家就要整段重摆而不是换一颗判据——与 <see cref="StructureSyncPathTests"/>
    /// 的"三家各摆各的"同一格纪律：只跑 sqlite 一场，另几家按名跳过。</summary>
    static void SkipUnlessSqlite()
        => Assert.SkipWhen(Database.Family != Database.Target.Sqlite,
            "本用例的靶表按 SQLite 的裸 DDL 摆（内联自增键那一形没有跨家写法可折），而这一档要量的探针与跳过清单在本家是文件库上当场读得回的；其余家欠的是活库连接串，归另两册。");

    /// <summary>每次运行先把靶表摆回"库里可空、契约要它非空"那一份差：两列都 <c>NULL</c> 形，
    /// 自增键 <c>NOT NULL</c> 形（与契约一致，免得 Id 混进动作清单干扰读数）。</summary>
    static void BuildStore()
    {
        Database.Execute($"DROP TABLE IF EXISTS {Table}");

        Database.Execute($"""
            CREATE TABLE {Table}(
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Stamp" TEXT NULL,
                "Icon" TEXT NULL)
            """);
    }

    /// <summary>只挂 <see cref="TightPath"/> 一册的一台 ORM——理由与 <see cref="StructureSyncPathTests"/> 的
    /// <c>Probe()</c> 逐字同一颗：不用 <see cref="Database.Orm"/>，免得这一趟替夹具的全部实体查岗。</summary>
    static QuickSqlOrm Probe()
    {
        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

        _ = orm.Schemas.Contract<TightPath>();

        return orm;
    }

    static Dictionary<string, string> CatalogColumn(string name)
        => Database.Columns("QuickSqlTightPath").Single(r => r["name"] == Database.CatalogueName(name));

    /// <summary>第一案（事故本尊）：表里有 NULL 行。宿主等价的 <c>SyncStructureAsync</c> 那一趟
    /// <b>不抛</b>（这一句本身就是断言——抛出即红），收紧那一句整条没发，目录里 <c>Stamp</c> 仍可空，
    /// 而结果里点着 <c>QuickSqlTightPath.Stamp</c> 的名并给出路。<b>那一行 NULL 必须原样还在</b>：
    /// 跳过不是搬数据，更不是抹数据。</summary>
    [Fact]
    public async Task A_dirty_store_survives_the_startup_sync_and_reports_the_skip()
    {
        SkipUnlessSqlite();

        Database.Ready();

        BuildStore();

        Database.Execute($"""INSERT INTO {Table} ("Stamp", "Icon") VALUES (NULL, NULL)""");

        var result = await Probe().SyncStructureAsync(TestContext.Current.CancellationToken);

        // 动作清单里没有任何一句 ALTER——被挡的是整句（收紧与改形状在同一句里的形状见跳过文案）。
        Assert.Empty(result.AlteredColumns);
        Assert.Empty(result.DdlStatements);
        Assert.DoesNotContain(result.DdlStatements, s => s.Contains("Stamp", StringComparison.Ordinal));

        // 跳过那一格：表.列点名 + 出路（回填完下一趟自己收紧）。
        Assert.Single(result.SkippedNullabilityTightening,
            s => s.Contains("QuickSqlTightPath.Stamp", StringComparison.Ordinal) &&
                 s.Contains("回填", StringComparison.Ordinal));

        // 裁判问目录：这一列现在仍然可空，而不是"发了但没改成"。
        Assert.Equal("1", CatalogColumn("Stamp")["is_nullable"]);

        // 那一行 NULL 还在：跳过顺带没碰数据。
        Assert.Equal("1", Database.Rows($"""SELECT COUNT(*) AS "c" FROM {Table} WHERE "Stamp" IS NULL""")[0]["c"]);

        // 第二趟同一份读数：脏表两趟都挡（它是给人看的话单，不是发一次就消失的动作），而且依旧不抛。
        var second = await Probe().SyncStructureAsync(TestContext.Current.CancellationToken);

        Assert.Single(second.SkippedNullabilityTightening, s => s.Contains("QuickSqlTightPath.Stamp", StringComparison.Ordinal));
        Assert.Empty(second.DdlStatements);
    }

    /// <summary>第二案（净表孪生）：同一份形状差，库里<em>没有</em> NULL 行。探针答"没有"⇒ 跳过清单
    /// <b>必须为空</b>（这一臂不许退化成"为了保险都不发"）。而收紧那一句在本家今天落不下去：
    /// SQLite 没有改列语句、重建一族 §14 未建模——以前这一发以兜底抛收尾（宿主起不来，与 23502 同族），
    /// 现在它被 <c>Alter_Column_Precision</c> 能力位挡在计划清单这一格，报进"判不了"并点名。
    /// 目录里仍可空，那一行数据原样。</summary>
    [Fact]
    public async Task A_clean_store_is_not_reported_skipped_and_the_startup_still_survives()
    {
        SkipUnlessSqlite();

        Database.Ready();

        BuildStore();

        Database.Execute($"""INSERT INTO {Table} ("Stamp", "Icon") VALUES ('干净', '干净')""");

        var result = await Probe().SyncStructureAsync(TestContext.Current.CancellationToken);

        // 探针答"没有空值"：没有东西是被 NULL 挡下的，跳过那一格必须空。
        Assert.Empty(result.SkippedNullabilityTightening);

        // 而本家这一句发不出去（没有改列语句，重建未建模）：不抛、不发，报告里点名。
        Assert.Empty(result.AlteredColumns);
        Assert.Contains(result.NotJudged, n => n.Contains("QuickSqlTightPath.Stamp", StringComparison.Ordinal));

        // 裁判问目录：这一列在这一家今天仍是可空——"落不成"要看得见，不是静默成功。
        Assert.Equal("1", CatalogColumn("Stamp")["is_nullable"]);

        // 那一行没被碰。
        Assert.Equal("干净", Database.Rows($"""SELECT "Stamp" AS "s" FROM {Table}""")[0]["s"]);
    }
}
