# 树形查询（父级集合 / 子集集合 / 整树）—— 方案与落地记录

> **状态（2026-11-10）：内存闭包路已落地并全绿；库端原生树语档未落地（§8）。**
> 硬约束（同日定）：**兼容全部 19 家 provider，允许各家出自有语法**。由此定死的方向是
> **通用底座 = 内存闭包（只用一句普通 SELECT），原生树语 = 第二档加速路**，
> 而不是反过来——因为递归 CTE 要 19 家 × 三格文本差异逐家实测，而活库靶只有六家，
> 余下十三家会永远停在"文档口径"。那是看运气，不是兼容。
>
> 标记口径：**【已核实】**= 当场读到 file:line 或跑出的读数；**【已落地·有读数】**= 代码在树上、用例在册里；**【未验证】**= 只能由活库或厂商文档给答案，本册不猜。

## 0. 三件事与一颗内核

| 口 | 语义 | 输入 | 输出 | 取数形状 |
| --- | --- | --- | --- | --- |
| `Descendants` | 给锚点，拿全部后代 | 一批主键 + 深度上限 | 扁闭包或森林 | 逐层爬 |
| `Ancestors` | 给节点，拿全部祖先（面包屑） | 一批主键 | 同上，前序即根→锚点 | 逐层爬 |
| `ToTree` | 给一片条件，一次嵌成森林 | `Where` 片界定 + 父键 | `TreeNode<T>` 森林 | 一句取片 |

三颗口共用一颗内核（`QueryEngine.Tree` / `CrawlAsync` / `Stitch`）：差别只在**锚点从哪来**（一片谓词 vs 一批主键）与**往哪一头走**，缝合、`Depth`、`Path`、环、排序全在同一段。扁形状与树形状是**同一次缝合的两种交法**（先建森林，再前序摊平），所以"行与节点对不上"这类缺陷没有存在的空间——G-parity 由构造保证，不靠测试兜。

## 1. 已落地清单【已核实】

| 层 | 落点 |
| --- | --- |
| 公共 API | `IQuery<T>` 上四颗口（Descendants / Ancestors / ToTreeAsync / ToTree）+ `ITreeQuery<T>`、`TreeRow<T>`、`TreeNode<T>`（src/QuickSql.Abstractions/Query/TreeQuery.cs） |
| 查询模型 | `TreeRequest`（方向、父键列、锚点集、深度上限、含否自身）挂进 `QueryModel.Tree`，并**进规范文本**（QueryModelPrinter 的 TREE 行） |
| 取闭包 | `QueryEngine.Tree.cs`：一句取片 / 逐层爬；IN 臂复用共用发射段 `QueryInBatchesAsync`（新参 `extraPredicate`，既有四个调用点不递 → 句子逐字节同形） |
| 键读口 | 新增按列序的非泛型读口 `IBoxedEntityAccessor` + `IEntitySchemaRegistry.Accessors(Type)`：G1 侧交回生成期写死的强转（`EntityFactsWriter` 多一行实现），G2 侧交回 `ReflectionEntitySchema` 启动期编译好的那张委托表。树形闭包的键与父键读数走这一颗——**热路径上不再按名字反射读成员**，也不再为每型每名付一轮 `MemberAccessors` 定位 |
| 键表比较 | `visited`/`byKey`/`byParent`/`DistinctKeys` 一律 `StructuralComparer`，并让它覆盖 `byte[]`（含 `object[]` 复合键里的 binary 分量）；`Path` 里的二进制段打十六进制。默认比较器按引用相等，而每一趟物化都新建一只数组——那样每个节点都"找不到父"，交回扁表且库里不报错（审出的 P1）。 |
| 缝合 | `Stitch`：`ParentKey` 一次分桶 + BFS 落 `Depth`/`Path`，O(n)；环上余行升为根 |
| 键读口 | 新增按列序的非泛型读口 `IBoxedEntityAccessor` + `IEntitySchemaRegistry.Accessors(Type)`：G1 侧交回生成期写死的强转（`EntityFactsWriter` 多一行实现），G2 侧交回 `ReflectionEntitySchema` 启动期编译好的那张委托表。树形闭包的键与父键读数走这一颗——**热路径上不再按名字反射读成员**，也不再为每型每名付一轮 `MemberAccessors` 定位 |
| 守卫 | 投影 / 急切加载 / 拆查 / WithSql / 派生 / 集合运算 / 联表 / 分组 / 分页 / 分表 / 复合主键 / 父键不是一颗列 / null 锚点 / 整片缺片界定 / 深度上限 ≤ 0 / 与主键不同型 / 逐层爬配 `WithCache` —— 全部**接链当场响** |
| 顺带修的 | 契约优先级那一格：命名约定的自增不再盖掉显式 `IsIdentity=No` 声明（详见 §9，带反验） |
| 用例 | 离线 `TreeQueryTests` 25 颗 + 接缝 `MetadataEquivalenceTests.The_boxed_value_getter_...` 1 颗 + 约定优先级 2 颗；活库 `TreePathTests` 14 颗（SQLite/PG 各 14 绿；SqlServer/MySQL/OceanBase/虚谷 各 13 绿 + 1 skip——二进制键建表拦在 CodeFirst 那格，理由写在用例里）。全量离线册 1628 绿 |
| 读数 | `docs/evidence/tree-memory-closure-1.log` |

