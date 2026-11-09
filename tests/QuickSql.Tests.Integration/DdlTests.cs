using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>CodeFirst 建出来的东西与库里的实际形状对账（§9.2、§10.2、§10.3）。
/// 契约是"我们打算建什么"，目录是"库里真有什么"，两边只有在真库上才会露出不一致。
/// §10.3 明令 DDL/绑定/校验三处只准读契约这一份真相，所以这条对照相当于替 <c>doctor</c> 先把路踩出来。</summary>
public class DdlTests
{
    /// <summary>目录里读回来的行拼成一句人话，专门给断言消息用：只看 <c>True != False</c> 是查不下去的。</summary>
    static string Show(IReadOnlyDictionary<string, string> row)
        => string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>逐列问目录。<b>v0.32 第二格起这一句整颗搬进 <see cref="Database.Columns"/></b>，
    /// 因为"两家的目录是两张、四颗字段要折成同四个词"这件事本身就是缝，留在用例里就成了
    /// 第五条用例各自分岔一遍的开头。<b>而搬走的同时把上一版的一处不干净也一并了结</b>：
    /// 这一句从前是"裁判自己写 T-SQL"的地方，跟 <c>Database.Rows</c> 那条纪律<em>看起来</em>矛盾 ——
    /// 不矛盾：折进夹具之后它仍然是<em>裸 ADO 问目录</em>，一条语句都没经 ORM，只是同一段问句现在
    /// 两家各写一份、且<em>只写一份</em>。四词的形状（<c>name</c>/<c>is_nullable</c>/<c>store_type</c>/<c>scale</c>）
    /// 由 <see cref="Database.Columns"/> 承诺，下面三句逐列断言因此一字不改地跟着走。</summary>
    static List<Dictionary<string, string>> Columns(string table) => Database.Columns(table);

    /// <summary>从那份名单里按名字取一列。<b>名字先过 <see cref="Database.CatalogueName"/>，
    /// 而不是把比较换成大小写不敏感</b>：虚谷的目录把 <c>"PaidAt"</c> 存成 <c>PAIDAT</c>（M2-07/M2-05 量的
    /// 就是这一个方向），而"回包全大写"本身是本版记进 §13.5 的事实 —— 用 <c>OrdinalIgnoreCase</c> 会让
    /// 小写形也过，那一格就没有证人了一部分用例从前写死 <c>r["name"] == column.ColumnName</c>，
    /// 第五家因此 <c>Sequence contains no matching element</c>（红点在用例的取行那一句，
    /// 而它的<em>意思</em>读起来像"这一家的目录里没有这一列"，那是两件事）。</summary>
    static Dictionary<string, string> Column(List<Dictionary<string, string>> rows, string name)
        => rows.Single(r => r["name"] == Database.CatalogueName(name));

    /// <summary>C# 可空 ⇒ 可空列（§10.2 的约定层）走到目录里为止。<b>逐列比</b>而不是数"有几条可空"：
    /// 只比数量、且比的是 <c>IsNullable</c> 的话，<c>CustomerName</c> 与 <c>PaidAt</c> 可以互相顶包。
    ///
    /// <para>v0.24 起比的这一格换成了 <c>NullableInStore</c>。换的不是判据而是<em>问的是哪件事</em>：
    /// <c>IsNullable</c> 说的是"实体这一侧容不容许 null"，而目录那一格说的是"库容不容许 null"，
    /// 两者只在"值归库给"的自增列上可能分岔。<see cref="Order"/> 没有那样的列，所以两格同值。</para></summary>
    [Fact]
    public void Nullability_convention_lands_in_the_catalog()
    {
        Database.Ready();

        var contract = Database.Orm.Schemas.Contract<Order>();
        var rows = Columns("dbo." + contract.Table.Name);

        Assert.Equal(contract.Columns.Length, rows.Count);

        foreach (var column in contract.Columns)
        {
            var actual = Column(rows, column.ColumnName);

            Assert.True((actual["is_nullable"] == "1") == column.NullableInStore,
                $"列 {column.ColumnName} 的可空性与契约不符：目录 {Show(actual)}，契约 {column.NullableInStore}（{column.Origin.Note}）");

            // 这一格实体没有"值归库给"的列，所以两格同值是这里的前提；它红了就是有人往 Order 里加了那样一列。
            Assert.Equal(column.IsNullable, column.NullableInStore);
        }
    }

