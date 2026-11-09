using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.TestInfra;
using System.Text;
using QuickSql.Providers;
using QuickSql.Sql;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>只读计算属性不许成列 —— 离线四臂（这一格的证人是一颗真库的 500，不是推演）。
/// 全程 <see cref="SqlServer.NoDriverFactory"/>，一句 SQL 不上线。
///
/// <para><b>事故的形状</b>：ABP 那一族的聚合上挂着 <c>public bool NeedsGrading =&gt; …</c> 这类
/// 从已存列算出来的成员。它有公共 getter、类型又可映射，旧那一版因此把它收进契约当一列；
/// 而<em>读取器</em>建到它就 <c>Expression.Assign</c> 不起（左端不可写），
/// 抛 <c>ArgumentException: Expression must be writeable</c>。症状不是"这一列读不出来"而是
/// 整个列表接口 500 —— 现场是题库与考试记录两颗列表，报错点在 <c>EntitySchemaRegistry.ReadDelegate</c>
/// 里，堆栈里连成员名都没有。</para>
///
/// <para><b>摘哪一格要说死</b>：判据是"有没有写口"，不是"写口公不公开"。第二臂钉这一句 ——
/// <c>{ get; protected set; }</c>、<c>{ get; private set; }</c>、<c>{ get; init; }</c> 三形都落得回值，
/// 若把它们一起摘掉，砍的是整族 ABP 实体的主键与明细列。</para></summary>
public sealed class ComputedMemberTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => SqlServer.Orm();

    static IEntitySchemaRegistry Generated()
        => new GeneratedEntitySchemaRegistry(Provider, QuickSql.Generated.QuickSqlGeneratedMetadata.TryGet);

    static string[] Members(EntityContract contract) => [.. contract.Columns.Select(c => c.MemberName)];

    // ── 第一臂：计算属性不在契约的列清单里 ─────────────────────────

    [Fact]
    public void A_read_only_computed_member_is_not_a_column()
    {
        Assert.Equal(
            [nameof(ComputedMemberRow.Id), nameof(ComputedMemberRow.Type), nameof(ComputedMemberRow.Name),
                nameof(ComputedMemberRow.Guarded), nameof(ComputedMemberRow.Secretive), nameof(ComputedMemberRow.Seeded)],
            Members(orm().Schemas.Contract<ComputedMemberRow>()));
    }

    // ── 第二臂：有写口的那三形（受保护/私有/init）原样在场 ─────────

    [Fact]
    public void A_non_public_setter_is_still_a_write_path_and_still_a_column()
    {
        var columns = orm().Schemas.Contract<ComputedMemberRow>().Columns;

        Assert.Contains(nameof(ComputedMemberRow.Guarded), Members(orm().Schemas.Contract<ComputedMemberRow>()));
        Assert.Contains(nameof(ComputedMemberRow.Secretive), Members(orm().Schemas.Contract<ComputedMemberRow>()));
        Assert.Contains(nameof(ComputedMemberRow.Seeded), Members(orm().Schemas.Contract<ComputedMemberRow>()));

        // 三形的可空性各按自己那一句：值类型两格非空，字符串那两格在可空上下文之内说了非空。
        Assert.False(columns.Single(c => c.MemberName == nameof(ComputedMemberRow.Seeded)).IsNullable);
        Assert.False(columns.Single(c => c.MemberName == nameof(ComputedMemberRow.Guarded)).IsNullable);
    }

    // ── 第三臂：读取器建得起来（本案的抛出点）──────────────────────

    [Fact]
    public void The_read_delegate_builds_instead_of_throwing_on_the_computed_member()
    {
        // 这一句就是现场那一颗抛出点：旧那一版在这里抛 ArgumentException，
        // 而抛的时刻是"第一次给这一型建行读取器"，于是症状落在列表接口上。
        var reader = orm().Schemas.ReadDelegate(typeof(ComputedMemberRow));

        Assert.NotNull(reader);

        // G1 那一侧同一句：两条路径都得建得起来（生成器也认这一档）。
        Assert.NotNull(Generated().ReadDelegate(typeof(ComputedMemberRow)));
    }

    // ── 第四臂：建表里也没有那一列 ─────────────────────────────────

    [Fact]
    public void The_generated_ddl_has_no_column_for_the_computed_member()
    {
        var writer = new SqlWriter(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

        Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<ComputedMemberRow>());

        var ddl = writer.ToSqlString();

        Assert.DoesNotContain("RequiresManualGrading", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("Doubled", ddl, StringComparison.Ordinal);
        Checks.Has("[Guarded]", ddl);
        Checks.Has("[Seeded]", ddl);
    }

    // ── 第五臂：摘掉的是列，不是关系（集合导航那一形仍认）──────────

    [Fact]
    public void A_read_only_collection_member_is_dropped_as_a_relation_not_silently_kept_as_a_column()
    {
        // 计算属性与"只读集合导航"在契约里是两个问题：前者不是列也不是关系（没有写口，落不回值），
        // 后者如果落得回值就仍是一根边。这一臂钉住新那一档没有顺手把集合边一起摘掉。
        var contract = orm().Schemas.Contract<Rack>();

        Assert.Contains(nameof(Rack.Slots), contract.Collections.Select(c => c.MemberName));
    }
}
