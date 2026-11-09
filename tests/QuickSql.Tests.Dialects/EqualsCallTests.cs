using System.Data;
using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary><c>Equals</c> 那一族落 <c>=</c>（v0.39，读侧谓词）。
///
/// <para><b>这一格是从消费侧的真红抄来的</b>：ABP 那一类仓储里 <c>await repo.FindAsync(x =&gt; x.Id.Equals(id))</c>
/// 是常态写法，而 <c>Id</c> 是 <c>long</c> / <c>Guid</c> 时 C# 绑到的是 <c>Int64.Equals(long)</c> 或
/// <c>Object.Equals(object)</c>（量那一侧还多一层装箱 <c>Convert</c>）。旧那一支只认
/// <c>declaring == typeof(string)</c>，于是整族落到"为 <c>Object.Equals</c> 注册映射"那句上 ——
/// 而那句话指错了地方：缺的不是方言模板，<c>=</c> 就是它的落点。本文件钉的是三件事：
/// ① 每一族的产出与<em>同一个比较写成 <c>==</c></em> 时逐字节相同（连参数值一起比），
/// ② 空值那一格<em>故意</em>不折成 <c>IS NULL</c>，③ 两种"照 <c>=</c> 发射就换了语义"的形状各自拒在自己的理由上。</para>
///
/// <para><b>③ 那一格是本文件最该留下的东西</b>：装进 <c>object</c> 的两个值只有<em>同型</em>才相等，所以
/// <c>intCol.Equals(someLong)</c> 在 CLR 上恒为 false —— 而同样这一对写成 <c>intCol == someLong</c> 时
/// 编译器会替调用方提升到 <c>long</c>，于是它翻得成、而且应该翻得成。<b>两种写法的差别不是实现口径，
/// 是 CLR 语义</b>，所以 <c>==</c> 那条路上豁免的整型加宽与 <c>enum</c>↔底层型（<c>RepresentationOnly</c>）
/// 在这一族必须拒：照 <c>=</c> 发射会把"一行都不该中"翻成"值相同就中"，那是静默给错结果，比报错坏。</para>
///
/// <para><b>本文件没有覆盖的两格，别读成覆盖过了</b>：真库上"<c>=</c> 与参数绑上去真能读回那一行"的见证在
/// 集成册 <c>ReadPathTests</c>（五家各跑一遍），而 <c>StringComparison</c> 那一格<em>能不能</em>下推
/// （各家 <c>COLLATE</c> 口径）今天没有结论 —— 这里是拒，不是"已证不行"。</para></summary>
public sealed class EqualsCallTests
{
    /// <summary>逐族钉"与 <c>==</c> 同形"。<b>两家方言各跑一遍</b>：这一族的落点是 <c>=</c>，
    /// 与引号/占位符口径无关，而跑两家正是为了钉住这句"无关" —— 哪天有人把 <c>Equals</c>
    /// 接到某个方言模板上，这一条会在第二家红出来。</summary>
    [Fact]
    public void Every_value_family_lands_on_the_same_equality_as_the_operator()
    {
        long big = 7;
        long? boxed = 7;
        var key = Guid.NewGuid();
        Guid? boxedKey = key;
        var state = OrderState.Paid;
        var moment = new DateTime(2026, 1, 2, 3, 4, 5);
        var name = "上海";

        foreach (var orm in new[] { SqlServer.Orm(), MySql.Orm() })
        {
            // long 那一族：本家的整型列与"量是可空、树上多一层装箱"的两形（消费侧那一颗真红是后者）。
            Same(orm.Select<RepresentationProbe>().Where(p => p.Big == big),
                 orm.Select<RepresentationProbe>().Where(p => p.Big.Equals(big)));
            Same(orm.Select<RepresentationProbe>().Where(p => p.Big == boxed),
                 orm.Select<RepresentationProbe>().Where(p => p.Big.Equals(boxed)));

            // Guid 那一族：同两形。可空那一形 C# 绑的是 Object.Equals，与 long 那一形绑的 Int64.Equals 不同支。
            Same(orm.Select<SessionToken>().Where(t => t.Id == key),
                 orm.Select<SessionToken>().Where(t => t.Id.Equals(key)));
            Same(orm.Select<SessionToken>().Where(t => t.Id == boxedKey),
                 orm.Select<SessionToken>().Where(t => t.Id.Equals(boxedKey)));

            // 其余各族：枚举、decimal、可空 DateTime、字符串、列比列，以及静态那颗 object.Equals(a, b)。
            Same(orm.Select<Order>().Where(o => o.State == state),
                 orm.Select<Order>().Where(o => o.State.Equals(state)));
            Same(orm.Select<Order>().Where(o => o.Amount == 100m),
                 orm.Select<Order>().Where(o => o.Amount.Equals(100m)));
            Same(orm.Select<Order>().Where(o => o.PaidAt == moment),
                 orm.Select<Order>().Where(o => o.PaidAt.Equals(moment)));
            Same(orm.Select<Order>().Where(o => o.CustomerName == name),
                 orm.Select<Order>().Where(o => o.CustomerName!.Equals(name)));
            Same(orm.Select<Order>().Where(o => o.CustomerId == o.Id),
                 orm.Select<Order>().Where(o => o.CustomerId.Equals(o.Id)));
            Same(orm.Select<RepresentationProbe>().Where(p => p.Big == big),
                 orm.Select<RepresentationProbe>().Where(p => object.Equals(p.Big, big)));
        }
    }

