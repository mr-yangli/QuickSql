using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§13.5 那句"每一个声明出来的能力位都要有一个见证"在<em>没有活库</em>的现场怎么兑现。
///
/// <para>金仓、达梦、MySQL 与虚谷这四系今天一句 SQL 都没在真的解析器上跑过（授权的活库只有两台 SQL Server），
/// 所以那条判据没有可以执行的形态。这里给的是一份<b>会红的台账</b>：每一位要么写着"离线文本已钉（并指出是哪份黄金样本
/// 或本套件里的哪条断言）"，要么写着"仅厂商文档"。<c>_ledger 里的行_</c> 与 <c>IDbProvider.Capabilities</c>
/// 必须逐位相等 —— 于是"新增一个能力位"要么带来一行出处，要么当场编译不过断言，而不是安静地多一句乐观声明。</para>
///
/// <para><b>上面那一句"四系今天一句 SQL 都没跑过"在 v0.32 第二格只作废了虚谷那一半</b>：这一族现在有了一台
/// 授权实例（<c>TESTDB</c> / <c>XuguDB 12.0.0</c>），本文件那四位因此翻成 <c>Live</c>（逐位的执行见证写在它们
/// 各自的注释上，不在这里重复），其余三位各有一条不翻的理由。金仓、达梦、MySQL 三族这一句<em>仍然一字不改</em>，
/// 而这句话现在是有钉子的：<c>PinnedLive</c> 那三颗 0 与 <c>§13.5</c> 那本活库见证账要对得上。</para>
///
/// <para><b>【v0.40 批量写侧那一轮 —— 原文照抄在前（上面两段），改判在后】上面那句「金仓、达梦、MySQL 三族这一句
/// 仍然一字不改」里的 MySQL 那一半今天作废，而它不是在 v0.37 第九笔（那台 MySQL 实例到场、集成册整本跑绿）作废的，
/// 是在本笔作废的：那一场动的是"有没有活库"，本笔动的是"这一本台账里有没有一位的出处落在那台库上"。
/// <c>mysql</c> 那本的 <c>Bulk_Copy</c> 现在带着集成册的日志名站着 <c>Live</c>，于是「<c>PinnedLive</c> 那三颗 0」
/// 今天只剩两颗 0（金仓 ∶ 达梦），第三颗数从 0 变成 1。同向的还有 PG 本尊那一本（<c>postgresql</c>，
/// 第十三本）：它那一位也从 0 变 1。两句合起来的读数——十五本台账里 <c>PinnedLive</c> 非零的从两本变四本
/// （虚谷四位 ∶ SQLite 三颗 ∶ 本笔新添的两本各一颗），而"三族一字不改"那句的范围缩成两族。
/// 上面那段"四系今天一句 SQL 都没跑过"与它下面那句"其余三位各有一条不翻的理由"都按本笔改读数量词，
/// 判据一字不动：翻档要的是"由本 provider 产出的那一条被那台库执行过"，本笔给 MySQL 与 PG 各交出了那一条。</para>
///
/// <para><b>一家一份，而不是几家共用一份</b>：这四份名册今天差得很远 —— 14 位对 6 位对 5 位对 7 位，而"仅厂商文档"
/// 那一档的位数是 7 对 0 对 0 对 0。<b>【本笔改判】这两行读数一起动，而动的方向是同一格新形状</b>：PG 那本
/// 14 → 15（"仅厂商文档"7 → 8），MySQL 那本 5 → 6（0 → 1），0 那一侧此刻站着达梦／神通／Oracle／虚谷／
/// GBase 8s 五本。新添的那一位不是又一颗文档乐观声明，是 <c>Bulk_Copy</c> ——<b>这本账第一颗没有 SQL 文本可指
/// 的位</b>：代码接的是装载通道那段反射装配，它按 Live 的判据（"由本 provider 产出的那一条被那台库执行过"）
/// 还差集成册那一步，所以落"仅厂商文档"那一档，而落档理由与老七颗异因（那七颗是"文档说有而代码没接"，
/// 这两颗是"代码接了而库没证"）。"一家一份"这条判据本身不动：它管的仍是"同掩码也不合并见证"。
/// 共用一份台账会把这两处形状一起抹平，而它们正是 §4 那笔"两族能力位不一致是待收敛的账"
/// 的全部内容。所以这里逐家一份，四家共有的那三位另由
/// <see cref="The_offline_ledgers_declare_only_the_bits_they_really_have_in_common"/> 对读一次。
/// <b>v0.35 第三笔把"一家一份"读成了"一本 provider 一份"</b>：第五份（tidb）是 MySQL 血统的第二本，
/// 掩码与 mysql 那本逐位同形，而见证各是各的 —— "一家一份"的原句不删，它量的是 v0.31 之前的 provider
/// 分布恰好一家一本；那个"恰好"从今天起不再成立，而剩下的判据（同掩码也不合并见证）反而更纯了。
/// <b>v0.35 第四笔把那条判据当场复用了第二次</b>：第六份（oceanbase）是 MySQL 血统的第三本，与前两本逐位
/// 同掩码而见证各是各的 —— "一本 provider 一份"不再需要加粗，它已经是这本账的常态形状（六本 ∶ 五条血统）。
/// <b>v0.36 第二笔把册数带到七本</b>：第七份（opengauss）是 PG 血统的第二本，14 位与金仓本逐位同掩码，
/// 而对拍链的中间那颗换成了邻家叶子 —— PG 血统没有"本尊"可回指。<b>【v0.37 第二笔自纠】那一句今天作废</b>：
/// 原句不删，因为它量的从来是"当时在场的六本里没有本尊"，而不是"这一族不会有"；作废它的是第十三本，见下面
/// v0.37 第二笔那一段。这是"一本 provider 一份"的第三次复用，
/// 也是第一次<em>跨血统</em>的复用：前两次（tidb/oceanbase）都发生在同一份 FamilyCapabilities 内部。
/// <b>v0.36 第三笔把册数带到八本</b>：第八份（gaussdb）是 PG 血统的第三本，与金仓本、openGauss 本
/// 逐位同掩码而对拍链长成四段 —— "第四次复用"与"第一次跨血统"这两句都被本册改写：复用的方向反过来
/// 走了一遍（openGauss 本复用金仓本的判据，本册一次复用两本），而"每接一家交集就小一格"的隐含预期
/// 到这里有了第四份反证（八本 ∶ 三位，仍不变）。
/// <b>v0.36 第四笔把册数带到九本</b>：第九份（highgo）是 PG 血统的第四本，与另三本逐位同掩码
/// （四本 14 位出自同一颗 FamilyCapabilities），"一本 provider 一份"的第五次复用已无"第几次"可加粗；
/// 本册真正的新消息在两处形状：它是这一族第一本<b>整本没有任何前件</b>的台账（厂商行坑列零
/// 方言级点名，连【推断】都不立——"复件册涨到第四本仍然量不出新事实"由此有了最纯的一份），
/// 而两两互读补满格把血统四叶的六条边收齐，交集链第五次复点仍是那三位（九本 ∶ 三位，第五份反证）。
/// <b>v0.36 第五笔把册数带到十本</b>：第十份（shentong）是 Oracle 血统的第二本，6 位与达梦本逐位同掩码
/// （两本出自同一颗 FamilyCapabilities，"一本 provider 一份"的第六次复用）——而本册给这本账带来第一格
/// <b>同掩码而不同形</b>的读数：此前四对同掩码的邻居（tidb∶mysql、oceanbase∶前两家、opengauss／gaussdb／
/// highgo∶金仓）掩码相同处产出也相同，shentong 与 dameng 掩码逐位同而 CREATE TABLE 差在恰好那道缝 carry
/// 的一句。能力位台账量的是"声明了哪几位"，从来量不到"这几位的文本长什么样"——这句话此前只在注释里，
/// 今天第一次有一对台账让它可指认。交集链第六次复点仍是那三位（十本 ∶ 三位，第六份反证）。
/// <b>v0.36 第六笔把册数带到十一本</b>：第十一份（gbase8c）是 PG 血统的第五本，14 位与另四本同源于一颗
/// FamilyCapabilities（"一本 provider 一份"的第七次复用，本族已无人再数它是第几次）。本册给这本账带来的
/// 新形状在<b>前件的来路</b>而不是前件的有无：金仓本有名点过的 SERIAL vs IDENTITY，openGauss／GaussDB 两本
/// 挂着本行自证的 9.2 内核代数（一跳），瀚高本是唯一一本整本无前件的，而本册那一问的前件是<em>转引来的</em>
/// ——行 905 不答 IDENTITY，只写"与 openGauss 高度相似"，于是那一问要两跳才落地（本行→邻家行→内核代数）。
/// 同一颗"若这一家认得血统今天发的形状则文本同形"的读法，第一次带着邻家的名字。交集链第七次复点仍是那三位
/// （十一本 ∶ 三位，第七份反证）。</para>
/// <para><b>v0.37 第一笔把册数带到十二本，而这一本不再是复点</b>：第十二份（gbase8s）是 Informix 血统的第一颗，
/// 声明位只有 4 颗——它是六族里第一本"每一颗都在别人见过的位上、却有一颗别人都见过的位它没有"的台账。
/// 于是交集第一次变小：<b>3 位 → 2 位</b>（<c>Schemas</c> ∶ <c>Alter_Column_Precision</c>），掉出去的那一颗是
/// <c>Comment_On_Column</c>，而它掉出去的原因是这一族的语句清单里压根没有 <c>COMMENT ON</c> 那一行
/// （列注释走 Core 的"跳过并 WARN"退路）。下面那段 v0.32 的预测今天兑现了，兑现的方向是反的：
/// 红的一刻不是"某一家接了只有它有的一种能力"，是"某一家没有别人都有的那一种"——<b>多一颗位涨的是名单，
/// 少一颗位削的是地基</b>，而这份账此前十一本只演示过前一种。第二格新消息在本家的分页：它有分页产出
/// （<c>SELECT FIRST n SKIP m</c>，句首限定词，§9.4 那句"三套实现"到本家变四套）而分页声明位为<b>零</b>，
/// 因为枚举里没有第四颗——这本账第一次有一行"能力面比名单宽，而宽的那一格不能报"。</para>
/// <para><b>v0.37 第二笔把册数带到十三本</b>：第十三份（postgresql）是 PG 血统的第六本，也是这一族第一颗
/// <b>本尊</b>叶——它让上面"PG 血统没有本尊可回指"那一句（v0.36 第二笔立下的）当场作废，而作废它是本笔唯一
/// 一处<em>推翻既有断言</em>的动作，其余全是复点。"一本 provider 一份"的第八次复用；14 位与另五本同源于一颗
/// FamilyCapabilities，交集链第九次复点仍是那两位（十三本 ∶ 两位，第一次缩小之后的第一次不变）。
/// 本册给这本账带来的新形状在<b>前件的方向</b>：另五本对 IDENTITY 那一位的表态各有各的来路（金仓点名、
/// openGauss／GaussDB 挂本行自证的 9.2 内核代数、瀚高无前件、8c 转引邻家），而本尊那一问不再问"认不认"或
/// "像到哪一层"——它问的是<em>现场那台是几</em>。这一格今天第一次可算，因为血统的文本层里有两颗版本钉死的
/// 格子朝相反方向指（<c>IdentityClause</c> 的 SQL 标准那一支要 PG 10+，<c>PostgreSqlExpressions</c> 那三颗
/// <c>CAST(… AS numeric)</c> 按"最老的内核"写而它是 14 之前的读法）：五叶时这两格各由别家兜住、无人需要
/// 同时满足，本尊到场后同一台库必须同时吃下两句，于是三段区间（&lt;10 语法错 ∶ 10–13 恰合 ∶ ≥14 冗余）
/// 第一次算得出来，而它是本仓库自己算的【推断】——厂商行里没有这一句。</para>
/// <para><b>v0.37 第三笔把册数带到十四本</b>：第十四份（oracle）是 Oracle 血统的第三本，也是这一族第一颗
/// <b>本尊</b>——它作废的不是 §9.1 的一句表口径（那一行本来就没有 Oracle 本尊，本笔补的是行），而是
/// <c>OracleProviderBase</c> 类注释第一段那位挂了四版的住户："达梦/Oracle 本尊/人大金仓的那一支 Oracle 模式
/// 共享这一份"，三个住户里那一颗本尊今天才到场。6 位与达梦本、神通本逐位同掩码（"一本 provider 一份"的
/// 第九次复用，本族第二本同掩码的账），交集链第十次复点仍是那两位（十四本 ∶ 两位）。
/// 本册给这本账带来的两格新形状都在<b>同形与异形之间那条线画在哪里</b>：① 这一族第一次有两颗逐字同形的
/// 真 provider（本尊 ∶ 神通），此前 Oracle 血统的两两对读只有"各自与基类对"与"两叶差一句"两种形状；
/// ② 上一笔在 PG 本尊上量出的"零覆写的正典叶带代价"今天有了<em>反例</em>，而反例正是正典叶这一档自己交的——
/// 这一族的两格版本读数（<c>IdentityClause</c> 要 12c 起、标识符上限在 12.1 之前是 30 字节）<b>分界是同一颗
/// 12.x</b>，所以区间两段而不是三段，本尊落在"两格一起满足"那一侧，零覆写而零代价。于是那句判据要改读：
/// 正典叶带不带代价，量的不是"它是正典"，是"它族里两格版本读数的朝向"。同一格的可执行那一半在下面
/// 本册的 Guid 行与 <c>OracleDialectTests</c> 那条长名断言里，而它不新添一颗位——这本账今天涨的是
/// <em>同一句话有第三家说</em>。</para>
/// <para><b>v0.37 第四笔把册数带到十五本，而这一本不是那条链上的一票</b>：第十五份（sqlite）是第七家，
/// 也是"一本 provider 一份"这条判据<em>字面意义上</em>的那一份——它没有同掩码的邻本，PG 血统那五本与
/// MySQL 血统那三本谁也不与它逐位相同，所以本册不递增复用次数。它递增的是另外三格：
/// ① <b>第三档</b>——本家三颗 <c>Live</c> 没有一颗出自手写 recon 脚本，三颗的执行见证是同一条集成日志
/// （<c>obj/logs/v37-sqlite-int-4.log</c>），也就是说这本账第一次有一行的"库里接得住"完全由
/// <c>QueryEngine</c> ∶ <c>InsertEngine</c> ∶ <c>CodeFirst</c> 的产出来背——而第三颗是这本账第一颗落在
/// <b>DDL 侧</b>的 <c>Live</c>，它的见证不在日志里而在事后翻库翻出来的那颗索引上；② <b>第二档</b>——<c>VendorDocOnly</c> 那一颗
/// 第一次落在 PG 血统之外，而它那一颗的理由与那七颗相反（那七颗是"文档说有而代码没接"，本颗是
/// "行为在活库上量过而这一位在 src/ 里没有发射口"）；③ <b>交集</b>——这条链第二次变小
/// （<b>2 位 → 1 位</b>，掉出去的是 <c>Alter_Column_Precision</c>），本家给的是"多一颗涨名单、少一颗削地基"
/// 那句话的第二次兑现，而两次兑现都在"少"的那一侧。剩下的那一颗是 <c>Schemas</c>，而它此刻正在两家手里
/// 指着不同的东西（GBase 8s 那一颗是"所有者"，本家那一颗是"挂进来的另一个文件"）——于是这本账第一次
/// 出现"交集只剩一颗，而那一颗跨家不同义"的形状：交集的"共"字到这一笔只剩一颗的体量，而那颗的<em>语义</em>
/// 已经不是同一颗。Guid 那一格也换了形状：本家是立项以来第一本<b>两颗 Guid 位都不声明</b>的账，
/// 上面那段"1∶4∶…两边都是库种事实"的比值从此要加第三种读法（不是"落哪一边"，是"两边都不落"）。</para>
///
/// <para><b>v0.39 第一笔把册数带到十六本</b>：第十六份（duckdb）是 PG 血统的第七本，也是这一族第一颗
/// <b>真的改了血统产出的叶子</b>——此前六本在文本层全部逐字同形、对拍代钉，本家类型表四格换名
/// （<c>DOUBLE</c>/<c>TIMESTAMP</c>/<c>TIMESTAMPTZ</c>/<c>TIME</c>）、身份列换形态（句外
/// <c>CREATE SEQUENCE</c> + <c>DEFAULT nextval</c>）、"建库"整格不存在，OfflineText 那八位于是头一回
/// <em>在这个族里有了自家的 golden/ 基线可指</em>（PinnedOffline 8 是这一族的最高读数，此前六本各 7）。
/// 掩码照抄血统那 15 位一位不翻——其中 <c>CaseSensitive_Identifiers</c> 与本家的实际行为有偏差
/// （标识符大小写不敏感而保留原样【文档·DuckDB】），偏差全文记 §14，位不删、血统文本不改。
/// 交集链第十三次触链仍是那一位（十六本 ∶ <c>Schemas</c>，缩过两次之后本家既有那两位被扣掉的位、
/// 也交不出别人缺的一位）。<b>PinnedLive 0 是本册最立得住的一格</b>：本机没有任何一台引擎、
/// 驱动从未解析成功，§13.5 那一行整行「未实测」，逐格口径与出处 URL 都挂在那本台账上。</para>
///
/// <para><b>v0.39 第二笔把册数带到十七本</b>：第十七份（clickhouse）是单层 provider 的第二颗
///【文档口径行】（继 duckdb 之后的又一次"整层文档、零实测"，本机没有引擎、<c>ClickHouse.Client</c>
/// 从未解析成功），也是这本账里<b>第一颗"写语句两格全空"的台账</b>：UPDATE/DELETE 在本家整格拒发
/// （官方形状都在变异档【文档·ClickHouse：alter 页与 lightweight DELETE 页】），写路径那三位
///（<c>Returning</c>/<c>Upsert_*</c>/<c>Transactions</c>）因此一位都不声明 ——
/// <b>它还是交集链上第一颗把"内检索引位"扣出交集的家</b>：<c>Transactions</c> 十六本逐本声明，
/// 本家取"没有"那一格（内核写路径逐句 autocommit 直发、多语句不原子，公开 <c>BeginTransaction</c>
/// 无位即抛），于是交集从"十六本 ∶ 两位"变"十七本 ∶ 一位"——
/// 第十四次变小，而这一次削的是一笔内检索引位，不是一句库种事实。声明位只有 6 颗
///（<c>Paging_LimitOffset</c>/<c>Boolean_Type</c>/<c>Guid_Raw16</c>/<c>Schemas</c>/
/// <c>Alter_Column_Precision</c>/<c>CaseSensitive_Identifiers</c>），PinnedOffline 6 = PinnedLive 0，
/// 五份自家 golden/ 基线加一条 UUID 断言（<c>insert-with-keys</c> 那份基线在本家不落盘：
/// <c>EmitReturning</c> 整格抛，黄金样本改判为"断言抛"）。</para>
///
/// <para><b>v0.39 第三笔把这一本的 PinnedOffline 清了零</b>：2026-10-20 第一台引擎（server
/// 26.9.21.9 ∶ 驱动 <c>ClickHouse.Client</c> 7.14.0，NuGet 即装即连、无 native 依赖）到场，六位逐颗
/// 换 <c>Live</c>、颗颗带可点名的见证语句（golden 逐字回放为主，出处行内注明；探针与回放输出
/// 存 <c>obj/logs/v39-ch-*.md</c>）。翻位的是"已声明的六位"，写路径位的"整格拒"与
/// <c>Transactions</c> 的"没有"两档立场原样不动 —— 活库读数反而给它们添了正面证词：
/// <c>BeginTransaction</c> 两颗重载均当场抛 <c>NotSupportedException</c>，lightweight DELETE
/// 服务端认语法（变异档立场不改）。</para>
///
/// <para><b>v0.39 第五笔把册数带到十八本</b>：第十八份（tdengine）是单层 provider 的第三颗
///【文档口径行】（继 duckdb、clickhouse 而来，时序库这一档的第一颗；本机没有引擎、驱动程序集
/// <c>TDengine</c> —— 包名 <c>TDengine.Connector</c> 不是程序集名 —— 从未解析成功），整层
///【文档·TDengine】口径。声明位只有 4 颗（<c>Paging_LimitOffset</c>/<c>Boolean_Type</c>/
/// <c>Guid_Char36</c>/<c>Schemas</c>），四颗全是 OfflineText、各自指一份自家 golden/ 基线；
/// 写路径那组一位都不声明：3.x 无传统 UPDATE、DELETE 只认首列时间窗（两格在方言整格拒），
/// 无 RETURNING、无 identity（主键就是那颗时间戳、由调用方给值）、驱动 <c>BeginDbTransaction</c>
/// 当场 throw（<c>Transactions</c> 与 clickhouse 同格取"没有"）；
/// <c>Comment_On_Column</c> 也不声明——本家没有列注释这一格（官方 column_definition 文法里没有
/// COMMENT 项，INS_COLUMNS 目录也没有备注列），而表注释那半能发也能读回，不欠一颗位。
/// <c>insert-with-keys</c> 那份基线在本家不落盘（回填被内核先拦、方言侧 EmitReturning 整格抛，
/// 黄金样本改判为"断言抛"）。</para>
///
/// <para>第四份（虚谷）带来的不是"又多一位乐观声明"，而是<b>交集没变</b>这一件反常事：它声明的 7 位全部落在
/// 前四家已经出现过的位上，于是"每接一族交集就小一格"那句隐含预期第一次有了反例。那一格不是运气，是判据
/// 被执行的结果 —— 掩码按"代码今天真的走到那一条语句"攒，不按"这一族文档里有什么"抄，所以新增一族能带来的
/// 位天然只有"已经有人带来过的那几位"。四份台账的 <c>VendorDocOnly</c> 全是 0 也是同一件事的另一半。
/// 反过来读这一格才是它的用处：哪天某一家接了只有它有的一种能力，交集会当场从三位掉到两位，而这一份文件
/// 是第一个红的地方。<b>v0.37 第一笔：这一格今天真的红了，走的却是那条没被预测到的反路</b>——不是多出一颗
/// 别人没有的位，是新来的那一家少了别人都有的一颗（<c>Comment_On_Column</c>）。预测里"接了"两个字量的是
/// 掩码变宽，而交集对掩码变宽是钝感的（邻家没接就不动）、对掩码变窄是敏感的：这条读法以前只在注释里，
/// 今天有了可执行的那一半。</para>
///
/// <para><b>T-SQL 那一族不进这份台账</b>，这不是漏记：它的每一位都由集成套件在两台真库上跑过
/// （§13.4/§13.5 的用例即见证），一份"见证是另一个测试"的名册只会多一处要同步的地方。
/// 这里管的是"今天<em>没有</em>那种见证"的那四族，而那件事必须显式到能被数出来。</para>
///
/// <para>每份台账的三颗计数常量（<c>Declared</c>/<c>PinnedOffline</c>/<c>VendorDocOnly</c>）是本文件的钉子。
/// 往上走的合法路径只有一条：那一格补了离线样本或真库见证。<b>把「仅厂商文档」改成一格「离线」而不加样本，
/// 这条测试不会红</b>——它红的是"有没有名字对不上的行"与"计数变了没"，状态措辞本身仍要靠人读，
/// 这是这份台账今天能力的边界，写在这里而不是留成一个暗示。</para></summary>
public sealed class CapabilityLedgerTests
{
    enum Provenance
    {
        /// <summary>在场实例真跑过（两台 SQL Server ∶ v0.32 第二格起的那台虚谷 ∶ v0.37 第四笔起的那座
        /// 临时文件库 SQLite）。<b>判据是"由本 provider 产出的那一条语句被那台库执行过"</b>，不是"那台库连上过"，
        /// 也不是"探针自己写的一句同形 SQL 收得回来"—— 后者连 <c>XuguDialect</c> 的一个字都没经过。
        /// <b>这条判据到 v0.37 第四笔才有第二种满足方式</b>：虚谷那四位靠的是把基线文本逐句敲给那台库
        /// （recon 的 G-10 段），本家那三颗靠的是集成套件里 ORM 与 CodeFirst 自己拼出来的语句——两边都没有一行手写文本参与，
        /// 而本家那三颗里最晚翻的那一颗（<c>MultiStatement</c>）是<b>这本账第一颗落在 DDL 侧的 Live</b>：
        /// 它的见证不是日志，是事后翻库翻出来的一颗索引。
        /// 十五本里今天两本有（虚谷四位 ∶ SQLite 三颗），其余十三本一位都没有。</summary>
        /// <remarks><b>【v0.40 批量写侧那一轮 —— 原文照抄在前（上面三行），改判在后】上面那句"两本有 ∶ 十三本一位都没有"
        /// 今天是四本有、十一本没有</b>：新增的是 <c>mysql</c> 与 <c>postgresql</c> 那本各自的一位 <c>Bulk_Copy</c>，
        /// 各自的逐条出处写在这两行的见证串上。上面第一句那份在场实例清单同步添两支（v0.37 第九笔到场的那台
        /// MySQL 8.4.8 ∶ 同一笔到的那座 PostgreSQL 14），而那一句"由本 provider 产出的那一条被那台库执行过"
        /// 在本笔正是第一次由<em>装载通道</em>而不是由 SQL 文本满足：那一条不是本仓库拼出的文本，
        /// 是本仓库经反射装配把行交进去、由那一家的驱动发出去的装载请求。判据不动，动的是一句判据第一次被哪种产物满足。</remarks>
        Live,

