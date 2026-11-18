# 03 · 插入数据

> 本篇解决：单行与批量插入、批量分块规则、自增键回填、批量高速层（装载通道）、一句 upsert（InsertOrUpdate）。

## 1. 两个入口

```csharp
// 单行
await orm.Insert(new Order { CustomerName = "甲", Amount = 12.5m }).ExecuteAffrowsAsync();

// 批量：一条多值 INSERT … VALUES，按参数预算自动分块
await orm.InsertRange(orders).ExecuteAffrowsAsync();
```

入口不建 lambda、零反射税：列清单来自契约，值来自编译好的委托。链上值审计钩子（`Aop.AuditValue`，见 [11 篇](11-filters-behaviors-aop.md)）在这两条路上触发（`OperationKind = "insert"`）。

## 2. 出口方法

| 出口 | 返回 | 说明 |
| --- | --- | --- |
| `ExecuteAffrowsAsync(ct)` | `Task<int>` | 受影响行数 |
| `ExecuteIdentityAsync(ct)` | `Task<long>` | 单行插入并回填自增键（SQL Server `OUTPUT` / PG `RETURNING` / MySQL 等语句后回读） |
| `ExecuteReturnKeysAsync(ct)` | `Task<List<T>>` | 批量插入、按行回填自增键，交回**同一批实体** |
| `ExecuteBulkAsync(ct)` | `Task<BulkReport>` | 走批量高速层（需先 `WithBulk`，见 §5） |

诊断口：`ToSql()` / `ToSql(out parameters)` / `ToLogSql()`（内联字面量版）/ `Explain()` → `InsertPlan` / `ExplainKeys()` / `ExplainBulk()`。

## 3. 分块是看得见的事实

`InsertPlan` 给出全部计划：`Chunks`（每块的行区间与语句）、`RowsPerStatement`、`ParameterBudget`、`Reason`。块宽 = 每句参数数压在各库参数预算内：

| 家族 | 参数预算 |
| --- | --- |
| SQL Server | 2100 |
| PostgreSQL 系 | 32767 |
| MySQL 系 | 65535 |
| Oracle 系 / 虚谷 / GBase 8s | 保守值 100 |

例：600 行 × 7 列 → SQL Server 切 3 块、MySQL 一块、达梦 50 块。`plan.Reason` 会自陈预算与"**跨块不是原子的**"——批量插入多块之间没有事务保证，需要原子时显式开事务（`orm.BeginTransaction()`，见 [10 篇](10-transactions.md)）。

**贴着预算上限不等于划算的块宽**：大批量建议用 `WithBulk` 的 `rowsPerRoundTrip` 显式压块（只压不抬）。

## 4. 自增键回填

三条出口在各家能走到哪一档，取决于两颗键策略槽位：

| 家族 | 批量 `OUTPUT`/`RETURNING` | 语句后回读 | 要键时的形态 |
| --- | --- | --- | --- |
| SQL Server | ✔ | ✘ | `OUTPUT inserted.[Id]` |
| PostgreSQL 系（含金仓等六叶） | ✔ | ✘ | 句尾 `RETURNING` |
| MySQL 系 / 虚谷 / GBase 8s | ✘ | ✔ | 语句后补 `SELECT LAST_INSERT_ID()` 一类 |
| **Oracle 系（Oracle/达梦/神通）** | ✘ | ✘ | **两个口子都关，直接抛**（消息写明，不发一条"看着像回填"的语句） |

