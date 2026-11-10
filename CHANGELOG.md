# 更新日志

QuickSql 各版本的变更，倒序排列（最新在上）。

> **口径**
> - 版本号跟 .NET 主版本走（.NET 10 → 10.x）；Quick 五族同批发布时对齐全主版本。破坏性改动升次版本号（10.0.0 → 10.1.0），缺陷修复升修订号。
> - 「未发布」收 main 已合入、尚未定版的改动；定版时改节名为版本号并标「待发布」（`Directory.Build.props` 的 `<Version>` 同步就位），上包那天把「待发布」换成日期。
> - 分档为 新增 / 变更 / 修复 / 移除；标 **破坏性** 的改动，消费方升级时要动代码。
> - 版本日期取该版本在 NuGet 上可还原之日，不是提交日。

## 10.1.0 — 待发布

### 移除

- **破坏性** rowversion 并发令牌整条路下线（2026-11-10 合并，`57d127c`）：`ConcurrencyMode.RowVersion`、`ColumnContract.StoreFillsValue`、`OUTPUT` / `RETURNING` 回读并发列、SQL Server 的 rowversion 类型映射一并删除。并发令牌只剩两族——整型 `Version`（ORM 在 `SET` 里发 `Version = Version + 1`）与 `AppSetsValue`（应用给新值，随行写出去）。非整型且非 `AppSetsValue` 的令牌在 `EntityTracker` 构造时抛出，不再静默少写一列。
  - `ColumnContract.ValueFromStore` 结论位随之收窄为「= 自增」；唯一读者 `NullableInStore` 现在只由自增决定。
  - SQL Server 侧 `SqlServerSchemaReader` / `SqlServerTypes` / `SqlServerConcurrency` / `SqlServerChangeGuard` 去掉 rowversion 分支；结构同步不再把 timestamp 列当库给值列。
  - `docs/quicksql/02-entity-mapping.md`、`04-update.md`、`notes.md` 的并发令牌口径同步收敛；`notes.md` 历史条目里涉及 rowversion / StoreFillsValue / 「值归库给」的，一律以本条为准。
  - 方言册与活库册删掉 rowversion 用例，golden 产物 `golden/sqlserver/ddl-inventory-item.sql` 移除。

## 10.0.0 — 2026-11-09

首个公开版本：QuickSql 从内部单仓拆为独立仓库（GitHub `mr-yangli/QuickSql`、Gitee `mr_yangli/quick-sql`），按 .NET 主版本定版，本族 25 颗包同批上 NuGet（`QuickSql.Abstractions`、`QuickSql.Core`、`QuickSql.Generators` + 22 颗 provider 工程）。下面列的是发布内容面，不是相对上一版的 diff——10.0.0 之前没有公开版本。

### 新增

- **实体与映射**：特性标注 / 约定 / Fluent API 三口；主键与自增、并发令牌、导航与外键、契约读取。源生成器在编译期生成实体契约，物化零反射，AOT 干净注册路径。
- **查询**：条件、排序、投影（含行号 `QuickFx.RowNumber`）、Distinct、分页与计数、存在性探针、聚合、流式读取、子查询、`WithSql` 表源替换。
- **联表与导航**：`LeftJoin` / `InnerJoin`、`Include` / `ThenInclude`、`IncludeMany`（主查询一字不改，按层补一句 `WHERE fk IN (父主键)`，超参数预算自动分块）、引用导航自动联表、拆分查询。
- **分组聚合**：`GroupBy` / `Having`、组内聚合、复合键、分组计数。
- **写入**：单行与批量插入、自增键回填、批量分块、SQL Server 装载通道、`InsertOrUpdate` upsert；按条件更新、按实体稀疏更新、按实体整行批量更新、软删除与「谁删的」扩展。
- **工作单元**：`DbContext` / `DbSet` / `ChangeTracker` / `SaveChanges`——脏列判定、级联（新增 / 摘行 / 到位）、乐观锁、共享会话。
- **事务**：四种形态、保存点嵌套、跨集群规则、无事务库的降级口径。
- **横切**：全局查询过滤器、行为注册、AOP 钩子（审计 / 日志 / 改写 / 取消）、缓存口。
- **分表分库与读写分离**：`[SplitTable]` 六档分片、`[DataSource]` 具名集群、主从路由、写后读、拒绝规则。
- **结构同步（CodeFirst）**：三档策略、逐列比形状、建表 / 补列 / 改列、分表建片、DBA 脚本模式。
- **原生 SQL**：`orm.Ado` 字符串口全家、参数规则、行装配。
- **19 家数据库 provider**：SQL Server、PostgreSQL、MySQL、Oracle、SQLite、达梦、人大金仓、openGauss、GaussDB、瀚高、GBase 8c / 8s、DuckDB、ClickHouse、TDengine、TiDB、OceanBase、神通、虚谷（`MySqlBase` / `OracleBase` / `PostgreSqlBase` 三颗共享基座不计入家数）。
- **诊断**：`ToSql()` 离线产 SQL、`Explain`、异常对照表。
- **测试面**：离线方言册覆盖 19 家 golden SQL 加跨 provider 契约对账；活库路径覆盖 SQL Server、虚谷、SQLite、PostgreSQL、MySQL、OceanBase 六家。
