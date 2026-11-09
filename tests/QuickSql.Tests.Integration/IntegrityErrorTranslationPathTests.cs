using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>完整性违例在真库上被翻成哪一颗（§8.4 那一格的第二层验收：分类漏斗过一遍真驱动）。
///
/// <para><b>离线那册判不了的事</b>：<c>IntegrityClassificationTests</c> 钉的是"给它一个 23505，翻成唯一键"，
/// 用的是桩异常。库会不会真报这个码、码挂在 <c>DbException.SqlState</c> 上还是只挂在驱动专有成员上、
/// 收口点（<c>AdoExecutor.Failed</c>）真的把包装接在了每一条出口上——这三件都要一台会答的库。</para>
///
/// <para><b><c>ConstraintName</c> 拿不拿得到是这条用例的一半</b>：§8.4 记着 PG 把约束名放在
/// <c>DbException.Data</c> 里而不是任何基类属性上，而"消费者能不能凭这一格回到契约查列"就挂在这格上。
/// 名字具体叫什么（<c>ix_…</c> 还是 <c>ux_…</c>）不在判据里，本用例只要求它非空。</para>
///
/// <para><b>反向那一条同样必测</b>：不是完整性违例的错误（这里用"表不存在"）必须<b>原样</b>出洞、不包装。
/// 少了这一条，"判不出就交回驱动原话"这一格从来没被真驱动验过，而误包装的代价是把一句 DDL 错误
/// 说成"重复提交"。</para></summary>
public sealed class IntegrityErrorTranslationPathTests
{
    [Table("QuickSqlIntegrityTranslatePath")]
    [Index(nameof(Board), nameof(LineNo), IsUnique = true)]
    public sealed class TranslateRow
    {
        public Guid Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Board { get; set; }

        public int LineNo { get; set; }
    }

    const string TableName = "QuickSqlIntegrityTranslatePath";

    static string Quote(string identifier) => Database.Provider.CodeFirst.Identifiers.Quote(identifier);

    static string Table => Quote(TableName);

    static CancellationToken ct => TestContext.Current.CancellationToken;

    /// <summary>分类闸门：<see cref="IDbProvider.ClassifyIntegrityError"/> 今天只有 SQL Server ∶ PG 系 ∶
    /// MySQL 系 ∶ SQLite 四族认领，虚谷（以及将来任何走接口默认 null 的家）撞唯一键交回的是驱动原话，
    /// typed-exception 断言在它上面必红。skip 消息把欠账写明——补齐一家就把这一家从排除清单里摘掉。</summary>
    static void AssumeClassified() =>
        Assert.SkipUnless(Database.Family is not Database.Target.Xugu,
            "完整性异常分类（ClassifyIntegrityError）当前只在 sqlserver/postgresql/mysql/sqlite 四族实现；" +
            "虚谷及未实现的各家撞唯一键交回驱动原话，typed-exception 断言不适用——补齐实现即摘这条 skip。");

    /// <summary>自增主键的那一形。主键整型默认自增（EntityContractFactory 的约定档），
    /// 插入因此走 <b>OUTPUT/RETURNING</b> 的带结果集写出口（WriteWithResultAsync）——
    /// 而约束违例恰恰在 ExecuteReader 当场抛，是分类漏斗最容易漏的那条腿（v0.39 修的那格）。
    /// 上面 <see cref="TranslateRow"/> 用 Guid 主键、无生成列，正好绕开那条路。</summary>
    [Table("QuickSqlIntegrityTranslatePathId")]
    [Index(nameof(Board), nameof(LineNo), IsUnique = true)]
    public sealed class TranslateIdentityRow
    {
        public int Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Board { get; set; }

        public int LineNo { get; set; }
    }

    /// <summary>把靶表摆出来。唯一索引由 <c>SyncStructureAsync</c> 从契约上的
    /// <c>[Index(IsUnique = true)]</c> 建，所以"索引真是唯一的"这一格不用另外手写 DDL 去铺——
    /// 而它下面那条反向用例正是靠这张表被 drop 掉来制造一个<em>非完整性</em>的错误。</summary>
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

        // 先问一次契约，同步那一趟才看得见这张表（与 RowSwapOrderingPathTests 同一颗顺序）
        orm.Schemas.Contract<TranslateRow>();

        await orm.SyncStructureAsync(token);

