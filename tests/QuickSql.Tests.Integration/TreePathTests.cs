using QuickSql.Aop;
using QuickSql.Query;
using QuickSql.TestInfra;
using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>树形闭包在真库上的那一半：Depth / Path / 环 / 孤儿 / 过滤器断链 / 句数账 / 参数分块。
/// 离线那一半（锚点句形状、拒档、19 家通用）在 <c>TreeQueryTests</c>。
///
/// <para><b>为什么这些只能在库里看</b>：闭包是执行期一句一句爬出来的，层数、每层的键集、
/// 以及"最后一句空手而归"那笔固有账都要真行才数得出来；缝合的 Depth/Path 与环的落点也一样——
/// 离线册发不出第二句，判不了"第二句的 IN 里躺的是不是刚取回来的那批键"。</para>
///
/// <para><b>隔离</b>：每用例一颗随机 TenantId 与一颗随机 id 基址，所有断言都落在自己那一片里；
/// 表由夹具建一次（<c>Database.EnsureTable</c>），行留在库里与既有路径测试同一纪律。
/// 万行那一颗用例自己清场，不让它把后续每一跑都拖重。</para></summary>
public sealed class TreePathTests(ITestOutputHelper output)
{
    static QuickSqlOrm Orm => Database.Orm;

    static CancellationToken ct => TestContext.Current.CancellationToken;

    readonly int _tenant = Random.Shared.Next(1, int.MaxValue);
    readonly int _basis = Random.Shared.Next(1, 2_000_000) * 100;

    int Id(int offset) => _basis + offset;

    OrgNode Node(int offset, int? parentOffset, string name, int sort = 0) => new()
    {
        Id = Id(offset),
        ParentId = parentOffset is { } p ? Id(p) : null,
        Name = name,
        SortNo = sort,
        TenantId = _tenant,
    };

    /// <summary>靶子树：100 为根，110/120 是它的两个孩子（SortNo 1 与 2），111 挂 110、112 挂 111、
    /// 121 挂 120；另加一颗孤儿 200（父键指向片外的 999）。三层深，够判 Depth 与面包屑两个方向。</summary>
    async Task SeedAsync(params OrgNode[] extra) => await SeedCore(null, extra);

    async Task SeedAsync(int? archive, params OrgNode[] extra) => await SeedCore(archive, extra);

    async Task SeedCore(int? archive, OrgNode[] extra)
    {
        Database.Ready();
        Database.EnsureTable(typeof(OrgNode));

        var rows = new List<OrgNode>
        {
            Node(100, null, "root"),
            Node(110, 100, "a", 1),
            Node(120, 100, "b", 2),
            Node(111, 110, "a1", 1),
            Node(121, 120, "b1", 1),
            Node(112, 111, "a1a", 1),
            Node(200, 999, "orphan", 3),
        };

        rows.AddRange(extra);

        if (archive is { } offset)
            rows.Single(n => n.Id == Id(offset)).Archived = true;

        var written = await Orm.InsertRange(rows).ExecuteAffrowsAsync(ct);
        Assert.Equal(rows.Count, written);
    }

    static (QuickSqlOrm Orm, List<string> Sql, List<int> Parameters) Probe()
    {
        var sql = new List<string>();
        var parameters = new List<int>();
        var aop = new QuickSqlAop
        {
            Executing = e =>
            {
                sql.Add(e.Sql.Text);
                parameters.Add(e.Sql.Parameters.Count);
            },
        };

        return (Database.OrmWith(aop), sql, parameters);
    }

    // ── 子集 ────────────────────────────────────────────────────────────

    /// <summary>G1 + G2：子集闭包取回整棵子树，Depth 从锚点起算，Path 是根到本行的键串；
    /// 句数 = 锚点句 + 每层一句 + 探底那一句（要知道没有下一层只能问一次，这是逐层爬的固有账）。</summary>
    [Fact]
    public async Task Descendants_return_the_subtree_with_depth_path_and_a_countable_number_of_sentences()
    {
        await SeedAsync();
        var (orm, sql, _) = Probe();

        var rows = await orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(100)).ToTreeRowsAsync(ct);

