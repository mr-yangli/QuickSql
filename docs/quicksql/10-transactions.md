# 10 · 事务

> 本篇解决：四种事务形态怎么选、保存点嵌套、事务与集群/读写分离的关系、无事务能力库的降级口径。

## 1. 四种形态

```csharp
// (1) 显式事务：块内语句共享一条连接与事务
using var tran = orm.BeginTransaction();                 // 默认 ReadCommitted
await orm.Insert(row).ExecuteAffrowsAsync();
await orm.Update<Order>().Set(o => o.Note, "x").Where(o => o.Id == 1).ExecuteAffrowsAsync();
tran.Commit();                                            // IQuickSqlTransaction { Completed, Commit(), Rollback() }

// (2) 包一段业务逻辑：结果对象，异常不打断流程
var result = await orm.ExecuteInTransactionAsync(async ct =>
{
    await orm.Insert(row).ExecuteAffrowsAsync();
    await orm.Insert(other).ExecuteAffrowsAsync();
});
if (!result.Success) logger.LogError(result.Exception, "业务失败：{Msg}", result.ErrorMessage);
// TransactionResult { Success, ErrorMessage, Exception, RollbackFailure }
// 泛型重载 ExecuteInTransactionAsync<T>(...) 多一颗 Value

// (3) 环境事务：挂在当前 async 执行流上，DbContext 自动并入
using var ambient = orm.BeginAmbientTransaction();

// (4) 会话事务：把事务钉在这只 orm 上（跨帧也进同一场）
using var pinned = orm.BeginSessionTransaction();
```

事务默认 `IsolationLevel.ReadCommitted`，可换；`BeginAmbientTransaction(level)` 同样收级别；`propagation: TransactionPropagation.RequiresNew` 开独立新事务（`IQuickSqlTransaction`/`TransactionResult`/`TransactionPropagation` 在 `QuickSql.Transactions` 命名空间）。**保存点路径上隔离级别继承外层**（SAVEPOINT 没有自己的隔离级别，SQL 标准行为）：嵌套时传了与外层不同的级别当场抛 `QuickSqlException` 指路 `RequiresNew`，传 `Unspecified`（没有表态）或相同级别照常放行。

### 四种形态怎么选

| 形态 | 用在 |
| --- | --- |
| (1) `BeginTransaction` | 直白的块状提交 |
| (2) `ExecuteInTransactionAsync` | 不想让异常把控制流炸穿（返回值判定）；回滚失败不盖掉业务异常原文（`RollbackFailure` 承旁路，两个都看） |
| (3) `BeginAmbientTransaction` | 装配层开事务、`DbContext` 会话里的语句要并入同一场 |
| (4) `BeginSessionTransaction` | **装配层在自己的 async 帧里开事务、语句却由调用方那一帧发**的形状（典型：ABP 工作单元）。环境事务靠 AsyncLocal，异步被调方写的槽位父帧看不见；这一形态归属"实例"而不只是"流"，隔帧也进同一场 |

## 2. 嵌套与保存点

- **同一座集群上嵌套 `BeginTransaction`（默认 `Requires`）= 保存点**：内层 `Commit` 释放保存点、`Rollback` 回到保存点，计数器按嵌套深度命名（多层嵌套不会互撞同名）。
- **嵌套传的 `IsolationLevel` 必须与外层一致（或 `Unspecified`）**：保存点继承外层的隔离级别，传不同级别当场拒——"要别的级别"的正当出口是 `RequiresNew`（挂起外层、按传入级别新开一条独立事务，两边互不影响）。
- `RequiresNew` 挂起外层、开真正的新事务；**新事务开不出来时被挂起的那条会恢复回槽位**，不会永远回不来。
- 跨集群的嵌套不是保存点（两阶段提交/XA 未建模）：`main` 的事务还开着时 `BeginTransaction("report")` 当场抛，消息写"请按集群分别开事务"。
- 一只实例同时只钉一场会话事务（再钉当场抛）；提交/回滚/`Dispose` 后摘钩，还能再钉。

## 3. 事务与集群

- 事务要钉在一座集群上：`orm.BeginTransaction()` 开在缺省集群 `main`，`BeginTransaction("report")` 点名开哪座。
- **一场事务一条连接**：事务内的一切语句（含读）都落那座集群的主库连接（事务内不换从库——换连接读到的就不是本事务的数据）。
- 事务里出现另一座集群的语句 → 当场抛（读写装载三条路都拦）。
- `SaveChanges` 一批跨两座集群 → 当场抛（按集群分两次保存）。
- `FromReplica()` 在事务内点 → 当场抛（在事务外读）。

## 4. 事务与 `DbContext`

- 会话里的语句自动并入环境事务（(3) 形态）：共享被事务独占的那条连接。
- 一批 `SaveChanges` 自带事务：发语句前开、全部成功后提交；中途失败回滚（有事务能力的库上）。
- 会话嵌套在同一只 orm 上共享一条流的连接工厂：先到当主人、后到不动字，里层先 `Dispose` 拆不掉外层的槽与连接。

## 5. 无事务能力的库（如 ClickHouse）

provider 没声明 `Transactions` 能力位时：

- 四个公开事务口**全部当场抛 `NotSupportedException`**（消息明说，绝不静默开假事务）；
- 内核写路径（`SaveChanges`/`InsertOrUpdate`/批量执行段）自动退成**逐句 autocommit 直发**；
- 代价要用实话记住：**一次保存/一次批量里的多条语句不原子**——中途失败时已发出去的语句不回滚，留在库里的是半批。要整批落库，用单条语句表达（多值 INSERT/批量链一条句子能装下的量）或自己分批排程。

## 6. AsyncLocal 纪律（为什么会话事务是形态 (4)）

环境事务挂在 `AsyncLocal` 槽位上，而异步被调方（哪怕全程 `await` 已完成的 Task）写入的值**父帧与兄弟帧都看不见**。所以"装配层 async 方法里 `BeginAmbientTransaction`、语句由调用方帧发"必然踩空——症状不是报错，而是"一场工作单元一次提交"静默退化：中途抛异常时前半段的写已经落库。识别出这种形状就用 `BeginSessionTransaction()`（归属从"流"扩成"流 *或* 实例"）。

另外两条常识：

- 同一只 orm 实例上不要并发使一场事务（一条连接一个 `DbTransaction`；`Task.WhenAll` 扇出的分支各自开流或各自建实例）。
- 两只 orm 实例不能互相塞事务（槽位挂在实例上）。

---

[← 上一篇：09 工作单元与变更跟踪](09-dbcontext-and-tracking.md) · [下一篇：11 全局过滤器与 AOP →](11-filters-behaviors-aop.md)
