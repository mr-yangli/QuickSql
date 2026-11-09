using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>默认值那一格（2026-11-01 落地）的离线半：契约、门禁与四家 DDL 文本。
///
/// <para><b>契约半</b>：<c>[DefaultValue]</c>（System.ComponentModel）经反射侧解析链落进
/// <see cref="ColumnContract.DefaultValue"/>，没配的那一列是 null —— "没配"与"配了 null"两种说法
/// 只有一种存在。</para>
///
/// <para><b>门禁半</b>：值域（常量字面量档）、类型相容（剥 Nullable 精确同型）、自增/主键不收 ——
/// 三格都在 <c>EntityContractFactory</c> 收尾判，G1 ∶ G2 两侧共用一份；G1 侧生成期那半边
/// （QuickSql005）另有 <see cref="GeneratorDiagnosticTests"/> 同族量具，这里不重复。</para>
///
/// <para><b>DDL 半</b>：DEFAULT 落在建表文本里，四家各一份黄金样本（SqlServer <c>N''</c> ∶
/// MySQL 反斜杠转义 ∶ Kingbase <c>TRUE/FALSE</c> ∶ Dameng <c>1/0</c>）—— DDL 多一个括号就建不出表，
/// 与 <c>SqlServerDdlTests</c> 同一条纪律。</para></summary>
public sealed class DefaultValueTests
{
    static EntityContract Contract(IDbProvider provider, ModelConfiguration? fluent = null)
        => new EntitySchemaRegistry(new ContractResolver(provider, fluent)).Contract<DefaultsRow>();

    static ColumnContract Column(EntityContract contract, string member)
        => contract.Columns.Single(c => c.MemberName == member);

    // ── 契约半 ─────────────────────────────────────────────────────────

    [Fact]
    public void Declared_defaults_land_on_their_columns_and_unconfigured_stays_null()
    {
        var contract = Contract(SqlServer.Provider(SqlServer.NoDriverFactory.Instance));

        Assert.Equal("新行", (string) Column(contract, "Label").DefaultValue!);
        Assert.True((bool) Column(contract, "Active").DefaultValue!);
        Assert.Equal(7, (int) Column(contract, "Reserved").DefaultValue!);
        Assert.Equal(0.5, (double) Column(contract, "Ratio").DefaultValue!);
        Assert.Null(Column(contract, "Bare").DefaultValue);
    }

    /// <summary>Fluent 与注解同格而座次更高：两边都写时 Fluent 赢 —— 与备注那条链同一把尺。
    /// decimal 那一档顺带在这里（特性实参收不下 decimal，它只能走 Fluent 这条路）。</summary>
    [Fact]
    public void Fluent_wins_over_the_attribute_and_carries_decimal()
    {
        var fluent = new ModelConfiguration();
        fluent.Entity<DefaultsRow>().Property(r => r.Reserved).HasDefaultValue(42);
        fluent.Entity<DefaultsRow>().Property(r => r.Amount).HasDefaultValue(0.5m);

        var contract = Contract(SqlServer.Provider(SqlServer.NoDriverFactory.Instance), fluent);

        Assert.Equal(42, (int) Column(contract, "Reserved").DefaultValue!);
        Assert.Equal(0.5m, (decimal) Column(contract, "Amount").DefaultValue!);
    }

    // ── 门禁半（收尾那一格，G1 ∶ G2 共用；这里走反射侧进门） ───────────

    [Fact]
    public void A_datetime_default_is_refused_at_declaration()
    {
        var fluent = new ModelConfiguration();
        fluent.Entity<DefaultsRow>().Property(r => r.Label).HasDefaultValue(DateTime.Now);

        var raised = Checks.Raised<QuickSqlException>(
            () => Contract(SqlServer.Provider(SqlServer.NoDriverFactory.Instance), fluent));

        Checks.Has("常量字面量档", raised.Message);
        Checks.Has("DateTime", raised.Message);
    }

