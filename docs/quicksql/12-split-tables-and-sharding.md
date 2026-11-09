# 12 · 分表分库与读写分离

> 本篇解决：一张实体铺在多张同构表（`[SplitTable]`）、实体住哪座库（`[DataSource]`）、主从路由与写后读、跨片/跨库的拒绝规则。这两个能力共用同一条路由，是"一格的两侧"。

## 1. 分表：一张实体，多张同构表

> 做的是**一个库里的多张表**。多库那格由 §3 的具名集群承接，两者正交。

### 1.1 声明：一条模板 + 一条算法

```csharp
[Table("AccessLog_{year}{month}")]                          // 表名写的是模板，不是一张能建的表
[SplitTable(SplitKind.Month, nameof(OccurredAt))]           // 分片键 = 属性名，档 = 月
public class AccessLog { … }

[Table("TicketRow_{shard}")]
[SplitTable(SplitKind.Hash, nameof(TenantId), Buckets = 4)] // 哈希档要有桶数
public class TicketRow { … }
```

- 六档：`Day` / `Week` / `Month` / `Season` / `Year` / `Hash`；占位符只认 `{year}` `{month}` `{day}` `{shard}` 四颗（每颗至多一次；本档要的必须在，不要的不许有；认不出的花括号启动期抛）。
- **启动期判据**：分片键必须是一列；模板要配得上档；哈希档 ≥2 桶；**主键不许自增**（跨片唯一库给不了——用 `Guid` 等客户端生成主键，或这一张不分：`[QuickColumn(IsIdentity = TriState.No)]`）。
- 片名换算只有一份实现：时间档先下整到周期起点再填模板（`Week` 档记周一）；哈希档整数折无符号取模、字符串走 FNV-1a（**不用 `GetHashCode()`**，它随进程随机化）。

### 1.2 读侧：选片范围

```csharp
// 时间档：给范围（自动换算出覆盖的片）
var q = orm.Select<AccessLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));

// 哈希档：给桶号（重复自动去重）
var rows = orm.Select<TicketRow>().SplitTables(0, 1).ToListAsync();

var page = await q.OrderBy(o => o.OccurredAt)
    .ToPagedAsync(1, 2, new PagedOptions(CountMethod.SkipTotal));   // 跨片翻页常配 SkipTotal
```

- 多片 = 一句 `UNION ALL`，外层统一选列；**单片时不包派生表**，语句与不分表的实体逐字同形。
- 剪枝是真的少问：范围只覆盖二月时，一月与三月的片不出现在文本里。
- 序与分页只长在外层。
- **跨片明确拒绝**（都是抛，不是给错数）：没选片就读；跨片 `JOIN`/`Include`；跨片聚合/分组/`HAVING`（`COUNT(*)` 打在 UNION ALL 外面是对的，`AVG` 与"分完组再数"不是包一层能救的）。出路写在消息里：范围收到一片（普通聚合）或 `CountMethod.SkipTotal`（只要"有没有下一页"）。

### 1.3 写侧：一片一句

```csharp
await orm.InsertRange(logs).ExecuteAffrowsAsync();        // 跨三月的六行 = 三句，各带各的片名；行按分片键值自动归片

var upd = orm.Update<AccessLog>().SplitTable(new DateTime(2026, 2, 9))   // 改与删钉一片，且必须在链首
            .Set(o => o.Status, 299).Where(o => o.Status == 404);
await orm.Delete<AccessLog>().SplitTable(new DateTime(2026, 2, 9)).Where(o => o.Status == 500).ExecuteAffrowsAsync();
```

- **分片键为空的那一行当场抛、一条都不写**（不"当作今天"送进今天那张表）。
- 分片键来自实体值（按实体写的 `orm.Update(实体)`/`orm.Delete(实体)` 按这只实体自己的分片键换算片）。
- 批量装载通道（[03 篇](03-insert.md)）在分表上是一片一次递交。

### 1.4 建片：范围只有业务知道

```csharp
var shards = await orm.CreateSplitTablesAsync<AccessLog>(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
var script = orm.SplitTablesScript<TicketRow>(0, 1, 2, 3);     // 不连库，交回 DDL 文本
// SplitTablesResult { Requested, Created, AlreadyThere, IndexesNotCreated }
```

