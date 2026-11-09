using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Query;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>跨方言族的<em>对拍</em>（§13.2 那句"等价性要在多个 provider 上各跑一遍"的第二、三、四家）。
///
/// <para>它钉的不是"几家产出相同"，而是<b>相同的部分必须逐字相同、不同的部分必须是点名过的那几处</b>。
/// 这个区分很重要：只看"两边各自都绿"的套件里，共用层的一处漏写会让两家的样本<em>同时</em>变红，
/// 于是没人会注意到"这一格本来不该一起动"；对拍把"一起动"变成了断言，任何一边单独漂移都会红。</para>
///
/// <para>从 v0.23 起这里比的是<b>三家</b>，v0.30 起是<b>四家</b>，v0.31 起是<b>五家</b>，v0.37 起配对账本里有<b>六家</b>
/// （新那一家只进配对账本，没有把五家版的那几条 <c>All5</c> 断言换成六家版——那些量的是"第五家落在哪一边"，
/// 与"第六家落在哪一边"是两本账，合并会得出一句"六家哪里都不同"），于是同一类断言有两种形状：只有两家的用例仍用
/// <see cref="Flatten"/>（只折引号），多家的用例用 <see cref="Fold"/>（折掉引号、大小写、别名前的
/// <c>AS</c>、参数前缀那四类字形）。<b>两者都不是"忽略差异"的开关</b>：用了哪一类折法，那几处差异就
/// 必须在同一个用例里各自再断言一次"确实不同"，否则对拍就退化成了互相抄。</para>
///
/// <para>第四家带来的不是"又多一处对读"，而是一个<b>方向相反</b>的用法：它落在哪一边才是产出。
/// 写侧它站在共用层那一边、分页尾巴它站在金仓那一边、函数名与布尔守卫它各站在另一家那一边 —— 于是
/// "两两六对"的分岔数从三对的 5/5/3 变成 5/5/5/3/2/4（<see cref="The_four_families_disagree_only_where_a_pinning_test_says_so"/>
/// 逐对点名），而"某一家独有的那一格"必须仍然只在那一格上红。</para>
///
/// <para>第五家把这一节带回了一个新问题：<b>接一家"和某一家几乎逐字同形"的库，对拍还剩什么可判？</b>
/// 虚谷在引号、大小写折叠、别名关键字、参数前缀、写侧目标别名这五处全部与达梦同一边（<c>IdentifierPolicy.Xugu</c>
/// 与 <c>OracleDialect</c> 那几颗口子中任何一颗都没有为它新写形状），所以它带来的四对配对里出现了
/// 十对中<em>最低</em>的那一个数：<b>2</b>（达梦↔虚谷：只有分页与取年份两格）。这份对拍因此第一次显式回答
/// "同形不是漏判"：那五处的同形各有<em>点名它的断言</em>（<c>XuguDialectTests</c> 里那一整批
/// <c>Assert.Equal(dameng…)</c>），而这一文件只负责"不同处必须点名 + 相同处必须逐字"这两半各量一次。
/// 四对新配对里唯一一处<em>另四家都没有</em>的形状是它自己造的那一颗年份函数（<c>GETYEAR</c>：
/// T-SQL 用 <c>DATEPART</c>、另三家都写 <c>EXTRACT</c>，五家三种写法），所以账本里"year"那一格
/// 从<em>两样</em>变成<em>三样</em>，而它是这一版唯一一处新添的函数形状。</para>
///
/// <para><b>金仓、达梦、MySQL 与虚谷这四侧的一切都是离线文本</b>：本机没有那四台库（§13.5 的出处台账），所以这一份证明的是
/// "几家在共用层上形状一致"，不是"几家在库里行为一致"。后者要先有活库，重开条件写在 §14。</para>
///
/// <para>第六家（v0.37 第一笔，GBase 8s / Informix 血统）带来的不是"又多五处对读"，而是<b>这把尺子上第一次量不到的那一档</b>：
/// <see cref="Fold"/> 折的四处字形是按"前缀之后总跟着一个名字"写的，而本家的占位符只有位置形 —— 名字压根不进文本。
/// 于是 <c>where-enum</c> 那一格从"十对全不中"变成"新五对全中"，共用层的地盘从三格读成两格，<b>而这一格红的不是共用层
/// 被谁动了</b>：把 <c>?</c> 加进折法就能让它全绿，而那一折抹掉的正是本家唯一一处<em>别家都没有</em>的产出形状，
/// 所以这一档留在账上、不折（<see cref="The_sixth_family_diverges_from_the_other_five_on_exactly_the_cells_named_below"/>）。
/// 第二格新消息在<em>配对的那一头</em>：本家与金仓的 <c>IdentifierPolicy</c> 按值相等，配对却交出五处不同形，
/// 这是"同一颗策略值 ≠ 同一份产出"第一次在账本上以数字而不是以一句 <c>AS</c> 的缺席出现。年份那一格则从三派涨到四派。</para>
///
/// <para>能力位的声明不进这一份（那是 <see cref="CapabilityLedgerTests"/> 逐位对账的对象），
/// 这里只比文本与参数。</para></summary>
public sealed class DialectParityTests
{
    static QuickSqlOrm sqlServer() => SqlServer.Orm();

    static QuickSqlOrm kingbase() => Kingbase.Orm();

    static QuickSqlOrm dameng() => Dameng.Orm();

    static QuickSqlOrm mysql() => MySql.Orm();

    static QuickSqlOrm xugu() => Xugu.Orm();

    static QuickSqlOrm gbase8s() => Gbase8s.Orm();

    /// <summary>把两家的引号都抹掉。<b>只用在"这一格两家只差引号"的用例上</b>：它折掉标识符策略这一处
    /// 已知的分岔，好让剩下的部分能逐字比较。用了它的用例都同时单独断言了引号本身 —— 否则这个函数
    /// 就成了"忽略差异"的开关，而对拍的全部意义在于差异不许被忽略。
    /// <b>它不认反引号</b>，这是刻意的：目前只有两家的用例（T-SQL↔金仓）走它，而第四家一进来就不止差
    /// 引号一处（分页那颗哨兵就是第二处），那一档全部走 <see cref="Fold"/>。</summary>
    static string Flatten(string sql) => sql.Replace("[", "").Replace("]", "").Replace("\"", "");

    /// <summary>把<em>几家只差字形</em>的那四处折平，好让剩下的部分能逐字比较。折掉的四处是：
    /// <list type="number">
    /// <item>引号：方括号 / 双引号 / 双引号 / 反引号 / 双引号（第四家带进来的是第三颗字形，也是五家里唯一一颗
    /// <em>不取决于会话模式</em>的：<c>ANSI_QUOTES</c> 一开，双引号就成字符串引号，反引号不会）。
    /// <b>第五家没有带进第四颗字形</b>，这一格今天仍然是三颗，而"双引号"那一档从两家变成三家 ——
    /// 折价的字行数没变、字的<em>分布</em>变了，所以这一处仍然单独断言（下面第 1 组那四句现在是五句）；</item>
    /// <item>大小写：<c>IdentifierPolicy.FoldToUpperCase</c> 到这一版有两族会折（达梦与虚谷），余下三家
    /// 保留契约里的原样大小写。两族的来路<em>不同一条</em>：前者按 Oracle 血统的厂商文档，后者按已发布产物里
    /// 那句 <c>INDEX_NAME=UPPER('{0}')</c> —— 而"要不要为第二家改这颗口"的答案是不改：这颗位只决定
    /// "发出去的名字是不是大写"，两族在这一问上同结论，分歧只在<em>为什么</em>同结论，那份理由记在
    /// <c>IdentifierPolicy.Xugu</c> 而不是这里。折它的理由与原来一样：不折就会把"同一种策略的两家"读成"分岔的一家"；</item>
    /// <item>表别名前的 <c>AS</c>：那一格是 <c>DialectBase.SourceAliasKeyword</c>（折大写那两族都没有那个关键字），
    /// 它只在<em>表别名</em>上分岔，投影别名各家都写 <c>AS</c>，所以整串替换不会把一处真分岔折掉；</item>
    /// <item>参数前缀：<c>@p</c> 对 <c>:p</c>，那是 <c>dialect.ParameterPrefix</c> 说好的那一颗字 ——
    /// 五家里两家是冒号（达梦与虚谷），所以这一处折的是<em>两</em>家对<em>三</em>家，而不再是 v0.30 那一份
    /// "一家对三家"。<b>v0.37 第一笔起这一处有一个<em>不成立</em>的前提</b>：它按整串 <c>:p</c> 替换，
    /// 预设了"前缀之后总跟着一个名字"，而第六家的占位符只有位置形（一颗 <c>?</c>，名字不进文本），
    /// 于是它在这一处不是"第四颗字形"而是<em>第五种形</em> —— 这一档不折、也不该折（折了就把"名字不进文本"
    /// 这件真分岔抹平了），后果是 <c>where-enum</c> 那一格在本家与另五家的五对配对里<em>每一对都中</em>，
    /// 见 <see cref="The_sixth_family_diverges_from_the_other_five_on_exactly_the_cells_named_below"/>。</item>
    /// </list>
    /// <b>纪律与 <see cref="Flatten"/> 同一条，而且更严</b>：用到它的每个用例都要把这四处各自再断言一次
    /// "确实不同"。最后一条有边界 —— 它按整串 <c>" AS "</c> 替换、按 <c>:p</c> 替换，所以只用在
    /// 文本里没有字符串字面量的链上（本文件的链都是），有字面量的那一批走 <see cref="Flatten"/>。</summary>
    static string Fold(string sql)
        => sql
            .Replace("[", "").Replace("]", "").Replace("\"", "").Replace("`", "")
            .Replace(":p", "@p")
            .ToUpperInvariant()
            .Replace(" AS ", " ");

