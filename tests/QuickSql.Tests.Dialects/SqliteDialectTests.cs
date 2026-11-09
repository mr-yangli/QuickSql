using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.Sqlite;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>SQLite（第七家）的 SQL <b>文本</b>。全程 <see cref="SqlServer.NoDriverFactory"/>，与前六族的
/// 离线套件同一分工：黄金样本钉版式，结构断言钉"凭什么长这样"。
///
/// <para><b>这一批样本证明的是回归，不是"SQLite 接受这条 SQL"</b>——这一句在本家比在前六家更要说死，
/// 因为本家<em>确实</em>有一台活库、也确实有四十多轮探针读数挂在每一格里，而那些读数量的全是
/// <b>探针手写的语句</b>：本文件里 <c>Checks.Has</c> 钉的是"我们发出去了什么"，括号里引用的编号钉的是
/// "库对同一种形状答了什么"，<b>这两者在离线册里仍然是两件事</b>——本文件一条用例都不连库。
/// <b>【v0.37 第四笔自纠】原段落在这里写过一句更绝对的："本 provider 产出的文本到今天为止一条都没有
/// 被库执行过"。那一句今天作废，作废它的是集成册</b>：<c>QUICKSQL_TEST_PROVIDER=sqlite</c> 那一跑
/// （<c>obj/logs/v37-sqlite-int-4.log</c>，95 条 ∶ 0 红 ∶ 14 跳）里 <c>QueryEngine</c> 与
/// <c>InsertEngine</c> 自己拼出的语句真在那台库上跑过，于是 <see cref="CapabilityLedgerTests"/>
/// 本家那本的五行里有三行（分页、句尾 <c>RETURNING</c>、建表那一串）翻成了 <c>Live</c>。
/// <b>剩下两行仍然停在原地，而各自的原因不同</b>：<c>Schemas</c> 量的那一形 DDL 本仓库发得出、
/// 但那条路径今天不建带模式的表（夹具那十一张全在 <c>main</c>，探针那两句是手写的）；
/// <c>Identity_ExplicitInsert</c> 连可发的文本都没有（Core 那一档直接抛）。所以这一段真正的读法要换一颗钉子：
/// 不是"离线 ∶ 在线"两档，是<b>"这一格的语句今天发得出、而且被发出去过吗"</b>——发得出而跑过的翻档，
/// 发得出而没跑过的留在 <c>OfflineText</c>，压根发不出的留在 <c>VendorDoc</c>。这是七家里最容易被读错的一格：
/// 别家的"未实测"是连尺子都没有，本家的"未实测"是尺子有、靶子也有，而缺的到底是"接起来的那一次运行"还是
/// "一个发射口"，逐行不同。</para>
///
/// <para><b>为什么这一族有磁盘上的基线而不是全部交给对拍</b>：它在<em>形状</em>上是第一颗"半同形"的邻居——
/// 分页那一尾与 PG 血统逐字同形（<c>LIMIT n OFFSET m</c>），而缺省那一支不同（本家不许光写 <c>OFFSET</c>，
/// 要补一颗 <c>LIMIT -1</c>）；标识符那一格与 PG 血统同一副引号而<em>不</em>同一颗大小写纪律；
/// 写侧、守卫、DDL 三处全部异形。<c>DialectParityTests</c> 那种"整句逐字相等"的对拍在这里一条都提不出来：
/// 提出来的断言会一半真一半假。所以按 §13.3 那句"样本按形状选"，本家每一格各落一份磁盘样本，
/// 而<b>对读量不到的那一格由基线量</b>这条判据在本家是第一次不能不成立。</para>
///
/// <para>类注释第三段那句"尺子"在本文件还有一层具体形状：<b>下面没有一条用例断"这台库会接受"</b>，
/// 包括那条 <c>Assert.Equal</c>——它与 <c>PostgreSql</c> 对读的是<em>两颗策略值</em>与一份尾巴文本，
/// 不是两份可执行性。这一句到 v0.37 第四笔<em>仍然成立</em>，而它成立的范围要说清楚：真的跑过库的那两句
/// 在 <c>QuickSql.Tests.Integration</c> 里，本文件一个字节都没连库（<see cref="SqlServer.NoDriverFactory"/>
/// 从头到尾就是那颗探针）。</para></summary>
public sealed class SqliteDialectTests
{
    static readonly IDbProvider Provider = Sqlite.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名。理由与前六家同一条：不带目录参数就会改写别家的基线。</summary>
    const string Leaf = "sqlite";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Leaf);

    static QuickSqlOrm orm() => Sqlite.Orm();

    /// <summary>放开分页无序那一道闸的组装口，与 <c>SqlServerPagingTests</c>、<c>DamengDialectTests</c>
    /// 同一颗手段。<b>本家需要它而判据与别家不同条</b>：<c>DialectBase.PagingRequiresOrdering</c> 在本家
    /// 保持 false【实测·34：无 <c>ORDER BY</c> 的 <c>LIMIT</c> 直接通过】，也就是<em>库</em>不问序，
    /// 而 <c>QueryEngine.Effective</c> 那道闸是<em>共用层</em>按 <c>PagingOrdering</c> 策略放的，与方言无关。
    /// 所以这一家"库允许"与"我们默认不发"两句同时成立，钉前者必须点名放开策略。</summary>
    static QuickSqlOrm orm(PagingOrderingPolicy policy)
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString, PagingOrdering = policy });

    /// <summary>PG 血统那一颗——本文件只有一条用例以它为判据，而且判据是<em>尾巴文本加两颗策略值</em>：
    /// 这一家在分页上与它同套而不同实现，在寻址上与它同字而不同纪律，在其余各处全部异形。</summary>
    static QuickSqlOrm postgreSql() => PostgreSql.Orm();

    /// <summary>带模式的模型：只有 <see cref="A_named_schema_qualifies_the_table_and_moves_the_qualifier_onto_the_index_name"/>
    /// 需要它。与 <c>ChangeGuardTests</c>、<c>Gbase8sDialectTests</c> 同一手法（Fluent 覆写而不是新建实体）。</summary>
    static QuickSqlOrm ormWithSchema()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().ToTable("QuickSqlOrder", "app");

        return new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));
    }

    static Order row(int n) => new()
    {
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, 890),
    };

    // ── 标识符：第一颗每一颗取值都打在一台活库上的策略 ────────────────

    [Fact]
    public void The_policy_is_the_first_one_whose_every_cell_was_asked_of_a_live_library()
    {
        var sql = orm().Select<Order>().ToSql();

        Verify("select-entity", sql);

        // 双引号 + 恒加引号 + 不折，所以"建的时候叫什么、发的时候就是什么"【实测·probe10 22/81/616：
        // 目录原样回显声明字形】。这一格与 PG 血统差一颗布尔值而差出整条迁移链：那一家折小写，
        // 不带引号发出去指的是另一张表，本家不折所以不带引号也指同一张——但本家仍恒加引号，理由在下一格。
        Checks.Has("FROM \"QuickSqlOrder\" AS \"t0\"", sql);
        Checks.Has("SELECT \"t0\".\"Id\", \"t0\".\"CustomerName\"", sql);
        Checks.Absent("[", sql);
        Checks.Absent("`", sql);
        Checks.Absent("\"quicksqlorder\"", sql);
        Checks.Absent("\"QUICKSQLORDER\"", sql);

        // 引号字形只有三种这件事在本家第二次被确认（第一次是虚谷那一族）：双引号而方括号也认
        // 【实测·10：[Name] 与 "Name" 命中同一列】，取标准字形与"取这一家唯一没有第二候选的字形"恰好同向。
        Assert.Equal('"', IdentifierPolicy.Sqlite.OpenQuote);

        // 本家与 PG 血统那颗策略<em>按值不等</em>——而差的那一格（CaseSensitive）在本家不承担任何行为：
        // NeedsQuote 被 QuoteAlways 短路在前面【实测·12/19：带引号的 "MIXEDcase" 命中不带引号建的 mixedcase】。
        // 这一句写在这里是为了让下一版别把"两颗值不同"读成"两家行为不同"。
        Assert.NotEqual(IdentifierPolicy.PostgreSQL, IdentifierPolicy.Sqlite);
        Assert.False(IdentifierPolicy.Sqlite.CaseSensitive);
        Assert.True(IdentifierPolicy.Sqlite.QuoteAlways);
    }

    [Fact]
    public void Quoting_is_what_keeps_a_wrong_name_loud_because_an_unquoted_one_can_fail_silently()
    {
        // 恒加引号在本家的理由与另两家反着来：加引号是<em>降低</em>错误响度的反面——它把"名字写错了"
        // 留在报错里。四条能绿着错的格子都有编号：投影位置落成一列常量字符串【实测·13】、
        // WHERE 里恒假【实测·14】、算术里恒零【实测·15】、ORDER BY 既不报错也不改序【实测·L】，
        // 而不带引号的未知列是响亮的【实测·J/P】。这一格是本仓库第一次"护栏的取值本身就是一条实测"。
        // 本用例钉的是发射侧：全部标识符带引号，一处裸名都不留。
        var sql = orm().Select<Order>().Where(o => o.CustomerId == 7).OrderBy(o => o.Id).ToSql();

        Assert.DoesNotContain(" FROM QuickSqlOrder", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE CustomerId", sql, StringComparison.Ordinal);
        Checks.Has("WHERE \"t0\".\"CustomerId\" = @p0", sql);
        Checks.Has("ORDER BY \"t0\".\"Id\" ASC", sql);
    }

    // ── 分页：§9.4 那句"套数"不动，动的是套内实现 ────────────────────

    [Fact]
    public void The_tail_is_the_first_family_s_shape_word_for_word()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var parameters);

        Verify("paging-limit-offset", sql);

        // 与 PG 血统<em>逐字同形</em>的那一半：LIMIT 在前、OFFSET 在后，两颗都是参数。
        // 这里刻意不写 Assert.Equal(postgreSql()…)：那一家折小写，整句比会先把标识符那一格的差混进来，
        // 而对拍要量的只有尾巴——所以尾巴单独钉，整句交给下面的基线。
        Checks.Has("ORDER BY \"t0\".\"Id\" ASC LIMIT @p0 OFFSET @p1", sql);
        Assert.Equal([5, 10], parameters.Select(p => (int) p.Value!).ToArray());

        // 另三套的形状一个都不许漏进来（ROWNUM 那一家、OFFSET…FETCH 那一家、句首限定词那一家）。
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWNUM", sql);
        Checks.Absent("FIRST", sql);
        Checks.Absent("SKIP", sql);
    }

    [Fact]
    public void Offset_cannot_stand_alone_so_the_missing_limit_becomes_a_marker_not_a_number()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql(out var parameters);

        Verify("paging-skip-only", sql);

        // 本家不许光写 OFFSET【实测·A4/31/317】，所以有偏移而无上限时必须先落一颗 LIMIT。
        // 补的那一颗是"三派语义里的第三派"【实测·32/315/315b/408/832】：MySQL 血统写 2^64−1（厂商上限）、
        // 虚谷写 2147483647（那一家那型写死的数），两颗都是"挑一颗装得下的最大数"；本家的 -1 不是数量，
        // 是库认得的"不设上限"记号。于是"同一套文本形状里两家实现不通用"有了第一例。
        Assert.Single(parameters);
        Assert.Equal(30, parameters[0].Value);
        Checks.Has("LIMIT -1 OFFSET @p0", sql);
        Checks.Absent("18446744073709551615", sql);
        Checks.Absent("2147483647", sql);

        // 哨兵写成文本而不是参数：它与另两颗同档（写进文本的常数不参与绑定），而这里多一句——
        // 它<em>不是</em>一颗数，把它参数化就等于把"没有上限"送进绑定层去比大小。
        Checks.Absent("@p1", sql);

        // 与 PG 血统恰好相反：那一家光写 OFFSET 合法，所以它这一格一颗都不补（PostgreSqlDialect 类注释第二条）。
        Checks.Has("OFFSET @p0", postgreSql().Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql());
        Checks.Absent("LIMIT", postgreSql().Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql());
    }

    [Fact]
    public void Paging_needs_no_ordering_and_no_sentinel_when_both_cells_are_absent()
    {
        // 无 ORDER BY 的 LIMIT 直接通过【实测·34】，所以 DialectBase.PagingRequiresOrdering 保持 false；
        // 两支都缺省时整段不发【实测·A4 的反面：缺句子即无该子句】。
        // 放开的那道闸是<em>共用层</em>的策略而不是这一家的语法（见 <see cref="orm(PagingOrderingPolicy)"/>），
        // 所以这一条用例量的是"库那一侧不拦"，默认档那一拦由 <c>SqlServerPagingTests</c> 钉着。
        var sql = orm(PagingOrderingPolicy.Allow).Select<Order>().Take(5).ToSql();

        Checks.Has("LIMIT @p0", sql);
        Checks.Absent("ORDER BY", sql);
        Checks.Absent("OFFSET", sql);

        var plain = orm().Select<Order>().ToSql();

        Checks.Absent("LIMIT", plain);
        Checks.Absent("OFFSET", plain);
    }

    // ── 占位符：七家里唯一一颗前缀可以四选一的 ────────────────────────

    [Fact]
    public void The_prefix_is_one_of_four_legal_ones_and_is_chosen_by_the_shared_layer_not_by_the_library()
    {
        var sql = orm().Select<Order>().Where(o => o.Amount > 1m && o.CustomerId != 7).ToSql(out var parameters);

        // 四种记号在同一条语句里各绑定成功【实测·第 2 轮 113】——这一族的驱动与引擎都不挑前缀，
        // 而另六家的那颗是被驱动定死的。取 '@' 是为了与 T-SQL、PG 血统、MySQL 血统同形
        // （SqlLogging.ToLogSql 按"前缀 + 字母"找占位符），所以这颗取值是<em>共用层的便利</em>选中的。
        Assert.Equal('@', Provider.Dialect.ParameterPrefix);
        Assert.Equal(2, parameters.Count);

        // 原文照抄在前、改判在后：这一颗从 v0.35 起钉的是 <c>"t0"."Amount" > @p0 AND "t0"."CustomerId" <> @p1</c>
        // ——「基类的默认文本在本家第一次被量」那一档，另六家零覆写、由它们的同名用例继续钉着这一句。
        // v0.37 排查给本家添了一个例外：定点列在这里存 TEXT，而存储类排序 INTEGER &lt; TEXT 让
        // <c>1700 > '1000'</c> 得 <c>0</c>【实测·v0.37 排查】，于是比较运算符入口把 decimal 参数包成
        // <c>CAST(@p AS NUMERIC)</c>（<c>SqliteDialect.PrepareForEmission</c>）。所以本家这一格的真相不是
        // "基类文本"，是"基类文本加一颗方言局部 CAST"，而 CAST 的射程由
        // <see cref="The_decimal_cast_wraps_comparisons_only_and_never_arithmetic_or_non_decimal_values" />
        // 单独钉住——摘掉它会让那一条当场红，而不是静默换成一枚说不清的空集。
        Checks.Has("\"t0\".\"Amount\" > CAST(@p0 AS NUMERIC) AND \"t0\".\"CustomerId\" <> @p1", sql);

        // 具名两侧都认【实测·E1/E2b/O/414】⇒ 执行器不必摘前缀，那笔每颗参数一次的 Substring 本家不付。
        // 这颗开关第二个为真的家，而两家的来路不同条（T-SQL 是两段历史，本家是一次现场）。
        Assert.True(Provider.Dialect.DriverAcceptsPrefixedParameterNames);
        Checks.Has("@p0", parameters[0].Name);

        // 反面两格都有编号，因为它们是本家"前缀自由"这句话的两条边界：匿名那颗 ? 驱动直接拒【实测·225】
        // （尽管引擎自己支持 ?1 那一族【实测·N】——"引擎支持而驱动不支持"在本仓库第一次有实测），
        // 而 N'…' 前缀在这一族是语法错【实测·P】。所以"四选一"说的是具名前缀，不是全部记号。
        Assert.DoesNotContain("?", sql, StringComparison.Ordinal);
        Assert.False(Provider.Dialect.PrefixUnicodeLiterals);
    }

    [Fact]
    public void ToLogSql_substitutes_here_because_the_prefix_matches_the_three_other_families()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        var insert = new InsertStatement
        {
            Table = new TableRef(0, "t0", contract.Table, contract),
            Columns = [name, note],
            Rows = [[new ParameterValue("客户甲", name), new ParameterValue("C:\\temp", note)]],
        };

        var emitted = EmitInsert(in insert);
        var log = emitted.ToLogSql(Provider);

        Assert.Equal(2, emitted.Parameters.Count);

        // 本家与 T-SQL／PG 血统／MySQL 血统同一档：替换找得到占位符，日志里是带值的文本。
        // 这一句在本家比在另三家更要说清<em>为什么必须带值</em>：定点那一列在库里是文本，
        // 内联的形状与绑定的形状差一齿就是一句静默空集【实测·329】，所以 SqliteValues 那两条路
        // 必须逐字同形（TryInlineLiteral 类注释第一句），日志读到的才是库真会读到的那一串。
        Assert.NotEqual(emitted.Text, log);
        Checks.Has("'客户甲'", log);
        Checks.Has(@"'C:\temp'", log);
        Checks.Has("VALUES (@p0, @p1)", emitted.Text);
    }

    // ── 谓词形状：三格零覆写，而它们各自是别的家覆写过的那三格 ────────

    [Fact]
    public void The_constant_predicates_are_the_base_class_text_and_this_is_the_first_time_they_were_measured()
    {
        var none = Array.Empty<int>();

        var empty = orm().Select<Order>().Where(o => none.Contains(o.Id)).ToSql();

        // 恒假那一格基类默认 1 = 0，在本家第一次拿到实测【实测·2110】（恒真是 2111）。
        // 六家里这两格要么被 PG 血统改写成布尔字面量、要么"没覆写而也没人问过"——所以这里钉的不是形状，
        // 是"基类的默认值第一次被正面量过"这一格。
        Checks.Has("WHERE 1 = 0", empty);
        Checks.Absent("WHERE false", empty);

        var paid = true;

        var flag = orm().Select<Order>().Where(o => paid && o.Amount > 1m).ToSql(out var parameters);

        // 布尔比较两形在本家都合法且同判【实测·H0/335/607/831】：基类的 = 1 成立，而 IS TRUE 也成立
        // （PG 血统只有后一支、其余几家只有前一支）。所以 v0.35 那条"位只跟存储型走"落在这里
        // <em>没有</em>被迫二选一——这是七家里唯一一颗两种写法都量过的家。
        Assert.Equal(2, parameters.Count);

        // 原文照抄在前、改判在后：这一颗原先钉 <c>@p0 = 1 AND "t0"."Amount" > @p1</c>，为的是"基类的恒真/恒假
        // 与布尔比较那一组默认文本在本家第一次拿到实测"。v0.37 那颗 decimal 比较的 CAST 也落在这一句上，
        // 于是本家这一格量到的是"基类文本加 CAST"；基类那一句本身仍由另六家的同名用例钉。
        // 这一颗同时把改判的边界摆正：被 CAST 的只有 <c>@p1</c>（decimal），<c>@p0</c>（bool 折成 1）逐字不包。
        Checks.Has("@p0 = 1 AND \"t0\".\"Amount\" > CAST(@p1 AS NUMERIC)", flag);
        Checks.Absent("IS TRUE", flag);

        // 反面：声明 Boolean_Type 会把 EmitBooleanEquality 推到 IS TRUE 那一支，而本家不声明那颗位的理由
        // 与 MySQL 血统／虚谷反号【实测·607：typeof() 对 BOOLEAN 声明的列报 integer，= 1 与 IS TRUE 同判】——
        // 说的不是"真布尔有坑"，是"库里没有布尔这一型，声明它只是换一颗 affinity 配方"。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Boolean_Type));
    }

    /// <summary>v0.37 那颗 <c>CAST(@p AS NUMERIC)</c> 的<b>射程</b>单独钉一条。
    /// 为什么要单钉：上面三颗 needle 只证"今天发出去的文本长这样"，它们不证"这一形不能少"。
    /// 而这一颗是<em>产品语义</em>不是文本偏好——定点列在本家存 TEXT（<c>SqliteTypes</c>），
    /// 存储类排序 INTEGER &lt; TEXT 让 <c>1700 &gt; '1000'</c> 得 <c>0</c>【实测·v0.37 排查】，
    /// 摘掉 CAST 换来的不是更朴素的文本，是一批静默的空集；而空集落在那三颗 needle 上只会报
    /// "SQL 里没有这颗 needle"，那种红说的是版式，不是语义（§5.8 那条"护栏的价值一半在消息有没有说清该改哪里"）。
    /// 于是这里<em>正向</em>钉六颗比较符加一颗对称形都要包，<em>反向</em>钉三颗不包：非 decimal 的两型参数、
    /// 以及挂在算术里的 decimal 参数。反向那颗与 <see cref="The_write_target_keeps_the_base_alias_on_both_halves_because_the_library_takes_it_on_both" />
    /// 用的是同一条语句，所以"参数在赋值侧不受射程"这一格有两处独立读数。</summary>
    [Fact]
    public void The_decimal_cast_wraps_comparisons_only_and_never_arithmetic_or_non_decimal_values()
    {
        static string WhereSql(System.Linq.Expressions.Expression<Func<Order, bool>> predicate)
            => orm().Select<Order>().Where(predicate).ToSql();

        // 正向：六种比较符各一次。射程挂在"比较运算符的两侧"上，因此一颗都不能漏——
        // 少包一种就是那一族查询在这台库上静默返回空集。
        foreach (var sql in new[]
                 {
                     WhereSql(o => o.Amount > 1m), WhereSql(o => o.Amount >= 1m),
                     WhereSql(o => o.Amount < 1m), WhereSql(o => o.Amount <= 1m),
                     WhereSql(o => o.Amount == 1m), WhereSql(o => o.Amount != 1m),
                 })
            Checks.Has("CAST(@p0 AS NUMERIC)", sql);

        // 对称形：参数写在运算符左侧也要包。
        Checks.Has("CAST(@p0 AS NUMERIC)", WhereSql(o => 1m < o.Amount));

        // 反向一：整型与字符串参数逐字不受射程。
        Checks.Absent("CAST(", WhereSql(o => o.CustomerId == 7));
        Checks.Absent("CAST(", WhereSql(o => o.CustomerName == "x"));

        // 反向二：同一颗 decimal 参数挂在赋值（算术）里时不包 —— 上面那颗 needle 的 SET 半边是同一条对照。
        var increment = orm().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql();

        Checks.Has("SET \"Amount\" = \"t0\".\"Amount\" + @p0", increment);
        Checks.Absent("CAST(", increment);

        Checks.Has("SET \"Amount\" = \"t0\".\"Amount\" + @p0", increment);
        Checks.Absent("CAST(", increment);
    }

    // ── 写目标：两半都能带别名，而这是基类默认那一支第一次不需要重写 ──

    [Fact]
    public void The_write_target_keeps_the_base_alias_on_both_halves_because_the_library_takes_it_on_both()
    {
        var update = orm().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql();

        // UPDATE 侧【实测·2100，别名与表名同名的那一格是 2101】。T-SQL 要为这一格重写整条语句，
        // Oracle 血统、MySQL 血统、虚谷、GBase 8s 四族反而要把它关掉——本家三口全用基类默认。
        Checks.Has("UPDATE \"QuickSqlOrder\" AS \"t0\" SET \"Amount\" = \"t0\".\"Amount\" + @p0 WHERE \"t0\".\"Id\" = @p1", update);

        var delete = orm().Delete<Order>().Where(o => o.Id == 7).ToSql();

        // DELETE 侧同样带别名【实测·2104】，而这一格是本家独有的形状：
        // 另六家里 DELETE 能带别名的没有一家同时能让 UPDATE 也带。
        Checks.Has("DELETE FROM \"QuickSqlOrder\" AS \"t0\" WHERE \"t0\".\"Id\" = @p0", delete);
        Assert.Equal("t0", Provider.Dialect.WriteTargetAlias(orm().Schemas.Contract<Order>().Table));
    }

    [Fact]
    public void The_joined_write_refusal_names_two_equivalent_paths_that_were_both_measured()
    {
        // SupportsJoinedWrites 为 false，而本家的"缺"是两半各缺一次【实测·1311-1314/2108 有 UPDATE…FROM；
        // 1317/1318 的 DELETE FROM 与 USING 各一句语法错】，真正的挡点是列名清单
        // （AS t(cols) 五形五拒【实测·1201-1205】，UPDATE 侧第六次是 2107，而隐式列名是 column1..N【实测·1200/1207】）。
        Assert.False(Provider.Dialect.SupportsJoinedWrites);

        var failure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount)));

        // 本家把那句共用话术改写了，而改写是故意的：另三家（MySQL 血统、虚谷、GBase 8s）说的是
        // "不接受目标表之外的表源"，那一句话在本家是<em>半真</em>——它确实不接受，但不是因为
        // 没有表源那一格，是因为改名那一形被拒。所以这一格钉的是本家自己的措辞，
        // 而"grep 那句共用话术搜不到本家"这件事由这一行注明，免得下一版把它当成漏改。
        Checks.Has("接不下 §12.5 合并档那一形", failure.Message);
        Checks.Has("SupportsJoinedWrites", failure.Message);

        // 抛的话里那两条等价路本家<em>都有实测</em>（2108 的标量子查询形、2106 的关联形）——
        // 这一点是它比 GBase 8s 那一份多出来的一格：那一家两条路都只有【文档】。
        // 现场读到这条错的人下一步就是问"那这一家怎么写"，本家答得出，而答案仍然是"共用层不发"。
        Checks.Has("标量子查询", failure.Message);
        Checks.Has("WHERE EXISTS", failure.Message);
        Checks.Absent("MERGE INTO", failure.Message);

        var deleteFailure = Checks.Raised<QuickSqlException>(() => DeleteWithJoin());

        Checks.Has("1 个", deleteFailure.Message);

        // DELETE 那半边在本家是"两样都没有"，所以消息里的 FROM/USING 那一颗要说得出被问过几次。
        Checks.Has("FROM/USING", deleteFailure.Message);
    }

    // ── 变更守卫：第七家，而它是这一边第一颗"两个候选都在同一台活库上过"的 ──

    [Fact]
    public void A_not_null_column_still_needs_only_the_inequality()
    {
        var sql = GuardText(nameof(Order.Amount));

        // 与 T-SQL、Oracle 血统、MySQL 血统、虚谷、GBase 8s 六家逐字同判（本家是第六家走这一支的）。
        // 但本家这一句的"同形"要读成"同一条理由"：NOT NULL 列上两侧都不可能是 NULL，
        // 而多出来的两段 OR 只会让谓词更难走索引——那一句在本家是有对照的（下面的三段式在 BLOB 上也数得出）。
        // 表侧限定用的是别名 t0【实测·2100】，这一格与 Oracle 血统、虚谷、GBase 8s 那三家（目标自带表名）正对面。
        Checks.Has("\"t0\".\"Amount\" <> \"t1\".\"Amount\"", sql);
        Assert.Equal(0, Checks.Occurrences(sql, " OR "));
    }

    [Fact]
    public void A_nullable_column_takes_the_three_way_form_although_this_family_can_emit_the_one_sentence_form()
    {
        var sql = GuardText(nameof(Order.PaidAt));

        Verify("update-guard-three-way", sql);

        Checks.Has("\"t0\".\"PaidAt\" <> \"t1\".\"PaidAt\" OR (\"t0\".\"PaidAt\" IS NULL AND \"t1\".\"PaidAt\" IS NOT NULL) OR (\"t0\".\"PaidAt\" IS NOT NULL AND \"t1\".\"PaidAt\" IS NULL)", sql);
        Assert.Equal(2, Checks.Occurrences(sql, " OR "));

        // 本家是"两句式发得出而故意不发"的第一例——另六家没有一家同时握有两句式的实测：
        // IS DISTINCT FROM 在本家三处都量过（投影【实测·D1】、UPDATE…WHERE【实测·2112】、大对象【实测·3105】），
        // 不发它的理由是那颗算子要引擎 3.39 而 DbCapabilities 里没有"库的版本"那一格（§14）。
        // 这一句的措辞要与 MySqlChangeGuard 那句同族而不同处境：MySQL 那一家问得到服务器版本，
        // 本家是内嵌引擎，连问的入口都没有——版本由应用那侧的包图决定。
        Checks.Absent("IS DISTINCT FROM", sql);
        Checks.Absent("<=>", sql);

        // 三段式在本家第一次是逐颗数出来的（含两侧同空那一格）：NULL <> NULL 是 UNKNOWN，
        // 两句式的 NULL IS DISTINCT FROM NULL 是 0【实测·3203】，而差异只在把它当投影里的值取出来时才显形
        // 【实测·3103 ∶ 3204】——本仓库的用法是 UPDATE…WHERE 的谓词，UNKNOWN 与 0 同样不匹配【实测·3109 ∶ 3205 ∶ 3206】。
        // 这一段留在注释里而不落进断言，因为它量的是库的判语而不是文本的形状。
    }

    [Fact]
    public void The_guard_compares_every_type_because_this_library_answers_that_question_for_all_four()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        // 七家第一颗恒真的守卫——所以本文件没有另六家那两段（Incomparable 集合加 BaseType 切分 helper），
        // 而那两段的缺席本身就是读数：大对象在本家比得动，而且是在真正会用的那种量上比得动
        // （BLOB 列上 = ∶ <> ∶ IS ∶ IS DISTINCT FROM 四形各有所值【实测·3105】，三段式同样数得出【实测·3106】，
        // 200,000 字节 ∶ 1,000,000 字符两颗各写过再比【实测·3108a…3108e】）。
        Assert.True(Provider.ChangeGuard.Compares(note));
        Assert.True(Provider.ChangeGuard.Compares(name));
        Assert.True(Provider.ChangeGuard.Compares(amount));
        Assert.True(Provider.ChangeGuard.Compares(note with { ClrType = typeof(byte[]), StoreTypeName = null }));

        // 六家那一格的缺法在这里一种都不成立：Oracle 血统那四颗是厂商明文的 LOB 限制、虚谷那两颗问不出答案、
        // MySQL 血统那颗 json 是护栏。本家的类型映射只产 INTEGER ∶ REAL ∶ TEXT ∶ BLOB 四颗基名
        // （SqliteTypes.GetStoreType 那一段），带宽度时写作 VARCHAR(n) 而它落的是同一族文本比较
        // ——宽度在本家从不参与存储【实测·4001 ∶ 4002】。
        Assert.True(Provider.ChangeGuard.Compares(note with { StoreTypeName = "BLOB" }));
        Assert.True(Provider.ChangeGuard.Compares(note with { StoreTypeName = "VARCHAR(4)" }));
        Assert.True(Provider.ChangeGuard.Compares(amount with { StoreTypeName = "DECIMAL(18,4)" }));

        // 恒真的代价也要写在这里：本家的 SupportsJoinedWrites 同样是 false，而那道闸在别家替 Compares 的
        // false 代跑过（MySqlChangeGuard 第二段、XuguChangeGuard 第三段各记过一次），本家守卫恒真——
        // 也就是说本家是七家第一颗没有"false 但走不到"那一层缓冲的守卫，它为真的一切后果都是真的。
        Assert.False(Provider.Dialect.SupportsJoinedWrites);
    }

    [Fact]
    public void The_money_column_is_compared_character_by_character_and_the_direction_is_spelled_out()
    {
        var sql = GuardText(nameof(Order.Amount));

        // 定点在本家落 TEXT（SqliteTypes 第三条），绑定侧写不变形文本（SqliteValues.Normalize），
        // 所以守卫比的是那两串文本：库里 '12.3400' 而新值 12.34m → '12.34' → <> 成立【实测·3201 ∶ 3205】，
        // <b>判"变了"而多发一次写</b>。方向要说死：本家<em>没有</em>丢写的那一面——
        // 两串不变形文本相同则数值必相同，那一蕴含关系是绑定形状给的，不是库给的。
        // （与虚谷那颗"存入时去尾空格"同方向，与 MySQL 血统那颗 CI 排序规则反方向。）
        Checks.Has("<>", sql);
        Checks.Absent("COLLATE", sql);

        // 跨库差异表里那颗 CI 风险在本家为 0，而且是量出来的 0【实测·97 第一列 'Abc' = 'abc' → 0】；
        // 同一格上显式写 COLLATE NOCASE 才等【实测·97 第二列】，而 NOCASE 也只到 ASCII【实测·2239 三列】。
        // 那颗"守卫说没变而 WHERE … LIKE … 说变了"的反向风险【实测·96：LIKE 默认不分大小写】
        // 归 SqliteExpressions 与 §14，不在这里改守卫。
        Assert.DoesNotContain("NOCASE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_hash_function_here_so_the_no_approximation_rule_has_a_reading_not_an_argument()
    {
        // §12.4 第 2 项那句"宁可退回逐条也不 CHECKSUM/哈希近似"在本家要按两格读：
        // 本家<em>没有</em>内置哈希函数（UUID() 当场报 no such function【实测·2134】，
        // 整批探针里没有一个 *hash* 被问到过），同时本家<em>也不需要</em>它——
        // <> 在 200KB 的量上比得动【实测·3108d】，没有一段是被库逼出来"必须换算法"的档位（Oracle 血统那一家有）。
        // 所以本家留下的是一句<em>缺席读数</em>，而不是一句"我们选择了不近似"。
        // 这一条断言能钉的只有产出：守卫的文本里没有任何近似算子。
        var sql = GuardText(nameof(Order.PaidAt));

        foreach (var needle in new[] { "CHECKSUM", "HASH", "MD5", "SHA", "UUID" })
            Assert.DoesNotContain(needle, sql, StringComparison.Ordinal);

        // 反向一格：本家不发哈希而别家也不发，所以这一句不是"本家独有"，它独有的是<em>那句缺席读数</em>。
        Checks.Has("<>", sql);
    }

    // ── 并发列 ───────────────────────────────────────────────────────

    [Fact]
    public void The_concurrency_predicate_is_a_plain_equality_and_the_version_column_is_text_here()
    {
        var sql = ConcurrencyText(nameof(Order.Amount));

        Assert.Equal(ConcurrencyMode.TimestampColumn, Provider.Concurrency.Mode);
        // 别名只在 WHERE 那一侧，SET 那一侧是裸列【实测·2100：UPDATE "q21" AS "x" SET "v" = 'U' WHERE "x"."w" = 1】。
        // <b>"SET 那一侧带别名"那一形第四十五轮问过，答案是语法错</b>【实测·4521：
        // <c>UPDATE "t45" AS "x" SET "x"."V" = 'U' WHERE "x"."N" = @n</c> → <c>near ".": syntax error</c>，
        // 同一轮把裸列那一形对照跑了一次【实测·4522】】——
        // 所以这里钉的不只是"我们发出去的形状被这台库答过行"，而是"这一形没有另一种发法"：
        // 别名限定的 SET 列在本家不是"更严格"的写法，是<em>发不出去</em>的写法。
        Checks.Has("SET \"Amount\" = @p0", sql);

        // 原文照抄在前、改判在后：这一颗原先钉 <c>WHERE "t0"."Amount" = @p1</c>——「并发谓词是一条普通等式」
        // 那一格。v0.37 那颗 CAST 把它改成了"等式加 CAST"，而 SET 那一侧（上面那颗，逐字未动）恰好是同一条
        // 语句里的对照组：同一颗 <c>decimal</c> 参数，挂在赋值上不受射程，挂在比较上才受——
        // 这一对同场读数就是射程本身，比新写一条反面对照更省。
        Checks.Has("WHERE \"t0\".\"Amount\" = CAST(@p1 AS NUMERIC)", sql);
        Checks.Absent("ROWVERSION", sql);
        Checks.Absent(" IS NULL", sql);

        // 可空那一档与六家同判：`列 = @p` 在 NULL 上是 UNKNOWN【实测·3306 ∶ 3307 那一对】，
        // 所以并发列必须落在非空列上或由应用给值；折 IS NULL 那一形属于守卫，不属于并发谓词。
        Assert.DoesNotContain(" OR ", sql, StringComparison.Ordinal);

        // 本家在这一槽上比另六家多一处要小心的地方（SqliteConcurrency 类注释第三段）：
        // 版本列是文本时比的是<em>写法</em>而不是时刻【实测·3304 ∶ 3305】，所以"这一列能不能当版本列"
        // 在本家取决于它是否只由本仓库写过。这一句不进断言，因为它的判据是部署纪律而不是文本。
    }

    // ── 主键回填：四宿主全在活库上逐条问过的那一格 ────────────────────

    [Fact]
    public void Asking_for_the_generated_key_appends_a_tail_returning_that_this_library_answers_on_four_hosts()
    {
        var plan = orm().Insert(row(1)).ExplainKeys();
        var sql = plan.ToSql();

        Verify("insert-returning", sql);

        // 句尾 RETURNING 与 PG 血统同形，而那一家这一格拿的是厂商文档、本家是实测：
        // INSERT【实测·D3/403】、UPDATE【实测·第 2 轮 105 ∶ 404 ∶ 1319】、DELETE【实测·第 2 轮 106】、
        // UPSERT…DO UPDATE【实测·130/421】四形各一次，多列【实测·K/124/421】、多行【实测·1320/1011/1013】。
        Checks.Has("RETURNING \"Id\"", sql);
        Checks.Has(" VALUES (", sql);
        Checks.Absent("OUTPUT", sql);
        Checks.Absent("SCOPE_IDENTITY", sql);
        Checks.Absent("last_insert_rowid", sql);

        // 回填不改变计数——这一格在另六家是默认成立而没人问的，本家它是问出来的【实测·340 ∶ 341】：
        // 带 RETURNING 的写入语句 ExecuteNonQuery 仍交回受影响行数（1 与 4 两颗）。
        // 本仓库的 Affrows 判据正压在这颗数上，所以这一句是 §12 那一族的集成前提，不只是洁癖。
        Assert.False(Provider.Keys.SupportsPostStatementKeyRead);
        Checks.Has("RETURNING", plan.Chunks[0].Sql.Text);
    }

    [Fact]
    public void Many_rows_come_back_from_one_statement_because_bulk_key_retrieval_was_measured_not_assumed()
    {
        var plan = orm().InsertRange([row(1), row(2), row(3), row(4)]).ExplainKeys();

        // 批量回填位为真【实测·1320/1011/1013 多行一次】⇒ 四行一句而每句都带 RETURNING；
        // 另六家里这一位的两颗取值来自厂商文档（PostgreSqlKeys 类抬头那句"现场没有实例跑过"是七家里最诚实的自述）。
        Assert.True(Provider.Keys.SupportsBulkKeyRetrieval);
        Assert.Single(plan.Chunks);
        Assert.Equal(4, plan.Chunks[0].RowCount);
        Checks.Has("RETURNING \"Id\"", plan.Chunks[0].Sql.Text);
        // 四行落在一句的 VALUES 段里，而尾巴上只有<em>一颗</em> RETURNING：回包是四行的键，不是一行四份。
        // 数的是"(@p" 而不是"ROW"这个词——本家的多行形就是四份带括号的实参表，
        // 而 "ROW" 那一形（Oracle 血统的 <c>ROW</c> 伪列、MySQL 血统的 <c>ROWS</c> 影响行数）在本家根本不出现。
        Assert.Equal(4, Checks.Occurrences(plan.Chunks[0].Sql.Text, "(@p"));
        Assert.Equal(1, Checks.Occurrences(plan.Chunks[0].Sql.Text, "RETURNING"));
        Assert.DoesNotContain("ROW", plan.Chunks[0].Sql.Text, StringComparison.Ordinal);
        Assert.Equal(28, plan.Chunks[0].Sql.Parameters.Count);
    }

    [Fact]
    public void An_explicit_key_on_an_identity_column_is_legal_in_the_library_while_the_shared_layer_never_sends_it()
    {
        var order = row(1);

        order.Id = 4242;

        var sql = orm().Insert(order).ToSql();

        Verify("insert-with-explicit-key", sql);

        // 这一格在本家与前六家<em>同产出</em>：自增列不进列清单（<c>InsertEngine.Prepare</c> 那句
        // "§12.4 第 1 项"），所以人手里那颗 4242 一个字节都没有上线，而这条基线钉的就是"没有上线"这件事。
        // <b>本家比另六家多的那一格是：这一次"发不出去"不再是因为库要一道开关</b>——
        // NULL 进自增列由库补号【实测·210/211】，显式值进去之后序列<em>会挪到它上面</em>
        // （100 → 下一号 101【实测·54 ∶ 55】；31 亿 → 3000000001【实测·310/310b】），
        // 也就是这条路在库那一侧是通的、缺的只有共用层那一步。后一条读数正是 Gbase8sKeys 说
        // "连会不会撞都答不出"而记 §14 的那一颗，本家有答案，而答案是"不会撞"。
        Assert.DoesNotContain("(\"Id\"", sql);
        Checks.Has("VALUES", sql);

        // 与 T-SQL 的分别是开关：那一家要 SET IDENTITY_INSERT ON 才允许写，本家任何时候都允许【实测·54/310】，
        // 所以 toggle 返回 null。七家返回 null 的那五家说的是"没读到有这能力"，本家说的是"不需要开关"——
        // 同路不同因，这一句留在这里是因为抄代码的人会把它当成前五种之一。
        // <b>而两颗取值今天都没有消费者</b>：Core 里 <c>IdentityInsertToggle</c> 零调用点（各 provider 类注释
        // 记的是同一句），本家这一位为 true 只把"库那一侧的读法"登记下来，不构成"这条通路已通"的证据。
        Assert.True(Provider.Keys.SupportsExplicitIdentityInsert);
        Assert.Null(Provider.Keys.IdentityInsertToggle(NewWriter(), new TableInfo(null, "QuickSqlOrder"), true));
    }

    [Fact]
    public void The_post_statement_read_slot_refuses_although_this_family_really_can_do_it()
    {
        var contract = orm().Schemas.Contract<Order>();
        var key = contract.PrimaryKey[0];

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.Keys.EmitPostStatementKeyRead(w, key)));

        // 本家做得到：last_insert_rowid() 存在、原样可问【实测·D5/212/1018】，一条文本里放两句 SQL 也量过
        // 【实测·E3b/152/213/226/426】。但 Core 只在批量回填位为 false 时才问这颗开关，而本家批量位为真，
        // 所以这一位说的是"无入口"，与 PostgreSqlKeys 那句"不是没有那条路而是用不上那条路"同一条读法。
        Checks.Has("没有入口", failure.Message);
        Checks.Has("last_insert_rowid", failure.Message);

        // 两颗限定留在消息里，因为它们是这条路在本家<em>真</em>不可靠的原因：那颗函数是连接级的【实测·D5】，
        // 而一条多行 INSERT 只回最后一行的号【实测·212/1018】——即使哪天批量位因别的原因关掉，
        // 退化路也只对"每语句一行"的形状成立。
        Checks.Has("连接级", failure.Message);
        Checks.Has("最后一行", failure.Message);
    }

    [Fact]
    public void Returning_would_carry_a_defaulted_column_but_no_one_in_the_contract_asks_for_it()
    {
        var contract = orm().Schemas.Contract<Order>();
        var key = contract.PrimaryKey[0];
        var created = contract.Columns.Single(c => c.MemberName == nameof(Order.CreatedAt));

        var sql = Statement(w => Provider.Keys.EmitReturning(w, [key, created]));

        // 句尾多列 RETURNING 有实测【实测·K/124/421】，所以这一族理论上能回填"库里刚算出来的任何一格"
        // （DEFAULT 值、CURRENT_TIMESTAMP）。但 ColumnContract.StoreFillsValue 那条链今天只有
        // SqlServerTypes 会置位——<b>本家有承接的语法而契约里没有那一格的调用方</b>，与
        // Gbase8sConcurrency 那句"两颗一起记 §14"并排（那一家有表达式没有回包，本家两者都有而没人调用）。
        Checks.Has("RETURNING \"Id\", \"CreatedAt\"", sql);
        Assert.False(created.StoreFillsValue);
    }

    // ── 函数表：每一颗模板都在活库上问过取值形状 ──────────────────────

    [Fact]
    public void String_reads_share_the_standard_forms_and_concat_carries_the_parentheses_that_are_required_here()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Length > 3 && (o.CustomerName + o.Note).Length > 4)
            .ToSql();

        Verify("string-functions", sql);

        // 颗数而不是字节【实测·2201：'中文' → 2、'😀' → 1】，与另六家同一档而本家是量出来的。
        Checks.Has("LENGTH(\"t0\".\"CustomerName\") > @p0", sql);

        // 括号是必需的，而且本家把它量成了两条【实测·2230】：`1 || 2 = 12` 交回 0（算子被 = 抢了先），
        // `(1 || 2) = '12'` 才交回 1。共用层把结果再拼进 WHERE 时不带自己的括号（§5.6）。
        //
        // <b>两颗 COALESCE 是 §5.8 那一档在本家的落地形，而它是被集成册量出来的</b>：C# 的 <c>a + b</c>
        // 把 null 当空串，本家的 <c>||</c> 一跳整式就是 NULL【实测·2200 第二臂】，第一跑里那句
        // <c>Set(o =&gt; o.Note, o =&gt; o.Note + "/尾")</c> 打在 NULL 列上交回的是 <c>NULL</c> 而不是
        // <c>/尾</c>（<c>obj/logs/v37-sqlite-int-1.log</c>，判据的原文在
        // <c>UpdateDeletePathTests.A_string_set_computed_from_the_column_concatenates_and_treats_null_as_empty</c>）。
        // 逐字文本在这里钉住它，是因为集成册那一句只跑<em>写侧</em>的一形；<c>SELECT</c> 侧同一颗模板
        // 没有别的证人。
        Checks.Has("(COALESCE(\"t0\".\"CustomerName\", '') || COALESCE(\"t0\".\"Note\", ''))", sql);
        Checks.Has("||", Provider.Expressions.ConcatOperator);
        Checks.Absent("CONCAT(", sql);

        // 0 基 → 1 基的换算与另六家同一条；越界那一档本家比别家<em>宽</em>【实测·2130/2131：SUBSTR('abcdef',0)
        // 交回整串、负起点从串尾数】，所以 RequiresNonNegativeLength 那道闸不是防它报错，
        // 是防我们把 CLR 会抛的那一档静默发下去（§5.7）。
        var substring = orm().Select<Order>().Select(o => new { Tail = o.CustomerName!.Substring(2) }).ToSql();

        Checks.Has("SUBSTR(\"t0\".\"CustomerName\", @p0 + 1)", substring);

        // INSTR 同序（串在前、子串在后），1 基换算成 CLR 的 0 基、找不到交回 -1【实测·2207 两臂 ∶ 2226 ∶ 2612】。
        // 这颗键写成 <c>Fn.Key(颗, 元数)</c> 而不是字面量：注册侧那颗是 "str.indexOf/2"，
        // 少尾巴上那颗元数会得到一条"这一族没注册这个函数"的红，而那条红读起来像缺功能。
        Assert.True(Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.IndexOf, 2), out var indexOf));
        Checks.Has("(INSTR({0}, {1}) - 1)", indexOf.Template);

        // 同一颗实参用两次在本家登得进来：@p0 在同一句里出现两次是驱动的合法用法【实测·4220 两臂 ∶ 4221 三处】。
        // 这一格与 Gbase8sExpressions 为同一条形把那行删掉是"同一判据两种答案"的第一例——差在参数记号，不在库。
        Assert.True(Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.Blank, 1), out var blank));
        Checks.Has("({0} IS NULL OR TRIM({0}) = '')", blank.Template);
    }

    [Fact]
    public void Date_arithmetic_goes_through_strftime_and_comes_back_normalized_to_the_driver_s_own_shape()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Tomorrow = o.CreatedAt.AddDays(1), Next = o.CreatedAt.AddMonths(2), Year = o.CreatedAt.Year })
            .ToSql();

        Verify("date-arithmetic", sql);

        // 修饰符的单复数两形都收【实测·2213】，本表统一发复数（+1.5 days 才是"一天半"，+1.5 day 没量过、不赌）。
        // 里外两层归一是必需的：STRFTIME 的 %f 交回定长毫秒【实测·2214】，而驱动自己写进库里的是去尾零那一形
        // 【实测·907 ∶ 908】，不归一就是一句"算出来的值与库里存的值不相等"。
        Checks.Has("RTRIM(RTRIM(STRFTIME('%Y-%m-%d %H:%M:%f', \"t0\".\"CreatedAt\", printf('%+f days', @p0)), '0'), '.')", sql);
        Checks.Has("printf('%+d months'", sql);

        // 掩码打在读不懂的文本上交回 NULL 而不是报错【实测·2227/2613】，所以一句 WHERE Year(x)=2026
        // 在一列混着脏数据的文本上会<em>静默漏行</em>——这一条记 §14，形状本身仍然发（六家同形）。
        Checks.Has("CAST(STRFTIME('%Y', \"t0\".\"CreatedAt\") AS INTEGER)", sql);

        // 另三族的算子与两颗会话函数都不许漏进来。
        Checks.Absent("DATEADD", sql);
        Checks.Absent("NUMTODSINTERVAL", sql);
        Checks.Absent("SYSDATE", sql);
        Checks.Absent("GETDATE", sql);
    }

    [Fact]
    public void Now_is_localtime_because_the_library_clock_is_utc_and_the_driver_drops_the_kind()
    {
        var sql = orm().Select<Order>().Where(o => o.CreatedAt > DateTime.UtcNow).ToSql();

        // 'now' 是 UTC【实测·2211 两臂差八小时，与本机偏移正好对上】，所以 Fn.Now 要带 'localtime'。
        // 为什么本家这一颗必须带而别家可以不带：驱动写 DateTime 时整颗丢掉 Kind
        // 【实测·2400 前三行：Unspecified/Utc/Local 落成同一串 23 字符裸时刻】，
        // 而 Local 那一档写的就是本地墙上时间——与"库里最近那一行"可比的只有本地档。
        // 归一命中【实测·2407/2408/2516】。本家也没有"库那台机器的钟"这一格（类注释最后一条）。
        Checks.Has("RTRIM(RTRIM(STRFTIME('%Y-%m-%d %H:%M:%f', 'now', 'localtime'), '0'), '.')",
            Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.Now, 0), out var now) ? now.Template : string.Empty);
        Checks.Has("'localtime'", now.Template);
        Checks.Absent("SYSTIMESTAMP", now.Template);
        Assert.DoesNotContain("SYSUTCDATETIME", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Regex_is_not_registered_here_because_the_operator_has_no_function_until_a_host_supplies_one()
    {
        // REGEXP 的操作符与函数两形都报 no such function【实测·2700/2701】：这一族默认没有那颗函数，
        // 它要宿主进程自己注册，而 Microsoft.Data.Sqlite 没有注册。
        // 把 SupportsRegex 报真是让共用层发一颗这一家没有的算子——而这一格在七家里只有本家能当场问出来
        // （别家是【文档】或【无出处】，本家是一句红话）。
        Assert.False(Provider.Expressions.SupportsRegex);

        // 场上有的是 GLOB【实测·2123】与 LIKE【实测·2119 起】，而 GLOB 的通配是 * ? [ ] 那一族，不是正则。
        // 反斜杠那一颗在这里的真语义要说清：本家<em>没有</em>"默认转义符"这一颗东西
        // 【实测·2119：'a_b' LIKE 'a_b' 命中，裸下划线当通配用】，共用层每次都显式发 ESCAPE 子句，
        // 于是这一位与另六家一样是"允许哪个字符做转义符"【实测·2120 两臂 ∶ 2121 换 # 也认 ∶ 2225 绑定参数那一臂】。
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);

        var sql = orm().Select<Order>().Where(o => o.CustomerName!.Contains("甲")).ToSql();

        Checks.Has("LIKE @p0 ESCAPE '\\'", sql);
        Checks.Absent("REGEXP", sql);
        Checks.Absent("GLOB", sql);
    }

    [Fact]
    public void Greatest_and_least_use_the_variadic_max_because_the_library_has_no_three_argument_form()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Top = o.Amount > 1m ? o.Amount : 1m, Bigger = Math.Max(o.CustomerId, o.Id) })
            .ToSql();

        // 本家没有 GREATEST/LEAST【文档·SQLite 把它们列在多参标量聚合那一类】，场上有的就是变参 MAX()/MIN()
        // 【实测·2220 四臂 ∶ 2608 ∶ 2710】。两颗代价都在注释里而不在文本里：定点列上它比的是词法【实测·2608】，
        // 且文本在这一家的类型序里恒大于数【实测·2710】。
        Assert.True(Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.Greatest, 2), out var greatest));
        Checks.Has("MAX({0}, {1})", greatest.Template);
        Assert.True(Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.Least, 2), out var least));
        Checks.Has("MIN({0}, {1})", least.Template);

        // NULL 那一档与 CLR Math.Max 不同：MIN(NULL, 1) 交回 NULL 而不是跳过【实测·2220 第四臂】。
        // 本表不补 COALESCE——补了就把"参与比较的是 NULL"这件事藏起来，而那一格正是共用层 Nullable 契约要露的。
        Checks.Absent("GREATEST", sql);

        // ROUND 的三格读数里最要紧的是"半数远离零进"【实测·2217：2.5 → 3、-2.5 → -3】而 CLR 是银行家——
        // 六家同然，本家只是第一家把它量出来，整格记 §14。一元那一臂补字面量 0 而不是发 ROUND(x)。
        Assert.True(Provider.Expressions.TryGetFunction(QuickFn.Key(QuickFn.Round, 1), out var round));
        Checks.Has("ROUND({0}, 0)", round.Template);
    }

    // ── CodeFirst：自增换的不是后缀而是整条键 ─────────────────────────

    [Fact]
    public void Create_table_replaces_the_whole_key_clause_because_inline_identity_and_a_table_key_are_exclusive()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Verify("ddl-order", sql);

        Checks.Has("CREATE TABLE \"QuickSqlOrder\" (", sql);

        // 三颗各自独立问到同一句 table has more than one primary key【实测·305 ∶ 2806 ∶ 3009】，
        // 所以这里发的不是"型名 + 表级键"而是"键长在列定义里"。内联那一形成得成【实测·2802 ∶ 2803 目录回显】。
        Checks.Has("\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL", sql);
        Checks.Absent("CONSTRAINT \"PK_QuickSqlOrder\" PRIMARY KEY", sql);

        // 型名必须是那颗 INTEGER：BIGINT PRIMARY KEY AUTOINCREMENT 被正面拒【实测·306 ∶ 2807】，
        // 而本家的 long 键按 SqliteTypes 得到的正是 BIGINT——所以自增那一支换掉型名。
        // <b>这不是把键弄窄</b>：这一档存储就是 64 位 rowid，30 亿写进去原样读回、下一号 3000000001【实测·310/310b】。
        Checks.Absent("\"Id\" BIGINT", sql);

        // 取 AUTOINCREMENT 而不是裸 INTEGER PRIMARY KEY，理由是一颗对照：裸形把删掉的最大号<em>还回去</em>
        // 【实测·2809：删掉 2 之后下一号是 2】，带 AUTOINCREMENT 只增【实测·2810：删掉 50 之后是 51】，
        // 而另六家的自增语义都是"只增"。发裸形会让一句 DELETE 之后的 INSERT 复用刚删掉的号，那是这一族独家。
        Checks.Has("AUTOINCREMENT", sql);

        // 类型层的七处分岔在 DDL 里一次读得出：定点落 TEXT【实测·606/2500】、时刻落 TEXT、
        // 跨度落 INTEGER、枚举与布尔落 INTEGER、无宽度字符串落 TEXT 而带宽度的落 VARCHAR(n)。
        Checks.Has("\"CustomerName\" VARCHAR(64) NULL", sql);
        Checks.Has("\"Amount\" TEXT NOT NULL", sql);
        Checks.Has("\"State\" INTEGER NOT NULL", sql);
        Checks.Has("\"CreatedAt\" TEXT NOT NULL", sql);
        Checks.Has("\"PaidAt\" TEXT NULL", sql);
        Checks.Has("\"Note\" TEXT NULL", sql);

        // 宽度在这一家是注释而不是约束【实测·4001 ∶ 4002：VARCHAR(4) 装得下 20 字符】，
        // 而 n 仍然发出去的唯一理由是对账可读——目录原样回显声明字形【实测·22/81/616】。
        Assert.Equal(LengthSemantics.Char, Provider.Types.GetLengthSemantics(orm().Schemas.Contract<Order>().Columns[1]));

        // 另六家的三派自增写法一颗都不许漏进来。
        Checks.Absent("IDENTITY", sql);
        Checks.Absent("AUTO_INCREMENT", sql);
        Checks.Absent("GENERATED", sql);
        Checks.Absent("SERIAL", sql);

        // 多语句串在本家第一次有了活证【实测·2820/2908/2909 ∶ 2915/2916 ∶ 三句串 2822/2912/2913】：
        // 六家共同的 ` ; ` 尾串在另六家从未有读数说收不收（Gbase8sCodeFirst 把它交给"第一台库"）。
        // 尾句不带分号也会被执行【实测·2908 ∶ 2910 ∶ 2912】——驱动不是按分号切完再逐条跑。
        Checks.Has(" ; CREATE INDEX \"IX_QuickSqlOrder_CreatedAt\" ON \"QuickSqlOrder\" ( \"CreatedAt\" ASC )", sql);
    }

    [Fact]
    public void Two_names_the_library_offers_and_this_provider_still_writes_the_affinity_recipe()
    {
        var token = orm().Schemas.Contract<SessionToken>();
        var order = orm().Schemas.Contract<Order>();

        // Guid 落 TEXT 而不落 CHAR(36)：与达梦、MySQL 血统、GBase 8s 三家的答案同宽【实测·V/336/337/608】，
        // 而这一家的 CHAR(n) 那一形不带来任何检查【实测·4001 ∶ 4002】——所以取舍变了形：
        // 那三家是"文本那一档更宽所以取定长"，本家是"两档一模一样宽，取不冒充约束的那一形"。
        Assert.Equal("TEXT", Provider.Types.GetStoreType(token.Columns.Single(c => c.MemberName == nameof(SessionToken.Id))));
        Assert.DoesNotContain(DbCapabilities.Guid_Char36, DeclaredBits());
        Assert.DoesNotContain(DbCapabilities.Guid_Raw16, DeclaredBits());

        // ulong 落 TEXT 而不是 NUMERIC(20,0)：驱动把 64 位无符号往 long 里塞，ulong.MaxValue 现场读回 -1
        // 【实测·505】，绑成不变文本之后原样可回【实测·702】；而 NUMERIC 那一形触发 affinity 会把大整数改写成 REAL。
        Assert.Equal("TEXT", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.Amount))
            with { ClrType = typeof(ulong), StoreTypeName = null }));

        // 浮点只有一档：声明 FLOAT 与声明 DOUBLE 触发同一颗 affinity【实测·22/81/616 的目录回显那一格】，
        // 差别只在目录里那串字——所以本家压根没有"照抄型名会拿到错宽度"那一格可犯（与 GBase 8s 相反）。
        Assert.Equal("REAL", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.Amount))
            with { ClrType = typeof(float), StoreTypeName = null }));

        // 带偏移的时刻在本家存得下，而这是本仓库第一次：MySQL 血统量到的是"存会话偏移"、虚谷是"驱动不认"、
        // GBase 8s 是"库里没有第三型"，三条缺法三条来路；本家是它自己的偏移原样进原样出【实测·Y/509/604】。
        Assert.Equal("TEXT", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.CreatedAt))
            with { ClrType = typeof(DateTimeOffset), StoreTypeName = null }));

        // TimeSpan 与 TimeOnly 一起落 INTEGER 而时刻那一族落 TEXT——七家第一颗"跨度存整数、时刻存文本"的家，
        // 两臂不同形不是疏忽：时刻要能被 strftime 问，跨度要能被减【实测·605/700 TS】。
        Assert.Equal("INTEGER", Provider.Types.GetStoreType(order.Columns.Single(c => c.MemberName == nameof(Order.CreatedAt))
            with { ClrType = typeof(TimeSpan), StoreTypeName = null }));
    }

    [Fact]
    public void A_declared_type_name_here_changes_the_catalog_echo_and_not_the_storage()
    {
        var order = orm().Schemas.Contract<Order>();
        var amount = order.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        // 取可空那一形才走得到发射：非空的 ADD COLUMN 在本家必抛（库自己说的
        // "Cannot add a NOT NULL column with default value NULL"【实测·313 ∶ 2811】，正面证人住在
        // <see cref="Add_column_refuses_two_shapes_and_the_refusals_quote_the_library_verbatim"/>）。
        // 本条要量的那一格与可空性无关——它量的是"人点名的型名走不走原样"，所以换一形不换判据。
        var ddl = Statement(w => Provider.CodeFirst.EmitAddColumn(w, order.Table,
            amount with { IsNullable = true, StoreTypeName = "DECIMAL(18,4)" }));

        // 人已点名的那一格原样 echo（§4 那句"provider 不翻译人写的话"，七家同规），<b>而本家这一格多一句诚实的话</b>：
        // echo 出去的那串字在这一家<em>不改变存储</em>，只改变 affinity 配方与目录回显——
        // DECIMAL(18,4) 里插一句文本会静默变成 REAL 12.34（标度被抹平）【实测·606 ∶ 4206】。
        // 也就是说 [Column(TypeName="DECIMAL(18,4)")] 在这里能得到"目录回显这一串"而得不到"定点存储"，
        // 这是七家里只有本家成立的一格，缺口与 §10.4 并排。
        Checks.Has("\"Amount\" DECIMAL(18,4) NULL", ddl);
        Checks.Has("TEXT", Provider.Types.GetStoreType(amount with { StoreTypeName = null }));
    }

    [Fact]
    public void An_unregistered_clr_type_is_refused_because_the_library_would_have_accepted_any_name()
    {
        var order = orm().Schemas.Contract<Order>();
        var amount = order.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        // 本家的兜底话术与另六家不同一条：这里未登记的型<em>多半也能建</em>（affinity 认任何字形），
        // 所以报的不是"这一族没有那一型"而是"本仓库不知道往哪一臂绑值"——那句要写在拒绝点而不是写在库里。
        // 与 GBase 8s 那一条（DateTimeOffset 抛"库里压根没有第三型"）恰好是两个方向，所以两边的文案
        // 都不许替对方说话：抄来源时连它的理由一起抄，是这一层立过的那条反例。
        var failure = Checks.Raised<QuickSqlException>(() => Provider.Types.GetStoreType(amount with { ClrType = typeof(Uri), StoreTypeName = null }));

        Checks.Has("未登记 CLR 类型", failure.Message);
        Checks.Has("Uri", failure.Message);
        Checks.Has("affinity", failure.Message);
        Checks.Absent("库里没有", failure.Message);
    }

    [Fact]
    public void A_named_schema_qualifies_the_table_and_moves_the_qualifier_onto_the_index_name()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema().Schemas.Contract<Order>()));

        Verify("ddl-qualified", sql);

        // 限定名在本家发得出而建到不存在的模式上报 unknown database【实测·2825】——这一族的"模式"
        // 是一颗 attach 进来的文件【实测·3000/3001】，由连接会话决定而不是由 provider 决定。
        Checks.Has("CREATE TABLE \"app\".\"QuickSqlOrder\" (", sql);
        Checks.Absent("\"APP\"", sql);
        Checks.Absent("\"app\".\"quicksqlorder\"", sql);

        // 换边那一格是本家独有的：限定挂在<em>索引名</em>那一侧。表那一侧不能挂，三颗各自独立问到
        // 同一句语法错【实测·1301 ∶ 2826 ∶ 2919】；而换边不但能过、还落进正确的库里
        // 【实测·3005 ∶ 3006，pragma_index_list('s30','side') 里看得见】。
        // 为什么不能"两边都不限定"：不带限定的 CREATE INDEX 会去 main 里找表并报 no such table: main.s30
        // 【实测·3002】。所以"两张库、表名不在 main"这一格只有换边那一条路。
        Checks.Has("CREATE INDEX \"app\".\"IX_QuickSqlOrder_CreatedAt\" ON \"QuickSqlOrder\" (", sql);
        Checks.Absent(" ON \"app\".\"QuickSqlOrder\"", sql);

        // 于是本家是七家里唯一一颗"限定名可以发而限定表不许发"的——另六家那两半都发得出。
        // 声明 Schemas 这一位的判据也因此要说死：它量的从来是"限定名发得出而库认得它"，不是"模式对象可由 provider 建出"
        // （CREATE SCHEMA 在本家是语法错【实测·2824】）。
        Assert.Contains(DbCapabilities.Schemas, DeclaredBits());
    }

    [Fact]
    public void Add_column_refuses_two_shapes_and_the_refusals_quote_the_library_verbatim()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));
        var key = contract.PrimaryKey[0];

        var add = Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, note));

        Verify("ddl-alter", add);

        Checks.Has("ALTER TABLE \"QuickSqlOrder\" ADD COLUMN \"Note\" TEXT NULL", add);

        // ADD COLUMN 在本家只有"可空、无键、无唯一"那一档是活的【实测·2812 ∶ 2813 ∶ 2814 ∶ 2811】。
        // 非空那一颗抛，而抛的话里要说清缺的是<em>契约里的一颗格子</em>：库自己只会说
        // "Cannot add a NOT NULL column with default value NULL"，那一句更容易读错——
        // 带上 DEFAULT 0 同一条语句就成立【实测·3010】，而 ColumnContract 里没有"列默认值"那一格（§14 新记）。
        var notNull = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, name with { IsNullable = false })));

        Checks.Has("Cannot add a NOT NULL column with default value NULL", notNull.Message);
        // 2026-11-01 契约有了「列默认值」那一格：没有配的那一形照旧拒，话术点名"没配默认值"与配的路子。
        Checks.Has("没有配默认值", notNull.Message);
        Checks.Has("HasDefaultValue", notNull.Message);

        // 自增那一颗也是抛，红话是库自己说过的【实测·312b ∶ 2813 Cannot add a PRIMARY KEY column】。
        var identity = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, key with { IsIdentity = true })));

        Checks.Has("Cannot add a PRIMARY KEY column", identity.Message);

        // 改列精度在本家分两层缺：语法层没有那条语句【实测·F4 ∶ 2815 near "ALTER": syntax error】，
        // 语义层这一问<em>没有对象</em>（声明宽度从不参与存储【实测·4001 ∶ 4002】，
        // 把 VARCHAR(4) 改成 VARCHAR(9) 在库里改不动任何东西）。Alter_Column_Precision 因此不声明，
        // Core 走不到这里，抛是那道闸被绕过时的兜底——七家里本家这一位的缺法独一份。
        var alter = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, name)));

        Checks.Has("没有对象", alter.Message);
        Checks.Has("syntax error", alter.Message);
        Assert.DoesNotContain(DbCapabilities.Alter_Column_Precision, DeclaredBits());

        // UNIQUE 那一档本家不在发射点挡（库里那一档是 2814 报的），交给库——这句留在注释里，
        // 因为它要说的是"这里少挡一颗是故意的"，不是漏了。
    }

    [Fact]
    public void A_composite_key_containing_the_identity_column_is_refused_before_the_ddl_leaves_the_process()
    {
        var model = new ModelConfiguration();

        // 自增列进表级主键这一形在本家不是"少写一句"而是语法级互斥【实测·305 ∶ 2806 ∶ 3009，三颗同一句
        // table has more than one primary key】，所以这一支在<em>发射前</em>挡而不是发出去等库红。
        // 这里用 Fluent 把两列点成键、并给其中一列点名自增——判的是那道闸，不是键的约定层。
        model.Entity<Order>().Property(o => o.Id).IsKey().IsIdentity();
        model.Entity<Order>().Property(o => o.CustomerId).IsKey();

        var orm = new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm.Schemas.Contract<Order>())));

        // 抛的话里那句"库没有「第二把键」这一格"要留着：缺的不是本家的发射而是这一族的语法，
        // 而读代码的人会先怀疑自己少写了一句。
        Checks.Has("互斥", failure.Message);
        Checks.Has("more than one primary key", failure.Message);

        // 同一条语句里的第二档：单列具名表级主键也收【实测·3007/3008】，所以挡的从来是"自增列进了表级键"，
        // 不是"表级键本身"——下面那一格（非整数族上的自增）用的正是只有一把键的模型。
        Assert.Equal(2, orm.Schemas.Contract<Order>().PrimaryKey.Length);
    }

    [Fact]
    public void An_identity_on_a_column_that_is_not_an_integer_family_is_refused_by_name()
    {
        var model = new ModelConfiguration();

        // 判据取"算出来的型名里有没有 INT"：有则换成 INTEGER 只改声明字形而不改存储【实测·310】，
        // 没有则抛。这里把一根字符串列点成自增主键，量的就是那一道闸。
        model.Entity<Order>().Property(o => o.Note).IsKey().IsIdentity();

        var keyed = new QuickSqlOrm(
            new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));

        // 类型层不判自增（共用层先定型后判自增，Gbase8sTypes 第三条记过的那次改判在本家逐字成立），
        // 所以 GetStoreType 在这里<em>不该</em>抛——断言写下来是因为下一版可能想把这一格挪进 SqliteTypes。
        var contract = keyed.Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        Assert.Equal("TEXT", Provider.Types.GetStoreType(note));

        var failure = Checks.Raised<QuickSqlException>(
            () => Statement(w => Provider.CodeFirst.EmitCreateTable(w, contract)));

        // 红话是库自己说过的原话【实测·306 ∶ 2807 两颗同一句，后者是 BIGINT】。
        Checks.Has("AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY", failure.Message);
        Checks.Has("INTEGER", failure.Message);
    }

    [Fact]
    public void A_column_comment_returns_false_because_the_word_is_a_syntax_error_here()
    {
        var contract = orm().Schemas.Contract<Order>();
        var commented = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName)) with { Comment = "客户名" };

        var writer = NewWriter();

        // 那一颗 false 是第二次（第一次是 Gbase8sCodeFirst），而本家更硬：那一家是"注释在目录表里而不在语句里"，
        // 本家是 COMMENT 这个词在本族是语法错【实测·F5 ∶ 2828 near "COMMENT": syntax error】。
        // 而且这一族<em>没有任何</em>列注释的落处：sqlite_master 回显的是引擎自己规范化的 DDL 文本
        // 【实测·3013】，所以"把注释夹进尾巴再靠目录读回来"这条路也不通。
        Assert.False(Provider.CodeFirst.TryEmitColumnComment(writer, contract.Table, commented));
        Assert.Equal(string.Empty, writer.ToSqlString());

        Assert.DoesNotContain(DbCapabilities.Comment_On_Column, DeclaredBits());

        // 它<em>不在交集里已经一笔了</em>（上一笔 GBase 8s 刚把它扣掉），所以本家这一颗 false 不再缩交集——
        // 交集掉的是 Alter_Column_Precision 那一颗，而七家交集从此只剩 Schemas 一位。
        var ddl = Statement(w => Provider.CodeFirst.EmitCreateTable(w, contract));

        Checks.Absent("COMMENT", ddl);
    }

    [Fact]
    public void A_full_text_index_is_refused_while_the_library_has_a_virtual_table_nobody_measured()
    {
        var contract = orm().Schemas.Contract<Order>();

        var plain = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("IX_QuickSqlOrder_Note", IndexKind.NonClustered, [nameof(Order.Note)]) { ColumnNames = ["Note"] }));

        // 方向两颗关键字都发【实测·307 ∶ 2816】，与虚谷同数而理由不同：那一家是"产物在这里比文档话多"，
        // 本家是当场建成的索引在 pragma_index_list 里看得见【实测·2819】。
        Checks.Has("CREATE INDEX \"IX_QuickSqlOrder_Note\" ON \"QuickSqlOrder\" ( \"Note\" ASC )", plain);

        var descending = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("IX_QuickSqlOrder_NoteDesc", IndexKind.NonClustered, [nameof(Order.Note)]) { ColumnNames = ["Note"], Descending = true }));

        Checks.Has("\"Note\" DESC", descending);

        var unique = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("UX_QuickSqlOrder_Note", IndexKind.Unique, [nameof(Order.Note)]) { ColumnNames = ["Note"] }));

        Checks.Has("CREATE UNIQUE INDEX", unique);

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("FT_QuickSqlOrder_Note", IndexKind.FullText, [nameof(Order.Note)]) { ColumnNames = ["Note"] })));

        // 抛的话这次要说清一件事：这一族有 fts5 那一整族虚表，而本仓库对它<em>一条读数都没有</em>
        // （28-30 轮问的是 DDL 主干）。所以"这一家有全文而没有那条语句"在别家是【文档】档，
        // 在本家是【实测】(没有那条语句) 加【未量】(那一族虚表)两颗并排——这句是本文件里最窄的一条主张。
        Checks.Has("fts5", failure.Message);
        Checks.Has("一条读数都没有", failure.Message);

        // 部分索引（WHERE 那一支）这一族确实有【实测·2818】，而 IndexContract 里没有那一颗——
        // 与 XuguCodeFirst 那条 BITMAP 同一格，只是本家那一支更常用。
        Assert.DoesNotContain("FILTERED", Enum.GetNames<IndexKind>());
    }

    [Fact]
    public void A_rowversion_declaration_is_echoed_because_this_library_accepts_any_spelling()
    {
        var contract = orm().Schemas.Contract<InventoryItem>();

        // 与虚谷、GBase 8s 那两家不同：那两家的 DDL 发射点拒 rowversion（虚谷建得成而读不回、
        // 本家按缺席读），<b>本家不拒</b>——因为 affinity 让这一族接受任何声明字形，
        // 而"人写下的名字"这一格在 provider 侧的纪律是原样 echo（§4）。
        // 所以这一张表在本家会建成，而 [ConcurrencyToken] 的语义在这一列上<em>没有任何东西保证</em>：
        // 这一族没有自动更新的版本列，值必须由应用给（SqliteConcurrency.Mode 就是 TimestampColumn）。
        // 这一格今天没有读数（探针没问过 rowversion 那一串字被回显成什么），所以它记 §14 而不写成断言。
        var ddl = Statement(w => Provider.CodeFirst.EmitCreateTable(w, contract));

        Checks.Has("\"RowVersion\" rowversion NULL", ddl);

        // 这一张表的主键仍然是那根自增 Id，而它在本家换成内联那一形——本行量的不是键而是"声明名照样穿过"。
        Checks.Has("\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL", ddl);

        // 第二根并发列（可空 DateTime?）落 TEXT，而文本上的等值比的是写法【实测·3304 ∶ 3305】——
        // 这一句留在这里是因为它是"能不能当版本列"在本家的真实判据：它是否只由本仓库写过。
        Checks.Has("\"CheckedAt\" TEXT NULL", ddl);
        Assert.Equal(ConcurrencyMode.TimestampColumn, Provider.Concurrency.Mode);
    }

    // ── 内联字面量：本家在这一格争的是正确性而不是计划缓存 ────────────

    [Fact]
    public void Inlined_literals_must_match_the_binding_shape_character_by_character()
    {
        // 库里是文本、等号比的也是文本，所以内联的形状与绑定侧差一齿就是一句静默空集【实测·329】。
        // 于是本家这一格每一支都必须与 SqliteValues.Normalize 逐字同形——这是七家里只有本家需要检查的：
        // 另六家这两条路各自都能被库纠正。
        Assert.True(Provider.Values.TryInlineLiteral(12.3400m, null, out var money));
        Assert.Equal("'12.3400'", money);

        Assert.True(Provider.Values.TryInlineLiteral(18446744073709551615ul, null, out var wide));
        Assert.Equal("'18446744073709551615'", wide);

        // 布尔与列型同边（库里那一格是 1/0【实测·607/335】）。
        Assert.True(Provider.Values.TryInlineLiteral(true, null, out var flag));
        Assert.Equal("1", flag);

        // 可变宽小数位：内联不能写定宽 7 位（与库里那一串不相等），也不能截到 3 位（本家存的是 100 纳秒）。
        // 这一条规则在四种小数宽度上逐字对过【实测·907 ∶ 908 ∶ 921/922 ∶ 2400 id 5/6】。
        Assert.True(Provider.Values.TryInlineLiteral(new DateTime(2026, 3, 4, 5, 6, 7, 890), null, out var moment));
        Assert.Equal("'2026-03-04 05:06:07.89'", moment);

        Assert.True(Provider.Values.TryInlineLiteral(new DateTime(2026, 3, 4, 5, 6, 7, 456), null, out var millis));
        Assert.Equal("'2026-03-04 05:06:07.456'", millis);

        // Guid 的 'D' 形小写，与驱动写入的那一形连大小写都一样【实测·608】。
        // 这一句在本家不是洁癖：文本等号区分大小写而 LIKE 不区分【实测·97 ∶ 实测·96】，
        // 所以大写形在 = 上是一场静默空集。
        Assert.True(Provider.Values.TryInlineLiteral(Guid.Parse("12345678-1234-1234-1234-123456789ABC"), null, out var id));
        Assert.Equal("'12345678-1234-1234-1234-123456789abc'", id);

        // 跨度落整数（列声明与绑定两侧都是 INTEGER【实测·605】）。
        Assert.True(Provider.Values.TryInlineLiteral(TimeSpan.FromMinutes(90), null, out var span));
        Assert.Equal((TimeSpan.FromMinutes(90).Ticks).ToString(System.Globalization.CultureInfo.InvariantCulture), span);

        // 两档不发：非有限浮点，和大对象。后者是本家独有的那一格——X'…' 那一形<em>看着能用</em>而本次没有点位，
        // §5.8 的纪律是不发没问过的写法；"有能力发而不发"在本家是第一次。
        Assert.False(Provider.Values.TryInlineLiteral(double.NaN, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(new byte[] { 1, 15 }, null, out _));

        // DateTimeOffset 本家<em>发</em>，而它是七家里唯一一颗内联了带偏移文本的实现【实测·Y/509/604】。
        Assert.True(Provider.Values.TryInlineLiteral(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(8)), null, out var withOffset));
        Assert.Contains("+08:00", withOffset, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parameter_size_and_dbtype_slots_are_measured_to_not_matter_here()
    {
        var contract = orm().Schemas.Contract<Order>();
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        var parameter = Provider.Values.CreateParameter("@p0", "客户甲", name);

        // Size 是七家里唯一一颗"实测证明完全不被读"的【实测·500/500b/501】，DbType 同理（类注释第一条）。
        // 那两格填的意义只剩"绑定意图唯一可读的地方"，而它必须与列一致——这一点本家比别家更要紧：
        // 因为驱动不看 DbType，"落成什么"完全由 Normalize 返回的那颗 CLR 型决定【实测·500 那句 Int64 读数】。
        Assert.Equal(64, parameter.Size);
        Assert.Equal(System.Data.DbType.String, parameter.DbType);

        var money = Provider.Values.CreateParameter("@p0", 12.3400m, contract.Columns.Single(c => c.MemberName == nameof(Order.Amount)));

        // 归一之后的形状喂给后两颗（六家喂原始值），所以这里看到的是文本而不是 Decimal——
        // "值说是文本而 DbType 说是定点"那种矛盾在本家不许出现（§13.5 那条"注释与代码不许各说一套"）。
        Assert.IsType<string>(money.Value);
        Assert.Equal("12.3400", money.Value);
        Assert.Equal(System.Data.DbType.String, money.DbType);
    }

    // ── 槽位本身：这一档声明了什么、没声明什么 ────────────────────────

    [Fact]
    public void The_family_declares_six_bits_and_the_old_five_all_have_a_live_reading_behind_them()
    {
        var declared = DeclaredBits();

        // 5 位与 MySQL 血统同一档（census 因此是 17 ∶ 14 ∶ 7 ∶ 6 ∶ 5 ∶ 5 ∶ 4），
        // <b>而本家与另六家不同的不是位数是档位</b>：另六家里只有虚谷那一本翻过 <c>Live</c>（四位），
        // 而那四位的执行见证是人把基线文本逐句敲给那台库（recon 的 G-10 段）；本家每一位后面跟的
        // 是一颗活库当场交回的东西（逐位的编号写在 SqliteProvider 类注释第四段）。
        // 【v0.37 第四笔】原段落此处写的是"另六家每一位后面最多跟离线文本已钉"——那一句当时就不成立
        // （虚谷那四位已经翻了半版），而本笔之后它连反面都不成立：本家有两行现在也翻成了 <c>Live</c>，
        // 且这两行是这本账第一例完全由 ORM 产出的语句撑起来的。逐位对 §4 那句判据的读法记台账，不记这里。
        // 【本笔（ClickHouse 重开·笔一）】第 6 位是 Transactions —— 内检索引位（内核写路径按位分岔），
        // 不在那串"活库读数"的账里；旧的 5 + 新的 1 一起数完这一格。
        Assert.Equal(6, declared.Length);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, declared);
        Assert.Contains(DbCapabilities.Returning, declared);
        Assert.Contains(DbCapabilities.Identity_ExplicitInsert, declared);
        Assert.Contains(DbCapabilities.Schemas, declared);
        Assert.Contains(DbCapabilities.MultiStatement, declared);
        Assert.Contains(DbCapabilities.Transactions, declared);

        // 三格不声明而它们都是<em>问过才知道</em>的：
        // CaseSensitive_Identifiers——库里标识符不区分大小写【实测·12/19/18】，这一位在本家第一次是"问过而答否"；
        Assert.DoesNotContain(DbCapabilities.CaseSensitive_Identifiers, declared);

        // Boolean_Type——typeof() 对 BOOLEAN 声明的列报 integer【实测·607】，库里没有布尔这一型；
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, declared);

        // Guid_Raw16 / Guid_Char36——本家的两档同宽【实测·V/336/337/608】而取不冒充约束的那一形；
        Assert.DoesNotContain(DbCapabilities.Guid_Char36, declared);
        Assert.DoesNotContain(DbCapabilities.Guid_Raw16, declared);

        // Comment_On_Column——COMMENT 是语法错【实测·F5 ∶ 2828】；
        Assert.DoesNotContain(DbCapabilities.Comment_On_Column, declared);

        // Alter_Column_Precision——语法层没有那条语句【实测·F4 ∶ 2815】而语义层没有对象【实测·4001 ∶ 4002】，
        // 于是七家交集第一次只剩一位。
        Assert.DoesNotContain(DbCapabilities.Alter_Column_Precision, declared);

        // Upsert_OnConflict——本家有 UPSERT…DO UPDATE 而且量过【实测·130/421】，而共用层不发它
        // （.OnConflict( 在 src/ 里零处）。这一位的缺席与 MultiResultSets 那颗同判据：
        // 声明的判据是"代码今天真的走到那一条语句"，不是"这一族语法里有"。
        Assert.DoesNotContain(DbCapabilities.Upsert_OnConflict, declared);
        Assert.DoesNotContain(DbCapabilities.MultiResultSets, declared);
    }

    [Fact]
    public void The_bulk_slot_is_the_fourth_one_to_answer_null_and_the_reason_is_a_missing_number()
    {
        Assert.Null(Provider.Bulk);

        var plan = orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        // 没有能举证的参数预算数字 ⇒ 交回 null，分块器走那颗 100 的保守兜底（与达梦、虚谷、GBase 8s 同档）。
        // 本家的来路比那三家短一截：Microsoft.Data.Sqlite 确实有批量接口，而"一批能塞多少颗参数"
        // 这一问在本家<em>没有读数</em>——IBulkStrategy.ParameterBudget 要的正是一个颗数。
        Assert.Equal(100, plan.ParameterBudget);
        Assert.Equal(7, plan.ColumnsPerRow);
        Assert.Equal(600, plan.Chunks.Sum(static c => c.RowCount));

        var xuguPlan = Xugu.Orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        Assert.Equal(xuguPlan.Chunks.Count, plan.Chunks.Count);
        Assert.Equal(xuguPlan.ParameterBudget, plan.ParameterBudget);
    }

    /// <summary>本家声明出来的那几颗位——<see cref="CapabilityLedgerTests"/> 用同一把尺量台账，
    /// 这里把它写成一颗方法是因为上面的断言要的是"名单"而不是掩码整数。</summary>
    static DbCapabilities[] DeclaredBits()
        => Enum.GetValues<DbCapabilities>()
            .Where(static bit => bit != DbCapabilities.None && Provider.Capabilities.HasFlag(bit))
            .ToArray();

    // ── 内部 ─────────────────────────────────────────────────────────

    static SqlWriter NewWriter(IDbProvider? provider = null)
        => new(new StringBuilder(), [], (provider ?? Provider).Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }

    static DialectSql EmitUpdate(UpdateStatement ast)
    {
        var writer = NewWriter();

        Provider.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    static DialectSql EmitDelete(DeleteStatement ast)
    {
        var writer = NewWriter();

        Provider.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    static DialectSql EmitInsert(in InsertStatement ast)
    {
        var writer = NewWriter();

        Provider.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    /// <summary>守卫的文本，与前六家同一手法：这一族<em>有</em> UPDATE…FROM 而发不出合并档那一形
    /// （列名清单被拒），所以合并路径仍然走不到，而守卫仍然要有文本见证 ⇒ 挂在一条不带表源的最小
    /// UPDATE 上发射。两侧别名按 <c>EntityTracker</c> 的真实构造取：表侧走 <c>WriteTargetAlias</c>
    /// （本家是 "t0" 而不是表名，这一格与 Oracle 血统、虚谷、GBase 8s 那三家正对面）。
    /// 那一半"这条语句是不是 SaveChanges 会发的那一条"在本家与前六家同样读不出——它不是。</summary>
    static string GuardText(string member)
    {
        var contract = orm().Schemas.Contract<Order>();
        var column = contract.Columns.Single(c => c.MemberName == member);

        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(amount, new ParameterValue(1m, amount))],
            Where = Provider.ChangeGuard.BuildDifference(column,
                new ColumnExpression(stored, column), new ColumnExpression(side, column)),
        };

        return EmitUpdate(ast).Text;
    }

    /// <summary>并发谓词的文本，与 <see cref="GuardText"/> 同一手法挂在一条最小 UPDATE 上发射。
    /// 收到的实参是<em>列</em>而不是值：那颗 <c>12m</c> 是占位的原值，它出现在参数表里而不是文本里，
    /// 这一格正是本家与另六家共同的主张。</summary>
    static string ConcurrencyText(string member)
    {
        var contract = orm().Schemas.Contract<Order>();
        var column = contract.Columns.Single(c => c.MemberName == member);

        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(amount, new ParameterValue(1m, amount))],
            Where = Provider.Concurrency.BuildPredicate(new ColumnExpression(stored, column), column, 12m),
        };

        return EmitUpdate(ast).Text;
    }

    /// <summary>手搭一份合并 UPDATE，形状与 <c>EntityTracker.WriteMergedAsync</c> 同构（七家各有一份）。
    /// <b>在本家它唯一的用途是"抛得准不准"</b>：发射必然在 <c>EmitJoinedSources</c> 那一句上红——
    /// 与 GBase 8s 那一家同一立场（发得出子句而发不出列名清单，等于发不出这一档）。</summary>
    static DialectSql MergedUpdate(string member)
    {
        var contract = orm().Schemas.Contract<Order>();
        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var key = contract.PrimaryKey[0];
        var set = contract.Columns.Single(c => c.MemberName == member);
        ColumnContract[] columns = [key, set];

        var rows = new IReadOnlyList<SqlExpression>[2];

        for (var row = 0; row < rows.Length; row++)
            rows[row] = [new ParameterValue(7 + row, key), new ParameterValue("值", set)];

        var on = new SqlBinaryExpression(BinaryOperator.Equal,
            new ColumnExpression(stored, key), new ColumnExpression(side, key));

        var ast = new UpdateStatement
        {
            Table = stored,
            Joins = [new JoinClause(JoinKind.Inner, new SourceRows(side, columns, rows), on)],
            Sets = [new SetClause(set, new ColumnExpression(side, set))],
        };

        return EmitUpdate(ast);
    }

    /// <summary>DELETE 侧的表源：本家那一半边是"FROM 与 USING 两形各一句语法错"【实测·1317/1318】，
    /// 所以这一条与 <see cref="MergedUpdate"/> 撞在同一道闸上，而消息里那句"DELETE 那半边两样都没有"由此可指。</summary>
    static DialectSql DeleteWithJoin()
    {
        var contract = orm().Schemas.Contract<Order>();
        var item = orm().Schemas.Contract<OrderItem>();

        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var details = new TableRef(1, "t1", item.Table);

        var id = contract.Columns.Single(c => c.MemberName == nameof(Order.Id));
        var parentOrderId = item.Columns.Single(c => c.MemberName == nameof(OrderItem.OrderId));

        var ast = new DeleteStatement
        {
            Table = stored,
            Joins =
            [
                new JoinClause(JoinKind.Inner, new SourceRows(details, [parentOrderId], [[new ParameterValue(1, parentOrderId)]]),
                    new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, id), new ColumnExpression(details, parentOrderId))),
            ],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, id), new ParameterValue(7, id)),
        };

        return EmitDelete(ast);
    }
}
