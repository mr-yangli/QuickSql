using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>本机测试库的一次性准备：清单解析驱动 → 建库 → 用 <c>CodeFirst</c> 产出的 DDL 建表 → 灌固定种子。
///
/// <b>只做裸 ADO，而且写路径落地之后仍然只做裸 ADO</b>：这里不是写路径的测试，它是全部真库断言的
/// <b>独立裁判</b>。用 ORM 的写口灌数据会把"读路径正确"变成"同一个缺陷自证正确"，而 v0.12 之后同一句
/// 对写侧更成立 —— <see cref="WritePathTests"/> 里那句"回包出来的 id 逐行对上库里的行"用的就是这里的
/// <see cref="Rows"/>：裁判若是"用 ORM 读回 ORM 自己写进去的行"，那么读写两侧同错时对账照样绿，
/// 所以它必须是另一条路。
/// 每一步都幂等（缺才建、有就不动），所以跨进程重复跑安全，也不需要 <c>DELETE</c>/<c>TRUNCATE</c> ——
/// 种子行自带批次标签 <see cref="Tag"/>，读侧一律按标签过滤，与库里别人的数据互不干扰。
///
/// <para><b>v0.32 第二格起，这一层是一条家族缝，不是一份 T-SQL 脚本</b>：环境变量
/// <c>QUICKSQL_TEST_PROVIDER</c> 选目标库（缺省 <c>sqlserver</c>，可填 <c>xugu</c>），
/// 其余 95 条用例<em>一行不改</em>。做法是把"裁判怎么说"分三处收在 <see cref="Translate"/> 这一颗函数里：
/// 标识符字形（<c>[dbo].[X]</c> → <c>"X"</c>）、占位符（<c>@p</c> → <c>:p</c>）、
/// 以及下面那几颗<em>问法本身</em>按厂商分岔的（存在性判据、自增回读、建库那一步）。
/// <b>为什么不干脆为第五家写第二份夹具</b>：那样一来"同一批用例在两台库上跑"这句话就没了对象 ——
/// 两份夹具会各自漂移，而漂移的那一侧恰好是没人看的那一份。收成一处之后，用例里那 55 处
/// <c>Database.Rows</c>/<c>Database.Execute</c> 自动跟着走，两边跑的<em>是同一份断言文本</em>。</para>
///
/// <para><b>而这一颗翻译器的边界要说死</b>：它只改<em>字形</em>，不改<em>语义</em>。凡是只有某一家才有的
/// 东西 —— <c>sys.columns</c>、<c>OBJECT_ID</c>、<c>SCOPE_IDENTITY()</c>、<c>SERVERPROPERTY</c>、
/// <c>CONVERT</c>/<c>DATALENGTH</c>/<c>UNICODE</c> 这些函数，以及那颗 <c>rowversion</c> 列 ——
/// 它<em>不翻译也不代做</em>，用例要在第一句用 <see cref="JudgeWrittenFor"/> 点名"这条裁判是写给哪一家的"，
/// 换一家就带着理由跳过。<b>这是"跳过"在这一层唯一的合法用途</b>：一条裁判写错家族是<em>红</em>，
/// 一条裁判<em>没有</em>那一族的对应物才是 skip；把后者也做成红，第五家第一跑就会得到一屏
/// 分不清"库不支持"与"我们没写"的失败，那正是 §13.4 那句"连不上库和被跳过的用例必须能被看出来"要避免的形状。</para>
///
/// <para><b>本层现在<em>能</em>在虚谷上跑到哪一格，只有真跑一次的那本账知道</b>（§13.4/§13.5 抄它）：
/// 上面那三处翻译的形状不是设计出来的，是 <c>obj/logs/v32-recon-txn-3.log</c> 那一段量出来的 ——
/// 事务提交/回滚两半都真（T-01/T-02）、<c>INSERT … ; SELECT LAST_INSERT_ID()</c> 这种多语句批在这一家收
/// （T-03，<c>Int64</c>）、<c>CASE WHEN … THEN … ELSE … END</c> 与 <c>NVL(:sch, UPPER(USER))</c> 同句可用
/// （T-05）、中文参数写进去再读回来逐字对得上（T-06，与 §10.4 那一族"静默变 ?"不同，这一家没有那一格）、
/// 一颗事务里错一条之后<em>同一颗事务仍然能写、且回滚仍然生效</em>（T-07/T-99：残留 3 条正好是
/// 事务外那三颗）。反过来，<c>obj/logs/v32-recon-keys-1.log</c> 的 N-06/N-07 说明<em>走 ORM 写口且要回填自增键</em>
/// 的那一批用例在这一家必红，红点与理由记在 <c>InsertEngine.ReadBackAsync</c> 与本版的 §14 账里。</para></summary>
static class Database
{
    /// <summary>本层支持的目标库。<b>四家</b>：§13.4 授权的三台实例里，两台是同一家的两个实例
    /// （2016/GBK 与 LocalDB 2019/cp1252，走同一个 <c>SqlServer</c> 分支），第三台是虚谷；
    /// 第七家（SQLite）<b>不在那份授权清单里，也不需要</b>——它的"实例"就是本机临时目录里的一颗文件
    /// （<see cref="Sqlite.ScratchConnectionString"/> 那一段写的正是这一格差别：另两家要一台别人批过的
    /// 实例，本家只要求一个可写目录）。<b>所以本枚举的判据换了一次</b>：从前是"没有授权实例就不留枚举值"，
    /// 现在是"能连得上一座真库、而且那座库是本层自己造的，就留"。另五族到今天<em>仍然</em>没有任何一台
    /// 授权实例，也仍然没有可自建的形状（要建的是实例而不是文件），所以这里<em>不</em>给它们预留值 ——
    /// 留了就会有人拿它当"这条路已经通了"的证据。
    ///
    /// <para><b>【2026-09-29 第四家到场】</b>上面那句"另五族仍然没有任何一台"对 PG 血统已经翻了一半：
    /// 用户提供的一台公网 PostgreSQL（<c>61.157.37.243:14432/TestDB</c>，version() 交
    /// <c>PostgreSQL 18.3</c>）走的是 <see cref="PostgreSql.TestConnectionString"/> 那条配置通路 ——
    /// 与 SQLite 那颗"自建文件"不同，它<em>要一台别人给的实例</em>，所以它进本枚举凭的是
    /// §13.4 那句"授权清单"添了第四台，凭的是实例可达而<em>不是</em>本层自造。</para>
    ///
    /// <para><b>【2026-09-29 同日 MySQL 一家到场】</b>同一句"另五族"今天读作"另四族"：MySQL 血统添了
    /// <see cref="Target.MySql"/>，凭据与第四家同一颗通路（用户提供的一台 <c>192.168.0.228:3306/TestDB</c>，
    /// <c>SELECT VERSION()</c> 交 <c>8.4.8</c> 而 <c>@@version_comment</c> 交
    /// <c>MySQL Community Server - GPL</c>【实测·S-01 ∶ S-02，<c>obj/logs/v37-my-recon-1.log</c>】）。
    /// 它是 MySQL 血统的<b>第一台</b>，而这一族的另外两棵叶子（<c>TiDb</c>／<c>OceanBase</c>）到今天
    /// <em>仍然</em>没有实例，所以本枚举<em>不</em>替它们留值（留了就会有人拿它当"这条路已经通了"的证据）。
    /// 剩下四族（Oracle 血统、达梦、金仓那一支、GBase 8s）同样没有实例、也没有可自建的形状，仍然不留。</para></summary>
    public enum Target
    {
        SqlServer,
        Xugu,
        Sqlite,
        PostgreSql,
        MySql,
    }

    static Target? _family;

    /// <summary>当前跑在哪一家。<b>缺省必须是 <see cref="Target.SqlServer"/></b>：这一层从 v0.9 起
    /// 全部断言都建立在它上面，"什么环境变量都不设"这一格不能变成"换一家跑"。
    /// 认不出来的值直接抛而不是回默认：拼错一个字母得到一份 SQL Server 的绿，比得到一条红更糟。</summary>
    public static Target Family
        => _family ??= (Environment.GetEnvironmentVariable("QUICKSQL_TEST_PROVIDER")?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "sqlserver" => Target.SqlServer,
            "xugu" => Target.Xugu,
            "sqlite" => Target.Sqlite,
            "postgresql" => Target.PostgreSql,
            "mysql" => Target.MySql,
            var other => throw new InvalidOperationException(
                $"QUICKSQL_TEST_PROVIDER = \"{other}\" 不认识：本层只接 sqlserver（缺省）、xugu、sqlite、postgresql 与 mysql。"),
        };

    /// <summary>目标库的连接串，三家的配置各走各的环境变量（<c>QUICKSQL_TEST_CONNECTION</c>、
    /// <c>QUICKSQL_XUGU_CONNECTION</c>、<c>QUICKSQL_SQLITE_CONNECTION</c>）—— 分开的理由写在
    /// <see cref="Xugu.TestConnectionString"/>。
    ///
    /// <para><b>本家多一条退路，而那条退路是有条件的</b>：配了（环境变量或仓库根那颗本地文件）就打那台，
    /// 没配就在临时目录自建一座 <c>quicksql-sqlite-integration.db</c>。这个分岔<em>不是</em>把
    /// <see cref="Sqlite.TestConnectionString"/> 那道闸门松开 —— 没配时的红在本家读起来是"没有那座文件"，
    /// 而那是本层唯一一处能自己造的对象。自建这一走会打一行出来（<see cref="Run"/> 的第一句），
    /// 所以"这一跑连的是配的那台还是自造的那座"在读数里看得见，不是暗账。</para></summary>
    public static string ConnectionString => Family switch
    {
        Target.Xugu => Xugu.TestConnectionString,
        Target.Sqlite => Sqlite.TryTestConnectionString() ?? Sqlite.ScratchConnectionString("integration"),
        Target.PostgreSql => PostgreSql.TestConnectionString,
        Target.MySql => MySql.TestConnectionString,
        _ => SqlServer.TestConnectionString,
    };

    /// <summary>本层<em>自己</em>要用的那一份 ORM（夹具建表走 <c>CodeFirst</c>，那是被测产出）。
    /// <see cref="Driver"/> 交出去给需要自己组装 <see cref="QuickSqlOrmOptions"/> 的用例
    /// （<c>TrackingPathTests</c> 那颗低预算包壳、<c>ConcurrencyPathTests</c> 的第二会话）。</summary>
    public static DbProviderFactory Driver => Family switch
    {
        Target.Xugu => Xugu.Driver(),
        Target.Sqlite => Sqlite.Driver(),
        Target.PostgreSql => PostgreSql.Driver(),
        Target.MySql => MySql.Driver(),
        _ => SqlServer.Driver(),
    };

    static QuickSqlOrm BuildOrm(DbProviderFactory factory)
        => Family switch
        {
            Target.Xugu => Xugu.Orm(factory),
            Target.Sqlite => Sqlite.Orm(factory, ConnectionString),
            Target.PostgreSql => PostgreSql.Orm(factory, ConnectionString),
            Target.MySql => MySql.Orm(factory, ConnectionString),
            _ => SqlServer.Orm(factory),
        };

    /// <summary>本层当前那一家的 <see cref="IDbProvider"/>。
    ///
    /// <para><b>为什么要公开它</b>：<c>TrackingPathTests</c> 那颗低预算包壳要的就是这一档 —— 它把
    /// provider 掰一层再交给 <see cref="OrmWith"/>。从前它写的是
    /// <c>LowBudgetProvider.Around(SqlServer.Provider(SqlServer.Driver()), 6)</c>，
    /// 也就是<em>用例自己点一家</em>；第五家第一跑它就把语句写进了 SQL Server、把断言问进了虚谷
    /// （同一场里 <c>Database.Rows</c> 走的是缝）。收成这一颗之后，"包壳包的是哪一家的 provider"
    /// 与"这一跑在哪一家"从此不可能不一致。</para></summary>
    public static IDbProvider Provider => BuildProvider(Driver);

    static IDbProvider BuildProvider(DbProviderFactory factory)
        => Family switch
        {
            Target.Xugu => Xugu.Provider(factory),
            Target.Sqlite => Sqlite.Provider(factory),
            Target.PostgreSql => PostgreSql.Provider(factory),
            Target.MySql => MySql.Provider(factory),
            _ => SqlServer.Provider(factory),
        };