        Assert.Equal(
            [(Id(100), 0), (Id(110), 1), (Id(111), 2), (Id(112), 3), (Id(120), 1), (Id(121), 2)],
            rows.Select(r => (r.Value.Id, r.Depth)).OrderBy(x => x.Id).ToArray());

        var byId = rows.ToDictionary(r => r.Value.Id);
        Assert.Equal(Id(100).ToString(), byId[Id(100)].Path);
        Assert.Equal($"{Id(100)}/{Id(110)}/{Id(111)}/{Id(112)}", byId[Id(112)].Path);
        Assert.Equal(Id(111), byId[Id(112)].ParentKey);

        // 孤儿不在锚点 100 的子树里。
        Assert.DoesNotContain(Id(200), byId.Keys);

        // 锚点句 + 三层 + 探底一句 = 5。第二句起 IN 臂落在父键上，第一句落在主键上。
        // 认列不认字形：虚谷把标识符折大写（"PARENTID"），大小写敏感的断言会在这一家假红。
        Assert.Equal(5, sql.Count);
        Assert.Contains("parentid", sql[1].ToLowerInvariant());
        output.WriteLine($"子集三层树：{sql.Count} 句（锚点 1 + 层 3 + 探底 1），{rows.Count} 行");
    }

    /// <summary>深度上限截的是层数，不是行数：Depth(1) 只到锚点的直接子，句子数也跟着少。</summary>
    [Fact]
    public async Task Depth_caps_levels_and_sentences()
    {
        await SeedAsync();
        var (orm, sql, _) = Probe();

        var rows = await orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(100)).Depth(1).ToTreeRowsAsync(ct);

        Assert.Equal([Id(100), Id(110), Id(120)], rows.Select(r => r.Value.Id).OrderBy(x => x).ToArray());
        Assert.Equal(2, sql.Count);
    }

    /// <summary>摘掉锚点：它的直接子升为根（Depth = 0），锚点自己不在结果里。</summary>
    [Fact]
    public async Task IncludeSelf_false_promotes_the_anchors_children_to_roots()
    {
        await SeedAsync();

        var forest = await Orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(100)).IncludeSelf(false).ToTreeAsync(ct);

        Assert.Equal([Id(110), Id(120)], forest.Select(n => (int)n.Key).OrderBy(x => x).ToArray());
        Assert.All(forest, n => Assert.Equal(0, n.Depth));
        Assert.All(forest, n => Assert.True(n.IsRoot));
        Assert.Equal([Id(111)], forest.Single(n => n.Key.Equals(Id(110))).Children.Select(c => c.Key).ToArray());
    }

    // ── 父级 ────────────────────────────────────────────────────────────

    /// <summary>面包屑：从叶子往上爬到根，输出森林的根是最上层祖先，所以前序摊平就是根 → 锚点序。
    /// 方向翻的是每层 IN 臂落在哪颗列上（父级档恒按主键认，IN 的是上一层的父键集）。</summary>
    [Fact]
    public async Task Ancestors_come_back_root_first_as_a_breadcrumb()
    {
        await SeedAsync();
        var (orm, sql, _) = Probe();

        var rows = await orm.Select<OrgNode>().Ancestors(n => n.ParentId, Id(112)).ToTreeRowsAsync(ct);

        Assert.Equal([Id(100), Id(110), Id(111), Id(112)], rows.Select(r => r.Value.Id).ToArray());
        Assert.Equal([0, 1, 2, 3], rows.Select(r => r.Depth).ToArray());
        Assert.Equal($"{Id(100)}/{Id(110)}/{Id(111)}/{Id(112)}", rows[^1].Path);

        // 锚点句 + 三层父 + 探底（根的父键为 null，不再发）= 4。
        Assert.Equal(4, sql.Count);
        output.WriteLine($"父级三层链：{sql.Count} 句，{rows.Count} 行");
    }

    // ── 整片 ────────────────────────────────────────────────────────────

    /// <summary>G1 的整片半格 + 兄弟序：一句取片，嵌成森林，孩子的顺序 = SQL 的 ORDER BY，
    /// 内存不重排。孤儿（父被片界定切在外面）升为根，一行不丢。</summary>
    [Fact]
    public async Task ToTree_nests_one_slice_in_a_single_sentence_and_keeps_sibling_order()
    {
        await SeedAsync();
        var (orm, sql, _) = Probe();

        var forest = await orm.Select<OrgNode>()
            .Where(n => n.TenantId == _tenant)
            .OrderBy(n => n.SortNo)
            .ToTreeAsync(n => n.ParentId, ct);

        Assert.Single(sql);
        Assert.Equal(7, forest.Sum(Count));

        var root = forest.Single(n => n.Key.Equals(Id(100)));
        Assert.Equal([Id(110), Id(120)], root.Children.Select(c => c.Key).ToArray());
        Assert.Equal([Id(111)], root.Children[0].Children.Select(c => c.Key).ToArray());
        Assert.Equal([Id(112)], root.Children[0].Children[0].Children.Select(c => c.Key).ToArray());
        Assert.Equal(2, root.Children[0].Children[0].Depth);
        Assert.Equal(3, root.Children[0].Children[0].Children[0].Depth);

        // 孤儿 200 与根 100 并列：它的父键指向片外，找不到主人就升为根。
        var orphan = forest.Single(n => n.Key.Equals(Id(200)));
        Assert.True(orphan.IsRoot);
        Assert.Equal(0, orphan.Depth);
        Assert.Empty(orphan.Children);

        output.WriteLine($"整片 7 行：{sql.Count} 句，森林 {forest.Count} 颗根");
    }

    static int Count(TreeNode<OrgNode> node) => 1 + node.Children.Sum(Count);

    /// <summary>扁形状与树形状是同一次缝合的两种交法：逐行键、Depth、Path 必须全等。
    /// 这一颗是"行与节点对不上"那类缺陷的守卫。</summary>
    [Fact]
    public async Task Rows_and_forest_are_the_same_stitch()
    {
        await SeedAsync();

        var chain = Orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(100));
        var rows = await chain.ToTreeRowsAsync(ct);
        var forest = await chain.ToTreeAsync(ct);

        var flat = new List<(object Key, int Depth, string Path)>();
        void Walk(TreeNode<OrgNode> node)
        {
            flat.Add((node.Key, node.Depth, node.Path));
            foreach (var child in node.Children)
                Walk(child);
        }

        foreach (var root in forest)
            Walk(root);

        Assert.Equal(
            rows.Select(r => (r.Key, r.Depth, r.Path)).ToArray(),
            flat.ToArray());
    }

    /// <summary>G11：会话里同一行只有那一只。两句取数路（一句取片 / 逐层爬）都必须把先前登记的那一只
    /// 递回来，而不是物化出的第二只——否则 <c>db.Entry(树里那只)</c> 会说它不在会话里，
    /// 而改树上的名字要么写两句、要么写空。</summary>
    [Fact]
    public async Task The_session_surfaces_one_instance_per_row_on_both_take_shapes()
    {
        await SeedAsync();

        // 键先落成局部变量：lambda 里调实例方法（Id(110)）翻译不了，那是闭包成员读口的形状。
        var target = Id(110);

        var db = new DbContext(Orm);
        var known = Assert.Single(await db.Set<OrgNode>().Where(n => n.Id == target).ToListAsync(ct));

        var sliced = await db.Set<OrgNode>().Where(n => n.TenantId == _tenant).ToTreeAsync(n => n.ParentId, ct);
        Assert.Same(known, Find(sliced, target));

        var crawled = await db.Set<OrgNode>().Descendants(n => n.ParentId, Id(100)).ToTreeRowsAsync(ct);
        Assert.Same(known, crawled.Single(r => r.Value.Id == target).Value);
    }

    static OrgNode? Find(IEnumerable<TreeNode<OrgNode>> forest, int id)
    {
        foreach (var node in forest)
        {
            if (Equals(node.Key, id))
                return node.Value;

            if (Find(node.Children, id) is { } hit)
                return hit;
        }

        return null;
    }

    /// <summary>二进制键的试纸（审出来的 P1）：主键与父键都是 <c>byte[]</c> 时，缝合的键表必须按内容比。
    /// 按引用比的话每一趟物化都新建一只数组 ⇒ 每个节点都"找不到父"⇒ 交回一张扁表，
    /// 而库里一句 SQL 都不报错、一个警告都不给。<c>Path</c> 同批：二进制段要写成十六进制。</summary>
    [Fact]
    public async Task Binary_keys_stitch_by_content_not_by_reference()
    {
        // 建表那一格先拦一道：byte[] 主键在 SQL Server/MySQL 系/虚谷 上落不成 varbinary(16)（Length 没进
        // binary 的类型映射，发了 max/BLOB ⇒ 不能当索引键）。那是 CodeFirst 的独立一笔，与树形无关，
        // 所以这里按家族跳掉并在消息里指名拦的是哪一格，而不是把用例改成"只有两家跑"。
        Assert.SkipWhen(Database.Family is not (Database.Target.Sqlite or Database.Target.PostgreSql),
            $"建表拦在前：{Database.Family} 的 binary 列类型映射今天不认 Length（varbinary(max)/BLOB 不能当索引键），" +
            "这一格是 CodeFirst 的账，与树形闭包无关；键表按内容比这一判据由 SQLite 与 PostgreSQL 两家守。");

        Database.Ready();
        Database.EnsureTable(typeof(BinNode));

        // 键里编上本场租户：二进制主键写死常量会撞上一场留下的行（同一组字面量在库里只有一行）。
        var prefix = BitConverter.GetBytes(_tenant);
        byte[] Bin(byte tail) => [.. prefix, tail];
        byte[] root = Bin(1), child = Bin(2), grand = Bin(3);

        Assert.Equal(3, await Orm.InsertRange(new[]
        {
            new BinNode { Id = root, ParentId = null, Name = "b-root", TenantId = _tenant },
            new BinNode { Id = child, ParentId = root, Name = "b-child", TenantId = _tenant },
            new BinNode { Id = grand, ParentId = child, Name = "b-grand", TenantId = _tenant },
        }).ExecuteAffrowsAsync(ct));

        var top = Assert.Single(await Orm.Select<BinNode>().Where(n => n.TenantId == _tenant).ToTreeAsync(n => n.ParentId, ct));

        var second = Assert.Single(top.Children);
        var third = Assert.Single(second.Children);
        Assert.Equal(2, third.Depth);

        var rows = await Orm.Select<BinNode>().Descendants(n => n.ParentId, root).ToTreeRowsAsync(ct);

        Assert.Equal(3, rows.Count);
        Assert.Equal([0, 1, 2], rows.Select(r => r.Depth).ToArray());
        Assert.Equal(Convert.ToHexString(grand), rows[^1].Path.Split('/')[^1]);
    }

    // ── 环、空集 ────────────────────────────────────────────────────────

    /// <summary>G3：库里成环时句子数与行数都有界，每颗键只收一次；环上的行升为根，一行不静默丢。
    /// 这一格不依赖任何库端的 MAXRECURSION / CYCLE——拦环的是内存里的已访问键集。</summary>
    [Fact]
    public async Task A_cycle_is_bounded_and_its_rows_surface_as_roots()
    {
        await SeedAsync(Node(300, 301, "cycA"), Node(301, 300, "cycB"));
        var (orm, sql, _) = Probe();

        var rows = await orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(300)).ToTreeRowsAsync(ct);

        Assert.Equal(2, rows.Count);
        Assert.Equal([Id(300), Id(301)], rows.Select(r => r.Value.Id).OrderBy(x => x).ToArray());
        Assert.Equal(3, sql.Count);

        var forest = await orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(300)).ToTreeAsync(ct);
        Assert.Single(forest);
        Assert.True(forest[0].IsRoot);
        Assert.Equal(2, Count(forest[0]));
        output.WriteLine($"环 A→B→A：{sql.Count} 句收口，2 行各出现一次");
    }

    /// <summary>空锚点一句都不发（与 IncludeMany 的空父集同一纪律）。</summary>
    [Fact]
    public async Task Empty_anchors_send_nothing()
    {
        await SeedAsync();
        var (orm, sql, _) = Probe();

        var rows = await orm.Select<OrgNode>().Descendants(n => n.ParentId).ToTreeRowsAsync(ct);

        Assert.Empty(rows);
        Assert.Empty(sql);
    }

    /// <summary>锚点不存在交回空集，不抛；只发锚点那一句，没有下一层可爬。</summary>
    [Fact]
    public async Task A_missing_anchor_returns_empty_after_one_sentence()
    {
        await SeedAsync();
        var (orm, sql, _) = Probe();

        var rows = await orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(99999)).ToTreeRowsAsync(ct);

        Assert.Empty(rows);
        Assert.Single(sql);
    }

    // ── 参数预算 ────────────────────────────────────────────────────────

    /// <summary>G4：锚点集超过参数预算时分块发，每句的参数颗数不超过预算口径，行一颗不漏。
    /// 预算按 provider 现算（SQLite 走保守值 100，SQL Server 2100，MySQL 系 65535），
    /// 所以这一颗在六家上判的是同一件事而不是某一个写死的数。</summary>
    [Fact]
    public async Task Many_anchors_are_chunked_by_the_provider_parameter_budget()
    {
        const int anchors = 250;
        var many = Enumerable.Range(1000, anchors).Select(i => Node(i, null, $"n{i}")).ToArray();
        await SeedAsync(many);

        var (orm, sql, parameters) = Probe();
        var budget = orm.Provider.Bulk?.ParameterBudget ?? 100;
        var perChunk = Math.Max(1, budget * 9 / 10);

        var ids = many.Select(n => (object)n.Id).ToArray();
        var rows = await orm.Select<OrgNode>().Descendants(n => n.ParentId, ids).ToTreeRowsAsync(ct);

        Assert.Equal(anchors, rows.Count);
        Assert.Equal(many.Select(n => n.Id).OrderBy(x => x).ToArray(), rows.Select(r => (int)r.Key).OrderBy(x => x).ToArray());
        Assert.All(parameters, count => Assert.True(count <= perChunk, $"一句 {count} 颗参数超过预算口径 {perChunk}"));
        Assert.True(sql.Count > 1 || perChunk >= anchors, "锚点超预算却没有分块");
        output.WriteLine($"{anchors} 颗锚点：{sql.Count} 句，预算 {budget}／每块 {perChunk}，最大一句 {parameters.Max()} 颗参数");
    }

    // ── 过滤器与软删同一形状 ──────────────────────────────────────────────

    /// <summary>G6：全局过滤器落到每一句，所以被筛掉的中间节点会让它下面整段读不到；
    /// 整片那一档同一颗过滤器把它的孩子变成孤儿，孤儿升为根。两格是同一行为的两个方向，
    /// 钉在一起才读得出"这不是缺陷是口径"。过滤器注册在独立组装根上，不污染 Database.Orm。</summary>
    [Fact]
    public async Task A_filtered_middle_node_truncates_the_chain_and_orphans_its_children()
    {
        await SeedAsync(archive: 110);

        var (orm, sql, _) = Probe();
        orm.Filters.Add<OrgNode>(n => !n.Archived);

        var down = await orm.Select<OrgNode>().Descendants(n => n.ParentId, Id(100)).ToTreeRowsAsync(ct);

        // 110 被筛掉 ⇒ 111/112 整段读不到；120 那一支照旧。
        Assert.Equal([Id(100), Id(120), Id(121)], down.Select(r => r.Value.Id).OrderBy(x => x).ToArray());

        var forest = await orm.Select<OrgNode>().Where(n => n.TenantId == _tenant).ToTreeAsync(n => n.ParentId, ct);

        // 111 的父（110）不在片里 ⇒ 升为根，112 仍挂在它下面。
        var promoted = forest.Single(n => n.Key.Equals(Id(111)));
        Assert.True(promoted.IsRoot);
        Assert.Equal(0, promoted.Depth);
        Assert.Equal([Id(112)], promoted.Children.Select(c => c.Key).ToArray());
        Assert.DoesNotContain(Id(110), forest.Select(n => n.Key));
        output.WriteLine($"过滤器断链：子集 {down.Count} 行（111/112 不可达），整片里 111 升为根；{sql.Count} 句");
    }

    // ── 税单 ────────────────────────────────────────────────────────────

    /// <summary>G11/G12：万行档的句数、行数、耗时与嵌套步读数。不断言耗时阈值（本机基准噪声带
    /// ±30–68%，钉阈值只会得到一颗随机器红的用例），只把数交出来——兼容 19 家赔进去的就是这笔账：
    /// 整片一句，逐层爬在宽树上要把上一层的全部键再 IN 回去探一次底。</summary>
    [Fact]
    public async Task Ten_thousand_nodes_report_their_sentence_and_time_tax()
    {
        const int width = 9999;

        // 这一颗自己摆靶子（不复用三层小树）：宽树的读数要能被一眼算清——根 1 + 叶 width。
        Database.Ready();
        Database.EnsureTable(typeof(OrgNode));

        var wide = new List<OrgNode> { Node(100, null, "root") };
        wide.AddRange(Enumerable.Range(2000, width).Select(i => Node(i, 100, $"leaf{i}")));
        Assert.Equal(wide.Count, await Orm.InsertRange(wide).ExecuteAffrowsAsync(ct));

        try
        {
            var (orm, sql, _) = Probe();

            var watch = Stopwatch.StartNew();
            var forest = await orm.Select<OrgNode>().Where(n => n.TenantId == _tenant).ToTreeAsync(n => n.ParentId, ct);
            watch.Stop();

            Assert.Single(sql);
            var root = forest.Single(n => n.Key.Equals(Id(100)));
            Assert.Equal(width + 1, Count(root));
            Assert.Equal(width, root.Children.Count);
            var sliceMs = watch.ElapsedMilliseconds;

            var (crawlOrm, crawlSql, crawlParams) = Probe();
            watch.Restart();
            var rows = await crawlOrm.Select<OrgNode>().Descendants(n => n.ParentId, Id(100)).ToTreeRowsAsync(ct);
            watch.Stop();

            Assert.Equal(width + 1, rows.Count);

            // 税单落跑日志（与 [bulk-timing] / [bulk-write-timing] 同一形）：整片一句 vs 逐层爬的句数差
            // 就是"兼容 19 家"赔进去的那笔账。只交数不钉阈值——本机基准噪声带 ±30–68%，
            // 阈值只会得到一颗随机器红的用例。
            Console.WriteLine($"[tree-tax] provider={Database.Family.ToString().ToLowerInvariant()} rows={width + 1} " +
                $"slice_sql={sql.Count} slice_ms={sliceMs.ToString(CultureInfo.InvariantCulture)} " +
                $"crawl_sql={crawlSql.Count} crawl_max_params={crawlParams.Max().ToString(CultureInfo.InvariantCulture)} " +
                $"crawl_ms={watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        }
        finally
        {
            await Orm.Delete<OrgNode>().Where(n => n.TenantId == _tenant).ExecuteAffrowsAsync(ct);
        }
    }
}
