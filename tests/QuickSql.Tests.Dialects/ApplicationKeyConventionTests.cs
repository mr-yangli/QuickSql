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
            [nameof(InventoryItem.Id), nameof(InventoryItem.Sku), nameof(InventoryItem.Quantity)],
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
        var writer = new SqlWriter(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

        Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<InventoryItem>());

        return writer.ToSqlString();
    }
}