## 2. 语义口径（落地后定稿）

1. **`Depth` = 输出森林里的层级，根 = 0**。`Descendants` 的根是锚点（`IncludeSelf(false)` 时是锚点的直接子）；`Ancestors` 的根是**最上层祖先**，所以前序摊平天然就是面包屑（根 → 锚点）；`ToTree` 的根是片内无父的那些行。三颗口一套定义，不按方向翻。
2. **`Path` 给了**：从森林根到本行的键串，`/` 连接（`42/57/913`）。在内存拼，19 家逐字节同形。它是展示/定位用，不承诺字典序 = 树序（键是数字时 `10` 排在 `2` 前面）。
3. **环两道拦**：已访问键集（第一道，每颗键只收一次）+ 深度上限（缺省 32）。A→B→A 现场读数是 **3 句、2 行、各出现一次**，且环上行升为根交回（不静默丢一行）。不依赖任何库端 `MAXRECURSION` / `CYCLE`。
4. **孤儿即根**：父键在结果里找不到主人（被片界定或过滤器切在外面、或父行本来就不存在）时，该行升为根、`Depth = 0`，不另给标记。理由：它与"父被软删"是同一形状，两套处理反而读不出差别。
5. **链上 `Where` 与全局过滤器落到每一句**（锚点句与每一层句同一口径）。**后果明写**：链上有一个被筛掉的中间节点，它下面整段读不到——`Descendants` 少一整支，`ToTree` 把它的孩子变成孤儿。这是行为不是缺陷，G6 钉住。
6. **`ToTree` 必须有片界定**（链上 `Where` 或生效的全局过滤器），否则接链就抛——那等于全表搬进内存。
7. **第一版只接单列主键**：复合主键在接链处拒（层与层之间的逐行 OR 链会放大成叉积）。
8. **分表实体拒**：锚点所在片与后代所在片可以不同，跨片闭包的路由没量过。
9. **树口收窄链**：`ITreeQuery<T>` 上没有 `Where`/`OrderBy`/`Select`，也没有 `ToPagedAsync`——闭包行上再筛一刀、或分页截一段闭包，交回的都是残段而不是树。

## 3. 句数账（逐层爬的真实形状）

**句数 = 1（锚点句）+ 实际探测的层数**。子集那一头到顶之前每层都要多问一句才知道"下面没了"，那句空手而归是固有账（三层树实测 5 句）；父级那一头靠父键为 null 就地收口、不多问（三层链实测 4 句）；撞上 `Depth` 上限时也没有探底句（`Depth(1)` 实测 2 句）。所以别把"+1"当恒等式背——它只在 Descendants 且未触顶时出现。锚点集或每层键集超过参数预算时按预算再分块，所以真实句子数是 `Σ ceil(每批键数 / perChunk)`。

六家活库对同一棵万行宽树（1 根 + 9999 叶）的读数【已核实·evidence，第二次刷新场】。`ms` 是单场读数（本机基准噪声带 ±30–68%，不要拿它当阈值用）；句数与每句参数颗数是确定值：

| provider | 整片 | 整片耗时 | 逐层爬句数 | 最大一句参数 | 逐层爬耗时 |
| --- | --- | --- | --- | --- | --- |
| SQL Server | 1 句 | 50 ms | 8 | 1890（预算 2100） | 1748 ms |
| 虚谷 | 1 句 | 75 ms | 114 | 90（保守预算 100） | 916 ms |
| SQLite | 1 句 | 71 ms | 114 | 90 | 160 ms |
| PostgreSQL | 1 句 | 51 ms | 3 | 9999 | 99 ms |
| MySQL 8.4 | 1 句 | 78 ms | 3 | 9999 | 99 ms |
| OceanBase | 1 句 | 105 ms | 3 | 9999 | 184 ms |

