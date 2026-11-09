using QuickSql.Aop;
using QuickSql.Providers;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>单行出口三颗（<c>First</c>/<c>Single</c>/<c>SingleOrDefault</c>）与全称量词（<c>All</c>）的
/// <b>文本钉</b>（2026-11-01 补齐的四口，§5.7 P1 行记账销账）。
///
/// <para>钉三件事：① <c>First</c> 的句子上限是 1 —— 与 <c>FirstOrDefault</c> 同一条判重帽（<c>CappedAsync(1)</c>），
/// 差别只在空集那格，文本上零分岔；② <c>Single</c>/<c>SingleOrDefault</c> 的句子上限是 2 —— §5.7 预留的判重档，
/// "恰一行"的判据在 SQL 文本里唯一看得见的地方就是取数帽；③ <c>All</c> 发的是<b>计数</b>（谓词取反并入 WHERE，
/// 数出"不满足"的行），不是 EXISTS 反形 —— 全标准 COUNT，没有任何一家翻不动的形状。</para>
///
/// <para><b>行为那一半不在这里</b>：空集抛、多行抛、NULL 谓词按通过计，三条由集成册
/// <c>SingleFirstAllPathTests</c> 在真库上判；无驱动的桩在播报（<see cref="QuickSqlAop.Executing"/>）
/// <em>之后</em>才炸（与 <c>AnyProbeTests</c> 同一把量具），所以这里只有文本与参数 ——
/// 而取数帽恰好活在参数里（T-SQL 的 <c>FETCH NEXT @p</c>），断言正打在那一颗参数的值上。</para></summary>
public sealed class FirstSingleAllTests
{
    static (QuickSqlOrm Orm, List<Sql.DialectSql> Sent) Capturing()
    {
        var sent = new List<Sql.DialectSql>();

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = new QuickSqlAop { Executing = e => sent.Add(e.Sql) },
        });

        return (orm, sent);
    }

    /// <summary>①：First 发的句子与 FirstOrDefault 同一条帽（上限 1 行）—— T-SQL 的分页是
    /// OFFSET..FETCH，帽是<b>参数</b>（缺省偏移才是常量 0），所以断言打在那一颗参数的值上：
    /// 整句唯一的参数就是取数帽，值为 1。</summary>
    [Fact]
    public void First_caps_the_statement_at_one_row()
    {
        var (orm, sent) = Capturing();

        Assert.Throws<DriverNotAvailableException>(
            () => orm.Select<Order>().FirstAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        var text = Assert.Single(sent);

        Assert.Contains("FETCH NEXT ", text.Text, StringComparison.Ordinal);
        Assert.Contains("ROWS ONLY", text.Text, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(text.Parameters).Value);
    }

    /// <summary>②：Single 与 SingleOrDefault 发同一条判重帽（上限 2 行）—— 多行那格的差异
    /// 全在读侧行数判定，SQL 文本零分岔；帽同样落在那一颗参数的值上。</summary>
    [Fact]
    public void Single_and_SingleOrDefault_cap_the_statement_at_two_rows()
    {
        var (orm, sent) = Capturing();

        Assert.Throws<DriverNotAvailableException>(
            () => orm.Select<Order>().SingleAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Throws<DriverNotAvailableException>(
            () => orm.Select<Order>().SingleOrDefaultAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Equal(2, sent.Count);

        foreach (var text in sent)
        {
            Assert.Contains("FETCH NEXT ", text.Text, StringComparison.Ordinal);
            Assert.Equal(2, Assert.Single(text.Parameters).Value);
        }
    }

    /// <summary>③：All 发计数 —— COUNT(*) 加取反谓词，不发 EXISTS。原有谓词与取反谓词在
    /// 同一条 WHERE 里 AND 叠加（数的是"链上条件成立且不满足谓词"的行）。</summary>
    [Fact]
    public void All_counts_the_failing_rows_instead_of_emitting_exists()
    {
        var (orm, sent) = Capturing();

        Assert.Throws<DriverNotAvailableException>(
            () => orm.Select<Order>()
                     .Where(o => o.CustomerId == 7)
                     .AllAsync(o => o.Amount > 5m, TestContext.Current.CancellationToken)
                     .GetAwaiter().GetResult());

        var text = Assert.Single(sent);

        Assert.Contains("COUNT(*)", text.Text, StringComparison.Ordinal);
        Assert.Contains("NOT ", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTS", text.Text, StringComparison.Ordinal);
        Assert.Contains("Customer", text.Text, StringComparison.Ordinal);
    }
}
