using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Providers.Xugu;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>虚谷（第五档）的 SQL <b>文本</b>。全程 <see cref="SqlServer.NoDriverFactory"/>，
/// 与前四族的离线套件同一分工：黄金样本钉版式，结构断言钉"凭什么长这样"。
///
/// <para><b>这一批样本证明的是回归，不是"虚谷接受这条 SQL"</b>，而这一句在这一家<em>已经翻了一半</em>，
/// 原文照抄在这里："<em>授权的活库仍然只有两台 SQL Server（§13.4），本机没有一台虚谷实例，清单里
/// <c>XuguClient</c> 那一行也从未解析成功过（§13.5 台账里这一家逐位标着「离线已钉」，「真库跑过」那一档一颗都没有）</em>"。
/// 那三格在写下时都是真的：§13.17 添了第三台授权实例、而它正是一台虚谷，v0.37 起集成层又开了虚谷通路
/// （<c>tests/QuickSql.Tests.Integration/Database.cs</c> 的家族缝），于是"没有实例""没跑过""驱动行解析不成"
/// 今天都不成立。<b>翻了一半的意思是另一半还立着</b>：这个类钉的是<em>文本</em>，真库那一场证的是<em>能跑</em>，
/// 两者的覆盖面不等价，逐颗账记 §13.5 与 §14。不变的是这一家<em>有两档出处</em>：
/// 厂商文档进了仓库（docs.xugudb.com 那一组页面逐页读过），而且有两家第三方实现为这一家发出的 SQL 文本可当产物读 ——
/// 所以这里每一条"未实测"都还分得清是"文档这么写"还是"别人的代码这么发"，判据写在
/// <see cref="XuguExpressions"/> 类注释第二段，逐格标在 <c>src/</c> 那一份里。</para>
///
/// <para>样本按<b>形状</b>选而不是按链式方法数（§13.3），而这一族的目录是五家里<em>最小</em>的一份（6 份，
/// MySQL 那一档 11 份）。原因是这一家的新增差异几乎全落在<em>语义格</em>上而不是<em>字形</em>上：
/// 它的标识符形状（双引号 + 折大写）、表别名不带 <c>AS</c>、冒号参数前缀、写目标用自己的表名、三段式守卫
/// —— 这五处<em>逐字</em>就是达梦那一族的形状，于是"读侧整条链与第三家一字不差"本身就是这一版的一条主张
/// （<see cref="The_read_chain_is_the_third_family_s_text_not_a_fifth_shape"/>），而它只能由对读量出来、
/// 复制一份基线反而会把"两家必须一起改"这件事藏起来。留下来的 6 份各自钉一颗这一家独有的形状。</para>
///
/// <para>这个类只构造<em>一层</em> provider：<see cref="XuguProvider"/> 直接实现 <see cref="IDbProvider"/>，
/// 没有血统基类，所以也就没有 <c>MySqlDialectTests</c> 里那枚<em>空壳证据</em>用的基类探针 ——
/// §9.1 那份清单里没有第二种挂在虚谷底下的库种，"两层产出相同"这句话在这一家无从提出。
/// 类注释第三段末尾那一条记的是这一版真正被这一家改掉的账：<b>provider 层的类注释写着
/// "<c>WriteTargetAlias</c> 与 <c>EmitUpdateTarget</c> 成对覆写"，而 <c>src/</c> 只覆写了一半</b>，
/// 是下面那条写目标的用例把它对读出来的。</para>
///
/// <para><b>本族新增的那三颗用例钉的是"开关与文本成对"这一件事</b>（v0.38 第一笔）：驱动层不发保存点，
/// 所以这一家把这句话自己发。三颗各钉一层 —— 开关的读数（本家 false、其余取 true 的各家）、三句文本的
/// 形状（裸名、不加引号、不折大写、不占参数位）、以及<em>把开关掰了而不给文本出口时必须在发射层红</em>。
/// 形状那一颗的判据是一台真库的读数而不是产物抄写：<c>obj/logs/v37-xg-recon-savepoint-1.log</c>，
/// 逐问账记 §13.5 与 §14 本版那一段。</para></summary>
public sealed class XuguDialectTests
{
    static readonly IDbProvider Provider = Xugu.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>本家样本的目录名。理由与前四家同一条：五族按形状起的名字是同一批，
    /// 不带目录参数就会改写别家的基线。</summary>
    const string Leaf = "xugu";

    static void Verify(string name, string sql) => Golden.Verify(name, sql, Leaf);

    static QuickSqlOrm orm() => Xugu.Orm();

    /// <summary>第三家的那一份对照。<b>本文件有五条用例以它为判据而不是以本家基线为判据</b>
    /// （读链、UPDATE、DELETE、§10.7 注入、守卫文本），因为这一家在这五格上与它逐字同形 ——
    /// 而对"逐字同形"这件事，最便宜的见证不是再抄一份文件，是 <c>Assert.Equal</c>。</summary>
    static QuickSqlOrm dameng() => Dameng.Orm();

    /// <summary>带模式的模型：只有 <see cref="A_named_schema_qualifies_the_table_and_folds_it_too"/> 需要它。
    /// 与 <c>ChangeGuardTests</c>、<c>MySqlDialectTests</c> 同一手法（Fluent 覆写而不是新建实体）：G1 只认
    /// <c>[Table]</c>，新加一个实体会把五家的对拍账牵连进来，而这一格要的只是"契约上写了一个模式名"这一个事实。</summary>
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

    // ── 标识符：第五份字形，而它不是新的 ──────────────────────────────

    [Fact]
    public void The_read_chain_is_the_third_family_s_text_not_a_fifth_shape()
    {
        var sql = orm().Select<Order>().ToSql();

        // 刻意<em>不</em>生成基线：这一句与 golden/dameng/select-entity.sql 逐字相同（双引号 + 折大写 + 别名不带 AS），
        // 复制一份只会多一处要同步的地方，而 Assert.Equal 量到的是那一整条主张本身。
        Assert.Equal(dameng().Select<Order>().ToSql(), sql);

        Checks.Has("FROM \"QUICKSQLORDER\" \"T0\"", sql);
        Checks.Has("SELECT \"T0\".\"ID\", \"T0\".\"CUSTOMERNAME\"", sql);
        Checks.Absent("[", sql);
        Checks.Absent("`", sql);
        Checks.Absent("\"QuickSqlOrder\"", sql);
        Checks.Absent(" AS \"T0\"", sql);

        // 这一格值得单独写一句：五家的引号字形仍然只有<em>三种</em>（方括号 / 双引号 / 反引号），
        // 新家族没有带来第四种 —— "又接一家"不等于"差异面又宽一格"，这一版有两格缩了（见 §13.2 的回写）。
    }

    // ── 分页：尾巴与另两家同形，哨兵是本家独有的一颗数 ────────────────

    [Fact]
    public void Limit_and_offset_are_two_tail_clauses_and_the_row_count_comes_first()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(10).Take(5).ToSql(out var parameters);

        // 不生成基线：这一句折掉引号、大小写与前缀之后与金仓、MySQL 血统两家逐字同形（同一套尾巴），
        // 交给 DialectParityTests 对读。下面量的是本家在这一格上的两件事实。
        Assert.Equal([5, 10], parameters.Select(p => (int) p.Value!).ToArray());
        Checks.Has("ORDER BY \"T0\".\"ID\" ASC LIMIT :p0 OFFSET :p1", sql);

