using QuickSql.Query;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>树形闭包（Descendants / Ancestors / ToTree）在<b>离线</b>能判的那一半。
///
/// <para><b>这一档离线判的是什么</b>：三颗口的取数都不要求库端有递归语法，所以发出去的仍然是普通
/// SELECT —— 离线这一半钉的就是这件事本身：<em>锚点句是一颗主键 IN 的普通句子</em>（19 家逐家发得出、
/// 文本里没有 WITH RECURSIVE / CONNECT BY 那一类库端树语），以及挂错形状在<em>接链那一刻</em>就响。
/// 闭包语义（Depth、环、孤儿、过滤器断链、句数账）要真行才判得出来，那一半在
/// <c>TreePathTests</c>（集成册，六家活库）。</para>
///
/// <para><b>为什么拒档要在离线钉</b>：这些形状发出去不会报错，只会交回一棵看着像树的残段
/// （投影之后没有实体可挂、分页截断闭包、叠上急切加载之后句数读不出账）。错在结果里看不出形状，
/// 所以它只能响在链上——与 IncludeMany/AsSplitQuery 那一对双向护栏同一手法。</para></summary>
public sealed class TreeQueryTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    // ── 出口形状 ────────────────────────────────────────────────────────

    /// <summary>锚点句 = 主键 IN 锚点集的普通 SELECT：没有库端树语，参数一颗锚点一个。
    /// 这一颗是"19 家通用"的根据——它发的东西与任何一句带 IN 的查询同形。</summary>
    [Fact]
    public void Descendants_anchor_sentence_is_a_plain_primary_key_in_select()
    {
        var plan = orm().Select<OrgNode>().Descendants(n => n.ParentId, 42, 43).Explain();

        Assert.Contains(" IN ", plan.Sql.Text);
        Assert.DoesNotContain("RECURSIVE", plan.Sql.Text);
        Assert.DoesNotContain("CONNECT BY", plan.Sql.Text);
        Assert.Equal(2, plan.Sql.Parameters.Count);

        // 锚点句按主键认，不按父键认：父键臂是第二句起才出现的。
        Assert.DoesNotContain("ParentId", plan.Sql.Text.Split("WHERE")[1]);
    }

    /// <summary>父级档与子集档的锚点句逐字同形：方向翻的是后续每一句的 IN 臂落在哪颗列上，
    /// 第一句都是"把锚点那几行取回来"。两档第一句不同形就说明方向被提前烧进了锚点句。</summary>
    [Fact]
    public void Ancestors_and_Descendants_share_the_same_anchor_sentence()
    {
        var down = orm().Select<OrgNode>().Descendants(n => n.ParentId, 42).ToSql();
        var up = orm().Select<OrgNode>().Ancestors(n => n.ParentId, 42).ToSql();

        Assert.Equal(down, up);
    }

    /// <summary>树形意图进规范文本：两条 SQL 逐字相同而树口不同的链交回的是两棵不同的闭包，
    /// 等价性判据必须分得开它们（这与 IncludeMany 不进文本不同档——那一颗只往同一批行上缝孩子）。</summary>
    [Fact]
    public void Tree_intent_is_visible_in_the_model_text()
    {
        var plain = orm().Select<OrgNode>().Where(n => n.TenantId == 7).Explain().ModelText;
        var sliced = orm().Select<OrgNode>().Where(n => n.TenantId == 7).Descendants(n => n.ParentId, 42).Explain().ModelText;

        Assert.DoesNotContain("TREE", plain);
        Assert.Contains("TREE Descendants parent=ParentId anchors=1[42] depth=32 self=True", sliced);
        Assert.NotEqual(plain, sliced);
    }

    /// <summary>两颗旋钮改的是模型不是锚点句：深度与含否自身决定后续爬几层、锚点要不要交回，
    /// 第一句的形状一个字不动。</summary>
    [Fact]
    public void Depth_and_IncludeSelf_change_the_model_but_not_the_anchor_sentence()
    {
        var bare = orm().Select<OrgNode>().Descendants(n => n.ParentId, 42);
        var tuned = bare.Depth(5).IncludeSelf(false);

        Assert.Equal(bare.ToSql(), tuned.ToSql());
        Assert.Contains("depth=5 self=False", tuned.Explain().ModelText);
        Assert.Contains("depth=32 self=True", bare.Explain().ModelText);

        // 不可变：派生不回写原链（与链式对象同一纪律）。
        Assert.DoesNotContain("depth=5", bare.Explain().ModelText);
    }

    /// <summary>链上 Where 与树口共存时，谓词进锚点句：闭包行与锚点行同一份口径
    /// （代价写在册子里——被筛掉的行会让它的子树整段读不到）。</summary>
    [Fact]
    public void Chain_predicate_lands_on_the_anchor_sentence()
    {
        var sql = orm().Select<OrgNode>().Where(n => n.TenantId == 7).Descendants(n => n.ParentId, 42).ToSql();

        Assert.Contains("TenantId", sql);
        Assert.Contains(" IN ", sql);
    }

    /// <summary>空锚点一句都不发，所以也没有 SQL 可给：诊断口在这里响，而不是交回一颗
    /// 空 IN 的退化句子假装能执行（执行期那一档直接交回空集，见集成册）。</summary>
    [Fact]
    public void Empty_anchors_have_no_sql_to_show()
    {
        var chain = orm().Select<OrgNode>().Descendants(n => n.ParentId);

        Assert.Throws<QuickSqlException>(() => chain.ToSql());
    }

    // ── 拒档：发出去不会报错、只会交回残段的那些形状 ──────────────────────

    [Fact]
    public void Projection_before_the_tree_mouth_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().Select(n => new { n.Id, n.Name }).Descendants(n => n.Id, 1));

    [Fact]
    public void IncludeMany_then_tree_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().IncludeMany(n => n.Children!).Descendants(n => n.ParentId, 1));

    [Fact]
    public void AsSplitQuery_then_tree_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().AsSplitQuery().Descendants(n => n.ParentId, 1));

    [Fact]
    public void WithSql_source_override_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().WithSql("SELECT * FROM QuickSqlOrgNode").Descendants(n => n.ParentId, 1));

    [Fact]
    public void Set_operations_are_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>()
            .UnionAll(orm().Select<OrgNode>().Where(n => n.TenantId == 2))
            .Descendants(n => n.ParentId, 1));

    [Fact]
    public void Paging_is_rejected()
    {
        Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().Take(10).Descendants(n => n.ParentId, 1));
        Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().Skip(1).Ancestors(n => n.ParentId, 1));
    }

    [Fact]
    public void Grouping_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>()
            .GroupBy(n => n.TenantId).Select(g => new { g.Key, Count = g.Count() })
            .Descendants(n => n.Key, 1));

    [Fact]
    public void Split_table_entity_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<ShardLog>().ToTree(n => n.Id));

    [Fact]
    public void Composite_primary_key_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgCompositeNode>().Descendants(n => n.ParentId, 1));

    /// <summary>父键 lambda 点到导航属性上：那不是一颗列，闭包的 IN 臂无处落。</summary>
    [Fact]
    public void Parent_selector_must_point_at_a_column()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().Descendants(n => n.Children!, 1));

    [Fact]
    public void Null_anchor_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().Descendants(n => n.ParentId, (object)null!));

    /// <summary>整片那一档没有片界定就是全表搬进内存：这不是查询是事故，所以接链就响。
    /// 有 Where 的那一半要真行才判得出来（集成册），离线只钉拒的那一侧。</summary>
    [Fact]
    public void ToTree_without_a_slice_predicate_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>().ToTree(n => n.ParentId));

    [Fact]
    public void Depth_must_be_positive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => orm().Select<OrgNode>().Descendants(n => n.ParentId, 1).Depth(0));

    /// <summary>主键 bigint 配父键 int：库里这一对连得上，内存里两侧值不同型 ⇒ 键表永远命不中 ⇒
    /// 交回一棵"每行都是根"的扁表，而一句 SQL 都不报错。所以接链就拒，消息把两颗列与两种型都点出来。</summary>
    [Fact]
    public void Parent_key_of_a_different_value_type_than_the_primary_key_is_rejected()
    {
        var error = Assert.Throws<QuickSqlException>(
            () => orm().Select<BadTypedNode>().Descendants(n => n.ParentId, 1L));

        Assert.Contains("ParentId", error.Message);
        Assert.Contains("Int32", error.Message);
        Assert.Contains("Int64", error.Message);
    }

    /// <summary>WithCache 在逐层爬那一档点了不生效（多句闭包不是一句可缓存的对象），所以拒而不是静默不接——
    /// 与 AsSplitQuery↔IncludeMany 那一对"点了名却不生效"的护栏同一档判据。
    /// 整片那一档接得住（缓存落在取片那一句上），可 ToTree 是终端、离线不发句子，那一半只能在集成册里看。</summary>
    [Fact]
    public void WithCache_on_a_crawled_closure_is_rejected()
        => Assert.Throws<QuickSqlException>(() => orm().Select<OrgNode>()
            .WithCache("tree", TimeSpan.FromMinutes(1)).Descendants(n => n.ParentId, 42));

    /// <summary>父键 lambda 写成非属性形态时，消息要指名是树口说错话，不是 Include。</summary>
    [Fact]
    public void A_malformed_parent_selector_names_the_tree_mouth()
    {
        var error = Assert.Throws<QuickSqlException>(
            () => orm().Select<OrgNode>().Descendants(n => n.ParentId ?? 0, 42));

        Assert.Contains("Descendants/Ancestors/ToTree", error.Message);
        Assert.DoesNotContain("Include", error.Message);
    }

    /// <summary>规范文本那一行的锚点账：一颗含分隔符的键 ≠ 两颗键；二进制键按内容分开。
    /// 这一格是 StableKey 这句话自己的用处（"两条 SQL 逐字相同而 Tree 不同必须分得开"）。</summary>
    [Fact]
    public void Distinct_anchor_sets_print_distinctly()
    {
        var one = orm().Select<StrNode>().Descendants(n => n.ParentId, "a|b").Explain().ModelText;
        var two = orm().Select<StrNode>().Descendants(n => n.ParentId, "a", "b").Explain().ModelText;

        Assert.NotEqual(one, two);
        Assert.Contains("anchors=1[a|b]", one);
        Assert.Contains("anchors=2[a][b]", two);

        var left = orm().Select<BinNode>().Descendants(n => n.ParentId, new byte[] { 1, 2 }).Explain().ModelText;
        var right = orm().Select<BinNode>().Descendants(n => n.ParentId, new byte[] { 3, 4 }).Explain().ModelText;

        Assert.NotEqual(left, right);
        Assert.Contains("[0x0102]", left);
    }

    /// <summary>锚点数组是调用方 params 现场造的那一只：接链那一刻拷贝进模型，此后调用方就地改内容
    /// 不再影响这条链（不可变纪律连锚点也要算上——规范文本与执行期的 IN 臂都按接链时的锚点算）。</summary>
    [Fact]
    public void Anchors_are_copied_at_chain_time_not_shared_with_the_caller()
    {
        var anchors = new object[] { 42, 43 };
        var chain = orm().Select<OrgNode>().Descendants(n => n.ParentId, anchors);

        anchors[0] = 99;

        Assert.Contains("anchors=2[42][43]", chain.Explain().ModelText);
        Assert.DoesNotContain("[99]", chain.Explain().ModelText);
    }

    // ── 19 家通用 ───────────────────────────────────────────────────────

    /// <summary>G5 的离线半格：同一颗口在 19 家都发得出锚点句，且没有一家需要库端树语。
    /// 这一颗是"兼容所有 provider"的判据本身——谁往发射层塞了方言分支，这里就会有一家抛或文本异形。</summary>
    [Fact]
    public void Every_provider_emits_the_same_plain_anchor_shape()
    {
        var families = new (string Name, Func<QuickSqlOrm> Orm)[]
        {
            ("SqlServer", () => SqlServer.Orm()), ("MySql", () => MySql.Orm()), ("PostgreSql", () => PostgreSql.Orm()),
            ("Oracle", () => Oracle.Orm()), ("Sqlite", () => Sqlite.Orm()), ("Dameng", () => Dameng.Orm()),
            ("Kingbase", () => Kingbase.Orm()), ("Xugu", () => Xugu.Orm()), ("Gbase8s", () => Gbase8s.Orm()),
            ("Gbase8c", () => Gbase8c.Orm()), ("OpenGauss", () => OpenGauss.Orm()), ("GaussDb", () => GaussDb.Orm()),
            ("Highgo", () => Highgo.Orm()), ("ShenTong", () => ShenTong.Orm()), ("DuckDb", () => DuckDb.Orm()),
            ("ClickHouse", () => ClickHouse.Orm()), ("TDengine", () => TDengine.Orm()), ("TiDb", () => TiDb.Orm()),
            ("OceanBase", () => OceanBase.Orm()),
        };

        Assert.Equal(19, families.Length);

        foreach (var (name, factory) in families)
        {
            var sql = factory().Select<OrgNode>().Descendants(n => n.ParentId, 42).ToSql();

            Assert.True(sql.Contains(" IN "), $"{name} 的锚点句没有 IN 臂：{sql}");
            Assert.True(!sql.Contains("RECURSIVE") && !sql.Contains("CONNECT BY"), $"{name} 的锚点句里出现了库端树语：{sql}");
        }
    }
}
