using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>链式合并（<c>UnionAll</c>/<c>Union</c>）的<b>发射形状</b>，四家离线各钉一份。判的是四件事：
///
/// <para>① 两腿各自完整成句、整串占一个表位：句里 <c>SELECT</code> 的个数 = 外层 1 + 腿 2，
/// 关键字按档落（<c>UNION ALL</code> 不去重 ∶ <c>UNION</code> 去重）。</para>
///
/// <para>② 序与分页<b>只长在外层</b>：腿里没有任何 ORDER BY/LIMIT 文本（形状守卫在链上就把带序的腿拒了，
/// 这里钉的是发出来的文本里也真的没有）；外层的 OFFSET/LIMIT 只出现一次。</para>
///
/// <para>③ Oracle 血统的包装式分页把<b>整串</b>包进去：UNION 活在内层、ROWNUM 包在外层 —— 那是唯一
/// 能保住"合并后取前 N 行"语义的形状（每腿各取 N 行是另一种语义，本家不发）。</para>
///
/// <para>④ 模型文本（<c>Explain().ModelText</code>）带 <c>SOURCE UNION</code> 那一行与两条腿的全文 ——
/// §13.2 那句"这一格里有东西，模型文本就必须有对应的一行"在这里兑现。</para>
///
/// <para>拒收口（带序的腿、套娃、无引擎的参数）由集成册 <c>UnionPathTests</code> 在活库那侧同名再钉；
/// 这里不重复。</para></summary>
public sealed class SetOperationEmissionTests
{
    static QuickSqlOrm SqlServerOrm => TestInfra.SqlServer.Orm();

    static QuickSqlOrm PostgreSqlOrm => TestInfra.PostgreSql.Orm();

    static QuickSqlOrm MySqlOrm => TestInfra.MySql.Orm();

    static QuickSqlOrm OracleOrm => TestInfra.Oracle.Orm();

    static IQuery<Order> Paid(QuickSqlOrm orm) => orm.Select<Order>().Where(o => o.Amount > 100m);

    static IQuery<Order> Recent(QuickSqlOrm orm) => orm.Select<Order>().Where(o => o.CustomerId == 7);

    /// <summary>①+②：T-SQL 那一份 —— 三句 SELECT、一个 UNION ALL、外层 OFFSET..FETCH 只有一份、
    /// 腿里没有任何序与分页文本。</summary>
    [Fact]
    public void SqlServer_union_all_keeps_ordering_on_the_outer_statement_only()
    {
        var sql = Paid(SqlServerOrm)
            .UnionAll(Recent(SqlServerOrm))
            .OrderBy(o => o.Id)
            .Skip(3)
            .Take(5)
            .ToSql();

        Assert.Equal(3, CountOf(sql, "SELECT "));
        Assert.Equal(1, CountOf(sql, " UNION ALL "));
        Assert.Equal(1, CountOf(sql, "OFFSET"));
        Assert.DoesNotContain("ORDER BY", sql[..sql.IndexOf(" UNION ALL ", StringComparison.Ordinal)]);
        Assert.DoesNotContain("OFFSET", sql[..sql.IndexOf(" UNION ALL ", StringComparison.Ordinal)]);
    }

    /// <summary>①：<c>UNION</code> 那一档整句去重 —— 关键字逐字是 <c> UNION </code> 而不是
    /// <c> UNION ALL </code>，其余形状与不去重档同构。</summary>
    [Fact]
    public void PostgreSql_union_uses_the_deduplicating_keyword()
    {
        var sql = Paid(PostgreSqlOrm)
            .Union(Recent(PostgreSqlOrm))
            .ToSql();

        Assert.Equal(3, CountOf(sql, "SELECT "));
        Assert.DoesNotContain(" UNION ALL ", sql);
        Assert.Contains(" UNION ", sql);
    }

    /// <summary>①：MySQL 那一份与 PG 同构（LIMIT 活在外层、腿里没有分页文本）。</summary>
    [Fact]
    public void MySql_union_all_keeps_limit_on_the_outer_statement_only()
    {
        var sql = Paid(MySqlOrm)
            .UnionAll(Recent(MySqlOrm))
            .OrderBy(o => o.Id)
            .Take(4)
            .ToSql();

        Assert.Equal(3, CountOf(sql, "SELECT "));
        Assert.Equal(1, CountOf(sql, " UNION ALL "));
        Assert.Equal(1, CountOf(sql, "LIMIT"));
        Assert.DoesNotContain("LIMIT", sql[..sql.IndexOf(" UNION ALL ", StringComparison.Ordinal)]);
    }

    /// <summary>③：Oracle 血统的包装式分页把整串包进去 —— UNION 活在内层，
    /// 外层是 ROWNUM 那两层；腿里照旧没有序与分页。</summary>
    [Fact]
    public void Oracle_family_wraps_the_whole_union_for_paging()
    {
        var sql = Paid(OracleOrm)
            .UnionAll(Recent(OracleOrm))
            .OrderBy(o => o.Id)
            .Take(2)
            .ToSql();

        // 四句 SELECT = 包装层 1 + 带 UNION 的那一份 1 + 两条腿 2 —— ROWNUM 层包的是整串，
        // 不是把某一条腿单独包一遍（那是"每腿各取 N 行"的另一种语义）。
        Assert.Equal(4, CountOf(sql, "SELECT "));
        Assert.Equal(1, CountOf(sql, " UNION ALL "));
        Assert.Contains("ROWNUM", sql);
    }

