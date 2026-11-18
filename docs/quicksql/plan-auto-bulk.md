# InsertRange 量多自动走批量：`Auto` 档的行数门槛（2026-11-18 落地）

> 诉求一句话：SqlServer 上 `InsertRange` 量多的时候自动走批量插入，调用点不想每次记得 `WithBulk(CopyChannel)`。
> 现状是 `BulkMethod.Auto`（`ExecuteBulkAsync` 的默认档）只要通道在场就无条件走 L1 —— 3 行也走 SqlBulkCopy；
> 而默认出口 `ExecuteAffrowsAsync` 被 `IInsert.WithBulk` 抬头那条在册裁决钉死在文本层。
> **本册只动 `Auto`，不动默认出口**——裁决、§12.8 门禁、修剪税标注三样全不过手，理由见 §1。
> 证据：`docs/evidence/v41-auto-bulk-1.log`（离线册选层问答）、`v41-auto-bulk-2.log`（SQL Server 真库全套 + stamp）。

## 1. 裁决边界（为什么只动 Auto）

- **默认出口不动**：`ExecuteAffrowsAsync` 若默认取阶梯最高层，SQL Server 上"接了 SqlBulkCopy"这一件事会让
  每一句都不再产生 SQL 文本，`CudBuilding`（软删改写、影子表）与 `ParameterBinding` 两个改写钩子集体失效，
  而返回 int 装不下"走了哪一层"——裁决全文在 `IInsert.WithBulk` 抬头。**这笔账与行数无关**：行数门槛救不了它，
  所以默认出口照旧钉死文本层。
- **门禁不翻层**：§12.8 第 6 行①（1000 行 × 30 列批量插入、稳态 0 分配）驱动的是
  `orm.InsertRange(rows).ExecuteAffrowsAsync()`（`Gates.cs` 写臂）——默认出口不动，这一格的形状（L3 多值、
  ArrayPool）就不变，无需重钉。
- **修剪税不动**：`ExecuteAffrowsAsync` 的签名上没有 `RequiresUnreferencedCode` 是在册的特征（"这条出口不可能
  走到装载通道"），本册不给它加。
- 调用姿势：`orm.InsertRange(rows).ExecuteBulkAsync()`（默认即 `Auto`）——量多自动走装载通道、量少自动落在
  多值语句，两档都带着 `BulkReport`。

## 2. 阈值口径：文本层往返次数 ≥ 8

`BulkLadder.AutoCopyRoundTrips = 8`（放 `QuickSql.Abstractions/Providers/`，先例是 `ParameterBudgets.Conservative`；
Core 没有 InternalsVisibleTo 给测试册，测试要引用同一颗）。

口径选**文本层的往返次数**而不是行数：量的是"不切要付多少"——每块行数由参数预算推出来（`§12.3` 同一条公式
`预算 × 9/10 ÷ 列数`），宽窄表自适应；显式点名 `CopyChannel` 的不算请求特征（3 行也走通道）；门槛没到不算降级、
不留痕（`Auto` 没承诺过任何一层，措辞写"取第一档满足条件的层"）。

推导表（切换行数 = 7 × 每块行数 + 1）：

| 家 | 预算 | 30 列每块行数 | 30 列切层行数 | 7 列每块行数 | 7 列切层行数 |
|---|---|---|---|---|---|
| SQL Server | 2100 | 63 | **442**（1000 行 = 16 往返 → 切） | 270 | **1891** |
| PostgreSQL 系 | 32767 | 983 | 6882 | 3807 | 26650 |
| MySQL 系 | 65535 | 1966 | 13763 | 7621 | 53348 |
| SQLite | （无通道） | — | 与本数无关 | — | — |

取 8 的依据：多值语句稳态走 ArrayPool、单事务包裹（§12.4 第 8 条那笔），几条语句以内它比通道省——通道要整批
`DataTable` 加逐格装箱（§12.1 代价清单）；语句数上到个数量级、递交次数成了主导项，一次递交才把这笔钱赢回来。

## 3. 改动清单

