using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>「同一张表这一轮既插又删」把删除那一段提前（<c>ChangeTracker.FlushDueAsync</c> 的触发点）。
///
/// <para><b>事故的形状</b>（本轮的证人，真库 PostgreSQL）：单据明细的「行号」归应用算，
/// 库里那条唯一索引管的就是 <c>(单据号, 行号)</c> 这一对。用户在编辑界面上把第 3 行换成另一种物资，
/// 应用的做法是摘掉旧行、插一行新号同样是 3 的（行号是位置，不是身份）。会话按 §8.2 的三档排出来的是
/// <c>INSERT</c> 在前、<c>DELETE</c> 在后，于是那句 INSERT 撞在<em>本轮自己就要删掉</em>的那一行上：
/// <c>23505 duplicate key ... ux_applyitem_applyid_lineno</c>。逐条看每句都对，串起来是错的。</para>
///
/// <para><b>为什么这一半只能在库里看</b>：离线那册判的是状态机（谁 Added、谁 Deleted），
/// 而「库里那条唯一索引挡没挡」要的是一台真会把约束当回事的库。这里连唯一索引本身也是库建的
/// （<c>SyncStructureAsync</c> 从契约上的 <c>[Index(IsUnique = true)]</c> 产出 <c>CREATE UNIQUE INDEX</c>），
/// 所以本案审的是「发句子的先后」，不是「约束在不在」。</para>
///
/// <para><b>裁判一律问裸 ADO</b>，与 <see cref="ApplicationKeyPathTests"/> 同一条纪律：写的一侧是被审的会话，
/// 读的一侧不能再是它。而且本案的读法要判<em>条数</em>：先删后插如果两边都跑了，
/// 表里应当只剩新的那一行；少跑一句删除就是同一行号两行并存。</para></summary>
public sealed class RowSwapOrderingPathTests
{
    /// <summary>靶实体：一颗归客户端给的主键（<c>Guid</c>，免掉自增那一档的形状差）+ 一条业务唯一索引。
    /// <c>LineNo</c> 就是明细的行号，<c>Board</c> 是"同一张单"的那个键。</summary>
    [Table("QuickSqlRowSwapPath")]
    [Index(nameof(Board), nameof(LineNo), IsUnique = true)]
    public sealed class SwapRow
    {
        public Guid Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Board { get; set; }

        public int LineNo { get; set; }

        [QuickColumn(Length = 32)]
        public string? Label { get; set; }
    }

    const string TableName = "QuickSqlRowSwapPath";

    static string Quote(string identifier) => Database.Provider.CodeFirst.Identifiers.Quote(identifier);

    static string Table => Quote(TableName);

    static CancellationToken ct => TestContext.Current.CancellationToken;

    /// <summary>把靶表摆回干净形状，并<em>当场验一次那条唯一索引真的活着</em>。
    ///
    /// <para>为什么不问目录（<c>Database.IndexNames</c>）而是撞一次：本案的裁判是「库里会不会挡」，
    /// 而目录里有没有那一行判的是"名字建出来了"。第七家的 <c>pragma_index_list</c> 在这一层从没被
    /// 用例走过（<c>DdlTests</c> 那一句走的是 T-SQL 与虚谷两支），拿它当"索引在不在"的判据，
    /// 一旦那一颗 pragma 收不了参数就会得到一个<em>空名单 + 空表</em>的假绿 —— 本案整个用例集就白跑。
    /// 撞一次是四家通用的问法：同键第二行发进去，库挡就是活的，库不挡这条用例当场红。</para>
    ///
    /// <para>探针行落在自己的那块板上（与用例的板号分开），事后按板号清掉，不碰库里别人的数据。</para></summary>
    static async Task<QuickSqlOrm> RebuildStore(CancellationToken token)
    {
        Database.Ready();
        Database.Execute($"DROP TABLE IF EXISTS {Table}");

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

        // 先问一次契约，同步那一趟才看得见这张表：反射注册表按"已经解析过的类型"清单走，
        // 而本用例的实体是就地声明的，没人替它问过一句（与 ApplicationKeyPathTests 同一颗顺序）。
        orm.Schemas.Contract<SwapRow>();

        await orm.SyncStructureAsync(token);

        var probe = "quicksql-probe-" + Guid.NewGuid().ToString("N")[..8];

        await orm.Insert(new SwapRow { Id = Guid.NewGuid(), Board = probe, LineNo = 1, Label = "甲" })
            .ExecuteAffrowsAsync(token);

        var refused = await Record.ExceptionAsync(async () =>
            await orm.Insert(new SwapRow { Id = Guid.NewGuid(), Board = probe, LineNo = 1, Label = "乙" })
                .ExecuteAffrowsAsync(token));

        Database.Execute($"DELETE FROM {Table} WHERE {Quote(nameof(SwapRow.Board))} = @board", ("@board", probe));

        Assert.NotNull(refused);

        return orm;
    }