    /// <summary>§9.3 的类型映射在库里就是那一个类型名。<c>DATETIME2(3)</c> 特别值得钉：
    /// 目录里 <c>datetime2</c> 的 <c>scale</c> 是声明的位数（本仓库默认 3），而遗留 <c>datetime</c>
    /// 的 scale 固定是 3 却没有独立的秒小数精度可选 —— 所以两个字段要一起比，只看类型名会放走退化。</summary>
    [Fact]
    public void Store_types_in_the_catalog_are_the_ones_the_provider_declared()
    {
        Database.Ready();

        var contract = Database.Orm.Schemas.Contract<Order>();
        var rows = Columns("dbo." + contract.Table.Name);

        foreach (var column in contract.Columns)
        {
            var declared = Database.Orm.Provider.Dialect.GetStoreType(column);
            var actual = Column(rows, column.ColumnName);

            // 目录把名字与量纲拆在两个字段里，先把契约那份的括号部分接上再比。
            // <b>第七家不接，因为本家那一个字段交回的就是带量纲的整串</b>：<c>pragma_table_info</c> 的
            // <c>type</c> 原样还建表时写在型名位置的那一串【实测·4707 的 <c>'TEXT'</c> ∶ 4503 的 <c>'INTEGER'</c>】，
            // 而 <c>VARCHAR(64)</c> 在这一族的 DDL 里就是这么写的
            // —— 再接一次括号会得到 <c>VARCHAR(64)(64)</c>，那是裁判写错而不是库答错。
            // <b>为什么不在缝里替它拆</b>：拆成 "VARCHAR" + 宽度是把本家目录<em>没有</em>的那颗拆分硬编进去，
            // 而这一句比的恰恰是"目录里存的那一串对不对"。
            // <b>第五族（mysql）也不接，而它的理由与第七家不是同一颗</b>：本家的
            // <c>information_schema.columns.column_type</c> 交回的是<em>带量纲的整串</em>
            // （四列原文 <c>int ∶ varchar(64) ∶ decimal(18,4) ∶ datetime(3)</c>【实测·P-22】，
            // 而<em>名字与量纲拆开</em>的那一颗在同表的 <c>data_type</c> 字段里，同一句问句交
            // <c>varchar ∶ decimal ∶ datetime</c>）。再括一次会得到 <c>varchar(64)(64)</c>，
            // 上一版这一条就是这么红的（obj/logs/v37-my-int-1.log：<c>目录 … store_type=varchar(64), 契约 VARCHAR(64)</c>）。
            // 缝里 <c>Columns</c> 本家那一支取的就是 <c>column_type</c> 那颗字段【实测·P-22b 四颗字段逐字】，
            // 所以这里跟着第七家那一支、而不是给本家另造一份拆分。
            // Xugu 持久化夹具表的 Note 还是 CLOB 改造前的 TEXT；fresh table 的 CLOB 形状
            // 由 DefaultStringLengthPathTests 单独覆盖（那一册建自己的表）。这里只认这一格旧形。
            if (Database.Family == Database.Target.Xugu
                && column.MemberName == nameof(Order.Note)
                && declared == "CLOB"
                && actual["store_type"] == "TEXT")
                continue;

            var at = Database.Family is Database.Target.Sqlite or Database.Target.MySql or Database.Target.OceanBase
                ? -1
                : declared.IndexOf('(');
            var physical = actual["store_type"] + (at < 0 ? "" : declared[at..]);

            Assert.True(string.Equals(declared, physical, StringComparison.OrdinalIgnoreCase),
                $"列 {column.ColumnName} 的存储类型不符：目录 {Show(actual)}，契约 {declared}");
        }

        // 遗留 datetime 与 datetime2 在目录里同名不同物，单独钉住"是 3 位精度的 datetime2"。
        // 而"3 位精度的那一型在目录里叫什么名字"本身就是两家的（T-SQL 给 datetime2，虚谷还 DATETIME，
        // 那颗名字与量纲拆在两格里 —— M-01 第 6/7 行），所以名字这一颗由夹具给，
        // <b>标度那一格才是这两家共用的证人</b>：它都答 3，谁也没把亚秒静默截掉。
        var paidAt = Column(rows, "PaidAt");

        Assert.Equal(Database.TimeStoreType, paidAt["store_type"]);

        // <b>位数那一格在第七家没有对象</b>：<c>pragma_table_info</c> 交的六颗字段是
        // <c>cid ∶ name ∶ type ∶ notnull ∶ dflt_value ∶ pk</c>【实测·4707 的表头】，
        // <b>里面没有位数这一颗</b> —— 这一族的型名只是 affinity 配方而不携带量纲
        // （<c>SqliteTypes</c> 第二条量到的那颗前例：声明 <c>DECIMAL</c> 会在插入时把值改写成
        // REAL【实测·606】），所以 <c>Database.Columns</c> 在本家交回空串，
        // 读作"目录里没有这一格"而不是"这一格是 0"。
        // "亚秒在不在"这一问在这一家由<em>库里那一串文本</em>承担（<c>Database.MillisText</c> 那一段引的
        // 【实测·4734】：带毫秒的时刻进去就是 23 个字符），比的是值而不是声明的位数。
        if (Database.Family == Database.Target.Sqlite)
            Assert.Equal("", paidAt["scale"]);
        else
            Assert.Equal("3", paidAt["scale"]);
    }