    /// <summary>④：模型文本带 SOURCE UNION 行与两条腿全文 —— 合并这一格在 Explain() 里看得见。</summary>
    [Fact]
    public void Explain_shows_the_set_operation_and_both_legs()
    {
        var plan = Paid(SqlServerOrm).UnionAll(Recent(SqlServerOrm)).Explain();

        Assert.Contains("SOURCE UNION ALL", plan.ModelText);
        Assert.Contains("LEG1", plan.ModelText);
        Assert.Contains("LEG2", plan.ModelText);
    }

    /// <summary>①（2026-11-01 补的两口）：<c>INTERSECT</c> 与 <c>EXCEPT</c> 走同一条链路，关键字逐字是
    /// 标准形 —— T-SQL 那一份。腿守卫与 <c>UnionAll</c> 同一条（同一颗 <c>SetOps</c>），不重复钉。</summary>
    [Fact]
    public void SqlServer_intersect_and_except_use_the_standard_keywords()
    {
        var intersect = Paid(SqlServerOrm).Intersect(Recent(SqlServerOrm)).ToSql();
        var except = Paid(SqlServerOrm).Except(Recent(SqlServerOrm)).ToSql();

        Assert.Equal(1, CountOf(intersect, " INTERSECT "));
        Assert.Equal(1, CountOf(except, " EXCEPT "));
        Assert.DoesNotContain(" UNION ", intersect);
        Assert.DoesNotContain(" MINUS ", except);
    }

    /// <summary>①：PG 那一份 <c>INTERSECT</c> 同构 —— 确认方言分岔只落在 EXCEPT 那一档，
    /// <c>INTERSECT</c> 全家标准字。</summary>
    [Fact]
    public void PostgreSql_intersect_keeps_the_standard_keyword()
    {
        var sql = Paid(PostgreSqlOrm).Intersect(Recent(PostgreSqlOrm)).ToSql();

        Assert.Equal(3, CountOf(sql, "SELECT "));
        Assert.Equal(1, CountOf(sql, " INTERSECT "));
    }

    /// <summary>①+③：Oracle 血统的 <c>EXCEPT</c> 发 <c>MINUS</c>（四种集合运算里唯一进方言的关键字分岔，
    /// <c>DialectBase.ExceptKeyword</c> 那一格），包装式分页照旧包整串。</summary>
    [Fact]
    public void Oracle_family_emits_minus_for_except_and_wraps_for_paging()
    {
        var sql = Paid(OracleOrm)
            .Except(Recent(OracleOrm))
            .OrderBy(o => o.Id)
            .Take(2)
            .ToSql();

        Assert.Equal(4, CountOf(sql, "SELECT "));
        Assert.Equal(1, CountOf(sql, " MINUS "));
        Assert.DoesNotContain(" EXCEPT ", sql);
        Assert.Contains("ROWNUM", sql);
    }

    /// <summary>④：四种集合运算在模型文本里各自看得见 —— SOURCE 行随档落字（打规范形，
    /// Oracle 族的 MINUS 是方言话，不进跨库稳定的模型文本）。</summary>
    [Fact]
    public void Explain_shows_intersect_and_except_lines()
    {
        var intersect = Paid(SqlServerOrm).Intersect(Recent(SqlServerOrm)).Explain();
        var except = Paid(SqlServerOrm).Except(Recent(SqlServerOrm)).Explain();

        Assert.Contains("SOURCE INTERSECT", intersect.ModelText);
        Assert.Contains("SOURCE EXCEPT", except.ModelText);
    }

    /// <summary>守卫（2026-11-11 审查补记）：<c>As()</c> 与集合运算覆盖源的尖角。<c>QueryEngine.Alias</c>
    /// 改写 <c>Model.Root</c> 与 <c>RawTextSource</c> 的 Ref（:420 三元只有 raw 一臂），<c>SourceSetOps</c>
    /// 的 Ref 是链上那一刻抓住的表位、不被改写——于是合并之后点 <c>As("x")</c>，外层投影与谓词全部
    /// 重绑成 <c>[x].*</c>，而整串的表位别名仍是 <c>AS [t0]</c>：同一句里列引用与新名配对、表位仍是
    /// 旧名，活库侧报"无法绑定多部分标识符"一类（不点 As() 时两侧都是 <c>[t0]</c>，这句是自洽的）。
    /// <b>钉现状不改形状</b>：改写 setOps.Ref（让新别名落到表位）或在链上当场拒 As() 属产品侧拍板，
    /// 翻格那天本用例改判。（注：断言按 2026-11-11 实发文本逐字核对后落定，诊断场
    /// <c>obj/logs/v41-closed-dialects-setops-probe.log</c>；首两版断言各红一场如实留痕——
    /// 裸 <c>[o]</c> 会误伤（腿本就合法引用旧名）、<c> AS [o]</c> 在实发里根本不存在；
    /// 钉"别名落位"要钉 <c>AS [新名]</c> 这一发射点与 <c>[新名].[列]</c> 的重绑两处。）
    /// </summary>
    [Fact]
    public void As_after_a_set_ops_chain_keeps_the_chained_from_alias()
    {
        var dropped = Paid(SqlServerOrm).UnionAll(Recent(SqlServerOrm)).As("x").ToSql();

        Assert.DoesNotContain(" AS [x]", dropped);
        Assert.Contains(" AS [t0]", dropped);
        Assert.Contains("[x].[Note]", dropped);

        var mismatched = Paid(SqlServerOrm).UnionAll(Recent(SqlServerOrm)).As("x").Where(x => x.Amount > 200m).ToSql();

        Assert.Contains("[x].[Amount]", mismatched);
        Assert.DoesNotContain(" AS [x]", mismatched);
        Assert.Contains(" AS [t0]", mismatched);
    }

    static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