读出来的两件事：

- **整片那一档没有税**：一句、45–81 ms，与任何一句带条件的查询同一形状。真实业务里的树几乎都有片界定列，而全局过滤器正好替调用方挂上这一刀——所以默认路选内存闭包不是让步。
- **税出在宽树 + 小预算**：SQL Server 那句 1890 参数的 IN 付了 1.6 s（编译成本），虚谷付 114 句往返 1.4 s。这一格是原生树语档存在的唯一理由，见 §8。

## 4. 现状勘察【已核实】

- **落地前全仓零面**：`src/` 下 `recursive` / `hierarchy` / `ancestor` / `descendant` grep 无命中；`DbCapabilities.Cte = 1 << 14`（DbCapabilities.cs:29）只有 SqlServer（:46）与 PostgreSqlBase（:61）声明，**零消费者**；Oracle 族类注（OracleProviderBase.cs:14）明确不声明。
- **`IncludeMany` 是最近的先例，也只有一层**：`ListWithIncludeManyAsync`（QueryEngine.cs:1108）→ `LoadChainAsync`（:1136）→ `LoadChildrenAsync`（:1155）；外键三口约定 `DiscoverFkColumns`（:838，命不中的文案 :891）与生成器双胞胎 EntityFactsBuilder.cs:458；与 `AsSplitQuery` 双向互斥（:610-612、:696-701）、投影后拒（:694）。本档的 B 形状 = 把它的"单层"扩到"到上限为止"，并加一条父方向。
- **发射面一字未动**：`SelectStatement`（SqlAst.cs:143）没加 `With` 字段，`DialectBase` 的句首挂点仍只有 `EmitPagingQualifier` 一颗（:83 类注原话、:88 调用点），19 家方言文本零新增形状。
- **缝合的读口复用既有的**：`BuildKeyGetters`（:1399）/`BuildKeyGettersByColumnName`（:1414）/`Materializer.EntityRows<T>`（:193）/`CanonicalOf`（ChangeTracker.cs:528），与 `IncludeMany` 同一批，没有新开第二套反射面。
- **测试靶**：活库六家由 `QUICKSQL_TEST_PROVIDER` 切（Database.cs:98，认值见 :107）；离线册 19 家 golden + 折叠对账（DialectParityTests.cs:50-62 钉六家），能力位归 `CapabilityLedgerTests`。

## 5. 闸门读数与当前状态

| 号 | 读数 | 状态 |
| --- | --- | --- |
| G1 | 句数账 = §3 那条公式，五档数据（1/3/30/预算整倍/预算+1） | **已兑现**：三层树 5 句、Depth(1) 2 句、面包屑 4 句、环 3 句、250 锚点分块每句 ≤ 预算 |
| G2 | `Depth`/`ParentKey` 与独立 BFS 逐节点对拍 | **已兑现**（六家），随机 500 节点那档留给下一批补 |
| G3 | 拦环有限、每键一次、环上行不丢 | **已兑现**（六家） |
| G4 | 参数预算分块不漏不重 | **已兑现**（六家，250 锚点） |
| G5 | 19 家通用且**零新增 SQL 文本形状** | **已兑现**：离线 25 颗里逐家发得出锚点句、无 `RECURSIVE`/`CONNECT BY`；离线全量 1628 绿 |
| G6 | 过滤器断链与孤儿升根两处断言 | **已兑现**（六家） |
| G7 | 互斥与拒档 | **已兑现**（离线：投影/急切加载/拆查/WithSql/集合运算/分页/分组/分表/复合键/非列父键/null 锚点/缺片界定/`Depth(0)`） |
| G8 | `ToSql`/`Explain` 不骗人（B 形状只给锚点句并注明；空锚点直接拒） | **已兑现** |
| G9 | 键读这一格零反射零动态代码 | **已兑现（范围要说死）**：`CrawlAsync`/`Stitch` 不再调 `MemberAccessors`，改用两侧已编译的按列序读口。但 `QueryEngine` 整类仍带 `RequiresUnreferencedCode`/`RequiresDynamicCode` 类级标注（物化层与 lambda 翻译那一档，§12.7 的既有账），所以这一格兑现的是"树形的键账"，不是"整棵库 AOT 干净"。仓里**没有** `PublishAot`/crossgen 那把闸（只有 `IsAotCompatible=true` 的分析器，本笔 0 警告通过），其余七处 `MemberAccessors` 调用点（含写路径的 `Storageable`）同 migrate 是独立一笔 |
| G10 | `Depth`/句数在五档规模上的税单 | **部分兑现**：万行宽树六家有数（evidence）；1/3/30/预算整倍/预算+1 那五档的句数账只钉了 1 与 250 两处，中间三档欠着 |
| G11 | 会话身份映射：树里同一行只有那一只 | **已兑现**：`The_session_surfaces_one_instance_per_row_on_both_take_shapes` 钉两档取数路都交回会话里那一只；反验做过——把 `Track(model, rows)` 摘掉，这一颗翻红（`TrackAll` 就地写回才是那条咽喉，原先手写的 `CanonicalOf` 是重复，已撤） |
| G12 | 与 `IncludeMany` 的语义分界写进用户篇 | **已兑现**：17 篇 §8 那张表（缝孩子 vs 取闭包） |

