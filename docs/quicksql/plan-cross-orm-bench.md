# 与 FreeSql / SqlSugar 的三方性能对照（SQLite 装置 2026-11-11 实测；§7 活库四家写侧 2026-11-18 扩展）

> 树册（`plan-tree-query.md` §13）把"整行物化比两家慢 1.2–1.8×，属另一本账"这句话留在了那一节末尾。
> 本册就是把那本账开出来：树册量的是"读树这一件事"，本册量 §12.8 的通用形状 —— 简单查询、宽行物化、
> 三表联接投影、空结果固定开销、SQL 构建、批量插入。装置、纪律与树册 §13 同源，规模对齐离线套件
> （`quicksql-orm-design.md` §12.8）。**本册是对照研究，不是门禁**：不进 `--gate`、不加门禁行、退出码不动；
> 读数落 `docs/evidence/live-three-way-bench-1.log`（第二场，全量）与 `live-three-way-bench-2.log`（第一场，对表用）。

## 1. 装置

- **一颗 SQLite 临时文件库**，四张表（`BenchOrder` 8 列 / `BenchCustomer` / `BenchItem` / `BenchWideRow` 30 列），
  DDL 与种子全部原始 ADO，四家都不参与建库灌数 —— 四臂之差只剩"ORM 自己怎么做"。行数 1001（与离线套件同档）。
- **四臂 + 地板**：QuickSql（基准臂，G1 生成路径，走 `DriverRegistry` 完整驱动解析链）、
  Dapper 2.1.35（业界锚点）、FreeSql 3.5.311、SqlSugarCore 5.1.4.221，外加裸 ADO 地板。
  版本与树册 §13 同款（那天刚量过，不换尺子）。
- **每臂每次执行先过守门**（树册"节点数递归数出来"的翻译版）：行数必须等于种入行数，
  外加 decimal / Guid / DateTime / 可空 / 枚举 / bool 的抽查值，按种子公式逐格对 —— 装置与守门共用同一批公式，
  谁把"读歪了"装成正常数字，行数那格先红。**装置自己红过三场**（种子参数错位、裸 ADO 臂没开连接、
  守门枚举期望写错），三场都是装置红、没有任何一臂被冤枉 —— 守门该有的行为。
- **跑法**：BenchmarkDotNet 0.15.8（MemoryDiagnoser，每形状一个类、QuickSql 臂 baseline），
  `--filter "*Live*"` 全量两场，中位对表。`Program.cs` 的
  `ConfigOptions.DisableOptimizationsValidator` 是为这一片开的（理由见 §2 第 4 条）。

## 2. 口径（六条，都写死）

1. **FreeSql 臂的驱动不同源**：FreeSql.Provider.Sqlite 底下是 System.Data.SQLite.Core 1.0.119（自带原生
   sqlite3），QuickSql / Dapper / SqlSugar / 裸 ADO 四臂共用 Microsoft.Data.Sqlite 10.0.11。这是它家用户
   在 SQLite 上的真实用法，不改配置硬凑。
2. **时间列只读同场内的比值**（§14 那条"活库时间列会翻符号"的纪律）；分配列（MemoryDiagnoser 的托管字节）
   是确定量，跨场可读。
3. **SqlBuild 的文本形状不同**：QuickSql 发参数占位（@p0 族），FreeSql/SqlSugar 的渲染把值内联进文本。
   这一格量的是"把一条链变成文本要花多少 CPU"，不是同一段工作的三种实现。
4. **FreeSql 3.5.311 与 SqlSugarCore 5.1.4.221 的官方 NuGet 是 Debug 构建**（两家 DLL 都带
   `DebuggableAttribute=263`，`DisableOptimizations`，JIT 对它们关优化；Dapper 实测正常 Release，逐个验过）。
   所以**这两臂的时间列偏悲观**，读比值时要带着这一条。BDN 的优化校验器会拒绝执行引用 Debug 程序集的
   基准，而本对照要量的恰恰是"用户从 NuGet 拿到的那一份" —— 校验放行，边界记在这里。
   （树册 §13 那一天的读数同样带着这条，只是当时没人翻开那颗 DLL。）
   **【2026-11-17 复核确认 + 决议】** 读元数据 blob 实测：FreeSql/SqlSugar/FreeSql.Provider.Sqlite = `07-01-00-00`（263，
   DisableOptimizations=True），Dapper/QuickSql.Core = `02-00-00-00`（2，False）；且 3.5.311 / 5.1.4.221 **就是 NuGet 最新版**，
   无更新 Release 可升。决议（用户拍板）：**保留官方包原样 + 显著标注**——不源码 Release 构建、不改二进制；
   各臂 BDN Description 就地标 Release/Debug，LiveWriteBench/LiveArms 类注释显著写明这颗税。要量"各家设计峰值"
   需源码 Release 重编两臂（§5 余项挂着），本套不做。