        return orm;
    }

    [Fact]
    public async Task A_real_unique_violation_surfaces_as_a_typed_exception()
    {
        AssumeClassified();

        var orm = await RebuildStore(ct);

        var board = "quicksql-translate-" + Guid.NewGuid().ToString("N")[..8];

        await orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 3 }).ExecuteAffrowsAsync(ct);

        var raised = Assert.Throws<UniqueConstraintException>(
            () => orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 3 })
                .ExecuteAffrowsAsync(ct).GetAwaiter().GetResult());

        // 整族落在同一颗底座下：消费者写"这一族都记一笔再抛"时只列一颗名字
        Assert.IsAssignableFrom<DbUpdateException>(raised);

        // 驱动原话没被换掉，只是被包住
        Assert.IsAssignableFrom<System.Data.Common.DbException>(raised.InnerException);

        // 最终 SQL 拿得到（进日志用）。约束名不在判据里：各家给不给形状不同（PG 给、SQL Server 与
        // MySQL 不给、SQLite 连档都没有），那格由离线册的 The_constraint_name_travels_with_the_violation 钉。
        Assert.Contains("insert", raised.Sql, StringComparison.OrdinalIgnoreCase);

        Database.Execute($"DELETE FROM {Table} WHERE {Quote(nameof(TranslateRow.Board))} = @board", ("@board", board));
    }

    /// <summary>主键重复走的是另一颗号（SQL Server 2627 / PG 仍是 23505），这一条把 2627 从
    /// "厂商文档的号"转成"本机撞过的号"。它与业务唯一键翻成同一颗 <see cref="UniqueConstraintException"/>：
    /// 处置相同（这一行已经在了），所以不为一颗号再开一种类型。</summary>
    [Fact]
    public async Task A_duplicate_primary_key_is_the_same_kind_as_a_duplicate_business_key()
    {
        AssumeClassified();

        var orm = await RebuildStore(ct);

        var id = Guid.NewGuid();

        await orm.Insert(new TranslateRow { Id = id, Board = "quicksql-pk-a", LineNo = 1 }).ExecuteAffrowsAsync(ct);

        Assert.Throws<UniqueConstraintException>(
            () => orm.Insert(new TranslateRow { Id = id, Board = "quicksql-pk-b", LineNo = 2 })
                .ExecuteAffrowsAsync(ct).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task An_error_that_is_not_an_integrity_violation_is_handed_back_untouched()
    {
        AssumeClassified();

        var orm = await RebuildStore(ct);

        // 把靶表拆掉：下一句撞的是"表不存在"（PG 的 42P01），它不属于完整性违例的任何一档
        Database.Execute($"DROP TABLE {Table}");

        var raised = await Assert.ThrowsAnyAsync<Exception>(
            () => orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = "x", LineNo = 1 })
                .ExecuteAffrowsAsync(ct));

        // 判的是"没有被翻成完整性异常"，不是"抛了哪一颗"——具体类型归驱动，本家不替它命名。
        Assert.IsNotAssignableFrom<DbUpdateException>(raised);
    }

    /// <summary>自增主键的插入走 <b>OUTPUT/RETURNING</b> 的带结果集写出口，约束违例在
    /// <c>ExecuteReader</c> 当场抛——这是分类漏斗曾经漏掉的那条腿（v0.39 修的那格）：
    /// 修之前消费者在 SQL Server 上拿到的是裸 <c>SqlException</c> 而不是
    /// <see cref="UniqueConstraintException"/>。与第一条用例同一语义、换一条出口。</summary>
    [Fact]
    public async Task An_identity_insert_that_violates_uniqueness_is_classified_on_the_readback_path()
    {
        AssumeClassified();

        Database.Ready();
        Database.Execute($"DROP TABLE IF EXISTS {Quote("QuickSqlIntegrityTranslatePathId")}");

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

        orm.Schemas.Contract<TranslateIdentityRow>();

        await orm.SyncStructureAsync(ct);

        var board = "quicksql-translate-id-" + Guid.NewGuid().ToString("N")[..8];

        // 自增主键不填值：回填要走 OUTPUT/RETURNING，违例（如果有）就发生在 ExecuteReader 当场。
        await orm.Insert(new TranslateIdentityRow { Board = board, LineNo = 7 }).ExecuteAffrowsAsync(ct);

        var raised = Assert.Throws<UniqueConstraintException>(
            () => orm.Insert(new TranslateIdentityRow { Board = board, LineNo = 7 })
                .ExecuteAffrowsAsync(ct).GetAwaiter().GetResult());

        Assert.IsAssignableFrom<System.Data.Common.DbException>(raised.InnerException);
        Assert.Contains("insert", raised.Sql, StringComparison.OrdinalIgnoreCase);

        Database.Execute($"DELETE FROM {Quote("QuickSqlIntegrityTranslatePathId")} WHERE {Quote(nameof(TranslateIdentityRow.Board))} = @board", ("@board", board));
    }

    // ── Columns 回查（§16.22 ② 取向 (a)，2026-11-01 落地） ─────────────────

    /// <summary>约束名回契约查唯一索引的列集合：驱动交回约束名（今天只有 PG 系，名字活在
    /// <c>Data["ConstraintName"]</c> 里）、名字与契约上的唯一索引对得上 —— 列集合就跟着走。
    /// 断言不写死列名与索引名：取的是契约本身那一份（<c>IndexContract.ColumnNames</c> 与名字），
    /// 命中的判据是"异常上那一份 == 契约上那一份"。</summary>
    [Fact]
    public async Task Columns_are_resolved_from_the_contract_when_the_driver_names_the_constraint()
    {
        AssumeClassified();

        Assert.SkipUnless(Database.Family == Database.Target.PostgreSql,
            "约束名今天只有 PG 系交回（Data[\"ConstraintName\"]）；SQL Server/MySQL 的号在结构化字段之外、" +
            "SQLite 连名字都不给——那三家的 Columns 走下一颗反向用例。");

        var orm = await RebuildStore(ct);

        var board = "quicksql-columns-" + Guid.NewGuid().ToString("N")[..8];

        await orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 5 }).ExecuteAffrowsAsync(ct);

        var unique = orm.Schemas.Contract<TranslateRow>().Indexes.Single(i => i.Kind == IndexKind.Unique);

        var raised = Assert.Throws<UniqueConstraintException>(
            () => orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 5 })
                .ExecuteAffrowsAsync(ct).GetAwaiter().GetResult());

        Assert.Equal(unique.Name, raised.ConstraintName);
        Assert.Equal(unique.ColumnNames, raised.Columns);

        Database.Execute($"DELETE FROM {Table} WHERE {Quote(nameof(TranslateRow.Board))} = @board", ("@board", board));
    }

    /// <summary>反向第一格：驱动交不出约束名的家（SQL Server/MySQL 的号在结构化字段之外、
    /// SQLite 连名字都不给），<see cref="DbUpdateException.Columns"/> 恒 <c>null</c> ——
    /// 没有名字就没有回查的钥匙，宁可提示粗一点也不猜。</summary>
    [Fact]
    public async Task Columns_stay_null_when_the_driver_gives_no_constraint_name()
    {
        AssumeClassified();

        Assert.SkipWhen(Database.Family == Database.Target.PostgreSql,
            "PG 系交回约束名，命中那一半由上一颗用例判，这一颗在 PG 上没有意义。");

        var orm = await RebuildStore(ct);

        var board = "quicksql-noname-" + Guid.NewGuid().ToString("N")[..8];

        await orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 6 }).ExecuteAffrowsAsync(ct);

        var raised = Assert.Throws<UniqueConstraintException>(
            () => orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 6 })
                .ExecuteAffrowsAsync(ct).GetAwaiter().GetResult());

        Assert.Null(raised.ConstraintName);
        Assert.Null(raised.Columns);

        Database.Execute($"DELETE FROM {Table} WHERE {Quote(nameof(TranslateRow.Board))} = @board", ("@board", board));
    }

    /// <summary>反向第二格：约束名有了、但契约里没有这只对象（手工 DDL 自起名的唯一索引）——
    /// 查不到给 <c>null</c>，<b>既不抛也不误报</b>：失败方向安全（提示变粗），
    /// 不把半句人话当列名摆上 UI。</summary>
    [Fact]
    public async Task A_constraint_outside_the_contract_leaves_columns_null()
    {
        AssumeClassified();

        Assert.SkipUnless(Database.Family == Database.Target.PostgreSql,
            "要在真实现场拿到约束名，今天只有 PG 系一档。");

        var orm = await RebuildStore(ct);

        Database.Execute($"CREATE UNIQUE INDEX {Quote("ux_out_of_band")} ON {Table} ({Quote(nameof(TranslateRow.Board))})");

        var board = "quicksql-oob-" + Guid.NewGuid().ToString("N")[..8];

        await orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 1 }).ExecuteAffrowsAsync(ct);

        var raised = Assert.Throws<UniqueConstraintException>(
            () => orm.Insert(new TranslateRow { Id = Guid.NewGuid(), Board = board, LineNo = 2 })
                .ExecuteAffrowsAsync(ct).GetAwaiter().GetResult());

        // 撞的是契约外那只对象：名字逐字是它自己的（手工 DDL 起的那颗），契约里没有这一格可对，
        // 名字交得回来、列查不出 —— 两格各答各的。
        Assert.Equal("ux_out_of_band", raised.ConstraintName);
        Assert.Null(raised.Columns);
    }
}