        /// <summary>产出的文本被黄金样本或本套件的断言钉住；库里接不接是另一件事。</summary>
        OfflineText,

        /// <summary>只有厂商文档。发这句 SQL 之前要先在真库上证。</summary>
        VendorDoc,
    }

    record Row(DbCapabilities Bit, Provenance From, string Witness);

    /// <summary>一家一份。<paramref name="Name"/> 是黄金样本目录名，出处里那些 <c>golden/x/y.sql</c>
    /// 就是按它落盘的 —— 台账与样本用同一个名字，才不会有一边改了目录而另一边仍然绿。
    /// <paramref name="PinnedLive"/> 是 v0.32 第二格添的第五颗钉子：它原本是"这一档恒为 0"的一句断言，
    /// 而那句断言在本版按它自己写的用法兑现了一次（虚谷四位翻 <c>Live</c>，它当场红），所以这里把它
    /// 从"恒 0"换成"钉住的数"—— 换的形状不是放宽，是它终于有了非零的那一侧可对。</summary>
    record Ledger(string Name, Func<IDbProvider> Provider, int Declared, int PinnedOffline, int PinnedLive, Row[] Rows)
    {
        /// <summary>只有厂商文档、今天没有任何实测的位数。<b>这个数字只该往下走</b>：
        /// 往下走意味着接了样本或上了活库。它是算出来的而不是再钉一颗，所以改 <c>From</c> 会当场把
        /// <c>PinnedOffline</c> 或 <c>PinnedLive</c> 那条计数断言顶红，而不是让两份数字各说各话。</summary>
        public int VendorDocOnly => Declared - PinnedOffline - PinnedLive;
    }