5. **QuickSql 写臂列口径（2026-11-17 改判）**：原默认装配口整型 Id 按自增约定省列（29 列），另三家显式 Id（30 列），
   列差 1/30。为公平消去这颗混淆，Live 写臂的 G1 注册表传 `integralPrimaryKeyAutoIncrement: false`，QuickSql 也显式插 Id
   （30 列齐平）。实测：改后 QuickSql 时间几乎不变（47.00→47.18 ms，噪声档），分配 +0.6 MB（多一列 Id 的每格对象）——
   即默认省 Id 那列反而略省，列差不是 QuickSql 慢的主项（主项是 SQLite 命名参数平方价 + 写侧无 ArrayPool）。
   只动 Live 臂；离线套件与门禁走 Harness.Generated（默认 true），29 列口径不变。
6. **MetadataLookup 与 ChangeTrackerDetect10k 不进本册**：前者是本家的元数据税、后者量的变更跟踪只有
   本家有 —— 没有对照物的形状进了表就是一行无意义的数。SelectSimpleDto 也不进（离线片已发现投影委托
   没有指纹缓存，账还清之前量它，量的是已知欠账）。

## 3. 读数（第二场，全量；第一场中位在括号里当对表）

读侧形状：1001 行，全部 `ORDER BY Id`（主键索引顺扫，五臂 SQL 工作量一致）。

| 形状（1001 行） | QuickSql | Dapper | FreeSql | SqlSugar | 裸 ADO |
| --- | --- | --- | --- | --- | --- |
| SelectSimple（8 列整行） | 1.478 ms / 433 KB | 0.91× / 1.04× | 1.09× / 0.89× | 1.54× / 1.14× | 0.78× / 0.69× |
| MaterializeWide（30 列整行） | 4.579 ms / 1213 KB | 0.91× / 1.13× | 0.93× / 0.89× | 1.74× / 1.29× | 0.73× / 0.76× |
| SelectJoin3（三表联接投影） | 629.5 µs / 146 KB | 0.95× / 0.97× | **2.83× / 5.71×** | 1.34× / 1.06× | 0.76× / 0.54× |
| EmptyFixed（空结果固定开销） | 74.3 µs / 6.56 KB | 0.79× / 0.25× | 1.57× / 1.60× | 2.08× / 4.36× | 0.75× / 0.17× |
| SqlBuild100（100 步 Where → 文本） | 106.7 µs / 249 KB | — | **11.26× / 1.69×** | **10.65× / 4.59×** | — |
| BulkInsert（1000 行 × 29/30 列） | 604.2 ms / 14.2 MB | **0.03×** / 0.84× | 0.41× / 1.62× | 0.05× / 0.91× | — |

每行字节（÷1001）：SelectSimple —— QuickSql 433 B、Dapper 452 B、FreeSql 385 B、SqlSugar 493 B、裸 ADO 298 B；
MaterializeWide —— 1212 / 1364 / 1079 / 1566 / 919 B。
SqlBuild 截距（1 步）：1.50 µs / 17.29 µs / 71.67 µs —— 折成每步斜率约 1.06 / 11.96 / 10.75 µs。
两场中位差：全表 ≤ 6%（MaterializeWide −6.2%，其余 ≤ 3.5%），比值序无一格翻转。

## 4. 读数四条

1. **整行物化那本账，还清了大半**：树册 §13 那个"比 FreeSql 慢 1.2–1.8×"的平表读数，在通用形状上是
   **SelectSimple 1.09×、MaterializeWide 0.93×** —— 8 列与 30 列两档 QuickSql 与 Dapper/FreeSql 同档
   （±10% 以内），每行字节 433 B / 1212 B，对裸 ADO 地板（298 B / 919 B）付 1.45× / 1.32× 的物化税。
   树册那次被读成"QuickSql 整行物化贵"的形状，在四列小表 + Debug-构建的对照上放大了；30 列真形状上，
   本家的物化与业界锚点打平。
