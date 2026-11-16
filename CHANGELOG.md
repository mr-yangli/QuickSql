# 更新日志

QuickSql 各版本的变更，倒序排列（最新在上）。

> **口径**
> - 版本号跟 .NET 主版本走（.NET 10 → 10.x）；Quick 五族同批发布时对齐全主版本。破坏性改动升次版本号（10.0.0 → 10.1.0），缺陷修复升修订号。
> - 「未发布」收 main 已合入、尚未定版的改动；定版时改节名为版本号并标「待发布」（`Directory.Build.props` 的 `<Version>` 同步就位），上包那天把「待发布」换成日期。
> - 分档为 新增 / 变更 / 修复 / 移除；标 **破坏性** 的改动，消费方升级时要动代码。
> - 版本日期取该版本在 NuGet 上可还原之日，不是提交日。

## 10.1.0 — 待发布

### 新增

- **多目标 `net10.0;net11.0`**（2026-11-11）：`QuickSql` 与全部 provider 同包双 TFM，消费方按宿主框架自动还原对应资产，`net11.0` 消费方不用再退回 `net10.0` 资产。生成器仍 `netstandard2.0`，基准层照旧。两档都是现代 .NET，§2「不做 netstandard2.0 / .NET Framework / .NET 6/8 兼容层」的向下口径不变，设计册 §2/§16.2 的单一 TFM 决议同步翻档。

- **树形查询三颗口**：`IQuery<T>.Descendants(父键, 锚点…)` 取子集闭包、`.Ancestors(父键, 节点…)` 取祖先面包屑、`.ToTreeAsync(父键)` 把一片条件嵌成森林。交回扁闭包（`TreeRow<T>`：值 + `Depth` + `ParentKey` + `Path`）或嵌套森林（`TreeNode<T>`），实体不被污染——不要求有 `Children` 属性、不需要新注解，父键就是 lambda 点名的那颗本实体列（与主键不同型的在接链处拒，理由见册子 §2）。
  - **19 家 provider 通用**：闭包在服务器内存里算，发射面只用一句普通 `SELECT`（取片或按层 `IN`），没有递归 CTE、没有 `CONNECT BY`、没有任何库端树语。方言层与 provider 层零改动，AST 没加 CTE 槽。
  - 深度上限（缺省 32）与已访问键集两道拦环：库里成环时句子数与行数都有界、每颗键只出现一次，环上的行升为根不静默丢。链上 `Where` 与全局过滤器落到每一句，代价明写——被筛掉的中间节点会让它下面整段读不到。
  - 逐层爬的句数账与万行宽树税单：`docs/quicksql/plan-tree-query.md` §3，读数在 `docs/evidence/tree-memory-closure-1.log`（六家活库各 14 颗；离线 25 颗 + 接缝与约定优先级 3 颗，全量离线册 1628 绿）。
- **`IBulkStrategy.InListParameterBudget`**（带默认实现的新槽，十三家零改动）：IN 列表臂的参数预算与 VALUES 臂（`ParameterBudget`）分家。两条臂在驱动侧的形状不同——VALUES 的参数按序 `Add` 没有隐藏单价，IN/谓词链在 SQLite 族驱动上是"命名参数按名查找 ⇒ 整批平方级"——SQLite 是第一颗覆写者（VALUES 333、IN 按住 100）。常数 `ParameterBudgets.Conservative`（=100）落进 Abstractions，内核的 `StatementEmitter.ConservativeParameterBudget` 改指它。

### 变更

