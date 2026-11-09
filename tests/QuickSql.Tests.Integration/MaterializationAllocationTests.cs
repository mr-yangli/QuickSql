using System.Data;
using System.Data.Common;
using System.Globalization;
using QuickSql.Materialization;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§12.7 C 第二条（零装箱物化）在<b>真驱动</b>上的现状，以及"为什么现在还没有那道门"的见证。
///
/// <para>v0.14 实测过一版强类型门（<c>GetFieldType(o) == typeof(T)</c> 时改走 <c>IsDBNull + GetFieldValue&lt;T&gt;</c>）：
/// 它确实把一颗 <c>int</c> 列的读从 24 B 降到 0 B，但每次读从 95 ns 涨到 137 ns，于是撤掉了
/// （逐格数字与被撤的论证在 §12.7 C 末尾，重新成立的条件在 §16.21）。这一批用例钉的是<b>撤掉之后</b>的两件事：</para>
///
/// <para>① <b>真驱动上装箱仍然存在</b> —— 这是那把门的缺席证明。哪天 §16.21 的提升版落地并让这颗列不再装箱，
/// 这一条会红，那正是我们要的信号：它红了才允许把 §12.7 C 与 §12.8 第 3 行的说法一起改。<br>
/// ② <b>值与 NULL 语义在真驱动上和假驱动上一样</b> —— 撤门不能顺手改掉正确性。</para>
///
/// <para>③ <b>v0.32 第二格把这批用例放到第五家跑了一遍，结论是"门的缺席跨家族同号"</b>：三对
/// <c>Assert.Equal(boxed, door)</c> 在两家的五颗读数上全绿（第五家：<c>id 112</c> / <c>money 8 152</c> /
/// <c>moment 120</c> / <c>text 8 152</c> B/次），而<em>绝对值</em>差一个数量级、字符串那一格从第一家的
/// <c>0</c> 变成这里的 <c>8 152</c>。两处判据的分岔都写在用例里，各带自己的出处，不合并成一句"大致相同"。</para>
///
/// <para><b>判据只取字节不取毫秒</b>（§12.8 的口径）：分配数与机器负载无关，所以这里能断"等于 24"，
/// 而不能像时间那样只能设单向地板——那 42 ns 的差价就是没法这样断的东西，它只记在文档里。</para>
///
/// <para><b>④ 第七笔（2026-09-30）在这一层添了第三条问句，而它改的正是上面①那句"门的缺席证明"的读法</b>：
/// §16.21 的提升版<em>已经落地</em>（第五笔丙的票 + 第七笔丁的裁），但它落在<b>臂</b>上而不是把门换掉 ——
/// <c>ValueConverter.Read&lt;T&gt;</c> 仍然是那扇唯一的门、仍然装箱（所以上面①那条用例照旧绿，
/// 它钉的是"门这一侧"），装箱真正被跳过的是驱动交回的类型与成员声明同型的那些列走
/// <c>DirectRead.ReadXxx</c> 的那一条路。这一层的第三条用例
/// （<c>The_arm_and_the_door_differ_by_exactly_the_box_on_the_real_driver</c>）问的就是那一路在真驱动上的三件事：
/// 落不落臂仍只由 <c>GetFieldType</c> 决定、有臂的列一次取值不贵于门、字符串那一臂只钉方向不钉 0。
/// 在此之前这三件事只有探针读数（<c>obj/probe-real/</c>）而没有钉子，而<em>这一条用例第一次跑就把探针
/// 那句"臂 0 B"改成了两半</em>：ORM 这一侧不造盒是普遍的，那一格总共省不省还要看驱动的原始访问器
/// 自己分不分东西（虚谷的 <c>GetInt32</c> 与 mysql 的 <c>GetDecimal</c> 都自己分 32 B）——
/// 逐族逐型的读数在那条用例的注释里，判词的台账面在 §12.8 第 3 行末段。</para>
///
/// <para><b>⑤ 第八笔把同一批问句搬到标量与聚合出口上</b>（结 §14 债③ 的那一支）：那一档从这一笔起也领票了，
/// 于是这一层多一条 <c>The_scalar_exit_on_the_real_driver_never_costs_more_than_the_door</c>。
/// 它比 ④ 多出来的一截是<b>空值守卫换人问</b> —— 实体那一档的成员声明自带可空性，而标量出口的请求型不带，
/// 所以第七笔丁那一刀跳掉的那一问在这一档由出口问回来。这条新读数因此是按<em>出口</em>量的，
/// 而它必须仍然不贵于门。<b>「不贵于」而不是「等于」：那一趟 <c>IsDBNull</c> 在四族上不添堆，而虚谷那一族
/// 它自己每值分 32 B</b>（探针 三b 的【实测·v38-8-real-xugu-2】），所以本条在这一族判的是方向而不是零。</para></summary>
public sealed class MaterializationAllocationTests
{
    static double PerCall(Action body)
    {
        for (var i = 0; i < 500; i++)
            body();

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 20_000; i++)
            body();

