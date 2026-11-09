using System.Data.Common;
using QuickSql.Providers;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>完整性违例的分类码表（§8.4 那一格的第一层验收：逐码离线钉）。</summary>
///
/// <para><b>为什么这一格能离线证</b>：判据只读 <see cref="DbException"/> 这颗 ADO.NET 基类面——
/// <c>SqlState</c> 是它的虚属性，约束名在 <c>Data["ConstraintName"]</c>。这两样都可以用一只桩异常摆出来，
/// 所以"23505 翻成哪一颗"这件事不需要活库，也不该等活库（真库那一档在 §13 的集成册里判另一件事：
/// 库真的会报这个码）。</para>
///
/// <para><b>码的来路</b>：2026-10-23 在一台可达的 PG 18.6 上实测——<c>pg_temp</c> 一张夹具带齐
/// 唯一索引 / NOT NULL / CHECK / FK，五种违例各撞一次，取到的码与 <c>Data</c> 键记在 §8.4 那张表下面。
/// 这里钉的是"翻出来的形状"，不是"库会不会报这个码"。</para>
public sealed class IntegrityClassificationTests
{
    /// <summary>只带 SqlState 与 Data 的一只桩异常。<b>不给它任何驱动类型</b>正是这一格的要点：
    /// provider 编译期不认识 Npgsql（§1.2 原则 3），能碰的面就只有基类这几个。</summary>
    sealed class StubDbException(string? sqlState, string? constraintName) : DbException
    {
        public override string? SqlState { get; } = sqlState;

        public string? ConstraintName => constraintName;
    }

    static DbException Error(string? sqlState, string? constraintName = null)
    {
        var error = new StubDbException(sqlState, constraintName);

        if (constraintName is not null)
            error.Data["ConstraintName"] = constraintName;

        return error;
    }

    static readonly IDbProvider Pg = PostgreSql.Provider(SqlServer.NoDriverFactory.Instance);

    [Theory]
    [InlineData("23505", IntegrityKind.Unique)]
    [InlineData("23502", IntegrityKind.NotNull)]
    [InlineData("23503", IntegrityKind.ForeignKey)]
    [InlineData("23001", IntegrityKind.ForeignKey)]
    [InlineData("23514", IntegrityKind.Check)]
    public void PG_codes_map_to_their_own_kind(string sqlState, IntegrityKind expected)
        => Assert.Equal(expected, Pg.ClassifyIntegrityError(Error(sqlState))!.Kind);

    [Fact]
    public void The_constraint_name_travels_with_the_violation()
    {
        var violation = Pg.ClassifyIntegrityError(Error("23505", "ix_probe_k_no"));

        Assert.Equal(IntegrityKind.Unique, violation!.Kind);
        Assert.Equal("ix_probe_k_no", violation.ConstraintName);
    }

    /// <summary><b>非空那一档库不给约束名</b>（实测），而这一条要钉的是"拿不到就是 null"而不是
    /// "抛出一条更响的错误"——判不出键名只该让提示变粗，不该让整件事变成异常。</summary>
    [Fact]
    public void Not_null_carries_no_constraint_name_because_the_database_has_none_to_give()
        => Assert.Null(Pg.ClassifyIntegrityError(Error("23502"))!.ConstraintName);

    /// <summary>判不出的一律交回 <c>null</c>：语法错、权限错、连不上……<b>都不是完整性违例</b>。
    /// 这一条是 §8.4 那句"误分类比不分类坏得多"的落地形式——把 42601 翻成唯一键冲突，
    /// 消费者就会把一句 SQL 语法错误回成"您已提交过"。</summary>
    [Theory]
    [InlineData("42601")]
    [InlineData("23514x")]
    [InlineData("07006")]
    [InlineData(null)]
    public void Anything_else_is_not_classified(string? sqlState)
        => Assert.Null(Pg.ClassifyIntegrityError(Error(sqlState)));

    /// <summary>没覆写这一格的方言走接口的默认实现，交回 <c>null</c>（§8.4：首批四家，其余十几家
    /// 的表现与今天一模一样）。这一条同时是"默认实现真的在"的证据——少了它，未覆写的 provider
    /// 会在编译期就缺成员而不是安静地不分类。</summary>
    [Fact]
    public void A_dialect_without_a_map_yet_leaves_the_driver_exception_alone()
    {
        IDbProvider duckDb = DuckDb.Provider(SqlServer.NoDriverFactory.Instance);

        Assert.Null(duckDb.ClassifyIntegrityError(Error("23505", "IX_probe")));
    }

