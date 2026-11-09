# 08 · 分组与聚合

> 本篇解决：GroupBy/Having、组内聚合、复合键、分组后的计数与分页、哪些写法会当场拒。

`GroupBy` 走另一条类型链（`ISelectGrouping<TKey, T>`），出口只有 `Select`——组里能写什么、不能写什么由**类型**决定，不是运行期校验。

## 1. 基本形状

```csharp
var top = await orm.Select<Order>()
    .Where(o => o.Amount > 100m)                          // 分组之前的条件 → WHERE
    .GroupBy(o => o.CustomerId)                           // → ISelectGrouping<int, Order>
    .Having(g => g.Count() > 1)                           // 分组之后的条件 → HAVING（可多条，折成 AND）
    .OrderByDescending(g => g.Sum(o => o.Amount))
    .Take(5)                                              // 页与组同一单位：5 是"五组"
    .Select(g => new { g.Key, Total = g.Sum(o => o.Amount) })
    .ToListAsync();
```

- 链上成员：`Having`、`OrderBy/OrderByDescending`（排序后另有 `ThenBy/ThenByDescending`）、`Skip/Take`、`Select`。**没有 `Where`**——分组前的条件写在 `GroupBy` 之前，分组后的条件属于 `Having`，这一点由类型决定。
- 组内能读的只有 `g.Key` 与聚合：`g.Count()` / `g.LongCount()` → `COUNT(*)`；`g.Sum/Max/Min/Average(o => o.列)` → 各家模板。
- 聚合的 lambda 形参绑回同一张表位，产出 `SUM([t0].[Amount])`，不是子查询。

## 2. 复合键

```csharp
var rows = await orm.Select<OrderItem>()
    .GroupBy(i => new { i.OrderId, i.Sku })
    .Select(g => new { g.Key.OrderId, g.Key.Sku, Total = g.Sum(i => i.Price * i.Quantity) })
    .ToListAsync();
```

复合键读法只能**到成员**这一层（`g.Key.OrderId`）；`g.Key` 整体（那个匿名对象）在库里没有对应物，用它会当场抛。

## 3. 分组后的计数与分页：先 `Select` 投影回查询链

分组链本身没有计数/分页出口；先 `Select(...)` 投影回 `IQuery<TResult>`，计数与分页把整份分组包进派生表内层：

```csharp
var byCustomer = orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .OrderByDescending(g => g.Count())
    .Select(g => new { CustomerId = g.Key, Orders = g.Count() });

// 总组数：SELECT COUNT(*) FROM (SELECT … FROM … GROUP BY CustomerId) AS [t0]
var total = await byCustomer.CountAsync();
// 内层没有分页时，计数顺手剥掉 ORDER BY（T-SQL 与 PG 系都拒绝"带序而无页"的派生表）

// 分组分页：页与组同一单位，总数也是组数
var page = await byCustomer.ToPagedAsync(1, 20, new PagedOptions(CountMethod.CountStar));
// 点名 CountStar 拿得到 Total；用默认 Auto 时首页只多取一行判 HasNext，Total 是 null
```

- `AnyAsync` 同样走投影后的链：`SELECT 1 … GROUP BY … LIMIT 1` 交回一行 ⟺ 至少有一组，不数完。
- **这一档只接 COUNT 一族**：外层要引用内层的某一列（分组之后再 `SumAsync`/`MaxAsync`/`MinAsync`/`AverageAsync`）没有形状，当场抛。要"每组之和再取最大"用原生 SQL（[14 篇](14-raw-sql.md)）。

## 4. 当场拒的写法

| 写法 | 原因 |
| --- | --- |
| `GroupBy` 之后的 `Where` | 写不出来（类型上没有）；分组前条件用外层 `Where`，分组后用 `Having` |
| 联表链上 `GroupBy` | 只长在 `IQuery<T>` 单表契约上，`IQuery<T1,T2>` 没有——"联表之后再分组"今天没有对应物 |
| `g.CountDistinct(…)`、`g.Count(o => …)` | `COUNT(DISTINCT x)` 与 `FILTER` 各家不同，不假装统一 |
| 分组链上 `First` 一类 | 没有 `GROUP BY` 对应物 |
| 无序分页 + `AutoOrderPrimaryKey` | 主键不在分组键里，补出来不合法（这一档兜底不适用） |

## 5. `Distinct` 计数

`Distinct()` 之后的 `CountAsync` 数的是**去重后的行**（同一派生表咽喉）；带 `Skip/Take` 的链上计数数的是**那一页**。`ToPagedAsync` 的总数另算——它先摘掉偏移与上界再数，得跨页总数。

---

[← 上一篇：07 联表与导航](07-joins-and-navigation.md) · [下一篇：09 工作单元与变更跟踪 →](09-dbcontext-and-tracking.md)
