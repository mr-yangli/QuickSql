using System.ComponentModel.DataAnnotations.Schema;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>v0.20「关系」这一格在离线层的形状：一根边的两个来源、成环时那句拒绝、
/// 以及「有外键列但没有引用导航 ⇒ 不是边」这一条反向判据。
///
/// <para><b>这里判不了真正的语句顺序</b>：离线注册表一碰驱动就抛，所以「父行那条 INSERT 排在子行之前」
/// 只能在真库里看（集成层 <c>OrderingPathTests</c>，两边手工加一条 <c>FOREIGN KEY</c> 约束当裁判 ——
/// CodeFirst 今天不发约束文本，原因与代价记在 §14 未完项表）。本文件只钉得住<em>排序的判据与失败形状</em>：
/// 环必须在碰到驱动之前就被拒，不然现场读到的是一句"连不上库"。</para>
///
/// <para>负样本（外键指向未映射成员）住在本文件而不是 <c>QuickSql.Tests.Common</c>：那边挂着源生成器，
/// 同样的形状会让编译直接红（<c>QuickSql004</c>），而「编译期负样本没有断言口」是 §14 里的另一格。</para></summary>
public sealed class RelationshipTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static IEntitySchemaRegistry Reflected(ModelConfiguration? model = null)
        => new EntitySchemaRegistry(new ContractResolver(Provider, model));

    static QuickSqlOrm Offline(ModelConfiguration? model = null)
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = "offline://no-driver" }, Reflected(model));

    [Fact]
    public void An_explicit_ForeignKey_names_the_members_and_overrides_the_convention()
    {
        var contract = Reflected().Contract<ExplicitFk>();

        // [ForeignKey] 里写的是成员名，契约里同时留着成员名与换算出来的列名（§10.3 那份边）。
        var edge = Assert.Single(contract.Relationships);

        Assert.Equal(nameof(ExplicitFk.Partner), edge.MemberName);
        Assert.Equal(typeof(Customer), edge.TargetType);
        Assert.Equal(["CustId"], edge.MemberNames);
        Assert.Equal(["customer_id"], edge.ColumnNames);
        Assert.Equal(MetadataLayer.DataAccessAnnotation, edge.Origin.Layer);
        Assert.Equal("[ForeignKey(\"CustId\")]", edge.Origin.Note);
    }

    [Fact]
    public void A_composite_ForeignKey_keeps_its_members_in_declaration_order()
    {
        var edge = Assert.Single(Reflected().Contract<CompositeFk>().Relationships);

        // 复合外键<em>不是</em>两条边：一行的父子关系是一件事，拆开就排不出"两端都就位"那一刻（§8.2）。
        Assert.Equal(["OrderId", "Sku"], edge.MemberNames);
        Assert.Equal(["OrderId", "Sku"], edge.ColumnNames);
    }

    [Fact]
    public void A_foreign_key_naming_an_unmapped_member_is_refused_at_startup_not_dropped()
    {
        var raised = Checks.Raised<QuickSqlException>(() => Reflected().Contract<BadFk>());

        // 静默丢掉一根边 = SaveChanges 少排一次序 = 库里那句外键约束替我们把错误挪到最贵的时刻。
        // G1 侧同一个判据是编译期的 QuickSql004（§10.2 两侧对得上）。
        Assert.Contains("引用导航 Partner 引用了未映射的属性 Nobody", raised.Message, StringComparison.Ordinal);
        Assert.Contains("[NotMapped]", raised.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_named_like_a_foreign_key_is_not_an_edge_without_a_navigation()
    {
        // 反向判据：光凭列名推依赖会把"这张表其实和被引用表无关"（列名撞车、NoSQL 式冗余列）判成环。
        // Order 自己那一格（CustomerId 有列、Customer 有导航）由 §13.11 的对拍钉着，这里钉的是"只有列"这一种。
        Assert.Empty(Reflected().Contract<TwoKeys>().Relationships);
    }

    [Fact]
    public void A_self_reference_is_not_an_edge()
    {
        var contract = Reflected().Contract<SelfRef>();

        // 边<em>在</em>契约里（Manager ⇒ ManagerId 是声明过的事实），但排序时不算边：
        // 同一张表内部的先后不是类型序能表达的，把自己当边就是把自己判成环。
        Assert.Equal(typeof(SelfRef), Assert.Single(contract.Relationships).TargetType);

        var db = new DbContext(Offline());

        db.Add(new SelfRef { Id = 1 });
        db.Add(new SelfRef { Id = 2, ManagerId = 1 });

        // 这一句在离线注册表上是驱动不可用，不是环 —— 差别就是"自引用没被当成环"的全部证据。
        Assert.DoesNotContain("排不出写入先后", Raised(() => db.SaveChanges()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_cycle_is_refused_before_the_driver_is_touched()
    {
        var db = new DbContext(Offline());

        db.Add(new CycleA { Id = 1, BId = 1 });
        db.Add(new CycleB { Id = 1, AId = 1 });

        var message = Raised(() => db.SaveChanges());

        // 成环是元数据的事，不是连不上库的事：这句话必须<em>先</em>于任何一次驱动调用（DbContext 里那句
        // EnsureWriteOrderResolvable 就是为此排在 BeginTransaction 之前）。
        Assert.Contains("排不出写入先后", message, StringComparison.Ordinal);
        Assert.Contains(nameof(CycleA), message, StringComparison.Ordinal);
        Assert.Contains(nameof(CycleB), message, StringComparison.Ordinal);
        // 环只是"排不出"，不是"填不上"：说 Tp 是给这一格留的出路（Tp 与外键回填是 v0.21 那一格）。
        Assert.Contains("Tp", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dependency_on_a_type_that_owes_nothing_is_not_a_cycle()
    {
        // 边上那个人不在会话里 ⇒ 没有边：把 Milestone 依赖 Project 当成环来拒，就是每一处
        // "只往子表插数据"的正常用法都白报一次错。这里 Project 一行都没 Add。
        var db = new DbContext(Offline());

        db.Add(new Milestone { ProjectId = Guid.NewGuid(), Title = "只插子行" });

        Assert.DoesNotContain("排不出写入先后", Raised(() => db.SaveChanges()), StringComparison.Ordinal);
    }

    static string Raised(Func<object?> evaluate)
    {
        try
        {
            evaluate();
        }
        catch (Exception e)
        {
            return e.Message;
        }

        // 没抛就是这一格没判住 —— 返回空串会让上面的"不该含"断言假绿，所以这里必须响。
        // 没抛就是这一格没判住 —— 返回空串会让上面那句「不该含」假绿，所以这里必须响。
        throw new InvalidOperationException("这一句本该抛，结果什么都没抛：判据没生效");
    }
}

/// <summary>显式 <c>[ForeignKey]</c> 的靶子：外键列名与导航名都不重合，所以命中只可能来自那一层。</summary>
[Table("QuickSqlExplicitFk")]
public class ExplicitFk
{
    public int Id { get; set; }

    [DaSchema.ForeignKey("CustId")]
    public Customer? Partner { get; set; }

    [Column("customer_id")]
    public int CustId { get; set; }
}

/// <summary>复合外键：一根边两列，逗号分隔，顺序照声明。</summary>
[Table("QuickSqlCompositeFk")]
public class CompositeFk
{
    public int Id { get; set; }

    [DaSchema.ForeignKey("OrderId,Sku")]
    public OrderItem? Line { get; set; }

    public int OrderId { get; set; }

    [QuickColumn(Length = 32)]
    public string? Sku { get; set; }
}

/// <summary>负样本：<c>[ForeignKey]</c> 点了一个不存在的成员。</summary>
[Table("QuickSqlBadFk")]
public class BadFk
{
    public int Id { get; set; }

    [DaSchema.ForeignKey("Nobody")]
    public Customer? Partner { get; set; }
}

/// <summary>成环的两个成员：<c>A</c> 依赖 <c>B</c>、<c>B</c> 依赖 <c>A</c>，都靠约定命中（<c>BId</c>/<c>AId</c>）。
/// 放在本文件（没有源生成器）而不是 Common，是为了让 G2 的启动期抛点有用例 —— 挂上生成器它连编译都过不去。</summary>
[Table("QuickSqlCycleA")]
public class CycleA
{
    public int Id { get; set; }

    public int BId { get; set; }

    public CycleB? B { get; set; }
}

[Table("QuickSqlCycleB")]
public class CycleB
{
    public int Id { get; set; }

    public int AId { get; set; }

    public CycleA? A { get; set; }
}
