using System.Data;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>参数名"文本一颗 / 驱动一颗"的携带（v0.34 第二笔，§14 三条省法的 ①+② 一起落地）。
///
/// <para>背景一句话：<c>AdoExecutor.Bind</c> 从 v0.32 起对<em>所有家</em>摘参数前缀，那是虚谷一台真库逼出来的
/// （交 <c>":p0"</c> 得 E10036），代价是每颗参数一次 <c>Substring</c>，被 §12.8 五枚钉量成实数。
/// 本文件钉的是把这份代价收回到"必须付的那几家"的两颗新口：方言侧的声明位
/// （<see cref="ISqlDialect.DriverAcceptsPrefixedParameterNames"/>，只有拿到带前缀活库见证的 SQL Server 为 true）
/// 与参数侧的载体（<see cref="AdoParameter.DriverName"/>，provider 点名给驱动的名字）。</para>
///
/// <para><b>这里不测 <c>Bind</c> 本体</b>：离线侧没有假 ADO 栈（§14 记着），摘/不摘的最终判据归
/// §13.4 那 95×3 场的活库复跑；本文件钉的是两颗口的<em>声明形状</em>，门禁钉的是它的价。</para></summary>
public sealed class ParameterNameCarriageTests
{
    [Fact]
    public void Exactly_the_one_family_with_two_prefixed_name_eras_declares_it_accepts_them()
    {
        // 1 true ∶ 5 false。true 那一颗的凭据是<em>两段</em>历史都在同一台库上绿过：
        // 带前缀 v0.19–v0.31（两台 SQL Server 实例、95 条），摘前缀 v0.32–v0.34（同一批）。
        // 其余五家各有所因：虚谷<em>必须</em>摘（E10036 是它的读数）；金仓、MySQL 血统、Oracle 血统
        // 的"两种都认"停在 v0.32 那句观察、没有各自的活库复点 —— 没有实测就不改默认（§9.1 三句第 ② 句）。
        // v0.37 第一笔添的那一户是这一格里<em>第六种情形</em>：本家没有"两种都认"这个问题可问，
        // 因为它的占位符只有位置形（<c>?</c>），名字压根不进文本 —— 见下面那一条。
        Assert.True(SqlServer.Orm().Provider.Dialect.DriverAcceptsPrefixedParameterNames);

        Assert.False(Kingbase.Orm().Provider.Dialect.DriverAcceptsPrefixedParameterNames);
        Assert.False(Dameng.Orm().Provider.Dialect.DriverAcceptsPrefixedParameterNames);
        Assert.False(MySql.Orm().Provider.Dialect.DriverAcceptsPrefixedParameterNames);
        Assert.False(Xugu.Orm().Provider.Dialect.DriverAcceptsPrefixedParameterNames);
        Assert.False(Gbase8s.Orm().Provider.Dialect.DriverAcceptsPrefixedParameterNames);
    }

    [Fact]
    public void A_positional_family_pays_the_price_it_never_reads()
    {
        // 上面那五颗 false 里有四颗是"还没复点所以留在摘的那边"，一颗是"必须摘"；本家这第六颗
        // <b>取值改不动任何产出</b>：摘与不摘差的是给驱动的那一颗名字，而位置形绑定按序号配对，
        // 那两颗名字（<c>p0</c> 与 <c>?p0</c>）在本家连文本都不进。于是这颗开关在本家是<b>惰性</b>的，
        // 而它仍然写 false 而不是留空：那五家的分布要靠它可读，这一格要靠这条断言不被人后来"顺手改成 true"。
        var dialect = Gbase8s.Orm().Provider.Dialect;
        var sql = Gbase8s.Orm().Select<Order>().Where(o => o.Id == 7).ToSql(out var parameters);

        Assert.Equal('?', dialect.ParameterPrefix);
        Assert.False(dialect.DriverAcceptsPrefixedParameterNames);

        // 登记侧仍带着前缀造名（共用层那一份编号纪律一个字没改），只是这颗名在文本里看不见。
        Assert.StartsWith("?", Assert.Single(parameters).Name, StringComparison.Ordinal);
        Assert.DoesNotContain("p0", sql, StringComparison.Ordinal);
        Assert.Contains("= ?", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_carrier_defaults_to_silent_while_the_text_side_keeps_reading_only_name()
    {
        var plain = new AdoParameter("@p0", 7, DbType.Int32, 0);

        // 五家 binder 今天全发 null —— "provider 没表态"是默认档，摘不摘交给方言那颗声明位。
        Assert.Null(plain.DriverName);

        // 位置构造（4/5 颗实参）不破：新载体挂在末尾。
        var hinted = new AdoParameter("@p0", 7, DbType.Int32, 0, null);

        Assert.Null(hinted.DriverName);

        var declared = plain with { DriverName = "p0" };

        Assert.Equal("p0", declared.DriverName);

        // 文本一颗没动：写进 SQL 的仍是 Name 那颗占位符，载体只活到执行器那一步。
        var writer = new SqlWriter(new(), [], '@');

        writer.AppendParameter(declared);

        Assert.Equal("@p0", writer.ToSqlString());
        Assert.Same(declared, Assert.Single(writer.Parameters));
    }
}