    [Fact]
    public void A_default_of_a_different_type_is_refused()
    {
        var raised = Checks.Raised<QuickSqlException>(
            () => new EntitySchemaRegistry(new ContractResolver(SqlServer.Provider(SqlServer.NoDriverFactory.Instance))).Contract<DefaultsRowBad>());

        Checks.Has("对不上", raised.Message);
        Checks.Has("Mistyped", raised.Message);
    }

    [Fact]
    public void An_identity_column_refuses_a_default()
    {
        var raised = Checks.Raised<QuickSqlException>(
            () => new EntitySchemaRegistry(new ContractResolver(SqlServer.Provider(SqlServer.NoDriverFactory.Instance))).Contract<DefaultsRowIdentity>());

        Checks.Has("自增", raised.Message);
        Checks.Has("不收默认值", raised.Message);
    }

    // ── DDL 半：四家黄金样本 ───────────────────────────────────────────

    static string Ddl(QuickSqlOrm orm)
    {
        var contract = orm.Schemas.Contract<DefaultsRow>();
        var writer = new SqlWriter(new StringBuilder(), [], orm.Provider.Dialect.ParameterPrefix);

        orm.Provider.CodeFirst.EmitCreateTable(writer, contract);

        return writer.ToSqlString();
    }

    [Fact]
    public void SqlServer_emits_defaults_in_the_create_table()
    {
        var sql = Ddl(TestInfra.SqlServer.Orm());

        Golden.Verify("ddl-defaults", sql);
        Checks.Has("[Label] NVARCHAR(16) NULL DEFAULT N'新行'", sql);
        Checks.Has("[Active] BIT NOT NULL DEFAULT 1", sql);
        Checks.Has("[Reserved] INT NOT NULL DEFAULT 7", sql);
        Checks.Has("[Ratio] FLOAT NOT NULL DEFAULT 0.5", sql);
        Checks.Has("[Amount] DECIMAL(18,4) NOT NULL, ", sql);
    }

    [Fact]
    public void MySql_emits_defaults_with_its_own_string_shape()
    {
        var sql = Ddl(TestInfra.MySql.Orm());

        Golden.Verify("ddl-defaults", sql, "mysql");
        Checks.Has("`Label` VARCHAR(16) NULL DEFAULT '新行'", sql);
        Checks.Has("`Reserved` INT NOT NULL DEFAULT 7", sql);
    }

    [Fact]
    public void Kingbase_emits_defaults_with_native_boolean_literals()
    {
        var sql = Ddl(TestInfra.Kingbase.Orm());

        Golden.Verify("ddl-defaults", sql, "kingbase");
        Checks.Has("\"Active\"", sql);
        Checks.Has("DEFAULT TRUE", sql);
        Checks.Has("DEFAULT '新行'", sql);
    }

    [Fact]
    public void Dameng_emits_defaults_without_boolean_literals()
    {
        var sql = Ddl(TestInfra.Dameng.Orm());

        Golden.Verify("ddl-defaults", sql, "dameng");
        Checks.Has("DEFAULT 1", sql);
        Checks.Has("DEFAULT '新行'", sql);
    }

}

/// <summary>门禁用的坏形状实体：整型列上配字符串默认（类型相容那格的靶子）。
/// 与 <c>GeneratorDiagnosticTests</c> 那批坏形状同一档：本项目不挂生成器，编译不红、解析才红。</summary>
[Table("QuickSqlDefaultsRowBad")]
public class DefaultsRowBad
{
    public int Id { get; set; }

    [DefaultValue("写错的默认")]
    public int Mistyped { get; set; }
}

/// <summary>自增主键配默认值的靶子：Id 命中"整型主键 ⇒ 自增"约定，默认值在收尾那一格被第 ③ 格拒。</summary>
[Table("QuickSqlDefaultsRowIdentity")]
public class DefaultsRowIdentity
{
    [DefaultValue(1)]
    public int Id { get; set; }
}