## 6. 不做什么

- 不做 closure table、nested set、物化路径列的库端维护；不建表、不改实体、不加列。
- 不做多父 DAG、不做图；一棵树只认一颗父键列。
- 不做 `MoveNode` / `Reparent` / 整片删除子树——写侧另一册。
- 不做树缓存、不做 ORM 层的树驻留。
- 不改 `IncludeMany` 的语义、互斥规则与分块纪律。
- 不做分表实体上的树、不做复合主键上的树。
- 不背老版本，也不为某一族新版本开"只有它有"的 API——第二档只能走能力位，不能走名字。

## 7. 参照系（为什么这么定，带出处）

- **FreeSql**：两颗可组合的口——`ToTreeList()`（内存把扁结果嵌成树，可单独用）与 `AsTreeCte(pathSelector, up, pathSeparator, level)`（一句递归 CTE，向 `cte_level`/`cte_path` 两列）；官方 wiki 写的是"**通过测试的数据库**：MySql8.0、SqlServer、PostgreSQL、Oracle、Sqlite、Firebird、达梦、人大金仓、翰高"——九家，且没有"没测过的家会拒"这一档。本档把内存那一路升为唯一必经的语义底座，正是对这一格的纠正。
- **SqlSugar**：`ToTree(it=>it.Child, it=>it.ParentId, rootValue)` / `ToChildList` / `ToParentList` / `UtilMethods.BuildTree`，官方文档把 `ToTree` 归在「执行查询」那一档（与 `ToList`/`First`/`ToJson` 并列）= 物化终端，一句取回后内存建树；`ToParentList` 文档原话"只支持最简单的单表查询"。它的教训是一条硬账：`ToTree` 在 10000 行上耗时 35 秒（2022-09-21 报，作者答"后续优化，可结合二级缓存使用"，用户 PR 合并进 5.1.3.2-preview07）——平方级嵌套被外部贡献者修成分桶。本档的 `Stitch` 从第一天就是分桶 + BFS，且 G10 那颗税单用例就是这条账的守卫。
- **本仓自己的先例**：`IncludeMany` 当年选的就是"主查询一字不改 + 额外一句 IN + 内存缝合"，而不是把嵌套物化压进方言发射。同一家族第二颗口走同一条路，行为才对得上。

## 8. 第二档：库端原生树语（未落地）

要求"所有 provider 都能用"已经由 §1–§4 满足；这一档只解决 §3 那笔税。四条前提，缺一条不许开工：

1. **语义由内存路定义，它同时当裁判**。翻转拦环、`Depth`、过滤器、孤儿这些格子以本档为准；原生路不是第二种实现，是"必须逐行等于裁判的那一份"。闸门加一条 **G-parity**：同一份数据、同一个锚点，两路的行集与 `Depth` 全等，不等那家不许翻位。
2. **能力位实测才翻**，未翻的家走内存路——那不是降级档，是同一条 API 的唯一实现。新位建议 `RecursiveCte`（不复用现成的 `Cte`：那颗语义是"能写 WITH"，只有两家声明、零消费，把没量过的格子当已支持是这类位最常见的死法），并让 `CapabilityLedgerTests` 审"位与策略档一致"。
3. **先定死环与重复节点**。内存路每颗键只出现一次；递归 CTE 不带库端 `CYCLE` 时，成环数据会重复膨胀到上限才停——两路对脏数据会给不同行集。FreeSql 的 CTE 档正是这个形状（它只靠 `level` 拦）。要么原生路外侧去重、要么本档明写"两路行集可不等于"，不许含糊。
4. **改动面**：AST 加 `WithClause`（`SelectStatement` 多一格）、`DialectBase` 加句尾挂点（SQL Server 的 `OPTION (MAXRECURSION n)` 落在整句末尾，与 `EmitPagingQualifier` 不同侧）、provider 侧一颗 `ITreeStrategy` 槽位（照 `IDbProvider` 那条"新增 SPI 槽位一律带默认值"的纪律，默认 null = 内存路，19 家零改动即通用）。Oracle/达梦/神通那一族要的是 `CONNECT BY`+`LEVEL`+`NOCYCLE`，不是 CTE——所以这一档的"三格文本差异"实际不止三格。