2. **联接 + 投影是 FreeSql 的痛格，不是本家的**：SelectJoin3 上 FreeSql 2.83× 时间、5.71× 分配
   （834 KB，每行 833 B —— 三列投影交出了整行三倍的字），SqlSugar 1.34× 贴着本家。本家三表联接投影
   离 Dapper 0.95×、离地板 0.76×。
3. **SQL 构建是本家的强格，且斜率不是截率**：SqlBuild100 上本家 106.7 µs，两家都过 1.1 ms（10–11×）。
   截距（1.50 µs vs 17.3 / 71.7 µs）只占小头，斜率才是主项：每步 Where 本家 ~1.06 µs，两家 ~11–12 µs。
   口径差（内联 vs 占位）改不了这个量级 —— 占位文本更短，本家这一格的文本工作量反而更小。
4. **写侧是本家的真欠账，形状可推、源码可证**：BulkInsert 604 ms，比"Dapper 逐行 + 一条事务"慢 **32×**，
   比 SqlSugar 慢 21×。形状两条，都在自家源码里：
   - **每块只有 3 行**：SQLite provider 的 `Bulk => null`（SqliteProvider.cs），InsertEngine 落到
     `ConservativeParameterBudget = 100`，`RowsPerStatement(100, 29) = 3`（InsertEngine.cs:268 +
     StatementEmitter.cs:21）→ 1000 行 = **334 条多值语句**。
   - **引擎不包事务**：InsertEngine 全文没有事务处理，334 条语句每条各自 autocommit —— 本机每提交
     ~1.8 ms（604 ms ÷ 334），主项是日志 fsync 而不是 ORM。
   从时间形状反推，FreeSql/SqlSugar 的默认写路径都自带事务包裹（否则按本机提交价，1000 行进不了
   247 / 28 ms）。离线门禁第 6 行钉的是构建分配（8.8 MB），量不到"驱动侧怎么交付" —— 这一格
   30 条基准里只有活库装置看得见。

   **【同日改判：这笔欠账当天就还了】** 两刀一起落，落地后活库重跑（同装置同形状）：

   | 预算档（每块行数） | 语句数 | 实测 |
   | --- | --- | --- |
   | 改前：100（3 行） | 334，各自 autocommit | 604.2 ms |
   | 32766（1016 行） | 1 | 2467 ms —— **更慢**！ |
   | 2100（65 行） | 16 | 189.3 ms |
   | **333（10 行）** | 100 | **47.3 ms / 13.7 MB**（终跑） |

   曲线闭合在**每语句成本 ≈ 250 µs 固定 + 3 ns × 参数数²**：Microsoft.Data.Sqlite 把命名参数映射进
   语句是按名查找，参数数平方级——树册 §14 那条曲线在写侧 VALUES 臂同样成立（32766 颗是合法上限
   但合法不等于快）。极点落在"每条语句 ~100 条、~290 颗参数"，SQLite 的 `SqliteBulk` 因此报
   **333**（每语句 ≤300 颗），并把 IN 列表臂按住在 100（`InListParameterBudget` 覆写——同一颗平方级
   绑定价在谓词链上更疼，树册那条 45.9→164 ms 的曲线就是依据）。事务那一半由 InsertEngine 的多块
   收口包（多块一条事务、失败整批回滚），终跑 **47.3 ms**：比改前快 **12.8×**，与 Dapper 收敛到 2.5×、
   反超 FreeSql 5.3×；离 SqlSugar 还差 1.7×，那 25 ms 是每语句 ~250 µs 的编排固定成本——"多块共享
   一条已准备命令"是下一刀，记 §14。语义那半同时变了：多块中途失败从"半批已提交"改为"整批回滚"
   （§12.4 第 8 项改判，集成册新增原子性用例钉住，反验：撤掉事务收口当场红）。

## 5. 口径纪律的代价（装置红过的三场）

第一轮装置就量错过三场，全被守门拦下而不是被当成读数收进册子：种子参数错位（`@p1..@p30` 只绑了一半，
SQLite 抛"Must add values"）、裸 ADO 臂没开连接就发命令（Dapper 会自己开所以只有地板臂炸）、
守门的枚举期望写错（`9 % 4 = 1 = Placed`，断言写成了 `Paid`，五臂同红 —— 装置红而不是某家红，
正是守门该有的形状）。修这三处没有动任何一家 ORM 的调用方式。