    [Fact]
    public async Task A_row_removed_and_another_one_taken_its_business_key_in_the_same_save_flushes_the_delete_first()
    {
        var orm = await RebuildStore(ct);

        var board = "quicksql-swap-" + Guid.NewGuid().ToString("N")[..8];

        // 种子走不跟踪的那条链（与 TrackingPathTests 同一条纪律）：别让被审的写口先把自己的现场铺好。
        var old = new SwapRow { Id = Guid.NewGuid(), Board = board, LineNo = 1, Label = "甲" };
        var kept = new SwapRow { Id = Guid.NewGuid(), Board = board, LineNo = 2, Label = "乙" };

        await orm.InsertRange([old, kept]).ExecuteAffrowsAsync(ct);

        Guid fresh;

        await using (var db = new DbContext(orm))
        {
            var loaded = await db.Set<SwapRow>().Where(r => r.Board == board).ToListAsync(ct);

            Assert.Equal(2, loaded.Count);

            // 「把第 1 行换成另一种物资」= 摘掉旧的那一只、插一只行号仍然是 1 的新实例（新主键）。
            db.Remove(loaded.Single(r => r.LineNo == 1));

            fresh = Guid.NewGuid();
            db.Add(new SwapRow { Id = fresh, Board = board, LineNo = 1, Label = "丙" });

            // 这一句在改动之前是 23505：那句 INSERT 排在 DELETE 前面，撞的是本轮自己就要删掉的那一行。
            await db.SaveChangesAsync(ct);
        }

        // 裁判：行号 1 只剩一行、而且是新的那一串；行号 2 那一只一字未动。
        var rows = Database.Rows(
            $"SELECT {Quote(nameof(SwapRow.LineNo))}, {Quote(nameof(SwapRow.Label))}, {Quote(nameof(SwapRow.Id))} " +
            $"FROM {Table} WHERE {Quote(nameof(SwapRow.Board))} = @board ORDER BY {Quote(nameof(SwapRow.LineNo))}",
            ("@board", board));

        Assert.Equal(["1", "2"], rows.Select(r => r[nameof(SwapRow.LineNo)]).ToArray());
        Assert.Equal(["丙", "乙"], rows.Select(r => r[nameof(SwapRow.Label)]).ToArray());
        Assert.Equal(fresh.ToString(), rows[0][nameof(SwapRow.Id)], ignoreCase: true);
    }

    [Fact]
    public async Task Swapping_a_line_and_adding_another_one_in_the_same_save_leaves_the_whole_list_consistent()
    {
        var orm = await RebuildStore(ct);

        var board = "quicksql-swap-" + Guid.NewGuid().ToString("N")[..8];

        await orm.InsertRange(
        [
            new SwapRow { Id = Guid.NewGuid(), Board = board, LineNo = 1, Label = "甲" },
            new SwapRow { Id = Guid.NewGuid(), Board = board, LineNo = 2, Label = "乙" },
        ]).ExecuteAffrowsAsync(ct);

        await using (var db = new DbContext(orm))
        {
            var loaded = await db.Set<SwapRow>().Where(r => r.Board == board).ToListAsync(ct);

            var second = loaded.Single(r => r.LineNo == 2);

            // 同一轮里三样事凑齐：摘掉第 2 行、用它的行号插一只新的、再往表尾加一行。
            db.Remove(second);
            db.Add(new SwapRow { Id = Guid.NewGuid(), Board = board, LineNo = 2, Label = "丙" });
            db.Add(new SwapRow { Id = Guid.NewGuid(), Board = board, LineNo = 3, Label = "丁" });

            await db.SaveChangesAsync(ct);
        }

        var rows = Database.Rows(
            $"SELECT {Quote(nameof(SwapRow.LineNo))}, {Quote(nameof(SwapRow.Label))} FROM {Table} " +
            $"WHERE {Quote(nameof(SwapRow.Board))} = @board ORDER BY {Quote(nameof(SwapRow.LineNo))}",
            ("@board", board));

        Assert.Equal(["1", "2", "3"], rows.Select(r => r[nameof(SwapRow.LineNo)]).ToArray());
        Assert.Equal(["甲", "丙", "丁"], rows.Select(r => r[nameof(SwapRow.Label)]).ToArray());
    }
}
