using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§10.6 必填尺子的那一句区分：<b>"这一列非空"究竟是谁说的</b>。离线四臂（真库那一臂在
/// <c>NullabilityDeclarationPathTests</c>），全程 <see cref="SqlServer.NoDriverFactory"/>，一句 SQL 不上线。
///
/// <para><b>事故的形状</b>（本轮的证人，真库 PostgreSQL）：ABP 那一族的实体住在可空上下文之外
/// （<c>#nullable disable</c>、没带可空元数据的引用程序集），<c>NullabilityInfoContext</c> 对这样的成员
/// 交回 <c>Unknown</c>。旧那一版把 Unknown 一并折成"非空"，于是每一次合法写入 —— 租户号、备注、
/// 客户端令牌那几列本来就是 null —— 都被一把没人宣布过的尺子挡下：症状是"插入被必填校验整批拒掉、
/// 一行都没写进库"，而库里那一行从来没打算收这一句。</para>
///
/// <para><b>列的形状不动</b>：现库真有 130 颗 <c>NOT NULL</c> 的文本列，翻可空性方向等于启动期替它们
/// 批量 <c>DROP NOT NULL</c>。所以这一颗只摘尺子，建表那一句逐字与改动之前同形（第四臂钉这一格）。
/// 而"表过态"的那几形照旧挡：<c>string</c> 在可空上下文里、<c>[Required]</c>、Fluent 各一臂 ——
/// 只测放行不测拒绝的话，一把整片放下的实现照样全绿。</para></summary>
public sealed class NullabilityDeclarationTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => SqlServer.Orm();

    static ColumnContract UnspokenColumn() => Column(nameof(ObliviousRow.Unspoken));

    static ColumnContract Column(string member)
        => orm().Schemas.Contract<ObliviousRow>().Columns.Single(c => c.MemberName == member);

    /// <summary>可空上下文之外那一族的三格：没表过态的、显式说可空的、<c>[Required]</c> 说非空的。</summary>
    static ObliviousRow Row(string? unspoken = null, string? spokenNullable = null) => new()
    {
        Id = 1,
        Unspoken = unspoken!,
        SpokenNullable = spokenNullable,
        SpokenRequired = "REQ",
        Quantity = 2,
    };

    // ── 第一臂：Unknown 不是一句"非空"的声明，而三格的来路各不同 ────

    [Fact]
    public void A_member_outside_a_nullable_context_is_recorded_as_unspoken_not_as_not_null()
    {
        var unspoken = UnspokenColumn();

        // 形状照旧是非空列（那是建表的默认，改它要动现库），但这句话不算声明。
        Assert.False(unspoken.IsNullable);
        Assert.False(unspoken.NullabilityDeclared);
        Checks.Has("可空 ← C# 没表过态（可空上下文之外）⇒ 建表按非空列，不落必填尺子", unspoken.Origin.Note);

        // 同一颗实体上说过话的两格：一真一假，都不受这一颗影响。
        var nullable = Column(nameof(ObliviousRow.SpokenNullable));
        var required = Column(nameof(ObliviousRow.SpokenRequired));

        Assert.True(nullable.IsNullable);
        Assert.True(nullable.NullabilityDeclared);
        Checks.Has("可空 ← C# 可空 ⇒ 可空列", nullable.Origin.Note);

        Assert.False(required.IsNullable);
        Assert.True(required.NullabilityDeclared);
        Checks.Has("可空 ← [Required]", required.Origin.Note);

        // 值类型那一档与这一颗无关：非空值类型永远非空、永远算声明。
        var quantity = Column(nameof(ObliviousRow.Quantity));

        Assert.False(quantity.IsNullable);
        Assert.True(quantity.NullabilityDeclared);
        Checks.Has("可空 ← 值类型非 Nullable ⇒ 非空列", quantity.Origin.Note);
    }

    // ── 第二臂：尺子在这一格真的松了，而列与参数一根都没少 ──────────

    [Fact]
    public void An_unspoken_non_null_column_does_not_refuse_the_null_the_entity_hands_over()
    {
        var sql = orm().Insert(Row()).ToSql(out var parameters);

        Checks.Has("INSERT INTO [QuickSqlObliviousRow]", sql);

        // 放行的是校验，不是那一列：列还在清单里、参数还在原位、值就是那个 null。
        // Id 不在清单里是另一条规则（默认档的"整型主键 ⇒ 自增"那一格，见 ApplicationKeyConventionTests）。
        Assert.Equal(
            [nameof(ObliviousRow.Unspoken), nameof(ObliviousRow.SpokenNullable),
                nameof(ObliviousRow.SpokenRequired), nameof(ObliviousRow.Quantity)],
            [.. parameters.Select(p => p.Hint?.MemberName)]);
        // 原位那颗值是 <c>DBNull</c> 而不是 C# 的 null：绑定层对驱动只说一种话（<c>AdoExecutor</c> 里那句
        // <c>Value ?? DBNull.Value</c>）。这一格顺带钉住尺子松开后发出去的是库的"无值"，不是被折成空串。
        Assert.Equal(DBNull.Value, parameters[0].Value);
    }

    // ── 第三臂：说过话的照旧挡（三形各一颗，缺一形就看不见尺子被整体放下）──

    [Fact]
    public void A_column_declared_required_by_attribute_still_refuses_the_null()
    {
        var failure = Checks.Raised<ValidationFailedException>(
            () => orm().Insert(new ObliviousRow
            {
                Id = 1,
                Unspoken = "这一格本来就能空",
                SpokenRequired = null!,
            }).ToSql());

        var bad = Assert.Single(failure.Failures);

        Assert.Equal(nameof(ObliviousRow.SpokenRequired), bad.MemberName);
        Assert.Equal("required", bad.Rule);
    }

    [Fact]
    public void A_plain_string_inside_a_nullable_context_is_still_a_declaration_and_still_refuses_null()
    {
        // SpokenRow 与 ObliviousRow 唯一的差别就是所在文件的可空上下文：同一份规则在这一族上
        // 仍落成"非空列 + 说过话 + 挡 null"。摘的是 Unknown，不是整把尺子。
        var declared = orm().Schemas.Contract<SpokenRow>().Columns
            .Single(c => c.MemberName == nameof(SpokenRow.DeclaredNotNull));

        Assert.False(declared.IsNullable);
        Assert.True(declared.NullabilityDeclared);
        Checks.Has("可空 ← C# 非空 ⇒ 非空列", declared.Origin.Note);

        var failure = Checks.Raised<ValidationFailedException>(
            () => orm().Insert(new SpokenRow { Id = 1, DeclaredNotNull = null! }).ToSql());

        Assert.Equal(nameof(SpokenRow.DeclaredNotNull), Assert.Single(failure.Failures).MemberName);
    }

    [Fact]
    public void The_chained_update_shares_the_ruler_with_the_insert()
    {
        // UpdateEngine 走的是同一颗 ContractRules.Required：把一列置成 null 时，没表过态的那一列
        // 不该比插入更严，而说过话的那一列不许比插入更松 —— 两半在同一用例里成对。
        // 那颗局部变量是给重载口用的：Set(列, 值) 与 Set(列, 表达式) 在裸 null 上分不出是谁。
        string nothing = null!;

        var sql = orm().Update<ObliviousRow>()
            .Set(r => r.Unspoken, nothing)
            .Where(r => r.Id == 1)
            .ToSql();

        Checks.Has("SET [Unspoken] = @p0", sql);

        Checks.Raised<ValidationFailedException>(
            () => orm().Update<ObliviousRow>().Set(r => r.SpokenRequired, nothing).Where(r => r.Id == 1).ToSql());
    }

    // ── 第四臂：建表那一句逐字没动 ─────────────────────────────────

    [Fact]
    public void The_generated_ddl_keeps_the_column_shape_it_always_had()
    {
        var ddl = Ddl();

        // 摘掉的是尺子，不是 NOT NULL：这一句若变了，就等于替现库那些文本列批量松约束。
        Checks.Has("[Unspoken] NVARCHAR(MAX) NOT NULL", ddl);
        Checks.Has("[SpokenNullable] NVARCHAR(MAX) NULL", ddl);
        Checks.Has("[SpokenRequired] NVARCHAR(MAX) NOT NULL", ddl);
    }

    static string Ddl()
    {
        var writer = new SqlWriter(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

        Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<ObliviousRow>());

        return writer.ToSqlString();
    }
}
