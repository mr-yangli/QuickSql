using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary><c>DbContext.OnModelCreating</c>/<see cref="ModelBuilder"/> 那一格（2026-11-01 落地，
/// §8.1 挂账销账）的离线半。判四件只有这里能穷举的事：
///
/// <para>① <b>覆写即整份模型</b>：覆写钩子的上下文按自己配的那份 Fluent 出 SQL（列名/表名改判生效）；
/// ② <b>隔离</b>：两个上下文类型各配各的，互不串扰，而 <c>orm.Select&lt;T&gt;()</c>（组装根那份模型）
/// 逐字不变 —— "同一个 Type 在两个 DbContext 里配置不同"就是这一格要开口的形状（规格 L895 那道硬约束）；
/// ③ <b>每派生类型恰跑一次</b>：配置按类型缓存，两只同型实例共享；
/// ④ <b>预登记</b>：<c>mb.Entity&lt;T&gt;()</c> 点过名的实体在首次查询之前就在会话注册表的在册清单里。</para>
///
/// <para>靶实体只归本文件用（本地类，G2 解析 —— 本册不挂生成器）。</para></summary>
public sealed class OnModelCreatingTests
{
    [Table("QuickSqlHookRow")]
    public class HookRow
    {
        public int Id { get; set; }

        public string? Name { get; set; }
    }

    public class HookRow2
    {
        public int Id { get; set; }

        [QuickColumn(Length = 16)]
        public string? Tag { get; set; }
    }

    /// <summary>覆写钩子的上下文：两张实体各配各的表名，第一张连列名一起改判。</summary>
    public class RenamingContext : DbContext
    {
        public RenamingContext(QuickSqlOrm orm) : base(orm) { }

        public static int ModelBuilds;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ModelBuilds++;

            modelBuilder.Entity<HookRow>(e =>
            {
                e.ToTable("QuickSqlHookRowRenamed");
                e.Property(r => r.Name).HasColumnName("HookName");
            });

            modelBuilder.Entity<HookRow2>().ToTable("QuickSqlHookRow2Renamed");
        }
    }

    /// <summary>另一只覆写钩子的上下文：同一实体配成另一张表 —— 隔离那半的对照。</summary>
    public class AnotherRenamingContext : DbContext
    {
        public AnotherRenamingContext(QuickSqlOrm orm) : base(orm) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<HookRow>().ToTable("QuickSqlHookRowAnother");
    }

    /// <summary>不覆写的上下文：零变化那一半的对照（ModelSchemas 为 null，走组装根）。</summary>
    public class PlainContext : DbContext
    {
        public PlainContext(QuickSqlOrm orm) : base(orm) { }
    }

    /// <summary>计数器专用型：本型只归"恰一次"那一颗用例摸，保证它读到的是冷启动（缓存未暖）。</summary>
    public class CountingContext : DbContext
    {
        public CountingContext(QuickSqlOrm orm) : base(orm) { }

        public static int ModelBuilds;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ModelBuilds++;

            modelBuilder.Entity<HookRow>().ToTable("QuickSqlHookRowCounted");
        }
    }

    static DbContext Hooked() => new RenamingContext(TestInfra.SqlServer.Orm());

    [Fact]
    public void An_overridden_model_renames_columns_and_tables_in_the_emitted_sql()
    {
        var sql = Hooked().Set<HookRow>().Where(r => r.Name == "x").ToSql();

        Checks.Has("[QuickSqlHookRowRenamed]", sql);
        Checks.Has("[HookName]", sql);
        Checks.Absent("[QuickSqlHookRow]", sql);
        Checks.Absent("[Name]", sql);
    }

    [Fact]
    public void Two_context_types_configuring_the_same_entity_stay_isolated()
    {
        var first = new RenamingContext(TestInfra.SqlServer.Orm()).Set<HookRow>().ToSql();
        var second = new AnotherRenamingContext(TestInfra.SqlServer.Orm()).Set<HookRow>().ToSql();
        var bare = TestInfra.SqlServer.Orm().Select<HookRow>().ToSql();

        // 各配各的：两只上下文两张表名，而组装根那份模型照旧是原表名 —— 三份互不串。
        Assert.Contains("QuickSqlHookRowRenamed", first, StringComparison.Ordinal);
        Assert.Contains("QuickSqlHookRowAnother", second, StringComparison.Ordinal);
        Assert.Contains("[QuickSqlHookRow]", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickSqlHookRowRenamed", bare, StringComparison.Ordinal);
    }

    /// <summary>配置按派生类型缓存：同型的两只实例共享同一份模型，钩子只跑一次。
    /// 计数器从测试起点的读数起算 —— 本册别的用例已经让它跑过。第二只实例只 <c>ToSql()</c>
    /// 不发查询：模型构建挂在跟踪器懒建那一格，<c>Set&lt;T&gt;()</c> 那一刻就发生了。</summary>
    [Fact]
    public void The_hook_runs_once_per_derived_type()
    {
        var before = CountingContext.ModelBuilds;

        _ = new CountingContext(TestInfra.SqlServer.Orm()).Set<HookRow>().ToSql();
        _ = new CountingContext(TestInfra.SqlServer.Orm()).Set<HookRow>().ToSql();

        // 两只实例、两次 Set<T>，钩子只在第一只上跑：配置按派生类型缓存，第二只共享。
        Assert.Equal(before + 1, CountingContext.ModelBuilds);
    }

    /// <summary>预登记的公开面：<c>mb.Entity&lt;T&gt;()</c> 点过名的<em>每一只</em>实体都按覆写模型解析 ——
    /// 只摸过第一张表的情况下，第二张（从未被任何查询碰过的那一只）出来的仍是改名后的表。
    /// 注册表清单那一格是 internal（<c>RegisteredTypes</c>），它的账由 SchemaSync 那条线接线时一起钉。</summary>
    [Fact]
    public void Every_named_entity_resolves_through_the_context_model()
    {
        var context = Hooked();

        _ = context.Set<HookRow>().ToSql();

        var sql = context.Set<HookRow2>().ToSql();

        Assert.Contains("QuickSqlHookRow2Renamed", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[QuickSqlHookRow2]", sql, StringComparison.Ordinal);
    }

    /// <summary>没覆写的上下文零变化：列名、表名照组装根那份模型逐字不变 ——
    /// 懒建与模型缓存对"绝大多数不覆写的上下文"一颗字节都不多付。</summary>
    [Fact]
    public void A_context_without_the_hook_stays_on_the_orm_model()
    {
        var sql = new PlainContext(TestInfra.SqlServer.Orm()).Set<HookRow>().Where(r => r.Name == "x").ToSql();

        Assert.Contains("[QuickSqlHookRow]", sql, StringComparison.Ordinal);
        Assert.Contains("[Name]", sql, StringComparison.Ordinal);
    }
}