- `src/QuickSql.Abstractions/Providers/BulkLadder.cs`（新）：`AutoCopyRoundTrips = 8` + 推导抬头。
- `src/QuickSql.Core/Write/InsertEngine.cs` `Decide()`：`refusal is null` 分支内加门槛分岔——`Auto` 且
  `Trips(preparation, MultiValue) < 8` 落 `MultiValue`（非降级，Reason 算账："通道在场，但 N 行 × C 列按文本层
  只要 T 次往返（门槛 8）"）；达标则走通道，Reason 记"按文本层要 T 次往返（≥ 8）：量多"。
  `CopyChannel` 显式请求原样不动；refusal→Never 抛/降级、rows==0 各分支原样不动。
- 文档注释：`Bulk.cs` 的 `Auto` 成员、`IInsert.cs` 的 `WithBulk` / `ExecuteBulkAsync` 抬头各补一句（默认出口
  裁决文字原样）。
- 测试：离线册 `BulkLayerTests` 原 `Auto_takes_the_copy_channel_the_moment_a_provider_offers_one`（3 行）按新
  语义拆成两则（`rowsPerRoundTrip: 1` 压宽把往返次数顶到门槛数，达标 → 通道；3 行 → 文本层 + Reason 算账 +
  `Absent("退回")`）；`A_missing_channel_makes_Never_throw…` 顺带钉住"显式点名不看门槛"。集成册 `BulkPathTests`
  新增 `Auto_switches_to_the_channel_only_when_the_text_layer_hits_the_threshold`：50 行压 10 行/块 = 5 往返
  → 文本层落库；80 行压 10 行/块 = 8 往返 → 通道落库，读回行数两档都对。
- 用户册 `docs/quicksql/03-insert.md` §5：新增 Auto 门槛要点与用法；设计册 `§12.2` 追加 v11.0.0 段
  （草图那格 `ExecuteAffrowsAsync // Auto` 的裁决不松动，写明）；`CHANGELOG.md` 11.0.0 段一条。

## 4. 读数与证据

- **离线**（`v41-auto-bulk-1.log`）：`BulkLayerTests` 18/18 绿（xUnit v3 in-process，net11.0，0.349s）。
  达标档断言：`Requested=Auto / Method=CopyChannel / 8 行 8 往返 / Reason 带"按文本层要 8 次往返（≥ 8）"`；
  未达标档断言：`Method=MultiValue / 非降级 / Reason 带"装载通道在场，但 3 行 × 7 列按文本层只要 1 次往返
  （门槛 8）" / 无"退回"`。
- **真库**（`v41-auto-bulk-2.log`，stamp：`192.168.0.102,1433 / LightDB`）：全套 283 格 0 红、24 跳（各家的
  "本家没有对象"闸门），`BulkPathTests` 12 格 0 红 2 跳（两跳是 MySQL `AllowLoadLocalInfile` 闸，本家不适用）。
  新格在 SQL Server 上两档各落库 50/80 行全数读回。
- **时间读数未做**：本册量的是选层判据与两档落库，不是耗时——换层的收益（16 次往返 → 1 次递交）在
  §12.1 的代价模型里，真要计时属于 Live 写臂的活（[[cross-orm-bench-findings]] 那台装置），另开一格。
- **五家对照（2026-11-18，`BulkPathTests` 整册逐家直跑，`v41-auto-bulk-{mysql,postgresql,oceanbase,xugu,sqlite}-1.log`）**：
  stamp 是手写的——`scripts/run-integration.ps1` 的探针段用 `SqlConnectionStringBuilder`，只吃 SQL Server 串，
  非 SQL Server 家族按同一纪律手写（不打印口令）。SqlServer 那家走脚本（全套 283 格）+ 直跑各一。

  | 家 | 实例（stamp） | 结果 | 装载通道 | Auto 门槛新格 |
  |---|---|---|---|---|
  | SQL Server | LightDB @192.168.0.102 | 10 跑全绿 + 2 跳（infile 两格是 MySQL 家专属） | `SqlBulkCopy` | ✓ 跑绿 |
  | **MySQL** | TestDB @192.168.0.228 | **12 格全跑全绿 0 跳**（含 infile 两格本家在场） | `MySqlBulkCopy`（LOAD DATA LOCAL，串里自动补 `AllowLoadLocalInfile=true`，服务器侧也放行了） | ✓ 跑绿 |
  | PostgreSQL 系 | quicksql_it @61.157.37.243 | 10 跑全绿 + 2 跳（同 infile 两格） | 二进制 COPY | ✓ 跑绿 |
  | OceanBase | @192.168.0.228 | 12 全跳——**库不可达**（`Unable to connect to any of the specified MySQL hosts`） | 源码上血统继承 `MySqlProviderBase`、有通道（`OceanBaseProvider : MySqlProviderBase`），本日未实证 | 跳 |
  | 虚谷 Xugu | TESTDB @192.168.0.228 | 12 全跳——**可达但没接通道**（`ResolveCopyChannel()` 为 null，闸门理由即"本家没有对象"） | 无 | 跳（退路由离线册钉住） |
  | SQLite | scratch | 12 全跳——无通道 | 无（`SqliteProvider.Bulk` 只有 L3 预算） | 跳 |

  门槛自适应在真库上的形状由此可读：MySQL 预算 65535 → 30 列每块 1966 行、**13763 行才到数**；PG 32767 →
  983 行/块、**6882 行**——比 SQL Server（442 行）天然少切，与 §2 的推导表逐字一致。MySQL 的 LOAD DATA
  通道有部署侧前提（服务器 `local_infile`）+ 客户端放行，门槛更高正是这一家的正确形状。

