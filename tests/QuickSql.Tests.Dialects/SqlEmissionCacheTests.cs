using QuickSql.Generated;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>重复链的发射缓存（QueryEngine 的 Effective 记忆 + Build 产物槽）判据。
/// 缓存的合法性边界只有两条，各占一组用例：① 链不可变、值在算子时刻已翻进 AST，
/// 所以同模型重复发射是纯重复劳动，第二次起 0 分配；② 过滤器是唯一的运行期变数——
/// 在册时 Effective 不记忆（闭包租户值每语句重翻），后注册靠版本号顶掉缓存。
/// 全程离线（SqlServer 桩工厂），只到 ToSql/Explain 文本层。</summary>
public sealed class SqlEmissionCacheTests
{
    static QuickSqlOrm Orm()
    {
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);
        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = provider, ConnectionString = SqlServer.OfflineConnectionString },
            new G1OnlyEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet));
    }

    // ── ① 重复发射是纯重复劳动 ──────────────────────────────────────────

    [Fact]
    public void Re_emitting_the_same_chain_allocates_nothing()
    {
        var chain = Orm().Select<Order>();

        chain.ToSql();   // 首发建缓存

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sql = chain.ToSql();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Contains("FROM [QuickSqlOrder] AS [t0]", sql);
    }

    [Fact]
    public void The_cached_ast_and_sql_are_the_same_instances()
    {
        var chain = Orm().Select<Order>();

        var first = chain.Explain();
        var second = chain.Explain();

        Assert.Same(first.Ast, second.Ast);
        Assert.Same(first.Sql, second.Sql);
    }

    [Fact]
    public void Parallel_re_emission_gives_the_same_text()
    {
        var chain = Orm().Select<Order>();
        var expected = chain.ToSql();

        var results = new string[64];
        Parallel.For(0, results.Length, i => results[i] = chain.ToSql());

        Assert.All(results, text => Assert.Equal(expected, text));
    }

    // ── ② 过滤器是唯一的运行期变数 ──────────────────────────────────────

    [Fact]
    public void A_filter_registered_after_the_first_emission_invalidates_the_cache()
    {
        var orm = Orm();
        var chain = orm.Select<Order>();

        var bare = chain.ToSql();
        Assert.DoesNotContain("WHERE", bare);

        orm.Filters.Add<Order>(o => o.CustomerId == 1);

        var filtered = chain.ToSql();
        Assert.Contains("WHERE", filtered);
        Assert.Contains("[CustomerId]", filtered);
    }

    [Fact]
    public void Filter_closure_values_stay_fresh_across_emissions()
    {
        var orm = Orm();
        var tenant = 1;
        orm.Filters.Add<Order>(o => o.CustomerId == tenant);

        var chain = orm.Select<Order>();

        var first = chain.Explain();
        Assert.Contains(first.Sql.Parameters, p => Equals(p.Value, 1));

        tenant = 2;   // 换租户：不注册新过滤器，只改闭包值

        var second = chain.Explain();
        Assert.Contains(second.Sql.Parameters, p => Equals(p.Value, 2));
        Assert.DoesNotContain(second.Sql.Parameters, p => Equals(p.Value, 1));
    }

    [Fact]
    public void A_chain_without_filters_is_unaffected_by_later_registrations_on_other_tables()
    {
        var orm = Orm();
        var chain = orm.Select<Order>();
        var expected = chain.ToSql();

        // 注册在别的实体上：版本号涨了，但这条链的 SQL 不该变（重算一次也同文本）。
        orm.Filters.Add<Ticket>(t => !t.Deleted);

        Assert.Equal(expected, chain.ToSql());
    }
}