    static (string Tsql, string Kes) Both(Func<QuickSqlOrm, string> chain) => (chain(sqlServer()), chain(kingbase()));

    static (string Tsql, string Kes, string Dam, string Myq) All4(Func<QuickSqlOrm, string> chain)
        => (chain(sqlServer()), chain(kingbase()), chain(dameng()), chain(mysql()));

    /// <summary>五家一份。<b>没有把 <see cref="All4"/> 删掉</b>：v0.30 那三格四家版断言（写侧别名、
    /// 分页尾巴、"四家的字"）量的是"第四家落在哪一边"，而第五家进来的这一版问的是另一个问题
    /// （<see cref="The_fifth_family_diverges_from_the_other_four_on_exactly_the_cells_named_below"/>）。
    /// 两问各自成格，合并会得到一句"五家哪里都不同"，那既不是 v0.30 的账、也不是这一版的账。</summary>
    static (string Tsql, string Kes, string Dam, string Myq, string Xgu) All5(Func<QuickSqlOrm, string> chain)
        => (chain(sqlServer()), chain(kingbase()), chain(dameng()), chain(mysql()), chain(xugu()));

    // ── 只差引号的那一批 ─────────────────────────────────────────────

    [Fact]
    public void Quoting_is_the_only_difference_on_a_plain_read_chain()
    {
        // 这条链刻意不带分页：分页一进来就不止差引号了（那是下一条用例的内容），而这一条的全部价值
        // 就在于"共用层写出的主干逐字同形"，混进第二个分岔就读不出它红的原因。
        var both = Both(static orm => orm.Select<Order>()
            .Where(o => o.State == OrderState.Placed && o.Amount > 10m)
            .OrderByDescending(o => o.CreatedAt)
            .ToSql());

        Assert.Equal(Flatten(both.Tsql), Flatten(both.Kes));

        // 两侧各钉一次引号本身：折掉之后比"相同"，不折的时候比"确实不同"。
        Assert.Contains("[t0].[State]", both.Tsql, StringComparison.Ordinal);
        Assert.Contains("\"t0\".\"State\"", both.Kes, StringComparison.Ordinal);
    }

