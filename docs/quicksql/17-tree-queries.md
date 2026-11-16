# 17 · 树形查询

> 本篇解决：自引用表的三件事——给锚点取子集、给节点取祖先面包屑、给一片条件嵌成森林；以及这三件事在 19 家数据库上同一套语义是怎么做到的（闭包在服务器内存里算，发射面只用一句普通 `SELECT`）。

## 1. 三颗口

```csharp
public class OrgNode
{
    public int Id { get; set; }
    public int? ParentId { get; set; }      // 自引用父键：可空，null 就是根
    public string? Name { get; set; }
    public int SortNo { get; set; }
    public int TenantId { get; set; }
}

// ① 子集：给锚点，拿它下面全部后代（扁闭包）
var subtree = await orm.Select<OrgNode>()
    .Descendants(n => n.ParentId, 42)           // 多锚点：.Descendants(n => n.ParentId, 42, 43)
    .Depth(max: 5)                              // 缺省 32，同时也是拦环的第二道
    .ToTreeRowsAsync();
// TreeRow<OrgNode>：Value / Depth / ParentKey / Path

// ② 父级：给节点，拿它到根的全部祖先——前序就是面包屑（根 → 自己）
var crumbs = await orm.Select<OrgNode>()
    .Ancestors(n => n.ParentId, 913)
    .ToTreeRowsAsync();

// ③ 整片：给一片条件，一次嵌成森林
var forest = await orm.Select<OrgNode>()
    .Where(n => n.TenantId == tenant)           // 片界定，缺了当场抛
    .OrderBy(n => n.SortNo)                     // 兄弟序，发射在库里
    .ToTreeAsync(n => n.ParentId);              // IReadOnlyList<TreeNode<OrgNode>>
```

`ToTreeRowsAsync` 交回扁的，`ToTreeAsync` 交回嵌的——**同一次缝合的两种交法**（先建森林再前序摊平），所以两者永远不会互相矛盾。同步出口 `ToTree()` / `ToTreeRows()` 同样在（sync-over-async，口径与 `ToList` 一致）。

实体不需要 `Children` 属性，也不需要任何新注解：父键就是 lambda 点名的那一颗**本实体的列**（在契约的 `ByMember` 上认，认不出当场响）。这里没有三档外键反推——那是 `IncludeMany` 找「子实体上指向父实体的外键」时用的（`DiscoverFkColumns`），而树形只有一根自引用边、没有第二张表可猜，所以不借它那三档，也不新开第二套。父键与主键必须同一种值，不同型在接链处就拒（`long` 主键配 `int` 父键在库里连得上、在内存里对不上，交回的是扁表而不报错）。

## 2. `Depth` / `Path` 到底数的是什么

| 口 | 森林的根 | `Depth` | `Path` |
| --- | --- | --- | --- |
| `Descendants` | 锚点（`IncludeSelf(false)` 时是锚点的直接子） | 距锚点的层数，根 = 0 | 根到本行的键串，`/` 连接 |
| `Ancestors` | 最上层祖先 | 距根的层数，所以前序 = 面包屑 | 同上 |
| `ToTree` | 片内无父的那些行 | 距根的层数 | 同上 |

三颗口一套定义，不按方向翻——这一格不统一，两边的读数就没法对账。

`Path` 在内存拼，19 家逐字节同形；它是展示与定位用的，**不承诺字典序等于树序**（键是数字时 `10` 排在 `2` 前面，那是字符串比较）。要按树序走就直接遍历 `Children`，或按 `Depth` 排。

## 3. 句数：什么时候发几句

| 形状 | 用在 | 句数 |
| --- | --- | --- |
| 一句取片 | `ToTree` | **恒 1** |
| 逐层爬 | `Descendants` / `Ancestors` | **1（锚点句）+ 实际探测的层数**，超过参数预算时按预算再分块 |