## 9. 落地时挖出并修好的一处优先级缺陷

**现象**：靶实体写 `[QuickColumn(IsIdentity = TriState.No)] public int Id`，活库上契约读回 `identity=True`，`INSERT` 的列清单里没有 `Id`——号由库给（实际落成 20671 起，而不是声明要的 7849100 起）。

**根因**：`Complete`（EntityContractFactory:365）已经按声明算出 `false`，但随后 `ApplyKeyConvention` 在命中 `Id`/`{Entity}Id` 命名形态时把它 `||` 回 `true`。那一格的原注释写的正是"自增那一半不许把高优先层的声明盖掉"，代码与注释相反。键那一半（`IsPrimary`）归约定没问题，坏的是顺手把值那一半也改了。

**修法（已落地）**：`ApplyKeyConvention` 多收一份 `IReadOnlyList<ColumnFacts>`，约定只在 `DeclaredIdentity is null` 时添自增：

```csharp
IsIdentity = found.IsIdentity || (declared is null && integralPrimaryKeyAutoIncrement && IsIntegral(found.ClrType)),
```

两侧（G1 生成、G2 反射）共用这一个收尾器，所以一处改两边同修；显式声明主键的实体整个不走这一格（:188 的守卫），行为不变。

**反验**：新加的两颗用例——`A_declared_non_identity_on_the_convention_key_beats_the_integral_convention`（声明 No + 命名约定 + 默认装配口 ⇒ `IsIdentity=false`、`INSERT` 带 `[Id]`、号原样发出去）与 `The_split_table_advice_in_the_identity_error_message_actually_lands`（:353 那句分表提示叫用户写的声明，现在真的能让契约过门禁）——把那一行退回旧写法就同时翻红，而同文件既有 5 颗不动。

**为什么在册的用例没抓到它**：`ApplicationKeyConventionTests` 正是测这条优先级链的，但它只钉了三个方向（默认装配口 / 装配口关成 false / Fluent 声明 `IsIdentity(true)` 赢过装配口关），缺的恰好是对称那一格；而它的靶 `InventoryItem.Id` 是裸 `public int Id`，正走命名约定。`MetadataEquivalenceTests` 那条逐列对拍也拦不住：bug 长在两侧共用的工厂里，两侧一起错，"相等"照样成立——所以本笔给它补的是绝对值断言，不是又一处对拍。

**没做的那一半**：本来想让 `Origin.Note` 说出这一格的出处（"自增 ← 声明：不是"），一试发现 `MetadataEquivalenceTests:449` 正钉着那句原文——改它等于顺手改了别人的验收面，撤了。出处这一格要做，该连同其余几层的 note 形状一起立账，不混在本笔里。

## 10. 独立审查与反验

审出的问题分三档，落地的判据都做了反向实验（把修复撤掉看用例是否翻红），出处 `docs/evidence/tree-memory-closure-1.log`：

| 档 | 问题 | 判据 |
| --- | --- | --- |
| P1 | 键表按引用比 ⇒ `byte[]` 主键/父键的树交回一张扁表（库里不报错） | 撤掉 `StructuralComparer` 的 `byte[]` 一支 → 该用例红（三节点全成根）；恢复回绿 |
| P2 | 父键与主键不同型 ⇒ 同样静默扁平 | 新增拒档 + 用例（`long` 配 `int` 在接链处响，消息点出两颗列与两种型） |
| P2 | 逐层爬那一档 `WithCache` 点了不生效 | 新增拒档；整片那一档仍可缓存 |
| P2 | 句数账写成"恒 +1"、`IncludeSelf` 语义写成只有子集那一读、`Accessors` 射程吹成覆盖急切加载、用户篇称父键走 `DiscoverFkColumns` | 全部按代码事实改口；`Path` 与模型文本的二进制键改打十六进制，锚点行加颗数避免 `["a|b"]` 与 `["a","b"]` 折成同一行 |
| P3 | `FoldPredicates` 与 `QueryCompiler.Fold` 重复、`Stitch` 收着用不到的参数、`Slice` 档打印不会生效的旋钮、树口报错借用 Include 的话术 | 各自消掉；反向护栏那格由编译期守住，注释改成"只单向拦"并写明原因 |