        return Math.Round((GC.GetAllocatedBytesForCurrentThread() - before) / 20_000.0, 2);
    }

    /// <summary>真驱动上开一条只读命令，停在第一行。行数是 3 而不是 1：SQL 里带标准分页子句，
    /// 顺带证明 <c>OFFSET .. FETCH FIRST</c> 在 SQL Server 上可用（不用 <c>TOP</c>，那是方言字样）。
    ///
    /// <para><b>v0.32 第二格：这一句在第五家换形，而换的是<em>分页那一颗</em>、不是整批用例</b>。
    /// 第四跑里本文件两条用例红在 <c>[E19260] syntax error, unexpected '['</c>，那一颗红点会让人以为
    /// "这一家测不了装箱"，真相是夹具自己发了两句它不认的字：<c>[方括号]</c> 走 <see cref="Database.FamilyText"/>
    /// 就折成 <c>"大写"</c>，而 <c>OFFSET 0 ROWS FETCH FIRST 3 ROWS ONLY</c> <em>整形没有</em>
    /// （F-46 量到语法错，F-46b/A-01 量到 <c>LIMIT 3 OFFSET 0</c> 可用）。按 <c>Database</c> 类注释里那条纪律，
    /// 翻译器只管字形、不管"一句有、一句没有"，所以这一处分岔留在用例里点名。</para></summary>
    static DbDataReader RowOn(string columns, string table = "QuickSqlOrder")
    {
        Database.Ready();

        // <b>上一版这里写死 <c>SqlServer.TestConnectionString</c>，而同一句里用的是
        // <c>Database.Orm.Provider.Connectors</c> —— 组装根是缝给的、连接串却是某一家的</b>。
        // 第五家第一跑就是这一颗炸出来的：两条用例红在 <c>[E34304] 指定的 Ip,Port 无效</c>
        // （拿虚谷的驱动去连 <c>192.168.0.102</c>），量具与断言一句都没跑到，那种红<em>读起来像</em>
        // "这一家测不了分配"，而真相只是这一行连错了库。改成 <see cref="Database.ConnectionString"/>：
        // 这一层所有裸 ADO 口子从此共用一颗连接串，缝在哪一家就连哪一家。
        var connection = Database.Orm.Provider.Connectors.CreateConnection(Database.ConnectionString);

        connection.Open();

        // 读未提交仍然留着，但它已经不是这一层唯一的死锁闸门：v0.15 把整个 assembly 串行化了
        // （xunit.runner.json，理由见规格 §13.4 末段），"另一个类的 UPDATE"这个对手没有了。
        // 留着它的理由是这条测量用的 reader 要在第一行上停住上百毫秒，而这个库是活的 —— 别的进程
        // （查询工具、另一个运行中的测试会话）在同一批表上写时，默认的 READ COMMITTED 会把那
        // 上百毫秒变成别人等它的一段时间。这一句是设计判断而不是实测：串行化之后的五场里没有一场
        // 是"外部并发写入"的场景。这一批只测"读一颗值多少钱"与"读出来对不对"，两者都不需要可重复读。
        var transaction = connection.BeginTransaction(IsolationLevel.ReadUncommitted);

        var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = Database.FamilyText(
            $"SELECT {columns} FROM [{table}] ORDER BY [Id] " +
            (Database.Family switch
            {
                // 第五家与第七家同一形（<c>LIMIT n OFFSET m</c>）而<em>各家各是自己量出来的</em>：
                // 那一家是 F-46/F-46b（<c>OFFSET .. FETCH FIRST</c> 在那边是 <c>E19260 syntax error</c>），
                // 本家是【实测·4733 ∶ 4526 字面量两臂、4526 那一条还顺带数到 <c>OFFSET</c> 之后行号从第三行起】。
                // 为什么不写成"两家共用一颗常量"：这一格的<em>形</em>相同是巧合，出处不同，
                // 下一版有人按本家重问那一颗时不该顺带把第五家的读数也换掉。
                Database.Target.Xugu or Database.Target.Sqlite => "LIMIT 3 OFFSET 0",

                // <b>MySQL 这一家也是 <c>LIMIT n OFFSET m</c>，而它同样是<em>自己量出来的那一颗</em>，
                // 所以上面那句"不写成共用常量"在这里一个字都不松</b>：本家的三颗读数是
                // <c>Z-01</c>（<c>LIMIT 2 OFFSET 1</c> 交回第二、第三行 —— 同一颗形，不同的那一次是
                // <c>obj/logs/v37-my-recon-1.log</c>）、<c>Z-02</c>（<c>OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY</c>
                // 逐字红在 <c>near 'OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY'</c>）与这一版补的
                // <c>P-05 ∶ P-06</c>（<c>obj/logs/v37-my-recon-3.log</c>：<em>用例里逐字要写出去的那一串</em>
                // <c>LIMIT 3 OFFSET 0</c> 带 <c>ORDER BY</c> 交三行，同一张靶表上的原形交
                // <c>…near 'OFFSET 0 ROWS FETCH FIRST 3 ROWS ONLY'</c>）。
                // 为什么要把"逐字那一串"再问一次：<c>Z-01</c> 量的是那颗<em>形</em>可不可用，
                // 而这一句红过的那一条要的是"这一串发得出去且交回三行"，两颗问句不一样。
                // 第三家（T-SQL）与第四家（postgresql）留下面那一支：那两家的 <c>TOP</c> ∶ <c>OFFSET..FETCH</c>
                // 两形在本仓库早有真库读数，而本家的 <c>TOP</c> 也量过是红的【实测·Z-03】。
                Database.Target.MySql => "LIMIT 3 OFFSET 0",
                _ => "OFFSET 0 ROWS FETCH FIRST 3 ROWS ONLY",
            }));

        // CloseConnection：调用方 using 掉 reader 就把命令与连接一起收走，不往进程里漏连接。
        var reader = command.ExecuteReader(CommandBehavior.CloseConnection);

        if (!reader.Read())
            Assert.Fail($"[{table}] 里没有行，这条用例无从测量（种子没建起来？）。");

        return reader;
    }

    [Fact]
    public void A_value_typed_column_still_boxes_because_the_typed_door_is_not_hoisted_yet()
    {
        using var reader = RowOn("[Id], [Amount], [CreatedAt], [CustomerName]");

        // <b>这一格在第七家换的是这一颗</b>：<c>Id</c> 在契约里是 <c>int</c>，而本家把它存在唯一的
        // 那颗 64 位整数里，于是驱动交回的 CLR 型是 <c>Int64</c>（【实测·211 ∶ 310 ∶ 515】，
        // 三处不同来源的整数列全交 <c>Int64</c>，也就不是某一列的特例）。名字放在缝里
        // （<see cref="Database.IntClrType"/>）：下面那句"门与 <c>GetValue</c> 同值"在本家必须跟着换形，
        // 而<em>要换的理由就是这一颗</em>，两处隔七行各写一遍两家各一句的话，改一处忘一处就会假绿。
        Assert.Equal(Database.IntClrType, reader.GetFieldType(0));

        // <b>这颗是本版新加的，而它是这一批用例在第五家的分岔点</b>：同一列 <c>NUMERIC(18,4)</c>，
        // T-SQL 交回 <c>decimal</c>、这一家交回 <c>string</c>（A-01 逐字量到 <c>"12.3456"</c>，
        // 与 F-46b 同一件事的第二次量）。名字放在缝里（<see cref="Database.MoneyClrType"/>）而不是在这里写两家各一句：
        // 下面那对 <c>money*</c> 判据钉的是"这一格装了什么箱"，而<em>那一句成不成立要看的就是这一颗</em>。
        Assert.Equal(Database.MoneyClrType, reader.GetFieldType(1));

        // 逐列各测各的箱子：门的读数必须与"直接 GetValue 再进收容所"同价，而三颗列的箱子大小不同
        // （int 与 DateTime 24 B、decimal 32 B），所以那句 moneyBoxed > idBoxed 顺带钉住"这三格不是同一个数被抄了三遍"。
        var idBoxed = PerCall(() => _ = reader.GetValue(0));
        var idDoor = PerCall(() => _ = ValueConverter.Read<int>(reader, 0));
        var moneyBoxed = PerCall(() => _ = reader.GetValue(1));
        var moneyDoor = PerCall(() => _ = ValueConverter.Read<decimal>(reader, 1));
        var momentBoxed = PerCall(() => _ = reader.GetValue(2));
        var momentDoor = PerCall(() => _ = ValueConverter.Read<DateTime>(reader, 2));
        var textDoor = PerCall(() => _ = ValueConverter.Read<string>(reader, 3));

        Assert.True(idBoxed > 0, $"真驱动竟然不装箱了（实测 {idBoxed} B/次）：判据要改，别删用例。");

        // <b>第七家把上面那句"门的读数必须与直接装箱同价"打破了，而破的不是量具，是型</b>：本家的
        // <c>INTEGER</c> 列交回装箱 <c>Int64</c> 而契约要 <c>int</c>，于是门在 <c>GetValue</c> 那一颗箱之外
        // 还要再交一次 —— <c>ValueConverter.To&lt;int&gt;</c> 走到末尾那颗
        // <c>Convert.ChangeType(value, underlying, Invariant)</c>（<c>Materializer.cs:71</c>），它返回的是
        // <c>object</c>，那颗窄化出来的 <c>int</c> 得再进一次收容所。<b>贵出来的正好是一整颗箱</b>：
        // 同一把尺在本家量到 <c>id 24 → 48</c>、<c>moment 72 → 96</c>，两处差都是 24 B
        // （【实测·4812】，那颗 <c>ToInt64</c> 的对照臂交回 24，也就是差的那一截不是"读"、是多出来的那次装箱）。
        // 判据因此按方向写：<em>门比直接装箱贵</em>。它比等号弱在哪：弱在钉不住"贵多少"，
        // 而那一半记在这里而不是钉成数字，理由与下面 ③ 那句"8 152 不能钉"是同一条（钉的是驱动某一版的内部形状）。
        // <b>哪天 §16.21 那颗提升版的门落地</b>（原始访问器、零装箱），这两句会一起红，那才允许改口。
        if (Database.Family == Database.Target.Sqlite)
            Assert.True(idDoor > idBoxed,
                $"第七家读一颗整数列的门不再比直接装箱贵了（实测 {idBoxed} → {idDoor} B/次）：" +
                "那是 §16.21 那颗提升版的门落地的形状，本文件的类注释、§12.7 C 与 §12.8 第 3 行要一起改，别只改这一句。");
        else
            Assert.Equal(idBoxed, idDoor);

        Assert.True(moneyBoxed > idBoxed, $"decimal 的箱子（{moneyBoxed} B）不再比 int（{idBoxed} B）大，量具或驱动变了，这一批读数要重看。");
        Assert.Equal(moneyBoxed, moneyDoor);

        if (Database.Family == Database.Target.Sqlite)
            // 同一颗因，另一格果：本家的时刻列在库里是 <c>TEXT</c>（<c>SqliteTypes</c> 第五条），
            // 驱动读回 <c>string</c>【实测·4810 ∶ 4813】，门要的是 <c>DateTime</c> —— 于是又是一颗箱。
            Assert.True(momentDoor > momentBoxed,
                $"第七家读一颗时刻列的门不再比直接装箱贵了（实测 {momentBoxed} → {momentDoor} B/次）：同上那句的判据要一起重看。");
        else
            Assert.Equal(momentBoxed, momentDoor);

        // 字符串本来就不装箱：报 0 是为了让那个 0 不被读成"字符串那一格已经省过了"。
        //
        // <b>v0.32 第二格：那句"不装箱"只有第一家成立，而这一格是这批用例在第五家唯一的实质分岔</b>。
        // 同一把尺（<c>PerCall</c>，20 000 次取均值、四舍五入到 0.01）在第五家的四个读数：
        // <c>id=112</c>、<c>money=8 152</c>、<c>moment=120</c>、<c>text=8 152</c> B/次
        // （v32-int-xugu-7.log；第一家那一版是 <c>int 24</c> / <c>decimal 32</c> / <c>DateTime 24</c> / <c>string 0</c>，
        // 逐格在 §12.7 C 末尾）。三件事一起记：
        // ① <b>形状仍然成立</b> —— 上面那三对 <c>Assert.Equal(boxed, door)</c> 在五颗数上全绿，
        //   也就是"门与直接装箱同价"这一句<em>不随家族变</em>，那把缺席的门在两家的缺席是同一件事；
        //   <b>这一句到第七家不再成立，它已被上面那两臂换掉</b>（本家两格门比装箱贵一整颗箱，
        //   【实测·4812】；留这段原文是因为第五家那一版的三对确实全绿，改口要有可对读的那一版）。
        // ② <b>绝对值差一个数量级</b> —— 一颗 <c>int</c> 的读在这里要 112 B 而不是 24 B；
        // ③ <b>字符串那一格在这一家根本不"免费"</b>：<c>text</c> 与 <c>money</c> 同为 8 152 B/次，
        //   而 <c>money</c> 那一列在这一家交回的也是 <c>string</c>（A-01/F-46b）——两颗 8 152 是同一件事的两次量，
        //   一颗 <c>VARCHAR</c> 与一颗 <c>NUMERIC</c> 的串读起来等价，且都与串的实际长度无关（三字的"甲公司"
        //   与七字的"12.3456"同价）。所以这里的判据只能写成单向地板，理由与 §12.8 那三颗时间格子同一类：
        //   <b>"等于 8 152"钉的是驱动某一版的内部缓冲，不是这一族的行为约定</b>，换一版驱动就红不是我们想要的信号。
        //   地板取 8 000：它比第一家的 0 高三个数量级，比实测低一截，谁把这一格改成"缓存住就不分配"都会红在这里。
        if (Database.Family == Database.Target.Xugu)
            Assert.True(textDoor > 8_000,
                $"第五家读一颗字符串列的代价不再是每次数 KB（实测 {textDoor} B/次）：" +
                "那条 8 152 的读数变了，先重跑 v32-int-xugu-7 那一场再改这一格与 §12.7 C，别改判据放它过去。");
        else if (Database.Family == Database.Target.Sqlite)
            // <b>第七家：那一串"本来就不装箱"在本家只剩第一家成立</b>。本家的文本列每读一次都由驱动
            // 现造一颗 <c>string</c>（【实测·4812：<c>text GetValue</c> 与 <c>text 门</c> 同为 32 B/次，
            // 差 0 而都不为 0】；那条"门与装箱同价"在本家只有这一格还成立，因为这里根本不换型）。
            // 地板只取 <c>&gt; 0</c> 而不钉 32：那颗 32 是"这版驱动 × 这串字的长度"的函数（"甲公司"三字），
            // 换成"12.3456"那格的读数是 40（同一轮 4812），钉任何一个数都是钉驱动的缓冲形状。
            Assert.True(textDoor > 0,
                $"第七家读一颗文本列不再每次分配（实测 {textDoor} B/次）：驱动换行了，这一格的读数与 §12.7 C 末尾那份要一起重看。");
        else if (Database.Family == Database.Target.PostgreSql)
            // <b>第四家与第七家同一臂，而<em>不是</em>同一条因</b>：本家的文本列在库里就是 <c>varchar</c>
            // （原生型，不像第七家落 <c>TEXT</c>），而这一格上面那三对"门与直接装箱同价"在本家全绿
            // （<c>id</c>/<c>money</c>/<c>moment</c> 三处，见 <c>obj/logs/v37-pg-int-1.log</c> 的那一轮：
            // 这一句是那一跑里唯一红在本文件的用例，<c>Expected: 0 / Actual: 32</c>）——
            // 也就是说本家换的只有"字符串那一格免费"这一句，而不是"门要多换一次型"那一句。
            // 判据同样是地板而不是数：那颗 32 与第七家读到的 32 同值而<em>不同源</em>（一家是 UTF8 解码现造串、
            // 一家的驱动缓冲形状本身），钉 32 就等于把两家的巧合钉成一条约定。
            Assert.True(textDoor > 0,
                $"第四家读一颗文本列不再每次分配（实测 {textDoor} B/次）：Npgsql 换行了，这一格的读数与 §12.7 C 末尾那份要一起重看。");
        else if (Database.Family == Database.Target.MySql)
            // <b>第五族（mysql）也落在"文本那一格不是免费的"这一支，而它的第一颗读数就是这一条用例</b>：
            // 本家第一次到场的那一跑里这一句红在 <c>Expected: 0 / Actual: 32</c>
            // （<c>obj/logs/v37-my-int-2.log</c> 那一跑唯一的一颗红，用例名 <c>A_value_typed_column_still_boxes…</c>）——
            // 那句 0 是 T-SQL 那一侧的形状（驱动已经交回一颗 <c>string</c>，门不再换型就不再分配），
            // 而本家的 <c>MySqlConnector</c> 每读一次文本列自己解一次码，于是门那一侧多出一颗分配。
            // <b>判据仍然取地板而不钉 32</b>：那颗 32 与第七家、第四家读到的 32 <em>同值而不同源</em>
            // （一家是 UTF8 解码现造串、一家是驱动缓冲形状、本家这一颗今天只有一跑一个数），
            // 钉住它等于把三家的巧合钉成一条约定 —— 上面两支各写一句的理由在这里一个字不改。
            Assert.True(textDoor > 0,
                $"第五族读一颗文本列不再每次分配（实测 {textDoor} B/次）：MySqlConnector 换行了，这一格的读数与 §12.7 C 末尾那份要一起重看。");
        else
            Assert.Equal(0, textDoor);
    }

    [Fact]
    public void Values_and_null_semantics_on_the_real_driver_are_the_offline_ones()
    {
        using var reader = RowOn("[Id], [Amount], [CreatedAt], [CustomerName], [State]");

        // <b>第七家在这一句上换的仍是"等号两边"，而且红的形状与下面那格是同一颗</b>：本家的
        // <c>INTEGER</c> 列交回装箱 <c>Int64</c>（<see cref="Database.IntClrType"/>，三处不同来源同档
        // 【实测·211 ∶ 310 ∶ 515】），读侧的门按契约窄化成 <c>int</c> —— 于是原句在这里是
        // <c>Expected: 1 / Actual: 1</c>，与下面那格在第五家打出来的那对红同一件事（两颗数长得一样而型不同）。
        // 本家这一臂比的是<em>数</em>：把驱动那颗箱展开成 <c>long</c>，与门窄化出来的那颗再抬回 <c>long</c>，
        // 两边同型可比。<b>它比原句弱在哪</b>：弱在没有拿"两颗是同一个型"当判据 —— 而那一半并没有丢，
        // 它就钉在上面那条用例的第一句里（<c>GetFieldType</c> 按 <c>IntClrType</c> 比），两条合起来仍是
        // "驱动给了什么箱 + 门给了什么值"这一对完整读数，只是分在两句里各钉一颗。
        if (Database.Family == Database.Target.Sqlite)
            Assert.Equal(Convert.ToInt64(reader.GetValue(0)), (long) ValueConverter.Read<int>(reader, 0));
        else
            Assert.Equal(reader.GetValue(0), ValueConverter.Read<int>(reader, 0));

        // <b>这一句在第五家换的是<em>等号两边</em>，不是宽容度</b>：同一列 <c>NUMERIC(18,4)</c>，第一家交回
        // <c>decimal</c>（于是"门与 <c>GetValue</c> 同值"就是一句等号），第五家交回 <c>string</c>
        // （A-01 逐字量到 <c>"12.3456"</c>，与 F-46b 同一件事的第二次量）。原句在那里红的两行打印出来是
        // <c>Expected: 12.3456 / Actual: 12.3456</c> —— 一颗 <c>string</c> 对一颗 <c>decimal</c>，
        // 字符串表示一模一样而<em>不是同一个型</em>，那种红读起来像"转换错了"，真相是"没有可对照的那一颗"。
        // 所以这一家改问同一句话的可执行形式：门给的 <c>decimal</c> 与把那串按不变文化直接解析，两路同值。
        // 它比原句弱在哪也要说明：弱在没拿驱动当对照，而这一家的驱动今天没有那颗 decimal 可当对照。
        //
        // <b>第七家走的是同一臂，而它交回的那一串逐字不同于第五家</b>（缝里 <see cref="Database.MoneyClrType"/>
        // 那段：本家的钱列在 DDL 里就是 <c>TEXT</c>，驱动读回 <c>string</c>【实测·4810 ∶ 4813】，
        // 而"直绑 <c>decimal</c>"会落成一串被驱动抹掉尾零的字，所以 ORM 侧自己先折不变形文本）。
        // 两家的<em>判据</em>是同一句（门 == 把那串按不变文化解析），两份<em>字</em>不同，因此共用一条臂而各带出处。
        if (Database.Family is Database.Target.Xugu or Database.Target.Sqlite)
            Assert.Equal(decimal.Parse((string) reader.GetValue(1), CultureInfo.InvariantCulture),
                ValueConverter.Read<decimal>(reader, 1));
        else
            Assert.Equal(reader.GetValue(1), ValueConverter.Read<decimal>(reader, 1));

        // <b>这一句在第七家换的仍是"等号两边"，而且换的方向与上面那格同一条</b>：本家的时刻列是
        // <c>TEXT</c>（<c>SqliteTypes</c> 第五条），驱动读回一颗 <c>string</c> 而不是 <c>DateTime</c>
        // 【实测·4810 ∶ 4813，两条读数里 <c>GetFieldType</c> 与 <c>GetValue</c> 同型，都是 <c>String</c>】——
        // 也就是本家<em>连</em>那一家"驱动自己写进去的 ISO 文本"都不还原成时刻。
        // 于是这一臂与上面两臂同一式：把驱动那串按不变文化解析，与门的读数比。这里还有一个只在本家出现的
        // 形状：<b>那一串的宽度是变的</b>【实测·4811：带毫秒的行 23 字符、整秒的行 19 字符，
        // 同一轮 4805 两行并排】，所以"解析得动"这件事本身就是这一臂在量的东西，而不是它的前提。
        if (Database.Family == Database.Target.Sqlite)
            Assert.Equal(DateTime.Parse((string) reader.GetValue(2), CultureInfo.InvariantCulture),
                ValueConverter.Read<DateTime>(reader, 2));
        else
            Assert.Equal(reader.GetValue(2), ValueConverter.Read<DateTime>(reader, 2));

        Assert.Equal(reader.GetValue(3), ValueConverter.Read<string>(reader, 3));
        // 枚举那一格是本文件唯一一句<em>不按家族改形式</em>的断言，而它为什么不用改值得写一句：
        // 本家的 <c>State</c> 存成 <c>INTEGER</c>、驱动交回 <c>Int64</c>【实测·4810】，两边又都从
        // <c>ValueConverter.To&lt;OrderState&gt;(object)</c> 同一颗入口进（<c>Materializer.cs:47-48</c> 那一臂
        // 先 <c>Convert.ToInt64</c> 再 <c>Enum.ToObject</c>，本来就吃任何宽度的整数箱），
        // 于是"装箱宽度"这一档在两句之间被折掉了。<b>这一句的绿在本家是这一跑才有的</b>——
        // 前三跑它根本没被执行到（同一句用例红在上面的钱列那一格）。
        Assert.Equal(ValueConverter.To<OrderState>(reader.GetValue(4)), ValueConverter.Read<OrderState>(reader, 4));

        // NULL → 声明类型自己的默认值（§10.1 的 ruling）在真驱动上同样成立：0 与 null 各一句，
        // 只断一句的话另一条分支被删也照样绿。
        using var nulls = RowOn($"NULL, NULL, NULL, NULL, NULL");

        Assert.Equal(0, ValueConverter.Read<int>(nulls, 0));
        Assert.Null(ValueConverter.Read<int?>(nulls, 0));
        Assert.Null(ValueConverter.Read<string>(nulls, 1));
    }

    /// <summary>第七笔（真库 ORM 级耗时）在真驱动上补的那一格：<b>臂与门在同一列上的差</b>。
    ///
    /// <para>上面两条用例问的都是"门有多贵"，这一条问它对面那一句：原始访问器在真驱动上是不是
    /// <em>真的</em>一分箱子都不往堆上放，以及"这一列落不落臂"在真驱动上是不是仍然只由
    /// <c>GetFieldType</c> 说了算。这两问在此之前只有探针的读数（<c>obj/probe-real/</c>，
    /// 【实测·v38-7-real-…-6 ∶ -8】）而没有钉子，而探针是要人读日志的、用例不是。</para>
    ///
    /// <para>这一条跑出来的第一颗读数就改了判词：<b>"臂零装箱"在真库上只成立一半</b>——
    /// ORM 这一侧确实不再为那一格造盒（臂把门的 <c>GetValue</c> + 换型整段换掉），
    /// 可"那一格总共省不省"还要看驱动的原始访问器自己分不分东西：T-SQL 与 postgresql 三型全 0 B，
    /// mysql 只有整数列 0 B（它的 <c>GetDecimal</c> ∶ <c>GetDateTime</c> 各内部分 32 ∶ 24 B），
    /// 虚谷一个都不省（整数列 32 B、时刻列 40 B，但它的字符串列反而比门少 24 B）。
    /// 所以这里的通用判据是<b>臂不贵于门</b>，而 <c>0 B</c> 只在量到过的那几族按族钉。</para>
    ///
    /// <para><b>判据仍然只取字节不取毫秒</b>，与这个文件从头到尾同一条口径。那一档"臂比门快多少 ns"
    /// 在真库上两跑之间符号会翻（§12.8 第 3 行末段记着），钉在这里只会得到一颗随时会红的假钉子；
    /// 而字节是分配数，与机器负载无关。</para></summary>
    [Fact]
    public void The_arm_and_the_door_differ_by_exactly_the_box_on_the_real_driver()
    {
        using var reader = RowOn("[Id], [Amount], [CreatedAt], [CustomerName]");

        Type[] declared = [typeof(int), typeof(decimal), typeof(DateTime), typeof(string)];
        string[] names = ["Id", "Amount", "CreatedAt", "CustomerName"];

        var ops = new byte[declared.Length];

        for (var i = 0; i < declared.Length; i++)
        {
            ops[i] = ReadOps.Probe(reader, i, declared[i]);

            // ① <b>落不落臂的唯一判据，在真驱动上的原样形式</b>（§16.21 那句"驱动交回的类型与成员
            // 声明的类型<em>是同一个</em>才直读"）。这一句五族都跑，而且它是下面两句的前提：
            // 它错了，"臂 0 B"就只是在量一条没人走的臂，而那条臂永远不会被读到。
            // 本家把它读成真的那一格是虚谷的钱列（<c>NUMERIC</c> 回 <c>string</c> ⇒ 落门）与
            // 第七家的整数列（<c>INTEGER</c> 回 <c>Int64</c> ⇒ 落门），两处都不需要在这里再登记"哪家怎么回"。
            Assert.Equal(reader.GetFieldType(i) == declared[i], ops[i] != ReadOps.Boxed);
        }

        // ② <b>这一格是这一条用例真正的新读数，也是它一跑就改了判词的那一格</b>。
        // 原写法是"有臂的值类型列一次取值 0 B"，五族里三家（T-SQL ∶ postgresql ∶ mysql 的整数列）当场绿，
        // 而 mysql 的钱列交回 32 B、虚谷的整数列交回 32 B —— 那一颗不是盒子，是<em>驱动自己的类型化访问器
        // 内部分配的</em>（探针按值那一档早就把这两格印出来了：【实测·v38-7-real-mysql-8 ∶ -xugu-8，
        // 臂那一列逐列读 0 ∶ 32 ∶ 24 ∶ 40】）。所以"臂零装箱"这句在真库上必须拆成两半才成立：
        // <b>ORM 这一侧不再为那一格造盒</b>（臂把门的 <c>GetValue</c> + 换型整段换掉），
        // 而<em>那一格总共省不省</em>还要看驱动的原始访问器自己分不分东西 —— 分，就省不到 0。
        // 于是通用判据只能取这一条方向：<b>臂不贵于门</b>。它钉得住"没人能把臂改成更贵的那一路"，
        // 又不把某一版驱动的缓冲形状钉成约定（本文件从头到尾那条"不钉 8 152"的纪律）。
        // 而 0 B 这一格照旧按族钉，各带自己的出处：
        if (ops[0] != ReadOps.Boxed)
        {
            NotEmpty(reader, 0, names[0]);

            var idDoor = PerCall(() => _ = ValueConverter.Read<int>(reader, 0));
            var idArm = PerCall(() => _ = DirectRead.ReadInt32(reader, 0, ops[0]));

            Assert.Equal((int) reader.GetValue(0), DirectRead.ReadInt32(reader, 0, ops[0]));

            Assert.True(idArm <= idDoor,
                $"整数列的臂（{idArm} B/次）比门（{idDoor} B/次）还贵：这一格走臂不再划算，" +
                "要么驱动的 GetInt32 换了行，要么臂被改成了另一条路 —— 两者的解法都是重跑探针再判，不是放开这一句。");

            // 本家的整数列：类型化访问器真的 0 B（【实测·v38-7-real-mssql-8 ∶ -mysql-8 ∶ -postgresql-8 各 0 B】）。
            if (Database.Family is Database.Target.SqlServer or Database.Target.MySql or Database.Target.PostgreSql)
                Assert.Equal(0, idArm);
        }

        if (ops[1] != ReadOps.Boxed)
        {
            NotEmpty(reader, 1, names[1]);

            var moneyDoor = PerCall(() => _ = ValueConverter.Read<decimal>(reader, 1));
            var moneyArm = PerCall(() => _ = DirectRead.ReadDecimal(reader, 1, ops[1]));

            Assert.Equal((decimal) reader.GetValue(1), DirectRead.ReadDecimal(reader, 1, ops[1]));

            Assert.True(moneyArm <= moneyDoor,
                $"钱列的臂（{moneyArm} B/次）比门（{moneyDoor} B/次）还贵：同上那句的判据要一起重看。");

            // 只有这两家读到 0：本家的钱列在库里就是原生 decimal 且驱动一次到位
            // （【实测·v38-7-real-mssql-8 ∶ -postgresql-8 臂 0 B】）。第五族<em>不</em>在这一支——
            // MySqlConnector 的 <c>GetDecimal</c> 每调用自己造 32 B，与那颗箱同宽，于是臂在这一格一分不省
            // （【实测·v38-7-real-mysql-8 臂 32 B】），虚谷的钱列根本没有臂（①里那条）。
            if (Database.Family is Database.Target.SqlServer or Database.Target.PostgreSql)
                Assert.Equal(0, moneyArm);
        }

        if (ops[2] != ReadOps.Boxed)
        {
            NotEmpty(reader, 2, names[2]);

            var momentDoor = PerCall(() => _ = ValueConverter.Read<DateTime>(reader, 2));
            var momentArm = PerCall(() => _ = DirectRead.ReadDateTime(reader, 2, ops[2]));

            Assert.Equal((DateTime) reader.GetValue(2), DirectRead.ReadDateTime(reader, 2, ops[2]));

            Assert.True(momentArm <= momentDoor,
                $"时刻列的臂（{momentArm} B/次）比门（{momentDoor} B/次）还贵：同上那句的判据要一起重看。");

            // 同一支判据，同一颗因：本家这两家的驱动 <c>GetDateTime</c> 不分配（臂 0 B），
            // 而第五族那一格是 24 B、第四家在虚谷是 40 B —— 都是驱动自己的换算，不是盒子
            // （【实测·v38-7-real-mssql-8 ∶ -postgresql-8 臂 0 B 对 -mysql-8 臂 24 B 对 -xugu-8 臂 40 B】）。
            if (Database.Family is Database.Target.SqlServer or Database.Target.PostgreSql)
                Assert.Equal(0, momentArm);
        }

        // ③ 字符串那一臂<b>只钉方向、不钉 0</b>，而这不是偷懒：这一格上"门装箱、臂不装"根本不同型 ——
        // 门交回的那颗串本来就是驱动现造的（§12.7 C 末尾那份四族读数：mysql ∶ postgresql ∶ sqlite 的
        // 文本列门各 32 B 起、虚谷 8 152 B），臂走 <c>GetString</c> 也要同一颗串，
        // 所以"省一颗箱"在这里无从量起（探针按值那一档量到的就是"字符串列三式同宽"，虚谷只少那 24 B）。
        // 能钉的仍是那两句：读出来是同一颗串，且臂不贵于门。
        if (ops[3] != ReadOps.Boxed)
        {
            var textDoor = PerCall(() => _ = ValueConverter.Read<string>(reader, 3));
            var textArm = PerCall(() => _ = DirectRead.ReadString(reader, 3, ops[3]));

            Assert.Equal((string) reader.GetValue(3), DirectRead.ReadString(reader, 3, ops[3]));

            Assert.True(textArm <= textDoor,
                $"文本列的臂（{textArm} B/次）比门（{textDoor} B/次）还贵：驱动换行了，" +
                "这一格的读数与 §12.7 C 末尾那份要一起重看，别放开这一句。");
        }
    }

    /// <summary>第八笔（结 §14 债③ 的那一支：<b>标量与聚合出口也领一张票</b>）在真驱动上的三句。
    ///
    /// <para>上面那条问的是"实体那一档的臂值不值"，这一条问的是同一批列换到标量出口上还剩什么：
    /// ① <b>落不落臂的判据没有换形</b> —— 仍然只由 <c>GetFieldType</c> 与请求型是否同型决定，
    /// 所以本家的整数列（回 <c>Int64</c>）与虚谷的钱列（回 <c>string</c>）在这一档同样自动落门；
    /// ② <b>出口与门在同一格上同值</b>；③ <b>一次取值不贵于门</b>，而落门那一支必须<em>逐字节等于</em>门
    /// （那一支今天仍然只是"第 0 列进收容所"那一句，改动没有给它加东西）。</para>
    ///
    /// <para><b>这一条比上面那条多出来的一截是空值守卫</b>：非空值类型那一族的臂不问 <c>IsDBNull</c>（第七笔丁），
    /// 而标量出口的请求型 <c>T</c> 不携带可空性（空集上的 <c>SUM</c> 交回 NULL 是合法答案），
    /// 所以守卫由出口来问 —— 每值多一趟驱动调用。上面那条的 <c>0 B</c> 是按<em>臂</em>量的，
    /// 这里按<em>出口</em>重问一遍：那一截守卫在量到过 0 B 的三族上确实不添堆（否则本条会红在具体的数字上），
    /// 而<b>虚谷那一族它是添的</b>（<c>IsDBNull</c> 自己每值 32 B，探针 三b【实测·v38-8-real-xugu-2】），
    /// 所以这一族上本条判的是"出口仍然低于门"那句方向，判不到"出口 0 B"。
    /// 空集 <c>SUM</c> 交回 0 这件事本身的真库见证在 <c>ReadPathTests</c> 的聚合用例，不在这里（这里不建模型）。</para></summary>
    [Fact]
    public void The_scalar_exit_on_the_real_driver_never_costs_more_than_the_door()
    {
        // 第三颗参数是"这一型在本族上量到过臂 0 B"的那些家族（出处见上面那条用例的【实测·v38-7-real-*-8】）。
        Exit<int>("[Id]", "Id", [Database.Target.SqlServer, Database.Target.MySql, Database.Target.PostgreSql], 24);
        Exit<decimal>("[Amount]", "Amount", [Database.Target.SqlServer, Database.Target.PostgreSql], 32);
        Exit<DateTime>("[CreatedAt]", "CreatedAt", [Database.Target.SqlServer, Database.Target.PostgreSql], 24);
        Exit<string>("[CustomerName]", "CustomerName", [], 0);
    }

    /// <summary>一列一型的一档：把要量的那一颗列放在第 0 位（<see cref="Materializer.ScalarRows{T}"/> 读的就是第 0 列），
    /// 其余列只是把这一句撑成一条正常的分页读。<b>填场那一颗用 <c>[Note]</c> 而不是 <c>[Id]</c></b>：
    /// 这一档要量的列里有 <c>[Id]</c> 本身，同一颗在 SELECT 列表里出现两次，SQL Server 直接报
    /// 「列名 'Id' 不明确」（第一跑就红在那一句上，量具与断言一句都没跑到）；<c>[Note]</c> 与这一批的
    /// 四颗目标列都不重名，而它只是 <c>WHERE Note = tag</c> 那一列，读回来是常量、不影响第 0 列的量法。</summary>
    static void Exit<T>(string column, string name, Database.Target[] zeroOn, double boxFloor)
    {
        using var reader = RowOn($"{column}, [Note]");

        var armed = ReadOps.Probe(reader, 0, typeof(T)) != ReadOps.Boxed;
        var read = Materializer.ScalarRows<T>();

        // ① 判据在这一档没有换形：五族各有各的回法，这里不登记"哪家怎么回"。
        Assert.Equal(reader.GetFieldType(0) == typeof(T), armed);

        // ② 值与门逐格相同。落门那一支这句是同义反复（出口就是那一句），有臂那一支才是新东西。
        Assert.Equal(ValueConverter.Read<T>(reader, 0), read(reader));

        var door = PerCall(() => _ = ValueConverter.Read<T>(reader, 0));
        var exit = PerCall(() => _ = read(reader));

        // <b>读数要进日志</b>：这一批台账那一格要的是五家各自那四个数，而断言只在红的时候才开口 ——
        // 绿的时候不留痕迹，那一格就只能写成"跑过了"。打印的只有族名、列名、型名与字节数，
        // 连接串与口令一个字都不出（§13.1 第 4 条同一口径）。
        Console.WriteLine($"标量出口 ∶ {Database.Family} ∶ {name}（{typeof(T).Name}）∶ {(armed ? "落臂" : "落门")} ∶ 门 {door} B/值 ∶ 出口 {exit} B/值");

        Assert.True(exit <= door,
            $"真驱动上标量出口读 {name} 比门还贵（{door} → {exit} B/值）：{typeof(T).Name} 这一型" +
            "要么驱动的原始访问器换了行，要么出口被改成了另一条路。两者的解法都是重跑再看，不是放开这一句。");

        if (armed)
        {
            // 引用型那一格两侧都是 0（串本来就是驱动现造的），所以那句"等于 0"在它身上无从判起，
            // 上面那句方向判据就是它的全部判据 —— 与上面那条用例的字符串那一臂同一条口径。
            if (zeroOn.Contains(Database.Family) && typeof(T) != typeof(string))
            {
                Assert.Equal(0, exit);

                // 装箱口在这一档仍然是装箱口：它没有因为"出口有臂"而被改掉（§12.7 C 末尾那份读数的口径）。
                Assert.True(door >= boxFloor,
                    $"{typeof(T).Name} 的门在这一家不再付装箱那一份（实测 {door} B/值，下界 {boxFloor}）：量具或驱动换了行。");
            }
        }
        else
            // 落门 ⇒ 出口与门是同一句读数，一分不多。这一句比上面那句"不贵于"更硬，而它撑得住：
            // 这一支里 <c>op == ReadOps.Boxed</c> 直接 return 门，守卫与臂都没参与。
            Assert.Equal(door, exit);
    }

    /// <summary>非空值类型的臂现在不再问 <c>IsDBNull</c>（第七笔丁），所以空值上它<b>抛</b>而不是给
    /// <c>default</c>。这一句先把那颗种子行钉成非空：否则用例红成一串 <c>InvalidCastException</c>，
    /// 读起来像"臂坏了"，真相是种子换了形。</summary>
    static void NotEmpty(DbDataReader reader, int ordinal, string name)
        => Assert.False(reader.IsDBNull(ordinal),
            $"种子行的 {name} 这一格是空值：非空值类型的臂已不再问 IsDBNull（第七笔丁的改判），" +
            "空值上它抛 InvalidCastException，那不是这一条用例要报的错。要么改种子，要么把这一列从本用例的列单里拿掉。");
}