- **`IEntitySchemaRegistry` 多一颗按列序的非泛型读口** `IBoxedEntityAccessor Accessors(Type clrType)`（新接口 `IBoxedEntityAccessor`，G1 生成的 schema 与 G2 反射 schema 都实现）。树形闭包的键/父键读数走这一颗，热路径不再按名字反射读成员。**对外部自行实现 `IEntitySchemaRegistry` 的宿主是破坏性变更**（多一个必须实现的成员）；本仓内置的两颗注册表与两侧 provider 均已接上。其余七处运行期成员读（`IncludeMany`/拆分加载回填、`DbSet` 两条补载路、`Storageable` 写侧）暂时留在原路上，各自单独一笔。
- **SQLite 批量插入的交付方式改判，1000 行 × 30 列 604 → 47.3 ms**：`SqliteProvider.Bulk` 不再是 null——新 `SqliteBulk` 报 VALUES 臂预算 333（每语句 ≤300 颗参数；三档实测 1016/65/10 行每块 → 2467/189/50.5 ms，极点在 Microsoft.Data.Sqlite 命名参数映射的平方价上，读数 `docs/quicksql/plan-cross-orm-bench.md` §4.4）。多块由引擎包一条事务整批提交——**行为变化：多块插入中途失败，此前是"半批已提交、半批丢失"，现在是整批回滚**（原子性用例 `BulkInsertAtomicityTests` 钉住，撤收口反验红；未声明事务位的 provider 仍 autocommit 直发，环境事务在场自动并入，`InsertPlan.Reason` 按 `TransactionCapability.Declared` 分支措辞——没声明事务位的家（ClickHouse、TDengine）文案写明"跨块不是原子的"）。**删除侧这次不收口**：`DeleteRange` 多块、`Storageable` 的删除桶与存在性对账仍是逐块 autocommit，欠账一格记在 §12.4，与插入侧收口分批走。读侧 IN 臂同步按住不动（`InListParameterBudget` 覆写），树闭包形状零变化。
- **AST 三条基改 `closed`（C# 15，2026-11-11）**：`AstNode` ∶ `TableSource` ∶ `SqlExpression` 从 `abstract` 翻成 `closed`——仓外程序集直接派生当场编译红，实测 `error CS9382: "ProbeExpr": 无法使用其他程序集中的闭合类型"SqlExpression"作为基类型`（四颗探针 `: AstNode` ∶ `: TableSource(Ref)` ∶ `: SqlExpression` ∶ `: OverriddenSource(Ref)` × `net10.0`/`net11.0` 两档全红，两档各四条）；「方言不派生 AST」从家规纪律变成编译器执行。全部叶子都在仓内（`SqlExpression` 14 颗 ∶ `TableSource` 7 颗 ∶ `AstNode` 直叶另有 `JoinClause`/`Projection`/`OrderExpression`/`SetClause` 4 颗），19 家 provider 与全仓零外部派生，方言层零改动。net10.0 档靠 `QuickSql.Abstractions` 新增的内部 polyfill `System.Runtime.CompilerServices.IsClosedTypeAttribute`（与 `IsByRefLikeAttribute` 同一手法的元数据补齐），net11.0 档用运行时自带，两档产出的元数据对消费方同形。
  - **破坏性**：`QueryModel.Source` 的类型从 AST 的 `TableSource?` 收归模型层自有 closed union `OverriddenSource?`（`RawTextSource` ∶ `SourceSetOps`，均住 `QuickSql.Query.Model`）；`WithSql()` 写进这一格的从 AST 的 `SourceRawText` 换成同形的 `RawTextSource`（`Ref`/`Sql`/`Parameters` 三槽一字不差），编译口在发射时刻才把它端成 AST 的 `SourceRawText`。消费方若曾读 `model.Source`（`is SourceRawText` 或把它当 `TableSource` 用）要跟着换形；`SourceSetOps` 的名字与命名空间不变，基座从 AST 挪进 union。
  - 顺带的正收益：`QueryCompiler` 的 FROM 分派改穷尽 switch——union 两形各一臂、没有 default 臂可漏，`QueryModelPrinter` 同形；模型层不再借 AST 的基座放自己的形状。

### 修复

