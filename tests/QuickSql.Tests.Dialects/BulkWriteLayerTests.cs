using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.TestInfra;
using QuickSql.Write;
using Xunit;
using Da = System.ComponentModel.DataAnnotations;

namespace QuickSql.Tests.Dialects;

/// <summary>§12.1 写侧扩展那两条链（<c>UpdateRange</c> 的按主键整行更新、<c>DeleteRange</c> 的按主键删整批）
/// 在离线上一问一答的册子：<b>档是谁定的、并进来的那一条命令长什么形状、参数在跨语句时怎么编号、
/// 切块边界上两个取文本的口各怎么响、三颗不出 SET 的列、四条拒收各指哪儿</b>。
///
/// <para>与 <see cref="BulkLayerTests"/> 的分工：那一册量的是<em>插入</em>那条阶梯（装载通道 L1 与多值 L3），
/// 本册量的是<em>写侧扩展</em>这一族档位（Merged 与 SingleRow，没有装载通道那一档）。两册共用同一套纪律：
/// 全程 <see cref="SqlServer.NoDriverFactory"/>，一旦有用例把它碰着就是"生成期混进了连接依赖"的缺陷信号；
/// 断言失败的消息里带整条产出的 SQL 或整句 Reason。</para>
///
/// <para><b>为什么两条链能在离线判到执行层那一半之外的一切</b>：档位判定是纯函数（只看 provider 在
/// <see cref="IBulkStrategy"/> 上的表态与行数），文本层只看方言发射器，两样都不欠连接。
/// 剩下的那一半——分号批在活库上收不收、<c>IN</c> 清单真的删掉几行——在集成册，本册一条都不许连库。
/// <b>家与家之间的表态差本册逐家过一遍</b>（<see cref="WriteFamilies"/> 那一张十六行的手写表：哪家 true、
/// 哪家 false、哪家整颗 <c>Bulk</c> 是 null 而吃共用层的兜底），因为"所有 provider 都在这颗位上表过态"
/// 这件事只有一处判据：把十六家全部走一遍。能力台账（<see cref="CapabilityLedgerTests"/>）量的是
/// <c>DbCapabilities</c> 那一张位图，与这颗策略槽不重叠。</para></summary>
public sealed class BulkWriteLayerTests
{
    /// <summary>本册主靶：SQL Server 真预算 2100。<c>Order</c> 的契约 8 列，<c>Id</c> 是自增主键
    /// ⇒ 每行写 <c>SET</c> 7 列 + <c>WHERE</c> 1 列 = 8 颗参数，合并档块宽因此是 236 行。</summary>
    const int Budget = 2100;

    const int PerRow = 8;

    const int MergedWidth = Budget * 9 / 10 / PerRow;

    /// <summary><c>DeleteRange</c> 的 <c>IN</c> 清单每行只有一颗键参数，所以同一颗预算给它算出的块宽宽得多。</summary>
    const int DeleteWidth = Budget * 9 / 10;