    /// <summary>SQL Server 那一族认号也认来源：<c>HelpLink.EvtID</c> 是 ADO.NET 给"帮助链接"用的槽位，
    /// 别家驱动照样可能填。<c>EvtSrc</c> 不是装饰——不点名 MSSQLServer 就不认领，认错了家会把别家的一句
    /// 普通错误翻成"唯一键冲突"。</summary>
    [Fact]
    public void SqlServer_does_not_claim_a_number_that_does_not_come_from_itself()
    {
        var foreign = new StubDbException(null, null);
        foreign.Data["HelpLink.EvtID"] = "2601";   // 没有 EvtSrc

        Assert.Null(SqlServer.Provider(SqlServer.NoDriverFactory.Instance)
            .ClassifyIntegrityError(foreign));
    }

    /// <summary>整族落在同一颗底座下（§8.4 那个基类存在的全部理由：消费者写"这一族都记一笔再抛"时
    /// 只列一颗名字）。并发那颗今天也在这颗底座下，所以这里把它一起验。</summary>
    [Fact]
    public void Every_integrity_exception_shares_the_update_base_and_keeps_the_driver_error_inside()
    {
        var driver = Error("23505", "ix_probe_k_no");

        var raised = Assert.IsType<UniqueConstraintException>(
            new UniqueConstraintException("唯一键冲突。", "insert into probe_k values (11, 5, 'a', 1)", "ix_probe_k_no", driver));

        Assert.IsAssignableFrom<DbUpdateException>(raised);
        Assert.Same(driver, raised.InnerException);
        Assert.Equal("ix_probe_k_no", raised.ConstraintName);
    }

    // ── MySQL：SqlState 只到 23000 那一整档，分档看厂商号，而号在基类字典里 ──────────────────

    static DbException MySqlError(string serverErrorCode)
    {
        var error = new StubDbException("23000", null);
        error.Data["Server Error Code"] = serverErrorCode;

        return error;
    }

    [Theory]
    [InlineData("1062", IntegrityKind.Unique)]      // 实测撞过的那一颗
    [InlineData("1048", IntegrityKind.NotNull)]
    [InlineData("1452", IntegrityKind.ForeignKey)]
    [InlineData("3819", IntegrityKind.Check)]
    public void MySql_splits_the_23000_bucket_on_the_server_code(string code, IntegrityKind expected)
    {
        IDbProvider mySql = MySql.Provider(SqlServer.NoDriverFactory.Instance);

        Assert.Equal(expected, mySql.ClassifyIntegrityError(MySqlError(code))!.Kind);
    }

    /// <summary>号那一格要在 <c>SqlState == 23000</c> 这道门后面才作数：那根字典槽位放的是厂商号，
    /// 别家驱动照样可能填同名键，认了号不认档等于替别家猜。</summary>
    [Fact]
    public void MySql_does_not_claim_a_server_code_outside_the_integrity_bucket()
    {
        var error = new StubDbException("42000", null);   // 语法档，不是完整性档
        error.Data["Server Error Code"] = "1062";

        Assert.Null(MySql.Provider(SqlServer.NoDriverFactory.Instance).ClassifyIntegrityError(error));
    }

    // ── SQLite：基类面上什么都没有，唯一能读的是那句固定文案（见 §8.4 本节末）────────────────

    [Theory]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: T.Board, T.LineNo'.", true)]
    [InlineData("SQLite Error 1: 'no such table: T'.", false)]
    [InlineData("SQLite Error 19: 'FOREIGN KEY constraint failed'", false)]   // 本台没撞过，就不认
    public void Sqlite_recognises_only_the_constraint_message_it_has_actually_hit(string message, bool isUnique)
    {
        var raised = new MessageOnlyDbException(message);

        var violation = Sqlite.Provider(SqlServer.NoDriverFactory.Instance).ClassifyIntegrityError(raised);

        Assert.Equal(isUnique, violation is not null);
    }

    /// <summary>SQLite 那颗驱动抛的异常在基类面上给不出任何结构化线索（实测 <c>SqlState</c> 为 null、
    /// <c>Data</c> 为空），所以桩也只做这一件事：带一句消息。用例因此没法不小心依赖到不存在的字段。</summary>
    sealed class MessageOnlyDbException(string message) : DbException
    {
        public override string Message => message;
    }
}