子集那一头，"下一层还有没有"只能问一次，所以到顶之前会多发一句空手而归的探测句（三层树实测 5 句 = 锚点 1 + 层 3 + 探底 1）；父级那一头靠父键为 null 就地收口，不必多问那一句（三层链实测 4 句 = 锚点 1 + 层 3）；到了 `Depth` 上限也不再有探底句（`Depth(1)` 实测 2 句）。真实句数是 `Σ ceil(每批键数 / perChunk)`，`perChunk` 由 provider 的参数预算算出（SQL Server 2100、MySQL 系 65535、没声明预算的走保守值 100）。

**这一格别当优化项**：把预算抬到驱动的硬上限，实测是把税换个方向付。SQLite 的硬上限是 32766 颗，按它分块后同一棵万行宽树从 117 句降到 9 句，而耗时从 46 ms 涨到 164 ms——这一家语句的成本主项是**参数绑定**（命名参数按下标线性扫，整批平方级），不是往返次数；今天那颗"保守值 100"折出的 90 颗一臂恰好是实测曲线的最低点。读数与曲线在 `docs/evidence/tree-in-arm-cost-1.log`，判定过程记在 `plan-tree-query.md` §14。

万行宽树（1 根 + 9999 叶）在六家活库上的单场读数（`ms` 有 ±30–68% 的机器噪声，句数与参数颗数是确定值；明细 `docs/evidence/tree-memory-closure-1.log`）：

| provider | 整片 | 逐层爬 |
| --- | --- | --- |
| SQL Server | 1 句 / 50 ms | 8 句 / 1748 ms |
| 虚谷 | 1 句 / 75 ms | 114 句 / 916 ms |
| SQLite | 1 句 / 71 ms | 114 句 / 160 ms |
| PostgreSQL | 1 句 / 51 ms | 3 句 / 99 ms |
| MySQL | 1 句 / 78 ms | 3 句 / 99 ms |
| OceanBase | 1 句 / 105 ms | 3 句 / 184 ms |

读法：**有片界定就走 `ToTree`**（一句、几十毫秒，且与任何一句带条件的查询同形状）；只有"表很大、锚点下的闭包很小、又没有片界定列"才不得不逐层爬，那一档付的是层数趟往返。

## 4. 环、孤儿、被筛掉的父

- **环**：两道拦——已访问键集（每颗键只收一次）+ 深度上限（缺省 32）。库里种一行 A→B→A，实测 3 句收口、2 行各出现一次，且环上的行升为根交回，不静默丢一行。这一格不依赖任何库端的 `MAXRECURSION` / `CYCLE`。
- **孤儿即根**：`ParentKey` 在本次结果里找不到主人（父被片界定切在片外、父行本就不存在、或父在环上）时，该行升为根、`Depth = 0`，不另给标记。
- **过滤器落到每一句**：链上的 `Where` 与 `orm.Filters` 上注册的全局过滤器会进到锚点句和每一层句子。因此**被筛掉的中间节点会让它下面整段读不到**——`Descendants` 少一整支，`ToTree` 把它的孩子变成孤儿。这是行为不是缺陷：软删除的父节点走的就是这一条路，两格钉在同一颗用例里。

## 5. 拒档清单（接链当场响，不发一句 SQL）

树形口对形状很挑，因为下面这些形状发出去**不会报错，只会交回一棵看着像树的残段**：

| 拒什么 | 为什么 |
| --- | --- |
| `Select(...)` 之后接树口 | 投影之后 `Depth`/`ParentKey` 无处落，缝合没有实体可挂 |
| 链上已有 `Include` / `IncludeMany` | 两者都要在执行期另发句子，叠起来句数读不出账；`Include` 的 JOIN 展开列还会让闭包行的列清单与实体对不上 |
| 链上已有 `AsSplitQuery()` | 拆查改引用导航的取数路，树改整条链的取数路，两颗策略叠在一句上只能有一颗赢 |
| `WithSql` / 派生表 / 集合运算 / 联表 | 都换掉了根表位或列清单，而闭包每一句要按同一张表位发 IN 臂 |
| `GroupBy` / `Having` | 分组计划的选列是键加聚合，与实体列对不上 |
| `Skip` / `Take` | 分页截的是闭包的一段，截出来的行父不一定在结果里 |
| 分表实体 | 锚点所在片与后代所在片可以不同，跨片闭包的路由没量过 |
| 复合主键 | 闭包的键集与父键臂都按一颗列发 IN，复合键的逐行 OR 链在层与层之间放大成叉积 |
| 父键 lambda 指向导航属性 | 那不是一颗列，IN 臂无处落 |
| `ToTree` 没有任何片界定 | 等于全表搬进内存；要从锚点取一小片就用 `Descendants` |
| `Depth(0)` | 上限至少 1 |