- **`[QuickColumn(IsIdentity = TriState.No)]` 不再被命名约定盖掉**：主键成员名正好是 `Id`/`{实体名}Id` 时，`ApplyKeyConvention` 会把"整型主键即自增"的约定 `||` 回契约，把显式声明的"不归库给号"抹平——症状是 `INSERT` 的列清单里没有主键列、号由库给，而分表门禁的报错文案正好叫用户写这一句声明。现在约定只在没有任何一层表过态时添自增；显式 `[Key]` 的实体路径不变，源生成侧与反射侧共用同一个收尾器所以两边同修。读数与反验记在 `docs/quicksql/plan-tree-query.md` §9。
- **树形闭包的键表按内容比，不再按引用比**（审出的 P1）：`byte[]` 作主键/父键时，每一趟物化都新建一只数组，默认比较器（引用相等）会让每个节点都"找不到父"——交回的是一张扁表，而库里一句 SQL 都不报错。`StructuralComparer` 现覆盖 `byte[]`（含复合键里的 binary 分量），`Path` 里的二进制段改打十六进制。判据由 SQLite 与 PostgreSQL 两家守；SQL Server/MySQL/OceanBase/虚谷 上该用例显式 skip——`byte[]` 列带 `Length` 仍发 `varbinary(max)`/`BLOB`（不能当索引键），那是 CodeFirst 类型映射的一笔，与树形无关，理由写进了用例。
- **树形链的锚点数组在接链那一刻拷贝，不再与调用方共享**（审出的 P3）：`Descendants`/`Ancestors` 的 `params object[]` 本体是调用方现场造的那一只，此前 `TreeRequest.Anchors` 按引用存它——接链之后就地改数组内容，这条链的规范文本、`StableKey` 与执行期的 IN 臂都会跟着变，不可变纪律在锚点这一格漏了。现在接链时拷贝进模型，`IQuery<T>` 两颗口的 XML 文档同步写明；离线用例 `Anchors_are_copied_at_chain_time_not_shared_with_the_caller` 钉住。
- **虚谷 `InsertOrUpdate` 撞可空时刻列当场报 `[E34501]/[E17049] 没有实现DATETIME到BINARY的转换`**（2026-11-16，虚谷六颗既有红里 MERGE 那两颗）：多行 `MERGE INTO … USING (SELECT … FROM dual UNION ALL …)` 的源臂把每一格都绑成参数，同一列位置上"非 null 的 `DbType.DateTime2`"与"绑成 `DateTime2` 的 `DBNull`"混排时，驱动对那颗空值做 DATETIME→BINARY 型推当场炸——三行全 null（探针 E2）与整臂全绑旧型 `DbType.DateTime`（P16）都绿，唯独混排红。`XuguValues.DbTypeOf` 把 null 的时刻参数改绑旧型 `DbType.DateTime`：空值没有精度可失，非 null 路一字未动，离线金样零改动（内联规则未动）。活库落库核验：NULL 原样落、同臂非 null 毫秒保真、INSERT/UPDATE/谓词三侧同臂无殃及（读数 `obj/logs/v42-xugu-fix-probe-2/3.log`）。
- **虚谷集成册同批修掉其余四颗既有红**（同一笔，测试侧改判，2026-11-16）：① `MaterializationAllocationTests` 两颗红在钱列回包型——厂商驱动在 v0.32–v0.33 之间从 3.3.4.5 换到 4.0.0.8，`NUMERIC(18,4)` 回包从 `string` 改交 `Decimal`、行值改为一次物化（反复取同一格交回同一实例、逐列读数全 0 B/次），`Database.MoneyClrType` 跟着换代改判，两条用例的虚谷臂按新一代读数重写（读数 `obj/logs/v42-xugu-fix-probe-5/6.log`）；② `DefaultValuePathTests` 两颗红在夹具的占位符——原生 SQL 口的契约是"文本一颗不动"，虚谷驱动只认 `:` 前缀，夹具写死 `@sku` 连参数都挂不上（落成 NULL 按键找不回），占位符改取本家方言的 `ParameterPrefix`（其余五家前缀本来就是 `@`，逐字不变；读数 `obj/logs/v42-xugu-fix-probe-2.log` D 系列）。

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
