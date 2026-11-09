using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary><c>SaveChanges</c> 的档内类型序按外键依赖排（§8.2、v0.20），判据在<b>真库</b>上。
///
/// <para><b>为什么这一格只能真跑</b>：报告的 <c>Operations</c> 里只有表名，而"表名的先后"离线也能钉 ——
/// 但离线钉不住<em>那个先后是不是必要的</em>。这里的裁判是库自己加的那条 <c>FOREIGN KEY</c> 约束
/// （<see cref="Database"/> 的 <c>EnsureProjectForeignKey</c>，CodeFirst 今天不发约束文本，原因记在 §14 未完项表）：
/// 顺序错了语句就<em>发不出去</em>，而不是"报告读起来顺序不太对"。最后那条负样本判的就是这个裁判本身在不在位 ——
/// 约束不在位时，前面三条绿得毫无意义。</para>
///
/// <para><b>靶子为什么是 <see cref="Project"/>/<see cref="Milestone"/> 而不是 <see cref="Order"/>/<see cref="Customer"/></b>：
/// 会话今天只会排"键已经在手里"的那一种先后 —— 父行的主键得在插子行之前就存在。
/// <c>Project.Id</c> 是客户端给的 <c>Guid</c>，所以这一格成立；<c>Customer.Id</c> 是 IDENTITY，
/// 父行的键要等那一句 INSERT 回来，而"先插空外键再回填"是 <c>Tp</c> 临时主键那一档（§8.2，未接线）。
/// 换句话说：这里合上的是<em>先后</em>，没合上<em>引用</em>，两者的分界就写在下面第一条用例的 Add 顺序里。</para>
///
/// <para>写只写本次运行标签行（与 <see cref="TrackingPathTests"/> 同一条纪律），裁判一律是
/// <see cref="Database.Rows"/>/<see cref="Database.Execute"/>（裸 ADO、另一条连接）。</para>
///
/// <para><b>第七家：本类的裁判不在场，而三条绿的与一条 skip 的各钉到什么程度要分开说</b>
/// （外键为什么建不出来，逐字在 <c>Database.EnsureProjectForeignKey</c> 与
/// <see cref="Database.RequireForeignKeyJudge"/> 那两段，这里只记读法）：最后那颗负样本按家族 skip，
/// 因为它<em>就是</em>那道裁判；上面三条在本家照跑，但它们到得了的只有"报告的档内表序"与
/// "那些语句按下述次序在真库上执行得过去、数据落对"这两格，钉不到第三格"<em>那个次序是必要的</em>"——
/// 而必要性的证人恰恰是库里那条约束。上面第三段那句"约束不在位时，前面三条绿得毫无意义"因此要读窄：
/// 本家那三条不是毫无意义，是<em>少一格意义</em>，而少的那一格在这里明写出来，不让它被绿色的计数顶掉。
/// <b>为什么整类不一起跳</b>：档内类型序在全仓库<em>只有</em>本类这一处证人
/// （<c>grep -rn "OrderedByDependency" tests/</c> 零命中，离线册没钉过那颗排序），
/// 整类跳成本家全黑会把那两格本来拿得到的读数一起丢掉。两笔缺口（本家的必要性证人、类型序缺离线册）
/// 一起记 §14。</para></summary>
public sealed class OrderingPathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    static DbContext Session() => new(Orm);

    // 合并入 Quick 仓库时改的一笔（原仓库为 static）：类内多条用例都写同一颗 Title「子」，
    // 而标签按 Batch 过滤只能隔开<em>跨进程</em>的行 —— 同进程内共一篇 Batch 时，
    // 单例跑没跑、清没清理全看类内方法的执行顺序。xunit v3 的方法序不保证跨编译稳定，
    // 重编译一次就能把 Deleting_both_sides 排到 A_child_added 之后而当场红（合并首跑即为此形状）。
    // 把 Batch 收到每用例一颗，顺序就不再是判据的一部分；断言文本一个字没动。
    readonly string Batch = "quicksql-write/order-" + Guid.NewGuid().ToString("N")[..8];

    string tagged(string name) => $"{Batch}/{name}";

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static List<SaveChangesOp> Ops(SaveChangesReport report, WriteOperation phase)
        => [.. report.Operations.Where(o => o.Operation == phase)];

    Project NewProject(string name) => new() { Id = Guid.NewGuid(), Name = tagged(name) };

    Milestone NewMilestone(Project project, string title) => new() { ProjectId = project.Id, Title = tagged(title) };

    /// <summary>父子都还在库里时读回那一行的外键。<b>读的是 <c>Title</c> 而不是 <c>Id</c></b>：
    /// 标签是本次运行独有的，所以这句不会被别人留下的行撞中。</summary>
    static Dictionary<string, string> Stored(Milestone milestone)
        => Assert.Single(Database.Rows("SELECT [ProjectId], [Title] FROM [dbo].[QuickSqlMilestone] WHERE [Title] = @title",
            ("@title", milestone.Title)));

    static int Projects(string where, params (string Name, object? Value)[] args)
        => Convert.ToInt32(Assert.Single(Database.Rows($"SELECT COUNT(*) AS Cnt FROM [dbo].[QuickSqlProject] WHERE {where}", args))["Cnt"]);

    [Fact]
    public async Task A_child_added_before_its_parent_is_still_inserted_after_it()
    {
        // 本类那三条从前都没有 <c>Database.Ready()</c> 在第一句（它们一直<em>间接</em>靠 <c>Session()</c> 里的
        // <c>Database.Orm</c> 把夹具链拉起来，而别的类先跑过就把那一步替它做了 —— "夹具侥幸"在本仓库
        // 不算证人）。v37-xugu-int-2.log 量到的是这一手的代价：全新库建不出夹具那张并发列表时，
        // <c>Orm</c> 交回的是 <c>InvalidOperationException</c> 而不是 skip，于是同一格缺口在整片静默之外
        // 还额外写出三颗"红"，而那三颗红读的其实是"夹具没到"。加上这一句，库不可用时它落进 skip 列表、
        // 带着原因原文；库可用时它是 <c>SkipUnless(null)</c>，一个字节都不改判据。
        Database.Ready();

        var db = Session();

        var project = NewProject("父");
        var milestone = NewMilestone(project, "子");

        db.Add(milestone);
        db.Add(project);

        // 首次出现序是 [Milestone, Project] —— 那一格还没接线的话，子行先插、约束当场挡下来，
        // 这一句就抛在 SaveChanges 里，而不是下面那条顺序断言报红。两条路都判得动，红的都是同一件事。
        var report = await db.SaveChangesAsync(ct);

        Assert.Equal(["QuickSqlProject", "QuickSqlMilestone"], Ops(report, WriteOperation.Insert).Select(o => o.Table).ToArray());

        // 提交没被约束挡下来 = 库里真的按那个顺序执行过（报告说的不算，库认的才算）。
        // <b>本家那一半按 <see cref="Database.StoredGuidText"/> 给</b>：库里那一格是驱动写的文本，大写形
        // （4710），而它打得中同一颗 <c>Guid</c> 的绑定参数（4711）—— 差的只是回包字形。
        Assert.Equal(Database.StoredGuidText(project.Id), Stored(milestone)["ProjectId"]);
        Assert.Equal(1, Projects("[Id] = @id", ("@id", project.Id)));
    }

    [Fact]
    public async Task A_parent_insert_still_precedes_a_child_update_in_a_later_phase()
    {
        // <c>Database.Ready()</c> 的理由与本文件第一条用例那段注释同一颗（这三条共用一条纪律）。
        Database.Ready();

        // 排的是<em>档内</em>的类型序，而三档之间还压着"先插、再改、最后删"。
        // 于是有一种形状只在整体上成立：子行的 UPDATE 改的就是外键那一格，而它指向的新父行要在同一批里插进去。
        // <b>这一格由三档的固定次序保证，掐掉档内排序它照样绿</b>（证伪过），留在这里判的是另一件事：
        // 报告把三档平铺成一条序列，所以"改外键的那一句排在插父行之后"是看得见的、而库里那句约束也确实认它。
        var first = Session();

        var old = NewProject("旧父");
        var milestone = NewMilestone(old, "过继");
        var fresh = NewProject("新父");

        first.Add(old);
        first.Add(milestone);
        await first.SaveChangesAsync(ct);

        var db = Session();

        var id = int.Parse(MilestoneId(milestone));

        db.Add(fresh);
        // 整只实例按"库里已有"承认后再改：Update 把每一列都算脏，其中就有外键那一格。
        db.Update(new Milestone { Id = id, ProjectId = fresh.Id, Title = milestone.Title });

        var report = await db.SaveChangesAsync(ct);

        var inserts = Ops(report, WriteOperation.Insert);
        var updates = Ops(report, WriteOperation.Update);

        Assert.Equal(["QuickSqlProject"], inserts.Select(o => o.Table).ToArray());
        Assert.Equal(["QuickSqlMilestone"], updates.Select(o => o.Table).ToArray());

        // 报告里三档是平铺的一条序列，所以跨档的先后看得见：那一句 UPDATE 必须排在本批唯一的 INSERT 之后。
        List<string> tables = [.. report.Operations.Select(o => o.Table)];

        Assert.True(tables.FindIndex(t => t == "QuickSqlMilestone") > tables.FindIndex(t => t == "QuickSqlProject"),
            $"子行的 UPDATE 排在了新父行的 INSERT 之前：{report}");

        Assert.Equal(Database.StoredGuidText(fresh.Id), Stored(milestone)["ProjectId"]);
    }

    [Fact]
    public async Task Deleting_both_sides_in_one_session_leaves_the_parent_for_last()
    {
        Database.Ready();

        var first = Session();

        var project = NewProject("父");
        var milestone = NewMilestone(project, "子");

        first.Add(project);
        first.Add(milestone);
        await first.SaveChangesAsync(ct);

        var db = Session();

        // 读的顺序把"首次出现序"摆成 [Milestone, Project] —— 于是<em>只</em>靠"删除走逆序"这一条老规矩
        // 会得到 [Project, Milestone]，先删父、当场撞约束。救回这一句的只能是拓扑排序本身。
        var trackedMilestone = (await db.Set<Milestone>().Where(m => m.Title == milestone.Title).ToListAsync(ct)).Single();
        var trackedProject = (await db.Set<Project>().Where(p => p.Id == project.Id).ToListAsync(ct)).Single();

        db.Remove(trackedProject);
        db.Remove(trackedMilestone);

        var report = await db.SaveChangesAsync(ct);

        Assert.Equal(["QuickSqlMilestone", "QuickSqlProject"], Ops(report, WriteOperation.Delete).Select(o => o.Table).ToArray());
        Assert.Equal(0, Projects("[Id] = @id", ("@id", project.Id)));
        Assert.Empty(Database.Rows("SELECT [Id] FROM [dbo].[QuickSqlMilestone] WHERE [Title] = @title", ("@title", milestone.Title)));
    }

    [Fact]
    public void The_constraint_that_judges_the_order_is_actually_in_place()
    {
        // 负样本：绕开会话直接往子表插一行没有父的。这一句<em>必须</em>抛，否则上面三条的裁判根本不在场。
        // 断言按约束名（ASCII，两种语言环境的实例都原样带出来），不按错误文案 —— 192.168.0.102 那台的
        // 默认排序规则是 GBK/936，消息可能是中文。
        //
        // <b>第七家这一颗是 skip 而不是绿</b>：它判的就是"裁判在不在位"，而本家那道裁判建不出来
        // （没有 ALTER ADD CONSTRAINT，而 CodeFirst 不发 FOREIGN KEY —— 逐字理由在
        // <c>Database.EnsureProjectForeignKey</c> 与 <see cref="Database.RequireForeignKeyJudge"/> 那两段）。
        // 上面三条因此在本家只钉到"报告序 + 数据落点"，钉不到"那个次序是必要的"，那一句已写进类注释。
        Database.RequireForeignKeyJudge();

        var raised = Assert.ThrowsAny<Exception>(() => Database.Execute(
            "INSERT INTO [dbo].[QuickSqlMilestone] ([ProjectId], [Title]) VALUES (@pid, @title)",
            ("@pid", Guid.NewGuid()), ("@title", tagged("无父"))));

        // <b>而"按名字断"这一半是 T-SQL 的，虚谷一家今天按不了名字</b>：那一家的约束违规消息里
        // <em>一个 ASCII 字节都没有</em> —— F-45 逐字看过那行（<c>obj/logs/v32-recon-funcs-9.log</c>）：
        // <c>[E13005] </c> 之后整段是非 ASCII（中文文案经驱动到进程 stdout 那一路已经是 GBK 字节，
        // 而 ASCII 在两种解码下都会原样活下来，所以"看不见名字"这一句是<em>可判</em>的，不是"读不出"）。
        // 于是这一家改断<em>那一颗错误编号</em>：E13005 是它在"违反约束"这一件事上给的编号，
        // 断到编号就仍然断到"库是因为约束挡的"，而不是"随便抛了点什么"。
        // <b>缺口同记一笔</b>：这一家能不能把<em>哪一条</em>约束告诉客户端，本版没量出可用的形（目录里名字在，
        // 消息里没有），归 §14 —— 它不影响本条的性质（本条只问"库里挡不挡"，而同一句 F-45 答的是挡）。
        if (Database.Family == Database.Target.Xugu)
            Assert.Contains("E13005", raised.Message, StringComparison.Ordinal);
        else
            Assert.Contains("FK_QuickSqlMilestone_QuickSqlProject", raised.Message, StringComparison.Ordinal);
    }

    static string MilestoneId(Milestone milestone)
    {
        // <c>CONVERT(varchar(20), …)</c> 换成 <c>CAST(… AS VARCHAR(20))</c>：两句在 T-SQL 里同一件事，
        // 而<em>后一形在这一家发得出去</em>（F-24/F-25 量到 <c>CAST(时间戳 AS VARCHAR[(23)])</c> 两形都收，
        // 交回的是文本；那颗 20 是整数侧的宽度，比时间那一格窄，没有额外的截断问题）。
        // 目的不变：拿回来的是一串文本而不是装箱数，用例才好把"两行两个号"逐字比出来。
        //
        // <b>第五族（mysql）要换的是那颗型名，而不是整句</b>：本家的字符型叫 <c>CHAR</c>，
        // 而 <c>VARCHAR(20)</c> 在 <c>CAST</c> 的位置上是<em>整形没有</em>—— 逐字红相是
        // <c>…syntax to use near 'VARCHAR(20)) AS Id FROM `QuickSqlMilestone` WHERE `Title` = 'quicksql-write/order-'</c>
        // 【实测·P-04 同一形在靶表上的第二次量到，出处 obj/logs/v37-my-recon-3.log】。
        // 换形 <c>CAST(Id AS CHAR(20))</c> 则交回 <c>Id=1 (String)</c>【实测·P-03】——
        // <b>回包是 <c>String</c> 而不是装箱数</b>正是这一句的本题，所以换完型名之后它量的还是同一件事。
        // 第四家与第七家不换：那两家<em>认</em> <c>VARCHAR(20)</c> 这一形（第四家整册绿，v37-pg-int-2.log；
        // 第七家同，v37-sqlite-int-2.log），本家今天只有这一颗型名是它不认的。
        var sql = Database.Family == Database.Target.MySql
            ? "SELECT CAST([Id] AS CHAR(20)) AS Id FROM [dbo].[QuickSqlMilestone] WHERE [Title] = @title"
            : "SELECT CAST([Id] AS VARCHAR(20)) AS Id FROM [dbo].[QuickSqlMilestone] WHERE [Title] = @title";

        return Assert.Single(Database.Rows(sql, ("@title", milestone.Title)))["Id"];
    }
}