    static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, 890);

    static IDbProvider Real() => SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => SqlServer.Orm();

    /// <summary>把预算掰小的那一家：切块边界那一格要跨到第二条命令，真预算得灌 237 行实体，
    /// 而判据与行数无关（见 <see cref="LowBudgetWriteProvider"/>）。</summary>
    static QuickSqlOrm ormAt(int budget)
        => new(new QuickSqlOrmOptions
        {
            Provider = LowBudgetWriteProvider.Around(Real(), budget),
            ConnectionString = SqlServer.OfflineConnectionString,
        });

    static QuickSqlOrm ormWith(QuickSqlAop aop, int budget = Budget)
        => new(new QuickSqlOrmOptions
        {
            Provider = budget == Budget ? Real() : LowBudgetWriteProvider.Around(Real(), budget),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        });

    /// <summary><see cref="Order"/> 的一行，<b>主键给值</b>：这一条链按主键定位，
    /// <c>Id</c> 空着（<c>default</c>）也走得通，但那样发出的 <c>WHERE [Id] = 0</c> 判不出"键落进了哪一格"。</summary>
    static Order row(int n) => new()
    {
        Id = n,
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = Moment,
        Note = "备注" + n,
    };

    static Order[] rows(int count) => [.. Enumerable.Range(1, count).Select(row)];

    static Ticket ticket(int n) => new() { Id = n, Title = "工单" + n, TenantId = 3, Deleted = false };

    // ── 十六家的表态表 ────────────────────────────────────────────────

    /// <summary>十六家各自<em>应该</em>落到哪一档，这张表是手写的（不是从 provider 读回来的）：
    /// 读回来的话这条用例就只是在问 provider 自己"你怎么说"，那份产出永远是绿的。
    /// 三档来源各一批：<c>2100</c> 只有 SQL Server，<c>65535</c> 是 MySQL 血统那三家（共用 <c>MySqlBulk</c>），
    /// <c>32767</c> 是 PG 血统那六家（共用 <c>PostgreSqlBulk</c>），<c>100</c> 是整颗 <c>Bulk</c> 交回 null
    /// 而由共用层兜底的六家（Oracle 血统四家 + SQLite + Gbase8s）。</summary>
    static readonly (string Family, int Budget, bool Merged, bool Upsert)[] FamilyRows =
    [
        ("sqlserver", 2_100, true, false),
        ("mysql", 65_535, true, true),
        ("oceanbase", 65_535, true, true),
        ("tidb", 65_535, true, true),
        ("postgresql", 32_767, false, true),
        ("kingbase", 32_767, false, true),
        ("highgo", 32_767, false, true),
        ("gaussdb", 32_767, false, true),
        ("opengauss", 32_767, false, true),
        ("gbase8c", 32_767, false, true),
        ("oracle", 100, false, false),
        ("dameng", 100, false, false),
        ("shentong", 100, false, false),
        ("xugu", 100, false, false),
        ("sqlite", 100, false, false),
        ("gbase8s", 100, false, false),
    ];

    public static TheoryData<string, int, bool> WriteFamilies()
    {
        var data = new TheoryData<string, int, bool>();

        foreach (var (family, budget, merged, _) in FamilyRows)
            data.Add(family, budget, merged);

        return data;
    }

    /// <summary>同一张表的第四列单独成一条问句：<c>UpsertClause</c> 那颗槽在不在。
    /// 带 <c>merged</c> 那一列是为了把降级期望写成推导而不是第二张表——退下来落的那一档
    /// 正是那一家 <c>Auto</c> 取的那一档。</summary>
    public static TheoryData<string, bool, bool> UpsertFamilies()
    {
        var data = new TheoryData<string, bool, bool>();

        foreach (var (family, _, merged, upsert) in FamilyRows)
            data.Add(family, upsert, merged);

        return data;
    }

    /// <summary>把某一家的预算掰小到 <paramref name="budget"/>（内层仍是真方言，只改那一个数字）。
    /// upsert 档的切块只能在<em>接了句尾</em>的那一家身上量，所以这一颗不写死 <see cref="Real"/>。</summary>
    static QuickSqlOrm ormAt(string family, int budget)
        => new(new QuickSqlOrmOptions
        {
            Provider = LowBudgetWriteProvider.Around(ProviderFor(family), budget),
            ConnectionString = SqlServer.OfflineConnectionString,
        });

    static IDbProvider ProviderFor(string family) => family switch
    {
        "mysql" => MySql.Provider(SqlServer.NoDriverFactory.Instance),
        "postgresql" => PostgreSql.Provider(SqlServer.NoDriverFactory.Instance),
        _ => throw new NotSupportedException($"批量写册的 upsert 分块探针没有 {family} 这一家（要加就在 OrmFor 旁边一起加）。"),
    };

    /// <summary>按名取一家的离线组装根。默认分支<em>响</em>而不是交回 null：新增一家而忘了在这里登记，
    /// 要的是一句"这一家没有探针"，不是十六家里安静地少跑一家。</summary>
    static QuickSqlOrm OrmFor(string family) => family switch
    {
        "mysql" => MySql.Orm(),
        "oceanbase" => OceanBase.Orm(),
        "tidb" => TiDb.Orm(),
        "postgresql" => PostgreSql.Orm(),
        "kingbase" => Kingbase.Orm(),
        "highgo" => Highgo.Orm(),
        "gaussdb" => GaussDb.Orm(),
        "opengauss" => OpenGauss.Orm(),
        "gbase8c" => Gbase8c.Orm(),
        "oracle" => Oracle.Orm(),
        "dameng" => Dameng.Orm(),
        "shentong" => ShenTong.Orm(),
        "xugu" => Xugu.Orm(),
        "sqlite" => Sqlite.Orm(),
        "gbase8s" => Gbase8s.Orm(),
        "sqlserver" => SqlServer.Orm(),
        _ => throw new NotSupportedException($"批量写册里没有 {family} 这一家的探针（OrmFor 与 FamilyRows 要一起加）。"),
    };

    // ── 档是谁定的 ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(WriteFamilies))]
    public void Auto_takes_the_merged_tier_exactly_where_that_family_declared_it(string family, int budget, bool merged)
    {
        // 两颗表态的落点在这里对上号：十六家逐家过一遍，判据是那张手写的表而不是 provider 自己的回答。
        var chain = OrmFor(family).UpdateRange(rows(3));

        var report = chain.ExplainBulk();

        Assert.Equal(merged ? BulkWriteMethod.Merged : BulkWriteMethod.SingleRow, report.Method);
        Assert.Equal(BulkWriteMethod.Auto, report.Requested);

        // 全部不算降级：Auto 没承诺过任何一档，取阶梯上第一个满足条件的档就是它的定义（§12.2 同一条裁决）。
        Assert.False(report.Degraded);
        Assert.Equal(merged ? 1 : 3, report.RoundTrips);
        Checks.Absent("退回", report.Reason);

        // 预算那一路也逐家对：报得出数字的三家各用自己的数，报不出的六家吃共用层的兜底。
        Assert.Equal(budget, chain.Explain().ParameterBudget);
    }

    [Fact]
    public void The_merged_tier_is_declared_by_exactly_four_families_and_by_those_four_only()
    {
        // 这张表的<em>形状</em>也要有人钉：并档那一档今天只有 1 + 3 家有（SQL Server 与 MySQL 血统三家），
        // 而"谁家有"是一句会随落地点动的账。动它的人必须同时动这里，否则下面那条逐家用例就是在替改动背书。
        Assert.Equal(16, FamilyRows.Length);
        Assert.Equal(4, FamilyRows.Count(row => row.Merged));

        // 槽那一列同一条纪律，而它的分布是"血统整族"而不是"逐家"：MySQL 血统三家共用 MySqlBulk、
        // PG 血统六家共用 PostgreSqlBulk，两家各自覆写一颗 UpsertClause ⇒ 3 + 6 = 9 家可达。
        // 剩下的七家（T-SQL、Oracle 血统四家、虚谷、SQLite、GBase 8s）吃接口上那句默认 null。
        Assert.Equal(9, FamilyRows.Count(row => row.Upsert));

        // 每一位名字都要在 OrmFor 里有家：默认分支会响，所以这一句挡的是"表加了、探针没加"。
        foreach (var (family, _, _, _) in FamilyRows)
            Assert.NotNull(OrmFor(family));
    }

    [Fact]
    public void The_family_that_reports_no_tier_at_all_still_gets_a_block_width_from_the_fallback_budget()
    {
        // SQLite 交回的是 Bulk = null，于是预算走 StatementEmitter 那一份兜底（100）。
        // 这一格钉两件事：兜底在写侧扩展上同样有块宽可算（100×9/10÷8 = 11），
        // 以及"没有表态"落到的那一档是逐条而不是报错——与插入册那条兜底同一条算术。
        var report = Sqlite.Orm().UpdateRange(rows(30)).ExplainBulk();

        Assert.Equal(BulkWriteMethod.SingleRow, report.Method);
        Assert.Equal(1, report.ChunkSize);
        Assert.Equal(30, report.RoundTrips);
        Assert.Equal(100, Sqlite.Orm().UpdateRange(rows(30)).Explain().ParameterBudget);
        Checks.Has("今天没有多语句批的读数", report.Reason);
        Checks.Has("Auto 取阶梯上第一个满足条件的档", report.Reason);

        // 那一份块宽要在<em>表态可达 Merged</em> 的那一边才看得见：同一颗 100，配上 true 的表态
        // ⇒ 11 行一条命令、30 行切成 3 条。退不退是一回事，退下来之后每条塞几行是另一回事。
        var merged = ormAt(100).UpdateRange(rows(30)).WithBulk(BulkWriteMethod.Merged).Explain();

        Assert.Equal(11, merged.RowsPerCommand);
        Assert.Equal(3, merged.Commands.Count);
        Assert.Equal(100, merged.ParameterBudget);
    }

    [Fact]
    public void A_named_Merged_request_that_is_not_there_degrades_while_Never_throws()
    {
        // PG 血统那一家 false 是有条文靠山的（§9.4：扩展协议一次发送不收 ;-分隔的多条语句），
        // 但落到这一条链上的行为与任何一家 false 相同：点名而拿不到 → 按 fallback 三档办。
        var degraded = Kingbase.Orm().UpdateRange(rows(3)).WithBulk(BulkWriteMethod.Merged).ExplainBulk();

        Assert.Equal(BulkWriteMethod.Merged, degraded.Requested);
        Assert.Equal(BulkWriteMethod.SingleRow, degraded.Method);
        Assert.True(degraded.Degraded);
        Assert.Equal(3, degraded.RoundTrips);
        Assert.Equal(1, degraded.ChunkSize);
        Checks.Has("→ 退回逐条", degraded.Reason);
        Checks.Has("SupportsMergedStatements=false", degraded.Reason);

        var refused = Checks.Raised<QuickSqlException>(
            () => Kingbase.Orm().UpdateRange(rows(3)).WithBulk(BulkWriteMethod.Merged, FallbackPolicy.Never).ExplainBulk());

        // Never 那句是给 CI 的：它必须点名是谁没表态，否则现场只看到一个"批量写红掉"而不知道该改哪儿。
        Checks.Has("SupportsMergedStatements", refused.Message);
        Checks.Has("FallbackPolicy.Never 封死", refused.Message);
        Checks.Has(Kingbase.Provider(SqlServer.NoDriverFactory.Instance).Kind.ToString(), refused.Message);

        // 同一条请求在 true 那一家不咬人：Never 只在"拿不到"时生效，与插入册那条逐字同判据。
        Assert.Equal(BulkWriteMethod.Merged,
            orm().UpdateRange(rows(3)).WithBulk(BulkWriteMethod.Merged, FallbackPolicy.Never).ExplainBulk().Method);
    }

    [Fact]
    public void A_degradation_question_asked_of_the_report_does_not_move_the_chain()
    {
        // ExplainBulk(requested) 是问一答，不改 _requested：否则"先看一眼逐条长什么样"
        // 会把后面 Explain()/ExecuteBulk 的档位一起带走，那是现场最难查的一种串味。
        var update = orm().UpdateRange(rows(3));

        var peeked = update.ExplainBulk(BulkWriteMethod.SingleRow);

        // 那份回答里 Requested 记的就是被问的那一档（它是一次问答，不是链的请求）。
        Assert.Equal(BulkWriteMethod.SingleRow, peeked.Requested);
        Assert.Equal(BulkWriteMethod.SingleRow, peeked.Method);

        // 链自己那条路照旧：3 行并成一条命令，Reason 说的是并档那笔账。
        Assert.Single(update.Explain().Commands);
        Checks.Has($"每条命令 {MergedWidth} 行（1 条命令）", update.Explain().Reason);

        // 顺序反过来也一样：先取文本、再问另一档，已缓存的那份产出不被问话改写。
        var twice = orm().UpdateRange(rows(3));

        twice.Explain();
        twice.ExplainBulk(BulkWriteMethod.SingleRow);

        Assert.Single(twice.Explain().Commands);

        // 而点名过的那一档是留得住的：WithBulk 之后 ExplainBulk() 不带参数时读到的就是那一份请求。
        var named = orm().UpdateRange(rows(3)).WithBulk(BulkWriteMethod.SingleRow);

        Assert.Equal(BulkWriteMethod.SingleRow, named.ExplainBulk().Requested);
        Assert.Equal(BulkWriteMethod.SingleRow, named.ExplainBulk().Method);
    }

    // ── 并档那一条命令的形状 ──────────────────────────────────────────

    [Fact]
    public void The_merged_command_joins_statements_and_shares_one_parameter_table()
    {
        var update = orm().UpdateRange(rows(3));

        var report = update.ExplainBulk();
        var plan = update.Explain();

        Assert.Equal(MergedWidth, report.ChunkSize);
        Assert.Equal(1, report.RoundTrips);
        Assert.True(plan.IsSingleCommand);
        Assert.Equal(3, Checks.Occurrences(plan.Commands[0].Text, "UPDATE "));
        Assert.Equal(2, Checks.Occurrences(plan.Commands[0].Text, " ; "));

        // 编号长在参数表上（SqlWriter 的 NextParameterName 那句），所以并进来的三条语句天然不重号：
        // 3 行 × 8 颗 = 24 颗，名字从 @p0 连到 @p23。文本里出现 @p23 而参数表只有 24 条，两边对得上。
        var parameters = plan.Commands[0].Parameters;

        Assert.Equal(3 * PerRow, parameters.Count);
        Assert.Equal(3 * PerRow, parameters.Select(p => p.Name).Distinct().Count());
        Assert.Equal("@p0", parameters[0].Name);
        Assert.Equal($"@p{3 * PerRow - 1}", parameters[^1].Name);
        Checks.Has("@p23", plan.Commands[0].Text);

        // 出处跟着参数走：第 8 颗是第一行的键（WHERE Id），第 9 颗是第二行的第一个可写列。
        // 并档最容易错的正是这一格——两条语句共用一张表，串一位就是把 A 行的值写进 B 行。
        Assert.Equal("Id", parameters[7].Hint!.ColumnName);
        Assert.Equal("CustomerName", parameters[8].Hint!.ColumnName);

        Checks.Has("3 行 × 每行 8 颗参数（SET 7 列 + WHERE 1 列）", report.Reason);
        Checks.Has($"参数预算 {Budget} → 每条命令 {MergedWidth} 行（1 条命令）", plan.Reason);

        // 一条命令里仍然是 3 句：2026-10-07 改判之后这一格说的是"整批 3 句一次提交"，
        // 而不是旧那句按<em>命令数</em>判的话术（旧话术"跨命令不是原子的"今天只在开关关掉时回来）。
        Checks.Has("整批 3 句一次提交", plan.Reason);
        Checks.Absent("每条语句自己提交", plan.Reason);
    }

    [Fact]
    public void A_batch_transaction_opt_out_changes_the_sentence_and_not_the_text()
    {
        // WithBatchTransaction 那颗开关改的是执行段的递交姿势，<b>不是一个 SQL 形状</b>：
        // 两份产出的文本必须逐字相同，差别只在 Reason 说的那句话上——离线册能判的正好就是这一格，
        // "真包了事务"那一半归集成册（BulkWritePathTests）。
        var on = orm().UpdateRange(rows(5));
        var off = orm().UpdateRange(rows(5)).WithBatchTransaction(false);

        var wrapped = on.Explain();
        var unwrapped = off.Explain();

        Assert.Equal(wrapped.Commands[0].Text, unwrapped.Commands[0].Text);
        Checks.Has("整批 5 句一次提交", wrapped.Reason);
        Checks.Has("每条语句自己提交，半批可以在库里落地", unwrapped.Reason);
        Checks.Absent("一次提交", unwrapped.Reason);

        // 一句就删完的那一种不包：包它只多付两次往返，而一条语句本身就原子。
        Checks.Has("一条语句本身就原子", orm().DeleteRange(rows(3)).Explain().Reason);

        // 逐条档在这条链上同样是一句一命令，所以三行 = 三句，照样包（判据是语句数，不是命令数）。
        Checks.Has("整批 3 句一次提交", orm().DeleteRange(rows(3)).WithBulk(BulkWriteMethod.SingleRow).Explain().Reason);
    }

    [Fact]
    public void A_merged_batch_over_the_budget_splits_and_the_single_text_exits_refuse()
    {
        // 预算掰到 40 ⇒ 每条命令 4 行。5 行 = 两条命令（4 + 1），切块边界上的四件事在这里一次判完。
        var plan = ormAt(40).UpdateRange(rows(5)).Explain();

        Assert.Equal(2, plan.Commands.Count);
        Assert.Equal(4, plan.RowsPerCommand);
        Assert.Equal(4 * PerRow, plan.Commands[0].Parameters.Count);
        Assert.Equal(PerRow, plan.Commands[1].Parameters.Count);
        Assert.Equal(4, Checks.Occurrences(plan.Commands[0].Text, "UPDATE "));
        Assert.Equal(1, Checks.Occurrences(plan.Commands[1].Text, "UPDATE "));

        // 第二条命令的编号重新从 @p0 起：一次往返就是一条命令、一张参数表，续着编会让第二条命令
        // 单独拿出来时缺了前 32 颗——而那正是"把一条命令贴进查询器复现问题"的日常动作。
        Assert.Equal("@p0", plan.Commands[1].Parameters[0].Name);
        Checks.Has("@p7", plan.Commands[1].Text);

        // 两条命令没有"那一句 SQL"可交：ToSql() 拒并指路，ToLogSql() 带分节号让人看见确实有几条。
        var refused = Checks.Raised<QuickSqlException>(() => plan.ToSql());

        Checks.Has("没有单一 SQL 文本", refused.Message);
        Checks.Has("ToLogSql()", refused.Message);

        var log = plan.ToLogSql();

        Checks.Has("-- 命令 1", log);
        Checks.Has("-- 命令 2", log);

        // 原子性这一格要说出口。原文是「两条命令 = 两次往返，中间断了就是半批」而断的 phrase 是
        // "跨命令不是原子的"，2026-10-07 改判：默认整批一次提交（一批一事务），那句话换成新话术，
        // 旧话术只在 WithBatchTransaction(false) 那一条路上还在（见 A_batch_transaction_opt_out）。
        Checks.Has("整批 5 句一次提交", plan.Reason);
        Checks.Has("WithBatchTransaction(false) 可关", plan.Reason);
        Assert.Equal(2, ormAt(40).UpdateRange(rows(5)).ExplainBulk().RoundTrips);
    }

    [Fact]
    public void One_row_merged_and_one_row_per_row_are_the_same_text()
    {
        // §12.4 那句等价性的最小现场：块宽为 1 时并档就是一句语句，两条产出必须逐字相同。
        // 它同时是"分岔点在 Build 之下、不在它旁边"的证据——真有两条实现就会在这一格外跑出差别。
        var merged = orm().UpdateRange(rows(1)).WithBulk(BulkWriteMethod.Merged).Explain();
        var single = orm().UpdateRange(rows(1)).WithBulk(BulkWriteMethod.SingleRow).Explain();

        Assert.Equal(single.Commands[0].Text, merged.Commands[0].Text);
        Assert.Single(merged.Commands);
        Assert.Single(single.Commands);
        Checks.Absent(" ; ", merged.Commands[0].Text);

        // 逐条那一档的文本与整批的行数同数：3 行 = 3 条命令 = 3 次往返。
        var three = orm().UpdateRange(rows(3)).WithBulk(BulkWriteMethod.SingleRow).Explain();

        Assert.Equal(3, three.Commands.Count);
        Assert.Equal(1, three.RowsPerCommand);
        Assert.Equal(PerRow, three.Commands[2].Parameters.Count);
        Assert.Equal("@p0", three.Commands[2].Parameters[0].Name);
        Checks.Has("3 行逐条：3 条命令、每条一行", three.Reason);
    }

    [Fact]
    public void The_second_family_also_joins_and_quotes_its_own_way()
    {
        // 跨族的一格：并档这件事在 MySQL 血统上同样成立（那一家 SupportsMergedStatements=true），
        // 而被并进来的每条语句用的是那一家的标识符样式与参数前缀。分号分隔是两族共用的写法，
        // " ; " 这颗分隔符因此不能只在 T-SQL 上量一次。
        var plan = MySql.Orm().UpdateRange(rows(3)).Explain();

        Assert.Single(plan.Commands);
        Assert.Equal(2, Checks.Occurrences(plan.Commands[0].Text, ";"));
        Assert.Equal(3 * PerRow, plan.Commands[0].Parameters.Count);
        Checks.Has("@p23", plan.Commands[0].Text);

        // 那一家的写法：反引号标识符 + ANSI 的"表起别名再引用"（T-SQL 那一边是 UPDATE 别名 FROM 表 AS 别名）。
        // 这两格在族内是共用的、不在这里重判，这里只确认并档没有把它们改坏。
        Checks.Has("UPDATE `QuickSqlOrder` AS `t0`", plan.Commands[0].Text);
        Assert.Equal(3, Checks.Occurrences(plan.Commands[0].Text, "SET "));
    }

    // ── 整行更新哪几列不出 ────────────────────────────────────────────

    [Fact]
    public void The_whole_row_update_leaves_the_key_the_store_and_the_concurrency_columns_out_of_SET()
    {
        // InventoryItem：Id 自增主键、Sku 与 Quantity 是可写列、RowVersion 是库里给值的并发列、
        // CheckedAt 是可空并发列。三不出在这里一次判完（判据与 UpdateEngine.ColumnOf 逐字同源）。
        var items = new[]
        {
            new InventoryItem { Id = 1, Sku = "a", Quantity = 2, RowVersion = [1, 2, 3], CheckedAt = Moment },
            new InventoryItem { Id = 2, Sku = "b", Quantity = 3, RowVersion = [4, 5, 6], CheckedAt = null },
        };

        var plan = orm().UpdateRange(items).Explain();
        var text = plan.Commands[0].Text;

        Checks.Has("[Sku] = @p0", text);
        Checks.Has("[Quantity]", text);
        // 主键在这条命令里出现两次，而两次都是带别名限定的 WHERE 形状 ⇒ SET 里一颗都没有。
        // 不能写成 Absent("[Id] =")：WHERE 就长那样，只是它前面挂着 t0.，那句会把自己的定位条件判丢。
        Assert.Equal(2, Checks.Occurrences(text, "[Id]"));
        Assert.Equal(2, Checks.Occurrences(text, "[t0].[Id]"));
        Checks.Absent("[RowVersion]", text);
        Checks.Absent("[CheckedAt]", text);
        Checks.Has("[t0].[Id] = @p2", text);

        // 每行 2 颗 SET + 1 颗键 = 3 颗，两行 6 颗；不出 SET 的那三列也一处都不进参数表。
        Assert.Equal(6, plan.Commands[0].Parameters.Count);
        Assert.DoesNotContain("RowVersion", plan.Commands[0].Parameters.Select(p => p.Hint!.ColumnName));
        Assert.DoesNotContain("CheckedAt", plan.Commands[0].Parameters.Select(p => p.Hint!.ColumnName));

        // 并发列<em>哪儿都不去</em>：这一条链不做并发校验——那是 UpdateEngine（调用方点名改哪几列、
        // WHERE 带原值）与 SaveChanges（跟踪器的脏列判定）那一档。整行更新的契约就是"整行交出去"，
        // 所以它既不写并发列也不比并发列，这一格要钉死的是"少写一列"与"不比一列"是同一件事的两面。
        Checks.Has("SET 2 列 + WHERE 1 列", plan.Reason);
    }

    [Fact]
    public void A_composite_key_makes_a_conjunction_in_update_and_is_refused_in_delete()
    {
        var rows = new[]
        {
            new BulkLedger { Book = 1, Line = "a", Memo = "甲" },
            new BulkLedger { Book = 2, Line = "b", Memo = "乙" },
        };

        // 更新这一边：复合主键逐列进 WHERE，用 AND 合起来。这里没有可猜的东西——两列都给值了。
        var plan = orm().UpdateRange(rows).Explain();

        Assert.Equal(2, Checks.Occurrences(plan.Commands[0].Text, "UPDATE "));
        Assert.Equal(2, Checks.Occurrences(plan.Commands[0].Text, " AND "));
        Checks.Has("[t0].[Book] = @p1 AND [t0].[Line] = @p2", plan.Commands[0].Text);

        // 每行 1 颗载荷 + 2 颗键 = 3 颗；第二行第一颗是 @p3。
        Assert.Equal(6, plan.Commands[0].Parameters.Count);
        Assert.Equal("Memo", plan.Commands[0].Parameters[0].Hint!.ColumnName);
        Assert.Equal("Book", plan.Commands[0].Parameters[1].Hint!.ColumnName);

        // 删除那一边同一形状当场拒：IN 装不下元组，逐对 OR 展开又绕开参数预算的账。
        // 两条链对复合键的两个答案是分开的两格，所以两格都要有现场，否则"一家能写一家不能"看不出是设计还是漏。
        var refused = Checks.Raised<QuickSqlException>(() => orm().DeleteRange(rows));

        Checks.Has("IN 清单装不下元组", refused.Message);
        Checks.Has("SaveChanges", refused.Message);
    }

    // ── 四条拒收各指哪儿 ──────────────────────────────────────────────

    [Fact]
    public void A_split_entity_a_keyless_entity_and_a_writeless_entity_are_each_refused_with_directions()
    {
        // 分表：一批行会散在几张片上，而这一档今天没有按片分组的账。两条链都拒，各自指一条能走的路。
        var shards = new[] { new ShardLog { Id = Guid.NewGuid(), Level = 1, OccurredAt = Moment } };

        Checks.Has("UpdateRange 不接", Checks.Raised<QuickSqlException>(() => orm().UpdateRange(shards)).Message);
        Checks.Has("SplitTable(", Checks.Raised<QuickSqlException>(() => orm().DeleteRange(shards)).Message);

        // 无主键：WHERE 出不来。指的那两条路是"点主键"与"按条件走链式那条"。
        var keyless = new[] { new AuditRow { Actor = "谁", At = Moment } };

        var updateRefused = Checks.Raised<QuickSqlException>(() => orm().UpdateRange(keyless));

        Checks.Has("没有主键列", updateRefused.Message);
        Checks.Has("orm.Update<T>()", updateRefused.Message);

        Checks.Has("orm.Delete<T>()", Checks.Raised<QuickSqlException>(() => orm().DeleteRange(keyless)).Message);

        // 除了主键什么都没有的一张表：整行更新只剩 SET 空串。
        var writeless = new[] { new Ledger { Book = 1, Line = 2 } };

        Checks.Has("没有可写的列", Checks.Raised<QuickSqlException>(() => orm().UpdateRange(writeless)).Message);
    }

    [Fact]
    public void A_null_row_or_a_null_key_stops_the_build_inst_of_losing_that_row()
    {
        // null 实体：一条语句都发不出去，而不是把那一行省掉——省一行等于调用方以为写了。
        var withNullRow = new[] { row(1), null! };

        var rowRefused = Checks.Raised<QuickSqlException>(() => orm().UpdateRange(withNullRow).Explain());

        Checks.Has("第 1 行是 null", rowRefused.Message);
        Checks.Has("批量更新的集合里不能有 null 实体", rowRefused.Message);

        // null 主键：定位不到任何行。<c>WHERE [Token] = NULL</c> 在库里是 UNKNOWN，
        // 照那个形状发出去就是"执行成功而一行没改"，所以它在建文本那一刻响。
        var withNullKey = new[] { new BulkToken { Token = "a", Note = "甲" }, new BulkToken { Note = "乙" } };

        var keyRefused = Checks.Raised<QuickSqlException>(() => orm().UpdateRange(withNullKey).Explain());

        Checks.Has("第 1 行的主键列 Token 是 null", keyRefused.Message);
        Checks.Has("InsertRange", keyRefused.Message);

        // 删除那一边同一条判据，两处各说各的话（一条是"定位不到"，一条是"那一行没删而调用方以为删了"）。
        var deleteNullKey = Checks.Raised<QuickSqlException>(() => orm().DeleteRange(withNullKey).Explain());

        Checks.Has("第 1 行的主键列 Token 是 null", deleteNullKey.Message);
        Checks.Has("按主键删定位不到任何行", deleteNullKey.Message);

        // 同一形状在两条链上都要判：判档不看行，看行的是建文本那一步，所以拒必须落在 Explain() 上。
        var deleteNullRow = Checks.Raised<QuickSqlException>(() => orm().DeleteRange<BulkToken>([null!]).Explain());

        Checks.Has("第 0 行是 null", deleteNullRow.Message);
        Checks.Has("批量删除的集合里不能有 null 实体", deleteNullRow.Message);
    }

    // ── 一句 upsert 档（§12.1 写侧扩展的第二条裁决）──────────────────

    [Theory]
    [MemberData(nameof(UpsertFamilies))]
    public void Naming_upsert_lands_the_tier_exactly_where_that_family_wrote_a_clause(string family, bool writes, bool merged)
    {
        // 判档看的是那颗槽交不交得出东西，而"东西"是 provider 写的文本，不是内核猜的：
        // 接了的 9 家（MySQL 血统三家共用 MySqlBulk、PG 血统六家共用 PostgreSqlBulk）拿到 Upsert 档，
        // 其余 7 家吃接口上那句默认 null 并按 fallback 三档办。手写表的形状由
        // The_merged_tier_is_declared_by_exactly_four_families_and_by_those_four_only 那颗钉子管。
        var chain = OrmFor(family).UpdateRange(rows(3)).WithBulk(BulkWriteMethod.Upsert);

        var report = chain.ExplainBulk();
        var plan = chain.Explain();

        Assert.Equal(BulkWriteMethod.Upsert, report.Requested);

        if (writes)
        {
            Assert.Equal(BulkWriteMethod.Upsert, report.Method);
            Assert.False(report.Degraded);
            Assert.Single(plan.Commands);
            Assert.Equal(1, report.RoundTrips);

            // 块宽与并档同一把尺（每行 8 颗：可写 7 + 键 1），因为一句多值 INSERT 的参数账就是列数 × 行数。
            Assert.Equal(report.ChunkSize, plan.RowsPerCommand);
            Assert.Equal(family is "mysql" or "oceanbase" or "tidb" ? 7372 : 3686, report.ChunkSize);

            // 一句就是一句：这一档的产出里没有分号批那颗分隔符。
            Checks.Absent(" ; ", plan.Commands[0].Text);
            Checks.Has("一条命令 = 一句 = 一次往返", report.Reason);
            Checks.Has("库里没有的那一行会插进去", report.Reason);

            // 一句命令就是一句语句，所以提交那一格在这里说的是"不必再包"——与并档那句"整批 N 句"正对面。
            Checks.Has("一条语句本身就原子", plan.Reason);
        }
        else
        {
            // 退下来落的那一档就是那一家 Auto 取的那一档：数字由它自己的阶梯算，这里只把降级与语义差说出口。
            Assert.Equal(merged ? BulkWriteMethod.Merged : BulkWriteMethod.SingleRow, report.Method);
            Assert.True(report.Degraded);
            Checks.Has("没接一句 upsert 的句尾", report.Reason);
            Checks.Has("库里没有的那一行不会再插进去", report.Reason);

            // 退下来的那一档的产出里<em>没有 INSERT</em>：降级不是换一种 upsert 写法，而是换回"只改不改插"，
            // 那一句语义上的退换在文本上量得到，不只是 Reason 里的一句话。
            Checks.Absent("INSERT INTO", plan.Commands[0].Text);

            var refused = Checks.Raised<QuickSqlException>(
                () => OrmFor(family).UpdateRange(rows(3)).WithBulk(BulkWriteMethod.Upsert, FallbackPolicy.Never).ExplainBulk());

            Checks.Has("UpsertClause", refused.Message);
            Checks.Has("FallbackPolicy.Never 封死", refused.Message);
        }

        // Auto 永不取这一档，接了句尾的那一家也要判：语义差不是速度差，判据在这里是同一颗。
        var auto = OrmFor(family).UpdateRange(rows(3)).ExplainBulk();

        Assert.NotEqual(BulkWriteMethod.Upsert, auto.Method);
        Assert.False(auto.Degraded);
    }

    [Fact]
    public void The_MySQL_tail_is_the_row_alias_shape_and_not_the_deprecated_VALUES_one()
    {
        var plan = MySql.Orm().UpdateRange(rows(2)).WithBulk(BulkWriteMethod.Upsert).Explain();
        var text = plan.Commands[0].Text;

        // VALUES 那一半是内核拼的，与 InsertRange 共用一条出路：列清单 = 可写 7 列 ++ 主键 1 列。
        Checks.Has("INSERT INTO `QuickSqlOrder` (`CustomerName`, `CustomerId`, `Amount`, `State`, `CreatedAt`, `PaidAt`, `Note`, `Id`) VALUES (", text);

        // 两行 = 两个括号组（内核按 ", " 连接行），一行一组才叫"一句多值"。
        Assert.Equal(2, Checks.Occurrences(text, "(@p"));

        // 行别名那一形（8.0.19+）而不是 VALUES(col)：后者在 8.4.8 上每句带两条 Warning 1287，
        // 探针把两种都量过，本仓库选没有警告的那一种【实测·obj/logs/v40-upsert-shapes2.log】。
        Checks.Has("AS `fs`(", text);
        Checks.Has(") ON DUPLICATE KEY UPDATE ", text);
        Checks.Has("`CustomerName` = `fs`.`CustomerName`", text);
        Checks.Has("`Note` = `fs`.`Note`", text);
        Checks.Absent("= VALUES(", text);

        // 主键进别名清单（它必须是 VALUES 列清单的镜像）而不进 SET：改自己的键不是整行更新。
        Assert.Equal(2, Checks.Occurrences(text, "`Id`"));
        Checks.Absent("`Id` = `fs`", text);

        // 别名本身也过方言的取号口——反引号不是 provider 手拼的。
        Checks.Has("AS `fs`", text);

        // 两行 × 8 颗 = 16 颗，一张参数表；编号从 @p0 连到 @p15。
        Assert.Equal(16, plan.Commands[0].Parameters.Count);
        Assert.Equal("CustomerName", plan.Commands[0].Parameters[0].Hint!.ColumnName);
        Assert.Equal("Id", plan.Commands[0].Parameters[7].Hint!.ColumnName);
        Assert.Equal("Id", plan.Commands[0].Parameters[^1].Hint!.ColumnName);
    }

    [Fact]
    public void The_PG_tail_names_the_conflict_target_and_reads_EXCLUDED()
    {
        var plan = PostgreSql.Orm().UpdateRange(rows(2)).WithBulk(BulkWriteMethod.Upsert).Explain();
        var text = plan.Commands[0].Text;

        Checks.Has("INSERT INTO \"QuickSqlOrder\" (\"CustomerName\"", text);
        Checks.Has("ON CONFLICT (\"Id\") DO UPDATE SET ", text);
        Checks.Has("\"CustomerName\" = EXCLUDED.\"CustomerName\"", text);

        // 与 MySQL 那一边同一条判据的另一种写法：键进冲突目标而不进 SET。
        Assert.Equal(2, Checks.Occurrences(text, "\"Id\""));
        Checks.Absent("\"Id\" =", text);

        // 那一边的行别名在这里一处都没有：PG 用的是 EXCLUDED 那一颗伪表，没有别名可起。
        Checks.Absent(" AS ", text);
        Assert.Equal(16, plan.Commands[0].Parameters.Count);
    }

    [Fact]
    public void A_composite_key_fits_the_upsert_clause_where_IN_does_not_fit_a_tuple()
    {
        // 同一份复合键：DeleteRange 当场拒（IN 装不下元组），而这一档两家的句尾都装得下——
        // 那一格的区别是<em>文本形状</em>不是键的形状，两条链各答各的。
        var rows = new[]
        {
            new BulkLedger { Book = 1, Line = "a", Memo = "甲" },
            new BulkLedger { Book = 2, Line = "b", Memo = "乙" },
        };

        var pg = PostgreSql.Orm().UpdateRange(rows).WithBulk(BulkWriteMethod.Upsert).Explain();

        Checks.Has("ON CONFLICT (\"Book\", \"Line\") DO UPDATE SET \"Memo\" = EXCLUDED.\"Memo\"", pg.Commands[0].Text);

        // 别名清单按 VALUES 的列序（可写 ++ 键）而不是按两份清单各拼一遍——错一位就是把值写进别人的列。
        var mysql = MySql.Orm().UpdateRange(rows).WithBulk(BulkWriteMethod.Upsert).Explain();

        Checks.Has("AS `fs`(`Memo`, `Book`, `Line`) ON DUPLICATE KEY UPDATE `Memo` = `fs`.`Memo`", mysql.Commands[0].Text);

        // 每行 1 颗载荷 + 2 颗键 = 3 颗，两行 6 颗。
        Assert.Equal(6, mysql.Commands[0].Parameters.Count);
        Assert.Equal("Memo", mysql.Commands[0].Parameters[0].Hint!.ColumnName);
        Assert.Equal("Line", mysql.Commands[0].Parameters[2].Hint!.ColumnName);

        Checks.Has("IN 清单装不下元组", Checks.Raised<QuickSqlException>(() => PostgreSql.Orm().DeleteRange(rows)).Message);
    }

    [Fact]
    public void An_upsert_batch_over_the_budget_still_splits_into_one_statement_commands()
    {
        // 掰小到 40 ⇒ 每行 8 颗 ⇒ 每条 4 行。5 行 = 两条命令，而<em>每条命令仍是一句</em>：
        // 切块切的是递交次数，不是把一个形状改成另一种。
        var plan = ormAt("mysql", 40).UpdateRange(rows(5)).WithBulk(BulkWriteMethod.Upsert).Explain();

        Assert.Equal(2, plan.Commands.Count);
        Assert.Equal(4, plan.RowsPerCommand);
        Assert.Equal(32, plan.Commands[0].Parameters.Count);
        Assert.Equal(8, plan.Commands[1].Parameters.Count);
        Assert.Equal(1, Checks.Occurrences(plan.Commands[0].Text, "ON DUPLICATE KEY UPDATE"));
        Assert.Equal(1, Checks.Occurrences(plan.Commands[1].Text, "ON DUPLICATE KEY UPDATE"));

        // 编号各从 @p0 起重：两条命令单独拿出来都能贴进查询器复现。
        Assert.Equal("@p0", plan.Commands[1].Parameters[0].Name);

        // 两句 ⇒ 这一档的"语句数 = 命令数"在这里显形：包事务的话包的是这两句，一提交盖住两条命令。
        Checks.Has("整批 2 句一次提交", plan.Reason);
        Assert.Equal(2, ormAt("mysql", 40).UpdateRange(rows(5)).WithBulk(BulkWriteMethod.Upsert).ExplainBulk().RoundTrips);

        // 两句没有单一文本可交，与并档那一条同判据。
        Checks.Has("没有单一 SQL 文本", Checks.Raised<QuickSqlException>(() => plan.ToSql()).Message);
    }

    [Fact]
    public void A_store_given_key_left_at_its_default_is_refused_while_a_handed_key_is_not()
    {
        // Order 的 Id 是整型主键 ⇒ 自增 ⇒ 库里给值。交来 0 的那一种在 Merged 档是一句合法的
        // WHERE [Id] = 0，在这一档却会插进一个新行——那一格的区别就是这一条拒收存在的全部理由。
        var unassigned = new Order { Id = 0, CustomerName = "客户", CustomerId = 1, Amount = 2m, State = OrderState.Paid, CreatedAt = Moment, Note = "甲" };

        var refused = Checks.Raised<QuickSqlException>(
            () => MySql.Orm().UpdateRange([unassigned]).WithBulk(BulkWriteMethod.Upsert).Explain());

        Checks.Has("由库给值", refused.Message);
        Checks.Has("写进去的会是一个新行", refused.Message);
        Checks.Has("InsertRange", refused.Message);

        // 同一颗 0 在 Merged 档照发：那道门是档位级的，不是键级的一刀切。
        Assert.Single(MySql.Orm().UpdateRange([unassigned]).Explain().Commands);

        // 把键给实就通过——这一句挡的是"拒得太宽"那一种错法。
        var handed = new Order { Id = 7, CustomerName = "客户", CustomerId = 1, Amount = 2m, State = OrderState.Paid, CreatedAt = Moment, Note = "甲" };

        Checks.Has("VALUES (@p0", MySql.Orm().UpdateRange([handed]).WithBulk(BulkWriteMethod.Upsert).Explain().Commands[0].Text);

        // 非自增主键的"零"是一颗合法键：BulkToken 的 Token 是字符串主键、不是库里给值的那一列，
        // 空串在这种现场就是调用方要定位的那一颗键，不许被上面那道门顺手套住。
        var emptyKey = new BulkToken { Token = "", Note = "甲" };

        Assert.Single(MySql.Orm().UpdateRange([emptyKey]).WithBulk(BulkWriteMethod.Upsert).Explain().Commands);

        // 而 null 键在两档上同一条拒（消息与并档逐字相同）。
        var nullKey = new BulkToken { Token = null, Note = "甲" };

        Checks.Has("null 键定位不到任何行",
            Checks.Raised<QuickSqlException>(() => MySql.Orm().UpdateRange([nullKey]).WithBulk(BulkWriteMethod.Upsert).Explain()).Message);

        // 实体本身是 null 也同一条：一条都不发，而不是把那一行省掉。
        Checks.Has("第 1 行是 null",
            Checks.Raised<QuickSqlException>(() => MySql.Orm().UpdateRange([handed, null!]).WithBulk(BulkWriteMethod.Upsert).Explain()).Message);
    }

    [Fact]
    public void A_global_filter_refuses_the_upsert_tier_because_that_sentence_has_no_where_to_fold_into()
    {
        // 与上面那条并档的对照用例同一颗靶子（Ticket ∶ 软删 + 租户），这里判的是反方向：
        // 过滤器在 Merged 档叠得进 WHERE，在 upsert 那一句里无处安放——放过去就是改到过滤器之外的行。
        // 靶子挂在 MySQL 那一家：只有接了句尾的那一家才走得到这道门。
        var target = MySql.Orm();

        target.Filters.Add<Ticket>(t => !t.Deleted);

        var refused = Checks.Raised<QuickSqlException>(
            () => target.UpdateRange([ticket(1), ticket(2)]).WithBulk(BulkWriteMethod.Upsert).Explain());

        Checks.Has("全局过滤器", refused.Message);
        Checks.Has("没有 WHERE 可以叠过滤器", refused.Message);

        // 判档那颗纯出口不读过滤器：拒在建文本那一步，所以 ExplainBulk 仍只答档位。
        Assert.Equal(BulkWriteMethod.Upsert, target.UpdateRange([ticket(1)]).WithBulk(BulkWriteMethod.Upsert).ExplainBulk().Method);

        // 同一颗过滤器在 Merged 档照旧叠得进去（那条形状由 A_global_filter_folds_into_both_chains 钉），
        // 而那一格要用 SQL Server 量：它的过滤器文本本册只在那一条里过一遍，两处共用一种写法才比得出"少了一处"。
        var sqlserver = orm();

        sqlserver.Filters.Add<Ticket>(t => !t.Deleted);

        Checks.Has("NOT [t0].[Deleted] = 1", sqlserver.UpdateRange([ticket(1)]).Explain().Commands[0].Text);

        // 没接句尾的那一家先降级，于是这道门都走不到：它响的是退档而不是过滤器。
        Assert.Equal(BulkWriteMethod.Merged, sqlserver.UpdateRange([ticket(1)]).WithBulk(BulkWriteMethod.Upsert).ExplainBulk().Method);
    }

    [Fact]
    public void DeleteRange_named_Upsert_is_refused_and_says_which_tier_to_take()
    {
        // 那一句的"缺键就插"那一支写进去的正是被叫删的那一行——两半在一条链上互相否定，
        // 所以这里不降级、不猜，当场拒并指两条能走的路。
        var refused = Checks.Raised<QuickSqlException>(
            () => orm().DeleteRange(rows(3)).WithBulk(BulkWriteMethod.Upsert).ExplainBulk());

        Checks.Has("DeleteRange 不接 Upsert 档", refused.Message);
        Checks.Has("一句 IN 清单", refused.Message);

        // 同一条链在两种请求下都照常走（拒的只有点名这一档那一颗）。
        Assert.Equal(BulkWriteMethod.Merged, orm().DeleteRange(rows(3)).ExplainBulk().Method);
    }

    // ── DeleteRange 的 IN 档 ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(WriteFamilies))]
    public void The_IN_tier_is_reachable_on_every_family_because_it_asks_no_multi_statement_capability(
        string family, int budget, bool merged)
    {
        // 这一条链的 Merged 是一<em>条</em>语句（WHERE 键 IN (…))，不是分号批，所以那颗
        // SupportsMergedStatements 位不参与判档：十六家全部拿 Merged、全部不降级。
        // 与上一条（Auto 在没有那颗位的十二家落逐条）恰成对照——同一个词在两条链上是两种判据。
        // merged 那颗在这里只用来把"两家拿同一颗数而档位不同"说出来：UpdateRange 上 false 的家，
        // DeleteRange 上照样是 Merged。
        var report = OrmFor(family).DeleteRange(rows(3)).ExplainBulk();

        Assert.Equal(BulkWriteMethod.Merged, report.Method);
        Assert.False(report.Degraded);
        Assert.Equal(1, report.RoundTrips);
        Assert.Equal(budget * 9 / 10, report.ChunkSize);
        Checks.Has("一句 IN 清单是单条语句", report.Reason);
        Checks.Has($"参数预算 {budget} → 每条 {budget * 9 / 10} 颗", report.Reason);
        Assert.Equal(merged ? 1 : 3, OrmFor(family).UpdateRange(rows(3)).ExplainBulk().RoundTrips);
    }

    [Fact]
    public void The_IN_list_on_the_primary_family_is_one_statement_with_one_parameter_per_row()
    {
        var plan = orm().DeleteRange(rows(3)).Explain();

        Assert.Single(plan.Commands);
        Assert.Equal(3, plan.Commands[0].Parameters.Count);
        Checks.Has("[t0].[Id] IN (@p0, @p1, @p2)", plan.Commands[0].Text);
        Assert.Equal(1, Checks.Occurrences(plan.Commands[0].Text, "DELETE "));

        // 逐条那一档在这条链上照样可达（点名就有，与判档无关）：3 行 = 3 条一句一删的命令。
        var single = orm().DeleteRange(rows(3)).WithBulk(BulkWriteMethod.SingleRow).Explain();

        Assert.Equal(3, single.Commands.Count);
        Assert.Single(single.Commands[0].Parameters);
        Checks.Has("[t0].[Id] = @p0", single.Commands[0].Text);
    }

    [Fact]
    public void An_IN_list_over_the_budget_splits_into_commands_and_Stops_being_one_text()
    {
        // SQLite 的兜底预算给这条链算出的块宽是 90 颗 ⇒ 91 行 = 两条命令（90 + 1）。
        // 这里要的是"块宽由预算算、命令数由行数算"这件事在写侧扩展上也成立，且两份产出各说各的读者。
        var many = rows(91);
        var chain = Sqlite.Orm().DeleteRange(many);

        var report = chain.ExplainBulk();
        var plan = chain.Explain();

        Assert.Equal(90, report.ChunkSize);
        Assert.Equal(2, report.RoundTrips);
        Assert.Equal(2, plan.Commands.Count);
        Assert.Equal(90, plan.Commands[0].Parameters.Count);
        Assert.Single(plan.Commands[1].Parameters);
        Checks.Has("2 条 IN 语句", plan.Reason);
        // 这条链上的语句数就是命令数，所以两万颗键切成的那两句会被一批事务盖住（改判的同上一条注释）。
        Checks.Has("整批 2 句一次提交", plan.Reason);

        // 一条命令仍是一条语句：切块切的是"几次递交"，不是"一句里的条数"。
        Assert.Equal(1, Checks.Occurrences(plan.Commands[0].Text, "DELETE "));

        Checks.Has("没有单一 SQL 文本", Checks.Raised<QuickSqlException>(() => plan.ToSql()).Message);
    }

    // ── 全局过滤器（§10.7）落在这两条链上 ─────────────────────────────

    [Fact]
    public void A_global_filter_folds_into_both_chains_and_says_so_in_the_reason()
    {
        // §10.7 约束 1 剩下的那一半今天有现场了：批量写链的 WHERE 也叠过滤器。
        // 靶子是 Ticket（一张既有 Deleted 又带租户列的表），与 FilterTests 同一分工。
        var target = orm();

        target.Filters.Add<Ticket>(t => !t.Deleted);

        var update = target.UpdateRange([ticket(1), ticket(2)]).Explain();

        Checks.Has("NOT [t0].[Deleted] = 1", update.Commands[0].Text);
        Checks.Has("[t0].[Id] = @p4", update.Commands[0].Text);

        // 键 1 颗 + 载荷 4 颗（Title/TenantId/Deleted/DeletedAt）= 每行 5 颗；过滤器本身不吃参数。
        Assert.Equal(10, update.Commands[0].Parameters.Count);
        Checks.Has("除了主键还叠着 1 条全局过滤器", update.Reason);

        var delete = target.DeleteRange([ticket(1), ticket(2)]).Explain();

        Checks.Has("[t0].[Id] IN (@p0, @p1)", delete.Commands[0].Text);
        Checks.Has("NOT [t0].[Deleted] = 1", delete.Commands[0].Text);

        // 命中范围这一句必须有：删的调用方以为自己按键删了两行，而过滤器可能让其中一行根本不删。
        Checks.Has("命中范围比键清单窄", delete.Reason);
    }

    // ── 调用方在主键之外钉的条件（IUpdateBulk.Where，乐观锁那一格） ─────

    [Fact]
    public void A_caller_condition_folds_after_the_keys_and_before_the_filters()
    {
        // 这一格存在的理由是按主键整行改的那一类调用方也要能钉条件（并发戳、"还是我读到的那一行时才改它"）。
        // 判据是<em>叠的位置</em>：键 → 调用方条件 → 全局过滤器，与读侧多次 Where 同一条追加方向。
        var target = orm();
        var title = "工单1";

        target.Filters.Add<Ticket>(t => !t.Deleted);

        var plan = target.UpdateRange([ticket(1), ticket(2)]).Where(t => t.Title == title).Explain();
        var text = plan.Commands[0].Text;

        var key = text.IndexOf("[t0].[Id] = ", StringComparison.Ordinal);
        var condition = text.IndexOf("[t0].[Title] = ", StringComparison.Ordinal);
        var filter = text.IndexOf("NOT [t0].[Deleted] = 1", StringComparison.Ordinal);

        Assert.True(key >= 0, $"主键条件不在产出里：{text}");
        Assert.True(condition > key, $"调用方条件排在键之前：{text}");
        Assert.True(filter > condition, $"过滤器排在调用方条件之后：{text}");

        // SET 里的 Title 与 WHERE 里那颗条件参数是两笔：一行 5 颗键+载荷再 +1 颗条件，两行 12 颗。
        Assert.Equal(12, plan.Commands[0].Parameters.Count);
        Checks.Has("另有调用方点名的 1 条条件", plan.Reason);

        // 决策段那颗纯出口读不到受影响行数：那一颗只有执行段才有值。
        Assert.Equal(0, target.UpdateRange([ticket(1)]).Where(t => t.Title == title).ExplainBulk().AffectedRows);
    }

    [Fact]
    public void Several_caller_conditions_append_in_written_order_while_the_plain_chain_keeps_its_shape()
    {
        var target = orm();
        var title = "工单1";
        var tenant = 3;

        var plain = target.UpdateRange([ticket(1), ticket(2)]).Explain().Commands[0].Text;

        Assert.DoesNotContain("调用方点名", target.UpdateRange([ticket(1), ticket(2)]).Explain().Reason);

        var two = target.UpdateRange([ticket(1), ticket(2)])
                        .Where(t => t.Title == title)
                        .Where(t => t.TenantId == tenant)
                        .Explain();

        Assert.True(
            two.Commands[0].Text.IndexOf("[t0].[Title] = ", StringComparison.Ordinal)
            < two.Commands[0].Text.IndexOf("[t0].[TenantId] = ", StringComparison.Ordinal),
            $"先写的条件排在后面：{two.Commands[0].Text}");

        Checks.Has("另有调用方点名的 2 条条件", two.Reason);

        // 没点条件的那一条链仍然只交 10 颗参数（每行 4 颗载荷 + 1 颗键）：入口零税那句在这一颗上的兑现面。
        Assert.Equal(10, target.UpdateRange([ticket(1), ticket(2)]).Explain().Commands[0].Parameters.Count);
        Assert.Equal(14, two.Commands[0].Parameters.Count);
    }

    [Fact]
    public void The_upsert_tier_refuses_a_caller_condition_because_that_sentence_has_no_where_to_fold_into()
    {
        // 与 A_global_filter_refuses_the_upsert_tier… 同一道门、同一个理由：那一句没有 WHERE。
        // 靶子仍然挂在 MySQL 那一家（只有接了句尾的那一家走得到这道门）。
        var target = MySql.Orm();
        var tenant = 3;

        var refused = Checks.Raised<QuickSqlException>(
            () => target.UpdateRange([ticket(1), ticket(2)]).Where(t => t.TenantId == tenant)
                   .WithBulk(BulkWriteMethod.Upsert).Explain());

        Checks.Has("一句 upsert 档不接", refused.Message);
        Checks.Has("只认主键", refused.Message);

        // 判档那颗纯出口不读条件：拒在建文本那一步，口径与过滤器那一条逐字同族。
        Assert.Equal(BulkWriteMethod.Upsert,
            target.UpdateRange([ticket(1)]).Where(t => t.TenantId == tenant).WithBulk(BulkWriteMethod.Upsert).ExplainBulk().Method);
    }

    // ── 钩子回放与执行口 ──────────────────────────────────────────────

    [Fact]
    public void The_rewrite_hook_fires_once_per_command_and_the_binding_hook_once_per_parameter()
    {
        // 两个钩子的粒度不同（整句 vs 单颗参数），而切块改变的是前者的次数：
        // 5 行并成 2 条命令 ⇒ 改写响 2 次，参数绑定仍然响 40 次。
        var cud = 0;
        var binding = 0;
        var aop = new QuickSqlAop();

        aop.CudBuilding = e =>
        {
            cud++;
            e.Rewritten = e.Sql with { Text = e.Sql.Text + " /*w*/" };
        };

        aop.ParameterBinding = _ => binding++;

        var plan = ormWith(aop, 40).UpdateRange(rows(5)).Explain();

        Assert.Equal(2, cud);
        Assert.Equal(40, binding);
        Assert.Equal(2, plan.HookReplay.Count);
        Assert.Equal("0,1", string.Join(",", plan.HookReplay.Select(t => t.Step)));
        Assert.Equal("/*w*/", plan.Commands[^1].Text[^5..]);

        // 删那条链同一条纪律：一次改写、每颗参数一次绑定。
        cud = 0;
        binding = 0;

        var deleted = ormWith(aop).DeleteRange(rows(3)).Explain();

        Assert.Equal(1, cud);
        Assert.Equal(3, binding);
        Assert.Single(deleted.HookReplay);
    }

    [Fact]
    public void The_upsert_tier_asks_the_value_hook_for_the_writable_cells_only()
    {
        var fired = new List<string>();
        var aop = new QuickSqlAop();

        aop.AuditValue = e =>
        {
            fired.Add(e.Column.MemberName);

            if (e.Column.MemberName == "Note")
                e.SetValue("盖过");
        };

        // 一句 upsert 的写入清单 = 可写 7 列 + 主键 1 列（冲突判据进 VALUES 那一颗字面行），
        // 而钩子只问前一半：键位过了钩子，等于让"补一个备注"这种订阅方改掉冲突判定。
        // 档位不是种类（BuildUpsert 抬头那一句，CudBuilding 按 "update" 记），值钩子同一条口径。
        var plan = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = MySql.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        }).UpdateRange(rows(2)).WithBulk(BulkWriteMethod.Upsert, FallbackPolicy.Never).Explain();

        Assert.Equal(2 * 7, fired.Count);
        Assert.DoesNotContain("Id", fired);
        Assert.Equal(2, plan.Commands[0].Parameters.Count(p => Equals(p.Value, "盖过")));

        // 回放里那两笔排在发射段之前，与并档同一个次序（步骤号连续是硬规则 1 的另一半）。
        Assert.Equal(["AuditValue", "AuditValue"], plan.HookReplay.Take(2).Select(t => t.HookName).ToArray());
        Assert.Equal([0, 1], plan.HookReplay.Take(2).Select(t => t.Step).ToArray());
    }

    [Fact]
    public async Task The_execute_exit_pays_the_driver_probe_on_both_chains()
    {
        // 有行要写：离线册必然撞桩工厂的探针——这一句响本身就是"没有代码偷偷连库"的证据（§3）。
        var touched = await Assert.ThrowsAsync<DriverNotAvailableException>(
            () => orm().UpdateRange(rows(3)).ExecuteBulkAsync(TestContext.Current.CancellationToken));

        Checks.Has("只生成 SQL 的测试不该碰驱动", touched.Message);

        await Assert.ThrowsAsync<DriverNotAvailableException>(
            () => orm().DeleteRange(rows(3)).ExecuteBulkAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_zero_row_batch_makes_no_command_and_pays_no_connection()
    {
        // 0 行：一条命令都不产生，于是执行直接返回决策，既不建文本也不取连接。
        // 这一格的价值全在"它绿而没有探针响"——桩工厂没被碰着就是空批没走连接的那份证据。
        var empty = orm().UpdateRange<Order>([]);

        Assert.Equal(0, (await empty.ExecuteBulkAsync(TestContext.Current.CancellationToken)).RowCount);
        Assert.Empty(empty.Explain().Commands);
        Checks.Has("0 行：一档都不必选", empty.ExplainBulk().Reason);
        Checks.Has("-- 0 行：不产生任何命令", empty.Explain().ToLogSql());

        // 没有单一文本可交：那一格报的是"0 行没有对应的 SQL 文本"，而不是"分成几条命令"那句。
        Checks.Has("0 行批量写没有对应的 SQL 文本", Checks.Raised<QuickSqlException>(() => empty.Explain().ToSql()).Message);

        // 删那一条链的 0 行同一形状。
        Assert.Equal(0, (await orm().DeleteRange<Order>([]).ExecuteBulkAsync(TestContext.Current.CancellationToken)).RowCount);
    }

    [Fact]
    public void A_degradation_on_these_chains_leaves_no_AOP_trace_yet()
    {
        // 钉的是<em>记录过的缺口</em>，不是漏网：IUpdateBulk 抬头写着"本档的 BulkDegraded 接线
        // 等事件形状改成两种档位通用那天"，而插入那一族的 BulkDegradedEvent 带的是 BulkMethod，
        // 装不下这一族的档位枚举。这一格在那天会变红，而那正是它该有的行为。
        var seen = new List<BulkDegradedEvent>();
        var aop = new QuickSqlAop();

        aop.BulkDegraded = seen.Add;

        var honored = ormWith(aop).UpdateRange(rows(3)).WithBulk(BulkWriteMethod.Merged, FallbackPolicy.DegradeAndLog);

        Assert.False(honored.ExplainBulk().Degraded);

        // 换成没表态的那一家才真降级——痕仍然不派发，而报告里 Degraded 是真的。
        var degraded = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Kingbase.Provider(SqlServer.NoDriverFactory.Instance),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop,
        }).UpdateRange(rows(3)).WithBulk(BulkWriteMethod.Merged);

        Assert.True(degraded.ExplainBulk().Degraded);
        Assert.Empty(seen);
    }

    [Fact]
    public void The_layer_report_and_the_command_texts_never_disagree_on_the_numbers()
    {
        // 与 BulkLayerTests 那条同名的一格：同一个链对象先答档位、再交文本，两份产出必须出自同一次判定，
        // 于是"离线看到的档"与"执行走的那一档"之间没有第二套算术可漂移。
        // 两条链 × 三种表态（true / false / 整颗没报）× 三种请求各量一遍。
        foreach (var variant in new[]
                 {
                     (Orm: orm(), Chain: "update", Requested: BulkWriteMethod.Auto, Tier: BulkWriteMethod.Merged, Rows: 5, Commands: 1, Chunk: MergedWidth),
                     (Orm: ormAt(40), Chain: "update", Requested: BulkWriteMethod.Merged, Tier: BulkWriteMethod.Merged, Rows: 5, Commands: 2, Chunk: 4),
                     (Orm: orm(), Chain: "update", Requested: BulkWriteMethod.SingleRow, Tier: BulkWriteMethod.SingleRow, Rows: 5, Commands: 5, Chunk: 1),
                     (Orm: Kingbase.Orm(), Chain: "update", Requested: BulkWriteMethod.Auto, Tier: BulkWriteMethod.SingleRow, Rows: 5, Commands: 5, Chunk: 1),
                     (Orm: Sqlite.Orm(), Chain: "update", Requested: BulkWriteMethod.Merged, Tier: BulkWriteMethod.SingleRow, Rows: 5, Commands: 5, Chunk: 1),
                     (Orm: orm(), Chain: "delete", Requested: BulkWriteMethod.Auto, Tier: BulkWriteMethod.Merged, Rows: 5, Commands: 1, Chunk: DeleteWidth),
                     (Orm: orm(), Chain: "delete", Requested: BulkWriteMethod.SingleRow, Tier: BulkWriteMethod.SingleRow, Rows: 5, Commands: 5, Chunk: 1),
                     (Orm: Sqlite.Orm(), Chain: "delete", Requested: BulkWriteMethod.Merged, Tier: BulkWriteMethod.Merged, Rows: 91, Commands: 2, Chunk: 90),
                 })
        {
            BulkWriteReport report;
            BulkWritePlan plan;

            // 两份产出取自<em>同一个链对象</em>（与 BulkLayerTests 那条同形）：先问档、再要文本，
            // 中间不重开一条链，于是漂移的可能少一处。
            if (variant.Chain == "update")
            {
                var update = variant.Orm.UpdateRange(rows(variant.Rows)).WithBulk(variant.Requested);

                (report, plan) = (update.ExplainBulk(), update.Explain());
            }
            else
            {
                var delete = variant.Orm.DeleteRange(rows(variant.Rows)).WithBulk(variant.Requested);

                (report, plan) = (delete.ExplainBulk(), delete.Explain());
            }

            Assert.Equal(variant.Tier, report.Method);
            Assert.Equal(variant.Requested, report.Requested);
            Assert.Equal(variant.Commands, report.RoundTrips);
            Assert.Equal(variant.Chunk, report.ChunkSize);
            Assert.Equal(variant.Rows, report.RowCount);

            // 最后两条是那一格的本体：报告里的两个数就是产出里的两个数，不是各算一遍算成了同一个。
            Assert.Equal(plan.Commands.Count, report.RoundTrips);
            Assert.Equal(plan.RowsPerCommand, report.ChunkSize);
        }
    }
}