未改的一条：逐层爬循环头部的 `depth <= MaxDepth` 是冗余上界（`depth >= MaxDepth` 已让下一层空手），留着当第二道闸——它不改变行为，只是把"最坏情况写在脸上"。

## 11. 下一步

1. **其余七处 `MemberAccessors` 调用点同 migrate**（`DbSet` 两条补载路、`QueryEngine` 的 IncludeMany/拆分加载回填、`Storageable` 写侧两颗）：本笔只把树形的键账挪到按列序读口上，同一个接缝剩下七处还站在名字反射上。这一笔是独立的账，要配它自己的分配读数——`MemberAccessors` 的缓存注释里那些 µs/B 数字是它现有的验收面。
2. **仓里没有 AOT 那把闸**：只有 `IsAotCompatible=true` 的分析器（本笔 0 警告通过）。要量"发布产物真的不依赖动态代码"，得先立一个 host（`tests/aot` 那类项目在独立仓里还留着，本仓没有），否则"0 警告"会被读成"0 动态代码"——那是两件事。
3. G10 剩下三档规模（1/3/30/预算整倍/预算+1 里的中间三档）补上，税单表才齐。
4. §8 第二档（库端原生树语）要不要开：等 §3 那笔税在真实现场疼到人，或者先按四条前提把 G-parity 的桩子钉上。——**§13 的三方对照已经把读数交出来了**（宽闭包 117 句 vs 一句 CTE），这一项不再是"等"，是"要不要按 §8 四条前提开工"。
5. CHANGELOG 的 10.1.0 待发布节已挂「新增：树形闭包」与键读接缝那颗（`IBoxedEntityAccessor` / `IEntitySchemaRegistry.Accessors`）。
6. ~~读侧 IN 变量上限从写侧 Bulk 槽里分出来~~ —— **已试过，输了，已回退**（§14，读数 `docs/evidence/tree-in-arm-cost-1.log`）。缝本身开对了（那颗槽默认沿用 `Bulk ?? 100`，十三家零改动、行为逐字节不变），错的是要翻的数字：按硬上限 32766 声明之后，宽树闭包句数 117 → 9 而耗时 45.9 → 164 ms。这一家的成本在**参数绑定**（命名参数按下标线性扫 ⇒ 整批是平方级），不在句数；臂宽 90 恰好落在实测曲线的最低点，那颗「保守兜底 100」不是欠账而是巧合的最优点。⇒ 逐层爬那笔税不能靠分块口径消掉，能动的只剩两格：§8 的库端路（客户端一颗键都不发），以及「内核生成的整型键改内联字面量臂」（同宽度快 2–32 倍，代价是计划缓存零命中，而「把值拼进文本」的边界必须先写死成「只允许自家物化出来的键」）。那颗槽今天没有一家翻位，所以已撤——要再开，先带读数。

## 12. 一句话现状

三颗口的语义、两种取数形状、句数账、拦环与键比较、过滤器后果、闸门与反验都在本册；代码已落地（内存路），1628 离线 + 六家活库全绿，其中二进制键那格由 SQLite/PostgreSQL 守、其余四家显式 skip 并写明是 CodeFirst 的类型映射账。库端原生树语（第二档）仍未落地，开工前提写在 §8。

## 13. 与 FreeSql / SqlSugar 的三方对照（2026-11-10 实测）

装置：一颗 SQLite 文件、三张同构表（`qs_tree` / `fs_tree` / `ss_tree`），种子由同一段原始 SQL 写入，所以三家的差只剩"读树这一件事怎么做"。数据三档：宽（1 根挂 9999 子）、平衡（4 叉、深 7、10000 行）、链（20 层单枝）。版本：FreeSql 3.5.311 + FreeSql.Provider.Sqlite、SqlSugarCore 5.1.4.221、Microsoft.Data.Sqlite 10.0.11。

口径三条纪律，都是第一轮量错之后补的：**节点数递归数出来**，不量 `List.Count`——扁表会冒充树（第一版就是这么把 SqlSugar 的全表扫读成"它也建了树"）；**句数由各家自己的 AOP 钩子数**（`QuickSqlAop.Executing` / `Aop.CommandBefore` / `Aop.OnLogExecuting`），并且把语句原文一并落盘；**9 轮按轮交错跑**，取中位与最小，另跑第二遍对表（`docs/evidence/tree-three-way-bench-1.log` 底部）。原始逐句在 `tree-three-way-bench-1.sqls.log`。

