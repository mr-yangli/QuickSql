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
    /// 两者在"值归库给"的那一列上必然分岔（<see cref="Store_filled_tier_lands_in_the_catalog_through_the_emitted_ddl"/>）。
    /// <see cref="Order"/> 没有那样的列，所以这一格换了之后文本一字不变 —— 它留在这里的价值是"普通表的
    /// 两格同值"这一半，另一半在下面那格。</para></summary>
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

    /// <summary>「值归库给」那一档在<em>目录</em>里成立，而这张表是 CodeFirst 自己建的。
    /// 这一格是 §14 那一行（"CodeFirst 给并发列生成的 DDL 是 <c>rowversion NULL</c>"）的结案证词 ——
    /// 离线那份钉的是文本，只有这里能答"那句 DDL 在库里建出了什么"。
    ///
    /// <para><b>为什么是探针表而不是夹具那张 <c>QuickSqlInventoryItem</c></b>：跑过旧版夹具的机器上那张表已经在，
    /// <c>Database.EnsureInventoryItem</c> 的存在性分支会直接返回，于是"这一句 DDL 能不能执行"就永远判不到，
    /// 而它恰恰是这一版新写的那一句。换一个表名的同一形状（<c>rowversion</c> 的并发列 + 一格没人给值的并发列）
    /// 把那一格变成每次必跑。与 <c>TpPathTests</c> 那三张靶表同一手法：按契约建、常驻、表在就不动。</para>
    ///
    /// <para>顺带一提目录里那一列的<em>类型名</em>：<c>sys.types</c> 报的是 <c>timestamp</c>（那个遗留同义词，
    /// <c>ConcurrencyPathTests</c> 的 <c>Rows</c> 注释实测过）。所以本家的规则刻意按<em>契约里声明的那一份</em>
    /// （<c>rowversion</c>）认，不把 <c>timestamp</c> 列进来 —— 两侧名字不是一份，判据就不能写成一份。
    /// 这里不断言类型名，是因为它随实例的版本而变（各家 <c>sys.types</c> 的同义词表不同），
    /// 而它出现在 <see cref="Show"/> 里，红了直接看。</para></summary>
    [Fact]
    public void Store_filled_tier_lands_in_the_catalog_through_the_emitted_ddl()
    {
        Database.Ready();

        // <b>第四家（postgresql）在这一条用例上没有可判的对象</b>：本用例的题面是"那句 DDL 在库里建出了什么"，
        // 而那一句在本家<em>建不出表</em> —— 探针表带的还是 <c>[Column(TypeName = "rowversion")]</c>，
        // <c>PostgreSqlTypes.GetStoreType</c> 第一行那颗 <c>StoreTypeName</c> 短路照人写型名原样发出去，于是 <c>EnsureTable</c> 红在
        // <c>42704: type "rowversion" does not exist</c>【实测·<c>obj/logs/v37-pg-recon-1.log</c> D-04 逐字同一颗，
        // 本用例那一次红落在 <c>obj/logs/v37-pg-int-1.log</c> 第 28 行】。同形换 <c>bytea</c> 建得成（D-05），
        // 所以缺的是<em>那一型</em>而不是那句语法。
        //
        // <b>为什么这里收整条用例而不是收在 if 里</b>：上面那颗 <c>EnsureTable</c> 在臂分配之前，
        // 而本文件里"按家族收在 if 里"的那一处（<c>Chinese_text_is_stored_as_itself…</c>）的前提是本题的前两句跑得到 ——
        // 这一条连表都没有，前两句也无从跑起。跳过按用例点名、理由落进 skip 列表，这正是 §13.5 要的"没跑过"那一行。
        //
        // <b>缺口记 §14 本家那一格，而且它比"这一家没有那一型"大一格</b>：<c>XuguCodeFirst</c> 在 v0.34 第二笔
        // 为那颗型名立了拒收闸门（认型之后抛 <c>QuickSqlException</c>），<c>PostgreSqlCodeFirst</c> <em>没有</em> ——
        // 于是"人写的型名 provider 不校验"那条通例在本家发出去的是<em>一句建不出表的 DDL</em>而不是一列废列
        // （第七家是后者）。要不要在这一家也立闸门是待裁决，与 <c>Database.EnsureInventoryItem</c> 那段同一格。
        Assert.SkipWhen(Database.Family == Database.Target.PostgreSql,
            "postgresql 这一家没有 rowversion 那一型（pg_type 数到 0 行，S-10），本用例那句 DDL 建不出探针表" +
            "（42704，D-04；同形换 bytea 建得成，D-05）。题面是\u201c那句 DDL 在库里建出了什么\u201d，" +
            "而本家那句发出去就红在解析前 —— 没有目录可读，也就没有可判的对象。缺的那一格要么给本家换一型再判" +
            "（下一版的活，与 PostgreSqlCodeFirst 要不要立拒收闸门同一颗裁决），要么记 §14 —— 不是把这一句删掉。");

        // <b>第五族（mysql）在同一颗闸门上，而它的三颗读数与第四家<em>逐颗不同源</em>，所以另写一句而不是并进去</b>：
        // 那一型在本家库里也没有（<c>information_schema</c> 里按型名数到 0 行【实测·S-08】），
        // 那句 DDL 发出去红在解析前【实测·<c>obj/logs/v37-my-recon-3.log</c> D-03：<c>…near 'rowversion)'</c>；本用例那一次红在同一颗 1064 的
        // <c>…near 'rowversion NULL, `CheckedAt` DATETIME(3) NULL, CONSTRAINT `PK_QuickSqlStoreFilled…'</c>，
        // obj/logs/v37-my-int-1.log】，而同形把那一列换成 <c>TIMESTAMP(3)</c> 建得成【实测·D-04】——
        // 缺的是<em>那一型</em>而不是那句语法。上面那段"比第四家大一格"的缺口在本家同样成立：
        // <c>MySqlTypes</c> 对人写的型名是<em>原样发出</em>的（那颗 <c>StoreTypeName</c> 短路），
        // <c>MySqlCodeFirst</c> 没有拒收闸门，于是本家发出去的也是"一句建不出表的 DDL"而不是一列废列。
        // 库给值那一档在本家只有 <c>AUTO_INCREMENT</c> 那一颗整数键【实测·K-02 ∶ K-01】，
        // 没有"逐次换二进制令牌"的那一档，所以就算换了型名也建不出本题要的那两格（NOT NULL ∶ NULL）。
        Assert.SkipWhen(Database.Family == Database.Target.MySql,
            "mysql 这一家没有 rowversion 那一型（按型名数到 0 行，S-08），本用例那句 DDL 建不出探针表" +
            "（1064，D-03；同形换 TIMESTAMP(3) 建得成，D-04 —— 缺的是那一型而不是那句语法）。" +
            "题面与第四家同一颗：\u201c那句 DDL 在库里建出了什么\u201d，而本家那句发出去就红在解析前。" +
            "缺口记 §14，与 postgresql 那一格并排（同一条\u201cCodeFirst 没有拒收闸门\u201d的差异）。");

        try
        {
            Database.EnsureTable(typeof(StoreFilledProbe));
        }
        catch (QuickSql.QuickSqlException ex) when (Database.Family == Database.Target.Xugu)
        {
            // <b>第五家（xugu）在本用例上换的是"谁拒的"</b>：那一型库里有（A-04 目录交 <c>ROWVERSION</c>），
            // 是 <c>XuguCodeFirst.EmitColumnDefinition</c> 在 v0.34 第二笔把它拦在发射前（那句抛点原文
            // 就在下面这条 skip 消息里，逐字带着 RV-08 ∶ A-06 两颗读数）。
            //
            // <b>为什么这里要 <c>try</c> 而第四家只要一句 <c>SkipWhen</c></b>：本用例的题面是"那句 DDL
            // 在库里建出了什么"，而本家<em>跑过旧版夹具的机器上那句根本不发</em>（<c>EnsureTable</c> 见表即返），
            // 于是同一条用例在两家×两种库状态下有四种走法。按"表已经在"那一支留着跑、按"全新库"那一支
            // 点名 skip，才是这一格的诚实形状 —— 一句写死的 <c>SkipWhen(Xugu)</c> 会把前一支那三格 A-0x
            // 证人一起收掉，那是<em>减少</em>，也要红。
            Assert.Skip("xugu 一支的这句 DDL 被 provider 拦在发射前（不是库拒的，库里认那一型）：" + ex.Message);
        }

        var contract = Database.Orm.Schemas.Contract<StoreFilledProbe>();
        var rows = Columns("dbo." + contract.Table.Name);

        Assert.Equal(contract.Columns.Length, rows.Count);

        foreach (var column in contract.Columns)
        {
            var actual = Column(rows, column.ColumnName);

            Assert.True((actual["is_nullable"] == "1") == column.NullableInStore,
                $"列 {column.ColumnName} 的可空性与契约不符：目录 {Show(actual)}，契约 NullableInStore={column.NullableInStore} 而 CLR IsNullable={column.IsNullable}");
        }

        var filled = Column(rows, nameof(StoreFilledProbe.Version));
        var compareOnly = Column(rows, nameof(StoreFilledProbe.CheckedAt));

        // 两格并发列在实体侧一模一样（可空的 [ConcurrencyToken]），在库里一个是 NOT NULL、一个是 NULL。
        // 这两格分不开，"值归库给"就还是一句口头区分。
        //
        // <b>v0.32 第二格：这一对在第五家塌成"两格都可空"，而塌的原因不是"可空性判错"，是那一档在这一家根本不存在</b>。
        // 判据取<em>目录原文</em>而不是把第一家的两句删一半：<c>XuguTypes.ApplyStorageDefaults</c> 从来没有
        // 一处写 <c>StoreFillsValue</c>（<c>byte[]</c> 那一支只做 <c>StoreTypeName ?? "BLOB"</c>，
        // 于是注解里那颗 <c>rowversion</c> 原样穿了过去），所以契约自己就说那一格可空 —— 库里报可空是<em>对了</em>，
        // 红的是"这一家没有那一档"这件事本身。缺口记 §14，而它比"缺开关"大三格，逐颗见下。
        if (Database.Family == Database.Target.Xugu)
        {
            // 三格 A-0x 的原文都钉在这里（v32-recon-mat-4.log，靶表 QUICKSQLSTOREFILLEDPROBE 由上面那颗
            // <c>EnsureTable</c> 建出来）：
            // ① A-04 目录里 <c>TYPE_NAME="ROWVERSION"</c> —— 这一家<em>认</em>这个词，不是把它当错字拒掉；
            //    旁边那颗 <c>TAG</c> 报 <c>CHAR</c> + <c>VARYING=True</c> + <c>SCALE=32</c>、
            //    <c>CHECKEDAT</c> 报 <c>DATETIME</c> + <c>SCALE=3</c>，与 <c>XuguStoreType</c> 那四条分岔同一份原文。
            // ② A-05 <c>INSERT … ("TAG") VALUES (…)</c> 收下（AFFECTED=1）—— 那一格没人给值也建得成行。
            // ③ A-06 / A-06c 读它就抛 <c>System.DataOutOfBindException：参数绑定超界</c>（四列同句与单列同号），
            //    而<em>去掉那一列</em>的 A-06b 是绿的 —— 红点在那一列上，不在整句上。
            // ④ A-06d <c>LENGTH("VERSION")</c> 在服务端就被拒（<c>[E34501] … [E10049 L1 C8]</c>，
            //    那一句中文在这次抓取里是乱码，不作为厂商文案的证据；位置 L1 C8 正是 <c>LENGTH</c> 的首字母）。
            // 于是"把 <c>StoreFillsValue</c> 打开"这一手今天<em>补不出</em>这一档：库里那一格连读都读不回来，
            // 开关写上了也只是把一句能跑的 DDL 换成一句建出废列的 DDL。要不要在这一家<em>拒收</em>
            // <c>[Column(TypeName = "rowversion")]</c>（而不是原样发出去）曾是一笔记 §14 的待裁决 —— v0.34 第二笔
            // 已落地为拒收：三条 DDL 路在 <c>XuguCodeFirst.EmitColumnDefinition</c> 认型之后抛 <c>QuickSqlException</c>。
            // 本用例今天仍绿是因为 <c>EnsureTable</c> 见表即返，全新库建不出这两张夹具表那笔新账也记在 §14 同一格。
            Assert.Equal("1", filled["is_nullable"]);
        }
        else if (Database.Family == Database.Target.Sqlite)
        {
            // 本家同一格也是"1"，而<em>原因与第五家不是同一颗</em>，这一句必须写下来否则两家会被读成一家：
            // 那一家是"那一档建得出而读不回来"（A-06c 那一红），本家是<em>那一档在这一族没有入口</em> ——
            // 由库给值在这一家只有两个形状：列上的 <c>AUTOINCREMENT</c>（只作用于 rowid 那一格
            // 【实测·2802 ∶ 2810】）与触发器（本仓库没有那种形状），而型名压根不参与存储
            // （七颗随手写的型名一次建表、目录原样回读【实测·B1 ∶ C2】）。
            // 所以 <c>SqliteTypes</c> 从不置 <c>ValueFromStore</c>（<c>SqliteKeys</c> 类注释第五十九行那颗），
            // 契约自己就说这一格可空、DDL 发的就是 <c>NULL</c> —— 库里报可空是<em>对了</em>。
            // 本家也不像 <c>XuguCodeFirst</c> 那样为那颗型名立拒收闸门：<c>rowversion</c> 在这里发得出去，
            // 结果是<em>一列普通的、诚实参与比较而不自动递增的列</em>，那一格记 §14 的
            // "人写的型名 provider 不校验"那条通例（<c>SqliteConcurrency</c> 第一条末）。
            Assert.Equal("1", filled["is_nullable"]);
        }
        else
        {
            Assert.Equal("0", filled["is_nullable"]);
        }

        Assert.Equal("1", compareOnly["is_nullable"]);

        // 而自增列同属那一档（约定层判的，不是 provider 判的）：它在目录里也是 NOT NULL。
        Assert.Equal("0", Column(rows, nameof(StoreFilledProbe.Id))["is_nullable"]);
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
            var at = Database.Family is Database.Target.Sqlite or Database.Target.MySql
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
        else if (Database.Family == Database.Target.MySql)
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

/// <summary>并发列表的探针靶子（<see cref="DdlTests.Store_filled_tier_lands_in_the_catalog_through_the_emitted_ddl"/>）：
/// 形状照 <c>QuickSqlInventoryItem</c> 抄（自增主键 + 一格可写列 + <c>rowversion</c> 的并发列 + 一格没人给值的并发列），
/// 只有表名换一个 —— 因为这一格要的是"<b>这一句 DDL 每次运行都真跑一遍</b>"，
/// 而夹具那张同名表在旧机器上已经存在、存在性分支会让它跑不到。
///
/// <para>放本文件而不是 Common：Common 里的类型都挂源生成器，且都会被 <c>Database</c> 的夹具链认成
/// "要建的表"（那里是一份点名的清单，不是扫描），而这一格只要一张按契约现建的靶表。</para></summary>
[Table("QuickSqlStoreFilledProbe")]
public class StoreFilledProbe
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Tag { get; set; }

    /// <summary>值归库给的那一型：实体侧可空、库里 NOT NULL，这一格的分岔就是本题。</summary>
    [ConcurrencyToken]
    [Column(TypeName = "rowversion")]
    public byte[]? Version { get; set; }

    /// <summary>另一格并发列：同样不给写，可库里没有任何东西给它值，所以 DDL 必须留 NULL。</summary>
    [ConcurrencyToken]
    public DateTime? CheckedAt { get; set; }
}