`ITreeQuery<T>` 这一层刻意没有 `Where` / `OrderBy` / `Select` / `ToPagedAsync`：闭包行上再筛一刀就是把链砍断。

## 6. 出口

| 口 | 交回 |
| --- | --- |
| `ToTreeRowsAsync()` / `ToTreeRows()` | 扁闭包，前序（根 → 子） |
| `ToTreeAsync()` / `ToTree()` | 嵌套森林 |
| `ToSql()` | 整片档给那一句；逐层爬档给**锚点那一句**（分块后的实际句数看执行期播报），空锚点直接拒——一颗 string 装不下多句，假装装得下就是诊断口骗人 |
| `Explain()` | 模型段带 `TREE` 一行（方向、父键列、锚点、深度、含否自身），可与非树链逐字节比出来 |

要数真实句子，走 `Aop.Executing` 播报（与 `IncludeMany` 同一取读法，`TreePathTests` 的句数账就是这么钉的）。

## 7. 为什么不用递归 CTE

递归 CTE 是这件事的库端正解，但它要求 19 家 × 若干格文本差异（`RECURSIVE` 关键字有无、CTE 列清单必带否、深度上限写谓词还是写句尾 `OPTION (MAXRECURSION)`、Oracle 族用 `CONNECT BY` 而非 CTE）逐家实测，而活库靶只有六家——余下十三家会永远停在"文档口径"。本库的口径是**兼容所有 provider**，所以闭包由服务器内存算，发射面退到每一家都已经验过的那一句普通 `SELECT`。

代价写在 §3 那张表里（宽树 + 小预算那一格最疼）。库端原生树语作为**第二档加速路**的开工前提记在 `docs/quicksql/plan-tree-query.md` §8：语义由内存路定义并当裁判、能力位实测才翻、环与重复节点的口径先定死、闸门加一条"两路逐行全等"。没测过的家走内存路不是降级，是同一条 API 的唯一实现。

同表同数据的三方对照读数（FreeSql 3.5.311 / SqlSugarCore 5.1.4.221，明细 `docs/evidence/tree-three-way-bench-1.log`，口径 `plan-tree-query.md` §13）：整片那一档三家同形状，QuickSql 居中——比 FreeSql 的嵌套快 22–47%，比 SqlSugar 慢 27–36%。闭包那一档 FreeSql 的一句递归 CTE 确实最便宜；SqlSugar 的 `ToChildList` 那"1 句"原文是 ``SELECT ... FROM 表``——一个 WHERE 都没有，整表搬进内存再爬，所以取 20 行与取 9999 行花的钱一样（24 ms / 26 ms）。这一格读数不改上面那条 19 家的账，它只把 §3 那笔税与第二档的价值量了出来。

## 8. 与 `IncludeMany` 的分界

| | `IncludeMany` | 树形三颗口 |
| --- | --- | --- |
| 管什么 | 往**同一批父行**上缝孩子 | 决定**哪些行回来** |
| 深度 | 一层，或显式链到第 N 层（`ThenIncludeMany`） | 到上限为止，层数由数据决定 |
| 方向 | 只有父 → 子 | 子 → 父也有（`Ancestors`） |
| 主查询 | 一字不改 | 整片档一字不改；逐层爬档不发主查询 |
| 叠着用 | — | 同一句链上互斥（§5） |

要"部门树 + 每个部门的负责人"，先取树、再对那一批节点单发一条 `IncludeMany`——两件事各自一句，句数账各读得清。