Oracle 系上要自增键的替代路：插入后按业务键回查，或改用客户端生成主键（`Guid` / 雪花号，见 [02 篇 §3](02-entity-mapping.md#3-主键与自增)）。

## 5. 批量高速层：`WithBulk`

文本层之外另有一条不走 SQL 文本的装载通道：

```csharp
var report = await orm.InsertRange(rows)
    .WithBulk(BulkMethod.CopyChannel, rowsPerRoundTrip: 50_000)
    .ExecuteBulkAsync();

Console.WriteLine($"{report.Method} / {report.RoundTrips} 往返 / {report.RowCount} 行 / 降级={report.Degraded}");
```

量多时不必点名——`ExecuteBulkAsync` 不调 `WithBulk` 时默认取 `Auto`，它自己按行数换层：

```csharp
// 按文本层分块要发的语句数 ≥ 8（BulkLadder.AutoCopyRoundTrips）才换装载通道；
// 量少自动留在多值语句（L3），两档都带着 BulkReport。
var report = await orm.InsertRange(rows).ExecuteBulkAsync();
```

**只要行数、不要报告时，默认出口也自动选层**（v11.0.0 起）：

```csharp
// 没订阅 CudBuilding / ParameterBinding 时与上面同一颗 Auto 判据：量多走装载通道、量少多值语句。
// 订阅了任一颗改写钩子就钉回文本层（钩子要看得见整句/逐颗参数）。
var affected = await orm.InsertRange(rows).ExecuteAffrowsAsync();
```

```csharp
public enum BulkMethod { Auto, CopyChannel, MultiValue, SingleRow }        // 在 QuickSql.Providers 命名空间
public enum FallbackPolicy { DegradeAndLog, DegradeSilently, Never }
```

要点：

- **`Auto` 带行数门槛（量多自动走批量）**：它按"不切要付多少"——文本层分块后的往返次数——决定换不换通道，达到 `BulkLadder.AutoCopyRoundTrips`（8）才走装载通道，量少自动落在多值语句。门槛随参数预算自适应（SQL Server 30 列宽表 442 行到数、7 列表 1891 行；预算大的 MySQL/PG 天然少切）；显式点名 `CopyChannel` 不看门槛，3 行也走通道。要问"这一批会走哪层"用 `ExplainBulk()`（不连库、纯函数）。**`ExecuteAffrowsAsync` 不调 `WithBulk` 时走钩子感知 Auto**（v11.0.0 改判）：无 `CudBuilding`/`ParameterBinding` 订阅时与 `ExecuteBulkAsync` 同判据，订阅任一颗钉回文本层；点名 `MultiValue/SingleRow` 也钉文本。
- **通道今天接了三家**（SQL Server 的 `SqlBulkCopy`、MySQL 血统的 `MySqlBulkCopy`、PG 血统的二进制 COPY）；其余各家请求 `CopyChannel` 会整批退回文本层多值（L3）——**退层不是错误**：`BulkReport.Degraded = true` + `Reason` 说明原因，`Aop.BulkDegraded` 钩子只在真降级时派发。要"绝不静默降级"就 `FallbackPolicy.Never`。
- **一列不合格整批退层**（不会把那一列省掉；省一列=值不进库）。SQL Server 装载通道收 14 种 CLR 形状，`char`/`DateOnly`/`TimeOnly`/无符号整数/`object` 不在内。
- **装载通道那一档不产 SQL 文本**：`Explain()` 永远给文本层语句，层的问题问 `ExplainBulk()`；`CudBuilding`/`ParameterBinding` 改写钩子不适用（默认出口订阅了它们就钉文本层，`ExecuteBulkAsync` 点名要层时由调用方负责）；`AuditValue` 照旧生效（取值段跑）、`CommandTerminating` 闸门照旧过；**回填口 `ExecuteReturnKeysAsync`/`ExecuteIdentityAsync` 永不借道**。
- **`ExecuteAffrowsAsync` 拒绝点名高速层**（它返回 int 装不下"走了哪一层/几次往返"）：`WithBulk(Auto/CopyChannel)` 后调它会当场抛，要报告用 `ExecuteBulkAsync`；不调 `WithBulk` 的默认档自己选层（见上）。
- `rowsPerRoundTrip` 只压不抬：文本层是每块行数，装载通道是每次递交几行。**有环境事务时整串递交挂在同一条事务上**；没有事务时块与块之间不原子。
- 通道少报行数当场抛 `QuickSqlException`（拦"悄悄交出假数"，不回滚）。
- **列宽默认口径**：默认每句参数数 = 预算九成（如 SQL Server 恒 1890 颗）。实测"每句耗时 vs 每句颗数"非线性，超大批量自己压 `rowsPerRoundTrip`。

`WithBulk` 只长在插入链上；更新/删除的批量走 [04 篇](04-update.md)的 `WithBulk(BulkWriteMethod…)`。

## 6. 一句 upsert：`InsertOrUpdate`

按主键没有就插、有就改：

```csharp
await orm.InsertOrUpdate<Order>()
    .SetSource(new Order { Id = 7, CustomerName = "甲", Amount = 99m })
    .ExecuteAffrowsAsync();

await orm.InsertOrUpdate<InventoryItem>()
    .SetSource(batch)                    // 也可以给一批
    .WithBatchTransaction()              // 整批原子（可选）
    .ExecuteAffrowsAsync();
```

- 数据走 `SetSource(实体|集合)` 而不是入口形参；写入清单 = 可写列 + 主键列，主键既进插入清单又是冲突判定键。
- **发射形态由 provider 定、调用方不选**：MySQL 系 `ON DUPLICATE KEY UPDATE`；PG 系与 SQLite `ON CONFLICT … DO UPDATE`；SQL Server / Oracle 系 / 虚谷 `MERGE INTO`；GBase 8s 发"每行一对 `UPDATE`+`INSERT`"两条语句。
- 入口零反射税；全局过滤器在场时**当场拒**（不是叠进 WHERE）；主键是自增列而调用方没带键时也当场拒（冲突判定出不来）。
- 回填档不在这条链上——要键走 `Insert` 的 `ExecuteReturnKeysAsync`。
- 诊断：`Explain()` → `BulkWritePlan`、`ToSql()`、`ToLogSql()`。

---

[← 上一篇：02 实体定义](02-entity-mapping.md) · [下一篇：04 更新数据 →](04-update.md)