    /// <summary>产出逐字节相同，<b>而且参数值也逐颗相同</b>：只比文本比不出"枚举那一颗绑上去的是
    /// <c>OrderState.Paid</c> 还是 <c>2</c>"这类差别，而绑定层正是这一族唯一还没被文本覆盖到的那半截。</summary>
    static void Same<TEntity>(IQuery<TEntity> expected, IQuery<TEntity> actual)
    {
        Assert.Equal(expected.ToSql(out var expectedArgs), actual.ToSql(out var actualArgs));
        Assert.Equal(expectedArgs.Select(p => p.Value), actualArgs.Select(p => p.Value));
    }

    /// <summary>空值那一格<b>故意</b>不折成 <c>IS NULL</c>，而这一格是语义问题不是观感问题：
    /// CLR 上 <c>x.Equals(null)</c> 恒为 false（<c>Nullable&lt;T&gt;.Equals(null)</c> 也是 false），
    /// 而 <c>col = @p</c>（<c>@p</c> 为 NULL）在库里是未知、行被滤掉 —— 与 false 同值。
    /// 折成 <c>IS NULL</c> 反而是把"永不中"翻成"空的那些行都中"。对照那一行钉的是
    /// <c>== null</c> 确实折 <c>IS NULL</c>：两种写法在 CLR 上就是两件事，所以在这库里也是两件事。</summary>
    [Fact]
    public void Equals_on_a_null_is_equality_against_null_and_not_a_fold_into_is_null()
    {
        var orm = SqlServer.Orm();

        var text = orm.Select<Order>().Where(o => o.CustomerName!.Equals(null)).ToSql(out var args);

        Checks.Has("[t0].[CustomerName] = @p0", text);
        Assert.Equal(DBNull.Value, Assert.Single(args).Value);   // 书写器把 null 落成 DBNull，不是 CLR 的 null

        // 可空值类型那一侧同档：旧那一支连这一形都拒（它只认 string），今天它与字符串那一族同值。
        Checks.Has("[t0].[PaidAt] = @p0", orm.Select<Order>().Where(o => o.PaidAt.Equals(null)).ToSql());

        // 对照：== null 折 IS NULL，那一格有它自己的路（Binary 里那颗折叠），与这一族无关。
        Checks.Has("[t0].[CustomerName] IS NULL", orm.Select<Order>().Where(o => o.CustomerName == null).ToSql());
    }

    /// <summary>型不配即拒，<b>而且拒在自己的理由上</b>：旧那句"为 <c>Object.Equals</c> 注册映射"
    /// 会把人引去注册一个方言模板，而这一格缺的不是模板 —— 照 <c>=</c> 发射是静默给错结果。
    /// 消息里要读出两侧各是什么型，否则调用方只看到"翻不了"，看不到"你那句在 CLR 上恒为 false"。</summary>
    [Fact]
    public void A_pair_the_clr_would_answer_false_to_is_refused_with_both_type_names()
    {
        long big = 7;
        var orm = SqlServer.Orm();

        var intAgainstLong = Checks.Raised<UntranslatableExpressionException>(
            () => orm.Select<Order>().Where(o => o.Id.Equals(big)).ToSql());

        Assert.Contains("Int32 ∶ Int64", intAgainstLong.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("注册映射", intAgainstLong.Message, StringComparison.Ordinal);

        // 同一档的另两形：Guid 列 vs long 量、字符串列 vs long 量（消费侧那颗真红的形状之一）。
        Assert.Contains("Guid ∶ Int64", Checks.Raised<UntranslatableExpressionException>(
            () => orm.Select<SessionToken>().Where(t => t.Id.Equals(big)).ToSql()).Message, StringComparison.Ordinal);
        Assert.Contains("String ∶ Int64", Checks.Raised<UntranslatableExpressionException>(
            () => orm.Select<Order>().Where(o => o.CustomerName!.Equals(big)).ToSql()).Message, StringComparison.Ordinal);

        // 对照：同样这一对写成 == 时编译器提升到 long，于是它翻得成 —— 差别在 CLR，不在本库的口径。
        Checks.Has("[t0].[Id] = @p0", orm.Select<Order>().Where(o => o.Id == big).ToSql());
    }

    /// <summary>带"相等规则"的那一颗拒在自己的理由上（与 <c>Contains</c> 带自定义比较器同一条口径，§5.8）：
    /// 规则下推不到库里，照默认语义发射就是把调用方显式指定的相等规则偷偷换掉。
    /// <b>仍然拒、但拒在"注册映射"那一句上的一格</b>：<c>EqualityComparer&lt;T&gt;.Default.Equals(a, b)</c>
    /// 是实例<em>二元</em>，不被这一族认下（认下它就得先决定"比较器实例本身怎么翻"），本笔没有开它。</summary>
    [Fact]
    public void An_equality_rule_argument_is_refused_on_its_own_reason()
    {
        var name = "上海";
        var orm = SqlServer.Orm();

        var withRule = Checks.Raised<UntranslatableExpressionException>(
            () => orm.Select<Order>().Where(o => o.CustomerName!.Equals(name, StringComparison.OrdinalIgnoreCase)).ToSql());

        Assert.Contains("StringComparison", withRule.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("注册映射", withRule.Message, StringComparison.Ordinal);

        // 大小写不敏感那条今天有路可走：两侧都写 ToLower()，产出是 LOWER(a) = LOWER(b)。
        Checks.Has("LOWER(", orm.Select<Order>().Where(o => o.CustomerName!.ToLower() == name.ToLower()).ToSql());

        Assert.ThrowsAny<Exception>(
            () => orm.Select<Order>().Where(o => EqualityComparer<string>.Default.Equals(o.CustomerName!, name)).ToSql());
    }
}
