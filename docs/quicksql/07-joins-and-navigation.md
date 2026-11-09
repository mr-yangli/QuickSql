# 07 · 联表与导航

> 本篇解决：显式 Join、`Include` 家族（整只取回）、集合导航 `IncludeMany`、引用导航自动联表（不写 Include 也联）、拆分查询、子查询。

## 1. 显式联表：`LeftJoin` / `InnerJoin`

```csharp
var rows = await orm.Select<Order>()
    .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)      // 形参按 FROM 位置解析
    .Where((o, c) => c.City == "上海")
    .Select((o, c) => new { o.Id, o.Amount, City = c.City }) // 到 arity 2 的投影
    .ToListAsync();
```

- 联表 arity 递增：`IQuery<T1,T2>` → `LeftJoin<T3>` → `IQuery<T1,T2,T3>`，`Select` 后回到单表链。
- 别名按位置生成（`t0`/`t1`…），C# lambda 形参名不影响产出。
- 联表链上的出口：`ToListAsync(projector)` / `ToPagedAsync(projector, page, size)`（带投影直出），`Where`/`OrderBy`/`Skip`/`Take`/`Explain` 等。
- 联表之后**没有** `GroupBy`（编译期就过不去，见 [08 篇](08-groupby-aggregate.md)）。

## 2. `Include`：整只取回一条引用导航

```csharp
var orders = await orm.Select<Order>()
    .Include(o => o.Customer)                 // 主查询加一张 LEFT JOIN + 扩展列
    .ThenInclude(c => c.Region)               // 每层一张
    .Where(o => o.Amount > 100m)
    .ToListAsync();
```

- `Include`/`ThenInclude` 是**引用导航专用**（不接受集合，编译期接不上去）；每层往主查询加一张 `LEFT JOIN` 并发扩展列，缝合回导航属性。
- 两层链 = 两张 JOIN，第二层宿主在第一层那张表位上。
- `AsSplitQuery(navigateBatchSize = 20)`：主查询不发扩展列，整只实体改由主查询之后的一条独立 `SELECT` 缝回；谓词里读同一根边的那张 JOIN 照发（它来自查询模型，与拆查无关）。**与 `IncludeMany` 互斥**（混了当场抛，见下一节）。
- `Include` 与"谓词里读导航"共用**同一张 JOIN**（先写谁都一样），不会发两张。

## 3. `IncludeMany`：集合导航另发查询缝合

```csharp
var order = await orm.Select<Order>()
    .Where(o => o.Id == id)
    .IncludeMany(o => o.Items!)               // 第二条：SELECT … FROM OrderItem WHERE OrderId IN (@p0)
    .ThenIncludeMany(i => i.Parcels!)         // 第三条：… WHERE OrderId IN (@p0, @p1)
    .ThenInclude(p => p.Warehouse)            // 同层旁边那根引用导航：再一条
    .FirstOrDefaultAsync();
```

- `IncludeMany` **一个字不改主查询**，往下每一层各发一条 `SELECT … WHERE 主键 IN (上一批父键)`，内存缝合。
- `ThenIncludeMany` 的形参是**一只**上一层的子实体（`i => i.Parcels` 里 `i` 是 `OrderItem`，不是集合）；下一层挂谁由类型系统在编译期定死。
- `IncludeMany(...).ThenInclude(...)` 挂的是这一层**旁边**的引用导航（不是下一层）；重复调用是在同一层并列多挂几根。
- **深度就是查询条数**：根一条 + 每层一条；上一层没行就当场停，不会有空 `IN`。
- 子行排序：`IncludeMany(o => o.Items!).OrderByMany(i => i.Seq)`。
- 外键解析三档（每层各判一次）：子实体回指引用导航 → 集合成员上的 `[ForeignKey("列名")]` → 约定 `{父类型名}Id`；三档都命不中当场抛。
- **`AsSplitQuery()` 与 `IncludeMany` 互斥**（2026-11-01 起当场抛 `QuickSqlException`，此前是静默丢集合）：把 `IncludeMany` 链单独成句发一条，或去掉 `AsSplitQuery()` 走合批加载。两条接链顺序都在接链那一刻响。
- 投影之后不能再挂 `IncludeMany`（"把 IncludeMany 放到 Select 之前"）。