| 场景（节点数） | QuickSql | FreeSql | SqlSugar |
| --- | --- | --- | --- |
| 平表地板 bal（10000 行，不转树） | 1 句 / 14.7 ms | 1 句 / 8.5 ms | 1 句 / 10.4 ms |
| 整片转树 bal | 1 句 / 21.2 ms | 1 句 / 27.0 ms | 1 句 / 15.9 ms |
| 整片转树 wide | 1 句 / 15.6 ms | 1 句 / 27.2 ms | 1 句 / 11.2 ms |
| 闭包：根锚点 bal（10000） | **117 句** / 50.4 ms | 1 句 / 14.4 ms | 1 句 / 25.8 ms |
| 闭包：中层锚点（21） | 4 句 / 0.41 ms | 1 句 / 0.25 ms | 1 句 / 24.0 ms |
| 面包屑 bal（8） | 9 句 / 0.68 ms | 1 句 / 0.23 ms | 16 句 / 2.26 ms |
| 面包屑 chain（20） | 21 句 / 1.54 ms | 1 句 / 0.22 ms | 40 句 / 5.67 ms |

三家交回的节点数逐格相等（SqlSugar 那两格少一颗，是 `ToChildList(…, includeSelf: false)` 的旗子，不是漏行）。三条读数：

1. **整片那一档三家同形状，QuickSql 居中**：都是一句 SELECT + 内存嵌套。比 FreeSql 的嵌套快 22–47%，比 SqlSugar 慢 27–36%。§7 记的那笔 SqlSugar 平方级旧账（10000 行 35 秒）在这一版已经不复现。QuickSql 多出来的分配是 `TreeNode` 与 `Depth`/`Path`：同一片平表 2451 KiB，转树 6647 KiB——那是另两家不交的账。
2. **闭包那一档差得最开，而且差在句数**：一句递归 CTE（FreeSql）< 逐层爬（QuickSql）。bal-root 那 117 句全落在 IN 臂分块上——SQLite 没交 `Bulk.ParameterBudget`，`QueryInBatchesAsync` 就吃 `StatementEmitter.ConservativeParameterBudget = 100`，每臂 90 颗键，而最后一层光锚点集就有 4539 颗键（51 句）且空手而归。探针（同一颗驱动）：32766 颗参数收得下，32767 抛 `too many SQL variables`。——**这一度被读成"白付的 12 倍税"，实测推翻**：按硬上限放宽臂宽之后句数 117 → 9 而耗时 45.9 → 164 ms，成本在参数绑定不在句数（曲线与回退记录在 `docs/evidence/tree-in-arm-cost-1.log`，见 §14）。
3. **"句少"不等于"读得少"**：SqlSugar 的 `ToChildList` 那一句原文是 ``SELECT `Id`,`Pid`,`Grp`,`Name` FROM `ss_tree` ``——一个 WHERE 都没有，整表搬进内存再爬。所以取 20 个节点与取 9999 个节点花的钱一模一样（24.0 / 25.8 ms，6.5 / 7.9 MB）。它的 `ToParentList` 反过来是真逐句，且每层两句（`SELECT 1 … LIMIT 0,1` 探一次、取行取一次），20 层链 40 句。

结论落三格：

- **§8 的性价比由第 2 条定**，而 §14 把"先修分块再说"这一条自己试掉了：宽闭包那笔税在本地既不能靠少发几句消掉（句数 117 → 9，耗时反而 3.2 倍），也不是句价贵（每句固定开销三家里 QuickSql 最低）。剩下的只有库端那一路——深闭包或宽闭包要上它，没有第二条；远端库上每句还是一个 RTT，这一格只会更硬。
- **口径建议（只进文档，不进代码）**：闭包覆盖本片大半 → 用 `ToTree`（1 句，第 1 行 vs 第 4 行差 2.4 倍）；只要一小片 → 用 `Descendants`（第 5 行 0.41 ms，是三家里最快的一档内存路）。第 5 行与第 4 行那 120 倍的差就是这条建议的依据。
- **未复现的不等于不存在**：这台机器只有本地 SQLite。活库（PG / MySQL / OceanBase / 虚谷 / SQL Server）上三家的形状要另开一跑，且要给 FreeSql、SqlSugar 各挂对应 provider 驱动——尚未做，别把上面这表读成六家通用。

顺带把"每句固定开销 vs 每行物化开销"拆开量过（同一颗 bench 的 `scale` / `cliff` 两档，读数 `docs/evidence/tree-read-scaling-1.log`），三条与本册有关的推论：

