# 04 · 更新数据

> 本篇解决：按条件点名改几列、按实体稀疏改（null 不写库）、按实体整行批量改（含 upsert 档）、"清空某一格"怎么写。

QuickSql 有**三条互相独立**的更新路，按意图选而不是按写法选：

| 入口 | 意图 | `SET` 里有什么 |
| --- | --- | --- |
| `orm.Update<T>().Set(…).Where(…)` | 按条件点名改几列 | 只有点名的列 |
| `orm.Update(实体)` | 按主键改**这只实体带来值的列** | 非空属性（值类型永远在场） |
| `orm.UpdateRange(实体集)` | 按主键整行改（除主键/自增/并发列全部写回） | 几乎全列，null 真写 `NULL` |

## 1. 按条件更新（链式）

```csharp
await orm.Update<Order>()
    .Set(o => o.Note, "只改备注")
    .Set(o => o.State, OrderState.Paid)
    .Where(o => o.Id == 7)
    .ExecuteAffrowsAsync();
```

- `Set` 可多次；`Set(列, 表达式)` 二形可用其他列计算值（如 `Set(o => o.Amount, o => o.Amount + 10m)`）。
- **没有 `Where` 会在构建期抛**——不允许无条件的全表 `UPDATE` 溜出去。
- 全局过滤器（[11 篇](11-filters-behaviors-aop.md)）自动叠进 `WHERE`，排在调用方条件之后。
- `Set` 与 `Where` 只有在出口/诊断口才翻译表达式（入口无反射税）。
- 分表实体先 `.SplitTable(片)`（必须在链首），见 [12 篇](12-split-tables-and-sharding.md)。

## 2. 按实体稀疏更新（null 读作"没说"）

```csharp
var patch = await db.Orders.Where(o => o.Id == 7).FirstOrDefaultAsync();  // 任何来源的实体都行
patch.Note = "只改备注";
await orm.Update(patch).ExecuteAffrowsAsync();                    // SET 只含被赋过值的非空列
```

- 按主键定位；**值为 null 的属性不进 `SET`**（读作"这一列这次没说"）；值类型列永远带来值（`int Amount = 0` 不是"没填"）。
- 交回的仍是 `IUpdate<T>`，可以再 `.Where(...)` 补条件（叠在主键之后、全局过滤器之前）。
- 实体已带来值的那一列再 `.Set` 一次会被"设置了两次"守卫拦下——本库的答案不是后写覆盖先写，而是要求说清哪个值算。
- **"清空某一格"只有链式做得到**：`.Set(o => o.Note, (string?)null)`；`orm.Update(实体)` 永远不会把 null 写进库。
- 审计钩子 `Aop.AuditValue` 在这条路上触发（`OperationKind = "update"`，与 `UpdateRange` 同一把列尺子）。

## 3. 按实体整行批量更新：`UpdateRange`

```csharp
foreach (var o in orders) o.State = OrderState.Done;
var report = await orm.UpdateRange(orders).WithBatchTransaction().ExecuteBulkAsync();

// 要更快的形态（拼大句/合并/upsert）：
var r2 = await orm.UpdateRange(orders)
    .WithBulk(BulkWriteMethod.Merged)      // Auto | Merged | Upsert | SingleRow
    .ExecuteBulkAsync();
```

```csharp
public enum BulkWriteMethod { Auto, Merged, Upsert, SingleRow }
```

- **按主键整行改**：除主键、自增列、并发列外全部写回——**那一格是 null 就真写 NULL**（与 `orm.Update(实体)` 正对面：这一档的意思是"库里这一行 = 我手上这一行"）。
- 出口：`ExecuteBulkAsync()` → `BulkWriteReport`（`Requested`/`Method`/`RowCount`/`RoundTrips`/`ChunkSize`/`Degraded`/`Reason`/`UsedTransaction`/`AffectedRows`）；诊断 `Explain()`/`ExplainBulk()`/`ToSql()`。
- `WithBulk(Upsert)` 即"主键命中就改、不命中就插"的整行档（`orm.InsertOrUpdate<T>().SetSource(…)` 是同一句文本的另一扇门，见 [03 篇 §6](03-insert.md#6-一句-upsertinsertorupdate)）。
- `WithBatchTransaction(atomic = true)`：整批包进一条事务。
- 集合导航属性在这条链上被拒（它不是列）。

## 4. 脏列与并发列

- 并发令牌列（`[ConcurrencyToken]` 等，见 [02 篇 §3.2](02-entity-mapping.md#32-并发令牌乐观锁)）不进 `SET`、不进 `INSERT` 列清单，进 `WHERE` 与回填集；NULL 快照折成 `IS NULL`。
- 会话（`SaveChanges`）那条路的 `UPDATE` 只写被判定为脏的列——那是另一条路的职责划分，见 [09 篇](09-dbcontext-and-tracking.md)。

## 5. 诊断

```csharp
var plan = orm.Update<Order>().Set(o => o.Note, "x").Where(o => o.Id == 7).Explain();
Console.WriteLine(plan.Sql);        // 发出去的语句
Console.WriteLine(plan.Reason);     // 计划自陈（软删改写、过滤器叠入等都会写在这里）
Console.WriteLine(plan.ToLogSql()); // 内联字面量版（调试看参数值用）
```

---

[← 上一篇：03 插入数据](03-insert.md) · [下一篇：05 删除与软删除 →](05-delete-and-soft-delete.md)