## 5. 余项

- ~~MySQL/PG 血统没有专属实测~~ **已补**（2026-11-18，见 §4 五家对照表）：MySQL 12 格全跑全绿（infile 两格本家在场），PG 10 跑全绿。剩 OceanBase（库离线，源码血统有通道）与 TiDB 等 MySQL 协议血统待库可达时同法跑一遍；虚谷等没接通道的家由离线册钉住退路，不欠真库账。
- 阈值 8 是常数不是旋钮：真现场要调，走 `WithBulk(CopyChannel)` 显式点名（不看门槛）。要不要升成
  `IBulkStrategy` 槽位（per-provider 覆写）看后续有没有真实诉求，不预置。

## 6. 改判附录（2026-11-18 同日，用户裁决）：默认出口改钩子感知 Auto

§1 那三条「不动」里的第一条被推翻：**`ExecuteAffrowsAsync` 不调 `WithBulk` 时不再钉死文本层**，改走
钩子感知 Auto——没订阅 `CudBuilding` / `ParameterBinding` 时与 `ExecuteBulkAsync` 的 Auto 同判据
（同一颗 `Trips` + 同一颗门槛），订阅任一颗就钉回文本层。起因是跨 ORM 对照的 UX 欠账
（`plan-cross-orm-bench.md` §7.4：SqlServer 上 `InsertRange().ExecuteAffrowsAsync()` 比 L1 慢约 34×，
而 SqlSugar Insertable 默认就走 SqlBulkCopy——调用点不该每次记得换出口）。§1 的另两条照旧：

- **门禁不翻层**：§12.8 写臂改显式 `WithBulk(BulkMethod.MultiValue)` 点名文本档——量哪一层不再交给
  「假驱动能不能解析出装载类」这种巧合；19 钉复跑一致、0 回归。
- **修剪税**：默认出口从此够得到通道，`IInsert.ExecuteAffrowsAsync` 与实现加 RUC/RDC（税按方法收）；
  `Storageable.ExecuteInsertAsync` 镜像上传一颗。**会话写路径不背税**：跟踪器插入档改直调内部
  `ExecuteAffrowsTextAsync`——它的 Operation 报告要回放 plan 的语句文本，通道没有文本可回放。
- 钩子闸只 watch 两位改写钩子：`AuditValue` 在取值段跑、L1 读同一份 Cells（不被绕开）；
  `CommandTerminating` 闸门 `CopyAsync` 照常过。量少（往返 < 门槛）不建决策直接落文本，
  公开出口是非 async 路由壳——默认文本档的稳态分配与改判前逐字节同（门禁 +72 B 回归曾显形于
  双层 async 状态机，收壳后消除）。
- 判据：`BulkPathTests` 四颗新钉（门槛下全家族文本 / 门槛上无钩子一颗 `BULK COPY INTO` 合成描述符 /
  两位钩子在场各钉文本且钩子真 fired，批取门槛行数保证「本来会走通道」）；五族集成册全量
  （`obj/logs/v41-hookauto-int-*.log`）。观察方影响与消费方破坏性面记 CHANGELOG 11.0.0 变更档首条。