- 空结果那一格（固定开销）：QuickSql 0.10 ms / 177 KiB，FreeSql 0.13 ms / 347 KiB，SqlSugar 0.18 ms / 97 KiB，裸 ADO 0.04 ms。⇒ **句价不是 QuickSql 的短板**，逐层爬那笔税的主项在别处（§14 量出来是参数绑定）。
- 投影路径贴着地板：`Select(x => x.Id)` 取 10000 行 3.08 ms，同一句裸 ADO 3.39 ms。⇒ 发射、reader、投影这一段没有可找的账。
- 差距全在整行物化：QuickSql 14.5 ms / 238 B 每行，FreeSql 8.3–11.9 ms / 136 B，SqlSugar 10.7–12.0 ms / 137 B（跨跑 ±30% 噪声，比值读 1.2–1.8 倍）。这一格与树形无关，是一句普通 `SELECT` 的成本，属另一本账。**这本账已开：`plan-cross-orm-bench.md`**（通用形状三方对照，2026-11-11）——那笔 1.2–1.8× 在 8/30 列通用形状上收敛到与 Dapper/FreeSql 同档（±10% 内），另有一条口径发现：两家的官方 NuGet 都是 Debug 构建，时间列偏悲观。
- 上一轮怀疑的"20000 行超线性"**撤掉**：裸 ADO 同规模 2.1 倍、投影 1.9 倍，QuickSql 整行 2.2 倍——那是行数翻倍加上本机噪声，不是 QuickSql 特有的形状。


## 14. 一次失败的加速：把 IN 臂按驱动硬上限放宽（已回退）

§13 第 2 条读出来"SQLite 没声明参数预算 ⇒ 闭包按 90 颗一臂切成 117 句"，探针又证明这颗驱动收得下 32766 颗。于是做了一次改动：`IDbProvider` 加一颗读侧槽 `StatementParameterBudget`（默认 `Bulk?.ParameterBudget ?? 100`，所以已声明 Bulk 的十三家一字不改、行为逐字节相同），`QueryEngine.QueryInBatchesAsync` 与 `ExpressionTranslator` 的 IN 分块改读它，SQLite 声明实测硬上限。

读数：**句数 117 → 9（正是设计意图），耗时 45.9 → 164 ms（不是设计意图）**。改动当场判定为负，整体回退；回退后复测 117 句 / 45.88 ms，逐位与 §13 那一跑同形。

为什么反而慢——曲线在 `docs/evidence/tree-in-arm-cost-1.log`：同一批 4539 颗整数键、同一张表、同一句形状，只换臂宽，裸 ADO 跑（每格 9 轮中位）。

| 臂宽 | 句数 | 带参数 ms | 内联字面量 ms |
| --- | --- | --- | --- |
| 50 | 91 | 9.15 | — |
| **90** | **51** | **7.17** | 3.63 |
| 200 | 23 | 7.55 | — |
| 500 | 10 | 12.08 | 2.18 |
| 1000 | 5 | 18.92 | — |
| 2000 | 3 | 32.71 | — |
| 4539 | 1 | 74.49 | 2.35 |

三条判据：

1. **成本的主项是参数绑定，不是句数**：臂宽 90→4539（50 倍）时间 7.17→74.49 ms（10.4 倍），每颗键的单价从 1.6 µs 涨到 16.4 µs。形状与"命名参数按下标线性扫、整批平方级"一致——Microsoft.Data.Sqlite 用 `@p0…` 命名参数且不改写文本，绑定一次扫一次。
2. **那颗"保守兜底 100"是巧合的最优点，不是欠账**：90 一臂是全曲线最低，再窄（50）就开始付句数开销。所以 §13 里"白付的 12 倍税"那句作废，本册改口。
3. **真要动这一格，动的是"键怎么进语句"**：同一批键改内联字面量后几乎与臂宽无关（2.18–3.63 ms），比同宽度参数臂快 2–32 倍。但它欠两笔账，都不比"少发几句"小：命令文本每臂都不同 ⇒ 计划缓存零命中；而把值拼进文本这一手只在键是自家物化出来的整型主键时成立，用户输入仍必须走参数——那条边界要先写死成判据，不是留成一句注释。

对本册的净结论：逐层爬那笔税在本地已经量到低点，**消不掉**。能少发键的只剩两路——§8 的库端原生树语（客户端一颗键都不出，FreeSql 那一格 1 句 / 14.4 ms 就是它的读数），以及"有片界定时改用 `ToTree`"。§11 第 6 项据此改写，那颗槽今天没有一家翻位，已撤。