    [Fact]
    public void Paging_is_the_one_clause_that_diverges_inside_an_otherwise_identical_read_chain()
    {
        // 与上一条同链，只把尾巴换成"每家在自己的那一格里写什么"——两家逐字相同的主干在这里断掉，
        // 而断点必须精确落在分页那一段：别处也断了就说明有人把方言知识写进了共用层。
        var both = Both(static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql());

        Assert.Equal(Head(both.Tsql), Head(both.Kes));
        Assert.EndsWith("ORDER BY [t0].[Id] ASC OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY", both.Tsql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY \"t0\".\"Id\" ASC LIMIT @p0 OFFSET @p1", both.Kes, StringComparison.Ordinal);

        // 同一个 @p0 在两家装的不是同一个数（T-SQL 那边是 10，这一家是 5）：序号跟着发射序走，
        // 两家的子句序相反。绑定永远在同一份参数清单内完成，所以这不是缺陷，但跨库读文本的人要提前知道。
        sqlServer().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var tsqlParams);
        kingbase().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var kesParams);

        Assert.Equal(10, (int) tsqlParams[0].Value!);
        Assert.Equal(5, (int) kesParams[0].Value!);

        // 第四家与金仓共用这一条尾巴（LIMIT…OFFSET），连参数序也是同一边：@p0 装的同样是 5。
        // 所以"分页那一格分岔"在四家里是<b>两档</b>而不是四档：三家在尾巴上各写一种，另一家套整条语句。
        // 而两家这一句只差引号字形<em>不等于</em>两家分页同形 —— 只有 OFFSET 的那一条链差的是第四家
        // 自己造的那颗"无上限"哨兵，那一格由 §9.4 与 <c>golden/mysql/paging-skip-only.sql</c> 点名。
        var mys = mysql().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var myParams);

        Assert.EndsWith("ORDER BY `t0`.`Id` ASC LIMIT @p0 OFFSET @p1", mys, StringComparison.Ordinal);
        Assert.Equal(5, (int) myParams[0].Value!);
    }

    /// <summary>到 <c>ORDER BY</c> 之前的那一段（引号折掉）。<b>先在折完之后找</b>，因为两种引号长度不同，
    /// 拿一家的下标去切另一家会切在词中间。</summary>
    static string Head(string sql)
    {
        var flat = Flatten(sql);

        return flat[..flat.IndexOf("ORDER BY", StringComparison.Ordinal)];
    }

    [Fact]
    public void A_select_side_join_stays_a_join_because_the_fold_is_confined_to_the_write_side()
    {
        var both = Both(static orm => orm.Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => c.City == "上海")
            .Select((o, c) => new { o.Id, c.Name })
            .ToSql());

        Assert.Equal(Flatten(both.Tsql), Flatten(both.Kes));

        // SELECT 一侧的 JOIN 两家同形：UPDATE … FROM 那个"只吃逗号表源"的限制是写侧的事，
        // 它不许漏进读侧（漏了就等于把金仓的特殊性当成通用事实，而 §9.4 的三套分页同一条理由）。
        Assert.Contains("INNER JOIN QuickSqlCustomer AS t1 ON t0.CustomerId = t1.Id", Flatten(both.Kes), StringComparison.Ordinal);
    }

    [Fact]
    public void An_insert_carries_the_same_shape_without_key_retrieval_but_an_update_declares_its_alias_elsewhere()
    {
        var order = new Order { CustomerName = "甲", CustomerId = 1, Amount = 2m, State = OrderState.Placed, CreatedAt = new DateTime(2026, 1, 1) };

        Assert.Equal(
            Flatten(sqlServer().Insert(order).ToSql()),
            Flatten(kingbase().Insert(order).ToSql()));

        // 第三家的 INSERT 也在同一格里：它<em>没有</em> OUTPUT 也没有 RETURNING（§9.3 回填那一档它给不出批量形态），
        // 所以折掉前缀字形之后三家逐字同形。这一句要是红了，先怀疑的不是达梦，是有人往共用层的插入语句里
        // 加了只有某一家才写的东西。
        Assert.Equal(
            Fold(sqlServer().Insert(order).ToSql()),
            Fold(dameng().Insert(order).ToSql()));

        // 第四家同样落在这格里（它没有 RETURNING，与另两家无出参的一边）：三家那句红过之后它才可能红，
        // 而它红了说明有人往共用层的插入语句里加了东西 —— 与上一句同一个判据，多一家就多一次见证。
        Assert.Equal(
            Fold(sqlServer().Insert(order).ToSql()),
            Fold(mysql().Insert(order).ToSql()));

        // 第五家<em>不能</em>并进上面那三句里。v0.34 起它连回填档都不带 RETURNING 了（改"逐条 + 同句命令回读"），
        // 但带并发列那一种形状仍会走到 EmitReturning —— "它没有 OUTPUT、而 RETURNING 只在别的路径上"，
        // 所以不带出参的那条链（上面的 <c>Insert(order).ToSql()</c>，没调 ExplainKeys）才是它落的地方。
        // 这一句与方言套件里的 ExplainKeys 用例合起来读才是这一格的全貌：差异在"要不要回填、怎么回填"那一句上，不在主干上。
        Assert.Equal(
            Fold(sqlServer().Insert(order).ToSql()),
            Fold(xugu().Insert(order).ToSql()));

        // UPDATE 是第二个分岔点，而且分在开头而不是尾巴上：T-SQL 的 <c>UPDATE [t0] … FROM [表] AS [t0]</c>
        // 把别名声明挪到 FROM 里（目标位不接受 AS），共用层那份 ANSI 写法是 <c>UPDATE "表" AS "t0"</c>。
        // 少掉这一格的话，"SET 两侧同形"会被误当成"整条 UPDATE 同形"。
        Func<QuickSqlOrm, string> update = static orm => orm
            .Update<Order>()
            .Set(o => o.Amount, o => o.Amount + 1m)
            .Where(o => o.Id == 7)
            .ToSql();

        Checks.Has("UPDATE [t0] SET [Amount] = [t0].[Amount] + @p0 FROM [QuickSqlOrder] AS [t0] WHERE [t0].[Id] = @p1", update(sqlServer()));
        Checks.Has("UPDATE \"QuickSqlOrder\" AS \"t0\" SET \"Amount\" = \"t0\".\"Amount\" + @p0 WHERE \"t0\".\"Id\" = @p1", update(kingbase()));

        // 第三家那句是<em>第三种</em>写法，不是前两家的某一种：它的目标位既不接受别名也没有别名可引，
        // 于是 SET 与 WHERE 里的列全部用<em>表名</em>自限定（dialect.WriteTargetAlias 交回的是表名本身）。
        // 三家在这里凑齐了"同一件事的三种写法"，而这一格是 SPI 里那颗新增口的全部理由。
        Checks.Has("UPDATE \"QUICKSQLORDER\" SET \"AMOUNT\" = \"QUICKSQLORDER\".\"AMOUNT\" + :p0 WHERE \"QUICKSQLORDER\".\"ID\" = :p1", update(dameng()));

        // 第四家站在共用层那一边（<c>UPDATE 表 AS 别名</c>），所以"同一件事的写法数"到第四家仍然是
        // <b>三种而不是四种</b>。这里记的不是数字，是新增一族时真正要回答的问题：<em>它落在哪一边</em> ——
        // 而"落在别人那一边"同样是产出：一口三档（表别名 / 别名挪进 FROM / 没有别名）被三家各用一次，
        // 接口就不该写成 <c>if (是达梦)</c>。下面那一句才是这一段的主张：折掉字形后两家逐字同形。
        Checks.Has("UPDATE `QuickSqlOrder` AS `t0` SET `Amount` = `t0`.`Amount` + @p0 WHERE `t0`.`Id` = @p1", update(mysql()));

        Assert.Equal(Fold(update(kingbase())), Fold(update(mysql())));

        // 第五家落在"第三种写法"那一边，而且与达梦逐字同形（只有它自己的函数表与分页尾巴是另一回事）：
        // 于是这一格从"三种写法、四家用了三次"变成<em>三种写法、五家用了四次</em>，而 SPI 的结论不变 ——
        // 一颗口接三种形状，第五家不需要新口。这一句是那一格的可执行版本：折掉字形后与达梦一字不差。
        Assert.Equal(Fold(update(dameng())), Fold(update(xugu())));
        Checks.Has("UPDATE \"QUICKSQLORDER\" SET \"AMOUNT\" = \"QUICKSQLORDER\".\"AMOUNT\" + :p0 WHERE \"QUICKSQLORDER\".\"ID\" = :p1", update(xugu()));
    }

    [Fact]
    public void Even_a_bare_delete_diverges_because_t_sql_declares_the_alias_in_a_second_from()
    {
        Func<QuickSqlOrm, string> delete = static orm => orm.Delete<Order>().Where(o => o.Id == 7).ToSql();

        var both = Both(delete);

        // 这一格原本记的是"没有 JOIN 的 DELETE 两家只差引号"，跑出来才知道连不带表源都不同形：
        // T-SQL 的别名要在第二个 FROM 里给，而共用层那份 ANSI 写法把"表 + AS 别名"放在 DELETE FROM 之后。
        // 分岔点比原先记的早一档 —— 记错的那一半正是这条断言的价值（它把推测换成了实测）。
        Assert.NotEqual(Flatten(both.Tsql), Flatten(both.Kes));
        Checks.Has("DELETE FROM [t0] FROM [QuickSqlOrder] AS [t0] WHERE [t0].[Id] = @p0", both.Tsql);
        Checks.Has("DELETE FROM \"QuickSqlOrder\" AS \"t0\" WHERE \"t0\".\"Id\" = @p0", both.Kes);

        // 第三家：连"另一种别名写法"都不是 —— 它没有目标别名，谓词用表名自限定。
        // 这一句与上一条 UPDATE 是同一个口的两侧（WriteTargetAlias），所以两条一起红才算那个口动了。
        var dam = delete(dameng());

        Assert.NotEqual(Fold(both.Kes), Fold(dam));
        Checks.Has("DELETE FROM \"QUICKSQLORDER\" WHERE \"QUICKSQLORDER\".\"ID\" = :p0", dam);

        // 第四家与上一条里的 UPDATE 同边：它有 ANSI 的那个别名，所以这句折掉字形后与金仓逐字同形。
        // 两家同形是"共用层的写侧主干没被任何一家改走"的第二处见证 —— 这一格原本只有金仓一家撑着。
        var myq = delete(mysql());

        Assert.Equal(Fold(both.Kes), Fold(myq));
        Checks.Has("DELETE FROM `QuickSqlOrder` AS `t0` WHERE `t0`.`Id` = @p0", myq);

        // 第五家与上面那句 UPDATE 同一个口（WriteTargetAlias），所以这一格与上一格是同一颗位的两侧：
        // 它落在达梦那一边，折掉字形与达梦逐字同形。这一家单独一句的用意在<em>成对</em>：
        // 别名这颗口同时管 SELECT 的表源与写侧的目标位，只改一侧会在"上面那条 UPDATE 绿、这条 DELETE 红"里现形。
        var xgu = delete(xugu());

        Assert.Equal(Fold(dam), Fold(xgu));
        Checks.Has("DELETE FROM \"QUICKSQLORDER\" WHERE \"QUICKSQLORDER\".\"ID\" = :p0", xgu);
    }

    [Fact]
    public void Parameter_names_are_identical_because_no_family_renumbers_or_switches_to_positional()
    {
        Func<QuickSqlOrm, string> chain = static orm => orm.Select<Order>()
            .Where(o => o.CustomerName == "甲" && o.CustomerId > 1 && o.Amount < 9m)
            .ToSql(out var parameters) + "|" + string.Join(",", parameters.Select(p => p.Name));

        var both = Both(chain);
        var dam = chain(dameng());

        Assert.Equal(3, Count(both.Tsql));

        // 这一家没有改走 $1 风格的位置占位符：驱动侧是命名参数（Npgsql 与 Kdbndp 的 @name 都成立），
        // 而共用层只认 dialect 给的那一颗前缀。这一格钉的是"它不会顺手以为 PG 系就该用位置参数"。
        Assert.Equal("@p0,@p1,@p2", Names(both.Tsql));
        Assert.Equal(Names(both.Tsql), Names(both.Kes));

        // 第三家同样没有改走位置占位符，只是把那颗字换成了冒号：<b>编号序列四家一致，字形三家一致</b>。
        // 这一格是 Fold 里那次 ":p"→"@p" 的出处 —— 没有它，那个替换就是在抹一处没人记过的差异。
        Assert.Equal(":p0,:p1,:p2", Names(dam));
        Assert.Equal(Names(both.Tsql), Names(dam).Replace(':', '@'));
        Assert.Equal(Count(both.Tsql), Count(dam));

        // 第四家让"字形那一档"从 2∶1 变成 3∶1：它照抄 T-SQL 与 PG 系那一颗 @，于是上面那次替换
        // 今天只服务一族。这一句钉的是"接一家新血统不会顺手以为它的参数另有规矩"——它确实另有规矩
        // （会话变量 <c>@x</c>、局部变量 <c>DECLARE</c>），但那与占位符前缀不是同一格。
        var myq = chain(mysql());

        Assert.Equal(Names(both.Tsql), Names(myq));
        Assert.Equal(Count(both.Tsql), Count(myq));

        // 第五家把"字形那一档"从 3∶1 改成 <b>2∶3</b>：冒号那一侧不再只有一家。这一句钉的是新增一族的
        // 一条默认纪律 —— 它可以复用别家已经证过的<em>形状</em>（编号序列、命名风格），但每一家那一颗字
        // 仍要由这一族自己的产物或文档举一次证：虚谷这一颗来自两份 shipped 适配器共用的冒号占位符【产物】，
        // 不是"达梦也这么写所以跟着写"。位置占位符（$1 那一型）到今天仍然一家都没有。
        var xgu = chain(xugu());

        Assert.Equal(":p0,:p1,:p2", Names(xgu));
        Assert.Equal(Names(dam).Replace(':', '@'), Names(xgu).Replace(':', '@'));
        Assert.Equal(Count(both.Tsql), Count(xgu));
    }

    [Fact]
    public void The_five_families_agree_word_for_word_once_the_four_glyph_divergences_are_folded()
    {
        // 这一条最早是两家版，v0.23 三家、v0.30 四家，现在是五家版，而它读起来像"几家差不多" —— 恰好相反：
        // 它的价值全在下面那四组"确实不同"。折平一次比折平五次容易得多，所以这个用例的写法是"先比相同，
        // 再把每处被抹掉的差异各自钉回去"，缺了后半段这个函数就成了对拍里的作弊器。
        var chains = new Func<QuickSqlOrm, string>[]
        {
            static orm => orm.Select<Order>().Where(o => o.State == OrderState.Placed).ToSql(),
            static orm => orm.Select<Order>().InnerJoin<Customer>((o, c) => o.CustomerId == c.Id).Select((o, c) => new { o.Id, c.Name }).ToSql(),

            // §10.7 的全局过滤器：注册、按表匹配、贴进 WHERE 那几步全在共用层，没有一处方言分支 ——
            // 这一格是那个主张唯一的跨五家见证。谓词刻意取租户列而不是布尔列：布尔的守卫本身在两处
            // 分岔（四家写 = 1，只有金仓写 IS TRUE），那是点名过的形状差异、各家的套件各自钉
            // （<c>KingbaseDialectTests</c> 的 filter-soft-delete），混进这里就会把 Fold 变成作弊器。
            static orm =>
            {
                orm.Filters.Add<Ticket>(t => t.TenantId == 3);

                return orm.Select<Ticket>().Where(t => t.Title == "x").ToSql();
            },
        };

        foreach (var chain in chains)
        {
            var all = All5(chain);

            Assert.Equal(Fold(all.Tsql), Fold(all.Kes));
            Assert.Equal(Fold(all.Tsql), Fold(all.Dam));
            Assert.Equal(Fold(all.Tsql), Fold(all.Myq));
            Assert.Equal(Fold(all.Tsql), Fold(all.Xgu));
        }

        var sample = All5(static orm => orm.Select<Order>()
            .Where(o => o.State == OrderState.Placed && o.Amount > 10m)
            .OrderByDescending(o => o.CreatedAt)
            .ToSql());

        // 1) 引号：方括号 / 双引号 / 双引号 / 反引号 / 双引号。第五家<em>没有</em>带进第四颗字形，
        //    而它是"双引号"那一档从两家长成三家的原因 —— 这一处折字数的形状没变，变的是分布。
        //    反引号那一家仍然是唯一一颗不取决于会话模式的（<c>ANSI_QUOTES</c> 只改双引号的性质）。
        Assert.Contains("[t0].[State]", sample.Tsql, StringComparison.Ordinal);
        Assert.Contains("\"t0\".\"State\"", sample.Kes, StringComparison.Ordinal);
        Assert.Contains("\"T0\".\"STATE\"", sample.Dam, StringComparison.Ordinal);
        Assert.Contains("`t0`.`State`", sample.Myq, StringComparison.Ordinal);
        Assert.Contains("\"T0\".\"STATE\"", sample.Xgu, StringComparison.Ordinal);

        // 2) 大小写：五族里两族把<em>每一个</em>标识符折成大写（达梦与虚谷），另三家照契约里的原样写 ——
        //    折的那两家不折就会得到"建出来的是 QUICKSQLORDER、查的是 QuickSqlOrder"两张表（§9.2）。
        //    第五家让这一格从"一族的特性"长成"一族的<em>血统</em>"：两家的来路不同（文档对产物）而结论同一，
        //    这正是把折叠放在 IdentifierPolicy 而不是放在某家方言类里的理由。
        Assert.Contains("\"CustomerName\"", sample.Kes, StringComparison.Ordinal);
        Assert.Contains("\"CUSTOMERNAME\"", sample.Dam, StringComparison.Ordinal);
        Assert.Contains("`CustomerName`", sample.Myq, StringComparison.Ordinal);
        Assert.Contains("\"CUSTOMERNAME\"", sample.Xgu, StringComparison.Ordinal);

        // 3) 表别名前的 AS：两族没有那个关键字（SourceAliasKeyword）。这一格要单独钉，
        //    否则"几家只差大小写"会被当成对那一族的全部认识，而它们在写侧还各有一处只用自己的表名限定。
        Assert.Contains("FROM \"QuickSqlOrder\" AS \"t0\" WHERE", sample.Kes, StringComparison.Ordinal);
        Assert.Contains("FROM \"QUICKSQLORDER\" \"T0\" WHERE", sample.Dam, StringComparison.Ordinal);
        Assert.DoesNotContain(" AS ", sample.Dam, StringComparison.Ordinal);
        Assert.Contains("FROM `QuickSqlOrder` AS `t0` WHERE", sample.Myq, StringComparison.Ordinal);
        Assert.Contains("FROM \"QUICKSQLORDER\" \"T0\" WHERE", sample.Xgu, StringComparison.Ordinal);

        // 4) 参数前缀：两族是冒号风格的命名参数，另三家是 @。名字编号五家都是 p0/p1。
        Assert.Contains("= @p0", sample.Kes, StringComparison.Ordinal);
        Assert.Contains("= :p0", sample.Dam, StringComparison.Ordinal);
        Assert.Contains("= @p0", sample.Myq, StringComparison.Ordinal);
        Assert.Contains("= :p0", sample.Xgu, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filter_on_a_left_joined_table_sits_in_the_on_clause_of_every_family()
    {
        // 「贴进 ON 还是 WHERE」这条分岔只看 JoinKind，不看方言 —— 而它错了是<em>静默</em>改结果集：
        // 左联补的那些 NULL 行会被 WHERE 里的过滤器一起筛掉（NULL 上任何比较都是 UNKNOWN），
        // 左联就退化成内联。所以五家各判一次位置，而不只判一次文本。
        var all = All5(static orm =>
        {
            orm.Filters.Add<Ticket>(t => !t.Deleted);

            return orm.Select<Order>()
                .LeftJoin<Ticket>((o, t) => o.Id == t.TenantId)
                .Where((o, t) => o.Amount > 10m)
                .Select((o, t) => new { o.Id, t.Title })
                .ToSql();
        });

        foreach (var sql in new[] { all.Tsql, all.Kes, all.Dam, all.Myq, all.Xgu })
        {
            var at = sql.IndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase);

            Assert.True(at > 0, $"这条链没有 WHERE，位置判据无从谈起：{sql}");

            // 过滤器在 WHERE 之前 = 它在 ON 里；WHERE 那一段里不许再出现它（出现了就是"两处都贴"，
            // 那比贴错地方更难查，因为它在 T-SQL 侧还会让语句看着"哪儿都对"）。
            Assert.Contains("DELETED", sql[..at], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETED", sql[at..], StringComparison.OrdinalIgnoreCase);
        }

        // 位置一致不等于文本一致，下面这几句是上面那个 foreach 的"确实各处长得不一样"见证：
        // 引号、大小写、别名前的 AS、布尔守卫四处都在这里各自出现一次（前两处已由上一条用例单独钉过）。
        // 第四家把"布尔守卫"从 2∶1 变成 <b>3∶1</b>：它<em>有</em> IS TRUE，而 TINYINT(1) 装得下 2，
        // 于是 IS TRUE 与 = 1 不是同一判据，它选了与另两家同一边（§9.3 与 §10.5 那一格）。
        // 第五家把它长成 <b>4∶1</b>，而这一格的"多数派"是<em>越长越像惯例</em>的：一家写 IS TRUE、
        // 四家写 = 1。这里要防的反向风险是"共用层为了少数派改判" —— 那一改会让四家同时红，
        // 而这四句逐字写出来的文本正是让那一红<em>说得清是谁</em>的东西。
        Assert.Contains("AND NOT [t1].[Deleted] = 1 WHERE", all.Tsql, StringComparison.Ordinal);
        Assert.Contains("AND NOT \"t1\".\"Deleted\" IS TRUE WHERE", all.Kes, StringComparison.Ordinal);
        Assert.Contains("AND NOT \"T1\".\"DELETED\" = 1 WHERE", all.Dam, StringComparison.Ordinal);
        Assert.Contains("AND NOT `t1`.`Deleted` = 1 WHERE", all.Myq, StringComparison.Ordinal);
        Assert.Contains("AND NOT \"T1\".\"DELETED\" = 1 WHERE", all.Xgu, StringComparison.Ordinal);
    }

    [Fact]
    public void The_family_without_a_paging_tail_clause_wraps_the_other_families_whole_statement()
    {
        // 分页那一格五家各写各的，而这里要的不是"又多了几处分岔"，是<b>分岔的位置</b>：
        // 余下四家都只在<em>尾巴</em>上换一句（OFFSET…FETCH / LIMIT…OFFSET 三家共用），只有一族没有可用的尾巴，
        // 只能在整条语句外面再套一层。套的这一层如果不吃共用层的主干，那主干就必须与另外几家逐字同形 ——
        // 下面那一句 Assert.Contains 量的正是这件事：把另外两家那条<em>不带分页</em>的链折平，
        // 它是达梦那一句里括号内的<em>整个</em>内层，一个字都没多、一个字都没少。
        Func<QuickSqlOrm, string> paged = static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).Take(3).ToSql();

        var all = All5(paged);

        Assert.NotEqual(Fold(all.Kes), Fold(all.Dam));
        Assert.NotEqual(Fold(all.Tsql), Fold(all.Dam));

        // 第四家站在尾巴那一边，而且与金仓逐字同形（带 Take 时两家的两个子句都在）：这一句是 §9.4
        // "四套实现里 LIMIT 那一套有两家"最直接的落地形态。它与上面两句合起来才是这一格的全貌 ——
        // 四家里两家用 LIMIT…OFFSET、一家用 OFFSET…FETCH、只有一家套壳。
        Assert.Equal(Fold(all.Kes), Fold(all.Myq));
        Assert.NotEqual(Fold(all.Myq), Fold(all.Dam));

        // 第五家也站在尾巴那一边，而它<em>带 Take 时</em>与金仓、MySQL 两家三家逐字同形 —— 这一句是
        // 这一版对"分页"这一格的全部新增主张：<b>同形只到带 Take 为止</b>。少了 Take 那一句就分岔，
        // 因为这一家与 MySQL 血统各补一颗自己的"无上限"哨兵（2147483647 对 18446744073709551615），
        // 而金仓那一族的 OFFSET 可以单独出现、不需要补。所以三家在这一格上是 1∶2 而不是 3。
        // 下面那一句链刻意取 Skip+Take：换成 Skip-only，这一句就红了，而那一红说的是另一件事。
        Assert.Equal(Fold(all.Kes), Fold(all.Xgu));
        Assert.Equal(Fold(all.Myq), Fold(all.Xgu));
        Assert.NotEqual(Fold(all.Xgu), Fold(all.Dam));

        var skipOnly = All5(static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).ToSql());

        Assert.NotEqual(Fold(skipOnly.Myq), Fold(skipOnly.Xgu));
        Assert.NotEqual(Fold(skipOnly.Kes), Fold(skipOnly.Xgu));

        var body = Fold(kingbase().Select<Order>().OrderBy(o => o.Id).ToSql());

        Assert.Contains("(" + body + ")", Fold(all.Dam), StringComparison.Ordinal);
    }

    // ── 点名过的分岔 ─────────────────────────────────────────────────

    [Fact]
    public void A_create_table_diverges_only_on_the_one_family_that_knows_the_column_the_store_fills()
    {
        // DDL 一侧的对拍。它比的不是"几家同形"（类型名与自增写法四家本来就各一套），
        // 而是"可空性这一格的<em>判据</em>长在哪家"：v0.24 那一档是 provider 落的输入，
        // 所以它只许让落它的那一家分岔。两格并发列各比一次，缺一次就判不动：
        // 只比 rowversion 那一格，"共用层一律 NOT NULL"这条错规则没人红；
        // 只比另一格，"这一家把并发列全写成不可空"也没人红。
        var stamp = All5(static orm => Ddl<AppVersioned>(orm));

        // v0.34 第二笔起 <c>token</c> 不再走 <see cref="All5"/>：第五家在这一格里<em>抛</em>（rowversion 拒收），
        // 而 All5 会让那颗抛在取 tuple 的路上掀掉整格对拍 —— 前四家的三份文本还得逐字比。
        var token = (Tsql: Ddl<InventoryItem>(sqlServer()), Kes: Ddl<InventoryItem>(kingbase()),
            Dam: Ddl<InventoryItem>(dameng()), Myq: Ddl<InventoryItem>(mysql()));

        // 1) 并发列、可库里没有那一型（应用自己递增的版本号，§8.2）：五家全 NULL，而类型名五家四样
        //    （双引号那两族在这里同为 BLOB，所以"一格四样"而不是"一格五样"）。
        Checks.Has("[Stamp] VARBINARY(MAX) NULL", stamp.Tsql);
        Checks.Has("\"Stamp\" BYTEA NULL", stamp.Kes);
        Checks.Has("\"STAMP\" BLOB NULL", stamp.Dam);
        Checks.Has("`Stamp` LONGBLOB NULL", stamp.Myq);
        Checks.Has("\"STAMP\" BLOB NULL", stamp.Xgu);

        // 2) 并发列、而库里那一型只有本家认得：这一格 v0.34 第二笔起是<b>三档</b>而不是两家边：
        //    1 家 NOT NULL（只有它把"值归库给"当真）∶ 3 家 echo NULL（收名字、不校验，InventoryItem 本来就是
        //    SQL Server 专用靶，那三份文本没人会拿去执行）∶ 1 家抛（虚谷拒收，下面单断言）。
        Checks.Has("[RowVersion] rowversion NOT NULL", token.Tsql);
        Checks.Has("\"RowVersion\" rowversion NULL", token.Kes);
        Checks.Has("\"ROWVERSION\" rowversion NULL", token.Dam);
        Checks.Has("`RowVersion` rowversion NULL", token.Myq);

        // 2b) 第五家那一格的 v0.34 第二笔改判：从 echo NULL 换成点名拒收。判据不是"这一家没有那一型"——
        //     库收 CREATE 还会自增（RV-01/RV-11）—— 是<em>那一列读不回来</em>（RV-08/A-06 的
        //     DataOutOfBindException），§5.8 不许建出"看起来能用"的一列。抛点、逐字理由与出处见
        //     <c>XuguCodeFirst.EmitColumnDefinition</c> 与 §14。
        var rejection = Checks.Raised<QuickSqlException>(() => Ddl<InventoryItem>(xugu()));

        Checks.Has("System.DataOutOfBindException", rejection.Message);
        Checks.Absent("rowversion NULL", Ddl<AppVersioned>(xugu())); // 另一格并发列（应用给值）照旧建得出，拒收只点名 rowversion

        // 3) 同一份实体上的第二格并发列五家同档（都 NULL）—— 上面那一格的分岔因此是"一列"而不是"一类"。
        //    第五家在这里<em>不</em>与达梦同形：那一家是 TIMESTAMP(3)，这一家是 DATETIME(3)，
        //    而 MySQL 血统也是 DATETIME(3) —— 于是"时刻型带精度"这一格从 1∶1∶1 长成了 2∶2∶1（T-SQL 那一颗单独），
        //    它量的是类型表而不是任何一颗方言开关。
        //    <b>v0.34 第二笔：第五家的这一格换了取证姿势</b>——整份 <c>Ddl&lt;InventoryItem&gt;</c> 在它那边
        //    先撞拒收、到不了这一列，所以这一格改从<em>同一个发射点的另一支</em>（<c>EmitAddColumn</c>，
        //    与 CREATE TABLE 共用 <c>EmitColumnDefinition</c> 和那颗闸门）取：它同时证明拒收吃的是
        //    那一列、不是那张表、更不是整族开关。
        Checks.Has("[CheckedAt] DATETIME2(3) NULL", token.Tsql);
        Checks.Has("\"CheckedAt\" TIMESTAMP(3) NULL", token.Kes);
        Checks.Has("\"CHECKEDAT\" TIMESTAMP(3) NULL", token.Dam);
        Checks.Has("`CheckedAt` DATETIME(3) NULL", token.Myq);
        Checks.Has("\"CHECKEDAT\" DATETIME(3) NULL", XuguColumnDefinition<InventoryItem>(nameof(InventoryItem.CheckedAt)));

        // 外三家 echo 了 T-SQL 的类型名 <c>rowversion</c>：那是 <c>[Column(TypeName = …)]</c> 的语义
        // （provider 不翻译人写的话），而 InventoryItem 本来就是 SQL Server 专用靶。
        // 这一句写在这里是明记账：<b>那三份文本没有人会拿去执行</b>，它出现的唯一理由是把上面两格隔开。
        // （v0.34 第二笔前这句是"四份"；第五家从 echo 那一边搬到了抛的那一边。）
    }

    /// <summary>虚谷侧单列的建表片段（<c>ALTER TABLE … ADD COLUMN</c> 的整句，断言只吃尾部片段）。
    /// 存在的理由写在上面 3) 的注释里：整表文本自拒收落地后到不了带 <c>rowversion</c> 实体的第二格，
    /// 而这一支与那一支共用同一个发射点，取证不绕开闸门。</summary>
    static string XuguColumnDefinition<TEntity>(string memberName) where TEntity : class
    {
        var orm = xugu();
        var contract = orm.Schemas.Contract<TEntity>();
        var column = contract.Columns.Single(c => c.MemberName == memberName);

        var writer = new SqlWriter(new(), [], orm.Provider.Dialect.ParameterPrefix);

        orm.Provider.CodeFirst.EmitAddColumn(writer, contract.Table, column);

        return writer.ToSqlString();
    }

    /// <summary>一族的整段建表文本。<b>不走 <see cref="Fold"/></b>：DDL 的类型名、模式默认值与自增写法
    /// 四家各有一套，折不动；折得动的只有引号与大小写，而那两处正是上面逐字写出来的东西。</summary>
    static string Ddl<TEntity>(QuickSqlOrm orm) where TEntity : class
    {
        var writer = new SqlWriter(new(), [], orm.Provider.Dialect.ParameterPrefix);

        orm.Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<TEntity>());

        return writer.ToSqlString();
    }

    [Fact]
    public void The_kingbase_text_never_carries_a_token_of_another_family()
    {
        var chains = new Func<QuickSqlOrm, string>[]
        {
            static orm => orm.Select<Order>().ToSql(),
            static orm => orm.Select<Order>().Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026).ToSql(),
            static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).Take(3).ToSql(),
            static orm => orm.Select<Order>().Select(o => new { A = o.Note ?? "x", B = o.Amount == 1m ? 2m : o.Amount }).ToSql(),
        };

        foreach (var chain in chains)
        {
            var sql = chain(kingbase());

            // 名单末尾那颗反引号是 v0.30 添的：第四家带来第三颗引号字形，"不含别家的引号"从此是两颗。
            // 第五家没有再添新字形（它用双引号、冒号、GETYEAR），所以这一格仍然只有一颗要加：
            // GETYEAR —— 它是五家里只有一族写过的年份函数，出现在这里就说明有人把方言知识写进了共用层。
            foreach (var token in new[] { "[", "]", "N'", "FETCH NEXT", "TOP ", "DATEPART", "ISNULL", "dbo.", "`", "GETYEAR" })
                Assert.False(sql.Contains(token, StringComparison.Ordinal), $"金仓侧出现了只属于另四家的 {token}：{Environment.NewLine}{sql}");
        }
    }

    [Fact]
    public void The_sqlserver_text_never_carries_a_token_of_another_family()
    {
        // 反向那半边：共用层如果因为第二个方言族接进来就开始"顺手兼容"（比如把引号统一成双引号），
        // 先红的会是 T-SQL 这一侧的样本，而那条红看起来像"样本被谁改了"。这一格钉的是它不会静默发生。
        var chains = new Func<QuickSqlOrm, string>[]
        {
            static orm => orm.Select<Order>().ToSql(),
            static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).Take(3).ToSql(),
            static orm => orm.Select<Order>().Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026).ToSql(),
        };

        foreach (var chain in chains)
        {
            var sql = chain(sqlServer());

            // 名单从 v0.30 起要挡的是三族而不是一族：反引号（第四家的引号）、LENGTH（第四家吃契约里
            // "多少个字符"那一格的那一颗）、冒号参数前缀（达梦那一颗字）。三颗都是"这一家确实会写、
            // 而 T-SQL 侧一出现就说明有人把方言知识写进了共用层"。
            // 第五家把名单加长的是<em>一颗而不是四颗</em>：它的引号、参数前缀、函数名里只有 GETYEAR 是
            // T-SQL 侧不会写的（另三颗 —— 双引号本来就是挡金仓的、大写折法不在字形名单这一档、
            // LENGTH 与达梦同形且<em>已经</em>在名单上，只是那一颗写作带括号的 " LENGTH("）。
            foreach (var token in new[] { "\"t0\"", " LIMIT ", " USING ", "RETURNING", "IS DISTINCT FROM", "EXTRACT(", "`", " LENGTH(", ":p", "GETYEAR" })
                Assert.False(sql.Contains(token, StringComparison.Ordinal), $"T-SQL 侧出现了只属于另四家的 {token}：{Environment.NewLine}{sql}");
        }
    }

    [Fact]
    public void The_dameng_text_never_carries_a_token_of_the_other_three()
    {
        // 前两家的对拍是"互相不含对方的字"，第三家一进来这件事就变成<em>两两</em>的：它的黑名单得把
        // T-SQL 与 PG 系各点一次，否则"共用层为第三家顺手兼容了一句话"会谁都不红。
        // 名单里没有 EXTRACT：那一格第三家与金仓同形（它确实也是 Oracle 的语法），把它列进来会得到一句假红。
        // 同理，v0.30 添的两颗都没有落进这一家：<c> LIMIT </c> 仍然安全（它不写 LIMIT，写 ROWNUM 套壳），
        // 而反引号是第四家独有的字形。反过来，这一家<em>不能</em>挡 LENGTH —— 那是它与金仓共用的那一颗。
        var chains = new Func<QuickSqlOrm, string>[]
        {
            static orm => orm.Select<Order>().ToSql(),
            static orm => orm.Select<Order>().Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026).ToSql(),
            static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).Take(3).ToSql(),
            static orm => orm.Select<Order>().Select(o => new { A = o.Note ?? "x", B = o.Amount == 1m ? 2m : o.Amount }).ToSql(),
            static orm => orm.Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql(),
        };

        foreach (var chain in chains)
        {
            var sql = chain(dameng());

            foreach (var token in new[] { "[", "]", "N'", "FETCH NEXT", "TOP ", "DATEPART", "ISNULL", "dbo.", " OFFSET ", " LIMIT ", " USING ", "RETURNING", "IS DISTINCT FROM", "\"t0\"", "@p", "`", "CHAR_LENGTH", "GETYEAR" })
                Assert.False(sql.Contains(token, StringComparison.Ordinal), $"达梦侧出现了只属于另四家的 {token}：{Environment.NewLine}{sql}");
        }
    }

    [Fact]
    public void The_mysql_text_never_carries_a_token_of_the_other_three()
    {
        // 第四家的黑名单是这一节里唯一"事前就能预感到会漏"的一颗：前三家各挡两颗字，它要挡三家。
        // 名单按<em>字形</em>而不是按"哪家"点，所以它同时是"共用层不会为第四家顺手兼容谁"的见证：
        // 冒号参数点达梦，双引号点金仓与达梦，方括号与 DATEPART 点 T-SQL。
        // 注意它挡的是 <c>:p</c> 而<em>不是</em> <c>@p</c> —— 这一家的占位符前缀与 T-SQL、PG 系同一颗字，
        // 把 @p 列进来会得到一句必红的假红，而那正是"四家两档"这一格被数出来的地方。
        // 没有挡 LIMIT / OFFSET：那是它自己写的尾巴；也没有挡 EXTRACT —— 它与金仓、达梦同形。
        // 第五家只往这一格里添了一颗：GETYEAR（它唯一的"另四家都不这样写"的函数），而这一家<em>不能</em>
        // 跟着添双引号或冒号 —— 那两处是它与另两家的共有字形，添了就红在自己身上。
        var chains = new Func<QuickSqlOrm, string>[]
        {
            static orm => orm.Select<Order>().ToSql(),
            static orm => orm.Select<Order>().Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026).ToSql(),
            static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(1).ToSql(),
            static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).ToSql(),
            static orm => orm.Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql(),
            static orm => orm.Delete<Order>().Where(o => o.Id == 7).ToSql(),
        };

        foreach (var chain in chains)
        {
            var sql = chain(mysql());

            foreach (var token in new[] { "[", "]", "N'", "FETCH NEXT", "TOP ", "DATEPART", "ISNULL", "dbo.", ":p", "\"t0\"", "\"T0\"", "ROWNUM", "IS DISTINCT FROM", "RETURNING", " USING ", "BTRIM", "SYSDATE", "GETYEAR" })
                Assert.False(sql.Contains(token, StringComparison.Ordinal), $"MySQL 侧出现了只属于另四家的 {token}：{Environment.NewLine}{sql}");
        }

        // 黑名单只会红在"出现了"，不会红在"这一家把别家的字用成了自己的"。下面这几句是补的那一半：
        // 它自己的三颗字必须在 —— 反引号、LENGTH 的<b>带前缀</b>那一颗、以及只有 OFFSET 时那颗哨兵
        // （那一条链带 OrderBy：不带排序的 OFFSET 在共用层就被挡，四家同一判据，与方言无关）。
        var skipOnly = mysql().Select<Order>().OrderBy(o => o.Id).Skip(2).ToSql();

        Assert.Contains("`QuickSqlOrder`", skipOnly, StringComparison.Ordinal);
        Assert.Contains("LIMIT 18446744073709551615 OFFSET @p0", skipOnly, StringComparison.Ordinal);
        Assert.Contains("CHAR_LENGTH(", mysql().Select<Order>().Select(o => new { Length = o.CustomerName!.Length }).ToSql(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_xugu_text_never_carries_a_token_of_the_other_four()
    {
        // 第五家的黑名单是这一节里<em>唯一一份写的时候要靠排除法</em>的：它与达梦共用五处字形
        // （双引号、大写折叠、无 AS 的表别名、冒号前缀、写侧用表名自限定），所以那五处<em>一颗都不能挡</em> ——
        // 挡了会得到一句必红的假红，而"假红之后把名单改短"是最糟的收场（名单短了就等于没挡）。
        // 于是这一份的形状是"外四家独有的那几颗"：方括号与 N' 点 T-SQL，反引号点 MySQL，
        // ROWNUM 点达梦（它与这一家在其余四处同形，只有分页各写一边），双引号小写那两颗点金仓，
        // CHAR_LENGTH 点 MySQL，BTRIM/STRPOS/SUBSTRING( 点金仓，SYSDATE 不能挡（这一家自己写它），
        // EXTRACT( 与 GETYEAR 各挡一边 —— 那一对是这个套件里唯一"两家在同一格上互相成就"的名单项。
        var chains = new Func<QuickSqlOrm, string>[]
        {
            static orm => orm.Select<Order>().ToSql(),
            static orm => orm.Select<Order>().Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026).ToSql(),
            static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).Take(3).ToSql(),
            static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(2).ToSql(),
            static orm => orm.Select<Order>().Select(o => new { Cut = o.CustomerName!.Substring(1, 2), At = o.CustomerName!.IndexOf("x"), Blank = string.IsNullOrWhiteSpace(o.Note), Joined = o.CustomerName + "x" }).ToSql(),
            static orm => orm.Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql(),
            static orm => orm.Delete<Order>().Where(o => o.Id == 7).ToSql(),
            static orm => orm.Insert(new Order { CustomerName = "甲", CustomerId = 1, Amount = 2m, State = OrderState.Placed, CreatedAt = new DateTime(2026, 1, 1) }).ToSql(),
        };

        foreach (var chain in chains)
        {
            var sql = chain(xugu());

            foreach (var token in new[] { "[", "]", "N'", "FETCH NEXT", "TOP ", "DATEPART", "ISNULL", "dbo.", "`", "@p", "\"t0\"", "ROWNUM", "IS DISTINCT FROM", " USING ", "BTRIM", "STRPOS", "SUBSTRING(", "CHAR_LENGTH", "EXTRACT(", "TO_CHAR", "TRUNC(" })
                Assert.False(sql.Contains(token, StringComparison.Ordinal), $"虚谷侧出现了只属于另四家的 {token}：{Environment.NewLine}{sql}");
        }

        // 与 MySQL 那一份同一条补法：黑名单只红在"出现了"，所以这一族自己的三颗字要正面点一次。
        // GETYEAR 是五家里只此一家的一颗（另四家分成 DATEPART 与 EXTRACT 两派），
        // 而 <c>LIMIT 2147483647</c> 是它与 MySQL 血统那颗 2^64−1 的区别 —— 两格各钉一处。
        var paged = xugu().Select<Order>().OrderBy(o => o.Id).Skip(2).ToSql();

        Assert.Contains("LIMIT 2147483647 OFFSET :p0", paged, StringComparison.Ordinal);
        Assert.Contains("GETYEAR(", xugu().Select<Order>().Select(o => new { Year = o.CreatedAt.Year }).ToSql(), StringComparison.Ordinal);
        Assert.Contains("\"QUICKSQLORDER\" \"T0\"", paged, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chunk_count_follows_the_provider_budget_rather_than_a_number_baked_in_the_shared_layer()
    {
        var rows = Enumerable.Range(1, 600).Select(static n => new Order
        {
            CustomerName = "客户" + n,
            CustomerId = n,
            Amount = n,
            State = OrderState.Paid,
            CreatedAt = new DateTime(2026, 3, 4),
        }).ToArray();

        var tsql = sqlServer().InsertRange(rows).Explain();
        var kes = kingbase().InsertRange(rows).Explain();
        var dam = dameng().InsertRange(rows).Explain();
        var myq = mysql().InsertRange(rows).Explain();
        var xgu = xugu().InsertRange(rows).Explain();

        // 同一批 600 行：T-SQL 的预算 2100 要切成 3 块，这一家的 32767 一块就装得下。
        // 这条断言的意义是"分块公式里没有写死过任何一个数"——共用层若抄了一份 2100，两边都会是 3 块，
        // 而金仓侧那一块在真库里会以"参数过多"失败，那是最贵的一种错。
        Assert.Equal(3, tsql.Chunks.Count);
        Assert.Single(kes.Chunks);
        Assert.Equal(tsql.RowCount, kes.RowCount);
        Assert.Equal(tsql.Chunks.Sum(static c => c.RowCount), kes.Chunks.Sum(static c => c.RowCount));
        Assert.True(kes.ParameterBudget > tsql.ParameterBudget);

        // 第三家在这里补上了这个公式的<em>第三种</em>来路：它没有批量策略（Bulk 为 null），于是分块器
        // 走那个 100 的保守兜底，600 行被切成 50 块。
        // 50 不是缺陷也不是结论，它是<em>没有活库拿不到真预算</em>的代价，重开条件写在 §14。
        Assert.Equal(50, dam.Chunks.Count);
        Assert.Equal(100, dam.ParameterBudget);
        Assert.Equal(tsql.RowCount, dam.RowCount);
        Assert.Equal(600, dam.Chunks.Sum(static c => c.RowCount));
        Assert.True(dam.ParameterBudget < tsql.ParameterBudget);

        // 第四家把这一格<em>原来那句话</em>说成了假话：v0.23 记的是"三家的块数互不相同（3 / 1 / 50）"，
        // 而它的预算 65535 是四家里最大的那一颗，600 行 × 7 列 = 4200 颗参数在它与金仓两家都装得下，
        // 于是块数是 3 / 1 / 50 / <b>1</b> —— 1 出现了两次。"块数互不相同"从来就不是这个公式的性质，
        // 它只是三家恰好给出的表象；真正可数的是<b>预算与每语句行数那两位数</b>，所以从这一版起钉的是它们：
        // 同一份 <c>RowsPerStatement(budget, columns)</c> 函数、四个预算，得到 270 / 4212 / 12 / 8425。
        Assert.Single(myq.Chunks);
        Assert.Equal(65_535, myq.ParameterBudget);
        Assert.Equal(270, tsql.RowsPerStatement);
        Assert.Equal(4212, kes.RowsPerStatement);
        Assert.Equal(12, dam.RowsPerStatement);
        Assert.Equal(8425, myq.RowsPerStatement);
        Assert.Equal(600, myq.Chunks.Sum(static c => c.RowCount));
        Assert.True(myq.ParameterBudget > kes.ParameterBudget);
        Assert.True(myq.RowsPerStatement > kes.RowsPerStatement);

        // 第五家把这一格从"四家四个数"长成"五家<em>四</em>个数"：它与达梦同为"没有批量策略"，
        // 于是拿到同一份保守兜底（100 → 12 行 → 50 块），这是那三颗有出处的预算之外<em>第二</em>个走兜底分支的消费者。
        // v0.30 之前"只有这一族是 50 块"那句话从今天起是假话，而这一格记的不是数字，是<b>兜底分支的代价会长大</b>：
        // 两家没有真预算 ⇒ 同一批行在两处各多 49 次往返，而这两处谁都不会主动报告。
        // 重开条件与达梦那一条同一格（要先有活库才拿得到参数上限），§14 里现在记的是"两族"而不是"一族"。
        Assert.Equal(100, xgu.ParameterBudget);
        Assert.Equal(12, xgu.RowsPerStatement);
        Assert.Equal(50, xgu.Chunks.Count);
        Assert.Equal(600, xgu.Chunks.Sum(static c => c.RowCount));
        Assert.Equal(dam.RowsPerStatement, xgu.RowsPerStatement);
    }

    /// <summary>对拍账本用的那 8 条链，五家各跑一遍。<b>名字只服务于失败信息</b>：这一条红的时候要说得出
    /// 是<em>哪一格</em>变了，而不只是"5 变成了 6"——一进一出（比如 LEN 改成 LENGTH 的同时 DATEPART 也改成
    /// EXTRACT）在只钉计数的账本上是绿的。</summary>
    static readonly (string Name, Func<QuickSqlOrm, string> Run)[] LedgerChains =
    [
        ("plain", static orm => orm.Select<Order>().ToSql()),
        ("where-enum", static orm => orm.Select<Order>().Where(o => o.State == OrderState.Placed).ToSql()),
        ("paging", static orm => orm.Select<Order>().OrderBy(o => o.Id).Skip(1).ToSql()),
        ("join", static orm => orm.Select<Order>().InnerJoin<Customer>((o, c) => o.CustomerId == c.Id).Select((o, c) => new { o.Id, c.Name }).ToSql()),
        ("length", static orm => orm.Select<Order>().Select(o => new { o.Id, Length = o.CustomerName!.Length }).ToSql()),
        ("year", static orm => orm.Select<Order>().Select(o => new { Year = o.CreatedAt.Year }).ToSql()),
        ("delete", static orm => orm.Delete<Order>().Where(o => o.Id == 7).ToSql()),
        ("update", static orm => orm.Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql()),
    ];

    [Fact]
    public void The_four_families_disagree_only_where_a_pinning_test_says_so()
    {
        // 这一条是本文件的账本。原来它只记两家那一格（8 条链里 5 格不同形，那 5 格是：分页子句、
        // LEN/LENGTH、DATEPART/EXTRACT、DELETE 与 UPDATE 的目标别名写法），v0.23 把它摊成三对，
        // v0.30 摊成<b>六对</b>，v0.31 摊成<b>十对</b>（新那一族与另四家各一对）。往上走只有一个合法理由
        // （又接出一个真分岔），往下走则是有人把某两家的差异抹进了共用层 —— 两种都要看得见，所以这里点名到格。
        // 方法名保留 "four" 没有改：它是 v0.30 结的那一格的<em>账</em>，改名会把 §13.1 里引它的那一句牵连成
        // "某一版重命名过"，而这一版真正新增的账在下面那一段（<see cref="The_fifth_family_diverges_from_the_other_four_on_exactly_the_cells_named_below"/>）。
        Assert.Equal(new[] { "paging", "length", "year", "delete", "update" }, Diverging(sqlServer, kingbase));

        // 前两家那一格仍用引号折法量（它是 v0.22 结的账，换一个折法就不是同一个数了）。
        Assert.Equal(5, LedgerChains.Count(static chain => Flatten(chain.Run(sqlServer())) != Flatten(chain.Run(kingbase()))));

        // 金仓↔达梦只有 3 格，而原因值得记下来：这两家在<em>函数名</em>上是一致的
        // （LENGTH 与 EXTRACT 都是标准写法），分岔全在语句骨架上 —— 分页那一格是 ROWNUM 套壳 vs LIMIT，
        // 两格写侧是"有没有目标别名"。也就是说共用层为第三家改的那几处，没有一处需要为函数走第二条路。
        Assert.Equal(new[] { "paging", "delete", "update" }, Diverging(kingbase, dameng));

        // T-SQL↔达梦是满格的一列：前两家的分岔它一个不落，因为第三家与 PG 系相同的那些函数名，
        // 恰好都是 T-SQL 另写一种的那些。
        Assert.Equal(new[] { "paging", "length", "year", "delete", "update" }, Diverging(sqlServer, dameng));

        // ── 以下三对是第四家带来的 ───────────────────────────────────
        // 与 T-SQL 那一列逐字同形：第四家在<em>函数名</em>上另写的只有 LENGTH 一颗（它要 CHAR_LENGTH，
        // 因为 LENGTH 在这一家是字节数），DATEPART 那格它照 EXTRACT —— 于是 T-SQL 的三格分岔它全中。
        Assert.Equal(new[] { "paging", "length", "year", "delete", "update" }, Diverging(sqlServer, mysql));

        // 六对里最少的一格，而它红的是"两家共用尾巴"这件事的<em>内部</em>：写侧两家同边（ANSI 别名）、
        // 函数表里九颗有八颗同形，剩下的两格一颗是契约量纲（CHAR_LENGTH 对 LENGTH），
        // 另一颗是第四家自己造的"无上限"哨兵 —— 只有 OFFSET 时它必须补一颗 LIMIT，而 PG 系可以光写 OFFSET。
        // 换句话说：这一格证明"同一套分页语法"不等于"分页同形"，这是接第四家之前没有的形状。
        // （v0.31 起"六对里最少"这句要读成"前六对里最少"：新那一格里有一对是 2，见下面那一条用例。）
        Assert.Equal(new[] { "paging", "length" }, Diverging(kingbase, mysql));

        // 与达梦那一对读：多出来的那一格是 length，少掉的两格是 delete/update ——
        // 第四家在写侧完全走共用层，所以"有没有目标别名"那一格它不在。
        Assert.Equal(new[] { "paging", "length", "delete", "update" }, Diverging(dameng, mysql));

        // 六对的<em>形状</em>本身也钉一次（§13.1 那一格引的就是这六个数）：
        // 分页那一格六对全中，写侧那两格四对，length 五对，year 只有与 T-SQL 配对的三对 ——
        // 而 plain / where-enum / join 六对全不中，这三格才是共用层真正的地盘。
        var counts = new[]
        {
            Diverging(sqlServer, kingbase).Length,
            Diverging(sqlServer, dameng).Length,
            Diverging(sqlServer, mysql).Length,
            Diverging(kingbase, dameng).Length,
            Diverging(kingbase, mysql).Length,
            Diverging(dameng, mysql).Length,
        };

        Assert.Equal(new[] { 5, 5, 5, 3, 2, 4 }, counts);
        Assert.Equal(24, counts.Sum());
    }

    [Fact]
    public void The_fifth_family_diverges_from_the_other_four_on_exactly_the_cells_named_below()
    {
        // 十对账本里新添的四对。这一族带来的不是"又多四处对读"，而是<em>第一对分岔数低于三</em>的配对：
        // 达梦↔虚谷只分分页与年份两格（此前最少的是金仓↔MySQL 那两格，而那一对的两格各有独立用例点名）。
        // 之所以只两格：其余六格里有五格是<em>同一套字形策略</em>的产物
        // （大写折叠 / 双引号 / 无 AS / 冒号 / 写侧表名自限定），而那种"同形"必须在别处被点名成断言，
        // 不能只靠这一格绿着 —— 点名它的就是 <c>XuguDialectTests</c> 里那批 Assert.Equal(dameng…) 与上面
        // 那几条逐字的 Checks.Has。两份合起来才是"2"这个数的意思：骨架同形、函数与分页不同形。
        Assert.Equal(new[] { "paging", "year" }, Diverging(dameng, xugu));

        // 与金仓那四格：多出来的是"写侧有没有目标别名"两格（这一族与达梦同边），函数名那一格它反而与金仓同形
        // （两家都写 LENGTH）。于是这一格与上面那条合起来读出一件事：<b>length 那一格在五家里是三∶二，
        // 而 year 那一格是 3∶1∶1</b> —— DATEPART 一家、EXTRACT 三家、GETYEAR 一家。
        Assert.Equal(new[] { "paging", "year", "delete", "update" }, Diverging(kingbase, xugu));

        // 与 MySQL 血统那五格 = 它与 T-SQL 那一列逐字同形（paging/length/year/delete/update）：
        // 一颗 CHAR_LENGTH 把它与这一族拉开，而这一格拉开的是"契约量纲"那一档，不是字形。
        Assert.Equal(new[] { "paging", "length", "year", "delete", "update" }, Diverging(mysql, xugu));

        // T-SQL↔虚谷同样是满格的一列，理由与 T-SQL↔达梦那一条一样：这一族与 PG 系同形的那两颗函数名
        // （LENGTH）恰好都是 T-SQL 另写一种的那一颗。
        Assert.Equal(new[] { "paging", "length", "year", "delete", "update" }, Diverging(sqlServer, xugu));

        // 十对的形状（§13.1 引的就是这十个数）：{5,5,5,3,2,4} 是前六对，新四对是 {5,4,2,5}，合计 40。
        // 分页那一格从"六对全中"长成"十对全中"——它是唯一一处<em>每一对</em>都不同的格，
        // 而 plain / where-enum / join 三格十对全不中，这件事是共用层地盘最硬的一份见证。
        // （v0.37 第一笔要把这句读窄一格：新五对里 where-enum 那一格<em>每一对都中</em>，
        // 而它中的原因不在共用层——见 <see cref="The_sixth_family_diverges_from_the_other_five_on_exactly_the_cells_named_below"/>。）
        var counts = new[]
        {
            Diverging(sqlServer, kingbase).Length,
            Diverging(sqlServer, dameng).Length,
            Diverging(sqlServer, mysql).Length,
            Diverging(sqlServer, xugu).Length,
            Diverging(kingbase, dameng).Length,
            Diverging(kingbase, mysql).Length,
            Diverging(kingbase, xugu).Length,
            Diverging(dameng, mysql).Length,
            Diverging(dameng, xugu).Length,
            Diverging(mysql, xugu).Length,
        };

        Assert.Equal(new[] { 5, 5, 5, 5, 3, 2, 4, 4, 2, 5 }, counts);
        Assert.Equal(40, counts.Sum());

        // 每一格里"哪一家与哪一家同形"也钉一次，因为它才是这十个数的内容：year 那一格今天有三派
        // （DATEPART 一家、EXTRACT 三家、GETYEAR 一家），而新加入的那一族两边都不靠 —— 下面那三句
        // 逐个把这一格在两对配对里的表现钉住：与 T-SQL 那一对它中，与金仓那一对它也中，
        // 而与达梦那一对同样中（那一家是 EXTRACT）。一句都省不掉：三句合起来才是"第三派"而不是"又一派"。
        Assert.Contains("year", Diverging(sqlServer, xugu));
        Assert.Contains("year", Diverging(kingbase, xugu));
        Assert.Contains("year", Diverging(dameng, xugu));

        // 反手钉一次"同形的那一派没被拆散"：EXTRACT 那三家之间，year 这一格<em>不许</em>有人红。
        Assert.DoesNotContain("year", Diverging(kingbase, dameng));
        Assert.DoesNotContain("year", Diverging(kingbase, mysql));
        Assert.DoesNotContain("year", Diverging(dameng, mysql));
    }

    [Fact]
    public void The_sixth_family_diverges_from_the_other_five_on_exactly_the_cells_named_below()
    {
        // v0.37 第一笔：十五对里的新五对。<b>这一批把"共用层真正的地盘"从三格削成两格</b>：
        // 此前十五对里 plain / where-enum / join 三格全不中，而 where-enum 那一格从今天起<em>每一对都中</em> ——
        // 不是因为本家在枚举谓词上另写了什么，是因为它的占位符只有位置形（<c>= ?</c>），
        // 而 <see cref="Fold"/> 折的那四处字形里<em>没有一处管得到这颗</em>：它折 <c>:p</c> 对 <c>@p</c>，
        // 预设了"前缀之后总跟一个名字"。本家第一次让这颗预设不成立，于是这一格红得名副其实——
        // 它是第五种占位符形，不是第四处字形。（同一格在 <c>Gbase8sDialectTests</c> 里钉的是产出，
        // 这里钉的是"它在五对配对里的分布"，两半都要有。）
        Assert.Equal(new[] { "where-enum", "paging", "length", "year", "delete", "update" }, Diverging(sqlServer, gbase8s));
        Assert.Equal(new[] { "where-enum", "paging", "year", "delete", "update" }, Diverging(kingbase, gbase8s));
        Assert.Equal(new[] { "where-enum", "paging", "year", "delete", "update" }, Diverging(dameng, gbase8s));
        Assert.Equal(new[] { "where-enum", "paging", "length", "year", "delete", "update" }, Diverging(mysql, gbase8s));
        Assert.Equal(new[] { "where-enum", "paging", "year", "delete", "update" }, Diverging(xugu, gbase8s));

        // 与金仓那一对是这一批最要读的一格：两家的 <c>IdentifierPolicy</c> <b>按值相等</b>
        // （<c>Gbase8sDialectTests</c> 里那句 <c>Assert.Equal(IdentifierPolicy.PostgreSQL, IdentifierPolicy.Gbase8s)</c>），
        // 而这一格交出五处不同形——四格在标识符策略射程之外（占位符、分页、年份函数、写侧目标别名两格）。
        // "同一颗策略值 ≠ 同一份产出"这句话此前只有 <c>AS</c> 一处见证，今天有了账本上的一列数。

        // 五对的形状（{6,5,5,6,5}，合计 27）与前面那两本账合起来读：十五对总账 40 + 27 = 67。
        var counts = new[]
        {
            Diverging(sqlServer, gbase8s).Length,
            Diverging(kingbase, gbase8s).Length,
            Diverging(dameng, gbase8s).Length,
            Diverging(mysql, gbase8s).Length,
            Diverging(xugu, gbase8s).Length,
        };

        Assert.Equal(new[] { 6, 5, 5, 6, 5 }, counts);
        Assert.Equal(27, counts.Sum());

        // 反手钉一次"没中的那两格仍然没中"：共用层的地盘被削掉一格以后，剩下两格必须还站得住。
        var others = new Func<QuickSqlOrm>[] { sqlServer, kingbase, dameng, mysql, xugu };

        foreach (var pair in others)
        {
            Assert.DoesNotContain("plain", Diverging(pair, gbase8s));
            Assert.DoesNotContain("join", Diverging(pair, gbase8s));
        }

        // 年份那一格从今天起是<em>四派</em>：DATEPART 一家、EXTRACT 三家、GETYEAR 一家、YEAR 一家。
        // 上面那句"每一对都中"在 year 这一格同样成立，而它此前十五对里只在与 T-SQL 配对的三对上中过——
        // 本家把这一格从"配对里的少数"变成"配对里的全部"，种数与分布各涨一格。
        foreach (var pair in others)
            Assert.Contains("year", Diverging(pair, gbase8s));
    }

    /// <summary>两家在 8 条链上有几格不同形（折掉 <see cref="Fold"/> 那四处字形之后），返回<em>格名清单</em>。
    /// 顺序由 <see cref="LedgerChains"/> 决定，所以期望值要按那个顺序写。</summary>
    static string[] Diverging(Func<QuickSqlOrm> left, Func<QuickSqlOrm> right)
        => [.. LedgerChains
            .Where(chain => Fold(chain.Run(left())) != Fold(chain.Run(right())))
            .Select(static chain => chain.Name)];

    // ── 内部 ─────────────────────────────────────────────────────────

    static string Names(string withNames) => withNames[(withNames.IndexOf('|', StringComparison.Ordinal) + 1)..];

    static int Count(string withNames) => Names(withNames).Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
}
