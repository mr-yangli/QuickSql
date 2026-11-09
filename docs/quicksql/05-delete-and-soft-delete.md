# 05 · 删除与软删除

> 本篇解决：三种删除入口、复合主键的支持范围、软删除注册后"DELETE 变 UPDATE"的改写规则、"谁删的"扩展位。

## 1. 三个删除入口

```csharp
// ① 按条件（链式）
await orm.Delete<Order>().Where(o => o.CreatedAt < cutoff).ExecuteAffrowsAsync();

// ② 按实体那一行（按主键；复合主键在这一档支持）
await orm.Delete(order).ExecuteAffrowsAsync();

// ③ 按主键删整批（单列主键；复合键装不进 WHERE 键 IN (…) 所以拒）
var report = await orm.DeleteRange(orders).ExecuteBulkAsync();   // BulkWriteReport
```

- 链式没有 `Where` 会构建期抛（同 `Update` 的纪律）；全局过滤器自动叠进 `WHERE`。
- `orm.Delete(实体)` 对复合主键逐列 `=` 再 `AND`，分表实体按这只实体自己的分片键换算那一张片。
- `DeleteRange` 返回 `IDeleteBulk<T>`：出口 `ExecuteBulkAsync()` → `BulkWriteReport`，另有 `WithBulk(BulkWriteMethod…)` 与 `WithBatchTransaction()`（与 `UpdateRange` 同形，见 [04 篇](04-update.md)）；诊断 `Explain()` / `ToSql()`。

## 2. 软删除：注册一处，四个出口一起改写

软删不是删链上的参数，而是按实体注册的行为。注册之后，**四处删除出口**（链式 `Delete<T>()`、`orm.Delete(实体)`、`orm.DeleteRange(rows)`、`SaveChanges` 里被摘掉的行）都不再发 `DELETE`，改为"置位 + 记时间"的 `UPDATE`：

```csharp
orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt);              // 置位 + 记时间
orm.Behaviors.SoftDelete<Order>(o => o.Deleted, clock: () => DateTime.UtcNow);   // 缺省本地时钟，要 UTC 自己给

await orm.Delete<Ticket>().Where(t => t.Id == 7).ExecuteAffrowsAsync();
// 发出去的是：UPDATE Ticket SET Deleted = 1, DeletedAt = @p0 WHERE Id = 7 AND <全局过滤器>
```

规则与边界：

- **改的是形状，不是范围**：`WHERE` 一个字不动（过滤器照旧叠）。`Explain()` 里 `DeletePlan.Ast` 仍是你要求的 `DELETE`，`Sql` 才是发出去的 `UPDATE`——两句都真。
- **标记列必须是 `bool`**（写入恒为 `true`）；`0/1` 整数标记或 `"Y"/"N"` 字符标记不是这一档，自己写 `orm.Update<T>().Set(...)` 或订 `CudBuilding` 改写。
- `deleted` 与 `at` 要的是**成员读法**（`t => t.Deleted`），不是谓词：`t => !t.Deleted`、`t => t.A.B`、`t => t.DeletedAt!.Value` 注册期就拒。
- **接口键一次注册覆盖全部实现**：`SoftDelete<ISoftDelete>(e => e.Deleted)`；缺列的表第一次被删时才报，消息同时点名注册与缺列。
- 同一把键注册两次当场拒；两只注册册（接口键 + 具体类键）重叠时按注册序取第一条匹配的。
- **时钟在构建语句时读，一次构建一颗**：一条链一颗、一次 `DeleteRange` 整批一颗、一次 `SaveChanges` 一颗；`ToSql()` 预览两次不多读。
- **软删 ≠ 过滤**：`Behaviors` 管"删除落库里是哪一句"，`Filters`（[11 篇](11-filters-behaviors-aop.md)）管"读到的集合里有没有已删行"。只注册行为，已删行仍会被查出来；两个都注册才是完整的软删。

## 3. "谁删的"：`Aop.SoftDeleteValues`

软删自己只写两格（置位 + 时间）；"谁删的"是调用方的领域事实，库里不知道当前用户是谁。订阅这个钩子把额外的 `SET` 项加上去：

```csharp
var byColumn = orm.Schemas.Contract<Ticket>().ByMember[nameof(Ticket.DeletedBy)];

orm.Aop.SoftDeleteValues = e =>
    e.ExtraValues.Add(new(byColumn, currentUser.Name));
```

- 逐行触发（有行才触发）；链式 `Delete<T>().Where(...)` 删的是一批行、没有行可给，那一档**不触发**（那一档要自己带上操作者，用 `Update` 链或 `CudBuilding`）。
- 库负责把新值参数化、写出去，并按与那两格同一把尺子验（自增列与并发列不收）。

## 4. 硬删与软删并存

没在 `Behaviors` 上注册的实体，所有删除出口发的就是物理 `DELETE`。想对同一张表两种都要：注册软删后，物理删除用 `orm.Ado.ExecuteNonQueryAsync("DELETE FROM …")` 逃生舱（文本一颗不动），或把软删注册拆到另一只 orm 实例上。

---

[← 上一篇：04 更新数据](04-update.md) · [下一篇：06 查询数据 →](06-query.md)