- 逐张先问存在再发 DDL，同一范围跑第二趟是"新建 0 张"（幂等）。
- **索引不随片建**（`IndexesNotCreated` 恒有内容）：索引名在 N 张片上会撞，要自己按片点名建。
- 一次选片上限 512 片；建片是 DDL，走 [13 篇](13-schema-sync.md)同一道环境闸门（`ForceSchemaSync` 或 Development）。
- 整库同步遇到分表模板是**跳过并记名**（`SkippedSplitTemplates`），不建带花括号的表。

### 1.5 与变更跟踪互斥

分表实体可以被**读进**会话（`Attach`），但 `SaveChanges` 在开事务前逐行问"这行会不会被写出去"，会写就整批拒并指路（身份映射按主键记账，同一个主键在分表里可占两片——"跨片的一行由什么指认"未裁）。链式侧不受影响。

## 2. 读写分离：主库写、从库轮询读

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = mainPrimary,             // 缺省集群 main 的主库
    Replicas = [mainReplica1, mainReplica2],    // main 的从库，按声明顺序轮询
});
```

- **写永远主库；读默认从库**。从库按声明顺序轮询（无权重、无延迟探测、无存活检查——一颗死掉的从库会被轮到自己超时）。
- 会话内一个从库**钉住一颗**不逐句换（同会话两次读互相看得见顺序）；轮询落在会话之间。

### 强制主库的三格

1. **事务内**（一切语句主库，不换连接）；
2. **同一条执行流上这座集群刚写过**（写后读；这一格只在**会话**上成立——裸链没有会话可问）；
3. **链上显式 `FromPrimary()`**（对称的 `FromReplica()` 压得过写后读——显式表态压过保守档）。

```csharp
var fresh = await orm.Select<Order>().FromPrimary().Where(o => o.Id == id).FirstOrDefaultAsync();
```

## 3. 分库：具名集群

```csharp
[Table("SalesTotals")]
[DataSource("report")]          // 类上点名住哪座集群；不写即在 main
public class SalesTotal { … }

var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = mainPrimary,
    Replicas = [mainReplica],
    Clusters = [new ClusterConfig("report", reportPrimary, [reportReplica])],
});
```

- `ClusterConfig(名字, 主库串, 从库串[])`：一个名字、一颗主库、若干从库。加一座库就是添一条。
- 实体声明与集群清单**只在第一次碰那张表时对账**（注册表惰性）：名字对不上当场抛，消息带在册清单。要启动期挡，就把每张实体碰一遍（`_ = orm.Select<T>().ToSql()`）再读 `orm.Sources.ClusterNames`。
- 启动期判据：具名集群里不许出现 `main`；主库串不许空；从库清单里的空白串不许静默少一颗（症状是"轮询少一台"）。
- 一座集群一颗 provider——"主库 SQL Server、从库虚谷"那种异构拓扑要两只 orm。

## 4. 路由拒绝规则（五格，全在动手之前）

| 那一格 | 出路 |
| --- | --- |
| 跨集群 `JOIN`/`Include`（链式） | 把两个实体声明到同一座集群；或用 `IncludeMany`（另发 SELECT 缝合，跨集群天然成立） |
| 事务里点 `FromReplica()` | 在事务之外读 |
| 事务在位，语句要落另一座集群 | 按集群分别开事务、各自提交 |
| 一批 `SaveChanges` 跨两座集群 | 按集群分两次保存 |
| 一场事务开着，去另一座集群再开一场 | 同上（同座嵌套才是保存点） |

## 5. 观察路由：`Aop.Executed` 的 `Route`

```csharp
orm.Aop.Executed = e => Console.WriteLine($"{e.Route.Cluster}/{e.Route.Role} ← {e.Route.Reason}");
// Cluster: main/report  Role: Primary/Replica
// Reason: WritePrimary | ChainPrimary | ChainReplica | InsideTransaction | ReadAfterWrite | ReadNoReplicaConfigured | ReadReplica
```

"这一句实际走了哪座库"是订阅出来的事实，不用翻库猜。声明侧读数在 `orm.Sources`（`ClusterNames` / `Knows(cluster)` / `ReplicaCount(cluster)` / `PrimaryConnectionString(cluster)`）。

---

[← 上一篇：11 全局过滤器与 AOP](11-filters-behaviors-aop.md) · [下一篇：13 结构同步 →](13-schema-sync.md)