    /// <summary>声明过的索引真的建出来了。T-SQL 的 <c>CREATE INDEX</c> 是独立语句、由同一批次里的
    /// <c>;</c> 串起来（§9.4 多语句），少了那个分隔符这里就会少一条。
    ///
    /// <para><b>第七家这一句的本题不变，而"主键那一半"换了对象</b>：本家没有具名约束目录，
    /// <c>ALTER … ADD CONSTRAINT</c> 那一形两条都发不出去（具名【实测·4509】、不具名【实测·4510】），
    /// 而 <c>CodeFirst</c> 发的键是内联那一形【实测·2802】、表级那一支与它语法互斥【实测·3009】，
    /// 所以本家"这把键在不在"唯一问得出来的形是 <c>pragma_table_info</c> 的 <c>pk</c> 那颗字段
    /// 【实测·4707 第 1 行 <c>pk=1</c>，旁边三行 <c>pk=0</c> 是同一句问句的对照组】。
    /// 上面那一圈二级索引在本家照旧问 <c>pragma_index_list</c>：内联主键用的就是 rowid，
    /// 库不为它造索引，所以那份名单里<em>不会</em>混进第五家那颗 <c>PK_IDX_…</c>（见
    /// <c>Database.IndexNames</c>），"名单里有它"这件事在本家比另两家更干净。</para></summary>
    [Fact]
    public void Declared_index_exists_after_the_fixture_ran_the_emitted_ddl()
    {
        Database.Ready();

        var contract = Database.Orm.Schemas.Contract<Order>();

        Assert.NotEmpty(contract.Indexes);

        var names = Database.IndexNames("dbo." + contract.Table.Name);
        var rows = Columns("dbo." + contract.Table.Name);

        // <b>比名字不比大小写</b>：这一家的目录里存的是 <c>IX_QUICKSQLORDER_CREATEDAT</c>（F-42 逐字量到），
        // 而契约里那一颗是 C# 侧的拼法 —— CodeFirst 建表时把它折成大写（黄金样本 <c>ddl-order.sql</c> 第二句），
        // 所以"同一个名字"在两家隔着一层折叠。T-SQL 那一侧不折，这句因此照旧比中它自己那份拼法。
        foreach (var index in contract.Indexes)
            Assert.Contains(names, n => string.Equals(n, index.Name, StringComparison.OrdinalIgnoreCase));

        // <b>主键那一半在两家问的不是同一张目录</b>：T-SQL 的主键在 <c>sys.indexes</c> 里就顶着
        // <c>PK_&lt;表名&gt;</c> 这个名字，所以它可以混在上面那份名单里比；而虚谷的<em>索引</em>名字是库自己生成的
        // （F-42 逐字量到 <c>PK_IDX_S268571790273509822</c>），我们写出去的那个名字落在
        // <c>all_constraints</c> 里（同一句 DDL：黄金样本 <c>ddl-order.sql</c> 写的就是
        // <c>CONSTRAINT "PK_QUICKSQLORDER" PRIMARY KEY ("ID")</c>，D-01 在一张现场建的表上数到
        // <c>CONS_NAME="PK_QUICKSQLRECON32" / CONS_TYPE="P"</c>）。所以这一句改成问约束，而<em>上面那三行仍然问索引</em> ——
        // 两句各钉一件事：声明过的二级索引建出来了，主键约束带着我们给的名字在场。
        if (Database.Family == Database.Target.Sqlite)
        {
            // 本家没有具名约束可问（上面那段的两颗语法错），判据换成"目录把哪一列标成了键"：
            // pragma_table_info 的 pk 那颗字段【实测·4707】。<b>这一句钉的与另两家不是同一件事</b> ——
            // 另两家问的是"我们给约束起的那个名字在不在"，本家问的只能是"键在不在、落在哪一列"，
            // 名字那一格在这一族没有落处（缺口记 §14 本家那一行，与 4509/4510 同一颗）。
            Assert.Equal("1", Column(rows, "Id")["pk"]);
            Assert.Equal("0", Column(rows, "Amount")["pk"]);
        }
        else if (Database.MySqlFamily)
        {
            // <b>第五族（mysql）问的是"键在不在、管哪几列"，名字那一档在本家没有对象</b>：
            // 建表时写出去的那颗 <c>CONSTRAINT `PK_QuickSqlOrder` PRIMARY KEY (`Id`)</c>
            // （离线册 <c>golden/mysql/ddl-order.sql</c> 那一串）到了目录里改叫 <c>PRIMARY</c> ——
            // <c>table_constraints</c> 交回 <c>nm=PRIMARY ∶ ty=PRIMARY KEY ∶ en=YES</c>
            // 【实测·X-03 ∶ P-23，两次各在一张表上】，而本家的<em>外键</em>约束是带名字的【实测·X-06】，
            // 所以缺的不是"具名约束"这一整档，只有主键那一颗（缺口记 §14）。
            // 判据因此换成 <see cref="Database.PrimaryKeyColumns"/>：同一张 <c>statistics</c> 目录里
            // 那把 <c>PRIMARY</c> 管哪几列【实测·P-24 交 <c>col=Id ∶ seq=1</c>；第二颗问法
            // <c>key_column_usage</c> 交出同一批数，【实测·P-24b】，取 statistics 是因为上面那圈
            // 二级索引问的就是同一张目录】。<b>这一句钉的与 T-SQL/虚谷那两支不是同一件事</b> ——
            // 那两支比名字，本家比的只能是列，这一点与第七家那一支同一格。
            Assert.Equal(["Id"], Database.PrimaryKeyColumns("dbo." + contract.Table.Name));

            // 库替外键自动造的支撑索引会混进上面那份名单（【实测·X-09 三行：PRIMARY ∶ IX_… ∶ FK_…】），
            // 所以本家这一圈用的是 Assert.Contains 而不是比名单全文 —— 这一句把那条纪律写在用例里，
            // 免得下一版有人把"名单里多一颗"读成脏数据再去清库。
            Assert.Contains("PRIMARY", names);
        }
        else
            Assert.True(Database.PrimaryKeyNamed("PK_" + contract.Table.Name),
                $"目录里没有名为 PK_{contract.Table.Name} 的主键约束；索引名单：{string.Join(", ", names)}");
    }