    /// <summary>用例<em>自己那一台</em> ORM 的组装口：provider 与连接串都由缝给，只有
    /// <paramref name="aop"/> / <paramref name="provider"/> 两处让用例插手。
    ///
    /// <para><b>它补的是一格真缺陷，不是一颗便利品</b>：本层有三处"要一台独立的 ORM"的地方
    /// （钩子是进程内状态、低预算包壳会污染共享那一份），而三处从前都写成
    /// <c>Provider = SqlServer.Provider(SqlServer.Driver())</c> 配
    /// <c>ConnectionString = SqlServer.TestConnectionString</c>。<c>QUICKSQL_TEST_PROVIDER=xugu</c> 那一跑里
    /// 这三处的<em>写</em>落在 192.168.0.102、<em>读</em>落在 TESTDB，于是
    /// <c>WritePathTests.Audit_fills_the_same_value_either_way…</c> 得到
    /// <c>Expected: 6 / Actual: 0</c> —— 那种红<em>看起来像</em>"第五家的钩子链没通"，
    /// 真相只是这一行连错了库（与 <c>MaterializationAllocationTests</c> 那颗 <c>[E34304]</c> 同一族：
    /// 组装根跟着缝走、连接串不跟）。<b>独立 ORM 这个需求是对的，写死那两家才是错的</b>，
    /// 所以改法是让缝给这两格，而不是把用例改成按家族跳过。</para>
    ///
    /// <para><paramref name="aop"/> 为 null 时留默认那颗（<c>QuickSqlOrmOptions</c> 自己的 <c>new()</c>），
    /// 与前三处从前的行为逐字相同；<paramref name="provider"/> 只给包壳那一种用例用。</para></summary>
    public static QuickSqlOrm OrmWith(QuickSqlAop? aop = null, IDbProvider? provider = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = provider ?? BuildProvider(Driver),
            ConnectionString = connectionString ?? ConnectionString,
            Aop = aop ?? new QuickSqlAop(),
        });

    /// <summary>装载通道那一条出口要用的连接串。<b>只有 MySQL 一支与 <see cref="ConnectionString"/> 不同</b>：
    /// <c>MySqlBulkCopy</c> 走的是 <c>LOAD DATA LOCAL</c>，客户端不放行 <c>AllowLoadLocalInfile</c> 时驱动
    /// 在执行当场拒（那颗闸门写在 <c>MySqlBulkCopyChannel.UnavailableBecauseOf</c> 的抬头，本层今天有它的
    /// 真库读数，而那颗 gate 的另一半 —— "没放行时整批退 L3 并留痕" —— 由 <c>BulkPathTests</c> 用
    /// <em>原始那一颗串</em> 判）。
    ///
    /// <para><b>这里补的是一颗客户端开关，不是换一台库</b>：同一实例、同一座 <c>TestDB</c>、同一批表，
    /// 所以 <see cref="Rows"/> 那一路裁判照旧走 <see cref="ConnectionString"/>，两边读的是同一张表；
    /// 而"写的那一台用了额外的参数"这件事在本层是可见的（用例第一句就点名），不是暗账。
    /// 其余各家交回同一颗串：SQL Server 的 <c>SqlBulkCopy</c> 不欠参数，PG 那一支的 binary COPY 也不欠。</para></summary>
    public static string BulkConnectionString
        => Family == Target.MySql ? WithLocalInfile(ConnectionString) : ConnectionString;

    static string WithLocalInfile(string value)
    {
        if (value.Contains("AllowLoadLocalInfile", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(value, "(?i)(^|;)AllowLoadLocalInfile\\s*=\\s*[^;]*", "$1AllowLoadLocalInfile=true");

        if (value.Contains("UseLocalInfile", StringComparison.OrdinalIgnoreCase))
            return value;

        return value.TrimEnd(';', ' ') + ";AllowLoadLocalInfile=true";
    }

    /// <summary>要动装载通道的那台 ORM：与 <see cref="OrmWith"/> 同一个组装口，只把连接串换成
    /// <see cref="BulkConnectionString"/>。</summary>
    public static QuickSqlOrm BulkOrmWith(QuickSqlAop? aop = null, IDbProvider? provider = null)
        => OrmWith(aop, provider, BulkConnectionString);

    /// <summary>把 C# 侧的标识符折成<em>这一家目录里存着的那一形</em>，给"逐列比目录"那三句用。
    /// <b>只在虚谷折</b>：那一家的折叠方向是量出来的（写 <c>"PaidAt"</c> 建出来的列，
    /// <c>all_columns.col_name</c> 交回 <c>PAIDAT</c> —— M2-07/M2-05 与 F-42 同一件事），
    /// 而 T-SQL 那一支目录里存的就是建表时那份拼法，<em>不折</em>。
    /// <b>这里刻意不做"两家都用大小写不敏感比"</b>：那样 <c>paidat</c> 这种小写形也能过，
    /// 而"回包列名全大写"恰恰是本版记进 §13.5 的一条事实 —— 比 <c>Fold(期望)</c> 与"随便什么大小写"
    /// 之间差的正是那一条证人。同一颗 <see cref="Fold"/> 与 <c>XuguSchemaReader.Fold</c> 的纪律一样：
    /// 裁判这一侧自己折一次。
    ///
    /// <para><b>第七家也<em>不</em>折，而且这里必须钉一颗对照</b>：那一家目录里存的就是建表时那份拼法
    /// —— 带引号建出来的 <c>"QuickSqlMix"</c>，按绑定名 <c>'QuickSqlMix'</c> 问得到（4702 交 1 行）、
    /// 按大写形 <c>'QUICKSQLMIX'</c> 问<em>交 0 行</em>（4703，同一句问句的对照组）。列名同判：
    /// <c>pragma_table_info</c> 的 <c>name</c> 按 <c>'V'</c> 筛得中（4704）、按 <c>'v'</c> 筛不到（4705）。
    /// <b>也就是说本家与虚谷是这条缝上方向相反的两家</b>：那一家把写进去的拼法折成大写存，
    /// 这一家原样存，而两边都<em>不</em>能在比较那一侧做成大小写不敏感 —— 那样一来 4703 与 4705
    /// 这两颗对照组就没有对象了，"目录里到底存的是哪一串"这一格也就没有证人。</para></summary>
    public static string CatalogueName(string identifier) => Family == Target.Xugu ? Fold(identifier)! : identifier;

    /// <summary>把一颗 <see cref="Guid"/> 折成<em>裁判从库里读回来的那一串字</em>，给三句"按键逐字比"的用例用
    /// （<c>WritePathTests</c> 的客户端造键、<c>OrderingPathTests</c> 的两条外键回读）。
    ///
    /// <para><b>第七家换的是字形，而换的理由不是"这一家不认那颗键"</b>：本家的 <c>Guid</c> 落 <c>TEXT</c>
    /// （<c>SqliteTypes</c> 第五条：两档一模一样宽，所以取不冒充约束的那一形），写的是驱动自己那一份文本形
    /// 而不是本仓库的 <c>ToString("D")</c>，而<b>驱动那一份是大写</b>【实测·4710：绑进去
    /// <c>8F14E45F-…</c>，读回来 <c>'8F14E45F-EA41-4DF4-BA42-0823B1700BAF'</c> 且 <c>typeof</c> 为
    /// <c>text</c>、<c>LENGTH</c> 为 36】。<b>而它打得中</b>：同一颗 <c>Guid</c> 作绑定参数参与
    /// <c>"Id" = @id</c> 交回 <c>1</c>【实测·4711】—— 也就是说这一档是<em>展示形</em>上的差，
    /// 不是比较语义上的差（本家的 <c>=</c> 在 TEXT 上按二进制逐字判，两侧同为大写形才相等【实测·4717】，
    /// 而两侧都是驱动自己写的，所以恒同形）。因此这里改的是"期望值写哪一串"，
    /// <b>而不是把判据松成大小写不敏感</b>：后者会把"库里存的究竟是哪一形"这一格一起松掉。</para>
    ///
    /// <para>另两家不折：T-SQL 的 <c>uniqueidentifier</c> 是原生型而驱动回包的文本形是小写，虚谷一家
    /// 今天也交小写 —— 那两家的证人就是这两句用例在它们各自那一跑里的绿（<c>v34-int-*.log</c> 起）。</para></summary>
    public static string StoredGuidText(Guid value)
        => Family == Target.Sqlite ? value.ToString("D").ToUpperInvariant() : value.ToString("D");

    /// <summary>这一家的 <c>NUMERIC(18,4)</c> 那一格<em>交回什么 CLR 型</em>：T-SQL 交 <c>decimal</c>，
    /// 第五家交 <c>string</c>（<c>"12.3456"</c> 原样一串字，标度带着 ——
    /// 出处 <c>obj/logs/v32-recon-mat-1.log</c> 的 A-01，与 F-46b 同一件事的第二次量）。
    /// <b>这一颗为什么在缝里而不在那两条用例里</b>：它是"回包形状"而不是"某条断言的期望值"，
    /// 而读侧的 <c>ValueConverter</c> 恰好把这一格兜住了（<c>Amount</c> 在两家的集成用例里都读得出十进制数，
    /// 第四跑那 45 颗红没有一颗红在这里）—— 兜住不等于同形：装箱代价与等价判据在两家不是一式，
    /// <c>MaterializationAllocationTests</c> 那两条钉的就是这个差。
    ///
    /// <para><b>第七家与第五家同一形（<c>string</c>），而<em>不是</em>同一条路</b>：本家的钱列在 DDL 里就是
    /// <c>TEXT</c>（<c>SqliteTypes</c> 第二条：那一家有原生定点型而本家没有），驱动把
    /// <c>decimal</c> 交给它时<em>先抹掉标度</em>再落成一串字（<c>12.3400</c> → <c>'12.34'</c>【实测·2500】，
    /// <c>1.5000</c> → <c>'1.5'</c>【实测·4721】），所以本家交回的 <c>string</c> 与虚谷交回的
    /// <c>string</c> 逐字不同 —— 那一家带着声明的标度，这一家连"裁判自己写进去的那一串"都不还原。
    /// <b>ORM 写路径不走这一支</b>（<c>SqliteValues</c> 绑的是不变形文本，标度 4 原样进库【实测·326/333/701】），
    /// <b>第四家（PG）与 T-SQL 同形（<c>decimal</c>）</b>：<c>NUMERIC(18,4)</c> 在 Npgsql 的回包里就是
    /// 一颗 <c>decimal</c>（<c>PostgreSqlTypes.StoreToClr["numeric"] = typeof(decimal)</c> 那一格说的
    /// 是同一件事，而 2026-09-29 那一场侦察在真库上读到了它【实测·<c>obj/logs/v37-pg-recon-1.log</c>
    /// B-06（绑 <c>decimal</c> 交回 <c>Decimal</c>）与 V-03（<c>NUMERIC(18,4)</c> 列逐字交
    /// <c>12.3400 (Decimal)</c>】），所以 <see cref="MoneyClrType"/> 从今天起
    /// 是"两家 decimal ∶ 两家 string"而不是"一家 decimal ∶ 两家 string"。
    ///
    /// <para><b>【2026-09-29 MySQL 到场】那一句"两家 decimal"当天就读作"三家 ∶ 两家"</b>：新到那一家
    /// （MySQL）的定点型回包也是 <c>decimal</c> —— 一颗绑进去的 <c>12.3456m</c> 原样交回
    /// <c>Decimal</c>【实测·<c>obj/logs/v37-my-recon-1.log</c> B-07】，而 <c>MySqlTypes.StoreToClr</c>
    /// 那份映射里 <c>decimal</c> ∶ <c>numeric</c> 两格都写 <c>typeof(decimal)</c>，两边同判据。
    /// 与第四家同一侧而<em>不是</em>同一条来路：那一族的 <c>numeric</c> 在目录里是"型名 + 量纲拆两颗字段"，
    /// 而本家 <c>information_schema</c> 交的是 <c>decimal(18,4)</c> 一颗带量纲的字段【实测·X-02 第四行】，
    /// 所以"标度带着"这一格本家与虚谷同侧（库里带着），回包形状却与 T-SQL 同侧。</para>
    ///
    /// 于是<em>同一个数在两种来源下有两份落处</em>：夹具灌的种子是 <c>'12.34'</c>，ORM 写进去的是
    /// <c>'12.3400'</c>，那一句"回包对得上库里"在这里比的是数而不是字。缺口记 §14 本家那一格。</para></summary>
    public static Type MoneyClrType => Family is Target.SqlServer or Target.PostgreSql or Target.MySql ? typeof(decimal) : typeof(string);

    /// <summary>一列 <c>INT</c>（本家叫 <c>INTEGER</c>）在<em>回包</em>那一格是什么 CLR 型。
    /// <b>这一颗是"回包形状"而不是"契约里那一格"</b>，所以与 <see cref="MoneyClrType"/> 并排放：
    /// 契约说 <c>Id</c> 是 <c>int</c>，七家都这么读（读侧 <c>ValueConverter</c> 会窄化），
    /// 问的是<em>驱动先把那颗数装进了什么箱</em>。
    ///
    /// <para>T-SQL 与虚谷交 <c>Int32</c>（那两家的 <c>INT</c> 就是一颗 32 位列，证人仍是这两句用例
    /// 在各自那一跑里的绿）。<b>第七家交 <c>Int64</c>，而这一档在本家不是某一列的特例，是整族的</b>：
    /// 存储只有一颗 64 位整数，声明名窄而存储宽是常态（<c>SqliteTypes</c> 读侧那段），所以
    /// 一颗 <c>INTEGER</c> 列、一颗 <c>BIGINT</c> 列、甚至 <c>bool</c> 与 <c>pragma</c> 的计数字段
    /// 交回来的 CLR 型全是 <c>Int64</c>【实测·211 ∶ 310 ∶ 515，三处各是不同来源的一颗整数列】。
    /// 后果落在两句判据上：<c>GetFieldType</c> 那一句要按这一颗比，而"门与 <c>GetValue</c> 同值"那一句
    /// 在两边<em>不同型</em>时会打成 <c>Expected: 1 / Actual: 1</c>（正是 <c>MoneyClrType</c> 那段引过的
    /// 第五家同一种红），所以那里也比的是数不是箱。</para></summary>
    public static Type IntClrType => Family == Target.Sqlite ? typeof(long) : typeof(int);

    /// <summary>给<em>不走 <see cref="Rows"/> 的那两处裸 ADO 裁判</em>用的字形口：把一句 T-SQL 折成
    /// 本层目标库的写法，规则与"不翻译清单"都记在 <see cref="Translate"/>。
    ///
    /// <para><b>为什么不干脆让用例改调 <see cref="Rows"/></b>：<c>MaterializationAllocationTests</c> 要的是
    /// "同一条连接上把 reader 停在第一行、反复读同一格"，那颗 reader 必须活着跨过上百次取值，
    /// <c>Rows</c> 那种"读完即关"的形状量不了它。字形这一颗因此必须公开。</para>
    ///
    /// <para><b>而分页那一<em>形</em>不在它的职责里</b>：<c>OFFSET .. FETCH FIRST</c> 在第五家是语法错
    /// （F-46：<c>E19260 syntax error</c>），可用的那一形是 <c>LIMIT n OFFSET m</c>（F-46b，A-01 又跑了一次）——
    /// 这是"一句有、一句没有"，翻译器不代做，用例自己分岔并点名（同一颗 <c>RowOn</c> 里就是这么写的）。
    /// <b>第七家与第五家同形而同一条理由</b>：<c>TOP 1</c> 在本家是 <c>near "1": syntax error</c>【实测·4730】、
    /// <c>LIMIT 2 OFFSET 1</c> 是那颗能跑的【实测·4733 ∶ 4526】，所以那一处 <c>RowOn</c> 的分岔今天覆盖三家。
    /// 还有一句只在本家成立的：<c>SELECT</c> 那颗关键字<em>没有</em> <c>TOP</c> 可写，
    /// 也就没有"句首限定词"那一档 —— 与 §13.5 那本台账里"两族交集"的读法无关，但读那一颗 <c>RowOn</c> 的人会问，
    /// 所以写在这里。</para></summary>
    public static string FamilyText(string tSql) => Translate(tSql);

    /// <summary>裁判 SQL 的<em>家族闸门</em>：一条用例的裁判只写给某一家时，第一句调这里。
    /// <b>它不是"没配置就跳过"</b> —— 那种跳过会把"这条断言从没在这一家跑过"伪装成"这一家没问题"，
    /// 而 §13.5 那本台账要的正是一行行能指认的"没跑过"。消息里带上<em>为什么</em>与<em>缺哪一格</em>，
    /// 于是 xunit 的 skip 列表直接就是下一版的欠账清单。</summary>
    public static void JudgeWrittenFor(Target target, string what)
        => Assert.SkipUnless(Family == target,
            $"这条用例的裁判 SQL 是写给 {target} 的（{what}），当前目标库是 {Family}。" +
            "缺的那一格要么按这一家重写裁判（下一版的活），要么把这一家加进 §13.4 的授权清单——不是把这一句删掉。");

    /// <summary>批次标签。落进 <c>Customer.Remark</c> / <c>Order.Note</c> / <c>OrderItem.Sku</c> 这三个文本列。</summary>
    public const string Tag = "quicksql-p0";

    /// <summary>§13.6 左联专章要一行<b>"有 <c>CustomerId</c>、但没有对应客户"</b>的订单：
    /// 上面那批种子的 <c>CustomerId</c> 是取模分配的，永远配得到客户，于是左联补出来的 NULL 行从来没有真库见证。
    /// 标签单开一个（<c>quicksql-p0/orphan</c>），且与 <see cref="Tag"/> 是<b>不相等</b>的两个值 ——
    /// 所有按 <c>Note == Tag</c> 过滤的用例因此不会把它算进行数。</summary>
    public const string OrphanTag = Tag + "/orphan";

    /// <summary>孤儿订单指向的客户 id。取 <see cref="int.MaxValue"/>：客户表是自增的，正常走不到这里，
    /// 而用例另有一句断言钉住"这个 id 上确实没有客户"，所以哨兵失效时红的是那句前置断言而不是补行断言。</summary>
    public const int OrphanCustomerId = int.MaxValue;

    public sealed record CustomerSeed(string Name, string City);

    /// <summary>一条订单：客户名（可空，中文）、金额、状态、创建/支付时间。<see cref="PaidAt"/> 为 null
    /// 的那几条就是"可空列读回 null 而不是 default"的现场。</summary>
    public sealed record OrderSeed(string? CustomerName, decimal Amount, OrderState State, DateTime CreatedAt, DateTime? PaidAt);

    public sealed record OrderItemSeed(string Sku, int Quantity, decimal Price);

    /// <summary>客户名与城市都是中文：<c>nvarchar</c> + <c>N''</c> 参数在 cp1252 排序规则下的正确性
    /// 只有真跑一次才算验过（§10.4 第一行是"静默变 ?"，不是报错）。</summary>
    public static readonly CustomerSeed[] Customers =
    [
        new("张三", "上海"),
        new("李四", "北京"),
        new("王五", "广州"),
    ];

    /// <summary>毫秒都取 <c>.456</c>/<c>.123</c> 这类<b>不是 1/300 秒整数倍</b>的值：任何按遗留
    /// <c>datetime</c> 精度取整的绑定都会在这里露出来（§9.3 点名的陷阱，而列已经是 <c>datetime2(3)</c>，
    /// 所以取整只可能发生在参数绑定这一侧）。金额覆盖 <c>decimal(18,4)</c> 的最小标度与四位全用满。</summary>
    public static readonly OrderSeed[] Orders =
    [
        new("甲公司", 12.3456m, OrderState.Placed, At(2026, 1, 5, 8, 30, 123), At(2026, 1, 6, 9, 0, 456)),
        new(null, 0.0001m, OrderState.Draft, At(2026, 2, 1, 0, 0, 456), null),
        new("丙戌科技", 999999.9999m, OrderState.Shipped, At(2026, 2, 14, 23, 59, 456), At(2026, 2, 15, 8, 15, 123)),
        new("丁", 100m, OrderState.Cancelled, At(2026, 3, 1, 12, 0, 123), null),
        new("戊", 1.5000m, OrderState.Paid, At(2026, 3, 9, 10, 20, 30, 456), At(2026, 3, 10, 10, 20, 30, 456)),
    ];

    public static readonly OrderItemSeed[] Items =
    [
        new("A-1", 2, 3.5m),
        new("A-2", 1, 12.3456m),
        new("B-1", 7, 0.0001m),
        new("C-1", 3, 999999.9999m),
        new("E-1", 10, 1.5m),
        new("E-2", 11, 1.5m),
    ];

    static DateTime At(int year, int month, int day, int hour, int minute, int ms)
        => new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified).AddMilliseconds(ms);

    static DateTime At(int year, int month, int day, int hour, int minute, int second, int ms)
        => new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddMilliseconds(ms);

    static readonly Lazy<Setup> _setup = new(Run);

    readonly record struct Setup(QuickSqlOrm? Orm, string? Failure);

    /// <summary>非 null 就是"一条也跑不了"的原因原文。测试把它当 skip 的理由报出来 ——
    /// 连不上库和被跳过的用例必须能被看出来，否则 <c>LocalDB 没起</c> 会伪装成 <c>全绿</c>。</summary>
    public static string? Unavailable => _setup.Value.Failure;

    public static QuickSqlOrm Orm => _setup.Value.Orm
        ?? throw new InvalidOperationException($"库未就绪，用例必须先 SkipUnless：{Unavailable}");

    /// <summary>每条集成用例的第一句。<see cref="Assert.SkipUnless"/> 抛的是跳过异常，所以它同时是守卫。</summary>
    public static void Ready() => Assert.SkipUnless(Unavailable is null, $"本机测试库不可用：{Unavailable}");

    /// <summary>第二颗闸门，v0.34 第一笔起挂在<em>要动并发列</em>的用例第一句之后。
    ///
    /// <para><b>这一格从前挂的是另一颗闸门 <c>RequireKeyBackfill</c>：虚谷一家整族 skip</b>。
    /// 旧账原文照录不删——"第四跑那 51 颗红里有 45 颗红在这一颗上，它们<em>不是</em> 45 个缺陷，
    /// 是<em>一个</em>缺口的 45 个消费者"（那个缺口：<c>INSERT … RETURNING</c> 被库收下、行进去了、
    /// 回包 <c>FIELDS=0</c>，<c>obj/logs/v32-recon-keys-1.log</c> 的 N-06/N-07）。v0.34 第一笔把
    /// 那颗口子建起来了（<c>IKeyStrategy</c> 的语句后回读钩子，虚谷这一家带真库出处），
    /// 45 个消费者连同那句"改 <c>IKeyStrategy</c> 的签名不归本版（接口契约留给用户定）"一起退场 ——
    /// 后半句被用户一句「都要」收走。这一格现在剩的是并发列的<b>两颗缺口，彼此独立</b>：</para>
    ///
    /// <list type="bullet">
    /// <item><b>键＋版本的回填集仍走 <c>EmitReturning</c></b>：语句后回读那颗口子只接"回填集恰好
    /// 一颗自增列"这一形（<c>InsertEngine.Build</c> 的 <c>postRead</c> 判据），带令牌的形状发出去的
    /// <c>RETURNING "ID", "VERSION"</c> 还是实测的那个空结果集 —— 0/N 红与 v0.32 同格。</item>
    /// <item><b>那一列连读都读不回来</b>：<c>SELECT "VERSION"</c> 由驱动侧抛
    /// <c>System.DataOutOfBindException：参数绑定超界</c>（<c>v32-recon-mat-4.log</c> 的 A-06c；
    /// 四列同句 A-06 同号，去掉那一列的 A-06b 是绿的）。</item>
    /// </list>
    ///
    /// <para><b>所以这一句仍是 skip，既不是红、也不是把闸门松开</b>：它把两颗缺口的消费者收成
    /// 一格可数的账，逐颗缺口记在 §14 的并发契约格里（与"两个已落地家族都有批量 RETURNING"并排读）。</para>
    ///
    /// <para><b>第七家挂的是另一条账，而它比虚谷那一格更靠前</b>：本家不是"那一档建了而读不回来"，
    /// 是<b>那一档在本家没有入口</b>。<c>InventoryItem.RowVersion</c> 带的是
    /// <c>[Column(TypeName = "rowversion")]</c>，而型名在这一族不参与存储【实测·B1 ∶ C2 七颗型名原样回读】，
    /// "由库给值"在这一族只有 rowid 那颗 <c>AUTOINCREMENT</c> 一个入口【实测·2802 ∶ 2810】——
    /// 于是那一列就是一列普通可空列，库永远不给它值。两半后果都在第一跑里数到了
    /// （<c>obj/logs/v37-sqlite-int-1.log</c>）：回填交回 <c>null</c>（那条用例红在
    /// <c>item.RowVersion!.Length</c> 的 <c>NullReferenceException</c>），而"别人改过那一行"这种冲突
    /// 永不发生（另两颗红在 <c>Assert.Throws&lt;DbUpdateConcurrencyException&gt;</c> 的"没抛"）——
    /// 并发<em>谓词</em>本身在本家是通的【实测·3302 ∶ 3303 命中与不命中各一颗，
    /// <c>SqliteConcurrency</c> 第二条】，缺的是让那一格<em>动</em>起来的人。整格记 §14，
    /// 与"应用自己给版本值"那一条路（本家能跑的那一档）并排。</para></summary>
    public static void RequireConcurrencyColumn()
    {
        Assert.SkipWhen(Family == Target.Xugu,
            "xugu 这一家的并发列今天两颗缺口都没补：键＋版本的回填集仍发 RETURNING（库收下、回包空结果集，" +
            "obj/logs/v32-recon-keys-1.log N-06/N-07），而 SELECT 那一列直接被驱动抛 DataOutOfBindException" +
            "（v32-recon-mat-4.log A-06c）。单颗自增列的回填已在 v0.34 第一笔改走语句后回读" +
            "（本文件那颗整族闸门就是这么退场的），本条用例的判据因此没跑过；" +
            "它若被强跑，红点是 ReadBackAsync 的\u201c只回填了 0/N 行\u201d与裁判回读那一列各一处。");

        Assert.SkipWhen(Family == Target.Sqlite,
            "sqlite 这一家没有\u201c值归库给\u201d那一档：型名不参与存储（B1/C2），而库给值的入口只有 rowid 那颗 " +
            "AUTOINCREMENT（2802/2810），所以 [Column(TypeName = \"rowversion\")] 建的是一列普通可空列。" +
            "第一跑的两半读数在 v37-sqlite-int-1.log：回填交回 null，而\u201c别人改过\u201d永不成立（并发谓词本身是通的，" +
            "3302/3303）。本文件的十条用例判的都是\u201c库给那一格换了值\u201d，在本家没有对象。");

        // <b>第四家挂的是<em>第三颗</em>理由，与前两家都不重合，而它比第七家那一格更靠前</b>：
        // 第七家是"那一档在本族没有入口但列建得出来"，第五家是"建得出而读不回来"（A-06c），
        // 本家是<b>连那一列都建不出来</b> —— <c>rowversion</c> 这颗型名在这一台库里不存在
        // （<c>pg_type</c> 数到 0 行【实测·S-10】），而 provider 对人写的型名原样发出（<c>StoreTypeName</c> 短路），
        // 于是夹具那张表在本家今天建不出来（原文与三颗读数记在 <see cref="EnsureInventoryItem"/> 那一段，
        // 库里那一颗红是 <c>42704</c>【实测·D-04】）。
        // <b>本家另有一条与前两家相反的证据要一起读</b>：并发<em>谓词</em>与键的回填两句在本家都通 ——
        // <c>RETURNING</c> 场外与事务里各跑一次都命中【实测·K-01 ∶ K-02】，也就是说 v0.32 那一格
        // "回填集发 RETURNING 交回空结果集"的红<em>不是</em>本家的形状（那一家是库收下而回包空，本家是库答键）。
        // 所以这一家缺的只有那一型：把它换成 <c>bytea</c> 加一句人给的默认值就能建出<em>另一件事</em>的靶表，
        // 而那一档判的仍是"值归库给"—— 需要的是产品侧的一个决定（要不要像 <c>XuguCodeFirst</c> v0.34
        // 那样拒收这颗型名，还是替这一族登记一个真入口），不是夹具的一句替代 SQL。缺口记 §14。
        Assert.SkipWhen(Family == Target.PostgreSql,
            "postgresql 这一家没有 rowversion 那一型（pg_type 数到 0 行，S-10），夹具那张并发列表因此建不出来" +
            "（42704，D-04；同形换 bytea 建得成，D-05 —— 缺的是那一型而不是那句语法）。本条用例判的是" +
            "\u201c库给那一格换了值\u201d，而本家库给值的入口只有 identity 那颗序列（K-03），没有逐次换二进制令牌的那一档。" +
            "键的回填与并发谓词本身在本家都有读数（K-01/K-02），红点不会落在那两句上。");

        // <b>MySQL 挂的是第四颗理由，而它与第四家同源不同句</b>：那一格的两半在这里都成立 ——
        // 库里没有那一型（S-08 数到 0）、provider 原样发出人写的型名（StoreTypeName 短路），
        // 于是夹具那张表在本家建不出来；红点那句<em>不是</em> <c>42704</c> 而是 <c>1064</c>
        // 【实测·D-03】，因为这一族的目录不给"型名不存在"这一颗专门错误，它在解析阶段就当语法错折回来。
        // <b>本家另有一条与前三家都不同的加分要说</b>：并发<em>谓词</em>本身在这一族是通的 ——
        // <c>MySqlConcurrency.Mode</c> 是 <c>TimestampColumn</c>（人给值的那一列），而"这一列变了没有"
        // 那句比较在本家有专门的空值安全算子 <c>&lt;=&gt;</c>【实测·Z-05：<c>NULL &lt;=&gt; NULL</c> 交 1，
        // 而 <c>IS DISTINCT FROM</c> 这一族没有【实测·Z-04】】。所以这一家缺的仍然只有那一型与那张表。
        Assert.SkipWhen(Family == Target.MySql,
            "mysql 这一家没有 rowversion 那一型（S-08 数到 0 行），夹具那张并发列表因此建不出来" +
            "（1064，D-03；那一列换成 TIMESTAMP(3) 的建表语句收，D-04 —— 缺的是那一型而不是那句语法）。" +
            "本条用例判的是\u201c库给那一格换了值\u201d，而本家库给值的入口只有 AUTO_INCREMENT 那一档整数键（K-02），" +
            "没有逐次换二进制令牌的那一档。本家的并发档位设计上是\u201c人给值的那一列\u201d（MySqlConcurrency = TimestampColumn），" +
            "与夹具声明的 rowversion 不是同一颗；谓词那一半在本家有专门的空值安全算子（<=>，Z-05），红点不会落在那一句上。");
    }

    /// <summary>第三颗闸门：<b>这一家有没有"库里真的会挡"那条外键裁判</b>。
    ///
    /// <para>落点在 <see cref="EnsureProjectForeignKey"/> 那一颗方法上，而它在本家整段走不到（原文的
    /// 两颗缺口写在那一段：没有 <c>ALTER … ADD CONSTRAINT</c>【实测·4509 ∶ 4510 两形都是语法错】，
    /// 而 <c>CodeFirst</c> 不发 <c>FOREIGN KEY</c> 表级子句 —— 唯一能挂的位置本仓库不发【实测·4508：
    /// 建表时带上就建得成，而 4514 当场把无父的行挡下来】）。
    /// 本闸门挂在 <c>OrderingPathTests</c> 那颗<em>负样本</em>上（"绕开会话插一行没有父的必须抛"），
    /// 因为它是唯一一条直接把裁判本身当判据的用例；本家跑它只会得到"没抛"，而那种红
    /// <em>读起来像</em>"本族的约束不生效"，真相是约束压根没建。</para>
    ///
    /// <para><b>前三条不挂这颗闸门，而这里要写清它们在本家钉到什么程度</b>：类注释第三段那句
    /// "约束不在位时，前面三条绿得毫无意义"读法要收窄一点——那三条在本家仍然钉住两件事
    /// （报告的档内表序、以及那些语句在真库上按下述次序执行得过去且数据落对），
    /// 钉不住的是"<em>那个次序是必要的</em>"，因为必要性的证人就是这道裁判。
    /// 本笔核过一件事：档内类型序<em>没有</em>离线证人（<c>grep -rn "OrderedByDependency|拓扑" tests/</c>
    /// 在 <c>QuickSql.Tests.Dialects</c> 一侧零命中，那颗排序今天只由本类四条款盯），
    /// 所以把整类跳成本家全黑是更坏的选择——留三条带限定的绿，比留三条 skip 更接近真相。
    /// 这一格（本家的必要性证人 + 类型序缺离线册）一起记 §14。</para></summary>
    public static void RequireForeignKeyJudge()
        => Assert.SkipWhen(Family == Target.Sqlite,
            "sqlite 这一家没有本夹具的外键裁判：往已有的表上加约束是语法错（4509/4510），而 CodeFirst 不发 " +
            "FOREIGN KEY 表级子句（§14），所以 EnsureProjectForeignKey 在本家整段跳过（它那段注释是原文）。" +
            "本条用例判的就是\u201c库挡不挡\u201d，裁判不在场时它只会红在\u201c没抛\u201d。" +
            "这一族的外键语义本身有读数：建表时带上就挡，且默认就开（4508/4512/4513/4514）。");

    static Setup Run()
    {
        try
        {
            // 走完整解析链（内建表 BuiltinDrivers → 独立 AssemblyLoadContext → DbProviderFactory），
            // §1.2 原则 3 与 §9.5 的第一次真跑就是这一句，而不是 new SqlClientFactory()。
            // v0.32 第二格起这一句按 <see cref="Family"/> 分岔：第五家走的是 XuguClient 那一行，
            // 而它是<em>本仓库交付入库的那份厂商产物</em>（src/QuickSql.Providers.Xugu/Driver/），
            // 所以"内建表那一行有效"这件事在两家各有证人，少一家都少一半。
            var factory = Driver;
            var orm = BuildOrm(factory);

            PrepareCatalog(factory);

            using var connection = factory.CreateConnection()!;

            connection.ConnectionString = ConnectionString;
            connection.Open();

            // Project 必须在 Milestone 之前：后者的外键约束（下面那句）引用它。
            foreach (var entity in new[] { typeof(Customer), typeof(Order), typeof(OrderItem), typeof(SessionToken), typeof(Project), typeof(Milestone) })
                EnsureTable(connection, orm, entity);

            EnsureInventoryItem(connection, orm);

            EnsureProjectForeignKey(connection);

            Seed(connection);

            EnsureOrphanOrder(connection);

            return new Setup(orm, null);
        }
        catch (Exception ex)
        {
            // 夹具只留 <c>ex.Message</c> 是有道理的：那一句要贴在 87 条 skip 的每一条上。
            // 但<em>整场只失败一次</em>，所以原文（带类型名、内部异常与栈）另走一条口子打出来 ——
            // 第五家第一跑就是靠这一句才知道"Decimal 参数没类型"死在绑定而不是建表那一步的，
            // 而只看 <c>Unavailable</c> 那一行会连是 DDL 还是 DML 都猜不出来。
            Console.Error.WriteLine("夹具准备失败，原文如下：");
            Console.Error.WriteLine(ex);

            return new Setup(null, ex.Message);
        }
    }

    /// <summary>裁判连接上的<em>写</em>。<b>这条口子只为一个场景开：模拟"别人改过了"</b> ——
    /// 乐观并发的冲突必须由会话之外的人制造，用 <c>DbContext</c> 自己造就是把裁判交给被告
    /// （<see cref="Rows"/> 那条理由的同一半）。所以它只收裸 SQL、只允许测试自己按标签建的那些行，
    /// 而每个调用点都得在这里之外的 <c>WHERE</c> 里带上标签或主键。</summary>
    public static void Execute(string sql, params (string Name, object? Value)[] args)
    {
        Ready();

        using var connection = Orm.Provider.Connectors.CreateConnection(ConnectionString);

        connection.Open();

        Execute(connection, null, sql, args);
    }

    /// <summary>目录/数据查询。<b>返回的是字符串而不是值</b>：这类断言失败时，消息里要直接看得到库里
    /// 实际有什么（列的可空性、类型名、建表时间），否则"契约说可空、库里是 NOT NULL"这种分歧
    /// 只会留下一个 <c>True != False</c>，什么也查不下去。</summary>
    public static List<Dictionary<string, string>> Rows(string sql, params (string Name, object? Value)[] args)
    {
        Ready();

        using var connection = Orm.Provider.Connectors.CreateConnection(ConnectionString);

        connection.Open();

        using var command = connection.CreateCommand();

        Bind(command, transaction: null, sql, args);

        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, string>>();

        while (reader.Read())
        {
            // <b>键的比较器按家族分</b>：这一家的目录与查询回包里的列名<em>全是大写</em>（M2-20 那一段
            // 逐列数到的就是 <c>COL_NAME</c>/<c>DATA_TYPE</c> 这一形），而用例那边是按 T-SQL 的写法
            // 索引 <c>r["col_name"]</c>/<c>r["Cnt"]</c>。用 <see cref="StringComparer.Ordinal"/> 会得到
            // 一屏 <c>KeyNotFoundException</c>，那种红<em>读起来像</em>"这一家不返回这一列"，
            // 而真相只是大小写。SQL Server 那一支保持 <c>Ordinal</c>：它的排序规则不敏感，
            // 但用例里"两列只差大小写"这种写法一旦出现，宁可在字典这里红，不要在库里静默合并。
            //
            // <b>第七家走的是 <c>Ordinal</c> 那一支，而且是量出来的</b>：同一句里
            // <c>"V" AS "Tomorrow"</c> 与 <c>"N" AS "n_low"</c> 交回 <c>Tomorrow|n_low</c> 两颗
            // 【实测·4527 的表头】，<c>pragma_table_info</c> 的 <c>name</c> 列交回的是建表时那份拼法
            // 【实测·4707】—— 也就是说本家既不折大写、也不在回包这一侧动大小写，
            // 用例按 T-SQL 的写法索引因此<em>逐字命中</em>，不需要第五家那层宽容。
            //
            // <b>第四家走的是 <c>OrdinalIgnoreCase</c> 那一支，而它拿到这一支的原因与第五家不是同一颗</b>：
            // 用例那些问句里的别名是不带引号写的（<c>COUNT(*) AS Cnt</c> ∶ <c>… AS V</c> ∶ <c>… AS Differs</c>），
            // 而这一族对<em>不带引号</em>的标识符折的是<b>小写</b> —— 于是回包列名是 <c>cnt</c>/<c>v</c>/<c>differs</c>，
            // 按 <c>Ordinal</c> 索引会得到一屏 <c>KeyNotFoundException</c>，那种红<em>读起来像</em>
            // "这一家不返回这一列"（第五家那一段写的正是同一种误读，只是折向另一边）。
            // <b>而这一层宽容只用在"回包列名"，没有渗进判据</b>：目录那三句仍然按原样拼法问，
            // 大写那一形在本家交 0 行【实测·C-02 那颗对照】，别名与目录两头分开处理才既跑得动又留得住证人。
            var row = new Dictionary<string, string>(
                Family is Target.Xugu or Target.PostgreSql ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "NULL" : Render(reader.GetValue(i));

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>把一格里取回的值打成<em>裁判要的那一份文本</em>。<b>只有 <see cref="DateTime"/> 这一型分岔</b>，
    /// 而分岔的理由是"打不出"而不是"存不下"：这一家的目录与格式化函数<em>四形全丢小数秒</em> ——
    /// <c>CAST(… AS VARCHAR/VARCHAR(23)/CHAR(27))</c> 与 <c>TO_CHAR(…, '.MS'/'.FF3')</c> 交回的都是
    /// <c>2026-06-01 02:03:04</c> 与 <c>2026-01-05 08:30:00.</c>（F-24/F-25/F-31/F-36/F-36b/F-44 六问，
    /// <c>obj/logs/v32-recon-funcs-9.log</c>）；同一颗值走<em>数值</em>那一路却带着毫秒：读回来是
    /// <c>2026-06-01 02:03:04.5670000</c>（F-30），与 <c>TIMESTAMP '…08:30:00.123'</c> 相等而与不带毫秒的
    /// 同一颗字面量不等（F-38 交 1 / F-38b 交 0），CLR 里带 .123 的绑定参数出去再回来仍是 .1230000（F-39/F-40）。
    /// <b>所以这一家"毫秒在不在"这一句只能由裁判自己打</b>：形照 T-SQL 的
    /// <c>CONVERT(varchar(23), …, 121)</c> 对齐成 <c>yyyy-MM-dd HH:mm:ss.fff</c>，用例里那六处
    /// <c>Millis(…)</c> 因此在<em>两家是同一份预期文本</em> —— 换掉的只是"谁去打这一串"。
    /// <b>SQL Server 一支一字不动</b>（仍走 <c>value.ToString()</c> 与它那六处 <c>CONVERT</c>），
    /// 所以这一颗不牵动 v0.31 之前所有断言的文本。</summary>
    static string Render(object value)
        => value is DateTime moment && Family == Target.Xugu
            ? moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : value.ToString() ?? "NULL";

    /// <summary>"把这一列的毫秒打成文本"这一句的两家写法。<b>它收进来的理由只有一条：上一版它是两颗
    /// <c>const string Millis</c>，在 <c>WritePathTests</c> 与 <c>UpdateDeletePathTests</c> 各写一份，
    /// 而分岔一开就要改两处 —— 两颗只在一处才撞得上的缺陷，正是这一层收成一颗函数的原话</b>。
    ///
    /// <para>T-SQL 那一支没换：<c>CONVERT(varchar(23), …, 121)</c> 交 <c>2026-05-06 07:08:09.123</c>。
    /// 虚谷那一支<em>发不出等价的一句</em>，所以这里直接把列还给 <see cref="Rows"/>，由 <see cref="Render"/>
    /// 在裁判这一侧打上同一个形（四形全丢毫秒的实测与理由就写在那一颗的注释里）。
    /// <b>也就是说这一家换的不是"哪一句 SQL"，而是"谁去打这一串字"</b> —— 打的动作从库里挪到裁判里，
    /// 而它读的那一颗值仍然是库里那一颗（F-30/F-38/F-40 三问钉的是值，不是形）。</para>
    ///
    /// <para><b>第七家与第五家同一个返回（把列原样还），但理由正相反，而这一句必须写下来</b>：
    /// 那一家是"库里打不出小数秒，四形全丢"，所以由 <see cref="Render"/> 在裁判这一侧补；
    /// 本家是<b>库里那一格本来就是文本</b> —— <c>DbType.DateTime2</c> 绑一颗带毫秒的时刻进去，
    /// 读回来是 <c>'2026-01-05 08:30:00.123'</c> 这 23 个字符【实测·4734】，而驱动交回的 CLR 型是
    /// <c>String</c> 不是 <c>DateTime</c>【实测·4707 的 <c>GetFieldType</c> 一排】，
    /// 所以 <see cref="Render"/> 在本家<em>一条都不走</em>：没有东西需要打，那一串字库里就写着。
    /// <b>代价是"定宽"这一格在本家没有</b>：同一颗驱动在零毫秒时不写那颗小数点
    /// （<c>'2026-03-09 10:20:30'</c>【实测·4531】，同一批里另有 19 ∶ 24 ∶ 27 字符的宽度差【实测·4402】），
    /// 于是本层那六处 <c>Millis(…)</c> 断言在这一家读到的"<em>与另一家同一份预期文本</em>"，
    /// 前提是种子那批时刻的毫秒位全非零（<see cref="Orders"/> 那一段写的 <c>.456</c>/<c>.123</c> 正是）。
    /// 一颗零毫秒的时刻在这一家会打成缺一截的串，那是<em>红</em>，不是这一颗该兜的东西。</para></summary>
    public static string MillisText(string column)
        => Family switch
        {
            Target.Xugu or Target.Sqlite => column,
            // 第四家发得出一句与 T-SQL 同形的问句：<c>CAST("PaidAt" AS text)</c> 交
            // <c>2026-01-05 08:30:00.123</c>（与那边 <c>CONVERT(varchar(23), …, 121)</c> 同一份文本形）。
            // 与第七家那条"零毫秒时不写小数点"的代价同档：本家那颗 cast 在 <c>.000</c> 时交回
            // <c>2026-03-09 10:20:30</c>，所以这一句的前提仍是 <see cref="Orders"/> 那批时刻毫秒位全非零。
            Target.PostgreSql => "CAST(" + column + " AS text)",
            // <b>MySQL 这一家与本家同形而多一颗加分</b>：<c>CAST(col AS CHAR)</c> 交
            // <c>2026-01-05 08:30:00.123</c>（23 个字符）【实测·V-04】，而第四家那条"零毫秒时不写小数点"
            // 的代价在本家<em>不成立</em> —— 同一颗 cast 对 <c>.000</c> 交回
            // <c>2026-03-09 10:20:30.000</c> 而<em>不是</em>缺一截的那一串【实测·X-14，len 仍是 23】，
            // 所以本家是五家里唯一一处"<see cref="MillisText"/> 与 T-SQL 那颗 <c>CONVERT(…, 121)</c>
            // 连前提都相同"的一族：定宽由库给，不靠 <see cref="Orders"/> 那批种子的毫秒位全非零。
            Target.MySql => "CAST(" + column + " AS CHAR)",
            _ => "CONVERT(varchar(23), " + column + ", 121)",
        };

    /// <summary>目录里"带亚秒位数的那一颗时刻型"叫什么。<b>两颗名字不是一份，判据就不能写成一份</b>：
    /// T-SQL 还 <c>datetime2</c>（而遗留的 <c>datetime</c> 在它旁边同名不同物，所以那一格值得单独钉），
    /// 虚谷还 <c>DATETIME</c> 而把位数拆到 <c>scale</c> 那一格里（M-01 第 6/7 行：两列都是
    /// <c>TYPE_NAME="DATETIME"</c> 配 <c>SCALE=3</c>）。<b>用例真正钉的是后一半</b>（位数 = 3），
    /// 这一颗只是把名字那一半也写成两家各自说得出的那一个词，而不是让 T-SQL 的拼法去要求另一家。
    ///
    /// <para><b>第七家这一格交 <c>TEXT</c>，而那是本层唯一一处"名字对得上、问句问不出位数"</b>：
    /// 本家的时刻列在 DDL 里写的就是 <c>TEXT</c>，<c>pragma_table_info</c> 原样还它
    /// （<c>type</c> 那一颗【实测·4707 第 4 行 <c>'CheckedAt':TEXT</c>】），而那六颗字段是
    /// <c>cid ∶ name ∶ type ∶ notnull ∶ dflt_value ∶ pk</c>【实测·4707 的表头】——
    /// <b>里面没有位数这一颗</b>，因为这一族的型名不携带量纲（声明 <c>DATETIME(3)</c> 也只是换一颗
    /// affinity 配方）。所以 <c>DdlTests</c> 在这一家钉的是"型名 = TEXT 而库里那一串文本带着毫秒"，
    /// 位数那一句在本家<em>没有对象</em>，缺口记 §14 本家那一行。</para></summary>
    public static string TimeStoreType => Family switch
    {
        Target.Xugu => "DATETIME",
        Target.Sqlite => "TEXT",
        // 第四家交回的是 <c>information_schema.columns.data_type</c> 那一颗词折过的形：本家目录里写的是
        // <c>timestamp without time zone</c>，而 DDL 发出去的是 <c>TIMESTAMP(3)</c>，
        // 折回 "timestamp" 才与 <see cref="Columns"/> 那一份折叠同一式（量纲在括号里、位数在 scale 里）。
        Target.PostgreSql => "timestamp",
        // <b>MySQL 这一家交回的是 <c>column_type</c> 那一颗字段，而它是五家里唯一一处"名字与量纲
        // 本来就写在一起"的</b>：<c>DATETIME(3)</c> 建出来的列，<c>data_type</c> 只交 <c>datetime</c>
        // 而 <c>column_type</c> 交 <c>datetime(3)</c>【实测·X-02 第四行两颗并排】。
        // <c>MySqlTypes.GetStoreType</c> 写出去的就是 <c>DATETIME(3)</c> 那一串（型名 + 括号里的位数），
        // 所以本家取 <c>column_type</c> 才与产出同一个词，而<em>不</em>需要第四家那颗
        // <see cref="PostgresStoreType"/> 的改名表 —— 也不需要用例把量纲接回去。
        // <b>大小写是本家另一颗要钉的差</b>：目录交<b>小写</b>而 DDL 发<b>大写</b>【实测·X-02 第四行
        // <c>datetime(3)</c> 对产出里那颗 <c>DATETIME(3)</c>】，而 <see cref="Columns"/> 那一支
        // <em>不</em>在 SQL 里 <c>LOWER()</c>（折了就丢掉"目录里存的到底是哪一串"这一格证人，
        // 与 <see cref="CatalogueName"/> 那一段同一颗纪律），比的是"同一颗词忽略大小写"，
        // 那一步由 <c>DdlTests</c> 在本家那一支点名。
        Target.MySql => "datetime(3)",
        _ => "datetime2",
    };

    /// <summary>库里这一列<em>此刻的形状</em>，两家同一份列名（<c>name</c> / <c>is_nullable</c> /
    /// <c>store_type</c> / <c>scale</c>），因为 <c>DdlTests</c> 那三句逐列比的断言只认这四个词。
    ///
    /// <para>T-SQL 一支还是原来那一句 <c>sys.columns JOIN sys.types</c>。虚谷一支读
    /// <c>all_columns</c> 的五颗字段，其中三颗是本版探针量出来的：<b>可空性那颗要反着折</b>
    /// （目录里是 <c>not_null</c>，所以 <c>= 1</c> 交 <c>'0'</c>）—— 而这一颗比较<em>真的在分岔</em>：
    /// 同一张 <c>QUICKSQLORDER</c> 按 <c>not_null = 0</c> 数到 3 列、按 <c>= 1</c> 数到 5 列，两向相加正好是
    /// 八列（F-41b/F-41c）；只跑一向会把"比较恒假"读成"这一列可空"。<b><c>VARYING</c> 只能在
    /// <c>SELECT</c> 里取、不能在表达式里比</b>：写成 <c>CASE WHEN c."VARYING" = 1</c> 直接
    /// <c>E19260 unexpected VARYING, expecting SCONST</c>（F-41 那一红），所以它交回 CLR 的
    /// <c>True</c>/<c>False</c> 再由 <see cref="XuguStoreType"/> 折。</para>
    ///
    /// <para><b>第七家读的是 <c>pragma_table_info(@tbl)</c> 那六颗字段</b>【实测·4707 的表头：
    /// <c>cid ∶ name ∶ type ∶ notnull ∶ dflt_value ∶ pk</c>】，三件事要说死：
    /// ① <b>那颗 pragma 收绑定参数</b>【实测·4503 ∶ 4707】，所以这一支不需要把表名字面量拼进语句；
    /// ② <b>名字不折</b>：表与列都按建表时那份拼法存【实测·4702 ∶ 4704，对照组 4703 ∶ 4705 各交 0 行】；
    /// ③ <b>这里没有"位数"这一颗字段</b>，所以 <c>scale</c> 交回空串而<em>不是</em>现编一个 3 ——
    /// 空串在本层读作"目录里没有这一格"，那一格归 §14。<c>pk</c> 是本家多交的一颗，
    /// 它是"这把键在不在"在这一家唯一问得出的形（<see cref="PrimaryKeyNamed"/> 那一段）。</para></summary>
    public static List<Dictionary<string, string>> Columns(string table)
    {
        Ready();

        if (Family == Target.Sqlite)
            return Rows(
                "SELECT \"name\", \"type\", \"notnull\", \"pk\" FROM pragma_table_info(@tbl) ORDER BY \"cid\"",
                ("@tbl", AssumeName(table)))
                .Select(r => new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = r["name"],
                    // 本家那颗字段叫 notnull，方向与 is_nullable 相反，折一次（与第五家那颗同一手）。
                    ["is_nullable"] = r["notnull"] == "1" ? "0" : "1",
                    ["store_type"] = r["type"],
                    ["scale"] = "",
                    ["pk"] = r["pk"],
                }).ToList();

        if (Family == Target.SqlServer)
            return Rows(
                "SELECT col.name, CAST(col.is_nullable AS char(1)) AS is_nullable, ty.name AS store_type, " +
                "col.max_length, col.precision, col.scale " +
                "FROM sys.columns col JOIN sys.types ty ON ty.user_type_id = col.user_type_id " +
                "WHERE col.object_id = OBJECT_ID(@table) ORDER BY col.column_id",
                ("@table", table));

        // 第四家读 information_schema.columns 那一份目录：四颗字段要折成本层那四个词，而两处不是白送的：
        // ① <b>可空性那颗是文本 <c>'YES'/'NO'</c></b>，不是 T-SQL 那颗 bit 也不是第五家那颗 not_null，
        //   所以 CASE 在这一支自己折一次；② <b>名字与量纲拆在两颗字段里</b>（<c>data_type</c> 交
        //   <c>timestamp without time zone</c> 这一串全长名，<c>udt_name</c> 交 <c>timestamp</c>），
        //   而用例比的是 DDL 写出去的那一个词，于是 PostgresStoreType 那四条改名是本家目录与
        //   <c>PostgreSqlTypes.GetStoreType</c> 之间唯一的落差（折不出来的一律原样还，与第五家同一条纪律）。
        // 位数那一格本家问得出来：<c>datetime_precision</c> 对 <c>TIMESTAMP(3)</c> 交 3、
        // <c>numeric_scale</c> 对 <c>NUMERIC(18,4)</c> 交 4，两句 COALESCE 成一颗 scale 字段——
        // 这一格与第七家那条"目录里没有位数这颗字段"正好相反，所以 <c>DdlTests</c> 在本家走的是
        // "位数=3"那一条臂而不是空串那一条。
        if (Family == Target.PostgreSql)
            return Rows(
                "SELECT c.column_name AS \"name\", " +
                "CASE WHEN c.is_nullable = 'YES' THEN '1' ELSE '0' END AS \"is_nullable\", " +
                "c.data_type AS \"raw_type\", c.udt_name AS \"udt\", " +
                "COALESCE(c.numeric_scale::text, c.datetime_precision::text, '') AS \"scale\" " +
                "FROM information_schema.columns c " +
                "WHERE c.table_schema = current_schema() AND c.table_name = @tbl " +
                "ORDER BY c.ordinal_position",
                ("@tbl", AssumeName(table)))
                .Select(r => new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = r["name"],
                    ["is_nullable"] = r["is_nullable"],
                    ["store_type"] = PostgresStoreType(r["raw_type"], r["udt"]),
                    ["scale"] = r["scale"],
                }).ToList();

        // <b>MySQL 这一家读 <c>information_schema.columns</c> 那六颗字段，三件事各自与邻家不同</b>：
        // ① <b>型名与量纲在同一颗字段里</b>（<c>column_type</c> 交 <c>decimal(18,4)</c> ∶
        //   <c>datetime(3)</c> ∶ <c>int</c>），所以本家不写改名表，见 <see cref="TimeStoreType"/> 那一段
        //   【实测·X-02 七行】；② <b>可空性那颗与第四家同形（<c>'YES'/'NO'</c> 文本）而<em>不</em>能照抄
        //   那句 CASE</b> —— 本家的 <c>is_nullable</c> 已经是 <c>'YES'/'NO'</c>，折成 <c>'1'/'0'</c>
        //   只为对齐本层那四个词【实测·X-02 第五行 <c>PaidAt ∶ IS_NULLABLE=YES</c> 对第四行
        //   <c>CreatedAt</c>（DDL 里没写 NULL 而它带 <c>NULL</c> 关键字）交 <c>NO</c> —— 两向都有】；
        // ③ <b>位数这一格本家问得出来，而两颗字段会互相冒充</b>：<c>numeric_scale</c> 对一颗
        //   <c>int</c> 列交 <c>0</c> 而<em>不是</em> <c>NULL</c>【实测·X-02 第一行】，所以第四家那句
        //   <c>COALESCE(numeric_scale, datetime_precision)</c> 照抄过来会把每一颗整数列的位数写成 0，
        //   本家因此按 <c>data_type</c> 分派：定点取 <c>numeric_scale</c>、时刻取
        //   <c>datetime_precision</c>、其余交空串（空串在本层读作"目录里没有这一格"，与第七家同一条）；
        // ④ <b>这一句的两个筛选条件都落在大小写<em>不敏感</em>的那一档上，而它旁边那颗表级视图不是</b>：
        //   <c>columns.column_name</c> 的排序规则是 <c>utf8mb3_tolower_ci</c>【实测·Y-02】，
        //   于是同一颗列按 <c>'MiXeDcOl'</c> ∶ <c>'MIXEDCOL'</c> ∶ <c>'mixedcol'</c> 问<em>三形全交 1</em>
        //   【实测·Y-04】，而 <see cref="TableExists"/> 问的那颗 <c>tables.table_name</c> 是
        //   <c>utf8mb3_bin</c>、三形只中原名那一形【实测·Y-03】。<b>这一差不是一桩怪癖，是一颗会咬人的坑</b>：
        //   本台 <c>lower_case_table_names = 0</c>【实测·S-04 第三格】，所以 <c>`QuickSqlProbe`</c> 与
        //   <c>`quicksqlprobe`</c> 两张表在同一座库里<em>同时建得成</em>【实测·Y-07 那一支不报重名】，
        //   而按 <c>LOWER(table_name) = 'quicksqlprobe'</c> 去问列清单会把<b>两张表的列并成一份</b>
        //   （3 行而不是 2 行【实测·Y-09 那三行 <c>t</c> 一颗就写着两份拼法】）。
        //   <b>所以本句按 <c>=</c> 筛而<em>不</em>许先 <c>LOWER()</c> 一把</b>：折了不会让本句更宽，
        //   只会让它把两张表读成一张 —— 而 <see cref="TableExists"/> 那一侧仍然分得出，
        //   于是"夹具建了两张、裁判以为建了一张"这种红既查不下去也留不住证人。
        //   <b>"库里存的是哪一份拼法"这一格在本家唯一的证人是把那颗字段<em>读出来</em>比</b>
        //   （Y-09 交的 <c>MiXeDcOl</c> 就是那一颗），大写对照那一撞在第四家有【C-02】、在本家做不出来。
        // <b>别名必须自己写</b>：不写别名时本家把结果集的列名折成<em>大写</em>（视图定义那一侧的拼法，
        // 【实测·X-02 的表头 <c>COLUMN_NAME|DATA_TYPE|…</c> 全部大写】），而别名交回的是写出去的那一形
        // 【实测·X-13 的 <c>verbatim|upper</c>】—— 于是 <see cref="Rows"/> 那颗键比较器在本家
        // 仍是 <c>Ordinal</c>（第七家同判据），代价只是这六颗别名要一颗不落。
        if (Family == Target.MySql)
            return Rows(
                "SELECT c.column_name AS name, " +
                "CASE WHEN c.is_nullable = 'YES' THEN '1' ELSE '0' END AS is_nullable, " +
                "c.column_type AS store_type, " +
                "CASE WHEN c.data_type IN ('decimal', 'numeric') THEN CAST(c.numeric_scale AS CHAR) " +
                "     WHEN c.data_type IN ('datetime', 'timestamp', 'time') THEN CAST(c.datetime_precision AS CHAR) " +
                "     ELSE '' END AS scale " +
                "FROM information_schema.columns c " +
                "WHERE c.table_schema = DATABASE() AND c.table_name = @tbl " +
                "ORDER BY c.ordinal_position",
                ("@tbl", AssumeName(table)));

        var raw = Rows(
            "SELECT c.col_no, c.col_name, c.type_name, c.scale, c.not_null, c.\"VARYING\" " +
            "FROM all_columns c, all_tables t WHERE t.table_name = @tbl AND c.table_id = t.table_id ORDER BY c.col_no",
            ("@tbl", Fold(AssumeName(table))));

        return raw.Select(r => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = r["col_name"],
            ["is_nullable"] = r["not_null"] == "True" ? "0" : "1",
            ["store_type"] = XuguStoreType(r["type_name"], r["scale"], r["varying"]),
            ["scale"] = r["scale"],
        }).ToList();
    }

    /// <summary>目录里的型名折回<em>DDL 写出去的那一个词</em>。<b>这是裁判自己的一份折叠，不是
    /// <c>XuguSchemaReader.StoreType</c> 的副本</b>：那一份在被告手里，这一份按 M-01 那八行与黄金样本
    /// <c>golden/xugu/ddl-order.sql</c> 逐列对出来的四个分岔手写，两边各自成立才有对拍的价值。
    /// 四条分岔各有一行日志：<c>INT</c> 在目录里是 <c>INTEGER</c>（M-01 第 1 行），
    /// <c>VARCHAR(n)</c> 与 <c>TEXT</c> 都还成 <c>CHAR</c>、只靠 <c>VARYING</c> 与那颗宽度分开
    /// （M-01 第 2/8 行：一列带 64、一列带 -1），<c>NUMERIC</c> 与 <c>DATETIME</c> 原样
    /// （<c>DATETIME</c> 的 <c>scale</c> 那格量的就是声明的位数，M-01 第 6/7 行）。
    /// <b>折不出来的一律原样还</b>：宁可在下一句逐列比里红，不要在这一颗里猜一个相近的词。</summary>
    static string XuguStoreType(string typeName, string scale, string varying)
        => (typeName, varying) switch
        {
            ("INTEGER", _) => "INT",
            // 变长且<em>有宽度</em>才是 VARCHAR；变长而没有宽度在这一家是 TEXT 的同义字
            // （XuguTypes 那一格引的厂商行：不写长度的 VARCHAR 吃掉本行剩余空间 —— 与 TEXT 不同一档）。
            ("CHAR", "True") when int.TryParse(scale, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width > 0 => "VARCHAR",
            ("CHAR", "True") => "TEXT",
            ("CHAR", _) => "CHAR",
            _ => typeName,
        };

    /// <summary>第四家目录里的型名折回 <c>PostgreSqlTypes.GetStoreType</c> 写出去的那一个词。<b>本家这一份折叠
    /// 只有六颗改名，比第五家那八行窄得多</b>：<c>information_schema</c> 交的是 SQL 标准那一串全长名
    /// （<c>character varying</c> ∶ <c>timestamp without time zone</c>），而 DDL 那一侧写出去的是厂商简称
    /// （<c>varchar(n)</c> ∶ <c>timestamp(3)</c>）—— 同一型在同一进程里被读成两个词，不折就逐列全红。
    /// <b>量纲不在这里折</b>：括号那一段由用例原样接回去（<c>DdlTests</c> 那一句的 <c>declared[at..]</c>），
    /// 所以本方法<em>只</em>还型名那一个词，接回来正好是那一颗。<b>折不出来的一律原样还</b>：与第五家同一条
    /// 纪律 —— 宁可在下一句逐列比里红，不要在这一颗里猜一个相近的词。<c>USER-DEFINED</c> 交回
    /// <c>udt_name</c>：那一档（枚举 ∶ 域 ∶ <c>citext</c>）在本仓库只出现在 §14 的待办里，今天没有对象，
    /// 还它自己而不是抛，是为了它到场时先红在比名那一句而不是红在夹具取行。</summary>
    static string PostgresStoreType(string rawType, string udt)
        => rawType switch
        {
            "character varying" => "varchar",
            "character" => "char",
            "timestamp without time zone" => "timestamp",
            "timestamp with time zone" => "timestamptz",
            "time without time zone" => "time",
            "USER-DEFINED" => udt,
            var other => other,
        };

    /// <summary>这张表上<em>目录记着的</em>索引名。<c>IX_QUICKSQLORDER_CREATEDAT</c> 在虚谷是原样存着的
    /// （F-42 那两行），所以"声明过的索引真的建出来了"那一句在这一家有对象 —— 而它旁边多出一颗
    /// <c>PK_IDX_S268571790273509822</c>：那一家的主键索引<em>不叫</em> <c>PK_&lt;表名&gt;</c>，
    /// 名字由库自己生成，所以主键那一半走 <see cref="PrimaryKeyNamed"/>，不走这一份名单。</summary>
    ///
    /// <para><b>第七家这份名单里只有真正的二级索引</b>：<c>pragma_index_list(@tbl)</c> 在一张
    /// 刚建好、除了 rowid 主键什么都没有的表上交<em> 0 行</em>【实测·4504】，写过一条
    /// <c>CREATE INDEX "IX_QuickSqlMix_V" ON …</c> 之后交 1 行而且<b>名字就是那一份拼法</b>
    /// 【实测·4736 ∶ 4737】。<b>所以这一家不会把 <c>PK_&lt;表名&gt;</c> 混进来</b>：内联
    /// <c>INTEGER PRIMARY KEY</c> 用的就是 rowid 那一列，库不为它造索引，也就没有第五家那颗
    /// <c>PK_IDX_S268571790273509822</c> 形状的自动生成名。名单干净了，"声明过的索引建出来了"那一句
    /// 在本家问的就是它自己。</para>
    ///
    /// <para><b>第四家的名单里<em>会</em>混进主键，而这是这一族的结构而不是脏数据</b>：具名主键约束
    /// 在本家要由一把唯一索引来落地，那把索引<em>带着约束的名字</em> —— 一张带
    /// <c>CONSTRAINT "PK_QuickSqlProbeD" PRIMARY KEY ("Id")</c> 与一条 <c>"IX_QuickSqlProbeD_PaidAt"</c> 的表
    /// 在 <c>pg_indexes</c> 里交 2 行：<c>PK_QuickSqlProbeD</c>（<c>indexdef</c> 写着 <c>CREATE UNIQUE INDEX … USING btree</c>）
    /// 与 <c>IX_QuickSqlProbeD_PaidAt</c>【实测·C-03】，<c>DROP INDEX</c> 掉二级索引之后剩 1 行【实测·C-06b】。
    /// 于是本家的这份名单是<b>四家里最干净的"名字原样"</b>：既没有第五家那颗库自己生成的
    /// <c>PK_IDX_S…</c>，也像第七家一样不折大小写（旁边 <c>C-02</c> 那颗大写对照交 0 行钉的就是这一点）。
    /// <b>这一格对本层那两句判据是加分而不是干扰</b>：<c>DdlTests</c> 那一句"声明过的索引建出来了"用的
    /// 是 <c>Assert.Contains</c>，多出一行不碍事；而主键那一半仍然走 <see cref="PrimaryKeyNamed"/> 问
    /// <c>pg_constraint</c> —— 本家两处都问得出，所以不必像第七家那样把主键那一半换掉。</para></summary>
    public static IReadOnlyList<string> IndexNames(string table)
    {
        Ready();

        return Family switch
        {
            Target.Sqlite => Rows("SELECT \"name\" FROM pragma_index_list(@tbl)", ("@tbl", AssumeName(table)))
                .Select(r => r["name"]).ToArray(),
            Target.SqlServer => Rows("SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name IS NOT NULL", ("@table", table))
                .Select(r => r["name"]).ToArray(),
            // 与 <see cref="TableExists"/> 同一颗模式折法：夹具那批表落在 public（S-09）。
            Target.PostgreSql => Rows(
                    "SELECT i.indexname FROM pg_indexes i WHERE i.schemaname = @sch AND i.tablename = @tbl",
                    ("@tbl", AssumeName(table)), ("@sch", "public"))
                .Select(r => r["indexname"]).ToArray(),
            // <b>MySQL 这一家问 <c>information_schema.statistics</c>，而这份名单里混进两颗"没声明过的名字"</b>：
            // 一张只带具名主键与一条二级索引的表交回三行 —— <c>PRIMARY</c> 与 <c>IX_…_PaidAt</c> 之外
            // 还有一行 <c>FK_QuickSqlProbeT_QuickSqlProbeP</c>【实测·X-09 三行】，那颗是本家<em>为外键自动造的
            // 支撑索引</em>（外键列必须有索引，没有就替你建一条，名字照约束名【实测·X-05 建得成而 X-09 多出一行】）。
            // 两颗都是这一族的结构而不是脏数据，所以本家这份名单<em>不能</em>当"声明过的索引就是这几条"来判：
            // <c>DdlTests</c> 那一句用的是 <c>Assert.Contains</c>，多出来的行不碍事，而主键那一半在本家
            // 问不出名字（<see cref="PrimaryKeyNamed"/> 本家那一段抛，改由 <see cref="PrimaryKeyColumns"/>
            // 问"这把键管哪几列"，实测·P-24）。
            Target.MySql => Rows(
                    "SELECT DISTINCT s.index_name AS index_name FROM information_schema.statistics s " +
                    "WHERE s.table_schema = DATABASE() AND s.table_name = @tbl",
                    ("@tbl", AssumeName(table)))
                .Select(r => r["index_name"]).ToArray(),
            _ => Rows("SELECT i.index_name FROM all_indexes i, all_tables t WHERE t.table_name = @tbl AND i.table_id = t.table_id",
                    ("@tbl", Fold(AssumeName(table))))
                .Select(r => r["index_name"]).ToArray(),
        };
    }

    /// <summary>这条<em>主键约束</em>在不在目录里。两家问的是两张目录、两颗类型词：
    /// T-SQL 问 <c>sys.objects.type = 'PK'</c>，虚谷问 <c>all_constraints.cons_type = 'P'</c>
    /// （那颗 'P' 是 D-01 逐颗数出来的：同一张表上主键那行 <c>CONS_TYPE="P"</c>，外键那行是 'F'）。
    /// <b>名字按原样比，不折大小写之外的事</b>：两边建约束时都写了引号，名字就是那句里的拼法。
    ///
    /// <para><b>第七家这一句抛而不是退化成"在不在"</b>：本家的目录里<em>没有"具名约束"这一颗对象</em> ——
    /// 一条 <c>ALTER TABLE … ADD CONSTRAINT</c> 在这一族是语法错（具名【实测·4509：<c>near "CONSTRAINT": syntax error</c>】、
    /// 不具名也一样【实测·4510：<c>near "FOREIGN": syntax error</c>】），外键只能写在 <c>CREATE TABLE</c>
    /// 的表级子句里（【实测·4508】），而本仓库的 <c>SqliteCodeFirst</c> 发的键是<b>内联</b>那一形
    /// （<c>"Id" INTEGER PRIMARY KEY AUTOINCREMENT</c>【实测·2802】，表级那一支与它语法互斥【实测·3009】），
    /// 所以既没有名字可问、也没有约束对象可问。<b>这一家对应的判据是 <c>pragma_table_info</c> 的
    /// <c>pk</c> 那颗字段</b>【实测·4707 第 1 行 <c>pk=1</c>】，已经并进 <see cref="Columns"/> 的名单里，
    /// 由 <c>DdlTests</c> 按家族取那一支。在这里回 <c>false</c> 会把"这一家没有那一档"读成
    /// "我们的 DDL 没建出主键"，那是两种红里更坏的一种。</para></summary>
    public static bool PrimaryKeyNamed(string name)
    {
        Ready();

        if (Family == Target.Sqlite)
            throw new InvalidOperationException(
                $"sqlite 这一家没有具名约束目录，PrimaryKeyNamed(\"{name}\") 在本家没有对象：" +
                "ALTER ADD CONSTRAINT 是语法错（4509/4510），CodeFirst 发的键是内联形（2802/3009），" +
                "判据要换成 pragma_table_info 的 pk 那颗字段（4707），见本方法的注释。");

        if (Family == Target.MySql)
            throw new InvalidOperationException(
                $"mysql 这一家没有\u201c具名主键\u201d这一颗目录对象，PrimaryKeyNamed(\"{name}\") 在本家没有对象：" +
                "建表时写的那颗名字被库静默改掉了 —— ALTER/CREATE 里写 CONSTRAINT `PK_QuickSqlProbeT`，" +
                "information_schema.table_constraints 交回的那一行叫 PRIMARY（constraint_type='PRIMARY KEY'，" +
                "enforced='YES'，实测·X-03；夹具那一形在本家第二次量到，实测·P-23）。" +
                "与第七家（压根没有具名约束）不同一档：本家的外键约束带名字（实测·X-06），缺的只有主键那一颗。" +
                "判据要换成\u201c这张表上有没有一把主键、它管哪几列\u201d，问法见 " +
                nameof(PrimaryKeyColumns) + "（实测·P-24 那一颗读数），见 DdlTests 本家那一支。");

        using var connection = Open();

        // <b>第四家问 <c>pg_constraint</c> 的 <c>contype = 'p'</c></b>：那张靶表上 <c>CONSTRAINT
        // "PK_QuickSqlProbeD" PRIMARY KEY ("Id")</c> 在目录里就是两行之一 ——
        // <c>QuickSqlProbeD_Id_not_null</c>（<c>contype='n'</c>，那一颗是 <c>NOT NULL</c> 在这族的存法）与
        // <c>PK_QuickSqlProbeD</c>（<c>contype='p'</c>）【实测·C-04】。
        // <b>名字原样比、不折</b>：我们写出去带引号的那一形就是目录里那一形（同 <see cref="IndexNames"/>
        // 那一段的 C-03 ∶ C-02 两颗读数）。这一句与上面的 <c>IndexNames</c> 因此<em>各问一张目录</em>：
        // 那一句问"库为这条约束造的那把唯一索引在不在"，本句问"约束本身带着我们给的名字在场"——
        // 第五家只能问后者（它的索引名是库生成的），第七家两句都问不出，本家四家里唯一一处两句都有对象。
        if (Family == Target.PostgreSql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM pg_constraint WHERE conname = @nm AND contype = 'p'",
                ("@nm", name));

        return Family == Target.SqlServer
            ? Is(connection, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM sys.objects WHERE name = @nm AND type = 'PK'", ("@nm", name))
            : Is(connection, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM all_constraints WHERE cons_name = @nm AND cons_type = 'P'", ("@nm", Fold(name)));
    }

    /// <summary>这一张表上的主键管哪几列（按库给的序）。<b>只有 MySQL 这一支有对象</b>：
    /// 那一家的目录里主键约束<em>不带我们写的名字</em>（<see cref="PrimaryKeyNamed"/> 本家那一段——
    /// X-03 与 P-23 两处各量到名字被库改成 <c>PRIMARY</c>），所以"名字在不在"这一问在本家无从问起，
    /// 而"声明过的主键到底落没落"还剩一颗可问的对象：<c>information_schema.statistics</c> 里
    /// 那把叫 <c>PRIMARY</c> 的索引管哪些列【实测·P-24，交 <c>col=Id ∶ seq=1</c>】。
    /// <b>同一问句的第二颗问法也量过</b>（<c>key_column_usage</c> 按 <c>constraint_name = 'PRIMARY'</c> 筛，
    /// 【实测·P-24b】交出同一批数），取 statistics 这一支是因为它与本家的 <see cref="IndexNames"/>
    /// 问<em>同一张目录</em>，两份读数之间少一层假设。
    ///
    /// <para><b>本家这一格比另三家少的东西要说白</b>：那三家问的是"我们给约束起的那个名字在不在"，
    /// 本家问的只能是"键在不在、管哪几列"——名字那一档被库吃掉了（缺口记 §14，与"这一家没有
    /// rowversion 那一型"并排）。<b>别的家族走到这里抛而不是回一份名单</b>：那三家的判据有名字可问，
    /// 退到这一颗会把"裁判自己没写对"读成"库没有建出主键"，两种红里更坏的那一种。</para></summary>
    public static string[] PrimaryKeyColumns(string table)
    {
        Ready();

        if (Family != Target.MySql)
            throw new InvalidOperationException(
                $"PrimaryKeyColumns 只在 mysql 这一家有对象（那一家的主键约束不带名字，见 {nameof(PrimaryKeyNamed)}），" +
                $"当前目标库是 {Family}；这一家该问的是 PrimaryKeyNamed。");

        // 别名一颗不落：本家不写别名时结果集的列名会被折成大写（X-02 的表头），而 Rows 的键比较器在
        // 本家是 Ordinal（那一段的理由），两句凑一起就会把"问得出"变成"取不到"。
        return Rows(
                "SELECT s.column_name AS col, s.seq_in_index AS seq FROM information_schema.statistics s " +
                "WHERE s.table_schema = DATABASE() AND s.table_name = @tbl AND s.index_name = 'PRIMARY' " +
                "ORDER BY s.seq_in_index",
                ("@tbl", AssumeName(table)))
            .Select(r => r["col"]).ToArray();
    }

    /// <summary>把 <c>"dbo.QuickSqlOrder"</c> 这一形里的表名取出来（<see cref="Columns"/> 的虚谷一支要用）。
    /// <b>只做<em>取名字</em>这一件事</b>：schema 那一半在虚谷的目录里不是同一种问法（<see cref="TableExists"/>
    /// 那一段写着它为什么要 join <c>all_schemas</c>），而这三句裁判问的都是夹具自己建的那批表 ——
    /// 本库里同名跨模式的表今天没有（M2-08 数到 0 组），所以这一颗不补模式闸门，
    /// 补了反而把"这一家模式闸门长什么样"那一格从 <see cref="TableExists"/> 挪到这里、两处各错一次。</summary>
    static string AssumeName(string table)
    {
        var dot = table.LastIndexOf('.');

        return dot < 0 ? table : table[(dot + 1)..];
    }

    static DbConnection Open()
    {
        var connection = Orm.Provider.Connectors.CreateConnection(ConnectionString);

        connection.Open();

        return connection;
    }

    /// <summary>目标库不存在就建一个。连的是 <c>master</c>，且只在 <c>DB_ID</c> 为空时执行 ——
    /// 这个实例上任何别的库都不碰（§13.1 的边界）。
    ///
    /// <para><b>第五家这一支整个跳过，而且跳过是裁决不是欠账</b>：§13.17 那半句授权写的是
    /// "在 <c>TESTDB</c> 里 <c>CREATE</c>、只删本仓库自己建的东西"，<em>建库</em>不在里面 ——
    /// 那一台实例上除了 <c>TESTDB</c> 还有厂商自带的 <c>SECDB</c>（survey 那一段数到的），
    /// 在这里放开建库等于把"只碰一个名字"这条边界自己拆了。所以 <c>TESTDB</c> 必须在场，
    /// 不在场就让夹具红在连接/建表那一步，而不是替谁建一颗库。</para>
    ///
    /// <para><b>第七家这一支同样跳过，但跳过的理由与前两家都不是同一颗</b>：本家<em>没有"库"这一颗对象要建</em>
    /// —— 连接串里那颗文件路径在 <c>Open()</c> 那一刻由驱动自己造出来（<see cref="Sqlite"/> 类注释第三段写的
    /// 正是这一格：任何路径都连得上，默认值不会造成"连不上"的红，只会造成"凭空多一颗文件"）。
    /// 所以本家既不需要 §13.1 那台授权实例，也没有 <c>master</c> 那颗可连的服务器；
    /// <b>而"跳过"在这里不是把判据松开，是这一族根本没有这一步</b>。</para></summary>
    static void PrepareCatalog(DbProviderFactory factory)
    {
        if (Family == Target.Xugu)
        {
            Console.WriteLine("PrepareCatalog：xugu 一支不建库（§13.17 的授权只到建表），目标库必须在场。");

            return;
        }

        if (Family == Target.PostgreSql)
        {
            // <b>本家这一支也跳过，而它跳的是"权限够但授权不到"</b>：那颗连上去的库是 <c>TestDB</c>，
            // 现场读数里这一族确实有建库的权限（<c>has_database_privilege(… 'CREATE')</c> 交 <c>True</c>
            // 【实测·S-06】，模式 <c>public</c> 的建表权同样 <c>True</c>【实测·S-07】），
            // 但 §13.1 那一格给用户的是<em>一座名字确定的库</em>，不是整台实例 —— 在这里放开建库等于
            // 把"只碰一个名字"这条边界自己拆了，与第五家那一格同一判据。
            // 顺带记下这一台的三颗身份读数（本文件与 §13.5 那行都引它们）：内核是
            // <c>PostgreSQL 18.3</c>【实测·S-01 ∶ S-02 = 180003】、库编码 <c>UTF8</c>【实测·S-03】、
            // 排序规则 <c>en_US.utf8</c>【实测·S-04】，而 <c>current_schema()</c> 就是 <c>public</c>
            // 【实测·S-09】—— 后者是 <see cref="Columns"/> 那一句为什么按 <c>current_schema()</c> 筛、
            // 而<em>不</em>照搬契约里那颗 <c>dbo</c> 的原因（本仓库的 <c>PostgreSqlCodeFirst</c> 默认模式写的就是 <c>public</c>）。
            Console.WriteLine("PrepareCatalog：postgresql 一支不建库（授权只到那一座 TestDB 里建表），目标库必须在场。");

            return;
        }

        if (Family == Target.Sqlite)
        {
            Console.WriteLine("PrepareCatalog：sqlite 一支不建库 —— 连接串里那颗文件就是库，Open() 那一刻由驱动造出来（4701 那一形）。"
                + $"目标：{ConnectionString}");

            return;
        }

        // <b>MySQL 这一支与前两家同一判据（授权只到那一座 <c>TestDB</c> 里建表），而它到场的凭据是四颗身份读数</b>：
        // 内核 <c>8.4.8</c>【实测·S-01】、发行 <c>MySQL Community Server - GPL</c>【实测·S-02】—— 后面那一句要紧，
        // 因为这一族的 MariaDB 那一片今天在仓库里只两处在场（§14 行 2029 那颗 "MariaDB 10.5 <c>RETURNING</c>"
        // 的版本号读数、与 <c>MySqlKeys.cs:55</c> 那颗"刻意不是 <c>virtual</c>"的缝，而 §9.1 <em>没有</em>它的行），
        // 本台自报的是 Community Server 而不是 MariaDB，所以本层在这一台上看到的每一格都不许被下一版
        // 读成 MariaDB 的读数 —— 两边今天唯一实测到的差别就是 K-03 那一句：这一台答"没有 <c>RETURNING</c>"。
        // <c>sql_mode</c> 里<em>没有</em> <c>ANSI_QUOTES</c>【实测·S-03】—— 那颗正决定了 <see cref="MySqlText"/>
        // 为什么把方括号换成<em>反引号</em>而不是双引号：同一颗 <c>"X"</c> 在这一台是<em>字符串字面量</em>
        // 【实测·D-02：<c>SELECT "Id"</c> 交回的是 <c>Id</c> 那颗常量，不是一列】。
        // 字符集 <c>utf8mb4</c> ∶ 默认排序规则 <c>utf8mb4_0900_ai_ci</c> ∶ <c>lower_case_table_names=0</c>
        // 【实测·S-04】—— 最后那一颗是 <see cref="TableExists"/> 不许折大小写的根因之一（表名按字节比，
        // 三形对照见【实测·Y-03】），而 <c>root@%</c> 落在 <c>TestDB</c> 上【实测·S-05】。
        if (Family == Target.MySql)
        {
            Console.WriteLine("PrepareCatalog：mysql 一支不建库（授权只到那一座 TestDB 里建表），目标库必须在场。"
                + "实例身份四颗读数（8.4.8 / Community Server - GPL / 无 ANSI_QUOTES / lower_case_table_names=0）"
                + "记在 obj/logs/v37-my-recon-2.log 的 S 段，连接串本身含口令、不进任何一行输出。");

            return;
        }

        // 用驱动自己的连接串构建器改库名：<c>Database</c> 与 <c>Initial Catalog</c> 是同义词，
        // 裸 DbConnectionStringBuilder 会留下两份互相矛盾的键，而它才是最终被解析的那一个。
        var builder = factory.CreateConnectionStringBuilder()
            ?? throw new InvalidOperationException("驱动没有提供连接串构建器，无法只改库名。");

        builder.ConnectionString = SqlServer.TestConnectionString;

        var catalog = builder["Initial Catalog"] as string;

        if (string.IsNullOrEmpty(catalog))
            throw new InvalidOperationException("连接串里没有库名，拒绝在默认库上建表。");

        builder["Initial Catalog"] = "master";

        using var connection = factory.CreateConnection()!;

        connection.ConnectionString = builder.ConnectionString;
        connection.Open();

        // CREATE DATABASE 不能带变量名，也不能在创建它的批次里立刻被引用 → 走动态 SQL 单独一批。
        Execute(connection,
            "IF DB_ID(@db) IS NULL BEGIN DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC sys.sp_executesql @sql; END",
            ("@db", catalog));
    }

    /// <summary>夹具那条产路的公开口：按契约建一张<em>本层用例自己的</em>靶表（幂等，表在就不动）。
    /// <b>public 只为一个场景开：用例要"这一句 DDL 在本次运行里真执行过"</b> —— 夹具的建表链对已经存在的表
    /// 直接返回（<see cref="EnsureInventoryItem"/> 的注释里那一句"旧机器上会被绕过"），
    /// 所以换一张表名跑同一条产路，才是那句 DDL 每次必过的证人。判据与 <see cref="Execute"/> 同一半：
    /// 靶表由调用这一句的用例自己建、自己按名字管，不碰夹具那批按标签建的行。</summary>
    public static void EnsureTable(Type clrType)
    {
        Ready();

        using var connection = Orm.Provider.Connectors.CreateConnection(ConnectionString);

        connection.Open();

        EnsureTable(connection, Orm, clrType);
    }

    static void EnsureTable(DbConnection connection, QuickSqlOrm orm, Type clrType)
    {
        var contract = orm.Schemas.Contract(clrType);
        var table = contract.Table;

        if (TableExists(connection, table))
            return;

        var writer = new SqlWriter(new StringBuilder(), [], orm.Provider.Dialect.ParameterPrefix);

        orm.Provider.CodeFirst.EmitCreateTable(writer, contract);

        // DDL 到这里才算真跑过：黄金样本只保证文本没写错，括号多一个要在这一句才炸出来（§9.2）。
        Execute(connection, writer.ToSqlString());
    }

    /// <summary>按<em>已经换算好的那张片</em>问一次"库里有没有"。<b>公开口只为分表用例开</b>（§16.4）：
    /// 片名是从模板算出来的，用例侧拿不到 <see cref="EnsureTable(Type)"/> 那种"给个 CLR 类型就行"的形状 ——
    /// 给类型只能问回那颗模板，而模板今天在任何库里都不该存在（它存在的本身就是缺陷）。
    /// <b>这里不写第二份目录 SQL</b>：复用私有那颗 <see cref="TableExists(DbConnection,TableInfo)"/>，
    /// 五家的问法（<c>OBJECT_ID</c> ∶ <c>pg_class</c> ∶ <c>ALL_TABLES</c> ∶ <c>information_schema.tables</c>）
    /// 只在一处维护；用例里各写一句就等于把方言分岔抄进测试层，而那一抄就量不到实现里的那份。</summary>
    public static bool HasTable(TableInfo table)
    {
        Ready();

        using var connection = Orm.Provider.Connectors.CreateConnection(ConnectionString);

        connection.Open();

        return TableExists(connection, table);
    }

    /// <summary>这一句问句交回 1 就是"是"。<b>不写成 <c>is 1</c></b>：这一家的 <c>COUNT(*)</c> 与
    /// <c>CASE … THEN 1 END</c> 回包是 <c>Int64</c>（<c>obj/logs/v32-recon-txn-3.log</c> 的 T-03 量到
    /// <c>ExecuteScalar</c> 交 <c>Int64</c>），装箱模式匹配 <c>is 1</c> 只认 <c>Int32</c>，
    /// 于是同一句判据在两家会一个绿一个恒红。转一下而不是再分岔一次。</summary>
    static bool Is(DbConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var scalar = Scalar(connection, sql, args);

        return scalar is not null and not DBNull && Convert.ToInt64(scalar, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>这张表在不在。<b>两家的问法不是"同一句换个写法"，是问的两张目录</b>：
    /// T-SQL 侧一颗 <c>OBJECT_ID</c> 就够（它把 schema/name 两段一起收）；这一家的 <c>ALL_TABLES</c>
    /// 里<em>没有</em> <c>OWNER</c> 那一颗（K-15 那一红），模式那颗要 <c>SCHEMA_ID</c> join
    /// <c>ALL_SCHEMAS</c>，而"模式没写"那一支走 <c>NVL(:sch, UPPER(USER))</c> —— 整个形状在
    /// <c>obj/logs/v32-recon-txn-3.log</c> 的 T-05 上跑绿过（就是这一句，参数名也叫 <c>tbl</c>/<c>sch</c>）。
    /// <b>名字要先折成大写</b>：目录里存的是大写形，不折就直接查不到（M2-07 交 0 行 / M2-05 交 19 行），
    /// 而 <see cref="Translate"/> 只管<em>文本里</em>的标识符字形、管不到<em>绑进去</em>的值，
    /// 所以这一颗折叠在夹具里再写一遍，与 <c>XuguSchemaReader.Fold</c> 同一个理由。</summary>
    static bool TableExists(DbConnection connection, TableInfo table)
    {
        if (Family == Target.SqlServer)
            return Is(connection, "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END",
                ("@name", (table.Schema ?? "dbo") + "." + table.Name));

        // <b>第四家问的是 <c>pg_class</c> JOIN <c>pg_namespace</c>，两颗字段都按原样比</b>：
        // 本家的目录把带引号建出来的名字原样存【实测·C-01 交 8 行 ∶ C-02 同句大写形交 <b>0 行</b>】，
        // 所以这里<em>不能</em>套第五家那颗 <c>Fold</c> —— 一折就变成"永远答不存在"，
        // 而那种红读起来像夹具没建表，真相只是裁判折错了方向。
        // 模式那一颗在 C# 侧折成 <c>public</c>（而不是把 <c>null</c> 绑进去让 <c>COALESCE</c> 去兜）：
        // 契约里 <c>[Table]</c> 没写模式时交 <c>null</c>，而本家的 DDL 把那种情况落在 <c>public</c>
        // （<c>PostgreSqlCodeFirst.EmitTable</c> 那一句 <c>table.Schema ?? "public"</c>），裁判照同一句折，
        // 现场那颗 <c>current_schema()</c> 也正是 <c>public</c>【实测·S-09】—— 三个来源在同一格上应当同值，
        // 而把一颗无类型的 null 送进扩展协议只会多出一个"参数型别判不出"的问号（§9.3 那条纪律的同一颗）。
        // <c>relkind = 'r'</c> 那一格是"普通表"，与本层要判的对象同宽（视图与外部表不该算建好了）。
        if (Family == Target.PostgreSql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM pg_class c " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE c.relname = @tbl AND c.relkind = 'r' AND n.nspname = @sch",
                ("@tbl", table.Name), ("@sch", table.Schema is { Length: > 0 } schema ? schema : "public"));

        // <b>MySQL 这一家问 <c>information_schema.tables</c>，而这一颗问句是本家唯一一处
        // "名字按<em>原样</em>分得出"的目录问句</b>：那颗 <c>table_name</c> 字段落在
        // <c>utf8mb3_bin</c> 上【实测·Y-01 那颗 <c>COLLATION(table_name)</c> 交回的词】，
        // 于是同一张表按 <c>'QuickSqlProbe'</c> 问交 1、按 <c>'QUICKSQLPROBE'</c> 与按
        // <c>'quicksqlprobe'</c> 问各交 <b>0</b>【实测·Y-03 三形并排】—— 与第四家那对
        // <c>C-01 ∶ C-02</c> 同一形状而来路不同（那一族是"带引号原样存 + <c>=</c> 二进制比"，
        // 本家是视图那一侧就把比较定成了 <c>bin</c>）。所以这里既不能套 <see cref="Fold"/>，
        // 也不许在 SQL 里 <c>LOWER()</c> 一把再问 —— 后者会把两件事一起取消：这一句的"原样"证人，
        // 以及下面 <see cref="Columns"/> 那一段写的那颗本家特有的坑。
        if (Family == Target.MySql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM information_schema.tables " +
                "WHERE table_schema = DATABASE() AND table_name = @tbl AND table_type = 'BASE TABLE'",
                ("@tbl", table.Name));

        if (Family == Target.Sqlite)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM main.sqlite_master " +
                "WHERE \"type\" = 'table' AND \"name\" = @tbl",
                ("@tbl", SqliteTableName(table)));

        return Is(connection,
            "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM all_tables t, all_schemas s " +
            "WHERE t.table_name = @tbl AND s.schema_id = t.schema_id AND s.schema_name = NVL(@sch, UPPER(USER))",
            ("@tbl", Fold(table.Name)), ("@sch", Fold(table.Schema)));
    }

    /// <summary>本家那一颗表名闸门：<c>main</c> 之外只有一种说法可问，而<em>那种说法今天没有对象</em>。
    /// <b>为什么不是"顺手把 schema 拼上去"</b>：<c>app.t46</c> 那种跨库问句要先有一条
    /// <c>ATTACH DATABASE … AS app</c>（第四十六轮就是这么造的【实测·4600 ∶ 4601】），而本层没有 attach 过任何东西，
    /// <c>main</c> 就是唯一那座库（<c>pragma_database_list</c> 在本场交回 <c>main(0)</c> 一行【实测·4601 同一颗问句】）。
    /// 于是"契约里带着非 main 的模式名"这一形在本家<em>问不出结果</em>，只能抛而不能当成没写：
    /// 当成没写就是"两张库里的同名表被读成一张"，而那正是 <see cref="TableExists"/> 要防的那件事。</summary>
    static string SqliteTableName(TableInfo table)
        => table.Schema is null or "" || table.Schema.Equals("main", StringComparison.OrdinalIgnoreCase)
            ? table.Name
            : throw new InvalidOperationException(
                $"sqlite 这一家的本层只认 main（没有 ATTACH 过任何库），而契约里的模式是 \"{table.Schema}\"。" +
                "要跨库问句请先在夹具里 ATTACH 一座，见 SqliteTableName 的注释。");

    /// <summary>这一列在不在（<see cref="EnsureInventoryItem"/> 补那一颗后加的列要用）。
    /// 这一家没有 <c>COL_LENGTH</c>，同一条判据要三张目录连起来问，而它正是
    /// <c>XuguSchemaReader.ProbeSql</c> 的那个形状 —— 区别只在夹具问"有没有"、探测问"是什么形"。
    ///
    /// <para><b>第七家一颗 pragma 就够，而对照组钉的是"名字不折"这一格</b>：
    /// <c>SELECT COUNT(*) FROM pragma_table_info(@tbl) WHERE "name" = @col</c> 按建表那份拼法问
    /// <c>'V'</c> 交 1【实测·4704】，改问 <c>'v'</c> 交 <b>0</b>【实测·4705】。
    /// 这颗对照组要紧：本家的 <c>=</c> 在文本列上是大小写<em>敏感</em>的【实测·4717：<c>'A' = 'a'</c> 交 0
    /// 而 <c>'A' LIKE 'a'</c> 交 1】，所以"目录里存的到底是哪一串"在这一家问得出来，
    /// 而把它做成大小写不敏感就等于把这两颗读数一起取消。</para></summary>
    static bool ColumnExists(DbConnection connection, TableInfo table, string column)
    {
        if (Family == Target.SqlServer)
            return Is(connection, "SELECT CASE WHEN COL_LENGTH(@t, @c) IS NULL THEN 0 ELSE 1 END",
                ("@t", (table.Schema ?? "dbo") + "." + table.Name), ("@c", column));

        if (Family == Target.Sqlite)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM pragma_table_info(@tbl) WHERE \"name\" = @col",
                ("@tbl", SqliteTableName(table)), ("@col", column));

        // <b>第四家读 <c>pg_attribute</c>，三颗闸门一起写</b>：<c>attnum > 0</c> 脱掉系统列（<c>ctid</c> 那一排
        // 在本家也是"列"，不脱就会把它们数进去），<c>NOT attisdropped</c> 脱掉被 <c>DROP COLUMN</c> 留下的
        // 占位（那一颗在本家叫 <c>xxxxxxxxdrop</c> 形的隐藏列，是这一族目录的真实形状）。
        // 名字仍然<em>原样比</em>【实测·C-05：那张表交回的八颗正是 <c>Id ∶ Name ∶ … ∶ PaidAt</c> 那一份拼法】，
        // 而本家的 <c>=</c> 在文本上是大小写敏感的，所以这一句同时是"没折"的证人 ——
        // 与第七家那两颗 <c>4704 ∶ 4705</c> 对照同一地位（本家那颗对照是上面 <c>C-02</c> 的表级版本）。
        // 模式那一颗与 <see cref="TableExists"/> 同一折法（<c>public</c>），两句不能各折一份。
        if (Family == Target.PostgreSql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM pg_attribute a " +
                "JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE c.relname = @tbl AND a.attname = @col AND a.attnum > 0 AND NOT a.attisdropped " +
                "AND n.nspname = @sch",
                ("@tbl", table.Name), ("@col", column),
                ("@sch", table.Schema is { Length: > 0 } schema ? schema : "public"));

        // <b>MySQL 这一家读 <c>information_schema.columns</c> 那两颗名字字段，判据与
        // <see cref="Columns"/> 那一段第 ④ 条同一颗而这里更要紧</b>：本句比较用的排序规则是
        // <c>utf8mb3_tolower_ci</c>【实测·Y-02】，所以 <c>@col</c> 绑哪一份拼法都打得中
        // （三形全交 1【实测·Y-04】）。<b>也就是说这一颗闸门在本家答的是"这一列在不在"，
        // 答不出"库里那一列叫什么"</b> —— 后者归 <see cref="Columns"/>（把字段读出来比）。
        // 这一格不留白：<see cref="EnsureInventoryItem"/> 那一句"补一颗后加的列"要的正是前者，
        // 而它旁边那颗 <c>table_name</c> 筛选仍然按 <c>=</c> 原样问（不折，理由同第 ④ 条）。
        if (Family == Target.MySql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = @tbl AND column_name = @col",
                ("@tbl", table.Name), ("@col", column));

        return Is(connection,
            "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM all_columns c, all_tables t, all_schemas s " +
            "WHERE t.table_name = @tbl AND c.table_id = t.table_id AND c.col_name = @col " +
            "AND s.schema_id = t.schema_id AND s.schema_name = NVL(@sch, UPPER(USER))",
            ("@tbl", Fold(table.Name)), ("@col", Fold(column)), ("@sch", Fold(table.Schema)));
    }

    /// <summary>这条外键约束在不在目录里（<em>不</em>问"生效没有"，下一颗方法问）。
    /// <c>ALL_CONSTRAINTS.CONS_NAME</c> 那一颗是 K-13 数到的 16 颗字段之一。
    ///
    /// <para><b>第七家与 <see cref="PrimaryKeyNamed"/> 同一颗理由抛</b>：本家的目录里没有"具名约束"这一颗对象，
    /// 外键的形状是<em>表定义里的一条表级子句</em>（【实测·4508】），问得到的只有"这张表上挂着哪几条外键、
    /// 指向哪张父表"（<c>pragma_foreign_key_list(@tbl)</c> 收绑定参数【实测·4505】，交回
    /// <c>id ∶ seq ∶ table ∶ from ∶ to</c> 五颗而<b>没有名字那一颗</b>）。按名字问在这一家没有对象，
    /// 而这一句的两个调用点都在 <see cref="EnsureProjectForeignKey"/> 里，那颗方法在本家整颗走不到。</para></summary>
    static bool ConstraintPresent(DbConnection connection, string name)
    {
        RequireNamedConstraintSupport(nameof(ConstraintPresent), name);

        // 与 <see cref="PrimaryKeyNamed"/> 那一句同一张目录、换一颗类型词：<c>'f'</c> 是外键
        // —— 事后 <c>ADD CONSTRAINT … FOREIGN KEY</c> 在本家建得成【实测·C-09 那一绿】，
        // 建完目录里就是 <c>conname=FK_QuickSqlProbeC_QuickSqlProbeP / contype='f' / convalidated=True</c>
        // 【实测·C-10】。<b>这两句"按名字问约束"在本家都有对象</b>，所以 <see cref="RequireNamedConstraintSupport"/>
        // 那颗闸门在本家不开 —— 与第七家（连语句都没有）正好分居这条缝的两端。
        if (Family == Target.PostgreSql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM pg_constraint WHERE conname = @nm AND contype = 'f'",
                ("@nm", name));

        // <b>MySQL 这一家问 <c>information_schema.table_constraints</c> 那一颗类型词
        // <c>'FOREIGN KEY'</c>，而本家这一句<em>问得出名字</em>（与主键那一颗正相反）</b>：
        // 一条 <c>ALTER TABLE … ADD CONSTRAINT FK_… FOREIGN KEY</c> 建完之后，目录里那一行的
        // <c>constraint_name</c> 就是我们写出去的那一串【实测·X-05 建得成 ∶ X-06 交回
        // <c>FK_QuickSqlProbeT_QuickSqlProbeP</c>】，所以 <see cref="RequireNamedConstraintSupport"/>
        // 在本家不开（第七家连语句都没有，本家两句都有对象，与第四家同一侧）。
        // <b>名字的比较在这一家<em>不</em>能当"原样比"的证人</b>：那颗字段落在 <c>utf8mb3_tolower_ci</c>
        // 上【实测·W-01，同一颗问句在 referential_constraints 上也是它【实测·W-02 ∶ W-12】】，
        // 三形各问一次全部命中【实测·W-03 原名 1 ∶ 全大写 1 ∶ 全小写 1】—— 也就是说这一句答"在不在"，
        // 而<em>答不出</em>"库里存的是哪一份拼法"（那一格的唯一证人是把字段<em>读出来</em>比，
        // 【实测·W-12 交回的就是 <c>FK_QuickSqlProbeW_QuickSqlProbeV</c> 那一份原样】）。
        // <b>与 <c>columns.column_name</c> 同侧、与 <c>tables.table_name</c> 正相反</b>：后者是
        // <c>utf8mb3_bin</c>、三形只中原名【实测·Y-01 ∶ Y-03】，所以本文件里"表名按原样问"与"约束名
        // 问得出三形"两句<em>不是</em>同一条规则，照抄任何一方都会把另一边写错。
        // <b>这一句刻意不筛 <c>table_name</c>，而那一手是量出来的</b>：外键名在本家<em>按库唯一</em> ——
        // 拿同一颗名字去第二张表上挂，库正面拒 <c>Duplicate foreign key constraint name '…'</c>
        // 【实测·W-19】，于是按名字不带表数到的就是那<em>一颗</em>【实测·W-20 交 1 ∶ W-21 带表名的两形 1 ∶ 0】。
        // 反过来说，那一颗字段在 C# 侧也没有可依赖的换算 —— 约束名
        // <c>FK_&lt;子表&gt;_&lt;父表&gt;</c> 是本仓库的写法而不是库的规则，按它拆表名等于让裁判去猜
        // 产出的命名法。类型词那一颗闸门留住之后，本句问的仍然只是"这条外键在不在"，
        // 与 <see cref="ForeignKeyActive"/> 各问一档。
        if (Family == Target.MySql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM information_schema.table_constraints " +
                "WHERE constraint_schema = DATABASE() AND constraint_name = @nm " +
                "AND constraint_type = 'FOREIGN KEY'",
                ("@nm", name));

        return Family == Target.SqlServer
            ? Is(connection, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM sys.objects WHERE name = @nm AND type = 'F'", ("@nm", name))
            : Is(connection, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM all_constraints WHERE cons_name = @nm", ("@nm", Fold(name)));
    }

    /// <summary>这条外键现在是<em>启用</em>的吗。<b>判据用"没禁用"而不是"名字在不在"</b>：
    /// 一条禁用的约束不挡任何语句，只看名字会得到一个假绿的裁判（这一句是 v0.13 就写下的理由）。
    ///
    /// <para><b>v0.32 第二格把这一句在虚谷补齐了，而它差一点就补齐不了</b>：K-13 只数到目录里<em>有</em>
    /// <c>ENABLE</c> 与 <c>VALID</c> 两颗布尔，问不出"一颗布尔列在 SQL 里怎么比" ——
    /// 上一版因此把这一家退化成 <see cref="ConstraintPresent"/>（只问在不在），并把"问不出活没活"记成缺口。
    /// 本版探针把那一句问出来了（<c>obj/logs/v32-recon-funcs-8.log</c> 的 F-43 三颗）：
    /// <c>cons_name = :nm AND enable = 1</c> 对一颗存在的约束交 <b>1</b>（F-43），
    /// 换一颗不存在的名字交 <b>0</b>（F-43b，闸门不是自证），同一颗存在的改 <c>enable = 0</c> 交 <b>0</b>
    /// （F-43c，那颗比较真的在分岔而不是恒真）。三颗合起来才是"这一形可用"的凭据 —— 只跑正向那一句
    /// 会把"比较恒真"读成"约束活着"。</para>
    ///
    /// <para><b>而它不 participate 判"库里挡不挡"</b>：目录怎么写是一件事，约束真的挡语句是另一件事，
    /// 后者归 <c>OrderingPathTests</c> 那句"绕开会话插一行没有父的必须抛"。本方法红了就是夹具自己
    /// 要重建裁判（下面 <see cref="EnsureProjectForeignKey"/> 的删加两支因此都到得了）。</para>
    ///
    /// <para><c>VALID</c> 那一颗仍然没并进来：本家 <c>ADD CONSTRAINT</c> 没有 <c>NOCHECK</c> 可写，
    /// 于是"加了但没校验"那一档<em>造不出来</em>，判据里多一颗也就无从验证 —— 记 §14。</para>
    /// <b>顺带一颗没在这条路上但同族的字面量比较</b>（同一批量的，写在这里免得下一版再问一遍）：
    /// <c>NUMERIC(18,4)</c> 的 <c>0.0001</c> 与 <c>0.00000…</c> 逐字相等，<c>.123</c> 与 <c>0.123</c> 相等、
    /// 与 <c>.124</c> 不等（F-43d/F-43e）。§10.4 那一族"CAST 截成 0 而不报错"在这一家没撞上。</para>
    ///
    /// <para><b>第七家没有这一问，但<em>那一档的性质</em>在这一家反而是量到了的</b>：本家问不出"一条叫这个名字的
    /// 约束活不活"（<see cref="ConstraintPresent"/> 那段），可"外键到底挡不挡语句"这一问在本家有直接读数 ——
    /// <b>不必先开那颗开关</b>：<c>PRAGMA foreign_keys</c> 在两颗新连接上都交 <c>1</c>
    /// 【实测·4513 ∶ 4518】，连接串里写 <c>Foreign Keys=True</c> 也交 <c>1</c>【实测·4519】，
    /// 而<em>什么开关都没碰</em>的那一颗连接上插一条没有父的行就被挡下【实测·4514：<c>SQLite Error 19:
    /// 'FOREIGN KEY constraint failed'</c>】，开关之后再插仍是同一颗错【实测·4517/4520】。
    /// 也就是说本家"目录怎么写"与"库里挡不挡"之间没有第五家那道<em>可关</em>的缝 ——
    /// 那条缝在本仓库是 <c>ALTER TABLE … DROP CONSTRAINT</c> 那一档能造出来的，而本家连语句都没有【实测·4509】。</para></summary>
    static bool ForeignKeyActive(DbConnection connection, string name)
    {
        RequireNamedConstraintSupport(nameof(ForeignKeyActive), name);

        // <b>本家没有"禁用一条约束"那颗开关，所以判据换的是<em>档</em>而不是<em>词</em></b>：
        // T-SQL 与虚谷问的是 <c>is_disabled = 0</c> / <c>enable = 1</c>，而这一族能造出来的最近似一档是
        // <c>NOT VALID</c> —— 加约束时带它，目录里那颗 <c>convalidated</c> 就交 <c>False</c>，
        // 不带则交 <c>True</c>【实测·C-14 与 C-10 是同一颗字段的两向读数，一句正向一句负向，
        // 与第五家那三向对照（F-43/F-43b/F-43c）同一纪律】。
        // <b>为什么用它顶这一格而不是退化成 <see cref="ConstraintPresent"/></b>：本层要判的是
        // "目录写着活、库里是不是真挡"，而 <c>convalidated = False</c> 恰恰是"在而不挡"在这一族唯一的形状 ——
        // 拿它当证人比只问在不在强一档。<b>代价要说死</b>：本家这一颗<em>不</em>等价于那边那颗
        // <c>enable</c> —— 一条 NOT VALID 的外键仍然挡<em>新写入</em>，只是不校验历史行，
        // 而"禁用"在 T-SQL 是新旧都不挡。所以"这一档在四家各自意味着什么"记进 §14，
        // 而不是在这里替这一族补一个不存在的开关。
        if (Family == Target.PostgreSql)
            return Is(connection,
                "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM pg_constraint " +
                "WHERE conname = @nm AND contype = 'f' AND convalidated",
                ("@nm", name));

        // <b>MySQL 这一家在这一档上退化成"在不在"，而退化是量出来的、不是没写</b>：本家<em>有</em>具名
        // 外键目录（<see cref="ConstraintPresent"/> 那一段那颗 X-06），却没有"一条约束活不活"那颗字段 ——
        // 在 <c>information_schema.referential_constraints</c> 里问 <c>enforced</c> 被库正面拒
        // <c>Unknown column 'enforced' in 'field list'</c>【实测·X-07 那一红；那颗字段本家只长在
        // <c>check_constraints</c> 上，管的是 CHECK 一档，与外键无关】。这一族的开关是<em>会话级</em>的
        // （<c>SET FOREIGN_KEY_CHECKS</c>，一开全放）而不是<em>按约束</em>的，于是"加了但没校验"与
        // "在而不挡"两档在本家都造不出来：既没有 <c>WITH NOCHECK</c>（T-SQL 那一颗）、也没有
        // <c>NOT VALID</c>（第四家那颗 <c>convalidated</c> 的反向读数）、也没有 <c>enable</c>（第五家那三向对照）。
        // <b>代价要说死</b>：这一句在本家与 <see cref="ConstraintPresent"/> 同问一句，因此
        // <see cref="EnsureProjectForeignKey"/> 在本家不再能分辨"上一版留了一条禁用的"—— 而那一档
        // 在本家没有对象，分辨不出来的不是缺陷。<b>"库里到底挡不挡"这一问在本家有证人，只是不在这一句</b>：
        // 探针当场量过一对正反样本 —— 父表放一行、子表按它插得进【实测·W-14 affected=1】，换一颗不存在的
        // 父键插就被正面拒 <c>Cannot add or update a child row: a foreign key constraint fails</c> 且那一行
        // 没有落库【实测·W-15 ∶ W-16 数到 1 行】，而会话级那颗开关现场原值是 <c>1</c>【实测·W-17】。
        // <c>OrderingPathTests</c> 那颗负样本（绕开会话插一条没有父的行必须抛）在真夹具上是同一格的第二次作证，
        // 所以本家<em>不</em>挂 <see cref="RequireForeignKeyJudge"/>，那一格与第四家同一侧。
        if (Family == Target.MySql)
            return ConstraintPresent(connection, name);

        return Family == Target.SqlServer
            ? Is(connection, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM sys.foreign_keys " +
                             "WHERE name = @nm AND is_disabled = 0", ("@nm", name))
            : Is(connection, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM all_constraints " +
                             "WHERE cons_name = @nm AND enable = 1", ("@nm", Fold(name)));
    }

    /// <summary>两颗"按名字问约束"的判据共用的那一颗闸门：<b>第七家没有具名约束这一颗目录对象，
    /// 所以这两句在本家不许被问</b>。抛的理由与 <see cref="PrimaryKeyNamed"/> 那段同一颗，
    /// 而形状选在这里而不是各抛一遍，是因为这两句的调用点只有 <see cref="EnsureProjectForeignKey"/> 一处 ——
    /// 一处闸门比两处容易核对"今天到底还有谁在问"。</summary>
    static void RequireNamedConstraintSupport(string method, string name)
        => Assert.True(Family != Target.Sqlite,
            $"sqlite 这一家没有具名约束目录（ALTER ADD CONSTRAINT 是语法错，4509/4510；" +
            $"pragma_foreign_key_list 交回的五颗字段里没有名字那颗，4505），{method}(\"{name}\") 在本家没有对象。" +
            "要问外键挡不挡语句：本家默认就是挡（4513/4514/4518），见 ForeignKeyActive 的注释。");

    /// <summary>目录里那一形：值折成大写，null 与空串都交 <c>null</c>（<c>NVL</c> 的右支 = 当前会话用户）。
    /// 夹具这一颗与 <c>XuguSchemaReader.Fold</c> 同一条纪律，<b>而它<em>不是</em>重复实现</b>：
    /// 裁判这一侧必须与 provider 那一侧<em>各自</em>折一次，两边共用一份代码就等于用被告的尺子量被告。</summary>
    static string? Fold(string? identifier)
        => identifier is { Length: > 0 } name ? name.ToUpperInvariant() : null;

    /// <summary>并发列那张表（§8.2，v0.18）。<b>v0.24 起它与其它表同一条产路</b>（<see cref="EnsureTable"/> →
    /// <c>CodeFirst.EmitCreateTable</c>）；在此之前它是全份唯一一处手写建表，而那一处手写本身就是一格缺口：
    /// 契约里 <c>InventoryItem.RowVersion</c> 只能声明成<em>可空</em>（<c>byte[]?</c> —— §10.6 的必填规则量的是
    /// 整只实体的属性，非空的 <c>byte[]</c> 会让"新增一行"在客户端就过不去，而新增时那一格还没有值），
    /// 而 CodeFirst 的可空性恰恰照抄 CLR，于是产出 <c>rowversion NULL</c> —— 那一列的值<em>永远归库给</em>，
    /// "可能为空"是与它的语义不符的说法。现在契约长出了那一档（<c>ColumnContract.ValueFromStore</c>，
    /// 由本家 <c>ITypeMap.ApplyStorageDefaults</c> 落输入、DDL 读 <c>NullableInStore</c>），
    /// 自动产物与库的真实形状<em>一致</em>，于是这一处不再需要手写的理由 —— 而"两条 DDL 对上"
    /// 从一句主张换成了 <c>DdlTests</c> 里逐列比目录的那一句。
    ///
    /// <para>仍然在 <see cref="EnsureTable"/> 那一批<em>之后</em>建、仍然没有种子：<see cref="Seed"/> 的判据是
    /// "客户表非空"，把这张表并进那条链会牵动所有按标签计行数的用例。</para>
    ///
    /// <para>本机早先跑过一版的机器上表已经在（按上一份手写 DDL 建的）：只补后加的 <c>CheckedAt</c>，不重建 ——
    /// 重建会把别人写的行一起抹掉。那一份旧形状与今天的自动产出逐列相同（<c>rowversion NOT NULL</c>、
    /// <c>CheckedAt NULL</c>），所以 <c>DdlTests</c> 那一句对拍在两种起点上都是同一句断言。
    /// <b>而"这句 DDL 在真库上跑得通"这一格在旧机器上会被那个存在性分支绕过</b>，
    /// 所以它另有证人：<c>DdlTests</c> 里那张按同一形状现场建的探针表。</para></summary>
    static void EnsureInventoryItem(DbConnection connection, QuickSqlOrm orm)
    {
        var table = orm.Schemas.Contract(typeof(InventoryItem)).Table;

        if (TableExists(connection, table))
        {
            // 表已经在了（本机早先跑过一版）就只补后加的那一列，不重建。
            if (!ColumnExists(connection, table, "CheckedAt"))
                Execute(connection, Family switch
                {
                    // 这一家 ADD COLUMN 要带 <c>COLUMN</c> 那颗关键字（D-04 那一绿就是这一形），
                    // 而 T-SQL 那一支不要 —— 两条都是<em>各家自己的</em>写法，不是"同一句换个引号"，
                    // 所以它不进 <see cref="Translate"/>，留在这里点名。
                    Target.Xugu => """ALTER TABLE "QUICKSQLINVENTORYITEM" ADD COLUMN "CHECKEDAT" DATETIME(3) NULL""",
                    // 第四家这一支两个读数：<c>ADD COLUMN … timestamp(3) NULL</c> 收【实测·T-04】，
                    // 而 <c>COLUMN</c> 那颗关键字在本家<em>可省</em>【实测·T-05 同一形不带它也收】——
                    // 这里仍然写全，是因为本仓库 <c>PostgreSqlCodeFirst.EmitAddColumn</c> 发的就是全形，
                    // 裁判跟着产品的写法走而不是跟着"最少能跑"的写法走。型名取 <c>timestamp(3)</c>
                    // 与 <see cref="TimeStoreType"/> ∶ <c>PostgreSqlTypes</c> 那一格同一颗。
                    Target.PostgreSql => """ALTER TABLE "QuickSqlInventoryItem" ADD COLUMN "CheckedAt" timestamp(3) NULL""",
                    // 第七家这一句三颗读数各钉一格：<c>ADD COLUMN … TEXT NULL</c> 本家收【实测·4706，
                    // 那颗 <c>NULL</c> 关键字不是装饰 —— 非空而没给默认值的 <c>ADD COLUMN</c> 在这一族是
                    // 正面拒的【实测·313 ∶ 2811】】；表名与列名都按建表那份拼法问【实测·4702 ∶ 4704】；
                    // 型名写 <c>TEXT</c> 是因为本家的时刻列就是这么声明的（<c>TimeStoreType</c> 那一段），
                    // 而不是因为 <c>DATETIME</c> 会被拒 —— 那一形也建得成，只是换一颗 affinity 配方【实测·606】。
                    Target.Sqlite => """ALTER TABLE "QuickSqlInventoryItem" ADD COLUMN "CheckedAt" TEXT NULL""",
                    _ => "ALTER TABLE [dbo].[QuickSqlInventoryItem] ADD [CheckedAt] datetime2(3) NULL",
                });

            return;
        }

        // <b>第四家到这里就建不出这张表，而"建不出"必须留档、不能把整副夹具带走</b>：
        // <c>InventoryItem.RowVersion</c> 带的是 <c>[Column(TypeName = "rowversion")]</c>，而
        // <c>PostgreSqlTypes.GetStoreType</c> 第一行是"人写过的型名原样还"（<c>StoreTypeName</c> 短路），
        // 于是那句 DDL 里 <c>"RowVersion" rowversion</c> 直达库—— 库答
        // <c>42704: type "rowversion" does not exist</c>【实测·D-04】。
        // <b>三颗读数把这一格钉成"缺的是那一型，不是那句语法"</b>：这一台库里 <c>pg_type</c> 里
        // <c>typname='rowversion'</c> 数到 <b>0 行</b>【实测·S-10】；同一形把那一列换成 <c>bytea</c>
        // 就建得成【实测·D-05】；而"值由库给"在这一族唯一的入口是 identity 那颗序列
        // （<c>pg_get_serial_sequence</c> 问得出来【实测·K-03】），没有任何东西会替一列<em>逐次</em>换二进制令牌。
        // 所以这一家<em>今天</em>补不出这张夹具表，写开关也不行。
        //
        // <b>为什么是"跳过 + 留档"而不是让整副夹具红</b>：这颗异常如果照直抛出去，<c>Run()</c> 会把
        // 它记成 <see cref="Unavailable"/>，于是本家一百条用例全部 skip —— 那正是 §13.5 那条纪律
        // 一直在防的"一个缺口伪装成一百个没跑过"。做法与 v0.34 第一笔退场那颗闸门同一手：
        // <em>缺口按对象收，不按整副收</em>。这张表今天只有三处消费者（并发那十条、<c>TrackingPathTests</c>
        // 那一颗动并发列的、<c>ReadPathTests</c> 那一句只把它当"一张带文本列的靶表"），
        // 前两组已由 <see cref="RequireConcurrencyColumn"/> 在本家挂 skip，后一处挂
        // <see cref="RequireInventoryItemTable"/>。<b>这一手没有把判据松开</b>：它只是让
        // "这一族没有 rowversion"这一格由一条可数的 skip 报出来，而<em>不是</em>由整本册子的静默消失报出来。
        if (Family == Target.PostgreSql)
        {
            InventoryItemGap = "postgresql 这一家没有 rowversion 那一型：pg_type 里数到 0 行（S-10），" +
                "而 provider 对人写的型名原样发出（StoreTypeName 短路），建表因此红在 42704（D-04）；" +
                "同形换 bytea 建得成（D-05），所以缺的是那一型而不是那句语法。" +
                "库给值的入口在本族只有 identity（K-03），没有逐次换二进制令牌的那一档 —— 缺口记 §14，" +
                "要不要在这一家拒收 [Column(TypeName = \"rowversion\")]（XuguCodeFirst v0.34 那一手）是待裁决。";

            Console.WriteLine("EnsureInventoryItem：postgresql 一支建不出夹具表 —— " + InventoryItemGap);

            return;
        }

        // <b>第五家（xugu）走到同一格，方向<em>相反</em>：那一型它有（目录里认这个词，A-04 交
        // <c>TYPE_NAME="ROWVERSION"</c>），是<em>本仓库自己</em>在 v0.34 第二笔为它立了拒收闸门 ——
        // 因为库里建得成、会自增，而驱动一读那一列就抛 <c>System.DataOutOfBindException</c>
        // （<c>XuguCodeFirst.cs:202</c> 那句抛点引的 RV-08 ∶ A-06 两批同一颗红）。
        // 后果与第四家同一形而来源不同：第四家是库在解析前拒（42704），本家是 provider 在发射时拒
        // （<c>QuickSqlException</c>，一句 DDL 都没发出去）。
        //
        // <b>这一支补的是 v37-xugu-int-2.log 量到的一格真账</b>：那一跑是 <c>Total 100 / Failed 3 / Skipped 92</c>，
        // 而 92 那颗整片静默的红点来源就在这一句 —— 全新库（本机今天的状态）没有那张表，
        // 存在性分支绕不过去，异常照直进 <c>Unavailable</c>，于是"一家缺那一档"伪装成"一百条没跑过"。
        // <b>跑过旧版夹具的机器上今天仍然不走这一支</b>（表在就只补 <c>CHECKEDAT</c> 那一列，
        // 上面那段存在性分支），所以这一手不减少任何现有证人，只把全新库那一台的整册静默换成
        // 按对象点名的 skip。缺口本身（本家建不出夹具表）仍记 §14 那一格，与 DdlTests 探针表同一颗。
        if (Family == Target.Xugu)
        {
            InventoryItemGap = "xugu 这一家由 provider 自己拒收 rowversion 那一型：库里有那一型也会自增（A-04），" +
                "但驱动读不回那一列（带它的 SELECT 抛 DataOutOfBindException，RV-08 ∶ A-06），" +
                "所以 XuguCodeFirst 在 v0.34 第二笔把那句 DDL 拦在发射前（QuickSqlException）。" +
                "全新库因此建不出这张夹具表（跑过旧版夹具的机器上表已经在，走的是存在性分支）—— " +
                "缺口记 §14，与 DdlTests 那张探针表同一格；并发那十一条另由 RequireConcurrencyColumn 点名。";

            Console.WriteLine("EnsureInventoryItem：xugu 一支建不出夹具表 —— " + InventoryItemGap);

            return;
        }

        // <b>MySQL 这一家与第四家同一侧、同一颗形状，只有报错的那一句不同</b>：这一台库里没有
        // <c>rowversion</c> 那一型（<c>information_schema</c> 按型名数到 <b>0</b>【实测·S-08】），而
        // <c>MySqlTypes.GetStoreType</c> 的第一行同样是"人写过的型名原样还"（<c>StoreTypeName</c> 短路，
        // <c>src/QuickSql.Providers.MySqlBase/MySqlTypes.cs:92</c>），于是 <c>EmitCreateTable</c> 把
        // <c>`RowVersion` rowversion</c> 原样发出去，库答 <c>You have an error in your SQL syntax …
        // near 'rowversion)'</c>【实测·D-03】。
        // <b>与第四家同一手的两颗对照也量了</b>：只有那一列的形状不同 —— 带 <c>V rowversion</c> 的建表语句
        // 正面拒【实测·D-03】，带 <c>T TIMESTAMP(3) NULL</c> 的建表语句收【实测·D-04】，
        // 所以缺的是那一型而不是那句语法；而"值由库逐次换"在本族<em>没有</em>入口 —— 库给值的只有
        // <c>AUTO_INCREMENT</c> 那一档整数键（<c>LAST_INSERT_ID()</c> 问得出来【实测·K-02】），
        // 没有任何东西替一列生成逐次递增的二进制令牌。
        // <b>这一家与第四家不同的一格要一起写</b>：<c>MySqlConcurrency.Mode</c> 是 <c>TimestampColumn</c>，
        // 也就是这一族的并发<em>设计</em>本来就走"人给值的那一列"，而夹具那张表声明的是
        // <c>DbTypes = "rowversion"</c> 那一档 —— 于是这里缺的不是"并发这一档"，是<b>夹具的声明与这一族的
        // 设计档位不是同一颗</b>。要不要在本家把 <c>rowversion</c> 折成 <c>TIMESTAMP(3) ON UPDATE</c>
        // （语义近似而字节令牌不成立），还是像 <c>XuguCodeFirst</c> v0.34 那样拒收这颗型名，是产品侧的决定，
        // 不是夹具的一句替代 SQL。缺口记 §14，与第四家那一格并排。
        if (Family == Target.MySql)
        {
            InventoryItemGap = "mysql 这一家没有 rowversion 那一型（S-08 数到 0），而 provider 对人写的型名原样发出" +
                "（StoreTypeName 短路），建表因此红在 1064（D-03）；同形换 TIMESTAMP(3) 建得成（D-04），" +
                "所以缺的是那一型而不是那句语法。库给值的入口在本族只有 AUTO_INCREMENT 那一档整数键（K-02），" +
                "没有逐次换二进制令牌的那一档 —— 缺口记 §14，与 postgresql 那一格并排；" +
                "而本家的并发档位（MySqlConcurrency = TimestampColumn）设计上是\"人给值的那一列\"，" +
                "与夹具声明的 rowversion 不是同一颗，要不要折型或拒收是待裁决。";

            Console.WriteLine("EnsureInventoryItem：mysql 一支建不出夹具表 —— " + InventoryItemGap);

            return;
        }

        EnsureTable(connection, orm, typeof(InventoryItem));
    }

    /// <summary><see cref="EnsureInventoryItem"/> 在本家交不回那张夹具表时留下的原文。
    /// <b>它是夹具<em>自己</em>的一条账，不是从 <c>Unavailable</c> 那份抄来的</b>：整副夹具是好的，
    /// 缺的只有那一张表，所以 skip 的消息也只该带这一颗的原因。</summary>
    public static string? InventoryItemGap { get; private set; }

    /// <summary>第四颗闸门：<b>这一家今天有没有夹具那张并发列表</b>。挂在唯一一条<em>不涉并发语义</em>、
    /// 只是把 <c>InventoryItem</c> 当一张普通靶表用的用例上（<c>ReadPathTests</c> 那一句按前缀数行）。
    /// 与 <see cref="RequireConcurrencyColumn"/> 的分工写在各自的注释里：那一颗判的是"这一族有没有
    /// 值归库给那一档"，本颗判的只是"那张表在不在"，两者在本家同源而<em>不必</em>同源 ——
    /// 如果哪天本家有了能建的并发列形状，本颗闸门会自己失去对象，那一颗不会。</summary>
    public static void RequireInventoryItemTable()
    {
        var gap = InventoryItemGap;

        Assert.SkipWhen(gap is not null, gap ?? string.Empty);
    }

    /// <summary>排序见证用的那条外键约束 —— <b>手工加在裁判连接上，不是 CodeFirst 发的</b>。
    /// <b>它是全份唯一一处手写 DDL</b>（并发列那张表曾经是第二处，v0.24 起也走 <see cref="EnsureTable"/> 了），
    /// 而这一处的原因记在 §14 未完项表"CodeFirst 不发 FOREIGN KEY 约束"那一行：今天 <c>EmitCreateTable</c> 不产出 <c>FOREIGN KEY</c> 文本，
    /// 种子里还故意留着一行没有父行的孤儿子（§13.6 的 LEFT JOIN 见证要它），所以"库里真的会挡"这件事
    /// 只能在这里补出来给 <c>OrderingPathTests</c> 当裁判。
    ///
    /// <para>判据用 <c>is_disabled = 0</c> 而不是"名字在不在"：一条禁用的约束不挡任何语句，
    /// 只看名字会得到一个假绿的裁判。<c>WITH NOCHECK</c> 是给上一次中断留下的孤儿子行留的余地 ——
    /// 加一条会失败的约束等于把整个 fixture 挂掉，而<em>启用</em>才是这里要的性质，信任与否不是。</para>
    ///
    /// <para><b>v0.32 第二格起这一句按家族分岔</b>，而分岔的两半在<em>本版前后不是同一件事</em>，
    /// 所以这里按时间写两笔：
    /// <br>· 上一版：判据那一半在虚谷<em>只落实了一半</em>（目录里有 <c>ENABLE</c>/<c>VALID</c> 两颗布尔，
    /// 但一颗布尔列在 <c>WHERE</c> 里怎么比<em>没量过</em>），于是 <see cref="ForeignKeyActive"/> 退化成
    /// <see cref="ConstraintPresent"/>，而 <c>WITH NOCHECK</c> 那一半在语法里根本没有。
    /// <br>· 本版：<b>前一格已经补齐</b>（<c>enable = 1</c> 三向对照，F-43 / F-43b / F-43c，
    /// <c>obj/logs/v32-recon-funcs-10.log</c>），所以"在而不活"这一档现在<em>问得出来</em> ——
    /// 下面那条 <c>DROP CONSTRAINT</c> 因此从"走不到的分支"变成了真会发的语句。
    /// 只有后一格还缺：<c>WITH NOCHECK</c> 是 T-SQL 的关键字，这一家没有，所以"上一次中断留下的孤儿子行"
    /// 在第五家<em>没有</em>余地。而它今天<em>撞不上</em>：种子里那行孤儿子挂的是 <c>CustomerId</c>，
    /// 这条外键管的是 <c>ProjectId</c>，两句说的不是同一列，校验存量行因此看不到那行孤儿
    /// （F-45 正是拿一颗不存在的 <c>PROJECTID</c> 插一行，库按 <c>[E13005]</c> 挡下 ——
    /// 这是"这一家 <c>ADD CONSTRAINT</c> 真的在校验、也真的在挡"的第一颗见证）。
    /// 如果哪天有版本把孤儿子改挂到 <c>ProjectId</c> 上，这一句会红，红了要的是把父表补齐，
    /// <b>别把这一句删掉换 <c>NOVALIDATE</c> 之类的猜测文本</b>。</para></summary>
    static void EnsureProjectForeignKey(DbConnection connection)
    {
        if (Family == Target.Sqlite)
        {
            // 本家这一颗<em>整段走不到</em>，而且是两颗缺口叠出来的，不是"还没写"：
            // ① 往一张已有的表上加约束在这一族没有语句 —— 具名那一形【实测·4509：near "CONSTRAINT": syntax error】、
            //   不具名那一形【实测·4510：near "FOREIGN": syntax error】，两条都是库自己说的原话；
            //   唯一能挂外键的位置是 <c>CREATE TABLE</c> 的表级子句【实测·4508】，而 <c>CodeFirst</c> 不发那一段
            //   （§14 那一行"CodeFirst 不发 FOREIGN KEY 约束"在七家里今天只有本家<em>连补都补不回去</em>）。
            // ② 补一条 <c>ADD COLUMN … REFERENCES</c> 倒是发得出去【实测·4511】，可那是在<em>加一颗列</em>，
            //   不是给已有的 <c>ProjectId</c> 加约束 —— 拿它当这一句的替身会把"外键管的是哪一列"读歪。
            // 于是这一家今天没有"库里真的会挡"的裁判对象（<c>OrderingPathTests</c> 那一句按家族跳，
            // 理由与这一段同一颗）。要说的是<b>性质而不是形状</b>：外键约束这一档在本家是<em>建表时一次定型</em>的，
            // 而这条夹具要的是"表已经在了，事后把约束补上再验证它活着"，那一档在这一族压根没有。
            Console.WriteLine("EnsureProjectForeignKey：sqlite 一支整段跳过 —— 本家没有 ALTER ADD CONSTRAINT（4509/4510），" +
                "而 CodeFirst 不发 FOREIGN KEY（§14），所以这里没有可建的裁判。外键挡不挡语句另有读数：默认就挡（4513/4514/4518）。");

            return;
        }

        if (ForeignKeyActive(connection, ProjectForeignKey))
            return;

        // 先删后加：上一版留着的可能是禁用的、也可能指向已经重建过的父表，两种都不是我们要的裁判。
        // <b>而"删"要先问一句在不在</b>：这一家没有量过 <c>DROP CONSTRAINT IF EXISTS</c> 那一形
        // （探到的只有 <c>DROP TABLE</c>，K 段那一串里没出现过 CONSTRAINT），
        // 而拿一条"不在就红"的语句去探一个已经知道不在的名字，等于把夹具挂在第一次跑上。
        // T-SQL 那一支仍然用 <c>IF EXISTS</c> —— 它本来就支持，没必要为了对齐改掉。
        //
        // <b>v0.32 第二格起这一支在虚谷<em>到得了</em>了</b>：<see cref="ForeignKeyActive"/> 现在问的是
        // <c>enable = 1</c> 而不是"名字在不在"（三向对照 F-43/F-43b/F-43c），所以"在而不活"那一档
        // 从此有了一种可判的形态，删这一句正是为它准备的。上一版这里写的是一句相反的话
        // （"这一支走不到，因为判据退化成了 <c>ConstraintPresent</c>"），那句话当时是真的，
        // 而它随那颗退化一起作废 —— <b>留着它比删掉这条语句更危险：读的人会以为删支没有证人</b>。
        if (ConstraintPresent(connection, ProjectForeignKey))
            Execute(connection, Family switch
            {
                // 结尾那颗引号用 + '"' 补上一句：以引号收尾的原始字符串要 4 颗定界符，
                // 而行尾那串引号是按"内容 + 定界符"读的 —— 这种写法一眼看不出对错，不如换成看得懂的。
                Target.Xugu => """ALTER TABLE "QUICKSQLMILESTONE" DROP CONSTRAINT "FK_QUICKSQLMILESTONE_QUICKSQLPROJECT""" + '"',
                // 本家这一句有凭据：同一张靶表上刚 ADD 上的那条外键，按名字 DROP 得掉【实测·C-12】，
                // 而 DROP 之后 <c>pg_constraint</c> 里那一行就没了（C-10 与 C-14 是这一支的前后两半）。
                // <b>表名与约束名都不折大写</b>：夹具那张表是 <c>CodeFirst</c> 带引号建的，
                // 目录里存的就是 <c>QuickSqlMilestone</c> 那一份拼法【实测·C-01 ∶ C-05 同一颗对照，
                // 大写形交 0 行的是 C-02】。
                Target.PostgreSql => """ALTER TABLE "QuickSqlMilestone" DROP CONSTRAINT "FK_QuickSqlMilestone_QuickSqlProject""" + '"',
                // <b>MySQL 这一支三颗对照都量过，而它把"先删后加"这一手在本家钉成了实测而不是猜测</b>：
                // 按原名 DROP 收【实测·W-05】、按<em>小写</em>名 DROP 也收【实测·W-09】、按全大写名同样收
                // 【实测·W-10】—— 与 <see cref="ConstraintPresent"/> 那一颗问句的排序规则同一颗来源
                // （<c>table_constraints.constraint_name</c> 落在 <c>utf8mb3_tolower_ci</c>【实测·W-01】），
                // 也就是说这一族的<em>目录问句</em>与<em>DDL 里的名字</em>两头都不区分大小写。
                // <b>而"名字不在就红"这一档也量了</b>：拿一颗不存在的名字去 DROP 交
                // <c>Can't DROP '…'; check that column/key exists</c>【实测·W-11】，所以这一句<em>必须</em>
                // 先在 <c>ConstraintPresent</c> 那一颗闸门里过一道，与第四家那一段同一个理由。
                // <b>删完之后再加得回去</b>【实测·W-08】，这一支因此与 T-SQL 那一支同样是"反复跑同一次运行
                // 到得了同一状态"的那一形；加分的一格是：DROP 外键<em>不</em>带走库为它自动建的那颗索引
                // 【实测·W-07 两形名单一模一样，都含 <c>FK_…</c> 那一颗】，而再 ADD 时库认出那颗同名同列的
                // 索引直接复用，不会撞 <c>Duplicate key name</c> —— 那是本层"先删后加"在本家不红的根因。
                Target.MySql => "ALTER TABLE `QuickSqlMilestone` DROP FOREIGN KEY `" + ProjectForeignKey + '`',
                _ => $"ALTER TABLE [dbo].[QuickSqlMilestone] DROP CONSTRAINT [{ProjectForeignKey}]",
            });

        Execute(connection, Family switch
        {
            // 没有 <c>WITH NOCHECK</c>：那一颗是 T-SQL 的关键字，这一家的语法里没有它（D-09 那一段
            // 问的就是"加了外键会不会因为种子里那行孤儿子而失败"，交回来的红与绿都按<em>不加</em>那一形量的）。
            // 于是这一家真库上"库里挡不挡"这句话要等种子干净的那一次才算数 —— 记进 §14 与本版账。
            Target.Xugu => """ALTER TABLE "QUICKSQLMILESTONE" ADD CONSTRAINT "FK_QUICKSQLMILESTONE_QUICKSQLPROJECT" FOREIGN KEY ("PROJECTID") REFERENCES "QUICKSQLPROJECT" ("ID")""",
            // <b>第四家这一句是三颗读数叠出来的，而它把本层那格"唯一的欠账"直接补平了</b>：
            // ① 往一张<em>已经在</em>的表上事后加约束在本家有语句【实测·C-09：affected=1 那一支，
            //   形状与第七家那两颗语法错（4509/4510）正相反】；② 加完目录里认名字
            //   【实测·C-10：<c>conname=FK_… / contype='f' / convalidated=True</c>】；
            // ③ <b>库里真的挡</b> —— 拿一颗不存在的父键插一行，库按 <c>23503 violates foreign key constraint</c>
            //   拒掉【实测·C-11】，那颗正是 <c>OrderingPathTests</c> 那一句负样本要的裁判，
            //   所以 <see cref="RequireForeignKeyJudge"/> 在本家<em>不</em>挂 skip。
            // 而 <c>WITH NOCHECK</c> 那一半在本家有一个<em>不是同义字</em>的对应物：<c>NOT VALID</c>
            // 【实测·C-13 建得成，C-14 那颗 <c>convalidated</c> 因此交 <c>False</c>】——
            // 它只差一处：<c>NOT VALID</c> 的外键仍然校验<em>新写入</em>，只放过历史行，
            // 而 <c>WITH NOCHECK</c> 在 T-SQL 两头都不挡。本句因此<em>不</em>写 <c>NOT VALID</c>：
            // 种子里那行孤儿子挂的是 <c>CustomerId</c>，这条外键管的是 <c>ProjectId</c>，
            // 校验存量看不到它（与第五家那一段同一个理由），留着默认那一形才是本层要的性质。
            // 那一档的差记 §14，别在这里替它写一个"看着像"的关键字。
            Target.PostgreSql => """ALTER TABLE "QuickSqlMilestone" ADD CONSTRAINT "FK_QuickSqlMilestone_QuickSqlProject" FOREIGN KEY ("ProjectId") REFERENCES "QuickSqlProject" ("Id")""",
            // <b>MySQL 这一支是本层那一格"CodeFirst 不发 FOREIGN KEY 约束"在本家的补法</b>，三颗读数：
            // ① 往一张<em>已经在</em>的表上事后加约束有语句，形状就是这一条【实测·X-05 ∶ W-00c 两次各收一次，
            //   第二跑复现】；② 加完目录里认这个名字，且<em>原样还</em>（不像主键那颗会被改成 <c>PRIMARY</c>
            //   【实测·X-03】）【实测·X-06：<c>FK_QuickSqlProbeT_QuickSqlProbeP</c> ∶ W-12 同一颗对照】；
            // ③ <b>库里真的挡</b> —— 父表有一行、子表按那一行插得进【实测·W-14 affected=1】，
            //   换一颗不存在的父键插就交 <c>Cannot add or update a child row: a foreign key constraint fails</c>
            //   且那一行<em>没有</em>落库【实测·W-15 ∶ W-16 数到 1 行】，所以
            //   <see cref="RequireForeignKeyJudge"/> 在本家<em>不</em>挂 skip（与第四家同一侧，与第七家正相反）。
            // <c>WITH NOCHECK</c> 那一半在本家没有对应物，而且<em>连近似的关键字都没有</em>：这一族要么校验
            // （默认那一形，本句写的就是它），要么 <c>SET FOREIGN_KEY_CHECKS = 0</c> 那种<em>会话级</em>开关
            // 【实测·W-17 现场原值是 1】—— 后者不是"这一条约束不校验存量"而是"这个连接上所有外键都不校验"，
            // 拿它当 <c>WITH NOCHECK</c> 的替身会把一颗约束的性质写成一颗会话的性质。那一档的差记 §14。
            // 种子里那行孤儿子挂的是 <c>CustomerId</c>、这条管的是 <c>ProjectId</c>，校验存量看不到它，
            // 与第四家那一段同一个理由。
            Target.MySql => "ALTER TABLE `QuickSqlMilestone` ADD CONSTRAINT `" + ProjectForeignKey + "` " +
                            "FOREIGN KEY (`ProjectId`) REFERENCES `QuickSqlProject` (`Id`)",
            _ => "ALTER TABLE [dbo].[QuickSqlMilestone] WITH NOCHECK ADD CONSTRAINT [FK_QuickSqlMilestone_QuickSqlProject] " +
                 "FOREIGN KEY ([ProjectId]) REFERENCES [dbo].[QuickSqlProject] ([Id])",
        });
    }

    /// <summary>见证用外键的名字。<b>两家的目录里存的是同一个名字还是两个名字，取决于建它时带没带引号</b>：
    /// T-SQL 侧 <c>[FK_…]</c> 是带引号的原样名，虚谷侧这一句写的是 <c>"FK_…"</c>（也是原样名，且本仓库
    /// 的 CodeFirst 与探针都按大写折），所以这里存一份 T-SQL 拼法、查虚谷时由 <see cref="Fold"/> 折成大写，
    /// 而不是维护两个常量。</summary>
    const string ProjectForeignKey = "FK_QuickSqlMilestone_QuickSqlProject";

    static void Seed(DbConnection connection)
    {
        if (Count(connection, "[dbo].[QuickSqlCustomer]", "[Remark]") > 0)
            return;

        using var transaction = connection.BeginTransaction();

        var customerIds = new List<int>();

        foreach (var customer in Customers)
            customerIds.Add(InsertId(connection, transaction,
                "INSERT INTO [dbo].[QuickSqlCustomer] ([Name], [City], [Remark]) VALUES (@name, @city, @remark)",
                ("@name", customer.Name), ("@city", customer.City), ("@remark", Tag)));

        var orderIds = new List<int>();

        for (var i = 0; i < Orders.Length; i++)
        {
            var order = Orders[i];

            orderIds.Add(InsertId(connection, transaction,
                "INSERT INTO [dbo].[QuickSqlOrder] ([CustomerName], [CustomerId], [Amount], [State], [CreatedAt], [PaidAt], [Note]) " +
                "VALUES (@customerName, @customerId, @amount, @state, @createdAt, @paidAt, @note)",
                ("@customerName", order.CustomerName), ("@customerId", customerIds[i % customerIds.Count]),
                ("@amount", order.Amount), ("@state", (int) order.State),
                ("@createdAt", order.CreatedAt), ("@paidAt", order.PaidAt), ("@note", Tag)));
        }

        for (var i = 0; i < Items.Length; i++)
        {
            var item = Items[i];

            Execute(connection, transaction,
                "INSERT INTO [dbo].[QuickSqlOrderItem] ([OrderId], [Sku], [Quantity], [Price]) VALUES (@orderId, @sku, @quantity, @price)",
                ("@orderId", orderIds[i / 2]), ("@sku", Tag + "/" + item.Sku),
                ("@quantity", item.Quantity), ("@price", item.Price));
        }

        transaction.Commit();
    }

    /// <summary>补一条"指向不存在的客户"的订单（§13.6 的左联补行用例要它）。
    /// <b>为什么不并进 <see cref="Seed"/>：</b>那个方法是"客户表非空就整体跳过"的，
    /// 而这里的库早就是非空的 —— 并进去等于这条永远不灌。所以它自带一次按标签的存在性检查，
    /// 于是"已经灌过种子的库"和"空库"两条路都到得了同一个状态。
    /// 同样只插不删，标签 <see cref="OrphanTag"/> 与 <see cref="Tag"/> 不相等，故不影响任何按标签计行数的用例。</summary>
    static void EnsureOrphanOrder(DbConnection connection)
    {
        if (Count(connection, "[dbo].[QuickSqlOrder]", "[Note]", OrphanTag) > 0)
            return;

        Execute(connection,
            "INSERT INTO [dbo].[QuickSqlOrder] ([CustomerName], [CustomerId], [Amount], [State], [CreatedAt], [PaidAt], [Note]) " +
            "VALUES (NULL, @customerId, @amount, @state, @createdAt, NULL, @note)",
            ("@customerId", OrphanCustomerId), ("@amount", 42.4242m), ("@state", (int) OrderState.Draft),
            ("@createdAt", At(2026, 4, 1, 9, 0, 456)), ("@note", OrphanTag));
    }

    /// <summary>插入并取回自增 id。<b>两条语句必须落在同一个批次里</b>：<c>SCOPE_IDENTITY()</c> 的作用域
    /// 是当前 scope，分开两次 <c>ExecuteNonQuery</c> 就是两个 scope，第二次拿回来的是 <c>DBNull</c>。
    /// 这不是驱动的怪癖而是 T-SQL 语义，而 §9.4 的主键回填（<c>OUTPUT INSERTED</c>）存在的理由就在这里。
    /// <b>v0.12 之后这里仍然是裸 ADO，是裁决不是欠账</b>：同一件事在 ORM 侧已经有了实现与用例
    /// （<c>ExecuteReturnKeysAsync</c> / <c>WritePathTests</c>），而这段的活是"不被那条实现污染的预期值"，
    /// 换成它等于把裁判交给被告。两条路各留一份、互为对照，就是那批键回填用例的断言方式。</summary>
    static int InsertId(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object? Value)[] args)
    {
        // "刚插的那一颗"在两家是两颗不同的会话函数，而这是<em>问法</em>差、不是字形差，所以它不进
        // <see cref="Translate"/>，留在这里点名。虚谷这一支的凭据是 obj/logs/v32-recon-txn-3.log：
        // T-03 量到 <c>INSERT … ; SELECT LAST_INSERT_ID()</c> 这种多语句批在这一家收得下、交回 Int64；
        // T-04 量到它与分开两步再问 <c>MAX(ID)</c> 得到同一个数（20 == 20），也就是它量的确实是本会话
        // 刚插的那一颗而不是全表。T-01/T-02 那两半证明这一句在事务里同样成立。
        var scalar = Scalar(connection, transaction,
            sql + Family switch
            {
                // T-03 量到 <c>INSERT … ; SELECT LAST_INSERT_ID()</c> 这种多语句批在这一家收得下、交回 Int64。
                Target.Xugu => " ; SELECT LAST_INSERT_ID()",
                // 第七家同一颗形状有两只读数：场外【实测·4524：<c>3:Int64</c>】与<b>事务里</b>
                // 【实测·4725：种子那一批就跑在事务里，交回 <c>1:Int64</c>】—— 后者是本方法唯一的用法，
                // 所以这里钉的是 4725 而不是 4524。同一颗连接上<em>参数被两句共用</em>也量过【实测·4525】。
                Target.Sqlite => " ; SELECT last_insert_rowid()",
                // <b>第四家整个换形：不拼第二句，而是把键挂在同一句的尾巴上</b> —— <c>… RETURNING "Id"</c>。
                // 两颗读数支持这一手：K-01 场外一句交回 <c>3 (Int32)</c>，K-02 <em>事务里</em>同一形交回 <c>4</c>
                // 且随回滚一起退掉（本方法唯一的用法就是事务里那一只）；而拼两句那一形在本家虽然发得出去
                // （M-02 ∶ M-04 两绿：<c>create ; create</c> 与 <c>insert ; select</c> 都被收下），
                // <c>ExecuteScalar</c> 在那一形下取的是<em>哪一个结果</em>却没有可靠的读数 ——
                // 一颗自增键的取回不能建在"大概是最后那句"上。本家的 <c>Returning</c> 是能力位上声明过的
                // （<c>PostgreSqlProviderBase.FamilyCapabilities</c> 第二颗），也就是 §9.4 那条主键回填正路，
                // 裁判这一侧走同一形而句子是自己写的。回包型是 <c>Int32</c>（与 <see cref="IntClrType"/> 那格
                // 本家交 <c>int</c> 同一颗，见 V-03），所以下面那颗装箱匹配直接命中 <c>int id</c> 一支。
                Target.PostgreSql => " RETURNING \"Id\"",
                // <b>MySQL 回到"拼第二句"那一形（与虚谷、第七家同一支），而这是本家<em>唯一</em>一形 —— 不是选型</b>：
                // 挂在句尾的 <c>… RETURNING `Id`</c> 被库正面拒 <c>You have an error in your SQL syntax …
                // near 'RETURNING Id'</c>【实测·K-03】，也就是说第四家那一支在本家根本发不出去。
                // 拼两句那一形则有四颗读数：<em>场外</em>交 <c>1 (UInt64)</c>【实测·X-10】、<b>事务里</b>
                // 交 <c>2 (UInt64)</c>【实测·X-11，本方法唯一的用法】、<em>分两句</em>再问同一颗也交回同一个数
                // 【实测·X-12 与 X-11 都是 2，所以"必须同批"那一格在本家不是硬约束，写了也只是少一次往返】，
                // 而 <c>LAST_INSERT_ID()</c> 本身量的确实是本会话刚插的那一颗【实测·K-02 ∶ K-01 —— 后一句是
                // "插一行而不接任何问句"，交回的是<em>空结果集</em>，也就是这一族的 <c>INSERT</c> 自己不带键回来，
                // 键只能另问那一句】。本家这几颗 K 标号与第四家 <c>v37-pg-recon-1.log</c> 的同名标号<em>不是</em>
                // 同一批读数（那一家 K-01 命中的正是 <c>RETURNING</c>），出处一律是 <c>v37-my-recon-1.log</c>。
                // 回包型是 <c>UInt64</c>，所以下面那颗装箱匹配走的是 <c>var other</c> 那一支的
                // <c>Convert.ToInt32</c>，与 <see cref="IntClrType"/> 那格本家交 <c>Int32</c> <em>不</em>冲突
                // （那一格问的是<em>列</em>的回包，这一格问的是<em>会话函数</em>的回包，两颗不同的东西）。
                Target.MySql => " ; SELECT LAST_INSERT_ID()",
                _ => " ; SELECT CAST(SCOPE_IDENTITY() AS int)",
            },
            args);

        return scalar switch
        {
            int id => id,
            null or DBNull => throw new InvalidOperationException("插入后没取回自增 id，拿回来的是 null"),
            var other => Convert.ToInt32(other, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>本批次已经灌了几条。<b>只数带标签的</b>，所以库里别人留下的行不会把种子判成"已有数据"。
    /// 转 <c>Int32</c> 而不是直接拆箱，理由与 <see cref="Is"/> 同一颗（这一家的 <c>COUNT(*)</c> 交 <c>Int64</c>）。</summary>
    static int Count(DbConnection connection, string table, string tagColumn, string tag = Tag)
        => Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM {table} WHERE {tagColumn} = @tag", ("@tag", tag))!, CultureInfo.InvariantCulture);

    static void Execute(DbConnection connection, string sql, params (string Name, object? Value)[] args)
        => Execute(connection, null, sql, args);

    static void Execute(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] args)
    {
        using var command = connection.CreateCommand();

        Bind(command, transaction, sql, args);

        command.ExecuteNonQuery();
    }

    static object? Scalar(DbConnection connection, string sql, params (string Name, object? Value)[] args)
        => Scalar(connection, null, sql, args);

    static object? Scalar(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] args)
    {
        using var command = connection.CreateCommand();

        Bind(command, transaction, sql, args);

        return command.ExecuteScalar();
    }

    /// <summary>字符串一律 <see cref="DbType.String"/>（即 nvarchar）：留空让驱动自己推断，
    /// 在 cp1252 排序规则下会静默丢字，而这里是"库到底存了什么"的裁判，不能靠推断（§10.4）。
    /// 时间给 <see cref="DbType.DateTime2"/>：默认的 <c>DbType.DateTime</c> 会先把值取整到 1/300 秒再上线。
    ///
    /// <para><b>虚谷这一支多做两件字形上的事</b>：整句过 <see cref="Translate"/>，以及参数名<em>剥掉前缀</em>。
    /// 后者不是这一家的怪癖而是<em>本仓库自己</em>的边界纪律：生产写路径在同一条边界上剥
    /// （<c>AdoExecutor.Bind</c>，v0.32 第二格修的那一颗就是它），因为 SQL 文本里的占位符带方言前缀、
    /// <c>DbParameter.ParameterName</c> 不带，是厂商驱动的两套约定。裁判这一侧<em>故意不走</em> <c>AdoExecutor</c>
    /// （类注释那句"裁判必须是另一条路"），所以它自己剥一次 —— 两边各剥一次不是重复实现，
    /// 而是"用被告的尺子量被告"要避免的那种共用。</para>
    ///
    /// <para><b>第七家过翻译器而不剥前缀</b>：本家那一颗 <c>@</c> 是<em>驱动自己认识的形状</em> ——
    /// 这一轮每一颗带绑定参数的问句都是按 <c>ParameterName = "@tbl"</c>（带前缀）发出去的，
    /// 全部命中【实测·4503 ∶ 4504 ∶ 4505 ∶ 4506 ∶ 4702 ∶ 4704 ∶ 4731 ∶ 4737 一排 <c>BND</c> 行】，
    /// 而 <c>SqliteDialect.ParameterPrefix</c> 交回的就是同一颗 <c>'@'</c>。
    /// <b>所以本家与前两家各差一半</b>：虚谷要换前缀字形（<c>@</c> → <c>:</c>）而参数名不带前缀，
    /// 本家两句都用 <c>@</c>，<c>N''</c> 与方括号那两形才是要换的（下一颗方法的清单）。
    /// 类型那一格本家与前两家同判据：<b>点名才写、不点名就不碰</b>——那颗 <c>bool</c> 在本家<em>没有</em>
    /// <c>DbType</c> 分支，而它恰恰是量过的：<c>true</c>/<c>false</c> 原样交给驱动落成 <c>1</c>/<c>0</c> 的
    /// <c>integer</c>【实测·4713/4715 ∶ 4716 两行】，读回来是 <c>Int64</c> 而不是 <c>Boolean</c>，
    /// 于是 <see cref="Rows"/> 在本家交回 <c>"1"</c>/<c>"0"</c> 而 T-SQL 那一家交回 <c>"True"</c>/<c>"False"</c>
    /// —— 那是<em>回包形状</em>差而不是库里存的东西差，用例里比这一格的断言要按家族给。</para></summary>
    static void Bind(DbCommand command, DbTransaction? transaction, string sql, (string Name, object? Value)[] args)
    {
        // 只有虚谷那一家要剥前缀：本家与 T-SQL 两支的名字字形就是 SQL 文本里那一颗。
        // <b>第四家也归"不剥"这一支，而它的理由比"两家都认"更宽</b>：那一家四形全过 —— 文本 <c>@p</c> ∶ <c>:p</c>
        // 与名字带前缀 ∶ 不带前缀两两配对，四颗读数都命中同一颗值【实测·B-01..B-04】，所以这里既不必剥、
        // 也不必换前缀字形，缝里在本家一个动作都不加（缺口相反：本家连"必须剥"这件事都没有）。
        var stripPrefix = Family == Target.Xugu;

        command.CommandText = Family == Target.SqlServer ? sql : Translate(sql);
        command.Transaction = transaction;

        foreach (var (name, value) in args)
        {
            var parameter = command.CreateParameter();

            parameter.ParameterName = stripPrefix ? name.TrimStart('@', ':') : name;

            // <b>先定类型、再给值，而且"没点名的型"一律不去读驱动那颗默认值</b>。这两句顺序在虚谷上不是洁癖：
            // 它的 <c>DbType</c> getter 是<em>从 Value 反推</em>的，而 <c>decimal</c> 不在它那张表里
            // （反推出来的是 Binary），于是原先"值先给、类型再照旧写回去"那一串，对一颗 decimal 参数
            // 等于让 setter 把 <c>decimal</c> 往 <c>byte[]</c> 上转 —— 第一跑整批种子死在这句上：
            // <c>Unable to cast object of type 'System.Decimal' to type 'System.Byte[]'</c>
            // （obj/logs/v32-int-xugu-2.log）。而同一颗驱动<em>不报类型就不给绑</em>是另一头证据：
            // obj/logs/v32-recon-bind-1.log 的 B-09 只报了字符串那颗，<c>:v2</c> 那颗 <c>7.8m</c>
            // 到了库里是 NULL（红在 NUMERIC 的 NOT NULL 上）。两家都吃这一句，所以这里是"点名才写、
            // 不点名就不碰"，而不是给一个兜底值。
            switch (value)
            {
                // 字符串一律 String（即 nvarchar）：留空让驱动自己推断，在 cp1252 排序规则下会静默丢字，
                // 而这里是"库到底存了什么"的裁判，不能靠推断（§10.4）。
                case string: parameter.DbType = DbType.String; break;
                // 时间给 DateTime2：默认的 DbType.DateTime 会先把值取整到 1/300 秒再上线。
                case DateTime: parameter.DbType = DbType.DateTime2; break;
                case decimal: parameter.DbType = DbType.Decimal; break;
                case int: parameter.DbType = DbType.Int32; break;
            }

            parameter.Value = value ?? DBNull.Value;

            command.Parameters.Add(parameter);
        }
    }

    /// <summary>把 T-SQL 写法的裁判<em>字形</em>换成这一家的写法，<b>只换字形、不改语义</b>（边界与
    /// 为什么收成一颗函数，都写在类注释那两段里）。三件事按顺序做：
    /// <list type="number">
    ///   <item><c>[dbo].[X]</c> 与 <c>[X]</c> → <c>"X"</c>。<b>大写不是随手折的</b>：本仓库这一家的
    ///   <c>CodeFirst</c> 产出的就是 <c>"QUICKSQLORDER"</c> 这一形（黄金样本
    ///   <c>tests/QuickSql.Tests.Dialects/golden/xugu/ddl-order.sql</c> 第一行），建表与查询两侧都折成
    ///   同一个形才有对象可对；不折的写法（不加引号）这一家也会折成大写，所以两种起点落到同一处。
    ///   而<em>绑进去的值</em>它管不到，那一颗在 <see cref="Fold"/>。</item>
    ///   <item><c>@p</c> → <c>:p</c>：占位符前缀，来自 <c>XuguDialect</c> 的 <c>ParameterPrefix</c>。
    ///   名字本身<em>不</em>在这里换，只换前缀 —— 换名字要成对地改 <paramref name="args"/>，
    ///   那是语义差，得留在调用点点名（<c>:table</c>/<c>:like</c> 撞关键字那两红就是撞在这一颗上，
    ///   记 <c>obj/logs/v32-recon-txn-2.log</c>）。</item>
    ///   <item><c>N'…'</c> → <c>'…'</c>：这一家没有 <c>N</c> 前缀这颗语法（文本本来就是国家字符集）。</item>
    /// </list>
    /// <b>它<em>不</em>翻译的东西同样重要</b>：<c>sys.*</c>、<c>OBJECT_ID</c>、<c>COL_LENGTH</c>、
    /// <c>SCOPE_IDENTITY</c>、<c>SERVERPROPERTY</c>、<c>CONVERT</c>/<c>DATALENGTH</c>/<c>UNICODE</c>、
    /// <c>TOP</c>、<c>OUTPUT INSERTED</c> —— 一家有一句而另一家没有那一句时，正确的做法不是造一句相近的，
    /// 而是让用例在第一句 <see cref="JudgeWrittenFor"/> 承认"这一句没有对应物"。
    ///
    /// <para><b>第七家走的是同一颗函数，而清单短一半</b>：三件事里它只做两件、而且第一件<em>不折大写</em>
    /// （下面 <see cref="SqliteText"/> 那一段写着为什么不折）。<b>占位符那一件在本家整个取消</b> ——
    /// <c>@p</c> 就是本家驱动认的那一形【实测·4503/4702/4731 那一排 <c>BND</c> 行，全部带前缀命中】。
    /// 三形里只有一形与本家相同（<c>N''</c> 要脱掉前缀：本家把它读成一颗列名【实测·4728：
    /// <c>no such column: N</c>】而不是报语法错），两形是本家没有的对象，
    /// 而"没有"在这里<em>不是</em>退让 —— <c>TOP 1</c> 那一形在这一族连近似写法都不许造【实测·4730：
    /// <c>near "1": syntax error</c>，可用的那一形是 <c>LIMIT n OFFSET m</c>【实测·4733】，
    /// 那是"一句有、一句没有"，归用例点名）。</para></summary>
    static string Translate(string sql)
        => Family switch
        {
            Target.Xugu => XuguText(sql),
            Target.Sqlite => SqliteText(sql),
            Target.PostgreSql => PostgresText(sql),
            Target.MySql => MySqlText(sql),
            _ => sql,
        };

    static string XuguText(string sql)
    {
        sql = SchemaQualified.Replace(sql, m => '"' + m.Groups[1].Value.ToUpperInvariant() + '"');
        sql = Qualified.Replace(sql, m => '"' + m.Groups[1].Value.ToUpperInvariant() + '"');
        sql = National.Replace(sql, "'");

        return Placeholder.Replace(sql, ":$1");
    }

    /// <summary>把 T-SQL 的<em>字形</em>换成本家的，两句：<c>[dbo].[X]</c> 与 <c>[X]</c> → <c>"X"</c>（
    /// <b>原样搬名字，不折大写</b>），<c>N'…'</c> → <c>'…'</c>。占位符不动。
    ///
    /// <para><b>为什么不折，而这一格必须钉死</b>：本家的目录<em>存的就是建表时那份拼法</em> ——
    /// 带引号建出的 <c>"QuickSqlMix"</c> 按 <c>'QuickSqlMix'</c> 问得到【实测·4702】、按大写问交 0 行
    /// 【实测·4703】，列名同判【实测·4704 ∶ 4705】，而索引名也原样还【实测·4736 ∶ 4737】。
    /// 第五家那条"折成大写"的规则在这里<em>照抄就是把裁判写错</em>：本层那三处"逐列比目录"的用例会
    /// 在 4703 那一颗对照组上恒红，而红点读起来像"库里没有这一列"。
    /// <b>方括号那一形为什么非换不可</b>：<c>[V]</c> 在本家不是引号而是<em>名字的一部分都不算</em> ——
    /// <c>SELECT [V] FROM [dbo].[QuickSqlMix]</c> 交回的是 <c>no such table: dbo.QuickSqlMix</c>
    /// 【实测·4729】，也就是它把整串当一个标识符去找，而不是报错说语法不认识。</para></summary>
    static string SqliteText(string sql)
    {
        sql = SchemaQualified.Replace(sql, m => '"' + m.Groups[1].Value + '"');
        sql = Qualified.Replace(sql, m => '"' + m.Groups[1].Value + '"');

        return National.Replace(sql, "'");
    }

    /// <summary>第四家的字形：方括号换双引号而<em>名字原样搬</em>（与第七家同一支），<c>N'…'</c> 脱掉前缀，
    /// <b>占位符一形不动</b> —— 那颗 <c>@</c> 在本家是驱动认的两形之一，与 T-SQL 同一形。
    ///
    /// <para><b>不折大写的凭据是本家那三颗对照</b>：带引号建出的 <c>"QuickSqlProbeD"</c> 按
    /// <c>'QuickSqlProbeD'</c> 问 <c>information_schema</c> 交 8 行【实测·C-01】，同句改按大写
    /// <c>'QUICKSQLPROBED'</c> 交 <b>0 行</b>【实测·C-02】，列名同判【实测·C-05 交回的是
    /// <c>Id ∶ Name ∶ PaidAt</c> 这一排原样拼法】。这一族对标识符的规则是"<b>带引号原样存，不带引号折成
    /// <em>小写</em></b>" —— 与虚谷那条"折成大写"方向相反，所以把第五家那一支照抄过来会得到一屏
    /// "目录里没有这一列"，而真相只是折错了方向（<see cref="CatalogueName"/> 那一格的对照读法同一颗）。
    /// 本仓库的 <c>PostgreSqlCodeFirst</c> 全程带引号，于是建出来的与查的都是同一串 CamelCase。</para>
    ///
    /// <para><b>占位符这一件在本家整个取消，而它是量出来的、不是"应该兼容"</b>：四形各跑一遍
    /// 【实测·B-01 ∶ B-02 ∶ B-03 ∶ B-04】—— SQL 文本里写 <c>@p</c> 而 <c>ParameterName</c> 也带前缀、
    /// 文本写 <c>:p</c> 而名字不带、文本写 <c>@p</c> 而名字不带、文本写 <c>:p</c> 而名字带前缀，
    /// <b>四形全部命中同一颗值 7</b>。也就是说这一家对前缀与名字两头都宽容，本层沿用 T-SQL 那一形
    /// （文本 <c>@p</c> ∶ 名字 <c>@p</c>）就一处都不用改，<see cref="Bind"/> 里那颗剥前缀的闸门
    /// 在本家不开。与第七家那条【实测·4503 那一排】同一形状而理由不同：本家只认 <c>@</c> 一形，
    /// 而那一家是<b>两形都认</b>。厂商文档另有一条要记着的坑：<c>@</c> 在 PG 原文里是一元运算符，
    /// 所以一颗<em>没绑上</em>的 <c>@v</c> 不会被报成"缺参数"，而是报成 <c>column "v" does not exist</c>
    /// 【实测·M-05 那一红，同一句补上参数就绿【实测·M-06】】—— 将来在本家看到那颗错，先数参数够不够，
    /// 不要去改引号。</para></summary>
    static string PostgresText(string sql)
    {
        sql = SchemaQualified.Replace(sql, m => '"' + m.Groups[1].Value + '"');
        sql = Qualified.Replace(sql, m => '"' + m.Groups[1].Value + '"');

        return National.Replace(sql, "'");
    }

    /// <summary>MySQL 这一家的字形：方括号换<em>反引号</em>而名字原样搬，<c>N'…'</c> 脱掉前缀，占位符一形不动。
    ///
    /// <para><b>为什么换的是反引号而不是第四家那双引号，而这一格是量出来的</b>：这一台实例的
    /// <c>sql_mode</c> 里<em>没有</em> <c>ANSI_QUOTES</c>【实测·S-03 那一串：只有 <c>ONLY_FULL_GROUP_BY</c>、
    /// <c>STRICT_TRANS_TABLES</c>、两颗 <c>NO_ZERO_…</c>、<c>ERROR_FOR_DIVISION_BY_ZERO</c> 与
    /// <c>NO_ENGINE_SUBSTITUTION</c>】，于是 <c>"X"</c> 在这里是<em>字符串字面量</em>而不是标识符 ——
    /// 探针按 <c>SELECT "Id" FROM …</c> 问，交回的头一行就是常量 <c>Id</c>【实测·D-02】。
    /// 那一形不会报语法错，它<em>安静地答错</em>，所以这一支不能靠"跑不通"来发现。
    /// 反引号那一形是本仓库 <c>IdentifierPolicy.MySql</c> 声明的那一颗（QuoteAlways、不折大小写），
    /// 与建表侧同一个字形才有对象可对【实测·D-01 反引号建表收，目录里原样还】。</para>
    ///
    /// <para><b>名字不折，而这一格有两条相反的读数要一起钉</b>：① 库侧 <c>lower_case_table_names = 0</c>
    /// 【实测·S-04】，表名按字节存 —— 同一颗 <c>utf8mb3_bin</c> 让三形只中原名【实测·Y-03】，
    /// 折一次就变"永远答不存在"（第四家那条"折成小写"、第五家那条"折成大写"在这里都会红）；
    /// ② <c>N''</c> 那一件在本家<em>换不换都发得出去</em>（厂商把 <c>N'…'</c> 收进自己的字面量语法），
    /// 而本层仍然脱掉那颗前缀：一来它与第四家、第七家同一手，二来这一台的字符集已经是
    /// <c>utf8mb4</c>【实测·S-04】，前缀不携带任何这一族需要的信息 —— 中文绑定值原样还的那颗读数在
    /// 【实测·B-06】，它量的是<em>参数</em>那一形，不是 <c>N''</c> 字面量那一形，两者不要混着引。
    /// <b>占位符那一件在本家整个取消</b>：四形对照里 <c>@p</c> 两形都命中【实测·B-03 名字带前缀 ∶
    /// B-04 名字不带，两句都交 <c>7</c>】，而 <c>:p</c> 那一形是正面拒的语法错【实测·B-05】——
    /// 也就是说这一家与第五家（要换成 <c>:</c>）正好分居两侧，与第七家、第四家同一侧。
    /// 另外两颗不进这一颗函数：<c>LIMIT n OFFSET m</c> 与 <c>TOP</c>/<c>OFFSET…FETCH</c> 是<em>有与没有</em>
    /// 的关系【实测·Z-01 收 ∶ Z-02 ∶ Z-03 两句都是语法错】，按 <see cref="JudgeWrittenFor"/> 那一档办。</para></summary>
    static string MySqlText(string sql)
    {
        sql = SchemaQualified.Replace(sql, m => '`' + m.Groups[1].Value + '`');
        sql = Qualified.Replace(sql, m => '`' + m.Groups[1].Value + '`');

        return National.Replace(sql, "'");
    }

    static readonly Regex SchemaQualified = new(@"\[dbo\]\.\[([^\]]+)\]", RegexOptions.IgnoreCase);
    static readonly Regex Qualified = new(@"\[([^\]]+)\]", RegexOptions.CultureInvariant);
    static readonly Regex National = new(@"(?<![\w$])N'", RegexOptions.IgnoreCase);
    static readonly Regex Placeholder = new(@"@([A-Za-z_]\w*)");
}