    static readonly Ledger[] Ledgers =
    [
        new("kingbase", static () => Kingbase.Provider(SqlServer.NoDriverFactory.Instance), 15, 7, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "golden/kingbase/paging-order-skip-take.sql"),
            new(DbCapabilities.Returning, Provenance.OfflineText, "golden/kingbase/insert-with-keys.sql"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "golden/kingbase/select-entity.sql"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/kingbase/ddl-order.sql"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "golden/kingbase/ddl-alter-and-comment.sql"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "golden/kingbase/ddl-alter-and-comment.sql"),

            // Guid 的 16 字节原生形态：文本侧够得到的是类型名（UUID），库里是不是真按 16 字节存要靠活库。
            // 所以这一位算离线，而它的"16 字节"那一半仍是要在活库上补的（§14）。
            new(DbCapabilities.Guid_Raw16, Provenance.OfflineText, "本文件那条 UUID 断言"),

            // 下面七位今天没有任何消费方或样本：声明它们的是 §9.1/§9.4 的那几家共有说法。
            new(DbCapabilities.Upsert_OnConflict, Provenance.VendorDoc, "位无读者：写侧的一句 upsert 档从本笔起按 IBulkStrategy.UpsertClause 那颗槽分派（槽在血统基类已接，本家一台活库都没有——位与槽是两面，见 MySqlProviderBase 类注释 MultiStatement 那一条），而这一位今天仍然没有消费者"),
            new(DbCapabilities.Cte, Provenance.VendorDoc, "无消费方：共用层没有 WITH 的发射口"),
            new(DbCapabilities.WindowFunctions, Provenance.VendorDoc, "消费方=QuickFx.RowNumber（行号）：文本由共用层 DialectBase.EmitRowNumber 出、十八家零登记模板，而本家这一格还没有活库对账"),
            new(DbCapabilities.Count_Over_Window, Provenance.VendorDoc, "无消费方：CountMethod.CountOverWindow 那一档今天在 QueryEngine 里还是那句未接入的抛（窗口函数那一格有行号了，这一格没有）"),
            new(DbCapabilities.Boolean_Type, Provenance.VendorDoc, "类型表 bool→BOOLEAN，但样例模型里没有布尔列，故无样本"),
            new(DbCapabilities.TempTable, Provenance.VendorDoc, "无消费方：Tp 临时主键走的是真实表而不是临时表（§8.2）"),

            // 第 15 位是本笔挂上血统基类的 Bulk_Copy，它与上面七颗同档而<em>异因</em>：那七颗是"文档说有而代码没接"，
            // 本颗是"代码接了（<c>PostgreSqlBulk.ResolveCopyChannel</c> 那颗反射口）而装载语句一次都没发出去过"——
            // 本家与另五本一样一台活库都没有，所以它翻不了档。本家那颗行级点名（SERIAL vs IDENTITY）问的是内核
            // 代数，与本位无关：这一位问的是驱动。
            //
            // <b>【v0.40 批量写侧那一轮 —— 原文照抄在前（上面那段），改判只改其中一处数量】上面那句「本家与另五本一样
            // 一台活库都没有」里的"五本"今天少成四本：PG 本尊（下面的第十三本）从 v0.37 第九笔起有那台授权实例，
            // 而它这一位本笔已翻 <c>Live</c>。金仓自己那半句照旧成立——本家一台活库都没有，所以它翻不了档；
            // 能与它并排的是 opengauss／gaussdb／highgo／gbase8c 那四本。这句的数量词按本笔改读，判据一字不动。</b>
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "无活库：装载通道已接线（血统那颗反射口），COPY … FROM STDIN 那一句一次都没发出去过"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // PG 血统的第二本台账（v0.36 第二笔，openGauss 叶子到场）。"一本 provider 一份"的第三次复用，
        // 也是<em>第一次发生在两条不同血统之间</em>的前两次镜像：掩码与金仓那本逐位同形（同一份
        // FamilyCapabilities 14 位），见证各是各的——三段式在这里对的是邻家叶子（opengauss == 金仓叶 == 血统基类），
        // 因为 PG 血统没有"MySQL 本尊"可对。<b>【v0.37 第二笔自纠】"没有本尊可对"这一句从今天起不成立</b>——
        // 可对的那一颗就是下面的第十三本（postgresql），而它把那条链从三段长成七段。
        // 它没有 golden/opengauss/ 可指（同形交对拍），七颗 OfflineText
        // 全部落在 OpenGaussDialectTests 的逐字相等断言上。
        // 本家比 tidb 那本多一颗<em>点名过的</em>【推断】前件：血统发的 IDENTITY 那一形在 PG 9.2 内核上
        // 可能整个不认（PostgreSqlCodeFirst 类注释自陈），所以 DDL 段那三位的 OfflineText 在本家读作
        // "若这一家认得血统今天发的形状则文本同形"——真库到场第一问记 §14 与本家类注释。
        new("opengauss", static () => OpenGauss.Provider(SqlServer.NoDriverFactory.Instance), 15, 7, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "OpenGaussDialectTests 那条 LIMIT/OFFSET 尾巴与只写 OFFSET 不补哨兵、与金仓家逐字相等的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.Returning, Provenance.OfflineText, "OpenGaussDialectTests 键回读那条句尾 RETURNING 与金仓家逐字相等的断言（与 tidb/oceanbase 的\"语句后另发一句\"正对面）"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "OpenGaussDialectTests 读侧那条双引号标识符与金仓家逐字相等的断言"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "OpenGaussDialectTests DDL 段那条显式模式建表与金仓家逐字相等的断言（默认 public 写进限定由本册绝对形状钉）"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "OpenGaussDialectTests DDL 段那条 COMMENT ON COLUMN 与金仓家逐字相等的断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "同上：这一对在金仓家是两句串联，本家共用一颗见证（血统同形，槽位不同源）"),
            new(DbCapabilities.Guid_Raw16, Provenance.OfflineText, "OpenGaussDialectTests 那条 UUID 断言（金仓那两条从此有了第一个复件；\"16 字节存储\"那一半与金仓同样欠着）"),

            // 下面七位与金仓那本同一判据、同一批理由：这一档的掩码是从 §4 那张表攒来的乐观声明，
            // openGauss 继承的是同一份，本家一位不翻案、不删减（收敛是 §4 的账）。
            new(DbCapabilities.Upsert_OnConflict, Provenance.VendorDoc, "位无读者：写侧的一句 upsert 档从本笔起按 IBulkStrategy.UpsertClause 那颗槽分派（槽在血统基类已接，本家一台活库都没有——位与槽是两面，见 MySqlProviderBase 类注释 MultiStatement 那一条），而这一位今天仍然没有消费者"),
            new(DbCapabilities.Cte, Provenance.VendorDoc, "无消费方：共用层没有 WITH 的发射口"),
            new(DbCapabilities.WindowFunctions, Provenance.VendorDoc, "消费方=QuickFx.RowNumber（行号）：文本由共用层 DialectBase.EmitRowNumber 出、十八家零登记模板，而本家这一格还没有活库对账"),
            new(DbCapabilities.Count_Over_Window, Provenance.VendorDoc, "无消费方：CountMethod.CountOverWindow 那一档今天在 QueryEngine 里还是那句未接入的抛（窗口函数那一格有行号了，这一格没有）"),
            new(DbCapabilities.Boolean_Type, Provenance.VendorDoc, "类型表 bool→BOOLEAN，谓词侧 IS TRUE 有对拍见证但那是文本档，类型列本体无样本"),
            new(DbCapabilities.TempTable, Provenance.VendorDoc, "无消费方：Tp 临时主键走的是真实表而不是临时表（§8.2）"),

            // 第 15 位是本笔挂上血统基类的 Bulk_Copy，与上面七颗同档而异因（那七颗"文档说有而代码没接"，本颗
            // "代码接了而装载语句一次没发过"）。它与本家那颗 9.2 内核代数的【推断】前件同一档：两问都要真实例
            // 才答得出，而这一问的来路是反射口、不是厂商行。
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "无活库：装载通道已接线（血统那颗反射口），COPY … FROM STDIN 那一句一次都没发出去过"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // PG 血统的第三本台账（v0.36 第三笔，GaussDB 叶子到场 —— 行 904「到场即照本行再挂」裁决的落地）。
        // "一本 provider 一份"的第四次复用：掩码与金仓本、openGauss 本逐位同形（同一份 FamilyCapabilities
        // 14 位），对拍链在这里长成四段（gaussdb == openGauss 叶 == 金仓叶 == 血统），而两颗分叉叶子
        // 互读那一格是 PG 血统的第一次（先例：OceanBase 那册，MySQL 血统）。它同样没有 golden/gaussdb/
        // 可指（同形交对拍），七颗 OfflineText 全部落在 GaussDbDialectTests 的逐字相等断言上。
        // 与 openGauss 那本的形状差一处：上一本多一颗点名过的【推断】前件（IDENTITY 在 9.2 内核上可能
        // 不认），本册对同一问逐颗重裁、裁据一个字没换——共行的厂商读数对两家是同一句；而它比上一本
        // <em>薄</em>一档的地方在驱动来路：openGauss 至少有一条"厂商分发的是 Npgsql 的 fork"的行级说法，
        // GaussDB 自己分发的托管驱动连名字都没有入账读数，DDL 段那三位的 OfflineText 在本册读作
        // "若这一家认得血统今天发的形状则文本同形"，而"认不认"那一问前面还压着"这台库与 openGauss
        // 是不是同一套文本假设"——两问都记 §14 与本家类注释。
        new("gaussdb", static () => GaussDb.Provider(SqlServer.NoDriverFactory.Instance), 15, 7, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "GaussDbDialectTests 那条 LIMIT/OFFSET 尾巴与只写 OFFSET 不补哨兵、与金仓家逐字相等的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.Returning, Provenance.OfflineText, "GaussDbDialectTests 键回读那条句尾 RETURNING 与金仓家逐字相等的断言（两颗分叉叶互读的成功侧另有一格）"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "GaussDbDialectTests 读侧那条双引号标识符与金仓家逐字相等的断言"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "GaussDbDialectTests DDL 段那条显式模式建表与金仓家逐字相等的断言（默认 public 写进限定由本册绝对形状钉）"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "GaussDbDialectTests DDL 段那条 COMMENT ON COLUMN 与金仓家逐字相等的断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "同上：这一对在金仓家是两句串联，本家共用一颗见证（血统同形，槽位不同源）"),
            new(DbCapabilities.Guid_Raw16, Provenance.OfflineText, "GaussDbDialectTests 那条 UUID 断言（金仓那两条从此有了第二个复件；\"16 字节存储\"那一半三家一起欠着）"),

            // 下面七位与金仓本、openGauss 本同一判据、同一批理由：这一档的掩码是从 §4 那张表攒来的乐观声明，
            // GaussDB 继承的是同一份，本家一位不翻案、不删减（收敛是 §4 的账）。
            new(DbCapabilities.Upsert_OnConflict, Provenance.VendorDoc, "位无读者：写侧的一句 upsert 档从本笔起按 IBulkStrategy.UpsertClause 那颗槽分派（槽在血统基类已接，本家一台活库都没有——位与槽是两面，见 MySqlProviderBase 类注释 MultiStatement 那一条），而这一位今天仍然没有消费者"),
            new(DbCapabilities.Cte, Provenance.VendorDoc, "无消费方：共用层没有 WITH 的发射口"),
            new(DbCapabilities.WindowFunctions, Provenance.VendorDoc, "消费方=QuickFx.RowNumber（行号）：文本由共用层 DialectBase.EmitRowNumber 出、十八家零登记模板，而本家这一格还没有活库对账"),
            new(DbCapabilities.Count_Over_Window, Provenance.VendorDoc, "无消费方：CountMethod.CountOverWindow 那一档今天在 QueryEngine 里还是那句未接入的抛（窗口函数那一格有行号了，这一格没有）"),
            new(DbCapabilities.Boolean_Type, Provenance.VendorDoc, "类型表 bool→BOOLEAN，谓词侧 IS TRUE 有对拍见证但那是文本档，类型列本体无样本"),
            new(DbCapabilities.TempTable, Provenance.VendorDoc, "无消费方：Tp 临时主键走的是真实表而不是临时表（§8.2）"),

            // 第 15 位是本笔挂上血统基类的 Bulk_Copy，与上面七颗同档而异因（那七颗"文档说有而代码没接"，本颗
            // "代码接了而装载语句一次没发过"）。它在本家比 openGauss 本在这一位上多压一层：那一家至少有一条
            // "厂商分发的是 Npgsql 的 fork"的行级说法，GaussDB 连自家托管驱动的名字都没有入账读数。
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "无活库：装载通道已接线（血统那颗反射口），COPY … FROM STDIN 那一句一次都没发出去过（本家连驱动名字都没有读数）"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // PG 血统的第四本台账（v0.36 第四笔，瀚高 HighGo 叶子到场）。"一本 provider 一份"的第五次复用，
        // 而本本的处境在这一族里独占一档：§9.1 行 907 的坑列（权限模型、审计策略）一颗方言级分岔都
        // 没点名，所以它既不像金仓本有名点过的 SERIAL vs IDENTITY、也不像另两本挂着 9.2 内核代数的
        // 【推断】前件——DDL 段那三位的 OfflineText 在本册读作最空的一档："若这一家认得血统今天发的
        // 形状则文本同形"，而"认不认"连行级前件都没有，真实例第一问是"兼容的是哪一代 PG"（§14 行 2044
        // 本笔换主语处）。七颗 OfflineText 全部落在 HighgoDialectTests 的逐字相等断言上，其中两两互读
        // 补满格让血统四叶的 C(4,2)=6 条边至此每条至少一颗见证。
        new("highgo", static () => Highgo.Provider(SqlServer.NoDriverFactory.Instance), 15, 7, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "HighgoDialectTests 那条 LIMIT/OFFSET 尾巴与只写 OFFSET 不补哨兵、与金仓家逐字相等的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.Returning, Provenance.OfflineText, "HighgoDialectTests 键回读那条句尾 RETURNING 与金仓家逐字相等的断言（血统两两互读补满的六条边各含此形）"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "HighgoDialectTests 读侧那条双引号标识符与金仓家逐字相等的断言"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "HighgoDialectTests DDL 段那条显式模式建表与金仓家逐字相等的断言（默认 public 写进限定由本册绝对形状钉）"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "HighgoDialectTests DDL 段那条 COMMENT ON COLUMN 与金仓家逐字相等的断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "同上：这一对在金仓家是两句串联，本家共用一颗见证（血统同形，槽位不同源）"),
            new(DbCapabilities.Guid_Raw16, Provenance.OfflineText, "HighgoDialectTests 那条 UUID 断言（金仓那两条从此有了第三个复件；\"16 字节存储\"那一半四家一起欠着）"),

            // 下面七位与金仓本、openGauss 本、GaussDB 本同一判据、同一批理由：这一档的掩码是从 §4
            // 那张表攒来的乐观声明，瀚高继承的是同一份，本家一位不翻案、不删减（收敛是 §4 的账）。
            new(DbCapabilities.Upsert_OnConflict, Provenance.VendorDoc, "位无读者：写侧的一句 upsert 档从本笔起按 IBulkStrategy.UpsertClause 那颗槽分派（槽在血统基类已接，本家一台活库都没有——位与槽是两面，见 MySqlProviderBase 类注释 MultiStatement 那一条），而这一位今天仍然没有消费者"),
            new(DbCapabilities.Cte, Provenance.VendorDoc, "无消费方：共用层没有 WITH 的发射口"),
            new(DbCapabilities.WindowFunctions, Provenance.VendorDoc, "消费方=QuickFx.RowNumber（行号）：文本由共用层 DialectBase.EmitRowNumber 出、十八家零登记模板，而本家这一格还没有活库对账"),
            new(DbCapabilities.Count_Over_Window, Provenance.VendorDoc, "无消费方：CountMethod.CountOverWindow 那一档今天在 QueryEngine 里还是那句未接入的抛（窗口函数那一格有行号了，这一格没有）"),
            new(DbCapabilities.Boolean_Type, Provenance.VendorDoc, "类型表 bool→BOOLEAN，谓词侧 IS TRUE 有对拍见证但那是文本档，类型列本体无样本"),
            new(DbCapabilities.TempTable, Provenance.VendorDoc, "无消费方：Tp 临时主键走的是真实表而不是临时表（§8.2）"),

            // 第 15 位是本笔挂上血统基类的 Bulk_Copy，与上面七颗同档而异因（那七颗"文档说有而代码没接"，本颗
            // "代码接了而装载语句一次没发过"）。本家那句"整本没有行级前件"由它<em>不被推翻</em>：那一格量的是
            // 厂商行点没点名，而这一位的前件住在反射口里，行 907 对它一个字都没有说过。
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "无活库：装载通道已接线（血统那颗反射口），COPY … FROM STDIN 那一句一次都没发出去过"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第十一本台账（v0.36 第六笔，GBase 8c 叶子到场）。PG 血统的第五本，"一本 provider 一份"的第七次复用，
        // 而它在这一族的形状里补的是一格新档：<b>前件的来路</b>。金仓本那颗 SERIAL vs IDENTITY 是本行点名的，
        // openGauss／GaussDB 两本挂的 9.2 内核代数也是本行自证的（一跳：行→代数），瀚高本是唯一一本整本无前件
        // （行里连代数都没有，那一问对它不成立）——本册两样都不是：行 905 不答 IDENTITY，只写了一句
        // "与 openGauss 高度相似"，所以那一问要两跳才落到本家头上（本行→邻家行→内核代数）。同一句
        // "若这一家认得血统今天发的形状则文本同形"，第一次是替邻家问的。七颗 OfflineText 全部落在
        // Gbase8cDialectTests 的逐字相等断言上，其中两两互读那一格把血统五叶的 C(5,2)=10 条边补满（一册四条）。
        new("gbase8c", static () => Gbase8c.Provider(SqlServer.NoDriverFactory.Instance), 15, 7, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "Gbase8cDialectTests 那条 LIMIT/OFFSET 尾巴与只写 OFFSET 不补哨兵、与金仓家逐字相等的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.Returning, Provenance.OfflineText, "Gbase8cDialectTests 键回读那条句尾 RETURNING 与金仓家逐字相等的断言（血统两两互读补满的十条边各含此形）"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "Gbase8cDialectTests 读侧那条双引号标识符与金仓家逐字相等的断言"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "Gbase8cDialectTests DDL 段那条显式模式建表与金仓家逐字相等的断言（默认 public 写进限定由本册绝对形状钉）"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "Gbase8cDialectTests DDL 段那条 COMMENT ON COLUMN 与金仓家逐字相等的断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "同上：这一对在金仓家是两句串联，本家共用一颗见证（血统同形，槽位不同源）"),
            new(DbCapabilities.Guid_Raw16, Provenance.OfflineText, "Gbase8cDialectTests 那条 UUID 断言（金仓那两条从此有了第四个复件；\"16 字节存储\"那一半五家一起欠着）"),

            // 下面七位与金仓本、openGauss 本、GaussDB 本、瀚高本同一判据、同一批理由：这一档的掩码是从 §4
            // 那张表攒来的乐观声明，GBase 8c 继承的是同一份，本家一位不翻案、不删减（收敛是 §4 的账）。
            new(DbCapabilities.Upsert_OnConflict, Provenance.VendorDoc, "位无读者：写侧的一句 upsert 档从本笔起按 IBulkStrategy.UpsertClause 那颗槽分派（槽在血统基类已接，本家一台活库都没有——位与槽是两面，见 MySqlProviderBase 类注释 MultiStatement 那一条），而这一位今天仍然没有消费者"),
            new(DbCapabilities.Cte, Provenance.VendorDoc, "无消费方：共用层没有 WITH 的发射口"),
            new(DbCapabilities.WindowFunctions, Provenance.VendorDoc, "消费方=QuickFx.RowNumber（行号）：文本由共用层 DialectBase.EmitRowNumber 出、十八家零登记模板，而本家这一格还没有活库对账"),
            new(DbCapabilities.Count_Over_Window, Provenance.VendorDoc, "无消费方：CountMethod.CountOverWindow 那一档今天在 QueryEngine 里还是那句未接入的抛（窗口函数那一格有行号了，这一格没有）"),
            new(DbCapabilities.Boolean_Type, Provenance.VendorDoc, "类型表 bool→BOOLEAN，谓词侧 IS TRUE 有对拍见证但那是文本档，类型列本体无样本"),
            new(DbCapabilities.TempTable, Provenance.VendorDoc, "无消费方：Tp 临时主键走的是真实表而不是临时表（§8.2）"),

            // 第 15 位是本笔挂上血统基类的 Bulk_Copy，与上面七颗同档而异因（那七颗"文档说有而代码没接"，本颗
            // "代码接了而装载语句一次没发过"）。本家那颗"前件是转引来的"在这一位上第二次兑现：行 905 对驱动
            // 只说了"与 openGauss 高度相似"，所以"这台驱动里有没有导入器"要两跳才落到本家，而两跳之后没有活库替它答。
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "无活库：装载通道已接线（血统那颗反射口），COPY … FROM STDIN 那一句一次都没发出去过（本家那一问要经邻家那一行才问得出）"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第十三本台账（v0.37 第二笔，PostgreSQL 本尊叶到场）。PG 血统的第六本、"一本 provider 一份"的第八次复用，
        // 而它在这一族的位置与另五本不同向：它不是又一棵"照着血统发"的壳，它是血统那一句话所回指的那一家本身——
        // 于是文件头 v0.36 第二笔立下的"PG 血统没有本尊可回指"在本册作废（原句留在上面那一段，划不掉是因为
        // 它量的是当时在场的册数）。掩码照旧与另五本同源于一颗 FamilyCapabilities（本笔起那一份是 15 位，多的
        // 那一位见本册末尾的 <c>Bulk_Copy</c> 行），七颗 OfflineText 全部落在
        // PostgreSqlDialectTests 的逐字相等断言上，而本册没有 golden/postgresql/：血统的基线早在 v0.22 就是
        // golden/kingbase/ 那 18 份，本尊到场不改变"同形交给对拍"这条处置（对照：MySQL 本尊有 golden/mysql/，
        // 因为它是那一族的第一份产物，而这一族的第一份产物是金仓）。
        // 本册那一条只属于本尊的前件在<b>版本</b>而不是血统：血统的文本层里有两颗版本钉死的格子指向相反方向
        // （<c>IdentityClause</c> 要 PG 10+【文档】，<c>ROUND/CEIL/FLOOR</c> 那三颗 <c>CAST(… AS numeric)</c>
        // 按"最老的内核"写而那是 14 之前的读法【文档】）。五叶时两格各由别家兜住、无人需要同时满足；本尊到场后
        // 同一台库要同时吃下两句，于是三段区间（&lt;10 语法错 ∶ 10–13 恰合 ∶ ≥14 冗余）第一次算得出来——
        // 它是本仓库自己算的【推断】，而那一层下面 <c>PostgreSqlProvider</c> 类注释第三段与方言测试的版本格各钉一份。
        // 所以"零覆写的正典叶"在本家第一次带<em>代价</em>：正典叶对齐的是最老那一侧，而 MySQL 本尊对齐的是最新
        // 那一侧（8.0 的 <c>VALUES()</c> 读法），两颗本尊同为零覆写而选型方向相反。
        new("postgresql", static () => PostgreSql.Provider(SqlServer.NoDriverFactory.Instance), 15, 7, 1,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "PostgreSqlDialectTests 那条 LIMIT/OFFSET 尾巴与只写 OFFSET 不补哨兵、与金仓家逐字相等的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.Returning, Provenance.OfflineText, "PostgreSqlDialectTests 键回读那条句尾 RETURNING 与金仓家逐字相等的断言（血统两两互读补满的十五条边各含此形）"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "PostgreSqlDialectTests 读侧那条双引号标识符与金仓家逐字相等的断言（本尊与血统读的是同一颗 IdentifierPolicy.PostgreSQL）"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "PostgreSqlDialectTests DDL 段那条显式模式建表与金仓家逐字相等的断言（默认 public 写进限定由本册绝对形状钉）"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "PostgreSqlDialectTests DDL 段那条 COMMENT ON COLUMN 与金仓家逐字相等的断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "同上：这一对在金仓家是两句串联，本家共用一颗见证（血统同形，槽位不同源）"),
            new(DbCapabilities.Guid_Raw16, Provenance.OfflineText, "PostgreSqlDialectTests 那条 UUID 断言（第六家同一格主张；\"16 字节存储\"那一半六家一起欠着，而本家这一半比另五家都近——它就是那句话的出处）"),

            // 下面七位与金仓本及另四本 PG 血统台账同一判据、同一批理由：这一档的掩码是从 §4 那张表攒来的
            // 乐观声明，本尊继承的是同一份，一位不翻案、不删减（收敛是 §4 的账）。
            // 【本笔只改这一颗的措辞，且<em>只有本尊这一本</em>多一档读数：那一段句尾在本家授权实例上跑过
            // （集成册 <c>BulkWritePathTests</c> 的 upsert 那两条），而<em>位仍不翻 Live</em>——执行的是槽，
            // 位的判据是"有没有读者按这一位分派"，那一个读者今天还是没有。金仓本与另四本一字不动。】
            new(DbCapabilities.Upsert_OnConflict, Provenance.VendorDoc, "位无读者：写侧的一句 upsert 档从本笔起按 IBulkStrategy.UpsertClause 那颗槽分派（槽在本血统已接，ON CONFLICT … DO UPDATE SET … = EXCLUDED. 那一段句尾在本家授权实例上跑过——位与槽是两面，见 MySqlProviderBase 类注释 MultiStatement 那一条），而这一位今天仍然没有消费者"),
            new(DbCapabilities.Cte, Provenance.VendorDoc, "无消费方：共用层没有 WITH 的发射口"),
            new(DbCapabilities.WindowFunctions, Provenance.VendorDoc, "消费方=QuickFx.RowNumber（行号）：文本由共用层 DialectBase.EmitRowNumber 出、十八家零登记模板，而本家这一格还没有活库对账"),
            new(DbCapabilities.Count_Over_Window, Provenance.VendorDoc, "无消费方：CountMethod.CountOverWindow 那一档今天在 QueryEngine 里还是那句未接入的抛（窗口函数那一格有行号了，这一格没有）"),
            new(DbCapabilities.Boolean_Type, Provenance.VendorDoc, "类型表 bool→BOOLEAN，谓词侧 IS TRUE 有对拍见证但那是文本档，类型列本体无样本"),
            new(DbCapabilities.TempTable, Provenance.VendorDoc, "无消费方：Tp 临时主键走的是真实表而不是临时表（§8.2）"),

            // 第 15 位是本笔挂上血统基类的 Bulk_Copy，与上面七颗同档而异因（那七颗"文档说有而代码没接"，本颗
            // "代码接了而装载语句一次没发过"）。本尊在这一位上比另五本多一档读数：驱动侧那颗导入器与它的四个
            // 方法今天在一颗一次性探针里对着厂商真包量过签名，而仓库自己的驱动解析路径一次都没跑过——落档不变，
            // 落档的理由从"驱动侧不知道有什么"换成了"活库侧没证"。
            //
            // <b>【v0.40 批量写侧那一轮 —— 原文照抄在前（上面那段），改判在后】上面那句"活库侧没证"与那句
            // "COPY … FROM STDIN 那一句一次都没发出去过"今天两句一起作废，而<em>只有本尊这一本作废</em>：
            // 这台 PG 实例是 v0.37 第九笔带来的，本笔把集成册整本在它身上跑过，钉住这一位的是
            // <c>BulkPathTests.A_requested_copy_channel_lands_the_whole_batch_and_reports_the_layer</c>
            // （交回 <c>report.Method == CopyChannel</c>，另配 <c>What_the_channel_writes_is_what_the_text_layer_writes</c>
            // 那句"通道写进去的与文本层写的是同一批行"，以及 <c>The_payload_handed_to_the_channel_is_the_kernels_normalized_one_and_in_row_order</c>
            // 那颗行序钉）【实测·obj/logs/v40-postgresql-integration-fix1.log，160 ∶ 0 红 ∶ 44 具名 skip，
            // 本册在本家只 skip 掉 MySQL 独有的那两条】。耗时那一格照 §13.4 只记账：2 000 行
            // copy_ms 32 ∶ l3_ms 106，另有一趟重跑读到的 31 ∶ 132【v40-postgresql-integration-full2.log 同一句 [bulk-timing]】。
            // 上面那句"驱动侧签名量过而仓库自己的驱动解析路径一次都没跑过"也跟着换档：本笔跑的是清单 →
            // 独立 AssemblyLoadContext → 工厂 → 反射装配那条链，厂商包 Npgsql 在本家是真解析成功的一支。
            // 五本里其余四本（opengauss／gaussdb／highgo／gbase8c）的理由一字不动——它们一台活库都没有。
            new(DbCapabilities.Bulk_Copy, Provenance.Live, "装载语句在本家真库发出去了：集成册 BulkPathTests 整本全绿，钉的是点名 CopyChannel 那一条交回 L1（另有通道与文本层同批行、行序两颗钉）【obj/logs/v40-postgresql-integration-fix1.log】；2 000 行 copy 32ms ∶ l3 106ms 只记账"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第三族：这一家<em>只声明今天真的产得出文本的那几位</em>（§4 那份"能力位是声明，
        // 但没消费者就不许顺手加"的口径在这里被执行得比金仓那一档更严）。差异本身记在 §4 与
        // OracleProviderBase 的类注释里，那两处的说法是"待与金仓档一起收敛"，不是"这一家少了"。
        new("dameng", static () => Dameng.Provider(SqlServer.NoDriverFactory.Instance), 7, 6, 0,
        [
            new(DbCapabilities.Paging_RowNum, Provenance.OfflineText, "golden/dameng/paging-order-skip-take.sql"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "golden/dameng/select-entity.sql"),

            // 金仓把带模式的那一句并进了 ddl-order，这一家不行：它的默认形状是"契约没写模式就什么都不写"
            // （那一格由 ddl-order 守着），所以"写了模式"是第二种形状而不是第一种的一个参数，单独成一份基线。
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/dameng/ddl-qualified.sql"),

            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "golden/dameng/ddl-alter-and-comment.sql"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "golden/dameng/ddl-alter-and-comment.sql"),
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "本文件那条 CHAR(36) 断言"),

            // 这一家<em>没有</em> VendorDoc 那一档的位（6 位全部有离线见证），而这不是运气：
            // OracleProviderBase 那份掩码是按"今天真产得出文本的位"逐个攒出来的，不是照 §4 那张表抄的。
            // 与金仓那 7 位的对照记在下面的交集用例与 §4 —— 那一档里躺着七位"文档说有、代码没接"的声明。
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // Oracle 血统的第二本台账（v0.36 第五笔，神通叶子到场）。它是这一族第一次"同掩码的两本"，
        // 而它与达梦本的关系与另四对同掩码邻居不同向：那四对掩码相同处产出也逐字相同，本对掩码相同而
        // CREATE TABLE 差恰好一句（缝 carry 的 IDENTITY 形）——"同掩码 ≠ 同形"第一次可由两本台账并排指认。
        // 六位的攒法与达梦本同一句原话（掩码是按"今天真产得出文本的位"攒的，不是照 §4 抄），VendorDoc 照旧 0 位；
        // 本册见证全部指 ShenTongDialectTests 的断言而不是文件名——这一族没有 golden/shentong/，同形交对拍
        // （leaf == 血统），跨叶那格异形由点名的替换用例量，不进恒等式对拍表。
        new("shentong", static () => ShenTong.Provider(SqlServer.NoDriverFactory.Instance), 7, 6, 0,
        [
            new(DbCapabilities.Paging_RowNum, Provenance.OfflineText, "ShenTongDialectTests 那条两层 ROWNUM 伪列的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "ShenTongDialectTests 那条双引号折大写 select 的断言"),

            // 与达梦、MySQL 同一判据：这一家的默认形状也是"契约没写模式就什么都不写"（那格由 ddl 同形用例守着），
            // 所以"写了模式"要单独一枚见证才量得出 Schemas 这一位。
            new(DbCapabilities.Schemas, Provenance.OfflineText, "ShenTongDialectTests 那条 \"APP\".\"QUICKSQLORDER\" 限定折大写的断言"),

            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "ShenTongDialectTests 那条 COMMENT ON COLUMN 同形断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "ShenTongDialectTests 那条 MODIFY (…) 同形断言"),

            // 与达梦同一型（CHAR(36)）、同一句判据（这一族没有原生 uuid），而本家是这条主张的第二颗叶子见证：
            // "Oracle 侧落 CHAR(36)"从一家一句变成两家一句，存得省不省那一半照旧两家一起欠。
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "ShenTongDialectTests 那条 CHAR(36) 断言"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // Oracle 血统的第三本台账（v0.37 第三笔，本尊叶到场）。它是这一族第一颗"本尊比它的派生更接近
        // 基类自己"的账——不，这一句在上一笔的 PG 族同样成立；本家独有的是<b>基类那份默认就是它自家的话</b>：
        // 神通不覆缝是因为行里没读数（不赌），本尊不覆缝是因为缝里那句本来就是 Oracle 12c 的标准形（无事可裁）。
        // 于是本册六位的攒法与前两本同一句原话，而【离线已钉】那一档的见证第一次可以指一颗真 provider 而不是
        // 探针：这一族的空壳基线此前由一颗借 DbKind.Oracle 的探针量（此前那十颗探针里唯一一颗借"有枚举值而
        // 无 provider"的库种的，本册添第十一颗即 <c>OracleLeafProbe</c>），本尊到场让影子有了本体，两者今天只剩
        // CLR 类型名与清单键两颗分得开的东西。
        // 本册没有 golden/oracle/：这一族的黄金基线是 golden/dameng/ 那 18 份，被派生叶占着——"哪一族的第一份
        // 产物是谁"这一格今天有第二个答案（PG 族是金仓，Oracle 族是达梦），而同形交给逐字对读这条处置不改。
        // 掩码 6 位、VendorDoc 0 位，与前两本同源于一颗 FamilyCapabilities；本笔一位不翻案、不添。
        new("oracle", static () => Oracle.Provider(SqlServer.NoDriverFactory.Instance), 7, 6, 0,
        [
            new(DbCapabilities.Paging_RowNum, Provenance.OfflineText, "OracleDialectTests 那条两层 ROWNUM 伪列与血统探针逐字相等的断言（同形交逐字对读，故无基线）"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "OracleDialectTests 那条双引号折大写 select 与血统探针逐字相等的断言（本尊与血统读的是同一颗 IdentifierPolicy.OracleFamily）"),

            // 与达梦、神通同一判据：这一族的默认形状也是"契约没写模式就什么都不写"，而本家那一格比另两颗多一层
            // ——那一条"用户即模式"在文档上说的是<em>本尊</em>的规则，另两家是复述它。
            new(DbCapabilities.Schemas, Provenance.OfflineText, "OracleDialectTests 那条 \"APP\".\"QUICKSQLORDER\" 限定折大写与血统探针逐字相等的断言"),

            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "OracleDialectTests 那条 COMMENT ON COLUMN 同形断言（上一笔刚被扣掉的那一位，本家原样交回——它是交集第十次复点没再小一格的原因）"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "OracleDialectTests 那条 MODIFY (…) 同形断言"),

            // 与前两本同一型（CHAR(36)）、同一句判据（这一族没有原生 uuid），而本家的这一票是<em>最直的一票</em>：
            // 前两本说的是"复述血统的那一家没有"，本本说的是"那一族自己"没有——"16 字节存储往返"那一半照旧欠着，
            // 而本家连"库里有 BINARY(16) 那一档"都没有读数可借。
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "OracleDialectTests 那条 CHAR(36) 断言（同族第三本同一格主张）"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第四族：掩码最少的一档（5 位），而 VendorDoc 那一档同样是 0 位 —— 与第三家同一判据攒出来的，
        // 不是照 §4 那张表抄的。库里"有"而代码没接的那几位（Upsert_OnDuplicate、Cte、WindowFunctions、
        // Json_Type、MultiStatement…）全部留在 MySqlProviderBase 的"刻意不声明"那一段里，不进这本账：
        // <b>台账记的是声明位，不记可指认而未指认的库存量</b>，这一格与金仓那七位是同一件事的两半。
        // <b>【本笔改判】上面第一句那两颗数一起动：位数 5 → 6（<c>Bulk_Copy</c> 随血统基类挂上），
        // "VendorDoc 那一档同样是 0 位"跟着作废——本家此刻与 sqlite 那本各带 1 位、同数而异因。
        // "最少"那一格也不再是本家，它由 gbase8s 那本 4 位独占。本家的六颗里五颗按"代码今天真产得出文本"攒，
        // 第六颗按"代码今天真有一段反射装配"攒——这本账第一次有一位<em>不是文本</em>，所以它没有基线可指。
        // 【v0.40 批量写侧那一轮 —— 原文照抄在前（上面整段），改判在后】上面那句「本家此刻与 sqlite 那本各带 1 位、
        // 同数而异因」里的"各带 1 位"在本家这一侧今天换成 0：那 1 位（<c>Bulk_Copy</c>）走上 <c>Live</c> 了，
        // 于是 <c>VendorDocOnly</c> = 6 − 5 − 1 = 0，那句最早的原话「VendorDoc 那一档同样是 0 位」在位数涨过一格
        // 之后重新成立，而它成立的原因整颗换掉：原来那句说的是"这六位里没有一位是文档乐观声明"，今天这句说的是
        // "这一档空了，因为那位既不是文档也不是文本——它在真库上跑过"。本家的 <c>Live</c> 从此不再是 0，
        // 第一颗的逐条出处写在那一行的注释与见证串上。
        // 【同段的 sqlite 那一半照旧】sqlite 那本带的 1 位是 <c>MultiStatement</c>，它本来就是 Live 档的 DDL 见证，
        // 不随本笔动。"各带 1 位、同数而异因"这句的两侧数字从今天起不再相等：1 ∶ 0。
        // </b>
        new("mysql", static () => MySql.Provider(SqlServer.NoDriverFactory.Instance), 7, 5, 1,
        [
            // 这一位没有本家基线：它的产出与金仓那一族只差引号字形（同一套尾巴），按 §13.3 那句"同形交给对拍"。
            // 所以见证是 MySqlDialectTests 里那句 LIMIT/OFFSET 断言，而不是一个文件名 —— 台账接受这两种形状。
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "MySqlDialectTests 那条两个尾巴子句的断言（同形交对拍，故无基线）"),

            // 与达梦同判：这一家的默认形状也是"契约没写模式就什么都不写"（ddl-order 守那一格），
            // 所以"写了模式"要单独成一份基线才量得出 Schemas 这一位。
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/mysql/ddl-qualified.sql"),

            // 这两位在另一家是两句独立语句，在这里是同一句 MODIFY COLUMN 的两个槽位，所以共用一份基线
            // （金仓那一家也是同一份，理由不同：那一家是那一句话就把两半都说了）。
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "golden/mysql/ddl-alter-and-comment.sql"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "golden/mysql/ddl-alter-and-comment.sql"),

            // 与达梦同一型（CHAR(36)），而<em>不</em>是同一判断：那一家是"没有原生 uuid 这一档"，
            // 这一家有 BINARY(16) 而它要连接串参数与字节置换一致才成立 —— SPI 里没有那一格（§14）。
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "本文件那条 CHAR(36) 断言"),

            // 第 6 位是本笔挂上血统基类的 Bulk_Copy，也是这本账第一颗<em>不是 SQL 文本</em>的位：可指的产出是
            // <c>MySqlBulk.ResolveCopyChannel</c> 那条反射装配。它落在"仅厂商文档"而不是 Live，差的那一格判据
            // 写得很窄也要写下来：Live 要的是"由<em>本 provider</em> 产出的那一条语句被那台库执行过"，而对照量具
            // m1∶m2 那两场量的是驱动 API 本身（放行本地装载后 24 格全绿、不放行时驱动当场拒），provider 那一侧
            // 还没在集成册发过一句。翻档的那天它带着日志名往上走一档，与虚谷那四位同一判据。
            //
            // <b>【v0.40 批量写侧那一轮 —— 原文照抄在前（上面整段），改判在后】那一天到了，那一档也走上去了</b>：
            // 集成册在本家跑了 <c>BulkPathTests</c> 整本，钉住这一位的是
            // <c>The_same_batch_lands_on_l1_once_the_connection_string_allows_local_infile</c> 那一条：同一个请求在
            // <c>FallbackPolicy.Never</c> 下交回 <c>report.Method == CopyChannel</c> ∶ <c>Degraded == false</c> ∶
            // <c>BulkDegraded</c> 留痕为空 ∶ 库里真有那五行 —— 这四句一起成立才排掉"退到 L3 而只是没报错"那种假绿，
            // 而退到 L3 在 Never 下是当场抛。<b>另外三句各钉一角</b>：
            // <c>The_copy_channel_rides_the_transaction_and_rolls_back_with_it</c> 钉"通道走的是事务那条连接"
            // （未提交的行在同一条连接上读得到，通道若从工厂另借一条就是 0），
            // 三趟复跑的耗时【实测·obj/logs/v40-my-bulk-repeat-{1,2,3}.log：2 000 行 copy_ms 67 ∶ 68 ∶ 58
            // 对 l3_ms 108 ∶ 105 ∶ 107】只记账——照 §13.4 那句"不要把一次现场读数钉成断言"不钉数；
            // 钉住的是"这一家由本 provider 产出的那一句装载语句被那台 8.4.8 执行过"
            // 【实测·obj/logs/v40-mysql-integration-fix1.log】。上面那句"翻档的那天它带着日志名往上走一档"因此兑现，
            // 而它的"与虚谷那四位同一判据"这一半也一并兑现：本家这一位现在与那四位走的是同一格判据。
            new(DbCapabilities.Bulk_Copy, Provenance.Live, "集成册 BulkPathTests 在本家全绿（obj/logs/v40-mysql-integration-fix1.log）；钉的是那条 Never 档交回 CopyChannel ∶ 不降级 ∶ 无留痕 ∶ 库里有五行那一条，另有同一事务可见性一条与三趟复跑耗时【v40-my-bulk-repeat-{1,2,3}.log】"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // MySQL 血统的第二本台账（v0.35 第三笔，TiDB 叶子到场）。它是这本账的第一例<b>逐位同掩码的两份</b>：
        // 五位与上面 mysql 那一份同名同形，而<em>见证各是各的</em>——这里每行的"逐字相等"是三段式
        // （tidb == mysql 叶 == 血统基类），所以它的可迁移性依赖上面那本 mysql 的见证仍然成立；
        // 两份都记，是因为 §13.5 那句"每个声明位都要有一个见证"量的是 provider，不是血统。
        // 这一本没有 golden/tidb/ 文件可指（同形交对拍走到极端：连引号字形都不差），
        // 五颗<em>文本</em>见证全部落在 TiDbDialectTests 的逐字相等断言上——台账接受这种形状，先例就是上面
        // mysql 自己那本的第一行（"同形交对拍，故无基线"）。
        new("tidb", static () => TiDb.Provider(SqlServer.NoDriverFactory.Instance), 7, 5, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "TiDbDialectTests 那条尾巴两子句与 2^64−1 哨兵和 mysql 家逐字相等的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "TiDbDialectTests DDL 段那条带模式建表限定不折叠、与 mysql 家逐字相等的断言"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "TiDbDialectTests DDL 段那条 MODIFY COLUMN 带注释与 mysql 家逐字相等的断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "同上：这一对在这一家是同一句的两个槽位，共用一颗见证（与 mysql 本家那两行的形状相同）"),
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "TiDbDialectTests 那条 CHAR(36) 断言"),

            // 第 6 位随血统基类继承，本家一个字都不改写——而"继承来的位"在台账里交的是<em>本家自己的</em>出处：
            // 上面那五位继承时至少还有 mysql 本家那份文本可指，这一位连那一半都没有（本家的 LOAD DATA 侧
            // 连驱动解析都没跑过，比邻本少一档读数）。它因此与 tidb 那五颗同档而更虚：五颗是"文本钉住、库没证"，
            // 这一颗是"文本都没有、只有那段反射装配在场"。
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "随血统继承同一颗反射口（MySqlBulk.ResolveCopyChannel），本家无驱动解析读数：装载那一路连一次都没跑过"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // MySQL 血统的第三本台账（v0.35 第四笔，OceanBase 的 MySQL 模叶子到场）。它是"一本 provider 一份"
        // 这条判据的第二次复用：五位掩码与 mysql／tidb 两本逐位相同，见证仍然各记各的——三段式在这里长成四段式
        // （oceanbase == tidb == mysql 叶 == 血统基类；两颗分叉叶子的互读是 OceanBaseDialectTests 本笔新增的那条，
        // 此前"分叉库之间同形"只是逻辑必然、没有见证）。
        // 这一家比 tidb 那本多一层<em>表达不出来</em>的前件："模式在连接上决定"（§9.1），本本的每一颗
        // OfflineText 都读作"若连接在 MySQL 模则文本同形"——SPI 没有那一格（§14 的参数名单，第二个实例），
        // 所以这一格前件只能记在注释里，这本账因此也是六本里唯一一本每一行都自带悬条件的。
        new("oceanbase", static () => OceanBase.Provider(SqlServer.NoDriverFactory.Instance), 7, 5, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "OceanBaseDialectTests 那条尾巴两子句与 2^64−1 哨兵和 mysql 家逐字相等的断言（同形交对拍，故无基线）"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "OceanBaseDialectTests DDL 段那条带模式建表限定不折叠、与 mysql 家逐字相等的断言"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "OceanBaseDialectTests DDL 段那条 MODIFY COLUMN 带注释与 mysql 家逐字相等的断言"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "同上：这一对在这一家同样是同一句的两个槽位，共用一颗见证（与 mysql／tidb 两本的同格同形）"),
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "OceanBaseDialectTests 那条 CHAR(36) 断言"),

            // 第 6 位同样是随血统继承而不改写，而本家在这一位上比 tidb 本多压一层自家独有的悬条件：这一本
            // 每一行都读作"若连接在 MySQL 模则…"，装载那一路因此要先过"模式在连接上决定"那道闸，
            // 而它今天连被问的资格都还没排到（本家一台实例都没有）。
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "随血统继承同一颗反射口（MySqlBulk.ResolveCopyChannel），本家无驱动解析读数：装载那一路在 MySQL 模下成不成立没人证过"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第五族：7 位、VendorDoc 那一档仍然是 0，与前两家同一把尺（"代码今天真的走到那一条语句"）。
        // 它把这本账的形状从"一份大的带七位乐观声明、三份小的零位"读成了<b>三份零位 ∶ 一份七位</b>：
        // 后接的四家全都没有"文档说有而代码没接"的那一档，而金仓那七位是 §4 收敛要处理的对象，不是新家的模板。
        // 这一家与第三家（达梦）在位<em>名</em>上只差两处：多一位 Returning、分页那一位换了个名字
        // （Paging_RowNum 对 Paging_LimitOffset）。前者是"两家骨架同形却声明相反"的第一例，
        // 所以它的见证必须逐位写清楚，不能让"两家的骨架文本一致"这件事顺带替它说话。
        //
        // <b>v0.32 第二格：这一段末尾那句"三份零位 ∶ 一份七位"从今天起读作"三份零位 ∶ 一份七位（其中四位 Live）"</b>。
        // 翻的这四颗不是"这一家接了活库"这一句整体 —— 是逐位各自有一条<b>由本 provider 产出</b>的语句在那台
        // <c>XuguDB 12.0.0</c> 上被执行过：见证是 <c>obj/logs/v32-recon-golden-6.log</c>（裸连）与
        // <c>-utf8.log</c> 那两场的 G-10 逐句段，六份基线合 10 颗语句、8 通 2 红（红的两颗同属下面 <c>Schemas</c>
        // 那一位，所以它没翻）。<b>翻格时原文一句没删</b>：那四行的 <c>OfflineText</c> 措辞与它们各自的理由仍然成立，
        // 只是它们现在不再是这一位的最高档 —— 与 §13.5 那本活库见证账同一取向，一档一档往上走，不跳。
        new("xugu", static () => Xugu.Provider(SqlServer.NoDriverFactory.Instance), 8, 3, 4,
        [
            // 尾巴两子句，与金仓、MySQL 同边，而"只有 OFFSET"那一档它补的是<em>厂商写死的天花板</em>
            // （2147483647），不是 MySQL 那颗 2^64−1 —— 所以这一位有自己的一份基线，不是对拍代钉。
            // 【v0.32 第二格 → Live】该基线 1/1 句在活库上执行通过（G-10），且集成层那 47 条里
            // <c>ToPagedAsync</c> 与"只有 OFFSET"那一支发的也是这两子句；无 <c>ORDER BY</c> 的分页形另有一格
            // 见证（S-10，见 <c>XuguDialect</c> 类注释第一条）。
            new(DbCapabilities.Paging_LimitOffset, Provenance.Live, "golden/xugu/paging-skip-only.sql"),

            // 唯一一位"达梦没有而这一家有"的：句尾 RETURNING。两份 shipped 产物里只有逐条那一条走过它，
            // 批量那一形没有任何产物用过（§14 那一格），所以见证是"文本钉住了"而不是"库里接得住"。
            // 【v0.32 第二格：仍然 OfflineText，而理由换了一半】"库里接得住"这一问今天<em>还是</em>没有本族的
            // 见证 —— 不是没人试过，是那条路今天根本没有出口：键回填那一整档缺 SPI 槽位，集成层里 46 条按名
            // skip（§13.4 那份 skip 台账第一类）。探针自己发的 <c>RETURNING</c> 那一形在这台库上收（K 段），
            // 但它证的是解析器，不是 <c>XuguKeys.cs</c> 那一句，所以这一位不翻。
            new(DbCapabilities.Returning, Provenance.OfflineText, "XuguDialectTests 那条 trailing-returning 断言（无基线：与金仓同形交对拍）"),

            // 【v0.32 第二格：这一位是本文件唯一一颗"配对跑过而跑败"的】它那份基线是六份里红的那一份 ——
            // <c>"APP"."QUICKSQLORDER"</c> 报 <c>E4002</c>（模式不存在）紧随 <c>E5021</c>，而 §13.17 那句授权
            // 明写"不建库、不建模式"，所以本版不修它、也不替它找一份能过的基线。
            // 于是这一位的 <c>OfflineText</c> 有了一种新的读法：它不再是"没跑过"，而是"跑过，败因在授权那一侧、
            // 不在文本那一侧"。这一区别要留在台账里，否则下一版会去改那句 DDL 让它"能过"，而那是绕过授权。
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/xugu/ddl-qualified.sql"),

            // 【v0.32 第二格 → Live】该基线 3/3 句在活库上执行通过（建列、改精度、打注释各一颗），
            // 且那句中文注释在带 <c>;CHARSET=UTF8</c> 的那一场里逐字读回（N-06b 的 <c>SAME=1</c>，
            // <c>obj/logs/v32-recon-cjk-utf8-3.log</c>）—— 这一位因此同时有"语句被接受"与"内容原样回来"两半。
            new(DbCapabilities.Comment_On_Column, Provenance.Live, "golden/xugu/ddl-alter-and-comment.sql"),

            // 【v0.32 第二格 → Live】同一份基线里那一颗改精度的语句执行通过，且改完之后目录里读回的
            // <c>SCALE</c> 就是新值（G-07 那一段：数值列 <c>SCALE=1179652</c> 是那颗被打包过的读数，
            // 时刻两列 <c>SCALE=3</c>）—— 上面那句"改列 = 整列重写，契约里没写的格会被库抹掉"在这一台
            // 今天只证到"没抹掉注释与可空性"，其余格子仍然欠着（§14）。
            new(DbCapabilities.Alter_Column_Precision, Provenance.Live, "golden/xugu/ddl-alter-and-comment.sql"),

            // 与达梦同一型（CHAR(36)）而<em>不</em>同因：那一家没有原生 uuid，这一家有没有【无出处】，
            // 而两种情形今天都不足以把 Guid_Raw16 声明出来（后者还要驱动侧的字节置换与连接串参数一致）。
            // 【v0.32 第二格：仍然 OfflineText】这一位的两半今天各差一截：建表那半（<c>CHAR(36)</c> 被接受）
            // 在集成层的建表里顺带跑过，但那不是<em>为这一位立的</em>断言；值那一半（<c>Guid</c> 进出往返）
            // 落在那 46 颗 skip 的射程里，一个字节都没回来过。一颗都不翻，等 §14 那句"值归库给"有裁决。
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "XuguDialectTests 那条 CHAR(36) 断言（第三、四、五家同一格三个理由）"),

            // 引号照加 + 解析器判等区分大小写：这一家的折叠是"建出来即大写"，所以这一位量的是
            // "带引号的名字按字面处理"那一条纪律，与达梦同判据、不同出处（文档对产物）。
            // 【v0.32 第二格 → Live】该基线 2/2 句执行通过，且目录里读回的是<b>折叠后的大写</b>
            // （K-07：不带引号建的 <c>QuickSqlRecon33</c> 在目录里是 <c>"QUICKSQLRECON33"</c>），
            // 而模式名不折成大写就直接查不到（M2-07 交回 0 行，同一句换成 <c>SYSDBA</c> 交回 19 行）。
            // <b>这一位翻得心虚，而且要把这句心虚留在台账里</b>：这台实例的 <c>COMPATIBLE_MODE</c> 问不出
            // （五颗候选读法逐颗发过，见 <c>XuguSchemaReader</c> 类注释那条【实测·不可问】），
            // 所以"建出来即大写"在这一台是实测，在另一台 POSTGRESQL 兼容档的实例上仍然是推断。
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.Live, "golden/xugu/ddl-order.sql"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第十二本台账（v0.37 第一笔，GBase 8s 叶子到场，Informix 血统的第一颗）。它是这本账第一次<b>变小</b>：
        // 前十一本无论互相差多少，交出来的始终是那三位，而本家相对另五族缺两颗粒外能力
        // （<c>Comment_On_Column</c> 与那颗报不出的分页位），其中<b>只有一颗落在交集里</b>——分页那一位此前
        // 就因为各家用不同名字而从来不在交集中，所以交集掉的是 <c>Comment_On_Column</c> 这一颗：
        // 那一位此前十一本每一本都有，第一次有一家在它面前交回空（这一家的语句清单里没有 <c>COMMENT ON</c>，
        // 列注释走 Core 的"跳过并 WARN"退路）。
        // 于是文件头那段预测（"哪天某一家接了只有它有的一种能力，交集会当场从三位掉到两位"）今天兑现了，
        // 而兑现的方向是反的：红的原因不是"新来的多了一颗别人没有的"，是"新来的少了别人都有的那颗"。
        // 多一颗时交集链只要邻家没接就照旧不动，少一颗则当场扣掉一位——<b>前者涨的是名单，后者削的是地基</b>，
        // 而这份台账此前十一本只演示过前一种。
        // 本册另外两处形状：它是第一本"整本没有一颗分叉位"的（四颗全是别人有过的位，没有一位是新名），
        // 也是第一本"有分页产出而分页声明位为零"的（<c>SELECT FIRST n SKIP m</c> 每发一句都报不出，
        // 理由在 <c>Gbase8sProvider</c> 类注释第四段，不在这里重述）。4/4/0 三颗计数里那颗 0 是实况：
        // 没有驱动包也没有授权实例，本家的每一行读到今天都是"文本钉住了"，没有一行是"库里接得住"。
        new("gbase8s", static () => Gbase8s.Provider(SqlServer.NoDriverFactory.Instance), 5, 4, 0,
        [
            // 这一位在本家读作"所有者"而不是"模式"（<c>Gbase8sSchemaReader</c> 那道 <c>t.owner</c> 闸），
            // 而发射口是同一个，所以见证仍然是一份基线；限定名不折叠那半句写在本家方言测试的 DDL 段。
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/gbase8s/ddl-qualified.sql"),

            // 与 <c>Comment_On_Column</c> 那颗粒外（本家没有）在另一族是同一句 <c>ALTER TABLE</c> 的两个槽位，
            // 在本家只剩一颗：那一家的语句清单里没有 <c>COMMENT ON</c> 那一行，列注释走 Core 的跳过并 WARN 退路。
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "golden/gbase8s/ddl-alter.sql"),

            // 引号照加而别名不带 <c>AS</c>：这一位量的是"判等区分大小写"那条纪律，不是文本形——
            // 本家的 <c>IdentifierPolicy</c> 与金仓那颗按值相等而产出差一句，见下面 <c>CaseSensitive</c> 那一段。
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "golden/gbase8s/select-entity.sql"),

            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "Gbase8sDialectTests 那条 CHAR(36) 断言（本家无原生 uuid 型也无取号函数，档位【文档·IBM】）"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第十五本台账（v0.37 第四笔，SQLite 叶子到场，第七家）。它是这本账第一次<b>两家连档</b>：
        // xugu 那一份 7/3/4 之后十三本全是 x/全余/0，本家把第三档重新顶成非零，而且带上来一种新形状——
        // <b>它的三颗 Live 没有一颗出自手写 recon 脚本</b>。xugu 那四位的执行见证逐颗是
        // <c>obj/logs/v32-recon-golden-*.log</c> 的 G-10 逐句段（人把基线文本敲给那台库听），本家这三颗的
        // 见证是 <c>obj/logs/v37-sqlite-int-4.log</c>：那台库收到的是本仓库的 <c>QueryEngine</c> 与
        // <c>InsertEngine</c> 自己拼出来的语句，一次手写文本都没参与。这正是 <c>SqliteProvider</c> 类注释
        // 末段那句"第三档还没升到跑过本仓库的 ORM"今天改口的内容，也是这本账立项时给 Live 下的那条判据
        // （"由本 provider 产出的那一条语句被那台库执行过"）第一次<em>只靠</em>产出侧兑现。
        //
        // 第二格新消息在本册的第三颗计数上：<b>VendorDocOnly = 1 第一次出现在 PG 血统之外</b>。此前十四本里
        // 非零的只有 PG 那六本、每本七颗，而那七颗的口径是"§9.1 那一行说库里有，而 src/ 没接到"——
        // 它们脚下没有一台活库。本家那一颗脚下<em>有</em>一台，而且它量的那一格当场量到过
        // （一颗 100 写进去下一号是 101【实测·54 ∶ 55】，31 亿那颗同判【实测·310/310b】）：它不翻档的原因
        // 不是"没证"，是<b>这一位在 src/ 里连一句可发的文本都没有</b>。于是这本账第一次有一行读作
        // "行为在活库上量过，而那条语句发不出"，与 <c>MultiResultSets</c> 那一格（能测到、无发射口，
        // 差别是本家连位都不声明）成对。
        //
        // 第三格是本册落笔之后当场翻出来的，而它翻的是本册自己的一句错话。<b>它也是这本账第一颗 Live 落在
        // DDL 那一侧的位</b>：建表句由 <c>CodeFirst</c> 产出、由集成夹具发送，此前十四本的 Live 行全部住在
        // 读侧与写侧的 DML（虚谷那四位里最接近 DDL 的一颗也只是"基线文本被手敲进库"）。它的来路不是断言，
        // 是<em>事后翻库</em>：<c>node -e</c> 读临时目录里那颗 <c>quicksql-sqlite-integration.db</c> 的
        // <c>sqlite_master</c>，11 张表加一颗 <c>IX_QuickSqlOrder_CreatedAt</c> 全在，而那颗索引只可能来自
        // <c>CREATE TABLE … ; CREATE INDEX …</c> 这一条命令文本（夹具那句 <c>Execute</c> 是一次
        // <c>ExecuteNonQuery</c>，全仓库没有任何别的路径建它）。上一版据此写下的"CodeFirst 的产物没有执行口"
        // 因此作废，四处同源措辞一起改口，判据不变：<b>发过就是发过，不管它是夹具发的还是用例发的</b>。
        new("sqlite", static () => Sqlite.Provider(SqlServer.NoDriverFactory.Instance), 6, 1, 3,
        [
            // 三派哨兵里的第三派（<c>LIMIT -1</c> 是"不设上限"的记号，不是厂商上限数），所以这一位在本家
            // 有两份基线而不是对拍代钉。两颗今天都在真库上被 ORM 发过：见 <c>ReadPathTests</c> 那条
            // <c>Paged_query_pays_for_the_total_only_from_the_second_page</c>，它一次跑同过两形
            // （第 2、3 页走 <c> paging-limit-offset</c> 那一形，第 1 页只有 OFFSET 走 <c>LIMIT -1</c> 那一形）。
            // 【实测·A3 ∶ 32 ∶ 315/315b/408 是探针侧的同判，本行不靠它】
            new(DbCapabilities.Paging_LimitOffset, Provenance.Live, "golden/sqlite/paging-limit-offset.sql"),

            // 这一位是 xugu 那本唯一一颗"想翻而翻不动"的（键回填整档在那台库上没有出口，46 条按名 skip）。
            // 本家它有对象：<c>InsertEngine</c> 走的是批量 <c>RETURNING</c> 而句尾那一形今天真的发了出去——
            // 见证是 <c>WritePathTests</c> 那条 600 行 ∶ 3 块的批量回填（<c>ExecuteReturnKeysAsync</c> 交回
            // 600 颗主键且逐行按 payload 对得上），所以这一位不但翻 Live，而且翻的是"跨块映射正确"这一档
            // 而不是"解析器认得这个词"。【实测·105 ∶ 106 ∶ 404 ∶ 1319 ∶ 1013 是探针侧的同判】
            new(DbCapabilities.Returning, Provenance.Live, "golden/sqlite/insert-returning.sql"),

            // 七家今天都声明这一位，而本家那一颗指的是<em>挂进来的另一个文件</em>：<c>CREATE SCHEMA</c>
            // 发不出【实测·2824】，<c>main.</c> ∶ <c>side.</c> 两侧确实都限得住【实测·3000/3001 ∶ 3005 ∶ 3006】。
            // 不翻档的理由与上一版那句<em>不是</em>同一句：本仓库<em>有</em>产出并发送 DDL 的路径（见下面
            // <c>MultiStatement</c> 那一行），但那条路径建的是不带限定的表——夹具那 11 张全落在 <c>main</c>，
            // 因为契约里没有模式，而带模式的那一份基线只被方言册读过、探针侧那两句是手写的。
            // <b>这一位是交集里最后剩下的那一颗，而它跨家不同义</b>（GBase 8s 那一颗是"所有者"）：
            // "交集里还有这一位"到本笔起不等于"跨族建表可以按这一位发"。
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/sqlite/ddl-qualified.sql"),

            // 上一段说的那颗。位与 <c>IKeyStrategy.SupportsExplicitIdentityInsert</c> 是同一条事实的两种写法，
            // 而那条事实在本家当场量到过；<c>InsertEngine.cs:209</c> 与 <c>UpdateEngine.cs:104</c> 两处置口都指向
            // <c>IdentityInsertToggle</c> 那颗 SPI 槽位（未接），没有任何一处读这颗位。
            // <b>它与金仓那七颗 VendorDoc 同档而异因</b>：那七颗是"文档说有而代码没接"，本颗是"代码接得上、
            // 只是没人问这一位"——所以它下面那条 <c>insert-with-explicit-key.sql</c> 基线量的也不是这一位
            // （那句是"主键由调用方自带"的普通 INSERT，没有 toggle 子句）。
            new(DbCapabilities.Identity_ExplicitInsert, Provenance.VendorDoc, "无消费方：显式写自增列那一档在 Core 里直接抛（InsertEngine/UpdateEngine 两处置口指着 IdentityInsertToggle）"),

            // 七家里的第二份声明（第一份是 <c>SqlServerProvider.cs:43</c>），而"第一份背后站着活库读数"那一句由本行从今天起改到<em>本家</em>名下
            // 发它的是集成夹具那句 <c>Execute</c>（<c>Database.cs:812</c>，一次 <c>ExecuteNonQuery</c> 把整串交出去），
            // 产它的是 <c>SqliteCodeFirst.EmitCreateTable</c>；<b>本行因此是这本账第一颗 Live 落在 DDL 侧的位</b>——
            // 此前十四本的 Live 行全部住在读侧与写侧的 DML 里，而最接近 DDL 的那一颗（虚谷）也只是"基线文本被手敲进库"。
            // 见证不是日志而是<em>事后翻库</em>：那颗 <c>IX_QuickSqlOrder_CreatedAt</c> 在场，而全仓库只有这一条路径会建它。
            // 探针侧那四颗读数（2820 ∶ 2822 ∶ 2912/2913 ∶ 2908/2910）量的是"这样一串那台库收不收"，句子是手写的，
            // 按 <c>Ledger</c> 那条判据不构成 Live —— 本行翻档不靠它们。留下的判据是一句宽带：<b>发过就是发过，
            // 不管它是夹具发的还是用例发的</b>；上一版那句"产物没有执行口"由它作废，四处同源措辞一起改口。
            new(DbCapabilities.MultiStatement, Provenance.Live, "golden/sqlite/ddl-order.sql"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // PG 血统的第七本台账（v0.39 第一笔，DuckDB 叶子到场）。"一本 provider 一份"的第十次复用，
        // 而这一本在两格上都不是复点册：① 它是 PG 血统第一颗<b>真的改了血统产出</b>的叶子——此前六本
        // （金仓/openGauss/GaussDB/瀚高/8c/PG 本尊）产出与血统逐字同形，七颗 OfflineText 全靠"同形交对拍"代钉、
        // 自己没有一份基线；② 掩码同 15 位而 PinnedOffline 是 8 不是 7：本家在这个族里头一回有自家的
        // golden/ 基线可指（类型表四格换名、身份列换形态、谓词与分页各有绝对形状），离线档因此比邻本厚一颗。
        // 血统那 15 位在本家一位不翻不删（CaseSensitive_Identifiers 与库行为的偏差记 §14 与
        // DuckDbCodeFirst 类注释，本表照旧声明），VendorDoc 七颗的判据与血统那几本同源。
        // <b>【v0.39 第二笔补一句】下一本（第十七本，clickhouse）与它同档不同果</b>：同是"整层文档口径、
        // 零实测"的单层【文档口径行】，但本家不再是复点册 —— 声明位降到 6 颗，Transactions 从这一本起
        // 第一次被"内核写路径不开事务"那一格表态退回（交集就此削去那颗内检索引位，见下面的链），
        // 而插入那一份基线头一回代替"断言抛"出场（EmitReturning 整格抛，insert-with-keys 不落盘）。
        new("duckdb", static () => DuckDb.Provider(SqlServer.NoDriverFactory.Instance), 15, 8, 0,
        [
            // 八颗离线位各指一份自家基线。分页那一份钉的还多一格：skip-only 那颗"缺上限不补哨兵"
            // 由 golden/duckdb/paging-skip-only.sql 单独在场（能力位只有一颗，两形共用一行）。
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "golden/duckdb/paging-order-skip-take.sql"),
            new(DbCapabilities.Returning, Provenance.OfflineText, "golden/duckdb/insert-with-keys.sql"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.OfflineText, "golden/duckdb/select-entity.sql"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/duckdb/ddl-order.sql"),
            new(DbCapabilities.Comment_On_Column, Provenance.OfflineText, "golden/duckdb/ddl-alter-and-comment.sql"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.OfflineText, "golden/duckdb/ddl-alter-and-comment.sql"),

            // 类型列本体那半问仍欠（邻本同句：类型表 bool→BOOLEAN 够不着它）；谓词侧 IS TRUE 那半张
            // 在本家头一回有自家基线可指，不是血统对拍代钉的那一半。
            new(DbCapabilities.Boolean_Type, Provenance.OfflineText, "golden/duckdb/where-boolean-flag.sql"),

            // Guid 的 16 字节原生形态：文本侧够得到的是类型名（UUID），库里是不是真按 16 字节存要靠活库。
            // 与金仓那四本的同一句同判：这一位算离线，而"16 字节"那一半仍是要在活库上补的（§14）。
            new(DbCapabilities.Guid_Raw16, Provenance.OfflineText, "DuckDbDialectTests 那条 UUID 断言（金仓那两条的又一复件；\"16 字节存储\"那一半与邻本一起欠着）"),

            // 下面七颗与血统那几本同判据、同批理由：这一档的掩码是 §9.1/§9.4 的乐观声明，DuckDB 继承同一份，
            // 本家一位不翻案、不删减（收敛是 §4 的账）。读侧索引是血统那份 StoreToClr 的原样克隆
            // （json/jsonb 两名照登，"血统认什么"的最大集保留），写侧没有 CLR→JSON 的映射，故无样本。
            new(DbCapabilities.Upsert_OnConflict, Provenance.VendorDoc, "位无读者：写侧的一句 upsert 档按 IBulkStrategy.UpsertClause 那颗槽分派（槽在血统基类已接，本家一台活库都没有——位与槽是两面，见 MySqlProviderBase 类注释 MultiStatement 那一条），而这一位今天仍然没有消费者"),
            new(DbCapabilities.Cte, Provenance.VendorDoc, "无消费方：共用层没有 WITH 的发射口"),
            new(DbCapabilities.WindowFunctions, Provenance.VendorDoc, "消费方=QuickFx.RowNumber（行号）：文本由共用层 DialectBase.EmitRowNumber 出、十八家零登记模板，而本家这一格还没有活库对账"),
            new(DbCapabilities.Count_Over_Window, Provenance.VendorDoc, "无消费方：CountMethod.CountOverWindow 那一档今天在 QueryEngine 里还是那句未接入的抛（窗口函数那一格有行号了，这一格没有）"),
            new(DbCapabilities.TempTable, Provenance.VendorDoc, "无消费方：Tp 临时主键走的是真实表而不是临时表（§8.2）"),

            // 第 15 位与邻本同一判据：装载通道已接线（血统那颗反射口 PostgreSqlBulk），而 DuckDB.NET
            // 那一装从未在任何机器上解析成功过，COPY … FROM STDIN 一句没发出去过——本家整行「未实测」。
            new(DbCapabilities.Bulk_Copy, Provenance.VendorDoc, "无活库：装载通道已接线（血统那颗反射口），COPY … FROM STDIN 那一句一次都没发出去过"),
            // 【本笔（ClickHouse 重开·笔一）把一颗位挂到这一本上】 Transactions：它不是库种分岔，是内检索引位——
            // 内核写路径（SaveChanges/InsertOrUpdate/批量）从此先判这一位再定包不包事务，公开的 BeginTransaction
            // 没有位就抛 NotSupportedException。每一本在这里各记各的账：这一位由本家自己领名声明，而"库里接不接受
            // 多语句事务"仍然是各家的活库账——本家今天的活库见证是 0，所以落 VendorDoc；行为诚实条款全文住在
            // 位注释与用法册 §2.4，三处同源，改判必须三处一起动。
            new(DbCapabilities.Transactions, Provenance.VendorDoc, "内检索引位（内核写路径按位分岔的那份账）：多语句事务在库里成不成立由各血统的文档口径与活库见证记账，本家今天仍在『未实测』那半本上"),
        ]),

        // 第十七本台账（v0.39 第二笔，ClickHouse 落笔）。前面的复点/复用计数到这里换形状：本家是单层
        // provider 的第二颗【文档口径行】（与 duckdb 那本同档：本机没有引擎、驱动从未解析，整层
        //【文档·ClickHouse】口径），但它不是复点册的三处差异逐条记：① 声明位只有 6 颗 —— 写路径那组
        // 位的空档不是漏记，是整格拒：UPDATE/DELETE 的官方形状都在变异档（ALTER TABLE … UPDATE …
        //【文档·ClickHouse：alter 页】 ∶ lightweight DELETE
        //【文档·ClickHouse，https://clickhouse.com/docs/sql-reference/statements/delete 】），
        // RETURNING/OUTPUT 任何一支都不在官方写语句清单里【文档没逐页回读，本笔如写成"有"】，
        // Upsert_* 亦然（MERGE/ON CONFLICT/ON DUPLICATE 都没有【文档没逐页回读】）；
        // ② <c>Transactions</c> 是本家第一次把这位说"没有"：它不是库种缺欠，是内核那颗表态位在本家
        // 的落位 = 逐句 autocommit 直发、多条语句不原子，公开 BeginTransaction 无位即抛 ——
        // 行为诚实条款全文住在位注释与用法册 §2.4，三处同源；③ PinnedOffline 6 = PinnedLive 0
        //【此读数属第二笔，第三笔已按位逐颗翻 Live，见行内注明】。insert-with-keys 在本家不落盘
        //（EmitReturning 抛，golden 改判为"断言抛"，与 Gbase8s 那本同档）。
        //【v0.39 第三笔（2026-10-20）：第一台引擎（server 26.9.21.9 ∶ 驱动 7.14.0）到场的按位翻账】
        // 六位逐颗换 Live，颗颗带可点名的见证语句（探针与回放输出存 obj/logs/v39-ch-*.md）：
        // 分页三式golden 逐字、布尔三值语义 golden 逐字、Guid↔UUID 参数往返、
        // "app" 限定库建表逐字、MODIFY COLUMN 逐句、判等区分大小写（引号名命中 ∶ 小写被拒带 hint）。
        // 两条不在翻位里但当场记下的：③' 那份 alter golden 的 " ; " 拼接形一次发送被服务端当场拒
        // （Multi-statements are not allowed；拼接是测试端为存档做的、运行期同步是两句分头发，见
        // golden 测试自己那句注释）—— 与 Gbase8s 那本"一次发送多语句"那颗未问之问同格，本家第一次
        // 有读数：不可行 §14；③'' Insert/ExecuteNonQuery 交回的受影响行数恒 0（驱动不数行），
        // 依赖行数的路径别赖在这一家身上。
        //【v0.39 第四笔（beta.26 批，2026-10-20）：声明 6 → 7，第 7 颗是列注释】
        // <c>Comment_On_Column</c> 从"刻意不声明"清单挪进声明清单：发射 <c>ALTER TABLE … COMMENT COLUMN
        // "列" '注'</c> 与读回 <c>system.columns.comment</c>（列注半）、<c>ALTER TABLE … MODIFY COMMENT
        // '注'</c> 与读回 <c>system.tables.comment</c>（表注半，无能力位、读回义务记 TryEmitTableComment
        // 行）—— 四件事一组活库逐字回放过（obj/logs/v39-ch-replay-5/6.md：幂等重发不红；表注半另一份
        // 落盘 golden/clickhouse/ddl-table-comment.sql）。宣言这件事
        // 其实在说：本家"列级 COMMENT 修饰词语法存在而从前没接"是选边、非缺档，与 GBase 8s 那格
        //（真没有那条语句）从此两档分开记，不再同果同账。
        new("clickhouse", static () => ClickHouse.Provider(SqlServer.NoDriverFactory.Instance), 7, 0, 7,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.Live, "golden/clickhouse/paging-order-skip-take.sql"),
            new(DbCapabilities.Boolean_Type, Provenance.Live, "golden/clickhouse/where-boolean-flag.sql"),
            new(DbCapabilities.Guid_Raw16, Provenance.Live, "insert-plain 那一颗 @p0 = Guid 进 UUID 列、SELECT 读回 Guid 原样【实测·2026-10-20 活库】；列目录 system.columns 交回 type=UUID"),
            new(DbCapabilities.CaseSensitive_Identifiers, Provenance.Live, "判等区分大小写【实测·2026-10-20 活库】：\"quicksql_probe\".\"QuickSqlT1\" 按引号名命中；按小写非引号问同表名被服务端拒并给出 \"Maybe you meant … QuickSqlT1\" 提示 —— 第二笔那颗【推断】就此结"),
            new(DbCapabilities.Schemas, Provenance.Live, "golden/clickhouse/ddl-qualified.sql"),
            new(DbCapabilities.Alter_Column_Precision, Provenance.Live, "golden/clickhouse/ddl-alter-and-comment.sql"),
            new(DbCapabilities.Comment_On_Column, Provenance.Live, "golden/clickhouse/ddl-column-comment.sql"),
        ]),

        // 第十八本台账（v0.39 第五笔，TDengine 落笔）。单层 provider 的第三颗【文档口径行】
        //（与 duckdb/clickhouse 那两本同档：本机没有引擎、驱动从未解析，整层【文档·TDengine】口径），
        // 时序库这一档的第一颗。与第十七本同形的三处差异逐条记：① 声明位只有 4 颗 —— 写路径那组
        // 位的空档不是漏记，是整格拒：3.x 没有传统 UPDATE 语句（数据变更只有 INSERT 的 upsert 覆盖
        // 与 ALTER 那两族【文档·TDengine】），DELETE 官方形状只认首列时间窗
        //（https://docs.tdengine.com/tdengine-sql/08-delete/），RETURNING/OUTPUT 任何一支都不在
        // 官方写语句清单里，identity 那一档没有（主键就是那颗时间戳、由调用方给值）；
        // ② <c>Transactions</c> 与第十七本同格取"没有"——驱动侧 <c>BeginDbTransaction</c> 当场
        // throw NotSupportedException（源码读数），内核写路径逐句 autocommit 直发、公开路径无位即抛；
        // ③ <c>Guid_Char36</c> 而不是 Raw16：本家没有 UUID 型，Guid 落 VARCHAR(36) 文本形——
        // 与 GBase 8s 那颗 CHAR(36) 同档不同家，"文本形往返"那一半与邻本一起欠着活库。
        // 四颗全是 OfflineText、各指一份自家基线；insert-with-keys 在本家不落盘（回填被内核先拦、
        // 方言侧 EmitReturning 整格抛，golden 改判为"断言抛"）。
        new("tdengine", static () => TDengine.Provider(SqlServer.NoDriverFactory.Instance), 4, 4, 0,
        [
            new(DbCapabilities.Paging_LimitOffset, Provenance.OfflineText, "golden/tdengine/paging-order-skip-take.sql"),
            new(DbCapabilities.Boolean_Type, Provenance.OfflineText, "golden/tdengine/where-boolean-flag.sql"),
            new(DbCapabilities.Guid_Char36, Provenance.OfflineText, "TDengineDialectTests 那条 VARCHAR(36) 断言（类型表那一格；\"文本形往返\"那一半与邻本一起欠着活库）"),
            new(DbCapabilities.Schemas, Provenance.OfflineText, "golden/tdengine/ddl-qualified.sql"),
        ]),
    ];

    /// <summary>按名字取台账。<b>本类的三条逐家用例都收 <c>string</c> 而不是 <see cref="Ledger"/></b>：
    /// 前者是 <c>[Theory]</c> 能序列化的实参，后者是私有嵌套类型 —— 把它放进公开测试方法的签名里，
    /// C# 的直接判定就是 CS0051（可访问性不一致），而"能序列化的实参"这一条本来也要过。</summary>
    static Ledger LedgerOf(string name) => Ledgers.Single(ledger => ledger.Name == name);

    public static TheoryData<string> Families()
    {
        var data = new TheoryData<string>();

        foreach (var ledger in Ledgers)
            data.Add(ledger.Name);

        return data;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_declared_bit_has_exactly_one_ledger_row_and_vice_versa(string family)
    {
        var ledger = LedgerOf(family);
        var declared = ledger.Provider().Capabilities;

        var bits = ledger.Rows.Select(static row => row.Bit).ToArray();

        Assert.Equal(ledger.Declared, ledger.Rows.Length);
        Assert.Equal(bits.Length, bits.Distinct().Count());

        // 两侧都判：少了行的声明位是"没人证过的乐观"，多了行的未声明位是"台账在替 provider 说话"。
        foreach (var row in ledger.Rows)
            Assert.True(declared.HasFlag(row.Bit), $"{family} 的台账里有一位 {row.Bit} 并没有被 provider 声明——它今天发不出去。");

        var missing = AllBits().Where(bit => declared.HasFlag(bit) && !bits.Contains(bit)).ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Live_witnesses_are_pinned_per_family_and_the_count_is_the_thing_that_moves(string family)
    {
        var ledger = LedgerOf(family);

        Assert.Equal(ledger.PinnedOffline, ledger.Rows.Count(static row => row.From == Provenance.OfflineText));
        Assert.Equal(ledger.VendorDocOnly, ledger.Rows.Count(static row => row.From == Provenance.VendorDoc));

        // 一句"这一家接受这条 SQL"都没有被证明过。哪天接了活库，这一条应该第一个红，
        // 而不是继续绿着——红的时候把 Live 那一档加上来，计数与 §13.5 的台账一起改。
        // <b>v0.32 第二格：这一条按它自己写的用法兑现了，而且红绿成对留了档</b>。把上面那一颗换成 0 原样重跑
        // 一场，它红在虚谷那一份上：<c>obj/logs/v32-ledger-red-1.log</c> = <c>Total: 437, Failed: 1</c>、
        // <c>Expected: 0 / Actual: 4</c>、末行 <c>EXIT=1</c>；换回钉子再跑一场是
        // <c>obj/logs/v32-ledger-green-2.log</c> = <c>437 / 0 / 0 / 0</c>、<c>EXIT=0</c>。这里收口给的不是
        // "把 0 改成 4"这一颗数，是上面 <c>Ledger.PinnedLive</c> 那一颗钉子：<b>红是对的，但收口的动作必须是
        // 逐位重数</b> —— 四颗翻档的各有各的执行见证（<c>obj/logs/v32-recon-golden-6.log</c> 的 G-10 逐句段，
        // 10 颗语句 8 通 2 红），另外三颗（<c>Returning</c> / <c>Schemas</c> / <c>Guid_Char36</c>）今天各有一条
        // 不翻的理由，写在它们自己的注释上。所以这一格从"恒 0"变成"钉住 0 ∶ 0 ∶ 0 ∶ 4"，四族里三份仍然一位都没有。
        Assert.Equal(ledger.PinnedLive, ledger.Rows.Count(static row => row.From == Provenance.Live));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_offline_witness_is_a_file_that_actually_exists(string family)
    {
        var ledger = LedgerOf(family);

        // "见证是一份黄金样本"这话要能在磁盘上兑，否则台账就是一句自我认证。
        // 路径不自己拼：与写基线的那个函数同一个（Golden.PathFor），两处不会各自猜一次输出目录。
        foreach (var row in ledger.Rows.Where(static row => row.Witness.StartsWith("golden/", StringComparison.Ordinal)))
        {
            var relative = row.Witness["golden/".Length..];
            var path = Golden.PathFor(relative[..relative.IndexOf('/')], relative[(relative.IndexOf('/') + 1)..]);

            Assert.True(File.Exists(path), $"{family} 的 {row.Bit} 写着出处 {row.Witness}，而那个文件不存在：{path}");
        }
    }

    [Fact]
    public void The_offline_ledgers_declare_only_the_bits_they_really_have_in_common()
    {
        // 方法名从"four offline families"改成"offline ledgers"是 v0.35 第三笔逼的：台账有了第五份，
        // 而家族还是那四个 —— 名字若留住"四"，这一格改的是数；名字若留住"families"，改的就是口径。
        // 接下一家时该继承哪几位 —— 答案由这几份<em>已落地</em>的台账交出来，而不是由"某系大概有什么"给出。
        // 今天这几本共有的是那一颗（v0.35 第四笔复点：六本 ∶ 三位，形状不变；v0.36 第二笔三度复点：七本 ∶ 三位，仍不变；v0.36 第三笔四度复点：八本 ∶ 三位，仍不变；v0.36 第四笔五度复点：九本 ∶ 三位，第五份反证；v0.36 第五笔六度复点：十本 ∶ 三位，第六份反证；v0.36 第六笔七度复点：十一本 ∶ 三位，第七份反证；<b>v0.37 第一笔八度复点：十二本 ∶ 两位，第一次不是复点而是缩小；v0.37 第二笔九度复点：十三本 ∶ 两位，第一次缩小之后的第一次不变；v0.37 第三笔十度复点：十四本 ∶ 两位，第二次不变，而本笔的新格在交集量不到的那一侧（见下面那一段）；<b>v0.37 第四笔第十一次触链：十五本 ∶ 一位，第二次变小，而两次变小都在"少"的那一侧</b>；<b>v0.39 第一笔第十三次触链：十六本 ∶ 一位，第二次缩小之后的第一本——duckdb 那一本两头都不是它缩小的原因：它声明的 15 位里躺着那两位"中途被扣"的位（本家发得出 COMMENT 与改精度），所以它进链只可能拖进来位而不可能交回空白；而它又没有交出别人都缺的一位</b>），
        // 恰好把"分页怎么写"、"有没有原生 uuid"、"有没有布尔这一档"、"标识符判等分不分大小写"、
        // "回填要不要句尾那一形"五处分岔排除在外，而前三分岔正是 §4 与 §9.3 点名的那几处、第四处是 v0.30
        // 添的那一档、第五处是这一版带进来的；v0.37 第一笔添第六处：<b>列注释那一句发不发得出</b>，
        // 本笔添第七处：<b>改列型状那一问在不在对象里</b>。
        // <b>这一格读起来像统计，它钉的是"交集的形状讲不讲得通"</b> —— 前七次接家它都讲得通（新家声明的每一位
        // 都是邻家已经出现过的位），第十二次它讲不通了，而讲不通的那一位是一句这一族压根没有的语句，
        // 不是一颗还没接的共用层路径。"每接一族交集就小一格"这条隐含预期被否了七次以后今天兑现，
        // 兑现的形状写在下面第三段。
        // <b>v0.37 第四笔：同一句话第十五次上讲不通，而讲不通的那一颗又是一句这一族没有对象的事</b>——
        // <c>Alter_Column_Precision</c> 在本家不是"还没接"，是这一族的存储型清单里压根没有"型状"那一档
        // （声明名只是一颗 affinity 配方，宽度与标度从不参与存储【实测·4001 ∶ 4002】）。
        // 于是这两次缩小是同一形状的两次：<b>削掉的那一位都不是"缺一格实现"，是"少一类问题"</b>，
        // 而"少一类问题"这一档交集量得到（它掉一位），共用层量不到（src/ 的路径一颗没动）。
        var kingbase = Flags("kingbase");
        var dameng = Flags("dameng");
        var mysql = Flags("mysql");
        var xugu = Flags("xugu");
        var tidb = Flags("tidb");
        var oceanbase = Flags("oceanbase");
        var openGauss = Flags("opengauss");
        var gaussDb = Flags("gaussdb");
        var highgo = Flags("highgo");
        var shenTong = Flags("shentong");
        var gbase8c = Flags("gbase8c");
        var gbase8s = Flags("gbase8s");
        var postgreSql = Flags("postgresql");
        var oracle = Flags("oracle");
        var sqlite = Flags("sqlite");
        var duckDb = Flags("duckdb");
        var clickHouse = Flags("clickhouse");
        var tdengine = Flags("tdengine");

        // tidb 进交集链是白给的一票（掩码逐位同 mysql），而它进链本身就是要钉的事：
        // "接第二颗叶子没有把交集改小"与 v0.31 那句"接第五族没改小"是同一格判据的第二次兑现。
        // v0.35 第四笔再兑现一次：oceanbase 是 MySQL 血统的第三本，同样一票白给、同样"进链本身就是主张"——
        // 六本台账交出来仍是那三位，"每接一家交集就小一格"这条隐含预期今天有了<em>两份</em>反证加一个零头。
        // v0.36 第二笔第三次兑现，而这一票换了血统：opengauss 是 PG 血统的第二本，金仓已把它的位
        // 全部交过一次，交集仍是那三位 —— 七本 ∶ 三位，反证从两份长到三份。
        // v0.36 第三笔第四次兑现：gaussdb 是 PG 血统的第三本，白给的程度比上一票更彻底——它不是对
        // 金仓本逐位同掩码，是对金仓本<em>与</em> openGauss 本同时逐位同掩码（三份 14 位出自同一颗
        // FamilyCapabilities），交集照旧不动。
        // v0.36 第四笔第五次兑现：highgo 是 PG 血统的第四本，四份 14 位出自同一颗 FamilyCapabilities，
        // 白给程度照旧——而这一票投得最安静：它是唯一一本连前件都不带的（厂商行零方言级点名，见本本头注），
        // 交集链因此第一次出现"新来的那本连一处可犹豫的声明都没有"的形状。九本 ∶ 三位。
        // v0.36 第五笔第六次兑现，而这一票换了血统：shentong 是 Oracle 血统的第二本，6 位与达梦本逐位同
        // 掩码（同一颗 FamilyCapabilities），交集照旧不动——十本 ∶ 三位。本册的白给方式也新：它是
        // 第一本"掩码与邻本全同、产出与邻本差一句"的台账，交集量的是声明位，量的不是文本形——
        // "同掩码 ≠ 同形"第一次让交集链在不变的同时多了一格可指认的读法。
        // v0.36 第六笔第七次兑现：gbase8c 是 PG 血统的第五本，五份 14 位出自同一颗 FamilyCapabilities，
        // 交集照旧不动——十一本 ∶ 三位。本册白给的方式又换了一格：它带来的是<b>前件来路</b>的第四种，
        // 而那件事交集量不到（前件不在掩码里）——上一笔刚立了"同掩码 ≠ 同形"，本笔紧跟着立"同掩码 ≠ 同前件"。
        //
        // <b>v0.37 第一笔：这条链在这里断了，而断的方向与前面七次都相反。</b>第十二本（gbase8s，Informix
        // 血统的第一颗）不是一颗白给票，它把 <c>Comment_On_Column</c> 从交集里扣掉——那一位此前十一本每本都有，
        // 本家交回空（这一家的语句清单里没有 <c>COMMENT ON</c>，列注释走 Core 的"跳过并 WARN"退路）。
        // 于是交集从 3 位变 2 位（<c>Schemas</c> ∶ <c>Alter_Column_Precision</c>），而文件头那段预测
        // （"哪天某一家接了只有它有的一种能力，交集会当场从三位掉到两位，而这一份文件是第一个红的地方"）
        // 今天兑现了，兑现的却是<em>反方向</em>：红的原因不是"新来的多了一颗别人没有的"，是"新来的少了
        // 别人都有的那颗"。<b>多一颗时邻家没接就照旧不动，少一颗则当场扣掉交集的一位——涨的是名单，削的是地基</b>，
        // 而这份台账此前十一本只演示过前一种。
        // 还要紧的是这一格<em>不是</em>回退：v0.32 那条"交集量的是 src/ 的覆盖面而不是库的能力面"到本笔
        // 仍然成立而<b>不再充分</b>——覆盖面变了是因为这一家发不出那条语句，共用层的路径一颗没动。
        // 两句一起记 §13.5，而这里留可执行的那一半。
        //
        // <b>v0.37 第二笔：这条链第九次复点，而这一次的"白给"多一格读法。</b>第十三本（postgresql，PG 血统的
        // 第六本）交的是同一颗 FamilyCapabilities 那 14 位，两位一位不少——缩过一次之后第一次不再缩。它进链
        // 本身要钉的事与前五次同档（"接本尊没有把交集改小"），而它带来的那一格新事在<b>交集量不到的那一层</b>：
        // 位与来路都不变，变的是同一份掩码在<em>版本轴</em>上的宽度（本尊必须同时吃下血统那两句朝相反方向指的
        // 版本形状，见本本头注）。交集对这一格钝感，正如它对"同掩码 ≠ 同形""同掩码 ≠ 同前件"钝感——这是那两格
        // 的第三种读法，也是本文件第四次把"台账没红"与"这一族没有新事实"分开。
        //
        // <b>v0.37 第三笔：这条链第十次复点，而这一票是本文件那句"多一颗涨名单、少一颗削地基"的正面见证。</b>
        // 第十四本（oracle，Oracle 血统的第三本）交的是同一颗 FamilyCapabilities 那 6 位，两位一位不少——
        // 它上一笔刚被扣掉的那一位（<c>Comment_On_Column</c>）由本家<em>原样交回</em>，所以交集停在两位而不是
        // 掉到一位。这一格值得钉的不是"没缩"，是<b>缩过一次之后第一次有新书接上那一位</b>：那条语句在这一族
        // 是原生形（<c>COMMENT ON COLUMN</c>），本家发得出、也不新起一份覆盖面，它只是第三本说同一句话的账。
        // 本笔交集量不到的那一格换了轴：不再是形、前件或版本宽度，而是<b>"零覆写"那句理由的方向</b>
        // （神通"没读数所以不赌" ∶ 本尊"默认即自家的话"）——同一颗掩码，两种"为什么一个都不必改"。
        //
        // <b>v0.37 第四笔：这条链第二次变小，而两次变小都在"少"的那一侧。</b>第十五本（sqlite，第七家，
        // 一本没有同掩码邻本的账）交的五位里有一颗别人都有而它没有的 <c>Alter_Column_Precision</c>——
        // 于是 <b>2 位 → 1 位</b>，剩下的那一颗是 <c>Schemas</c>。这一票与上一笔那两票（postgresql／oracle
        // 各自"接了本尊而交集不动"）正对面：那两票钉的是"多一颗不涨交集"，本票钉的是"少一颗又削一次地基"，
        // 而 v0.37 第一笔立的那句"多一颗涨的是名单、少一颗削的是地基"到本笔有了第二次兑现——
        // 两次兑现都在同一个方向上，说明那条读法不是第一次的偶然。
        // <b>剩下那一颗本身正在变形，这一句必须写在这里而不是只写在类注释里</b>：七家今天都声明 <c>Schemas</c>，
        // 而它在这一家指的是"所有者"（<c>Gbase8sSchemaReader</c> 那道 <c>t.owner</c> 闸），在本家指的是
        // "挂进来的另一个文件"（<c>CREATE SCHEMA</c> 发不出【实测·2824】，而 <c>main.</c> ∶ <c>side.</c>
        // 两侧确实都限得住【实测·3000/3001 ∶ 3005 ∶ 3006】）。于是"交集只剩一颗"这句读起来像收敛的终点，
        // 实际是这本账能力的<em>下界</em>：剩下那颗共的位，其语义已经不是一个东西。
        //
        // <b>v0.39 第一笔：这条链第十三次触链，而本票两头都不会动交集。</b>第十六本（duckdb，PG 血统的
        // 第七本）交的是血统那 15 位，两位中途被扣的位（Comment_On_Column ∶ Alter_Column_Precision）它
        // 都带着——交集已经只剩 <c>Schemas</c>，多进来的位拖不动它；而它也没有交出别人都缺的一位。
        // 这一格值得记的另一半在改产出的那一侧：本家是 PG 血统头一颗<b>真的改了血统文本</b>的叶子，
        // 而交集对此照旧钝感——交集量的是声明位的覆盖面，量的不是"文本是谁发的"。
        // "同掩码 ≠ 同形"（v0.36 第五笔立的那一格）到本笔从 Oracle 血统跨到了 PG 血统。
        //
        // <b>v0.39 第二笔：这条链第十四次触链，而这一次被扣掉的是内检索引位。</b>第十七本（clickhouse，
        // 单层第二颗【文档口径行】）交的 6 位一颗不少都在别人见过的位上，但它把 <c>Transactions</c> 整格
        // 退回：UPDATE/DELETE 与"多语句原子"在那一族官方形状全在变异档【文档·ClickHouse：alter 页 ∶
        // lightweight DELETE 页】，内核写路径在那一族是逐句 autocommit 直发、多条语句不原子，
        // 公开 BeginTransaction 无位即抛 —— 于是交集从"十六本 ∶ 两位"缩到"十七本 ∶ 一位"，
        // <b>三位地基里的最后一颗库种位（<c>Schemas</c>）与那颗内检索引位分开算账从这里起生效</b>：
        // 交集里剩下的 <c>Schemas</c> 量的是"每一家都有限定名发射口"这一格，而 <c>Transactions</c>
        // 从"逐本声明"变成"十六本里有本缺"——它与 <c>Comment_On_Column</c> 那两颗相同格：掉出交集
        // 不等于"谁没人护着"，那一半的账仍在各行辖内。
        // <b>v0.39 第五笔：这条链第十五次触链，而本票又是一条白给票。</b>第十八本（tdengine，单层第三颗
        //【文档口径行】，时序库这一档的第一颗）交的 4 位（<c>Paging_LimitOffset</c> ∶
        // <c>Boolean_Type</c> ∶ <c>Guid_Char36</c> ∶ <c>Schemas</c>）全部落在别人已经出现过的位上，
        // 而 <c>Schemas</c> 它带着——交集停在"十八本 ∶ 一位"不动。这一格值得钉的另一半在
        // <c>Transactions</c>：本家与第十七本同格取"没有"，而它的来路比 clickhouse 那票更硬——
        // 不是"文档没逐页回读"那一档，是驱动源码的当场 throw（<c>BeginDbTransaction</c>，
        // VendorDoc·TDengine·驱动源码）。交集对此照旧钝感：它量的还是那颗 <c>Schemas</c>，
        // "没有事务"的账在各行辖内。
        var shared = kingbase.Intersect(dameng).Intersect(mysql).Intersect(xugu).Intersect(tidb).Intersect(oceanbase).Intersect(openGauss).Intersect(gaussDb).Intersect(highgo).Intersect(shenTong).Intersect(gbase8c).Intersect(gbase8s).Intersect(postgreSql).Intersect(oracle).Intersect(sqlite).Intersect(duckDb).Intersect(clickHouse).Intersect(tdengine).ToArray();

        var expected = new[]
        {
            DbCapabilities.Schemas,
        };

        // <b>【v0.39 第二笔续写】</b>第二位（<c>Transactions</c>）从本笔起不在交集里，而它退出的方式与
        // 到场的分岔都要说白：点明本位在内核写路径（SaveChanges ∶ InsertOrUpdate ∶ 三条批量链）
        // 与公开的 BeginTransaction 真读（判据取唯一写法 <c>TransactionCapability.Declared</c>），
        // 十六本在这里逐本声明回去；第十七本（clickhouse）取"没有"那一格 ——内核在那一族
        // 逐句 autocommit 直发、多条语句不原子，公开路径无位即抛（严禁静默假事务，与
        // <see cref="IDbProvider.CanCreateDatabase"/> 那颗 false 先例同一形状）。行为诚实条款全文住在
        // 位注释与用法册 §2.4。它进交集的方式与 <c>Schemas</c> 不同的那半要说白：
        // <c>Schemas</c> 是十六本各自发得出限定名的库种事实，本位是"内核按这一位分岔"的接线事实 ——
        // 位在每一家手里都是同一颗，家长的是同一份判词；它量不到任何一家的库收不收多语句事务
        //（那半在 clickhouse 行里落 VendorDoc，其余行是文档口径与活库见证记账）。
        Assert.DoesNotContain(DbCapabilities.Transactions, shared);
        Assert.Contains(DbCapabilities.Transactions, duckDb);
        Assert.DoesNotContain(DbCapabilities.Transactions, clickHouse);

        // 判集合相等而不是判序列：交出来的顺序是 Enum.GetValues 的事，不是这一条的主张。
        Assert.Equal(expected.Length, shared.Length);

        foreach (var bit in expected)
            Assert.Contains(bit, shared);

        // 分页那一档四家各说各话：位都声明了，但只有两家是同一个名字，所以交集里没有它 ——
        // 这一格是"为什么 Oracle 那一族要新造一个 Paging_RowNum 而不是复用 Paging_LimitOffset"的可执行版本。
        // 第五家落在 LIMIT 那一边，于是"同一位"那三家凑齐了四家里的三家（金仓、MySQL、虚谷），
        // 而这一格真正被它改变的地方是：同<em>一位</em>之下现在有了两颗<em>不同</em>的哨兵数
        // （2^64−1 与 2147483647）—— 能力位表达不到那一层，所以那一格不在这里量，在 §9.4 与
        // DialectParityTests 的那两条 Skip-only 断言里量。
        Assert.Contains(DbCapabilities.Paging_LimitOffset, kingbase);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, mysql);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, xugu);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, tidb);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, oceanbase);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, openGauss);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, gaussDb);
        Assert.Contains(DbCapabilities.Paging_LimitOffset, highgo);
        // v0.36 第六笔：PG 那一边从四家长到五家，同一位、同一种写法，"三种写法"的种数照旧不随册数涨。
        Assert.Contains(DbCapabilities.Paging_LimitOffset, gbase8c);
        // v0.37 第二笔：PG 那一边从五家长到六家（本尊那颗键与五叶同一位、同一种写法），
        // "能报出来的三种写法"的种数第四次不随册数涨。
        Assert.Contains(DbCapabilities.Paging_LimitOffset, postgreSql);
        // v0.37 第四笔：这一<em>位</em>的第七户，而它不是任何一种的复点——本家的尾巴是
        // <c>LIMIT @p0 OFFSET @p1</c>，缺上限那一档发的是 <c>LIMIT -1</c> 这一颗"不设上限"的记号，
        // 而不是 MySQL 那两家的 2^64−1、也不是虚谷那颗厂商写死的 2147483647。上面那段"同一位之下
        // 有了两颗不同的哨兵数"今天变三颗，而第三颗与前两颗不同类：那两颗是<em>一个真实存在的上限</em>，
        // 本家那一颗是<em>语法里的负数记号</em>。种数照旧是三（LIMIT ∶ ROW_NUMBER ∶ FETCH），
        // 而哨兵那一格不在这里量——它在 <c>SqliteDialect</c> 类注释第三格与
        // <c>golden/sqlite/paging-skip-only.sql</c> 里量。
        Assert.Contains(DbCapabilities.Paging_LimitOffset, sqlite);
        // v0.37 第一笔：这一格第一次有<b>第四种形状</b>可数，而它不进来——本家有分页产出
        // （<c>SELECT FIRST n SKIP m</c>，句首限定词）而枚举里没有第四颗分页位，所以它是六家里第一本
        // "分页那三位全空、而分页文本天天在发"的台账。上面那句"三种写法"因此要读成"能报出来的三种写法"：
        // 种数没涨，是因为涨的那一种报不出，不是因为本家没有分页。
        Assert.False(gbase8s.Contains(DbCapabilities.Paging_LimitOffset) || gbase8s.Contains(DbCapabilities.Paging_RowNum)
            || gbase8s.Contains(DbCapabilities.Paging_OffsetFetch));
        Assert.Contains(DbCapabilities.Paging_RowNum, dameng);
        // v0.36 第五笔：Oracle 那一边从一家变两家（shentong 落达梦同边，名字同为 Paging_RowNum）——
        // "四家各说各话"量的仍是三种写法（LimitOffset / RowNum / T-SQL FETCH），只是 RowNum 那一种有了第二户。
        Assert.Contains(DbCapabilities.Paging_RowNum, shenTong);
        // v0.37 第三笔：RowNum 那一<em>种</em>写法从两户走到三户，而本户与另两户的关系和神通那票不同——
        // 神通是"另一颗叶子落同一边"，本尊是"这一族的名字本来就是它家的"。所以这一格第一次出现
        // <b>种数与户数同时不涨而理由最直的一次</b>：上面那句"四家各说各话"量的仍是三种写法，
        // 而"三种"里 RowNum 那一支今天的读数来源正是这一本账（另两户是从它继承的）。
        Assert.Contains(DbCapabilities.Paging_RowNum, oracle);
        Assert.False(dameng.Contains(DbCapabilities.Paging_LimitOffset) || shenTong.Contains(DbCapabilities.Paging_LimitOffset)
            || oracle.Contains(DbCapabilities.Paging_LimitOffset) || kingbase.Contains(DbCapabilities.Paging_RowNum));

        // Boolean_Type 不在交集里，而四家的说法是<em>四种不同的事</em>，不是一句"这一家少了"：
        // 金仓有真布尔列所以声明这一位；达梦的布尔是 NUMBER(1) 近似（§9.1），没有布尔这一档可声明；
        // MySQL 有 BOOL 这个<em>名字</em>，而那一型是 TINYINT(1)、装得下 2；第五家<em>有</em>真三值布尔
        // （厂商文档明写 TRUE|FALSE|NULL），今天仍然不声明 —— 它是五家里第一次"库里明确有而 provider 说不"，
        // 理由是谓词侧那一档（IS TRUE）在这一家两档出处都没出现过（XuguProvider 类注释第一条）。
        // v0.36 第二笔给"有"那侧添了第二家（openGauss 与金仓同一份类型表），上面"四种不同的事"量的仍是
        // 四条血统，新来的这一票投的是金仓那份理由而不是第五种说法。v0.36 第三笔再投一票（gaussdb），
        // 投的还是同一份理由——PG 血统三家说的是同一句话，这一格的"种数"没有随册数涨。
        // v0.36 第四笔第四票（highgo），投的还是同一份理由：PG 血统四家一句话，"种数"仍不随册数涨。
        // v0.36 第五笔换血统投"不声明"那侧的第二票（shentong 落达梦同边，理由是基类注释那一条：
        // 声明它就等于让谓词侧走错分支）——"不声明"侧从四家变五家，而"种数"照旧不随册数涨。
        Assert.Contains(DbCapabilities.Boolean_Type, kingbase);
        Assert.Contains(DbCapabilities.Boolean_Type, openGauss);
        Assert.Contains(DbCapabilities.Boolean_Type, gaussDb);
        Assert.Contains(DbCapabilities.Boolean_Type, highgo);
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, dameng);
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, shenTong);
        // v0.37 第三笔：Oracle 血统"不声明"那侧的第三票，而它是三票里唯一一票<b>不需要"没读数所以不赌"</b>的——
        // 本尊这一族的布尔是 NUMBER(1) 近似这一条口径本来就出自它家（达梦/神通是 inherited 的读数），
        // 所以这一格第一次出现"同一句理由的第一份原文到场"：种数照旧不涨，涨的是那条理由的<b>出处层级</b>
        // （从"两家引用一份基类注释"变成"一家自己就是那份注释的来源"）。
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, oracle);
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, mysql);
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, xugu);
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, tidb);
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, oceanbase);
        // v0.37 第一笔：这一边来了第六族的第一户，而它的理由是"位只跟存储型走"那条规则第一次由<em>新落地的家</em>
        // 套用第二遍——本家库里<em>有</em> BOOLEAN 那一型【文档·IBM】，而 <c>Gbase8sTypes.GetStoreType</c>
        // 把 CLR bool 落 <c>INTEGER</c>，所以这一位不报。上面那句"四种不同的事"量的仍是血统条数，不随册数涨。
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, gbase8s);
        // v0.37 第四笔：同一句裁断（位只跟存储型走）第二次由<em>新落地的家</em>套用，而本家的因由更硬一层：
        // GBase 8s 那一票是"库里<em>有</em> BOOLEAN 那一型而类型表不落它"，本家是 affinity 之下
        // <c>BOOLEAN</c> 与 <c>INTEGER</c> 是<em>同一颗</em>存储【实测·610】——不是"有而不用"，是"那一档不存在"。
        // 于是"四种不同的事"量的仍然是血统条数：本家带来第五种说法，而它落在"不声明"那侧，种数不涨。
        Assert.DoesNotContain(DbCapabilities.Boolean_Type, sqlite);

        // Guid 那一档今天分成 1∶4，而两边都是"库种事实"不是取舍：金仓有原生 uuid，另四家没有（或有而不可证）。
        // 第五家落在 CHAR(36) 那三家里，所以这一格从"1∶2"长到"1∶3"而<em>没有</em>新增第四种形状 ——
        // 它是"交集为什么没被这一版改小"的那个回答：新家族带来的位全是已出现过的位。
        // v0.35 第四笔再长一格（1∶4）：oceanbase 落在同一边，而它连"有 BINARY(16) 而不可证"都轮不上——
        // 那一问在双模库上先要过"模式在连接上决定"那道前件（§14），今天的形状是"整本台账自带悬条件"。
        // v0.36 第二笔把比值翻成 2∶4：openGauss 落在金仓同边（同一份血统类型表），而"16 字节存储"那一半
        // 两家今天仍然一起欠着——复件带来的是主张的第二次见证，不是那一问的答案。
        // v0.36 第三笔把比值推到 3∶4：gaussdb 落同一边，"那一半"从两家一起欠变成三家一起欠——
        // 第三次见证仍然不是答案，而这正是"复件册涨到第三本仍然量不出新事实"这一格的形状本身。
        // v0.36 第四笔把比值推到 4∶4：highgo 落同一边，"那一半"从三家一起欠变成四家一起欠——
        // 第四次见证仍不是答案，而本家让这一格有了最纯的一份复件：连前件都不带（厂商行零方言级点名）。
        // v0.36 第五笔把比值推到 4∶6：shentong 落 CHAR(36) 那一边，而这一票与另九票都不同——它不是复件，
        // 是 Oracle 血统内部"同掩码的两家落同一个型名"的第二次表态；两边比值之外那句"库种事实不是取舍"
        // 第一次有了同一血统里的两份独立文档口径（达梦行与神通行各写各的，没有共行）。
        Assert.Contains(DbCapabilities.Guid_Raw16, kingbase);
        Assert.Contains(DbCapabilities.Guid_Raw16, openGauss);
        Assert.Contains(DbCapabilities.Guid_Raw16, gaussDb);
        Assert.Contains(DbCapabilities.Guid_Raw16, highgo);
        Assert.Contains(DbCapabilities.Guid_Char36, dameng);
        Assert.Contains(DbCapabilities.Guid_Char36, shenTong);
        Assert.Contains(DbCapabilities.Guid_Char36, mysql);
        Assert.Contains(DbCapabilities.Guid_Char36, xugu);
        Assert.Contains(DbCapabilities.Guid_Char36, tidb);
        Assert.Contains(DbCapabilities.Guid_Char36, oceanbase);
        // v0.37 第一笔：比值从 4∶6 走到 4∶7。这一票落的那一边达梦与神通已经站过（"库里没有那一型"，与 MySQL
        // 那家的"有而选文本"是两条判据，见下面那几条同名异事的 Fact），所以种数照旧不随册数涨。本家带来的
        // 新东西在 <em>那一问的形状</em>：前两家那一边的"16 字节存储往返"仍然是一句没证的悬账，本家是
        // <c>Guid_Raw16</c> 那一支候选压根不存在（无原生 uuid 型、<em>也没有取号函数</em>【文档·IBM】）——
        // 于是这一位在本家不是二选一，是唯一答案，与 <c>Identity_ExplicitInsert</c>
        // 那一格"false 而理由是用不上"同型：都是<b>候选少掉一支</b>而不是<b>候选没测</b>。
        Assert.Contains(DbCapabilities.Guid_Char36, gbase8s);
        Assert.DoesNotContain(DbCapabilities.Guid_Raw16, gbase8s);
        // v0.37 第三笔：本票落 CHAR(36) 那一边，而它给的是那一边从来没有过的一格——上面那句
        // <em>"库里没有那一型"是"库种事实不是取舍"</em>，达梦与神通那两票投的是<b>引用来的</b>事实
        // （读数来自 Oracle 血统那一份类型表），本票是<b>那份类型表自己的住户</b>第一次亲自投它。
        // 于是这一格的"比值"本笔刻意不重数：上一笔（postgresql，落 Raw16 那一侧）没有把上面那串比值
        // 复点过，这里若直接写一个新数，就把那一笔的缺口当成自己数出来的一部分了——缺口记本文件末的欠账。
        Assert.Contains(DbCapabilities.Guid_Char36, oracle);
        Assert.DoesNotContain(DbCapabilities.Guid_Raw16, oracle);
        // v0.37 第四笔：上面那一段比值从今天起要加<em>第三种读法</em>。此前十四本每本都在两边里落一边
        // （Raw16 ∶ Char36），本家两边都不落：<c>Guid</c> 落 <c>TEXT</c> 而那颗列上没有任何约束，
        // 36 字符文本 <c>GetGuid</c> 读回成立【实测·336 ∶ 337】，绑成 <c>byte[16]</c> 也进得去而读回是
        // <c>Byte[]</c>【实测·V ∶ X】。<b>"两边都不落"与"落 Char36 那一边的第四/第七票"不是一回事</b>：
        // 落那一边的至少主张"库认得这个型名"，本家那一颗 <c>CHAR(36)</c> 是 TEXT 的别名、
        // <c>RAW16</c> 那一支压根没有对应声明——两支的<em>前提</em>都不是这里缺的读数，而是那一问在本家没有对象。
        // 于是这一格第一次不能用"比值"记账：把它写成 4∶8 就是替本家撒一句它没说过的话。
        Assert.DoesNotContain(DbCapabilities.Guid_Raw16, sqlite);
        Assert.DoesNotContain(DbCapabilities.Guid_Char36, sqlite);

        // 引号照加与"解析器判等区分大小写"是两件事：MySQL 那一族只有前者（表名那颗开关在文件系统上、建库时定死）。
        // 于是"另两家共有、这一家没有"的那四位里，CaseSensitive_Identifiers 是唯一一位"文档里明确说不成立"的，
        // 而不是"代码还没接" —— 这一格与上面 Boolean_Type 那一段是同一类判断，值得写死而不是留在读注释里。
        // 第五家与金仓、达梦同边（三家里它最新），所以"这一位没进交集"这件事的<em>原因</em>从"两家共有"
        // 改成了"三家共有"，而判它的方法没变：仍然只看 MySQL 那一家为什么不进来。
        // v0.36 第二笔再涨一家（openGauss，金仓同血统）：这一位的"共有"侧从三家读成四家，判法照旧。
        // v0.36 第三笔再涨到五家（gaussdb，同一份血统类型表的第三颗壳），判法仍照旧——只数 MySQL 那一家为什么不进来。
        // v0.36 第四笔再涨到六家（highgo，第四颗壳），判法第四次照旧。
        // v0.36 第五笔再涨到七家（shentong，Oracle 血统第二颗叶，同一颗 IdentifierPolicy.OracleFamily）——
        // 而这一票多一层意思：这一位是两本同掩码台账共同的位，"共有"数第一次在 Oracle 血统内部也有第二户。
        Assert.DoesNotContain(DbCapabilities.CaseSensitive_Identifiers, mysql);
        Assert.DoesNotContain(DbCapabilities.CaseSensitive_Identifiers, tidb);
        Assert.DoesNotContain(DbCapabilities.CaseSensitive_Identifiers, oceanbase);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, kingbase);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, dameng);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, shenTong);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, xugu);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, openGauss);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, gaussDb);
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, highgo);
        // v0.37 第一笔：判法照旧——只数 MySQL 那三家为什么不进来。本家多带的一层在
        // <c>IdentifierPolicy</c> 那一颗记录上：它与金仓那颗<b>按值相等</b>（同一份双引号 + 判等区分 + 不折），
        // 而产出文本差一句 <c>AS</c>。于是"同一颗策略值"与"同一份产出"第一次在同一本账里被分开钉住
        // （见 <c>Gbase8sDialectTests</c> 读侧那两条一对的 Has/Absent）——这一位量的从来是判等纪律，不是文本形。
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, gbase8s);
        // v0.37 第三笔：判法照旧（只数 MySQL 那三家为什么不进来），"共有"侧从八家读成九家。
        // 本票的新东西在<b>谁在说这句话</b>：这一族的折叠规则今天有三本账在写，两本（达梦/神通）的
        // 判据都指回同一颗 <c>IdentifierPolicy.OracleFamily</c>，而那一颗的名字挂的就是本家——
        // 第九度复点第一次由<b>那颗策略值的冠名者</b>来投。
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, oracle);
        // v0.37 第四笔：不进来的是第四本，而它是<b>第一本不在 MySQL 血统里的</b>——上面那句
        // "只数 MySQL 那三家为什么不进来"到本笔要改口成"数那三家加本家"，而判法换了：
        // MySQL 那一族的理由是"折叠发生在文件系统上、建库时定死"（一颗开关，§14 的参数名单），
        // 本家那一颗是<em>解析器本身不区分</em>：带引号写错大小写照样解析得到，而"只差大小写的两张表"
        // 是同一张【实测·第 2 轮 11 ∶ 12 ∶ 16 ∶ 19 ∶ 20 ∶ 18 那颗 <c>table mixedcase already exists</c>】。
        // 于是"引号照加"与"判等区分"这两件事在本家彻底分家：那句 <c>"QuickSqlOrder"</c> 加了引号而
        // 不换来任何区分——<c>CaseSensitive_Identifiers</c> 这一位量的从来是后者，本家第一次让
        // "加了引号"这件事在台账里<em>一点都不得分</em>。
        Assert.DoesNotContain(DbCapabilities.CaseSensitive_Identifiers, sqlite);
        // v0.39 第一笔：血统那七户照在"共有"那侧，而本票带着一格档案性的读法——本家库里实际是
        // 大小写不敏感而保留原样【文档·DuckDB】，位照血统一字不翻，偏差全文记 §14：台账量的是
        // "声明面动没动"，不量"库收不收这一位讨来的区分"。
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, duckDb);
        // v0.39 第二笔：这一位的第十户，来路与另九户同向：官方 identifiers 页写明区分大小写且取
        // SQL 标准引号那一副【文档·ClickHouse】，判法照旧（不进交集只是因为 SQLite 那一票不声明它）。
        Assert.Contains(DbCapabilities.CaseSensitive_Identifiers, clickHouse);

        // Returning 是这一版新添的那一分岔：它在四家里只占两位（金仓与虚谷），而这两位的<em>来路</em>不同 ——
        // 那两家的产物/文档各给了一个句尾 RETURNING，另两家（Oracle 血统与 MySQL 血统）连批量那一形都给不出。
        // 它不进交集，而它不进的方式与上面几格不同：不是"这一家少了"，是"这一家压根没有可发的形状"，
        // 所以这一位是"离线四族的台账第一次在同一位上给出两种相反的声明"。
        // v0.36 第二笔：这一位在"有"那侧从两家变三家，而三家给的仍是同一种形状（句尾形），前置形照旧孤例。
        // v0.36 第三笔：变四家（gaussdb），形状仍那一种——PG 血统三家里没有一颗对 Returning 表过态。
        // v0.36 第四笔：变五家（highgo），形状仍那一种——第四颗也没表态，而它是五家里唯一一颗连被问的资格都没有的（行里没有分岔）。
        // v0.36 第五笔："没有"那侧从四家长到五家（shentong 落达梦同边），理由同一条血统的 OracleKeys 抛口；
        // 本笔在文案这一格还量出一件新事：这一族的拒绝消息带的是"Oracle 血统"那颗词而不是 Kind——
        // 同族两叶的键回读拒绝因此逐字同形（见 ShenTongDialectTests 写侧那一格），与 EmitJoinedSources
        // 那句带 {Kind} 的拒收在同一册里被分开钉住。
        Assert.Contains(DbCapabilities.Returning, kingbase);
        Assert.Contains(DbCapabilities.Returning, xugu);
        Assert.Contains(DbCapabilities.Returning, openGauss);
        Assert.Contains(DbCapabilities.Returning, gaussDb);
        Assert.Contains(DbCapabilities.Returning, highgo);
        Assert.DoesNotContain(DbCapabilities.Returning, dameng);
        Assert.DoesNotContain(DbCapabilities.Returning, shenTong);
        Assert.DoesNotContain(DbCapabilities.Returning, mysql);
        Assert.DoesNotContain(DbCapabilities.Returning, tidb);
        Assert.DoesNotContain(DbCapabilities.Returning, oceanbase);
        // v0.37 第一笔：这一位的"没有"那侧添第六族的第一户，而它给的是这条判据的<b>第四种"没有"</b>：
        // 达梦与神通是基类抛（血统里没这一形），金仓/虚谷/PG 四家是有这一形，MySQL 三家压根没有回填这一档；
        // 本家的 <c>RETURNING</c> 一词<em>确实存在</em>【文档·IBM】，却只活在 ESQL/SPL 里配 <c>INTO</c> 宿主变量，
        // CLI 收不到那个结果集——于是"库里有这一词"与"这一位可报"在本家是两件事，抛口写给的是那半件事
        // （<c>Gbase8sKeys.EmitReturning</c> 的消息里带"方向位"那一格）。
        Assert.DoesNotContain(DbCapabilities.Returning, gbase8s);
        // v0.37 第三笔：这一位"没有"那侧的第六家，而它是这一格判据的<b>反向</b>一次：本家不是"没有那一形"
        // 也不是"有而发不出"——是<b>这一族的键回填今天确实一句都发不出，而投出这一票的账本家就是那句
        // 拒绝文案的出处</b>（<c>OracleKeys</c> 消息里那句"Oracle 血统"从本笔起不再是一句借来的话）。
        // 第五笔在 shentong 那一格量到的"同族两叶逐字同形"今天涨到三叶，而本尊那一颗是<b>影子探针与它
        // 逐字同形</b>（见 OracleDialectTests 点名的那一条）——这是这一位第一次"两家同形而两家是同一份代码"。
        Assert.DoesNotContain(DbCapabilities.Returning, oracle);
        // v0.37 第四笔："有"那侧第六家，而这一票与另五家不同档——它是这一位<em>第一次有 Live 读数</em>的一本。
        // 上面那段"有而不可证"在虚谷那本上写得很具体（键回填整档缺 SPI 槽位、46 条按名 skip，所以那一颗
        // 只能停在 OfflineText），本家那一档有对象：<c>WritePathTests</c> 那条 600 行 ∶ 3 块的批量回填
        // 在临时文件库上把 600 颗主键逐颗收回、并按 payload 对上了行（<c>obj/logs/v37-sqlite-int-4.log</c>）。
        // 于是这一位从"五家说同一句话"变成"五家说同一句话、其中一家把那句话真发出去过"，
        // 而交集对此照旧钝感——句尾那一形本来就不在交集里。
        Assert.Contains(DbCapabilities.Returning, sqlite);

        // 下面这一段是 v0.37 第一笔才有的：一位从前十一本每本都有、因此从来没被单独立为判据的位。
        // 它掉出交集那一刻起，"这一位谁没接"就不再是一句统计而是一格要写理由的账——所以这里把它钉住：
        // 交集里现在<em>没有</em>它，而它掉出去的唯一原因是本家<em>发不出</em>那条语句
        // （<c>Gbase8sCodeFirst.TryEmitColumnComment</c> 交回 false 且一字不写，列注释走 Core 的跳过并 WARN 退路）。
        // 这一格与本文件开头那段预测的差别就在"发不出"三个字：覆盖面没有变薄，是这一族的语句清单里少一行。
        Assert.DoesNotContain(DbCapabilities.Comment_On_Column, gbase8s);
        Assert.DoesNotContain(DbCapabilities.Comment_On_Column, shared);
        // v0.37 第四笔：本家也在这一位上不声明，而它的因由与上一行不同档——GBase 8s 是"这一族的语句清单里
        // 没有 <c>COMMENT ON</c> 那一行"（走 Core 的跳过并 WARN），本家是 <c>COMMENT</c> 这个词在本族
        // <em>是语法错</em>【实测·F5 ∶ 2828】，而 <c>sqlite_master</c> 回显的是引擎规范化的 DDL 文本
        // 【实测·3013】——连"注释落在哪儿"这一问都没有对象。这一位对交集已经不构成新信息（上一笔就只剩两家），
        // 记在这里是为了让"谁没接"那份名单在十五本上仍然完整。
        Assert.DoesNotContain(DbCapabilities.Comment_On_Column, sqlite);

        // 下面这一段是 v0.37 第四笔才有的：与上面那一段同一个形状，只是这一次被扣的是<em>交集里最后一颗</em>。
        // <c>Alter_Column_Precision</c> 此前十四本每本都有（它是那"三位地基"里活到最后的两位之一），本家交回空：
        // <c>SqliteCodeFirst.EmitAlterColumnPrecision</c> 抛，而抛的理由不是"还没实现"，是这一问在本家
        // <b>没有对象</b>——库里没有列型状那一档，声明名只是一颗 affinity 配方，宽度与标度从不参与存储
        // 【实测·4001 ∶ 4002】。于是交集从两位掉到一位，而文件头那句"多一颗涨名单、少一颗削地基"
        // 第二次兑现。<b>剩下来的那一颗必须连同它的读法窄化一起记</b>：<c>Schemas</c> 在七家都声明，
        // 而它在 GBase 8s 那一族指"所有者"、在本家指"挂进来的另一个文件"，所以这里钉的只是
        // "每一家都<em>有</em>一颗限定名发射口"，不是"跨族建表迁移可以按这一位发"。
        Assert.DoesNotContain(DbCapabilities.Alter_Column_Precision, sqlite);
        Assert.DoesNotContain(DbCapabilities.Alter_Column_Precision, shared);
    }

    [Fact]
    public void Guid_columns_map_to_the_native_type_rather_than_char36()
    {
        // Guid_Raw16 那一行的见证本体。§4 原文给金仓那一档写的是 Guid_Char36，这一版把它翻案成"有原生 uuid"，
        // 所以这一条断言就是那次翻案的落地形态：文本里出现 char(36) 才是错的。
        var provider = Kingbase.Provider(SqlServer.NoDriverFactory.Instance);
        var id = Kingbase.Orm().Schemas.Contract<SessionToken>().Columns
            .Single(c => c.MemberName == nameof(SessionToken.Id));

        Assert.Equal("UUID", provider.Types.GetStoreType(id));
        Assert.DoesNotContain("36", provider.Types.GetStoreType(id), StringComparison.Ordinal);
    }

    [Fact]
    public void Guid_columns_map_to_char36_in_a_lineage_without_a_native_uuid()
    {
        // 翻案的另一半：Guid_Char36 这一位从 v0.10 立项起一直没有 provider 声明过，到第三族才第一次
        // 有代码走到它。上面那条 UUID 断言与这一条<em>必须同时存在</em>，否则"这一位从来没被用过"
        // 会被读成"这一位是错的"，而它是 §9.3 记的 Oracle 现实（无原生 uuid，只有 CHAR(36) 那一档）。
        var provider = Dameng.Provider(SqlServer.NoDriverFactory.Instance);
        var id = Dameng.Orm().Schemas.Contract<SessionToken>().Columns
            .Single(c => c.MemberName == nameof(SessionToken.Id));

        Assert.Equal("CHAR(36)", provider.Types.GetStoreType(id));
    }

    [Fact]
    public void A_lineage_with_a_binary_option_can_still_pick_the_text_one()
    {
        // 这一条与上面那条<em>同名而异事</em>，所以不并成一个 Theory：达梦写 CHAR(36) 是因为没有别的选择，
        // 而这一家有 BINARY(16) 却仍然选 CHAR(36) —— 那颗选择要连接串参数（GuidFormat=BinaryUuid）与字节
        // 置换一致才成立，而 SPI 里没有"连接串参数改变方言假设"那一格（§14，与金仓的 Oracle 兼容模式同一颗）。
        // 于是"同一个类型名"在两家是两条不同的判据：并成一个 Theory 就只剩"两家的产出都是 CHAR(36)"这一句统计。
        var provider = MySql.Provider(SqlServer.NoDriverFactory.Instance);
        var id = MySql.Orm().Schemas.Contract<SessionToken>().Columns
            .Single(c => c.MemberName == nameof(SessionToken.Id));

        Assert.Equal("CHAR(36)", provider.Types.GetStoreType(id));

        // 而且这一位对它<em>不</em>是"库里没有那一型"：把 Guid_Raw16 补进这一家的掩码会得到一位
        // 取决于现场连接串的声明，那正是 §4 那句判据不许的形状。
        Assert.False(provider.Capabilities.HasFlag(DbCapabilities.Guid_Raw16));
    }

    static IEnumerable<DbCapabilities> Flags(string name)
    {
        var declared = Ledgers.Single(ledger => ledger.Name == name).Provider().Capabilities;

        return AllBits().Where(bit => declared.HasFlag(bit));
    }

    /// <summary>枚举里的全部位。<see cref="Enum.GetValues{T}"/> 在这里是一次冷路径调用（本类是测试），
    /// 所以这一份不需要 provider 侧那样手抄一份数组。</summary>
    static IEnumerable<DbCapabilities> AllBits()
        => Enum.GetValues<DbCapabilities>().Where(static bit => bit != DbCapabilities.None);
}
