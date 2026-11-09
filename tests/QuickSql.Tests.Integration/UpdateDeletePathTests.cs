using System.Globalization;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>UPDATE / DELETE 两条链在真库上跑一遍（§5.4 写侧的第二、三条，§6 的 (1)(2) 两形态）。
///
/// 这一份不是离线方言用例的重复。离线那批钉的是"文本没写错"，而这两条链产出的是<b>别名形态的写语句</b> ——
/// <c>UPDATE [t0] SET … FROM [QuickSqlOrder] AS [t0]</c> 与 <c>DELETE FROM [t0] FROM [QuickSqlOrder] AS [t0]</c>：
/// "目标写别名、表在 FROM 里定义"这一种写法 T-SQL 认不认，黄金样本一点也证明不了（它比的是字符）。
/// 所以这里最先跑的那几条就该把这两句话真发一次。其余断言同样是"文本层面看不见的东西"：
/// <c>SET [Amount] = [t0].[Amount] + @p</c> 是不是真的在库里加、<c>CONCAT</c> 在 NULL 列上给空串还是给 NULL、
/// 那几种构建期拒绝是不是真的一个字节都没上线、被 DELETE 掉的行在回滚之后是不是回来了
/// （插入回滚了是"没有这行"，删除回滚了是"这行还得原样在"，后者没有"值没变"可以依赖）。
///
/// <b>删是有边界的删</b>：<see cref="WritePathTests"/> 那条"只插不删"在这里必须放宽，否则 DELETE 链就没有真库用例。
/// 放宽的方式是每一批行带一个本次运行独占的标签，删除语句的 WHERE 只按这个标签写，于是别人（包括上一轮的自己）
/// 留下的行一条也不会被碰到。唯一一次全表形状的写落在下面那条 <c>where_true</c> 用例：它打在
/// <c>QuickSqlSessionToken</c> 上（只有测试数据、没有谁按外键读它），整段关在事务里回滚，
/// 事后由裁判连接确认那次改写一行都没留下。人工清理仍然是一句 <c>LIKE 'quicksql-write/%'</c>。
///
/// <b>事务块内一律用 <c>Orm.Select</c> 复看，不用裁判连接</b>：裁判走的是另一条连接，
/// 未提交的东西它本来就看不见（那正是回滚判据的依据），而更糟的是它会去敲事务持有的那把 X 锁 ——
/// 一句 <c>Database.Rows</c> 写在块内不是"读到旧值"，是挂住等锁。</summary>
public sealed class UpdateDeletePathTests
{
    static QuickSqlOrm Orm => Database.Orm;