        // 套壳那一形是第三家的，OFFSET…FETCH 是 T-SQL 的，都不许漏进来。
        Checks.Absent("FETCH", sql);
        Checks.Absent("ROWNUM", sql);
    }

    [Fact]
    public void Offset_cannot_stand_alone_so_the_missing_limit_becomes_the_documented_ceiling()
    {
        var sql = orm().Select<Order>().OrderBy(o => o.Id).Skip(30).ToSql(out var parameters);

        Verify("paging-skip-only", sql);

        // 与金仓恰好相反（那一家 OFFSET 能单独出现），与 MySQL 血统<em>同形</em>：语法要求 LIMIT 必须在场。
        // 补的那一颗数两家不同一条 —— 见下面那句 Absent：这一家的 2147483647 是厂商写死的取值上界，
        // 那一家的 18446744073709551615 是无符号整型的顶。两颗"同形的哨兵"出处不是一类，所以对拍折不出。
        Assert.Single(parameters);
        Assert.Equal(30, parameters[0].Value);
        Checks.Has("LIMIT 2147483647 OFFSET :p0", sql);
        Checks.Absent("18446744073709551615", sql);

        // 哨兵写成文本而不是参数：与 MySQL 血统同一档纪律，它不是一格数据而是"没有上限"的另一种写法，
        // 多一颗参数会顶红下面这一句，而真正的代价在参数预算（§12.3 的分块公式量的是真值颗数）。
        Checks.Absent(":p1", sql);

        // 这一格是交账前<em>改判</em>的那一格：上一版这里抛（"没有一份产物发过光写 OFFSET 的语句，
        // 所以没有可抄的哨兵"），改判的依据是厂商文档直接给了 LIMIT 的取值范围 ——
        // "语法要求子句在场"与"这个数能吃多大"是两问，前一问产物已经答过。那一格记 §13.5。
    }

    // ── 谓词形状：两处都不覆写，而这一家覆写的理由与另两家各不同 ──────

    [Fact]
    public void The_shared_predicate_text_is_also_the_third_family_s_although_this_one_has_a_real_boolean_type()
    {
        var none = Array.Empty<int>();

        var empty = orm().Select<Order>().Where(o => none.Contains(o.Id)).ToSql();

        // 五家在这一格是 4∶1（只有金仓写 false），而这一家<em>有</em> BOOLEAN 型、也有 TRUE/FALSE 字面量
        // —— 它不写 false 的理由与 MySQL 血统不同条：那一家是"字面量只是 1/0 的同义词"，
        // 这一家是"我们的布尔列落成 TINYINT，谓词比的是整数"（三处一致，见 XuguValues 类注释第二条）。
        Assert.Equal(dameng().Select<Order>().Where(o => none.Contains(o.Id)).ToSql(), empty);
        Checks.Has("WHERE 1 = 0", empty);
        Checks.Absent("WHERE false", empty);

        var paid = true;

        var flag = orm().Select<Order>().Where(o => paid && o.Amount > 1m).ToSql(out var parameters);

        Assert.Equal(2, parameters.Count);
        Checks.Has(":p0 = 1 AND \"T0\".\"AMOUNT\" > :p1", flag);
        Checks.Absent("IS TRUE", flag);

        // 反面：声明 Boolean_Type 会把 EmitBooleanEquality 推到 IS TRUE 那一支，而这一家有没有
        // IS TRUE 两档出处都没写过 —— 那一格在 XuguTypes 里是"库确实有那一型而这里仍不用它"的三条理由之一。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Boolean_Type));
    }

    // ── 写目标：与第三家同边，而这一格是本次对读抓出来的 ──────────────

    [Fact]
    public void The_write_target_qualifies_itself_with_the_table_name_because_it_takes_no_alias()
    {
        var update = orm().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql();

        // 这一句是这一版<em>唯一一处 provider 层被测试改掉的代码</em>：XuguDialect 的类注释写着
        // "WriteTargetAlias 与 EmitUpdateTarget 成对覆写"，而 src/ 里只有前半。少了后半，基类的默认出口
        // 会发 UPDATE "T" AS "T" —— 表给自己起一个与自己同名的别名，两档出处里没有一形长这样。
        // 于是"这一家不许别名"这件事被写成了"这一家把别名写成表名"，风险方向恰好是类注释第三条说反的那一边。
        Assert.Equal(dameng().Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == 7).ToSql(), update);
        Checks.Has("UPDATE \"QUICKSQLORDER\" SET \"AMOUNT\" = \"QUICKSQLORDER\".\"AMOUNT\" + :p0 WHERE \"QUICKSQLORDER\".\"ID\" = :p1", update);
        Checks.Absent("AS \"QUICKSQLORDER\"", update);
        Checks.Absent("\"QUICKSQLORDER\" \"QUICKSQLORDER\"", update);
    }

    [Fact]
    public void A_delete_shares_the_one_slot_that_the_update_uses()
    {
        var delete = orm().Delete<Order>().Where(o => o.Id == 7).ToSql();

        // EmitDeleteTarget 默认转给 EmitUpdateTarget，所以这一条与上面那条一起红才算那一口动了 ——
        // 与前四族同一条纪律，而这一家在"两家共用一口"之外还多一层：本家与第三家在这一格逐字同形。
        Assert.Equal(dameng().Delete<Order>().Where(o => o.Id == 7).ToSql(), delete);
        Checks.Has("DELETE FROM \"QUICKSQLORDER\" WHERE \"QUICKSQLORDER\".\"ID\" = :p0", delete);
        Checks.Absent("USING", delete);
    }

    [Fact]
    public void A_global_filter_on_the_write_target_qualified_with_the_table_name_here_too()
    {
        var fresh = orm();

        fresh.Filters.Add<Ticket>(t => !t.Deleted);

        var damengFresh = dameng();

        damengFresh.Filters.Add<Ticket>(t => !t.Deleted);

        var sql = fresh.Delete<Ticket>().Where(t => t.Id == 7).ToSql();

        // §10.7 的注入必须接上"这一家没有目标别名"那一格：内核不能另造一只 t0，否则前缀变成 "T0". 而
        // 库里报"无法绑定多部分标识符"。这一家与达梦走的是同一只 TableRef，所以这一条的红的形状也与那一条相同。
        Assert.Equal(damengFresh.Delete<Ticket>().Where(t => t.Id == 7).ToSql(), sql);
        Checks.Has("DELETE FROM \"QUICKSQLTICKET\" WHERE \"QUICKSQLTICKET\".\"ID\" = :p0 AND NOT \"QUICKSQLTICKET\".\"DELETED\" = 1", sql);
        Checks.Absent("\"T0\".", sql);
    }

    // ── 主键回填：v0.34 起这一家走"逐条 + 语句后回读"，RETURNING 那一形退出回填档 ──
    //
    // 这一节从前是两条用例（v0.31 立）：单行钉"句尾带 RETURNING 的那一形"、多行钉"没有任何产物用过的
    // 批量形"，两句都自认"等真库那天拿它去对，而不是拿它自证"。那天到了（v0.32 侦察电池），
    // 对回来的答案是 N-06/N-07：<b>RETURNING 两形都被库收下、行进去了、回包 FIELDS=0 ROWS=0</b> ——
    // 两条用例钉的形状就此换向，证据逐格写在 XuguKeys.SupportsBulkKeyRetrieval 的改判注释里。

    [Fact]
    public void Asking_for_the_generated_key_appends_a_post_statement_read_in_the_same_command()
    {
        var plan = orm().Insert(row(1)).ExplainKeys();
        var sql = plan.ToSql();

        // 键不再由这条 INSERT 的结果集带（回包是空的），由同一句命令尾巴上的回读句带 ——
        // "同一条命令"是这一形的全部理由：LAST_INSERT_ID() 是会话级函数，拆开两次执行就出了会话保证。
        // 多语句批在这一家收得下且答得对（v32-recon-txn-3 的 T-03 收 + T-04 对上 MAX(ID)、N-09 交 Int64）。
        Checks.Has(" ; SELECT LAST_INSERT_ID()", sql);
        Checks.Has("VALUES (:p0, :p1, :p2, :p3, :p4, :p5, :p6)", sql);
        Assert.EndsWith("SELECT LAST_INSERT_ID()", sql, StringComparison.Ordinal);

        // 当初钉"发得出 RETURNING"，现在反向钉它<em>不出现</em>在回填档（带并发列的 Generated 档仍会撞向
        // EmitReturning 的旧路径，那一格今天红在空结果集上，记在并发那颗契约格里，不在这里粉饰）。
        Checks.Absent("RETURNING", sql);
        Checks.Absent("OUTPUT", sql);
        Checks.Absent("BULK COLLECT", sql);

        // 决策记录那一句要现场读得懂"为什么是一块一行还多带一条 SELECT"。
        Checks.Has("退到 L5 逐条", plan.Reason);
        Checks.Has("语句后回读", plan.Reason);
    }

    [Fact]
    public void A_multi_row_insert_with_keys_degrades_to_one_row_per_statement_each_reading_back_its_own_key()
    {
        var plan = orm().InsertRange([row(1), row(2)]).ExplainKeys();

        // 批量位 false ⇒ L3 多值形在回填档不再产生（旧用例"我们发出去的批量形长这样，等真库那天对"
        // 已随那次对账作废）。逐条 + 每块自带回读：块数 = 行数，回读句不会串到别人的行上 ——
        // 这一形同时绕开了 MySQL 血统那格的锁模式坑（连续性推断根本不需要发生），代价明写在 Reason 里。
        Assert.Equal(2, plan.Chunks.Count);

        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            var chunk = plan.Chunks[i];

            Assert.Equal(1, chunk.RowCount);
            Assert.Equal(i, chunk.FirstRow);

            Assert.Equal(7, Checks.Occurrences(chunk.Sql.Text, ":p"));
            Checks.Has(" ; SELECT LAST_INSERT_ID()", chunk.Sql.Text);
            Checks.Absent("RETURNING", chunk.Sql.Text);
        }
    }

    // ── §12.5 的合并档：第三道闸门，而它的理由与另两家不是一条 ────────

    [Fact]
    public void The_dialect_says_up_front_that_a_write_statement_cannot_carry_another_table()
    {
        // 这一格从 2∶2 变成 <b>3∶2</b>（true：T-SQL、金仓；false：达梦、MySQL 血统、这一家）。
        // 三家的理由三条各不同（类注释写在 XuguDialect 第四条）：目标不能起别名 / 没有那个子句 / 两样都没被证。
        // "整批退回一句一实体"从今天起是多数派而不是孤例，而它仍由 §12.4 第 2 项那句语义等价撑着。
        Assert.False(Provider.Dialect.SupportsJoinedWrites);

        Assert.True(SqlServer.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.True(Kingbase.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.False(Dameng.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
        Assert.False(MySql.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsJoinedWrites);
    }

    // ── 嵌套事务的保存点：本家的驱动不发这三句，改由本层发文本（§4 的 SupportsDriverSavepoints）──
    //
    // 这一节的三条用例各钉一层，缺一层就少一处能红的地方：分布（谁声明了什么）、文本（发出去的是哪一句）、
    // 反面（把开关翻掉而不给文本，红点落在哪一层）。

    [Fact]
    public void The_dialect_says_up_front_that_its_driver_does_not_issue_savepoints()
    {
        // 分布是 <b>1 家 false ∶ 4 家 true（各有活库见证）∶ 另几族 true（吃共用层默认，没有各自的复点）</b>。
        // false 那一家三颗读数都在 obj/logs/v37-xg-recon-savepoint-1.log 里：Q-02x 那三行（带名字的
        // Save/Rollback/Release 全停在 DbTransaction 基类，只有无参 Rollback() 被 XGTransaction 覆写）、
        // Q-03 与 Q-04（各调即 NotSupportedException）、Q-08 到 Q-15（同一台库收下文本三句，
        // 并且给出的就是保存点语义）。
        Assert.False(Provider.Dialect.SupportsDriverSavepoints);

        // true 那四家的见证是<em>同一批五场</em>（§13.4，本轮 v37-*-int-5/4 那五份）里同一条用例
        // WritePathTests.A_nested_transaction_uses_savepoint_and_only_rolls_back_inner_changes：
        // SQL Server ∶ PostgreSQL ∶ MySQL ∶ SQLite 四场都没把它列进 FAIL/SKIP（那就是跑过并且绿），
        // 只有虚谷那一场列了它。也就是说这四颗 true 不是"驱动文档这么写"，是"这条语句在真库上跑出来了"。
        Assert.True(SqlServer.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsDriverSavepoints);
        Assert.True(PostgreSql.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsDriverSavepoints);
        Assert.True(MySql.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsDriverSavepoints);
        Assert.True(Sqlite.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsDriverSavepoints);

        // 离线那几族吃的是 DialectBase 的默认值，而这一格<em>没有</em>它们各自的活库复点：这里断言的是
        // "本仓库今天声明了什么"，不是"那几家的驱动确实覆写了三颗方法"。达梦、金仓、GBase 8s 三颗是随手点的样本，
        // 剩下的族同一格同一句。缺口（一家新库接进来时这一格靠默认而不是靠读数）记 §14。
        Assert.True(Kingbase.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsDriverSavepoints);
        Assert.True(Dameng.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsDriverSavepoints);
        Assert.True(Gbase8s.Provider(SqlServer.NoDriverFactory.Instance).Dialect.SupportsDriverSavepoints);
    }

    [Fact]
    public void The_three_textual_savepoints_are_the_shape_the_instance_answered()
    {
        // 逐字对探针：Q-08 发的是 SAVEPOINT probe_sp，Q-11 是 ROLLBACK TO SAVEPOINT probe_sp，
        // Q-14 是 RELEASE SAVEPOINT probe_sp。这里把那颗名字换成本仓库真正的生成名形状（quicksql_sp_<n>，
        // 由 AdoExecutor 按外层事务上的计数器发号），文本其余一字不动。
        var create = Statement(w => Provider.Dialect.EmitSavepoint(w, "quicksql_sp_0"));
        var rollback = Statement(w => Provider.Dialect.EmitRollbackToSavepoint(w, "quicksql_sp_0"));
        var release = Statement(w => Provider.Dialect.EmitReleaseSavepoint(w, "quicksql_sp_1"));

        Assert.Equal("SAVEPOINT quicksql_sp_0", create);
        Assert.Equal("ROLLBACK TO SAVEPOINT quicksql_sp_0", rollback);
        Assert.Equal("RELEASE SAVEPOINT quicksql_sp_1", release);

        // 名字是裸的，没走 EmitIdentifier：本家的策略是"区分大小写 ⇒ 每个标识符都加引号 ⇒ 引号内折成大写"，
        // 一走它就得到 "QUICKSQL_SP_0" —— 那一形没有活库见证，而"没有实测就不改默认"是本仓库的判据。
        // 三口读的是同一个生成名，所以库把那一名折向大写这件事在三口之间是一致的，不构成风险（XuguDialect 里
        // EmitSavepoint 那一段把这条一致性与它的失效条件都写了）。
        Checks.Absent("\"", create);
        Checks.Absent("QUICKSQL_SP", create);

        // T-SQL 那一形在这一家是语法错（Q-09：unexpected TRANSACTION, expecting end of file），
        // 所以它不仅要"没被用"，还要被点名没被用。
        Checks.Absent("SAVE TRANSACTION", create);
        Checks.Absent("SAVE TRANSACTION", rollback);

        // 三口都不带参数：保存点名不是数据，一旦有人把它参数化，得到的是"SAVEPOINT :p0"这种没有一家认的文本。
        var writer = NewWriter();

        Provider.Dialect.EmitSavepoint(writer, "quicksql_sp_2");

        Assert.Empty(writer.Parameters);
    }

    [Fact]
    public void Flipping_the_gate_without_a_text_outlet_raises_in_the_emitting_layer()
    {
        // 这条钉的是接口注释里那句"不许为了让某一场变绿把 SupportsDriverSavepoints 改成 false"到底拦住谁：
        // 它<em>拦不住</em>改动本身，它把红点从驱动层挪到发射层，并且话里点名要覆写哪一口。
        // 三口都量（只量创建那口会漏掉"覆写了一半"那种改法 —— 而本仓库在这一格上正是被"成对覆写"坑过一次，
        // 见类注释第三段那条 WriteTargetAlias 与 EmitUpdateTarget 的账）。
        var sqlServer = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

        var create = Checks.Raised<QuickSqlException>(() => Statement(w => sqlServer.Dialect.EmitSavepoint(w, "quicksql_sp_0")));
        var rollback = Checks.Raised<QuickSqlException>(() => Statement(w => sqlServer.Dialect.EmitRollbackToSavepoint(w, "quicksql_sp_0")));
        var release = Checks.Raised<QuickSqlException>(() => Statement(w => sqlServer.Dialect.EmitReleaseSavepoint(w, "quicksql_sp_0")));

        Checks.Has("SupportsDriverSavepoints", create.Message);
        Checks.Has("EmitSavepoint", create.Message);
        Checks.Has("EmitRollbackToSavepoint", rollback.Message);
        Checks.Has("EmitReleaseSavepoint", release.Message);

        // 抛的是本仓库自己的 QuickSqlException 而不是 NotSupported：与 EmitReturning/EmitJoinedSources 同一档，
        // 那句话里带 Kind，读得出是哪一家没接上。
        Checks.Has("SqlServer", create.Message);
    }

    [Fact]
    public void A_hand_built_merged_update_names_the_shape_it_does_have_instead_of_dropping_the_join()
    {
        // 日常路径到不了这里（Mergeable 第一道就退桶），手工构造 AST 才量得到发射层那一句。
        var failure = Checks.Raised<QuickSqlException>(() => MergedUpdate(nameof(Order.Amount)));

        Checks.Has("不接受目标表之外的表源", failure.Message);
        Checks.Has("SupportsJoinedWrites", failure.Message);

        // 与 MySQL 血统那一条<em>正好反向</em>：那一家点名的是"没有可指认的替代形状"，所以它的文案里
        // 不许出现 MERGE INTO；这一家有 MERGE INTO … USING (…) ON (…)（产物里出现过），
        // 缺的是共用层那种带列名清单的行集在那个子句上成不成立。所以这一家<em>必须</em>提到它 —— 将来接的是这一形。
        Checks.Has("MERGE INTO", failure.Message);
        Checks.Absent("UPDATE … FROM", failure.Message);
    }

    [Fact]
    public void A_delete_with_joined_sources_refuses_on_the_same_gate_as_the_update()
    {
        var order = orm().Schemas.Contract<Order>();
        var item = orm().Schemas.Contract<OrderItem>();

        var stored = new TableRef(0, Provider.Dialect.WriteTargetAlias(order.Table), order.Table, order);
        var details = new TableRef(1, "t1", item.Table);

        var id = order.Columns.Single(c => c.MemberName == nameof(Order.Id));
        var parentOrderId = item.Columns.Single(c => c.MemberName == nameof(OrderItem.OrderId));

        // 连接条件写全：这一条要证的是"带着行集就抛"，不能让"缺 ON"成为它红的理由。
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

        // 发射留在 lambda 里面：写在外面就是"断言之前先抛"，那条红会是异常而不是这条用例。
        var failure = Checks.Raised<QuickSqlException>(() => EmitDelete(ast).Text);

        Checks.Has("不接受目标表之外的表源", failure.Message);
        Assert.Equal(1, Checks.Occurrences(failure.Message, "1 个"));
    }

    // ── §12.4 第 2 项的变更守卫：第三段形状的第二家，理由不同条 ───────

    [Fact]
    public void A_not_null_column_still_needs_only_the_inequality()
    {
        var sql = GuardText(nameof(Order.Amount));

        // NOT NULL 列上一句 <>：五家在这一格已经是四家同形（金仓那一句式也在这儿的另一档上），
        // 而本家与达梦逐字同形 —— 守卫文本的形状里读不出"为什么是三段"，那一半在下面两条的注释里。
        Assert.Equal(GuardText(nameof(Order.Amount), Dameng.Provider(SqlServer.NoDriverFactory.Instance)), sql);
        Checks.Has("\"QUICKSQLORDER\".\"AMOUNT\" <> \"T1\".\"AMOUNT\"", sql);
    }

    [Fact]
    public void A_nullable_column_takes_the_three_way_shape_because_no_one_sentence_form_is_documented()
    {
        var sql = GuardText(nameof(Order.PaidAt));

        // 不生成基线：这一句与 golden/dameng/update-guard-three-way.sql 逐字相同。三段式在四家里同形，
        // 而四家的<em>理由</em>是三条：T-SQL 与达梦是"布尔表达式不是一等值"，MySQL 血统是"能一句的候选都要按版本发"，
        // 这一家是"两档出处里都没有 IS DISTINCT FROM、也没有 <=>，所以三段不是选择而是唯一发得出的形状"。
        Assert.Equal(GuardText(nameof(Order.PaidAt), Dameng.Provider(SqlServer.NoDriverFactory.Instance)), sql);
        Checks.Has("\"QUICKSQLORDER\".\"PAIDAT\" <> \"T1\".\"PAIDAT\" OR (\"QUICKSQLORDER\".\"PAIDAT\" IS NULL AND \"T1\".\"PAIDAT\" IS NOT NULL) OR (\"QUICKSQLORDER\".\"PAIDAT\" IS NOT NULL AND \"T1\".\"PAIDAT\" IS NULL)", sql);
        Checks.Absent("IS DISTINCT FROM", sql);
        Checks.Absent("<=>", sql);
        Assert.Equal(2, Checks.Occurrences(sql, " OR "));
    }

    [Fact]
    public void The_blacklist_is_two_cells_here_and_for_once_it_is_reachable_by_the_default_mapping()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));

        // 无宽度字符串在这一家落 CLOB（2GB）：能存，但不可比——这一格因此成了"默认映射直接踩进黑名单"
        // 的唯一一家（另四族要手工构造一个 CLOB 才量得到）。给了宽度的 VARCHAR 才可比。
        // 出处：厂商文档明写 TEXT 是 VARCHAR 的映射类型（不写长度只吃行剩余空间 64K）；活库实测
        // 60,000 字符即拒、CLOB 能存 1M 字符但 ORDER BY／建索引报 E10008（无比较函数）。
        Assert.Equal("CLOB", Provider.Types.GetStoreType(note));
        Assert.False(Provider.ChangeGuard.Compares(note));
        Assert.True(Provider.ChangeGuard.Compares(note with { MaxChars = 64, StoreTypeName = null }));
        Assert.True(Provider.ChangeGuard.Compares(contract.Columns.Single(c => c.MemberName == nameof(Order.Amount))));

        // 剩下两颗<em>不是护栏而是限制</em>：byte[] → BLOB 是本家默认路径（XuguTypes 第二条）；
        // 显式点名的 CLOB 与手写的 "CLOB 128" 同判。
        Assert.False(Provider.ChangeGuard.Compares(note with { ClrType = typeof(byte[]), StoreTypeName = null }));
        Assert.False(Provider.ChangeGuard.Compares(note with { StoreTypeName = "CLOB" }));
        Assert.True(Provider.ChangeGuard.Compares(note with { StoreTypeName = "VARCHAR(64)" }));

        // 带限定词要落到基名上：TIME(3) 与 NUMERIC(18,4) 都带括号，而 [Column(TypeName = "CLOB …")] 那种
        // 手写的带法带的是空格 —— 这一家是五家里唯一一处"同一档型既有带括号形状也有不带形状"的，切分不能只认括号。
        Assert.False(Provider.ChangeGuard.Compares(note with { StoreTypeName = "CLOB 128" }));

        // 生产后果今天仍然是零：Mergeable 先问 SupportsJoinedWrites（EntityTracker.cs:1030），逐列问 Compares
        // 排在它后面（:1039）。所以"默认映射踩进黑名单"在这家只影响守卫的读数，不影响写路径的形状——
        // 变更检测是客户端快照逐列 Equals，不经过守卫。
        Assert.False(Provider.Dialect.SupportsJoinedWrites);
    }

    [Fact]
    public void The_guard_text_carries_no_collate_and_no_trailing_space_workaround()
    {
        var sql = GuardText(nameof(Order.CustomerName));

        // 厂商文档明写这一家的 CHAR/VARCHAR <em>存入时去掉尾部空格</em>【文档】，于是"库里 abc、新值 abc␣"
        // 会被判成"变了"而多发一句 UPDATE，写回去的规范化值与库里那份逐字相同 —— <b>多写而不是丢写</b>，
        // 方向与 MySQL 那一家（CI 排序规则把真变化判成没变）正好相反。两行都在 §14 的跨库差异表里，优先级不同。
        // 这里钉的是"我们没有偷偷在守卫文本里补任何东西"：去尾这件事既不该由 COLLATE 表达，也不该由 TRIM 表达。
        Checks.Absent("COLLATE", sql);
        Checks.Absent("TRIM(", sql);
        Checks.Absent("RTRIM(", sql);

        Assert.True(Provider.ChangeGuard.Compares(orm().Schemas.Contract<Order>()
            .Columns.Single(c => c.MemberName == nameof(Order.CustomerName))));
    }

    // ── 函数模板：五家里唯一一份"每格各归一家"的表 ────────────────────

    [Fact]
    public void The_same_chain_renders_this_family_s_function_templates()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.CustomerName!.Length > 3 && o.CreatedAt.Year == 2026 && Math.Round(o.Amount, 2) > 1m)
            .ToSql();

        Verify("functions", sql);

        // 长度这一颗是【产物】直接给的裸 LENGTH，而它计字符还是字节<em>两档都没有出处</em> ——
        // 那一格是 §14 跨库差异表的第三家（T-SQL 的 LEN 不计尾部空格、MySQL 血统的 LENGTH 计字节而契约写的是字符数）。
        // 与达梦同形，所以四家在这一格是 4∶1（只有 MySQL 血统要 CHAR_LENGTH）。
        Checks.Has("LENGTH(\"T0\".\"CUSTOMERNAME\") > :p0", sql);

        // 日期部分是本家独有的一形：GETYEAR 一颗一个函数，不是 EXTRACT(unit FROM x) 三家那一支，
        // 也不是 T-SQL 的 DATEPART。上一版设计稿这里取的是 EXTRACT（理由写的就是"这一家像 Oracle"），
        // 交账前按本文件自己的出处判据改判 —— 产物发的就是这六颗，而"像哪一家"不是出处。
        Checks.Has("GETYEAR(\"T0\".\"CREATEDAT\") = :p1", sql);
        Checks.Absent("EXTRACT", sql);
        Checks.Absent("DATEPART", sql);

        // 没有金仓那三层 CAST(… AS numeric)：那一格是 9.x 内核的版本原因，抄过来会给每条 ROUND 多三颗 cast。
        Checks.Has("ROUND(\"T0\".\"AMOUNT\", :p2)", sql);
    }

    [Fact]
    public void String_reads_share_substr_with_the_third_family_and_open_a_fifth_form_of_indexof()
    {
        var sql = orm().Select<Order>()
            .Select(o => new
            {
                Cut = o.CustomerName!.Substring(1, 2),
                Tail = o.CustomerName!.Substring(1),
                At = o.CustomerName!.IndexOf("x"),
                Joined = o.CustomerName + " " + o.Note,
                Blank = string.IsNullOrWhiteSpace(o.Note),
            })
            .ToSql();

        Verify("string-functions", sql);

        // SUBSTR(串, 起, 长) 与第三家逐字同形（0 基→1 基的 +1 五家同一格换算）；
        // 它<em>不</em>跟着金仓/MySQL 血统那两家的 SUBSTRING … FROM … FOR … 标准形。
        Checks.Has("SUBSTR(\"T0\".\"CUSTOMERNAME\", :p0 + 1, :p1)", sql);
        Checks.Has("SUBSTR(\"T0\".\"CUSTOMERNAME\", :p2 + 1)", sql);

        // INSTR 在这里是<em>四参</em>【产物】：子串与串的顺序跟标准那一支（与 CHARINDEX/LOCATE 相反），
        // 后两颗"起始位置 / 第几次出现"是产物自己不赌默认值的写法。这一格在五家凑成第四形。
        Checks.Has("(INSTR(\"T0\".\"CUSTOMERNAME\", :p3, 1, 1) - 1)", sql);
        Checks.Has("CONCAT(CONCAT(\"T0\".\"CUSTOMERNAME\", :p4), \"T0\".\"NOTE\")", sql);

        // Blank 这一颗是【推断·偏离产物】的第一格：产物发的是不带 TRIM 的 `( x<>'' AND x IS NOT NULL )`，
        // 因为它对的是 IsNullOrEmpty，而共用层这一格对的是 IsNullOrWhiteSpace —— 跟它就是改语义而不是跟出处。
        // 它同时带着一颗条件：文档记着这一家有 EMPTY_STR_AS_NULL（默认 FALSE），开了它这一形会静默漏掉纯空格串。
        Checks.Has("(\"T0\".\"NOTE\" IS NULL OR TRIM(\"T0\".\"NOTE\") = '')", sql);
        Checks.Absent("IS NULL AND", sql);
    }

    [Fact]
    public void Date_arithmetic_was_the_mysql_form_until_the_live_library_said_it_is_not_a_form_at_all()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Tomorrow = o.CreatedAt.AddDays(1), Ceiling = Math.Max(o.Amount, 1m) })
            .ToSql();

        // 整张表最反直觉的一格<em>换了方向</em>，而换它的不是文档、是那一台活库。
        // v0.31 写这一格时读到的形状是"这一家有 DUAL、SYSDATE、NVL、四参 INSTR（处处像 Oracle 那一族），
        // 日期加法却走 MySQL 那一形"，那句的出处是两家已发布适配器逐字发过的 <c>DATE_ADD(x, INTERVAL (n) unit)</c>。
        // v0.32 第二格把那一形原样发进 TESTDB，六颗单位逐颗被驳（<c>E19260 … unexpected _YEAR,
        // expecting ',' or ')'</c> 那一族报错，六条在 <c>obj/logs/v32-recon-expr-3.log</c> 的 E-07 段），
        // 于是这一格<em>不是"分岔到 MySQL 血统"，而是"这一家根本没有那一形"</em>。
        // 换过去的两支都在同一跑里问绿过：间隔加法走 Oracle 血统的 <c>x + NUMTODSINTERVAL(n, 'DAY')</c>，
        // 按月那一颗走 <c>ADD_MONTHS</c>。读法照旧：这一条断的是<em>产出文本</em>，"库收下了"记 §13.5。
        Checks.Has("(\"T0\".\"CREATEDAT\" + NUMTODSINTERVAL(:p0, 'DAY'))", sql);
        Checks.Absent("DATE_ADD", sql);
        Checks.Absent("INTERVAL '1 day'", sql);

        // 括号包着乘数与另两家同一条理由：{1} 若是表达式，没有它就把减法整个算进间隔里。
        // GREATEST 取【推断】的多数派（三家原生，只有 T-SQL 要 CASE 展开）。
        Checks.Has("GREATEST(\"T0\".\"AMOUNT\", :p1)", sql);
    }

    [Fact]
    public void The_second_cell_is_SECOND_not_GETSECOND_because_the_artifact_one_returns_the_fraction()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { Sec = o.CreatedAt.Second, Y = o.CreatedAt.Year })
            .ToSql();

        // 本族唯一一处"产物在场而产物那一颗是错的"：GETSECOND 在这台库上对
        // <c>…05:09:14.456</c> 交回 <c>9.456</c> 一颗 <c>Double</c>（同一跑里 E-05a 与 E-13b 两问同答，
        // <c>obj/logs/v32-recon-expr-4.log</c>），而共用层那一格要的是"秒序号"。
        // 后果不是<em>错行</em>而是<em>错等</em>：它不挑行序，只在 <c>== :p</c> 这种比较上把带小数秒的行整批漏掉。
        // 同一条 <c>SELECT</c> 里另五颗 <c>GET*</c> 各自交回整数，所以这一格是六颗里被摘出来的一颗，
        // 不是"GET* 那一族不能用"—— 下一行就把这句钉住。
        Checks.Has("SECOND(\"T0\".\"CREATEDAT\")", sql);
        Checks.Absent("GETSECOND", sql);
        Checks.Has("GETYEAR(\"T0\".\"CREATEDAT\")", sql);
    }

    [Fact]
    public void Like_escapes_with_a_backslash_so_the_tilde_cell_stays_a_single_family()
    {
        var query = orm().Select<Order>();

        var sql = query.Where(o => o.CustomerName!.Contains("50%_off")).ToSql(out var parameters);

        // 转义符这一格五家是 4∶1 —— MySQL 血统换字符的那条理由（'\' 在默认 sql_mode 下是一条未闭合的串）
        // 在这一家没有任何对应物【产物里三处都没有】，所以不跟第四家。
        Checks.Has("LIKE :p0 ESCAPE '\\'", sql);
        Assert.Equal("%50\\%\\_off%", parameters[0].Value);

        // 与第三家逐字同形（同一颗前缀、同一颗转义符），所以这一条<em>不</em>成基线。
        Assert.Equal(dameng().Select<Order>().Where(o => o.CustomerName!.Contains("50%_off")).ToSql(), sql);
    }

    // ── CodeFirst：类型名、IDENTITY 与那一颗原生 COMMENT ──────────────

    [Fact]
    public void Create_table_writes_the_identity_clause_and_carries_no_table_options()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, orm().Schemas.Contract<Order>()));

        Verify("ddl-order", sql);

        // 模式名一个都不写（与达梦、MySQL 血统同判，理由各不同：那两家一家是"当前用户模式"、
        // 一家是"连接串带来的库"，这一家按产物观察到的形状是"没人写过限定"）。
        Checks.Has("CREATE TABLE \"QUICKSQLORDER\" (", sql);
        Checks.Absent("\"APP\".", sql);
        Checks.Absent("\"PUBLIC\".", sql);

        // 类型名逐列：布尔落 TINYINT（库有 BOOLEAN 而这里不取，三条理由在 XuguTypes）、
        // 无宽度字符串落 CLOB（TEXT 只是 VARCHAR 的同义字，吃不穿行 64K）、时刻带 3 位小数秒
        // （这一颗是本文件唯一的【推断】，理由在那一行的注释里）。
        Checks.Has("\"ID\" INT IDENTITY(1,1) NOT NULL", sql);
        Checks.Has("\"CUSTOMERNAME\" VARCHAR(64) NULL", sql);
        Checks.Has("\"AMOUNT\" NUMERIC(18,4) NOT NULL", sql);
        Checks.Has("\"CREATEDAT\" DATETIME(3) NOT NULL", sql);
        Checks.Has("\"NOTE\" CLOB NULL", sql);
        Checks.Has("CONSTRAINT \"PK_QUICKSQLORDER\" PRIMARY KEY (\"ID\")", sql);

        // 尾部什么都没有：没有 ENGINE/CHARSET（那一家默认引擎不保证有事务，这一家只有一个存储引擎【推断】），
        // 而字符集<em>不在表上而在连接串上</em>（CHAR_SET=），于是 §9.3 那句"默认映射到宽的一侧"在这一家不由 DDL 保证。
        Checks.Absent("ENGINE", sql);
        Checks.Absent("CHARSET", sql);
        Checks.Absent("TABLESPACE", sql);

        // 另三族的自增写法与它们的类型名不许漏进来。
        Checks.Absent("AUTO_INCREMENT", sql);
        Checks.Absent("GENERATED BY DEFAULT", sql);
        Checks.Absent("NVARCHAR", sql);
    }

    [Fact]
    public void A_named_schema_qualifies_the_table_and_folds_it_too()
    {
        var sql = Statement(w => Provider.CodeFirst.EmitCreateTable(w, ormWithSchema().Schemas.Contract<Order>()));

        Verify("ddl-qualified", sql);

        // 限定符出来了，而且<em>被折叠</em>——模型里写 app、发出去是 "APP"（与达梦同判、与 MySQL 血统反向：
        // 那一家的敏感开关在文件系统上，既不能赌区分也不能赌不区分；这一家折叠是解析器行为，不折就查不到）。
        Checks.Has("CREATE TABLE \"APP\".\"QUICKSQLORDER\" (", sql);
        Checks.Absent("\"app\"", sql);
    }

    [Fact]
    public void Add_column_change_precision_and_comment_are_three_independent_statements()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));
        var commented = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName)) with { Comment = "客户名" };

        var sql = string.Join(" ; ",
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, note)),
            Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, amount)),
            Statement(w => Assert.True(Provider.CodeFirst.TryEmitColumnComment(w, contract.Table, commented))));

        Verify("ddl-alter-and-comment", sql);

        Checks.Has("ALTER TABLE \"QUICKSQLORDER\" ADD COLUMN \"NOTE\" CLOB NULL", sql);

        // 改精度一句说完，而可空性<em>必须在同一条里重述</em>：这一格是本文件出处最薄的一格
        // （文档那一侧本次没读到 ALTER COLUMN 的语法页，两份产物里也没有改列文本），取的是
        // "跟着会把可空性一起重置的那一族写"—— 重述在两种库行为下都不会错，不重述在 T-SQL 那一形上是静默改约束。
        Checks.Has("ALTER TABLE \"QUICKSQLORDER\" ALTER COLUMN \"AMOUNT\" NUMERIC(18,4) NOT NULL", sql);
        Checks.Absent("MODIFY COLUMN", sql);

        // 注释是独立一句<em>而且返回真</em>：五家里四家原生 COMMENT ON，只有 T-SQL 走存储过程。
        // 这一格是"新家族没有把分布拆细而是并入多数派"的少数几格之一。
        Checks.Has("COMMENT ON COLUMN \"QUICKSQLORDER\".\"CUSTOMERNAME\" IS '客户名'", sql);
        Checks.Absent("sp_addextendedproperty", sql);
        Checks.Absent("N'", sql);

        // 与 MySQL 血统反向的一件：那一家注释跟着列定义走，所以改精度会顺手带出 COMMENT；这一家两件事是两句。
        var precision = Statement(w => Provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, commented));

        Checks.Absent("COMMENT", precision);
    }

    [Fact]
    public void A_full_text_index_is_refused_and_the_refusal_names_the_kind_the_contract_lacks()
    {
        var contract = orm().Schemas.Contract<Order>();

        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("FT_QuickSqlOrder_Note", IndexKind.FullText, [nameof(Order.Note)]) { ColumnNames = ["Note"] })));

        // 这一家的"抛"与前两家不同档：文档那一页列的索引种类是普通、UNIQUE、BITMAP 三种【文档】——
        // 全文那一档<em>压根不在那张单子上</em>，所以这不是"有但没接"而是"没读到它有"。
        Checks.Has("FULLTEXT", failure.Message);
        Checks.Has("BITMAP", failure.Message);

        // BITMAP 是这一族在这一格多出来的那一支（四家里没有一家有），而 IndexKind 里没有那一颗 ——
        // "这一家有第四种索引而契约上没有那一格"与 WM_CONCAT 同一批，记 §14。
        Assert.False(Enum.GetNames<IndexKind>().Contains("Bitmap", StringComparer.Ordinal));

        var plain = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("IX_QuickSqlOrder_Note", IndexKind.NonClustered, [nameof(Order.Note)]) { ColumnNames = ["Note"] }));

        // 方向照多数派写出来，而这一格是"产物在这格上比文档话多"的一处：两份建索引文本都带了方向。
        Checks.Has("CREATE INDEX \"IX_QUICKSQLORDER_NOTE\" ON \"QUICKSQLORDER\" ( \"NOTE\" ASC )", plain);

        var unique = Statement(w => Provider.CodeFirst.EmitCreateIndex(w, contract.Table,
            new IndexContract("UX_QuickSqlOrder_Note", IndexKind.Unique, [nameof(Order.Note)]) { ColumnNames = ["Note"] }));

        Checks.Has("CREATE UNIQUE INDEX \"UX_QUICKSQLORDER_NOTE\"", unique);
    }

    [Fact]
    public void A_rowversion_column_is_refused_at_the_ddl_emission_point_and_only_there()
    {
        // v0.34 第二笔的裁决落点（§14「要不要拒收」，「全部完善」整批授权自决为拒收）。
        // 判据不在"这一家认不认这个词"——认，库里建得成还会自增（RV-01/RV-11）—— 在"建出来我们用不用得了"：
        // 带它的投影读即 System.DataOutOfBindException（RV-08/A-06），§5.8 不许建出"看起来能用"的列。
        var contract = orm().Schemas.Contract<InventoryItem>();
        var version = contract.Columns.Single(c => c.MemberName == nameof(InventoryItem.RowVersion));
        var checkedAt = contract.Columns.Single(c => c.MemberName == nameof(InventoryItem.CheckedAt));

        // 1) 整表与另一支单列都抛：闸门坐在 EmitColumnDefinition 上，CREATE TABLE / ADD COLUMN /
        //    ALTER COLUMN 三支共用它，所以一处立闸、三处吃到。
        var failure = Checks.Raised<QuickSqlException>(() => Statement(w => Provider.CodeFirst.EmitCreateTable(w, contract)));

        Checks.Has("System.DataOutOfBindException", failure.Message); // 说出为什么
        Checks.Has("[ConcurrencyToken]", failure.Message);           // 说出该改成什么

        Assert.Throws<QuickSqlException>(() =>
            Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, version)));

        // 2) 同一条判据吃三种字形（大小写与前后空白都关不住它）。
        foreach (var declared in new[] { "rowversion", "ROWVERSION", " RowVersion " })
            Assert.Throws<QuickSqlException>(() =>
                Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, version with { StoreTypeName = declared })));

        // 3) 闸门只点名那一颗：人写的别的名字照旧原样穿过 —— "这一族认哪些名"的清单仍是"全认、不校验"，
        //    rowversion 是第一颗也是唯一一颗例外（XuguTypes.GetStoreType 的注释同一句）。
        var echo = Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, version with { StoreTypeName = "NUMERIC(9,2)" }));

        Checks.Has("\"ROWVERSION\" NUMERIC(9,2) NULL", echo);

        // 4) 同一份契约的第二格并发列（应用给值那一档）不受牵连：拒收吃的是那一列，不是那一格语义。
        var untouched = Statement(w => Provider.CodeFirst.EmitAddColumn(w, contract.Table, checkedAt));

        Checks.Has("\"CHECKEDAT\" DATETIME(3) NULL", untouched);
    }

    // ── 内联字面量（ToLogSql 那一档）─────────────────────────────────

    [Fact]
    public void Text_inlines_here_because_doubling_the_quote_is_the_one_form_both_tiers_agree_on()
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

        // 与第四家<em>正好相反</em>的那一条：那一家把字符串内联整个关掉，因为两种 sql_mode 下没有一种统一写法；
        // 这一家两档出处在同一句话上重合（单引号翻倍），那份 shipped 实现做的也只有翻倍那一半。
        // 厂商另提了一句"或用转义字符"而<em>没写是哪一颗</em> —— 那句悬空的话记 §14，重开条件与那颗共用。
        Assert.Equal(2, emitted.Parameters.Count);
        Checks.Has("'客户甲'", log);
        Checks.Has(@"'C:\temp'", log);
        Checks.Absent(":p0", log);

        // 代价边界与另四家同一条：TryInlineLiteral 在 src/ 里只有 SqlLogging 一个消费者，
        // 也就是说这里改的只是"日志里那条能不能直接复制去跑"，执行侧一个字节都不走这条路。
        Checks.Has("INSERT INTO \"QUICKSQLORDER\"", log);
    }

    [Fact]
    public void The_bool_literal_follows_the_column_type_rather_than_the_type_the_library_offers()
    {
        Assert.True(Provider.Values.TryInlineLiteral(true, null, out var flag));
        Assert.Equal("1", flag);

        Assert.True(Provider.Values.TryInlineLiteral(false, null, out var unset));
        Assert.Equal("0", unset);

        // 这一格是交账前改判的第三格：设计稿取 TRUE（理由是"驱动登记了 bool 这一型"），
        // 改判按<em>三处一致</em>：列是 TINYINT、谓词比的是 = 1、内联若发 TRUE 就成了三处各说一句话，
        // 猜错的那一半是 INSERT 直接类型不匹配。四家分布因此从 2∶2 变成 3∶2。
        Assert.NotEqual("TRUE", flag, StringComparer.Ordinal);

        Assert.True(Provider.Values.TryInlineLiteral(new DateTime(2026, 3, 4, 5, 6, 7, 890), null, out var moment));
        Assert.Equal("'2026-03-04 05:06:07.890'", moment);

        // 分隔符是空格而不是 ISO 的 T，这一格是【产物】给的：同一个格式串在两份独立实现里各写一遍
        // （MySQL 血统那一家的同一颗是文档给的规范形，两档不同）。
        Assert.DoesNotContain("T05:06", moment, StringComparison.Ordinal);

        // 二进制不内联，而理由与第四家不同条：那边是包体上限管不着内联，这边是两份出处各给一形
        // （'CAFE' 与 '\x…'），而那颗没指明的转义字符会改变后一形的读法。
        Assert.False(Provider.Values.TryInlineLiteral(new byte[] { 1, 15 }, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), null, out _));

        Assert.True(Provider.Values.TryInlineLiteral(Guid.Empty, null, out var id));
        Assert.Equal("'00000000-0000-0000-0000-000000000000'", id);
    }

    [Fact]
    public void A_floating_point_literal_keeps_the_dot_that_the_first_two_families_learned_to_add()
    {
        // 第五份同一判据：抄的是 SqlServerValues 那一份 2026-09-22 真库实测<em>之后</em>的形状
        // （1E-08.0 是语法错，修复前后的两版在另两族各撞出来一次，§13.5）。
        // 这一家的 FLOAT/DOUBLE 列上 3 与 3.0 是不是同一档隐式转换<em>未实测</em>，取"与另四家一致"。
        Assert.True(Provider.Values.TryInlineLiteral(3d, null, out var whole));
        Assert.Equal("3.0", whole);

        Assert.True(Provider.Values.TryInlineLiteral(2.5f, null, out var fraction));
        Assert.Equal("2.5", fraction);

        Assert.True(Provider.Values.TryInlineLiteral(1e-08, null, out var tiny));
        Assert.Equal("1E-08", tiny);

        Assert.False(Provider.Values.TryInlineLiteral(double.NaN, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.PositiveInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(double.NegativeInfinity, null, out _));
        Assert.False(Provider.Values.TryInlineLiteral(float.NegativeInfinity, null, out _));
    }

    // ── 类型映射：两处"库有而这里不用"，一处"问不出第二把尺子" ────────

    [Fact]
    public void A_column_type_this_family_cannot_bind_is_refused_rather_than_narrowed()
    {
        var contract = orm().Schemas.Contract<Order>();
        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        // §9.3 给 DateTimeOffset 写的退路（降级 DateTime UTC + 列标记 + 读取端还原）那两半今天都不存在，所以抛。
        // 而这一家<em>有</em>带时区的类型（TIME/DATETIME/TIMESTAMP 各带一档 WITH TIME ZONE【文档】），
        // 缺的是绑定侧：驱动的 CLR 登记表里没有 System.DateTimeOffset。文案因此不许说"这一家没有那一型" ——
        // 说了，现场就会去找 TIMESTAMP WITH TIME ZONE 并以为自己找到了退路。
        var offset = amount with { ClrType = typeof(DateTimeOffset), StoreTypeName = null };

        var failure = Checks.Raised<QuickSqlException>(() => Provider.Types.GetStoreType(offset));

        Checks.Has("TIMESTAMP WITH TIME ZONE", failure.Message);
        Checks.Has("DateTimeOffset", failure.Message);
        Checks.Has("降级", failure.Message);

        var unregistered = amount with { ClrType = typeof(Uri), StoreTypeName = null };

        var unknown = Checks.Raised<QuickSqlException>(() => Provider.Types.GetStoreType(unregistered));

        Checks.Has("未登记 CLR 类型", unknown.Message);
        Checks.Has("Uri", unknown.Message);
    }

    [Fact]
    public void Two_types_the_library_offers_and_this_provider_still_writes_the_wide_way()
    {
        var ticket = orm().Schemas.Contract<Ticket>();
        var token = orm().Schemas.Contract<SessionToken>();

        // 布尔：库有 BOOLEAN/BOOL 一字节档，而这里落 TINYINT —— 三条理由（谓词/内联/列三处一致、
        // 声明能力位会把谓词推到没出处的 IS TRUE、读侧仍然认得 BOOLEAN）写在 XuguTypes 那一格里。
        Assert.Equal("TINYINT", Provider.Types.GetStoreType(ticket.Columns.Single(c => c.MemberName == nameof(Ticket.Deleted))));

        // Guid：厂商说 GUID 就是 16 字节的 uuid【文档】，两家 shipped 适配器一致发 char(36)【产物】。
        // 两边都有出处的一格取产物那一支，理由与另两家不同：这一家不是"没有那一型"而是"两型都有、按跨库默认取文本"。
        Assert.Equal("CHAR(36)", Provider.Types.GetStoreType(token.Columns.Single(c => c.MemberName == nameof(SessionToken.Id))));

        // 两型都有 ⇒ 那两颗能力位都不能顺手声明：Guid_Raw16 要靠"字节置换与连接串一致"才成立（SPI 没那一格），
        // Boolean_Type 一声明就把谓词推到 IS TRUE 那一支。
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Guid_Raw16));
        Assert.False(Provider.Capabilities.HasFlag(DbCapabilities.Boolean_Type));

        // 时刻：TIME(3) 是【文档】（p∈[0,3] 明写），DATETIME(3) 是【推断】（同一页没给那两行的限定词）——
        // 一颗精度限定词在相邻两型上一档文档级、一档猜的，这是"出处按格标不按族标"最好的样本。
        Assert.Equal("DATETIME(3)", Provider.Types.GetStoreType(token.Columns.Single(c => c.MemberName == nameof(SessionToken.ExpiresAt))));
        Assert.Equal("DATE", Provider.Types.GetStoreType(amountless() with { ClrType = typeof(DateOnly), StoreTypeName = null }));

        ColumnContract amountless()
            => orm().Schemas.Contract<Order>().Columns.Single(c => c.MemberName == nameof(Order.Amount));
    }

    [Fact]
    public void The_width_gate_is_a_column_ceiling_while_the_row_budget_has_no_slot()
    {
        var contract = orm().Schemas.Contract<Order>();
        var note = contract.Columns.Single(c => c.MemberName == nameof(Order.Note));
        var name = contract.Columns.Single(c => c.MemberName == nameof(Order.CustomerName));

        // 60000 是厂商那一页<em>写在字上的</em>列定义上限【文档】，而 MySQL 血统那颗 16383 是从行预算推出来的
        // （65535 / 4）—— 同一个槽位、两种出处档位。超过它不是"再写宽一点"而是换一档型：CLOB（2GB 那一档）。
        Assert.Equal("VARCHAR(64)", Provider.Types.GetStoreType(name));
        Assert.Equal("VARCHAR(60000)", Provider.Types.GetStoreType(note with { MaxChars = 60_000, StoreTypeName = null }));
        Assert.Equal("CLOB", Provider.Types.GetStoreType(note with { MaxChars = 60_001, StoreTypeName = null }));

        // 长度量纲是字符，这一格在五家是第二颗文档级的（第一颗是金仓）。但<em>第二把尺子</em>仍然问不出来：
        // 目录里只有一个宽度，而且它与精度/标度共用一颗字段（拆包写在 XuguSchemaReader）——
        // 所以 §10.4 那张表里这一家落金仓那一档，ProbeLengthSemanticsAsync 是退化实现而不是探测。
        var stored = Provider.Types.ApplyStorageDefaults(name);

        Assert.Equal(LengthSemantics.Char, Provider.Types.GetLengthSemantics(stored));
        Assert.Equal(256, stored.MaxBytes);
        Assert.True(stored.Unicode);

        // 那颗 ×4 是一把纯宽松的尺子（字符集由会话决定，列上没有厂商字段记着"这一列最多几字节"），
        // 而真正的行级预算（64K【文档】）在 ColumnContract 上没有槽位承接 —— 缺口与 MySQL 那一格并排记 §14。
        Assert.Null(Provider.Types.ApplyStorageDefaults(note).MaxBytes);
    }

    // ── 槽位本身：这一档声明了什么、没声明什么 ────────────────────────

    [Fact]
    public void The_family_declares_eight_bits_and_every_channel_bit_has_offline_text_behind_it()
    {
        var declared = Enum.GetValues<DbCapabilities>()
            .Where(static bit => bit != DbCapabilities.None && Provider.Capabilities.HasFlag(bit))
            .ToArray();

        // 8 = 7 离线文本 + 1 内检索引位（【本笔（ClickHouse 重开·笔一）】挂到族档的 Transactions）。
        // 7 离线那七颗"仅文档位是 0"的旧读数不动，只是 7 之后又涨了 1 —— 新的那颗不是
        // "敢不敢声明乐观位"，是内核写路径真的按它分岔；"离线文本在后头"那一半对它不适用，
        // 它的账在 TransactionCapabilityTests 那本判定册上。
        Assert.Equal(8, declared.Length);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, declared);
        Assert.Contains(DbCapabilities.Schemas, declared);
        Assert.Contains(DbCapabilities.Guid_Char36, declared);
        Assert.Contains(DbCapabilities.Comment_On_Column, declared);
        Assert.Contains(DbCapabilities.Alter_Column_Precision, declared);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, declared);

        // 第二家在离线四族里声明 Returning —— 金仓那一家是第一位，而它到 v0.31 为止也是唯一一位
        // "离线家族里 RETURNING 有产物出处"的（这一家那一半只有<em>一份</em>产物走过，见上面那两条回填用例）。
        Assert.Contains(DbCapabilities.Returning, declared);
        Assert.False(Dameng.Provider(SqlServer.NoDriverFactory.Instance).Capabilities.HasFlag(DbCapabilities.Returning));

        // 交集那一格：接第五家之前 §13.1 写的是"离线四族共有三位"，今天仍是三位 ——
        // 那一句"每接一族交集就小一格"从今天起有了一份反证。它由 CapabilityLedgerTests 数，这里只钉本家的位。
        Assert.DoesNotContain(DbCapabilities.MultiStatement, declared);
        Assert.DoesNotContain(DbCapabilities.Upsert_MergeInto, declared);
        Assert.DoesNotContain(DbCapabilities.Identity_ExplicitInsert, declared);
        Assert.DoesNotContain(DbCapabilities.Json_Type, declared);
    }

    [Fact]
    public void The_bulk_slot_is_the_second_one_to_answer_null_so_the_fallback_now_has_two_customers()
    {
        // 与第三家同一档：没有能举证的参数预算数字 ⇒ 交回 null，分块器走那颗 100 的保守兜底。
        // 三家各有一颗指得出的数（2100 / 32767 / 65535），两家指不到 —— 而"报大了得到真库一句参数过多、
        // 报小了只是多几次往返"这一句判据在两家身上给出同一个答案。
        Assert.Null(Provider.Bulk);

        var plan = orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        // 600 行 × 7 列按 100 切：900/7 → 每块 12 行 → 50 块。<b>第三家那句话"只有这一族是 50 块"从今天起不成立</b>
        // （那一格在 DamengDialectTests 里同步翻格），可数的是"两家同数"而不是"一家孤数"。
        Assert.Equal(100, plan.ParameterBudget);
        Assert.Equal(7, plan.ColumnsPerRow);
        Assert.Equal(12, plan.RowsPerStatement);
        Assert.Equal(50, plan.Chunks.Count);
        Assert.Equal(600, plan.Chunks.Sum(static c => c.RowCount));
        Checks.Has("参数预算 100", plan.Reason);

        var damengPlan = Dameng.Orm().InsertRange(Enumerable.Range(1, 600).Select(static n => row(n)).ToArray()).Explain();

        Assert.Equal(damengPlan.Chunks.Count, plan.Chunks.Count);
        Assert.Equal(damengPlan.ParameterBudget, plan.ParameterBudget);
    }

    [Fact]
    public void The_two_slots_nobody_reads_yet_answer_differently_here_than_in_the_fourth_family()
    {
        // ConcatOperator 与 SupportsRegex 在 src/ 里零消费者（五家同一格），所以它们今天只是台账上的一句话。
        // 而这一格在这一家<em>不是</em>多数派：MySQL 血统为了避免 "||" 被折成逻辑 OR 而报了 CONCAT，
        // 这一家没有那颗模式，跟着另三家报 "||"。今天谁都不消费它，将来接它的人手里就有两种答案了。
        Assert.Equal("||", Provider.Expressions.ConcatOperator);

        // 反向那一格写成正面断言（"另一家报的是 CONCAT"），而不是 NotEqual("||")：
        // 后者在一堆可能值里只排除一个，前者把这一版的分歧到底长什么样写在纸上。
        Assert.Equal("CONCAT", MySql.Provider(SqlServer.NoDriverFactory.Instance).Expressions.ConcatOperator);

        // 转义符是有消费者的一格（翻译器与 DialectBase 各读一次），所以它落在上面的 where-like 样本上。
        Assert.Equal('\\', Provider.Expressions.LikeEscapeCharacter);

        // 正则：三档出处里这一颗一档都没有（产物没出现过正则函数，文档那一侧本次没读到对应页）。
        // §5.7 的 P0 行里没有正则槽位、共用层没有消费方 —— 声明一句没有消费者的"支持"正是台账要避免的东西。
        Assert.False(Provider.Expressions.SupportsRegex);
    }

    [Fact]
    public void The_identity_slot_and_the_key_slot_agree_that_nobody_in_core_reads_the_toggle()
    {
        // 不支持显式自增写入，而且<em>刻意不为它留覆写点</em>：IdentityInsertToggle 在 Core 里至今零调用点，
        // 覆写它只会得到一颗永远不被调用的虚函数（这一格第三家已经记进 §14，这里是第二家有同样的形状）。
        Assert.False(Provider.Keys.SupportsExplicitIdentityInsert);
        Assert.Null(Provider.Keys.IdentityInsertToggle(NewWriter(), new TableInfo(null, "QUICKSQLORDER"), true));

        // 批量位今天为 <b>false</b>（v0.34 收回：v0.31 立句时它是 true，且当时注释说"这一颗位是 InsertEngine
        // 在 src/ 里唯一真正读的那一个"——两句都被真库换掉：N-06/N-07 交空结果集，而 v0.34 第一笔让 Core
        // 开始读第二颗 SupportsPostStatementKeyRead）。两颗合起来才是这一家的回填档：逐条 + 语句后回读。
        Assert.False(Provider.Keys.SupportsBulkKeyRetrieval);
        Assert.True(Provider.Keys.SupportsPostStatementKeyRead);

        // 没有开关 ⇒ 这一族的会话状态里没有一颗需要 provider 替它开；与"三处都没有的会话参数"那条缺口不同格。
        Assert.False(Provider.Dialect.ParameterPrefix == '@');
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    static SqlWriter NewWriter(IDbProvider? provider = null)
        => new(new StringBuilder(), [], (provider ?? Provider).Dialect.ParameterPrefix);

    static string Statement(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }

    // ISqlDialect 的 Emit 按语句类型各一记重载，没有"收一份 SqlStatement"的那一口 —— 所以每种语句各一个发射器。
    static DialectSql EmitUpdate(UpdateStatement ast, IDbProvider? provider = null)
    {
        var writer = NewWriter(provider);

        (provider ?? Provider).Dialect.Emit(in ast, writer);

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

    /// <summary>守卫的文本，收 provider 参数是为了<em>拿它跟第三家逐字对读</em>：这一族没有 UPDATE … FROM，
    /// 合并档发不出去（上面那两条拒绝用例量的就是这件事），而守卫仍然要有文本见证 —— 于是挂在一条
    /// <em>不带表源</em>的最小 UPDATE 上发射，那颗 SET 与守卫无关，它存在的理由只是"UPDATE 必须有一句 SET"。
    ///
    /// <para>两侧的别名按 <c>EntityTracker</c> 的真实构造取：表侧走 <c>WriteTargetAlias</c>（这一家与达梦都是表名本身），
    /// 行集侧仍是 t1。所以这段文本读得出的是守卫的形状与前缀的来路，读不出"这条语句是不是 SaveChanges 会发的那一条"
    /// —— 它不是，那一档在这一家永远不会被走到。</para></summary>
    static string GuardText(string member, IDbProvider? provider = null)
    {
        var owner = provider ?? Provider;
        var contract = orm().Schemas.Contract<Order>();
        var column = contract.Columns.Single(c => c.MemberName == member);

        var stored = new TableRef(0, owner.Dialect.WriteTargetAlias(contract.Table), contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var amount = contract.Columns.Single(c => c.MemberName == nameof(Order.Amount));

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(amount, new ParameterValue(1m, amount))],
            Where = owner.ChangeGuard.BuildDifference(column,
                new ColumnExpression(stored, column), new ColumnExpression(side, column)),
        };

        return EmitUpdate(ast, owner).Text;
    }

    /// <summary>手搭一份合并 UPDATE，形状与 <c>EntityTracker.WriteMergedAsync</c> 同构（前四族各有一份，
    /// 见 <see cref="ChangeGuardTests"/>、<c>KingbaseDialectTests</c>、<c>DamengDialectTests</c>、<c>MySqlDialectTests</c>）。
    /// <b>在这一家它唯一的用途是"抛得准不准"</b>：发射必然在 <c>EmitJoinedSources</c> 那一句上红 ——
    /// 所以它不需要键与行集值都真实，只要表源在就够了。</summary>
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

        var difference = Provider.ChangeGuard.BuildDifference(set,
            new ColumnExpression(stored, set), new ColumnExpression(side, set));

        return EmitUpdate(new UpdateStatement
        {
            Table = stored,
            Sets = [new SetClause(set, new ColumnExpression(side, set))],
            Where = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, key), new ParameterValue(7, key)),
            Joins = [new JoinClause(JoinKind.Inner, new SourceRows(side, columns, rows), on)],
        });
    }
}