## 6. 欠账与后续

- **产品（已还）**：SQLite 写侧两条（ParameterBudget + 引擎事务）——本册 §4.4 的前后读数就是兑现凭证；
  门禁第 6 行的基线随之重定型并重钉（`obj/logs/bulk-tx-gate-1/2.log`）。留下的半格：**多块共享一条已准备
  命令**（每语句 ~250 µs 编排固定成本 → SqlSugar 的 28 ms 那一线），记 §14。
- **对照**：FreeSql/SqlSugar 若出 Release 构建的官方包（或自源码 Release 重编两臂）重跑一场，
  第 4 条读数里的"偏悲观"边界就可以撤；FreeSql 联接格 2.83× 里有多少是 Debug 交付的，重编后才答得清。
- **活库（已兑现，见 §7）**：本装置原本只有本地 SQLite。2026-11-18 补了 SqlServer / MySQL / PG 三家活库写侧对照
  （§7.2）+ PG 5 万行缩放（§7.3），并把 SQLite 写侧再推一档（编号 ?N，§7.1）。§3–§4 那张表仍是本地 SQLite 形状，
  跨库绝对值别搬（驱动曲线不同源）；写侧 333 那个极点已被编号 ?N + 预算 150 取代（§7.1）。OceanBase / 虚谷活库待补。
- **补形状**：`SelectSimpleDto` 等投影指纹缓存落地后补进读侧（装置已留好位置，加一个类即可）。

## 7. 活库四家写侧对照（2026-11-18，兑现 §6 那句"活库另开一跑"）

§3–§4 那张表是本地 SQLite 的形状，§6 早已警告"别读成六家通用"。本节补 SqlServer / MySQL / PG 三家活库
（连接取仓库根 `quicksql-test.*.connection.local.txt`，本机凭据不入库），并把 SQLite 写侧再推一档。
装置：`benchmarks/QuickSql.Benchmarks/Live/LiveWriteBench{SqlServer,MySql,PostgreSql}.cs`（各五臂：
QuickSql Auto / QuickSql 显式 L1 / Dapper 逐行 / FreeSql / SqlSugar）+ `LiveWriteBenchPostgreSqlMass.cs`（5 万行）。
新增包 `FreeSql.Provider.{SqlServer,MySqlConnector,PostgreSQL}` + `Microsoft.Data.SqlClient` / `MySqlConnector` / `Npgsql`。

### 7.1 SQLite 写侧再改判：编号 ?N 取代 §4.4 的预算 333

§4.4 终跑 47.3 ms（预算 333、具名 @p）不是终点。Microsoft.Data.Sqlite 按名查找映射参数是平方价，
**编号位置参数 ?N**（`ParameterName="?N"` 绑位置 N，驱动认；匿名 ? 配无名参被拒）常数小一档。
`SqliteDialect.ParameterNamer` 把批量插入发射口（`InsertEngine.EmitChunk`）发名改「?+(下标+1)」，
`SqliteBulk.ParameterBudget` 333→**150**（编号消大半平方价后极点左移）。活库扫描（1000×30，BDN）：
333→29.9 ms、200→25.97、**150→23.73**、100→24.96，150 时间分配双优。再补**编号名串缓存**（?N 跨块复用，
静态 `string?[1024]`）→ **22.38 ms / 12.22 MB**（§4.4 的 47.3→22.38，累计 ≈2.1×）。
编号 ?N 是 SQLite 专属（其驱动平方价）；PG/MySQL/SqlServer 驱动按名绑定 O(N)（预算 32767/65535/2100 为证）不需编号。

### 7.2 四家 1000 行 × 30 列对照（Id 自增各家省 Id 29 列，short job）

