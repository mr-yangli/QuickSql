using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using QuickSql;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>装配口那一颗「整型主键归谁给号」的声明，在<b>离线</b>能判的那一半。
///
/// <para>动机是一颗真业务库的读数：ABP 那一族的 <c>long</c> 主键由客户端（雪花）在实体构造时赋值，
/// 建出来的列只是 <c>bigint DEFAULT 0</c>，库里既不给号也不许写号。库那条「整型主键 ⇒ 自增」的约定
/// 套在这一族上是反的：插入语句不带 Id 列 ⇒ 每行拿到默认值 0 ⇒ 第二发就是主键冲突。</para>
///
/// <para><b>这一份只判形状，不判落库</b>：契约的 <c>IsIdentity</c>、列清单、<c>OUTPUT</c>/<c>RETURNING</c>
/// 那一格回不回键，全是编译期事实。真把一行带显式主键的语句发进库里读回那一行，归集成层。</para></summary>
public sealed class ApplicationKeyConventionTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>装配口关掉那一半的组装根 —— 与 <c>ConcurrencyTokenTests</c> 同一台机器，只多一个形参。</summary>
    static QuickSqlOrm appKeyOrm()
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, null, integralPrimaryKeyAutoIncrement: false)));

    static InventoryItem row() => new() { Id = 1_987_654_321, Sku = "A-1", Quantity = 2 };

    // ── 默认档：一句都不写，形状与这一格落地之前逐字相同 ─────────────

    [Fact]
    public void The_default_assembly_keeps_the_integral_primary_key_as_an_identity_column()
    {
        var column = SqlServer.Orm().Schemas.Contract<InventoryItem>().Columns
            .Single(c => c.MemberName == nameof(InventoryItem.Id));

        Assert.True(column.IsPrimary);
        Assert.True(column.IsIdentity);

        // 列清单那一半：自增列不进 INSERT，值由库决定。
        Checks.Absent("[Id]", SqlServer.Orm().Insert(row()).ToSql());
    }

    // ── 装配口那一档：约定让位，声明照旧赢 ──────────────────────────

    [Fact]
    public void The_assembly_switch_moves_the_convention_and_puts_the_key_in_the_column_list()
    {
        var column = appKeyOrm().Schemas.Contract<InventoryItem>().Columns
            .Single(c => c.MemberName == nameof(InventoryItem.Id));

        // 主键还是主键：这一颗只挪「值归谁给」，不挪「谁是键」。
        Assert.True(column.IsPrimary);
        Assert.False(column.IsIdentity);

        var sql = appKeyOrm().Insert(row()).ToSql(out var parameters);

        Checks.Has("[Id], [Sku], [Quantity]", sql);
        Assert.Equal(
            [nameof(InventoryItem.Id), nameof(InventoryItem.Sku), nameof(InventoryItem.Quantity), nameof(InventoryItem.CheckedAt), nameof(InventoryItem.Exception)],
            parameters.Select(p => p.Hint?.MemberName).ToArray());

        // 客户端给的那个号原样发出去，不是 0、不是被吞掉。
        Assert.Equal(1_987_654_321, Convert.ToInt64(parameters[0].Value));
    }

    [Fact]
    public void The_switched_assembly_refuses_the_key_backfill_slot_by_name()
    {
        // 库里没有自增列时，"把库发的号给我"这句话没有东西可给。抛，而不是发一句
        // "OUTPUT inserted.[Id]" 把客户端自己写的号当成库的产出读回来 —— 消息本身指路。
        var error = Assert.Throws<QuickSqlException>(() => appKeyOrm().Insert(row()).ExplainKeys());

        Checks.Has("没有自增列", error.Message);
        Checks.Absent("OUTPUT", appKeyOrm().Insert(row()).Explain().ToSql());
    }

    [Fact]
    public void A_declared_identity_column_beats_the_assembly_switch()
    {
        // 反向那一半才是"只挪约定"的证据：装配口若把声明一起压掉，
        // 真自增表在这一族库里就没有任何出路，而那正是这条链最不该有的形状。
        var model = new ModelConfiguration();
        model.Entity<InventoryItem>().Property(i => i.Id).IsIdentity(true);

        var orm = ormWith(model, integralPrimaryKeyAutoIncrement: false);

        var column = orm.Schemas.Contract<InventoryItem>().Columns.Single(c => c.MemberName == nameof(InventoryItem.Id));
        Assert.True(column.IsIdentity);

        // 声明赢的具体形状：列清单里没有 Id，而回填那一格有。
        var insert = orm.Insert(row());

        Checks.Absent("[Id]", insert.ToSql());
        Checks.Has("OUTPUT inserted.[Id]", insert.ExplainKeys().ToSql());
    }

    // ── 声明说"不是"那一半（与上面三颗对称）──────────────────────────

    /// <summary>靶：成员名正好命中命名约定（裸 <c>Id</c>，没写 <c>[Key]</c>），而声明说了"不归库给号"。
    ///
    /// <para>修之前这一格是反的：<c>ApplyKeyConvention</c> 补「谁是键」时顺手把约定自增 <c>||</c> 回去，
    /// 盖掉了 <c>Complete</c> 已经按声明算出的 <c>false</c> —— 症状是 <c>INSERT</c> 少一列 <c>[Id]</c>、
    /// 号由库给，而 :353 那句分表门禁提示叫用户写的正是这一句声明。键那一半照旧归约定，
    /// 所以 <c>IsPrimary</c> 仍应是 <c>true</c>。</para></summary>
    [Fact]
    public void A_declared_non_identity_on_the_convention_key_beats_the_integral_convention()
    {
        var column = SqlServer.Orm().Schemas.Contract<GivenKeyRow>().Columns
            .Single(c => c.MemberName == nameof(GivenKeyRow.Id));

        Assert.True(column.IsPrimary);
        Assert.False(column.IsIdentity);

        var sql = SqlServer.Orm().Insert(new GivenKeyRow { Id = 1_987_654_321, Sku = "A-1" }).ToSql(out var parameters);

        // 声明赢的具体形状：客户端那个号原样发出去，不是被吞成库的产出。
        Checks.Has("[Id]", sql);
        Assert.Equal(1_987_654_321, Convert.ToInt64(parameters[0].Value));
    }

    /// <summary>门禁与文案自洽那一格：分表实体的整型主键按 :353 的提示写声明之后，契约真的不再算自增，
    /// 于是那张表建得出契约而不是被门禁挡回。修之前提示与门禁互相打架——照提示写完还是"自增主键"。</summary>
    [Fact]
    public void The_split_table_advice_in_the_identity_error_message_actually_lands()
    {
        var contract = SqlServer.Orm().Schemas.Contract<SplitGivenKeyLog>();

        Assert.True(contract.PrimaryKey[0].IsPrimary);
        Assert.False(contract.PrimaryKey[0].IsIdentity);
    }

    /// <summary>命名约定 + 声明说不是的靶实体（不写 <c>[Key]</c>，键那一半故意留给约定）。</summary>
    public class GivenKeyRow
    {
        [QuickColumn(IsIdentity = TriState.No)]
        public int Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Sku { get; set; }
    }

    /// <summary>同一句声明用在分表实体上：门禁要的就是"跨片唯一性由客户端给"。</summary>
    [Table("QuickSqlGivenKeyLog_{year}{month}")]
    [SplitTable(SplitKind.Month, nameof(OccurredAt))]
    public class SplitGivenKeyLog
    {
        [QuickColumn(IsIdentity = TriState.No)]
        public int Id { get; set; }

        public DateTime OccurredAt { get; set; }

        [QuickColumn(Length = 32)]
        public string? Note { get; set; }
    }

    // ── 建表那一句跟着同一把尺子走 ─────────────────────────────────

    [Fact]
    public void The_generated_ddl_stops_advertising_identity_for_the_key()
    {
        // CodeFirst 的语句取自契约，所以这一格不需要第二个开关：契约说不归库给，DDL 就不发 IDENTITY。
        var ddl = Ddl(appKeyOrm());

        Checks.Absent("IDENTITY", ddl);
        Checks.Has("[Id] INT NOT NULL", ddl);

        // 默认档照旧发那一格 —— 这一颗是装配声明，不是把 IDENTITY 从库里删掉。
        Checks.Has("[Id] INT IDENTITY(1,1) NOT NULL", Ddl(SqlServer.Orm()));
    }

    static QuickSqlOrm ormWith(ModelConfiguration model, bool integralPrimaryKeyAutoIncrement)
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model, integralPrimaryKeyAutoIncrement)));

    static string Ddl(QuickSqlOrm orm)
    {
        var writer = new SqlWriter(new StringBuilder(), Provider.Dialect.ParameterPrefix);

        Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<InventoryItem>());

        return writer.ToSqlString();
    }
}