### 键回填与缝不上的行

按子键捞目标（旁边那根引用导航）按键去重；配不到行的子实体留在结果里、导航为 `null`，不会被筛掉。

## 4. 引用导航自动联表：不写 `Include` 也联

谓词、排序键、投影里读到一条**引用**导航的**成员**，库按契约自动补 `LEFT JOIN`：

```csharp
var rows = await orm.Select<Order>()
    .Where(o => o.Customer!.City == "上海")               // 谓词里读 → 联 Customer
    .OrderBy(o => o.Customer!.Name)                        // 排序键里读 → 还是那一张表位
    .Select(o => new { o.Id, City = o.Customer!.City })    // 投影里读 → 三处共用同一张
    .Skip(20).Take(10)
    .ToListAsync();
```

- **ON 由契约推出来**（那条关系登记的外键列 ↔ 目标主键列），不是"属性名 + Id"猜测。
- 同一条边读几次都只联一张；两条不同的边各占一个表位；`Include` 的表位与它合并（同一根边不重复发）。
- **是 `LEFT` 不是 `INNER`**：主表的行不因对端缺失而消失（对端为空时投影列是 `NULL`，DTO 槽位按可空声明；`o.Customer!.Name` 的 `!` 只为过编译器）。
- **分页在联表之后**：`OFFSET/FETCH` 数的是联完的主表行。
- 模型可读：`Explain()` 的 `ModelText` 里有 `JOIN Left … ON …`，"这一句联了谁"不用猜。

**四格当场拒**（宁可报错也不发看似合理的错句）：

1. 谓词/投影里读**集合**导航（`o.Items.Count`、`o.Items.Any(...)`）——一条 JOIN 明细会把主行按明细数复制，页码、行数、聚合三处一起改语义。要的是相关子查询或 `IncludeMany`；
2. 整只导航当值读（`(object)o.Customer == null`）——往下写一格 `o.Customer!.Id == null`；
3. 显式 `LeftJoin`/`InnerJoin` 的 ON 里读导航；
4. 写侧（`Update`/`Delete`）不联表。

## 5. 子查询

**先把子查询链捕获进变量，再在外层谓词里调终端方法**（直接内联在链式调用里不认）：

```csharp
// 标量子查询：(SELECT COUNT(*) FROM Orders) > 3
var allOrders = orm.Select<Order>();
var list = await orm.Select<Customer>().Where(c => allOrders.Count() > 3).ToListAsync();

// IN 子查询：Id IN (SELECT CustomerId FROM Orders WHERE Amount >= 500)
var highValueIds = orm.Select<Order>().Where(o => o.Amount >= 500m).Select(o => o.CustomerId);
var customers = await orm.Select<Customer>().Where(c => highValueIds.Contains(c.Id)).ToListAsync();

// 标量聚合子查询：Sum/Max/Min/Average 同形
var orderCount = orm.Select<Order>().Where(o => o.CustomerId == c.Id);
var busy = await orm.Select<Customer>().Where(c => orderCount.Count() >= 5).ToListAsync();
```

相关子查询里读导航：联表进的是**内层**那一句的 `FROM`，外层一个字不多。

## 6. 会话侧补载：`db.Set<T>().Load(...)`

工作单元里（见 [09 篇](09-dbcontext-and-tracking.md)）按导航补载子实体：

```csharp
var order = await db.Orders.Where(o => o.Id == 7).FirstOrDefaultAsync();
db.Orders.Load(o => o.Items);                  // 引用与集合两档都接：对会话里每只实体发 SQL，按 FK↔PK 缝回导航
```

父集合超过这家库的参数预算时按 `IN` 分块、每块一条子 `SELECT`。

---

[← 上一篇：06 查询数据](06-query.md) · [下一篇：08 分组与聚合 →](08-groupby-aggregate.md)