| 库 | QuickSql 最优臂 | Dapper 逐行 | SqlSugar | FreeSql | 备注 |
| --- | --- | --- | --- | --- | --- |
| SQLite | 22.4 ms（编号?N L3） | **18.3 ms** | 27.7 ms | 247 ms | 无装载通道，Dapper 小参数集避平方最快 |
| SqlServer | **115 ms**（L1 SqlBulkCopy） | 961 ms | 175 ms | 4180 ms | L3 文本（ExecuteAffrowsAsync）慢 34×（3875 ms） |
| MySQL | **76.6 ms**（L1 LOAD DATA） | 637 ms | Insertable 125 ms；Fastest 91.8 ms/4.85MB（丢1行999） | 199 ms | **QuickSql L1 反超 SqlSugar Fastest**（快17%+更省+插满1000）；Auto L3 也快（106 ms） |
| PG | **36.2 ms**（L1 二进制 COPY） | 5495 ms | 108 ms | 257 ms | Auto L3 多值 98 ms 也赢两家 |
| 虚谷 | **411.6 ms**（L3 多值，Bulk=null） | 522 ms | 1087 ms / **4.3 GB** | —（缺驱动包） | 无装载通道、保守预算 100→334 块仍最快最省；SqlSugar 虚谷分配爆炸（185×） |

分配（QuickSql 最优臂，确定量）：SQLite 12.22 MB / SqlServer L1 5.12 MB / MySQL L1 4.69 MB / PG L1 3.89 MB。
**QuickSql L1 装载通道相对普通 Insertable/逐行是结构性优势**；但相对 SqlSugar **Fastest**（对等 bulk copy）则 SqlServer/PG 被反超、
MySQL 反超对方——公平性改判与 QuickSql L1 通道开销欠账见 §7.4（别把本表读成"QuickSql 全场最快"）。

### 7.3 PG 5 万行缩放（short job）

| 臂 | Mean | Allocated | Alloc Ratio |
| --- | --- | --- | --- |
| QuickSql L1 二进制 COPY | **2.23 s** | **194.75 MB** | 1.00 |
| QuickSql Auto（门槛上自动选 L1） | 2.47 s | 194.93 MB | 1.00 |
| QuickSql L3 多值 VALUES | 6.66 s | 698.8 MB | 3.58 |
| SqlSugar | 4.79 s | 1048 MB | 5.38 |
| FreeSql | 13.35 s | 1519 MB | 7.80 |

Auto 门槛设计生效：1000 行（门槛下）选 L3 多值省往返，5 万行（门槛上）自动选 L1 COPY。
**分配差距随规模拉大**（L1 vs FreeSql 从 1000 行 ~4× 到 5 万行 7.8×）；L3 文本 vs L1 COPY 量越大越亏（3× 时间 / 3.6× 分配）。

### 7.4 贯穿规律与口径

- **【2026-11-18 公平性改判：SqlSugar Fastest 反超 QuickSql L1】** 先前"QuickSql L1 最快最省"只在 SqlSugar 用普通 `Insertable`
  时成立——**不公平**。SqlSugar 有对等的 `Fastest<T>().BulkCopyAsync()`（同走 SqlBulkCopy / Npgsql COPY / MySqlBulkCopy）。
  补 Fastest 臂后（`IFastest<T>.BulkCopyAsync(List<T>)`，装置传 `new List<WideRow>(_rows)`）：
  **SqlServer SqlSugar Fastest 47.6 ms / 3.46 MB 胜 QuickSql L1 62 ms（中位）/ 5.12 MB；PG SqlSugar Fastest 31.7 ms / 2.93 MB
  胜 QuickSql L1 COPY ~36 ms / 3.89 MB**。SqlSugar 是 Debug 构建（JIT 关优化）仍赢 → **QuickSql L1 通道有真实开销**
  （CopyChannel 反射绑定 + 额外缓冲，分配多 ~1.66 MB、时间慢 ~30%）。这是 QuickSql 性能欠账：L1 通道实现不如 SqlSugar
  bulk copy 包装精简，优化方向＝减中间缓冲/反射、对齐 SqlBulkCopy 最优选项（BatchSize/KeepIdentity），记 §14。
  MySQL Fastest **实插 999 行（真丢 1 行，COUNT(*) 实证，非计数 quirk）**：AllowLoadLocalInfile=true 已在（EnsureConnectionFlag），
  根因是 SqlSugar `MySqlFastBuilder` 非 byte[] 列时走 CSV + LOAD DATA LOCAL + 临时表路径（非 MySqlBulkCopy.WriteToServer），该 CSV 路径丢行——SqlSugar bug，装置按实际 COUNT 守门故 NA。虚谷 Fastest 不支持（FastestProvider._BulkCopy 抛）。
  读数 `obj/logs/net11-bdn-{sqlserver-fastest2,PostgreSql-fastest}.log`。