/// <summary>复合主键 + 一颗载荷：<c>UpdateRange</c> 的合取现场。
/// 第二颗键故意是<em>可空字符串</em>——它同时是"null 键当场抛"那一格的现场（<see cref="BulkToken"/> 管单键那一边）。
/// 放在本文件而不是 Common：Common 挂了源生成器，新实体要能只靠反射路径产出才有用。</summary>
[Table("QuickSqlBulkLedger")]
public sealed class BulkLedger
{
    [Da.Key]
    public int Book { get; set; }

    [Da.Key]
    [QuickColumn(Length = 16)]
    public string? Line { get; set; }

    [QuickColumn(Length = 32)]
    public string? Memo { get; set; }
}

/// <summary>单列<em>可空</em>主键：两条链的"null 键"现场都靠它。
/// 键型选 <c>string</c> 而不是 <c>int?</c>：后者会被「整型主键 ⇒ 自增」那条例定约定接手，
/// 于是这一格测的就不是"一个可空主键"而是"一个自增主键怎么不发 SET"——那是 <see cref="InventoryItem"/> 已经钉过的一格。</summary>
[Table("QuickSqlBulkToken")]
public sealed class BulkToken
{
    [Da.Key]
    [QuickColumn(Length = 32)]
    public string? Token { get; set; }

    [QuickColumn(Length = 32)]
    public string? Note { get; set; }
}