    /// <summary>本次运行独占的标签前缀。与 <see cref="WritePathTests"/> 同一个前缀开头，所以清库时一句 LIKE 覆盖两个文件。</summary>
    // Batch 每用例一颗（static 会让同类各用例共一篇标签，隔离押在 xunit 方法序上，重编译可翻红；理由全话见 OrderingPathTests 头注）
    readonly string Batch = "quicksql-write/cud-" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 5, 6, 7, 8, 9, 123);

    /// <summary>可空列上的 null 要走"常量那一档"，而 <c>Set(o =&gt; o.X, null)</c> 写不出来：
    /// null 字面量对 <c>TProp</c> 与 <c>Expression&lt;Func&lt;T,TProp&gt;&gt;</c> 两个重载都适用，编译器报二义性。
    /// 带类型的局部变量把推断锁死在第一个重载上 —— 这本身就是这条 API 形状的一个后果，记在这里。</summary>
    static readonly string? Cleared = null;

    /// <summary>与 <c>WritePathTests</c> 里同一颗：本版起两边都只转发到 <see cref="Database.MillisText"/>，
    /// 理由（两颗 <c>const string</c> 并成一颗、分岔只开一次）写在那一份的注释里，不重复第二遍。</summary>
    static string Millis(string column) => Database.MillisText(column);

    string tagged(int n) => $"{Batch}/{n}";

    string scope(string part) => $"{Batch}/{part}";

    /// <summary><c>customerId</c> 默认给一个非零的怪数：不点名的列如果被整实体摊平成了 SET，
    /// 它第一个变成 0，所以每条用例都顺手带着它。</summary>
    static Order row(string name, decimal amount, string? note, int customerId = 4242, OrderState state = OrderState.Shipped) => new()
    {
        CustomerName = name,
        CustomerId = customerId,
        Amount = amount,
        State = state,
        CreatedAt = Moment,
        Note = note,
    };

    /// <summary>插一行并取回库里那一号的真实自增 id —— 后面每条 WHERE 都打在"就是这一行"上。
    ///
    /// <para><b>v0.32 第二格：这一颗也是本文件那 11 颗红的收口</b>。它要的正是 <c>ExecuteIdentityAsync</c>
    /// （库没把键发回来就没有 id，后面的 WHERE 无从落点），所以第五家那一跑里凡走这一句的用例都红在
    /// <c>InsertEngine.ReadBackAsync</c>，一起按名字跳掉。<b>【v0.34 第一笔改判：收口退场】</b>
    /// 单颗自增列回填改走语句后回读，本文件这 11 颗随整族闸门一起解绑重跑。
    /// <b>为什么不放在类级别</b>：本文件另有三条今天<em>是绿的</em>
    /// （客户端造键那条、被拒的那条、显式 <c>WHERE 1=1</c> 回滚那条），它们都不走这一颗 ——
    /// 把闸门放到共用的地方会把那三条证人一起跳掉，那是把账做错而不是把账结清。这条判据与版本无关，
    /// 留着它是下一道闸门的选址依据。</para></summary>
    static async Task<int> Inserted(Order order)
    {
        return (int) await Orm.Insert(order).ExecuteIdentityAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>裁判连接上的原样回读：走裸 ADO，不经 ORM 的读路径，所以"写进去的是那一格"和
    /// "读回来的是那一格"两件事不会互相包庇（§13.1 的独立裁判）。</summary>
    static List<Dictionary<string, string>> Orders(string where, params (string Name, object? Value)[] args)
        => Database.Rows(
            "SELECT [Id], [CustomerName], [CustomerId], [Amount], [State], " +
            $"{Millis("[CreatedAt]")} AS CreatedAt, {Millis("[PaidAt]")} AS PaidAt, [Note] " +
            $"FROM [dbo].[QuickSqlOrder] WHERE {where}",
            args);

    static Dictionary<string, string> Row(int id) => Assert.Single(Orders("[Id] = @id", ("@id", id)));

    static int CountOrders(string where, params (string Name, object? Value)[] args)
        => Num(Assert.Single(Database.Rows($"SELECT COUNT(*) AS Cnt FROM [dbo].[QuickSqlOrder] WHERE {where}", args)), "Cnt");

    static int CountTokens(string where, params (string Name, object? Value)[] args)
        => Num(Assert.Single(Database.Rows($"SELECT COUNT(*) AS Cnt FROM [dbo].[QuickSqlSessionToken] WHERE {where}", args)), "Cnt");

    static int Num(Dictionary<string, string> row, string column) => int.Parse(row[column], CultureInfo.InvariantCulture);

    static decimal Money(Dictionary<string, string> row, string column) => decimal.Parse(row[column], CultureInfo.InvariantCulture);

    static DateTime When(Dictionary<string, string> row, string column) => DateTime.Parse(row[column], CultureInfo.InvariantCulture);

    /// <summary>事务块内的复看：必须是 ORM 自己的读出口，它走的是那条持有事务的连接（§6 (3) 的环境槽位）。</summary>
    static async Task<Order?> Inside(int id)
        => await Orm.Select<Order>().Where(o => o.Id == id).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

    // ── 两条链的语句形状真被库接受 ───────────────────────────────────

    [Fact]
    public async Task An_update_writes_the_named_columns_and_leaves_every_other_column_alone()
    {
        Database.Ready();

        var name = tagged(1);
        var order = row(name, 12.3456m, Batch);

        var id = await Inserted(order);
        var later = new DateTime(2026, 8, 9, 10, 11, 12, 456);

        var changed = await Orm.Update<Order>()
            .Set(o => o.CustomerName, "新名字甲")
            .Set(o => o.State, OrderState.Cancelled)
            .Set(o => o.CreatedAt, later)
            .Where(o => o.Id == id)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, changed);

        var stored = Row(id);

        // 点名的三列：中文（排序规则）、枚举（存 INT）、带毫秒的时间（1/300 秒取整）。三者在 SET 位上的
        // 塌法与在 VALUES 位上并不相同，binder 走的是同一段，而只有真库能证明同一段确实被走到了。
        Assert.Equal("新名字甲", stored["CustomerName"]);
        Assert.Equal((int) OrderState.Cancelled, Num(stored, "State"));
        Assert.Equal(later, When(stored, "CreatedAt"));

        // 没点名的五列必须原样。这一组是这条链最值钱的断言：若实现把整个实体摊平成 SET，
        // 未填的列会被写成 default —— PaidAt 变 NULL、CustomerId 变 0、Note 变 NULL、Amount 变 0。
        Assert.Equal(4242, Num(stored, "CustomerId"));
        Assert.Equal(12.3456m, Money(stored, "Amount"));
        Assert.Equal("NULL", stored["PaidAt"]);
        Assert.Equal(Batch, stored["Note"]);

        // UPDATE 不换号。
        Assert.Equal(id, Num(stored, "Id"));

        // 也不增行：按"本次运行独占的标签 + 改后的新名字"数，还是只有一行
        // （标签在运行内唯一，所以这一数数的分母只有本用例自己灌的那一条，不会被上一轮留下的同名字行混进来）。
        Assert.Equal(1, CountOrders("[Note] = @note AND [CustomerName] = @name", ("@note", Batch), ("@name", "新名字甲")));

        // 强类型那一侧也要对上：物化按位置读，改过的枚举列读回来还得翻回 OrderState。
        var typed = await Inside(id);

        Assert.NotNull(typed);
        Assert.Equal("新名字甲", typed!.CustomerName);
        Assert.Equal(OrderState.Cancelled, typed.State);
        Assert.Equal(later, typed.CreatedAt);
        Assert.Null(typed.PaidAt);
    }

    [Fact]
    public async Task A_delete_takes_the_aliased_from_form_and_removes_exactly_the_matched_rows()
    {
        Database.Ready();

        var note = scope("del");

        var doomed = new List<int>();

        for (var i = 11; i <= 13; i++)
            doomed.Add(await Inserted(row(tagged(i), 1m, note)));

        var kept = await Inserted(row(tagged(14), 2m, note, customerId: 7777));

        var deleted = await Orm.Delete<Order>()
            .Where(o => o.Note == note && o.CustomerId == 4242)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        // DELETE FROM [t0] FROM [QuickSqlOrder] AS [t0] WHERE … —— 库认这个别名写法，而且只按谓词命中数回报。
        Assert.Equal(3, deleted);

        Assert.All(doomed, one => Assert.Empty(Orders("[Id] = @id", ("@id", one))));

        var survivor = Row(kept);

        Assert.Equal(7777, Num(survivor, "CustomerId"));
        Assert.Equal(note, survivor["Note"]);

        // 同一句话再跑一次：命中 0 不是错误，而是"库里没有符合的行了"。空命中必须安静地返回 0，
        // 否则幂等的清理写法（跑两遍不出事）在这条链上就不成立。
        Assert.Equal(0, await Orm.Delete<Order>().Where(o => o.Note == note && o.CustomerId == 4242)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, CountOrders("[Note] = @note", ("@note", note)));
    }

    [Fact]
    public async Task A_chinese_value_reaches_the_column_and_a_chinese_condition_finds_the_row()
    {
        Database.Ready();

        // 名字与备注都是中文，且谓词也用中文匹配：cp1252 实例上如果绑定降级成 varchar，
        // SET 那一侧会写成 '?'，WHERE 那一侧会"匹配不到"而不是报错 —— 两个下场都不在离线用例的可见范围里（§10.4）。
        var name = tagged(2) + "/戊戌公司";
        var id = await Inserted(row(name, 8m, Batch));

        var changed = await Orm.Update<Order>()
            .Set(o => o.Note, "备注乙丙丁")
            .Set(o => o.CustomerName, name + "（改）")
            .Where(o => o.CustomerName == name)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, changed);

        var stored = Row(id);

        Assert.Equal("备注乙丙丁", stored["Note"]);
        Assert.Equal(name + "（改）", stored["CustomerName"]);

        // 反方向的负样本：原来那个名字现在已经不匹配任何行了。
        Assert.Equal(0, CountOrders("[CustomerName] = @name", ("@name", name)));
    }

    // ── 表达式那一档：值在库里算 ─────────────────────────────────────

    [Fact]
    public async Task An_expression_set_adds_to_the_value_in_the_column_not_to_the_one_the_caller_held()
    {
        Database.Ready();

        var order = row(tagged(3), 10.5m, Batch);

        var id = await Inserted(order);

        Assert.Equal(1, await Orm.Update<Order>().Set(o => o.Amount, o => o.Amount + 1m).Where(o => o.Id == id)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        // 第二次自增加在"第一次之后的列值"上：若这条链是应用侧读改写，第二次会把第一次覆盖掉，
        // 这里就会停在 11.5 而不是 11.5001。标度也顺手验一次 —— 四位小数不能在第 3 位被截掉。
        Assert.Equal(1, await Orm.Update<Order>().Set(o => o.Amount, o => o.Amount + 0.0001m).Where(o => o.Id == id)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(11.5001m, Money(Row(id), "Amount"));

        // 客户端对象一个字节也没动：这条链上没有跟踪器、也没有整行，所以 SetValue 那套回填语义在这里不存在。
        // 钉住它是为了将来接 SaveChanges 时不把这两种形状混成一个。
        Assert.Equal(10.5m, order.Amount);
    }

    [Fact]
    public async Task A_string_set_computed_from_the_column_concatenates_and_treats_null_as_empty()
    {
        Database.Ready();

        var filled = await Inserted(row(tagged(4), 1m, scope("cat")));
        var empty = await Inserted(row(tagged(5), 1m, null));

        Assert.Equal(1, await Orm.Update<Order>().Set(o => o.Note, o => o.Note + "/尾").Where(o => o.Id == filled)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(scope("cat") + "/尾", Row(filled)["Note"]);

        // 这一条才是 §5.8 那句"不折成 CONCAT 就会悄悄少行"的现场：C# 的 "a + b" 把 null 当空串，
        // 而 T-SQL 的 + 会让整个表达式变成 NULL。库里 Note 原本为 NULL 的那一行，改完必须是 "/尾" 而不是 NULL。
        Assert.Equal(1, await Orm.Update<Order>().Set(o => o.Note, o => o.Note + "/尾").Where(o => o.Id == empty)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("/尾", Row(empty)["Note"]);
    }

    [Fact]
    public async Task A_set_of_a_column_that_is_itself_the_condition_still_uses_the_pre_update_value()
    {
        Database.Ready();

        var note = scope("self");

        var id = await Inserted(row(tagged(6), 1m, note));

        // WHERE 看到的与 SET 算的是同一个"改之前"的值：这是 T-SQL 的语义，但别名形态的 UPDATE
        // 写歪一点就会变成"改完再筛"，于是这一行要么被改两次要么永远改不到。
        Assert.Equal(1, await Orm.Update<Order>()
            .Set(o => o.CustomerId, o => o.CustomerId + 1)
            .Where(o => o.Note == note && o.CustomerId == 4242)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, await Orm.Update<Order>()
            .Set(o => o.CustomerId, o => o.CustomerId + 1)
            .Where(o => o.Note == note && o.CustomerId == 4242)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        var stored = Row(id);

        Assert.Equal(4243, Num(stored, "CustomerId"));
        Assert.Equal(note, stored["Note"]);
    }

    // ── 命中数的口径 ─────────────────────────────────────────────────

    [Fact]
    public async Task The_affected_row_count_counts_predicate_hits_rather_than_values_that_changed()
    {
        Database.Ready();

        var id = await Inserted(row(tagged(7), 10.5m, Batch));

        // 写成它本来就是的那个值：库里那一格变了吗？没有。回报几行？一行。
        // §12.4 第 2 项那条变更守卫（IS DISTINCT FROM）在批量保存那一支，这条链上没接，所以口径就是谓词命中数。
        Assert.Equal(1, await Orm.Update<Order>().Set(o => o.Amount, 10.5m).Where(o => o.Id == id)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(10.5m, Money(Row(id), "Amount"));

        // 一条也没命中：返回 0，不抛，库里任何东西都不该动。
        Assert.Equal(0, await Orm.Update<Order>().Set(o => o.Amount, 1m).Where(o => o.Id == int.MaxValue)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(10.5m, Money(Row(id), "Amount"));
    }

    [Fact]
    public async Task Stacking_where_narrows_the_hit_set_instead_of_replacing_the_previous_condition()
    {
        Database.Ready();

        var note = scope("and");

        var both = await Inserted(row(tagged(8), 1m, note, customerId: 11, state: OrderState.Shipped));
        var other = await Inserted(row(tagged(9), 1m, note, customerId: 11, state: OrderState.Draft));
        var third = await Inserted(row(tagged(10), 1m, note, customerId: 12, state: OrderState.Shipped));

        // 三条条件叠着写，而且与 SET 交错（先 Where 后 Set 再 Where）—— 这条链上算子不挑顺序。
        // 第一条必须是本次运行独占的标签：库里还躺着上一轮的两行 11/Shipped，不加标签的话"命中几行"
        // 每跑一次加一，判据就不是判据了（本文件每一条写谓词都带标签的通则由此来）。
        var changed = await Orm.Update<Order>()
            .Where(o => o.Note == note)
            .Set(o => o.Note, note + "/改")
            .Where(o => o.CustomerId == 11)
            .Where(o => o.State == OrderState.Shipped)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        // 后一条 Where 是 AND 进去的，不是覆盖前一条：丢掉 State 会命中 2 行（both + other），
        // 丢掉 CustomerId 也会命中 2 行（both + third）；只有三条全在才是 1 行，而且正是 both 那一行。
        Assert.Equal(1, changed);

        Assert.Equal(note + "/改", Row(both)["Note"]);
        Assert.Equal(note, Row(other)["Note"]);
        Assert.Equal(note, Row(third)["Note"]);
    }

    [Fact]
    public async Task A_client_generated_key_works_as_the_condition_of_an_update_and_a_delete()
    {
        Database.Ready();

        var id = Guid.NewGuid();
        var value = scope("token");
        var later = new DateTime(2027, 1, 2, 3, 4, 5, 456);

        Assert.Equal(1, await Orm.Insert(new SessionToken { Id = id, Value = value, ExpiresAt = Moment })
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        // 主键不是自增列的第二张表：条件里那颗参数是 Guid，走 binder 的 GUID 那一档而不是文本化。
        Assert.Equal(1, await Orm.Update<SessionToken>()
            .Set(t => t.ExpiresAt, later)
            .Where(t => t.Id == id)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        var stored = Assert.Single(Database.Rows(
            $"SELECT [Value], {Millis("[ExpiresAt]")} AS ExpiresAt FROM [dbo].[QuickSqlSessionToken] WHERE [Id] = @id",
            ("@id", id)));

        Assert.Equal(value, stored["Value"]);
        Assert.Equal(later, DateTime.Parse(stored["ExpiresAt"], CultureInfo.InvariantCulture));

        // 这里的删删的还是本用例自己那一行 —— Guid 主键上"按号删一行"是这条链最常见的真实用法。
        Assert.Equal(1, await Orm.Delete<SessionToken>().Where(t => t.Id == id)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, CountTokens("[Id] = @id", ("@id", id)));
    }

    // ── 拒绝：一个字节都不许上线 ─────────────────────────────────────

    [Fact]
    public async Task A_write_that_is_refused_leaves_the_library_byte_for_byte_as_it_was()
    {
        Database.Ready();

        // 种子行是这里的地秤：一次漏网的"无 WHERE 写"最先改到的就是它们（它们就在同一张表里）。
        var seededBefore = CountOrders("[Note] = @tag", ("@tag", Database.Tag));
        var orphansBefore = CountOrders("[Note] = @tag", ("@tag", Database.OrphanTag));

        Assert.True(seededBefore > 0, "种子行不在了，这把地秤就没有意义，先让这条用例响。");

        Assert.Contains("没有 WHERE", Assert.Throws<QuickSqlException>(() => Orm.Update<Order>().Set(o => o.Note, "整表").ToSql()).Message);
        Assert.Contains("没有 WHERE", Assert.Throws<QuickSqlException>(() => Orm.Update<Order>().Set(o => o.Note, "整表").Explain()).Message);
        Assert.Contains("没有 WHERE", Assert.Throws<QuickSqlException>(() => Orm.Delete<Order>().ToSql()).Message);
        Assert.Contains("没有 WHERE", Assert.Throws<QuickSqlException>(() => Orm.Delete<Order>().Explain()).Message);

        await Assert.ThrowsAsync<QuickSqlException>(
            async () => await Orm.Update<Order>().Set(o => o.Note, "整表").ExecuteAffrowsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<QuickSqlException>(
            async () => await Orm.Delete<Order>().ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        // 一个 SET 列都没有的 UPDATE 同样在构建时响：语句没有任何效果，不该悄悄产出。
        Assert.Contains("一个 SET 列都没有", Assert.Throws<QuickSqlException>(() => Orm.Update<Order>().Where(o => o.Id == 1).ToSql()).Message);

        // 自增列不进 SET —— 那一列库里不接受写，会报错的是库，而这句话该由我们在生成之前说。
        Assert.Contains("不接受写", Assert.Throws<QuickSqlException>(() => Orm.Update<Order>().Set(o => o.Id, 1).Where(o => o.Id == 2).ToSql()).Message);

        // 契约的两把尺子也在上线之前：长度越界与 NOT NULL 列给 null。
        Assert.Contains("语句还没有生成", Assert.Throws<ValidationFailedException>(
            () => Orm.Update<Order>().Set(o => o.CustomerName, new string('字', 70)).Where(o => o.Id == 1).ToSql()).Message);

        // 这一条用的实体表在测试库里根本没有（建表清单只有那四张）：它照样在构造语句之前就响了，
        // 所以它同时是"拒绝发生在连库之前"的最硬证据。
        Assert.Equal("required", Assert.Single(Assert.Throws<ValidationFailedException>(
            () => Orm.Update<LegacyShipment>().Set(s => s.CarrierCode, Cleared).Where(s => s.ShipmentId == 1).ToSql()).Failures).Rule);

        Assert.Equal(seededBefore, CountOrders("[Note] = @tag", ("@tag", Database.Tag)));
        Assert.Equal(orphansBefore, CountOrders("[Note] = @tag", ("@tag", Database.OrphanTag)));
        Assert.Equal(0, CountOrders("[Note] = @x", ("@x", "整表")));
    }

    [Fact]
    public async Task The_explicit_where_true_is_accepted_by_the_database_and_the_rollback_undoes_it()
    {
        Database.Ready();

        var first = scope("true-1");
        var second = scope("true-2");
        var sentinel = scope("覆盖全表");

        await Orm.InsertRange([
            new SessionToken { Id = Guid.NewGuid(), Value = first, ExpiresAt = Moment },
            new SessionToken { Id = Guid.NewGuid(), Value = second, ExpiresAt = Moment },
        ]).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, CountTokens("[Value] IN (@a, @b)", ("@a", first), ("@b", second)));

        using (var tran = Orm.BeginTransaction())
        {
            // 逃生舱在库里的形状是 WHERE @p = 1：一句恒真的参数化谓词。T-SQL 收不收、收了是不是真当"全表"算，
            // 只有真跑一次知道（离线那批钉的是文本）。
            var touched = await Orm.Update<SessionToken>().Set(t => t.Value, sentinel).Where(t => true)
                .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

            // 恒真在库里就是全表：命中数必须等于这张表当时的全部行数，而不是本用例那两行。
            // 这张表只由测试写，所以"全部"是一个稳定的参照。
            var total = await Orm.Select<SessionToken>().CountAsync(TestContext.Current.CancellationToken);

            Assert.True(total >= 2, $"这张表里只有 {total} 行，本用例插的两行没落到这个库里。");
            Assert.Equal(total, touched);

            // 同一条连接看得见未提交的自己：读侧吃环境事务这件事在写链上同样得成立，否则回滚判据无从下手。
            Assert.Equal(total, await Orm.Select<SessionToken>().Where(t => t.Value == sentinel).CountAsync(TestContext.Current.CancellationToken));

            Assert.False(tran.Completed);
        }

        // 出块不回滚就是"整张表被一次实验改写"。裁判连接只看得见已提交的，所以这三句一起才是撤销的证据。
        Assert.Equal(0, CountTokens("[Value] = @s", ("@s", sentinel)));
        Assert.Equal(1, CountTokens("[Value] = @a", ("@a", first)));
        Assert.Equal(1, CountTokens("[Value] = @b", ("@b", second)));
    }

    // ── 两条链进事务（§6 (1)(2)） ────────────────────────────────────

    [Fact]
    public async Task A_committed_transaction_carries_both_write_chains_through_and_survives_a_second_dispose()
    {
        Database.Ready();

        var keptId = await Inserted(row(tagged(21), 3m, scope("tran")));
        var goneId = await Inserted(row(tagged(22), 4m, scope("tran")));

        var tran = Orm.BeginTransaction();

        Assert.Equal(1, await Orm.Update<Order>().Set(o => o.Note, scope("tran") + "/提交").Where(o => o.Id == keptId)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await Orm.Delete<Order>().Where(o => o.Id == goneId)
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        // 块内只用自己的读出口复看（理由见类注释末段）：这两句证明两条链都挂在了这条连接上，
        // 而不是各自另开一条连接、写完就地提交。
        Assert.Equal(scope("tran") + "/提交", (await Inside(keptId))!.Note);
        Assert.Null(await Inside(goneId));

        tran.Commit();

        Assert.True(tran.Completed);

        // Commit 之后再 Dispose 既不能回滚也不能抛：using 与显式 Commit 并存是常见写法，
        // 而这里的后果不是异常而是数据 —— 回滚了就是把一次已确认的改写抹掉。
        tran.Dispose();
        tran.Dispose();

        Assert.Equal(scope("tran") + "/提交", Row(keptId)["Note"]);
        Assert.Empty(Orders("[Id] = @id", ("@id", goneId)));
    }

    [Fact]
    public async Task A_rolled_back_transaction_puts_a_deleted_row_back_and_an_updated_row_old()
    {
        Database.Ready();

        var note = scope("rb");

        var id = await Inserted(row(tagged(23), 5m, note));
        var goneId = await Inserted(row(tagged(24), 6m, note));

        var failed = await Orm.ExecuteInTransactionAsync(
            async _ =>
            {
                await Orm.Update<Order>().Set(o => o.Amount, 99m).Set(o => o.PaidAt, Moment).Where(o => o.Id == id)
                    .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);
                await Orm.Delete<Order>().Where(o => o.Id == goneId).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

                throw new InvalidOperationException("业务判定不通过");
            },
            ct: TestContext.Current.CancellationToken);

        Assert.False(failed.Success);
        Assert.Contains("业务判定不通过", failed.ErrorMessage, StringComparison.Ordinal);
        // v0.39 第二笔旁路：回滚失败记在 RollbackFailure 一旁，不盖业务原文。
        Assert.Null(failed.RollbackFailure);

        // 删除的回滚是这条链上最难假扮成绿的一条：插入回滚只是"少一行"，删除回滚要求那一行连内容一起回来。
        var restored = Row(id);

        Assert.Equal(5m, Money(restored, "Amount"));
        Assert.Equal("NULL", restored["PaidAt"]);
        Assert.Equal(note, restored["Note"]);
        Assert.NotEmpty(Orders("[Id] = @id", ("@id", goneId)));
    }

    [Fact]
    public async Task The_ambient_form_sees_a_deletion_it_made_and_gives_it_back_on_rollback()
    {
        Database.Ready();

        var id = await Inserted(row(tagged(25), 7m, scope("ambient")));

        using (var ambient = Orm.BeginAmbientTransaction())
        {
            Assert.Equal(1, await Orm.Delete<Order>().Where(o => o.Id == id)
                .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

            // 形态 (3) 与 (1) 是同一个机制，这里断的是 DELETE 这条新链也认那个槽位：
            // 不认的话这一句会在另一条连接上就地提交，回滚就成了空话，而块内复看会读到"还在"。
            Assert.Null(await Inside(id));

            ambient.Rollback();

            Assert.True(ambient.Completed);
        }

        Assert.NotEmpty(Orders("[Id] = @id", ("@id", id)));
    }

    // ── 按实体那一行的两个入口（orm.Update(实体) ∶ orm.Delete(实体)） ──────────

    [Fact]
    public async Task An_entity_update_leaves_the_column_it_never_said_at_the_value_the_store_has()
    {
        Database.Ready();

        // 库里那一行 PaidAt 有值，而交回来的那只实体把它抹成 null。这一档读作"这一列这次没说"，
        // 于是那一格必须留在库里。这一条是整件事唯一的证人：整行摊平那一种写法（UpdateRange 的契约）
        // 发出去的句子少一颗参数、而这一档发的句子也少一颗参数，两份黄金样本各自都"看着对" ——
        // 只有库能回答"那一格现在还是不是那颗时间"。
        var paidOn = new DateTime(2026, 6, 7, 8, 9, 10, 111);
        var order = row(tagged(26), 3m, scope("entity-door"));
        order.PaidAt = paidOn;

        var id = await Inserted(order);

        var loaded = await Orm.Select<Order>().Where(o => o.Id == id)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(paidOn, loaded!.PaidAt);

        loaded.Note = "改过备注";
        loaded.PaidAt = null;

        Assert.Equal(1, await Orm.Update(loaded).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        var stored = Row(id);

        // 点名的那一格真变了，没点名的那一格一格没动，其余列被同一份值原样盖回去（它们带来值）。
        Assert.Equal("改过备注", stored["Note"]);
        Assert.Equal(paidOn, When(stored, "PaidAt"));
        Assert.Equal(3m, Money(stored, "Amount"));
        Assert.Equal(4242, Num(stored, "CustomerId"));
        Assert.Equal(Moment, When(stored, "CreatedAt"));
    }

    [Fact]
    public async Task An_entity_delete_takes_exactly_the_row_it_was_handed_and_reads_nothing_else()
    {
        Database.Ready();

        var kept = await Inserted(row(tagged(27), 1m, scope("delete-door")));
        var gone = await Inserted(row(tagged(27), 2m, scope("delete-door")));

        // 交出去的是一只只填了主键的实体：其余列一列都不读，所以"这只实体别的列全是默认值"这件事
        // 在这一档没有任何后果——那一格只借主键定位，不借值。
        Assert.Equal(1, await Orm.Delete(new Order { Id = gone }).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Empty(Orders("[Id] = @id", ("@id", gone)));
        Assert.Equal(1m, Money(Row(kept), "Amount"));
    }
}