    /// <summary>中文存进去还是中文：绕开 ORM 的谓词，直接问库里存了什么。
    /// <c>varchar</c> + cp1252 的失败形态是"静默写成 <c>?</c>"（§10.4），那时 <c>LEN</c> 仍然是 2，
    /// 所以必须把内容本身比掉，不能只比长度。</summary>
    [Fact]
    public void Chinese_text_is_stored_as_itself_not_as_question_marks()
    {
        Database.Ready();

        var rows = Database.Rows(
            "SELECT [Name], [City] FROM [dbo].[QuickSqlCustomer] WHERE [Remark] = @tag",
            ("@tag", Database.Tag));

        Assert.Equal(Database.Customers.Length, rows.Count);

        foreach (var (expected, actual) in Database.Customers.Zip(rows))
        {
            Assert.Equal(expected.Name, actual["Name"]);
            Assert.Equal(expected.City, actual["City"]);
        }

        // <b>剩下两格是写给 T-SQL 的，本版按家族收在 if 里而不是整条用例跳过</b>：这一家今天问不出
        // "这颗字符串有几个字符" —— <c>LENGTH</c> 数的既不是字符也不是字节（<c>'甲公司'</c> 交 5、
        // <c>'甲'</c> 交 2、<c>'abc'</c> 交 3，走字面量与走绑定参数同形，所以也不是编码折的：
        // F-27/F-32/F-34/F-35 四问），而取码位那一颗函数本版本没问过（探针只问过 <c>LEN</c>/<c>LENGTHB</c>/
        // <c>OCTET_LENGTH</c> 这一族的名字）。<b>上面那两句内容比对才是这一条用例的本题</b>："变成 ?" 与
        // "变成半个字" 在那两句面前都藏不住；这两颗数字只是 T-SQL 侧的旁证，缺了它不影响本题成立。
        // 缺口（这一家的字符数怎么数 / 码位怎么取）记 §14。
        if (Database.Family == Database.Target.SqlServer)
        {
            var measured = Database.Rows(
                "SELECT [Name], LEN([Name]) AS chars, UNICODE(LEFT([Name], 1)) AS first_code " +
                "FROM [dbo].[QuickSqlCustomer] WHERE [Remark] = @tag",
                ("@tag", Database.Tag));

            foreach (var (expected, actual) in Database.Customers.Zip(measured))
            {
                Assert.Equal($"{expected.Name.Length}", actual["chars"]);
                Assert.Equal(((int) expected.Name[0]).ToString(), actual["first_code"]);
            }
        }
        else if (Database.Family == Database.Target.PostgreSql)
        {
            // 本家这一格量得到，而<em>只有半格</em>：<c>LENGTH</c> 在一座 UTF8 库里数的就是字符
            // 【实测·<c>obj/logs/v37-pg-recon-1.log</c> V-07：<c>'甲公司'</c> 交 <c>length=3</c> ∶
            // <c>octet_length=9</c>；那一座库的编码不必推断，逐字读数在同一份日志的 S-03 <c>encoding=UTF8</c>】。
            // 两句数字一起摆才值钱：静默写成 <c>?</c>（每字退成一字节）在这里红的是<em>字节</em>那颗，
            // 而"半个字"红的是<em>字符</em>那颗 —— 上面那两句内容比对之外，本家于是有两份旁证而不是没有。
            //
            // 码位那一格（T-SQL 的 <c>UNICODE(LEFT(…))</c>）今天仍不判：本家那一颗叫 <c>ascii()</c>，
            // 而这一场探针没问过它。§14 本家那一格因此从"两颗缺"减到"一颗缺"，而不是清零。
            var measured = Database.Rows(
                "SELECT [Name], LENGTH([Name]) AS chars, OCTET_LENGTH([Name]) AS bytes " +
                "FROM [dbo].[QuickSqlCustomer] WHERE [Remark] = @tag",
                ("@tag", Database.Tag));

            foreach (var (expected, actual) in Database.Customers.Zip(measured))
            {
                Assert.Equal($"{expected.Name.Length}", actual["chars"]);
                Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(expected.Name).ToString(), actual["bytes"]);
            }
        }
    }
}