- **【2026-11-18 优化兑现：QuickSql L1 反超 SqlSugar Fastest】** 定位开销主项＝`InsertEngine.CopyRequestFor` 为取归一值
  每格 `binder.CreateParameter(...).Value` 白 new 一颗 AdoParameter record（1000×30=30000 颗≈1.4 MB）。修：`IValueBinder` 加
  `NormalizeValue(value,hint)` 默认接口方法（fallback CreateParameter.Value），SqlServer/PG/MySQL 覆写直取各自廉价 Normalize；
  CopyRequestFor 改用 NormalizeValue（免 record + 免 c{column} 假名数组）；SqlServerBulkCopy.Table 加 `MinimumCapacity`+`BeginLoadData`。
  **战果（BDN short job，分配确定量）**：SqlServer L1 5.12→**3.36 MB**（< SqlSugar Fastest 3.45）；PG L1 COPY 3.89→**2.33 MB**、
  ~36→**22.4 ms**（胜 SqlSugar Fastest 36.9 ms/2.93 MB，时间分配双赢）。**SqlServer 时间长 job 稳测已结清（同日）**：
  BDN LongRun（3 启动 × 100 迭代）争议两臂 L1 均值 60.26 ms/中位 54.73 vs Fastest 70.38/60.97，99.9% CI 各 ±4.4 ms
  不重叠；反序复核轮 68.59/59.13 对 59.24/54.67 同向——时间（约 13%）分配双赢、与臂序无关。
  验证：方言册 1657 绿、门禁 5/19 钉不变、SqlServer/PG/MySQL 集成各 283/0 失败。读数 `obj/logs/net11-bdn-{sqlserver-opt1,pg-opt1,sqlserver-long1,sqlserver-long2-fastest,sqlserver-long2-qsl1}.log`。
- **L1 装载通道仍是结构性优势（相对普通 Insertable/逐行）**：SqlBulkCopy / LOAD DATA LOCAL / 二进制 COPY 在大批量下
  吞吐 + 分配远胜文本层与逐行（PG 5 万行 L1 vs FreeSql 7.8× 分配），规模越大越显著——只是相对 SqlSugar Fastest 还有精简空间。
- **API 选择决定成败（SqlServer UX 欠账，2026-11-18 同日兑现）**：原欠账＝`InsertRange().ExecuteAffrowsAsync()` 默认走 L3 文本
  （SqlServer 慢 34×），要快须 `ExecuteBulkAsync()`。**改判落地：默认出口改钩子感知 Auto**（用户裁决推翻「钉死文本层」在册裁决）——
  无 `CudBuilding`/`ParameterBinding` 订阅且量过门槛时默认自动走 L1，订阅任一颗钉回文本层；修剪税上签名、SaveChanges 走内部文本出口不背税。
  全文 `plan-auto-bulk.md` §6 与 CHANGELOG 11.0.0 变更档首条；判据 `BulkPathTests` 四颗新钉 + 五族集成册。
- **名缓存 provider 中性赢面**：默认发号 `prefix+"p"+count` 每格造三颗串（中间串 + ToString + 拼接），
  `AtParameterNames` 共享 '@pN' 分段惰性缓存（SqlServer/MySQL/PG 同 '@' 前缀）。门禁 SqlServer 形状 BulkInsert/BulkBuild
  各 −2.5 MB（8768728→6264128）；离线探针 N=30000 省 2.14 MB（缓存稳态 0 B）。MySQL/PG 块内名不重复、收益跨迭代。
- **ArrayPool 不适用**：取值矩阵 Cells 缓存在 `InsertEngine._preparation`（实例生命周期、三出口共用），非 transient，
  池化需 disposal 还池点（无）。真正分配杠杆是每格 ParameterValue/AdoParameter record→struct 或引擎复用（§14）。
- **口径边界**：① FreeSql/SqlSugar 官方最新 NuGet 是 Debug 构建（`DebuggableAttribute=263` DisableOptimizations，
  元数据探针实测；Dapper/QuickSql=2 Release），时间列偏悲观，各臂 Description 就地标注。② 远程库（SqlServer/MySQL/PG）
  网络延迟主导时间，short job 噪声大（Error 可达 Mean 量级），**排名可信、精确比值待长 job**；分配是确定量。
  ③ 各家 Id 自增省列 29 列齐平（SqlServer/MySQL/PG），SQLite 片显式 Id 30 列（口径不同源，别跨库搬绝对值）。
