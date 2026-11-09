using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>按<em>成员名</em>排序（v0.38 第二十笔）在<b>离线</b>能判的那一半。
///
/// <para>这一颗口的全部价值在一个不对称上：传进来的字符串<em>不参与</em>写 SQL，它只是拿去契约上认列的钥匙，
/// 认出来发的是那一颗列加方言引号，认不出当场抛。所以"注入面"在这里不是一个要过滤的风险，而是没有对应
/// 代码路径的一形 —— 这一点离线就判得动（判的正是文本），不必等活库。</para>
///
/// <para>判不到的那一半归集成层（<c>OrderingPathTests</c>）：那一头量的是<em>行真的按这一列排了序</em>，
/// 而文本相等不等于排序正确 —— 这里断言的是"发出去的是哪一颗列"，序对不对只有库能答。</para>
///
/// <para><b>v0.39 起这一颗口吃的是<em>一整串</em></b>（<c>"Id,Name desc"</c>，前端查询串上那一种形状），
/// 于是下面几颗各钉一个新面：一次给一串与逐颗接链逐字节同形、那颗 bool 退成"没点方向的颗的方向"、
/// 脏分隔与空颗、认不出方向词的整段留作名字、以及 <c>desc</c> 打头的那一形注入仍然响在同一处。</para></summary>
public sealed class MemberOrderingTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => SqlServer.Orm();

    /// <summary>带 Fluent 配置的那一台：列名那一档与"撞名"那一档都要靠改列名才造得出来，
    /// 而改的是<em>这台 orm 的解析输入</em>，不是往共享实体上加注解（那份实体是全仓库共用的列序基准）。</summary>
    static QuickSqlOrm ormWith(ModelConfiguration model)
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));

    // ── 文本：认出来的那一颗列，以及"与 lambda 形态逐字相等" ──────────

    /// <summary>本笔的主判据：<b>成员名那条口发出来的 SQL 与 lambda 那条逐字节相等</b>。
    /// 这一句比"断言里写着列名"有力 —— 它钉的是"两条口共用同一份发射"，而"共用"正是
    /// §5.5 那句"算子只实现一次"在排序这一格的全部内容；谁另写一份字符串拼接，这一句当场红。</summary>
    [Fact]
    public void A_member_name_emits_the_same_text_the_lambda_emits()
    {
        var byName = orm().Select<Order>().OrderBy("CreatedAt", descending: true).ThenBy("Id");
        var byLambda = orm().Select<Order>().OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id);

        Assert.Equal(byLambda.ToSql(), byName.ToSql());
        Checks.Has("ORDER BY [t0].[CreatedAt] DESC, [t0].[Id] ASC", byName.ToSql());
    }

    /// <summary>认列的三档之二、之三：<b>列名</b>（Fluent 改过名的那一颗）与<b>大小写不敏感的唯一命中</b>
    /// （HTTP 查询串上过来的是 <c>createdAt</c> 那一形）。这两档分开看才有意义：前者说明"认的是契约里的列名，
    /// 不是 CLR 属性名"，后者说明"给 camelCase 留了门，但只留一条缝"。</summary>
    [Fact]
    public void The_column_name_tier_and_the_case_insensitive_tier_each_answer()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.Amount).HasColumnName("amount_due");

        var renamed = ormWith(model).Select<Order>().OrderBy("amount_due");

        Checks.Has("ORDER BY [t0].[amount_due] ASC", renamed.ToSql());

        // 逐字节的两次命中都失败（属性名是 Amount、列名是 amount_due），第三档才接住它。
        Checks.Has("ORDER BY [t0].[Amount] ASC", orm().Select<Order>().OrderBy("amount").ToSql());
        Checks.Has("ORDER BY [t0].[CreatedAt] ASC", orm().Select<Order>().OrderBy("createdAt").ToSql());
    }

    // ── 拒绝：认不出的一律当场响，且消息点名那颗字符串 ────────────────

    /// <summary>五颗都归"不是列"：拼错的名字、导航属性、带点的成员路径、SQL 片段、空串。
    /// <b>逐颗断言消息</b>是这一族的交付物本身 —— 一条 <c>"invalid order key"</c> 半年后什么都说不出（§5.8）。
    /// 那颗注入用的字符串要的读数不是"被转义了"，而是<em>它根本没进句子</em>。</summary>
    [Fact]
    public void Nothing_that_is_not_a_column_of_this_table_can_be_an_order_key()
    {
        var query = orm().Select<Order>();

        Checks.Has("QuickSqlOrder 没有名为 CreatedA 的列",
            Checks.Raised<QuickSqlException>(() => query.OrderBy("CreatedA")).Message);

        Checks.Has("导航属性、投影列与 SQL 片段都不认",
            Checks.Raised<QuickSqlException>(() => query.OrderBy("Customer")).Message);

        // 那一档的退路是 lambda 而不是这句字符串：字符串按名字认列，而名字里没有"哪张表"这件事，
        // §5.2 的自动联表只在 lambda 形态上才有的读（见证 NavigationAutoJoinTests）。
        Checks.Has("请改用 lambda 形态",
            Checks.Raised<QuickSqlException>(() => query.OrderBy("Customer.Name")).Message);

        var injected = Checks.Raised<QuickSqlException>(
            () => query.OrderBy("CreatedAt; DROP TABLE QuickSqlOrder--").ToSql());

        // 消息把那颗字符串原样念出来 —— 这是"它被当成一个名字去认，而不是被当成一段句子"的读数。
        Checks.Has("DROP TABLE QuickSqlOrder", injected.Message);

        Checks.Has("排序成员名是空的",
            Checks.Raised<QuickSqlException>(() => query.OrderBy("   ")).Message);
    }

    /// <summary>撞名那一档：大小写不敏感命中两颗列时<em>不替调用方挑一颗</em>，消息把候选列点名列出。
    /// 造形用的是同一个改名列的两种写法（<c>amount</c> 与 <c>Amount</c>）—— 真实项目里"蛇形与驼峰混着改列名"
    /// 就会撞出这一格，而它必须响。
    ///
    /// <para><b>同一份夹具还钉住逐字节的那两档各自的位置</b>：<c>"amount"</c> 只有列名那一档认（属性名是 Amount），
    /// <c>"Amount"</c> 只有属性名那一档认——而两份在第三档都是<em>两颗命中</em>。所以前两档不是第三档的加速器，
    /// 是<em>消歧的优先级</em>：掐掉任何一档，对应那一句会从"认出列"变成"撞名而响"，本用例因此是有牙的。</para></summary>
    [Fact]
    public void An_ambiguous_name_refuses_instead_of_choosing()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.Amount).HasColumnName("amount");
        model.Entity<Order>().Property(o => o.PaidAt).HasColumnName("Amount");

        var query = ormWith(model).Select<Order>();

        // 逐字节的两档各判一次，而且判的是同一颗列：<c>"amount"</c> 走列名那一档，<c>"Amount"</c> 走属性名那一档
        // （属性名 Amount 的列已经被改成 amount）。两档都不是第三档的加速器 —— 第三档在这一份夹具上是两颗命中，
        // 掐掉任何一档，对应那一句都会从"认出列"变成"撞名而响"。
        Checks.Has("ORDER BY [t0].[amount] ASC", query.OrderBy("amount").ToSql());
        Checks.Has("ORDER BY [t0].[amount] ASC", query.OrderBy("Amount").ToSql());

        var failure = Checks.Raised<QuickSqlException>(() => query.OrderBy("AMOUNT").ToSql());

        Checks.Has("能对上 AMOUNT 的列不止一颗", failure.Message);
        Checks.Has("Amount / PaidAt", failure.Message);
    }

    // ── 与"投影之后不能再排序"那道闸门的关系 ─────────────────────────

    /// <summary>投影之后列契约就没了，两条口要给出同一句话。这一颗落在 <see cref="GuardrailTests"/>
    /// 那一份的隔壁：那里钉的是 lambda 那一条，这里钉的是"字符串这一条没有绕过同一道闸门"。</summary>
    [Fact]
    public void A_projected_query_refuses_a_member_name_the_same_way_it_refuses_a_lambda()
    {
        var projected = orm().Select<Order>().Select(o => new { o.Id, Money = o.Amount });

        var failure = Checks.Raised<QuickSqlException>(() => projected.OrderBy("Id").ToSql());

        Checks.Has("投影之后不能再排序", failure.Message);
    }

    // ── 整串（v0.39∶ 一颗口吃下前端那一串写法） ──────────────────────

    /// <summary>本笔的主判据：<b>一次给一串，与逐颗接链逐字节同形</b>。这一句钉的还是上一笔那句
    /// "两条口共用同一份发射"，只是这一回进去的是一颗串而不是一个名字。
    /// 判据里 <c>"Id,Id desc"</c> 是需求方原样给的形状 —— 同一颗列排两次在库里合法而冗余，
    /// 本家不替调用方去重，也不按名字猜他要哪一颗。</summary>
    [Fact]
    public void A_spec_string_lands_exactly_the_chain_of_calls_it_spells()
    {
        Checks.Has("ORDER BY [t0].[CreatedAt] ASC, [t0].[Amount] DESC",
            orm().Select<Order>().OrderBy("CreatedAt,Amount desc").ToSql());

        var bySpec = orm().Select<Order>().OrderBy("Id,Id desc");
        var byCall = orm().Select<Order>().OrderBy(o => o.Id).ThenByDescending(o => o.Id);

        Assert.Equal(byCall.ToSql(), bySpec.ToSql());
    }

    /// <summary>那颗形参从"这一颗的方向"变成"串里没点方向的那些颗的方向"，三格各钉一次：
    /// 单颗的老写法逐字不变（第一格是本笔的<em>不回归</em>判据）、后缀盖过形参（形参不许把调用方
    /// 显式写的方向偷偷翻回来）、没点名的颗吃形参。</summary>
    [Fact]
    public void The_flag_is_only_the_default_for_the_keys_that_name_no_direction()
    {
        Checks.Has("ORDER BY [t0].[Amount] DESC", orm().Select<Order>().OrderBy("Amount", true).ToSql());
        Checks.Has("ORDER BY [t0].[Amount] DESC", orm().Select<Order>().OrderBy("Amount desc").ToSql());
        Checks.Has("ORDER BY [t0].[Amount] ASC", orm().Select<Order>().OrderBy("Amount asc", true).ToSql());

        Checks.Has("ORDER BY [t0].[CreatedAt] DESC, [t0].[Amount] DESC",
            orm().Select<Order>().OrderBy("createdAt DESC, Amount", true).ToSql());
    }

    /// <summary>脏写法只当分隔不当名字：空颗（前端 <c>join(',')</c> 的尾巴）、连排空格、换行。
    /// 而 <c>",,,"</c> 与 <c>"   "</c> 归到同一句空名的读数上 —— 拆完是空清单与根本没给，
    /// 对调用方是同一件事，消息不许有两份。</summary>
    [Fact]
    public void Blanks_and_separators_are_not_order_keys()
    {
        Checks.Has("ORDER BY [t0].[Id] ASC, [t0].[Amount] DESC",
            orm().Select<Order>().OrderBy(" Id ,  Amount  desc , ").ToSql());

        Checks.Has("ORDER BY [t0].[Id] ASC, [t0].[PaidAt] ASC",
            orm().Select<Order>().OrderBy("Id,\nPaidAt").ToSql());

        Checks.Has("排序成员名是空的",
            Checks.Raised<QuickSqlException>(() => orm().Select<Order>().OrderBy(",,")).Message);
    }

    /// <summary>认不出方向词的<em>整段</em>按列名去认，两处读数各钉一次。① 单独一颗 <c>"desc"</c> 指的
    /// 就是那一颗名叫 desc 的列（夹具用 Fluent 改列名造出来），本笔不把它读成方向 —— 否则"哪颗列叫
    /// desc"这种实体一出现，按名字排就少认一颗列。② <c>"Id down"</c> 里 <c>down</c> 不是方向词，
    /// 于是整段当名字、认不出、响，而消息教一句只认哪两颗词。
    /// 这一颗钉的是"串里没有猜"：把 <c>down</c> 读成降序就是替调用方改了他的键。</summary>
    [Fact]
    public void A_word_that_is_not_a_direction_stays_part_of_the_name()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.Amount).HasColumnName("desc");

        Checks.Has("ORDER BY [t0].[desc] ASC", ormWith(model).Select<Order>().OrderBy("desc").ToSql());

        var failure = Checks.Raised<QuickSqlException>(() => orm().Select<Order>().OrderBy("Id down").ToSql());

        Checks.Has("没有名为 Id down 的列", failure.Message);
        Checks.Has("只认挂在名字后面的 asc / desc", failure.Message);
    }

    /// <summary><c>ThenBy</c> 那一颗同样吃整串，而且一颗都不许顶掉前面已经排好的（列序 = 调用序）。
    /// 末尾那一句钉的是新面上的注入形：<c>desc</c> 打头的那一颗串今天有了一半"看起来像方向"的外壳，
    /// 读数照旧是<em>它根本没进句子</em>。</summary>
    [Fact]
    public void ThenBy_appends_every_key_the_string_carries_and_the_injection_shape_still_refuses()
    {
        var bySpec = orm().Select<Order>().OrderBy("Id").ThenBy("Amount desc, PaidAt");
        var byLambda = orm().Select<Order>().OrderBy(o => o.Id).ThenByDescending(o => o.Amount).ThenBy(o => o.PaidAt);

        Assert.Equal(byLambda.ToSql(), bySpec.ToSql());

        var injected = Checks.Raised<QuickSqlException>(
            () => orm().Select<Order>().OrderBy("Amount desc; DROP TABLE QuickSqlOrder--").ToSql());

        Checks.Has("DROP TABLE QuickSqlOrder", injected.Message);
    }

    /// <summary>那颗串可以是 <c>null</c>：HTTP 查询参数没给的就是这一形（门面把 <c>PagedRequestInput.Order</c>
    /// 原样递过来时，"没排序"与"排错了"是两种读数，而它们都不该是 <c>NullReferenceException</c>）。
    /// 契约上写的是非空 <c>string</c>，所以这一颗是给<em>外部来的串</em>留的最后一档：响在认列那一句上，
    /// 与空串同一句话。<c>null!</c> 是这里故意的抑制 —— 让编译器那一头仍按 nullable 警告，库这一头不许崩。</summary>
    [Fact]
    public void A_null_sort_string_answers_the_same_sentence_an_empty_one_does()
    {
        var failure = Checks.Raised<QuickSqlException>(() => orm().Select<Order>().OrderBy(null!).ToSql());

        Checks.Has("排序成员名是空的", failure.Message);
    }
}
