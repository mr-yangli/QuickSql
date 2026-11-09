using QuickSql.Aop;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>分页在 <c>int.MaxValue</c> 那一档的活库半（闸口那一半在 <c>SqlServerPagingTests</c>）。
///
/// <para><b>事故的形状</b>（本轮的证人，真库 PostgreSQL）：消费侧那一族仓储把"取全部"的缺省写成
/// <c>pageSize = int.MaxValue</c>，于是"探下一页要多取一行"那一算（<c>size + 1</c>）溢成
/// <c>int.MinValue</c>，发出去的句子是 <c>LIMIT -2147483648</c>，库以
/// <c>2201W: LIMIT must not be negative</c> 收尾 —— 症状是一颗再普通不过的下拉列表 500。</para>
///
/// <para><b>为什么这一半只能在库里看</b>：溢出改的是<em>交出去的那颗数</em>，而离线那册没有一条
/// 句子真的上线。这里判的是"这一发真的跑完了、行数对得上、而且没有负 LIMIT"——
/// 三件事里只有第一件需要库。裁判用 AOP 收在册的语句文本与参数，不收被审那条链的返回值。</para></summary>
public sealed class PagingOverflowPathTests
{
    static CancellationToken ct => TestContext.Current.CancellationToken;

    /// <summary>页 1 的"取全部"：旧那一版在这里给库发一条负 LIMIT，而预容量那一档把进程按死。</summary>
    [Fact]
    public async Task Asking_for_every_row_at_the_int_ceiling_does_not_send_a_negative_limit()
    {
        Database.Ready();

        var sent = new List<(string Sql, string Parameters)>();
        var aop = new QuickSqlAop
        {
            Executing = e => sent.Add((e.Sql.Text, string.Join(',', e.Sql.Parameters.Select(p => p.Value?.ToString() ?? "null")))),
        };

        var page = await Database.OrmWith(aop).Select<Order>().ToPagedAsync(1, int.MaxValue, null, ct);

        // 一句上线：LIMIT 是正的页大小，不是溢出来的 int.MinValue。
        var statement = Assert.Single(sent);

        Assert.DoesNotContain("-2147483648", statement.Parameters, StringComparison.Ordinal);
        Assert.Contains("2147483647", statement.Parameters, StringComparison.Ordinal);

        // 探下一页那一行被摘掉了（size + 1 溢的就是那一行），于是 HasNext 在这一档恒 false。
        Assert.False(page.HasNext);
        Assert.Equal(int.MaxValue, page.Size);

        // 与不带分页的同一条链对得上行数 —— 这一句才是"读全了、没被预容量那一档吃掉"的证据。
        var all = await Database.Orm.Select<Order>().ToListAsync(ct);

        Assert.Equal(all.Count, page.Items.Count);
    }

    /// <summary>页 1 用默认档（每页几行）仍要多取一行探下一页：这一颗不许被上一颗顺手摘掉。</summary>
    [Fact]
    public async Task The_ordinary_page_still_peeks_one_row_more()
    {
        Database.Ready();

        var sent = new List<object?[]>();
        var aop = new QuickSqlAop { Executing = e => sent.Add([.. e.Sql.Parameters.Select(p => p.Value)]) };

        var page = await Database.OrmWith(aop).Select<Order>().OrderBy(o => o.Id).ToPagedAsync(1, 2, null, ct);

        var parameters = Assert.Single(sent);

        // 上线的那两颗是"这一页两行 + 探下一页那一行"与"页 1 的偏移"。
        // <b>按值比对而不按位序</b>：T-SQL 那一族的句子是 <c>OFFSET … FETCH NEXT …</c>，两颗参数在前；
        // 另几家是 <c>LIMIT … OFFSET …</c>，颗序正好相反。位序写死在断言里，同一句判据就只有一族绿，
        // 而这一格要判的是"那一行有没有多取"，与哪家先写哪颗无关。
        Assert.Equal(2, parameters.Length);
        Assert.Contains(3L, parameters.Select(Convert.ToInt64));
        Assert.Contains(0L, parameters.Select(Convert.ToInt64));
        Assert.True(page.Items.Count <= 2);

        if (page.Items.Count == 2)
            Assert.True(page.HasNext, "取满一整页还不报有下一页，说明探那一行被摘到了不该摘的档上");
    }
}