- **读数日志**：`obj/logs/net11-bdn-{sqlserver-l1,mysql-2,pg-3,pg-mass}.log`、`net11-sqlite-numbered-budget-sweep.log`、
  `net11-shared-namecache-evidence.log`。
- **装置事故备忘**：MySQL fixture 曾被误覆盖（未跟踪草稿），用 ilspycmd 反编译旧 dll 恢复原版（Auto 13763 行门槛断言 +
  显式 CopyChannel + ProbeArms 验真），仅修一处潜伏 bug（`GuidFormat=char36` 下探针 `GetString`→`GetGuid`）。
  PG fixture 修 SqlSugar `PgSqlIsAutoToLower=false`（保 quoted 混合大小写表名）。
- **虚谷注记（`LiveWriteBenchXugu.cs`，QuickSql+Dapper+SqlSugar 三臂）**：**XuguBulk 欠账已兑（2026-11-18）**：
  `XuguProvider.Bulk` 交 `XuguBulk`——仍无装载通道（驱动 XG* 导出面二进制扫描无装载类）。**双数落账**：
  安全界 **2048**（净室探针一尝试一连接：2048 颗收 / 2049 颗拒且服务端断连接；3 列/1 列两梯边界同点 ⇒ 限颗数不限文本长；
  边界内 2046/1842 颗带主键全行读回错绑 0）；性能终值 **300**（BDN A/B：LongRun 100=409.1ms±8.5 vs 2048=702.7ms±5.5，
  宽块时间输 1.72×；medium 扫描 200=340.5±19.8 / 300=346.6±12.6 / 600=381.0±14.6 / 1000=452.8±12.9，谷底平在 200–300，
  取 300＝9 行/块、1000 行 112 块；LongRun 定案腿 `net11-bdn-xugu-long-300.log`）。
  机制＝服务端每语句代价随颗数超线性（sweep：87 颗≈0.4ms / 435 颗 6ms / 899 颗 11ms / 1827 颗 39ms）——
  与 SQLite 驱动侧平方价同课不同源，**硬上限≠性能最优**；sweep 暖连接长事务低估宽块绝对值，只定机制不定终值。
  另实测两颗与预算无关的库侧界限：单事务改行上限 10000（max_trans_modify，E14024）、同步 ExecuteNonQuery 成功也交回 0
  （行数以读回为准；同连接读回看不见刚提交的写，读回须另开连接）——探针方法学三颗记 notes.md。
  旧读数备查：兜底 100 时代 QuickSql 411.6 ms / 23.22 MB 仍最快最省，赢 Dapper 逐行 522 ms（1.27×）、SqlSugar 1087 ms（2.65×）。**SqlSugar 虚谷分配爆炸：1000 行 4.3 GB（185× QuickSql，Gen0/1/2 全 14 万+）**——
  其虚谷 bulk 路径极度浪费（疑逐行重反射）+ Debug 税。SqlSugar 虚谷臂经文档（donet5.com typeId=2640）启用：包 **SqlSugar.XuguCoreNew**
  （交付程序集名 SqlSugar.XuguCore.dll）+ `InstanceFactory.CustomAssemblies = { Assembly.Load("SqlSugar.XuguCore") }` 绕过按名探测。
  FreeSql 虚谷臂驱动兼容未验暂不入。踩坑：残留非自增 ID 旧表（Create 先 DROP 再 SyncStructure）、虚谷参数前缀 `:`（非 @，Dapper 裸 SQL 须 :Name1）、
  标识符折大写（列名 CODE1 等）。驱动 XuguClient 厂商 4.0.0.8 经 DriverRegistry 解析（drivers/ 拷贝，编译期不引用）。
  集成册虚谷红皆环境非代码：TreePathTests 万节点撞夹具表 24907 残留行（历史跑 finally 撞 E14024 没删掉），
  重置后 14/14 绿（万节点批的内容对拍即由其读回断言承担）；端口耗尽 flake 在全量跑里轮换落点
  （LeakPathTests 或 ThreadConcurrencyPathTests，谁赶上端口耗尽谁红），单跑复绿（终值跑 287 格 1 环境红，
  并发册单跑 4/4 绿；v41-xugu300-int-xugu.log）。
