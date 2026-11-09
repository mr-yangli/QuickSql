using QuickSql.Aop;
using QuickSql.Providers;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>存在性探针的文本钉（§5.12 记名的 P1、<c>QueryEngine.AnyAsync</c> 那一格落地）。
///
/// <para><b>钉的是什么</b>：<c>Any</c> 发的是「<c>SELECT 1 … 取第一行</c>」而不再是 <c>COUNT(*)</c> ——
/// 差别是算法级的（COUNT 在大表上是全量计数，存在性只要库交出第一行就收口），而它在文本上只有这一处
/// 看得见。<b>为什么能离线证</b>：执行前的播报（<see cref="QuickSqlAop.Executing"/>）排在取连接之前，
/// 无驱动的桩在播报<em>之后</em>才炸，所以那一句话已经进了账本 —— 与 <c>AopExecutingTests</c>
/// 同一把量具，只是这里要的是文本不是顺序。</para>
///
/// <para><b>行为那一半不在这里</b>：有行答 true、空集答 false、带页问的是那一页，三条由集成册
/// <c>ReadPathTests.Any_and_LongCount_answer_on_the_same_rows_the_count_family_reads</c> 在真库上判；
/// 这一格只判"发出去的形状是探针不是计数"。</para></summary>
public sealed class AnyProbeTests
{
    [Fact]
    public void Any_sends_a_one_row_probe_instead_of_a_count()
    {
        var sent = new List<string>();

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = new QuickSqlAop { Executing = e => sent.Add(e.Sql.Text) },
        });

        Assert.Throws<DriverNotAvailableException>(
            () => orm.Select<Order>().AnyAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        var text = Assert.Single(sent);

        Assert.StartsWith("SELECT 1 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("COUNT", text, StringComparison.Ordinal);
    }
}
