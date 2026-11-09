# 11 · 全局过滤器、行为与 AOP

> 本篇解决：全局查询过滤器（多租户/软删过滤）、软删行为注册、全部 AOP 钩子（观察/改写/决策）、审计与日志、缓存口、DI 里的按请求装配。

这三只对象都挂在 `orm` 实例上：`orm.Filters`（过滤器）、`orm.Behaviors`（行为）、`orm.Aop`（钩子）。

## 1. 全局查询过滤器

```csharp
orm.Filters.Add<Ticket>(t => !t.Deleted);                           // 读、Update、Delete 三条链都自动带上
orm.Filters.Add<Customer>(c => c.TenantId == 7, mandatory: true);   // IgnoreQueryFilters 压不住的那条
orm.Filters.Add<Ticket>(t => t.TenantId == tenant.Id, mandatory: true);  // 可链式注册多条
```

- 谓词是 `Expression<Func<T, bool>>`，注册键 `T` 可以是**接口或基类**（如 `ISoftDelete`，一次覆盖全部实现类）；注册期先试翻译一次，写错了当场报。
- 过滤条件自动叠进 `WHERE`，排在调用方条件之后。它管读链与写链（Update/Delete），**不管 `SaveChanges` 与批量**（批量/会话那两档的过滤器语义与"被挡住的行算不算并发冲突"是一格未裁决的账）。
- 发出的 `WHERE` 里过滤与手写条件无法从语句上区分归属——这是特性不是缺陷：过滤就是"这条链上永远在场的条件"。

### 关闭过滤器

```csharp
orm.Select<Ticket>().IgnoreQueryFilters();                // 这一条链一条都不贴
orm.Select<Ticket>().IgnoreQueryFilter<ISoftDelete>();    // 只跳过注册键是 ISoftDelete 的那几条（可叠）
orm.Select<Ticket>().IgnoreQueryFilter<ISoftDelete>()
                  .IgnoreQueryFilter<Ticket>();
```

- 点名的判据是**注册键同一性**，不是继承：注册在接口上的要点名接口才跳得掉，点名实现类碰不到它。
- `mandatory: true` 的那条拦得住不带参的 `IgnoreQueryFilters()`，也拦"被它盖到的点名"——点名实体（没关掉任何不可忽略的东西）照常通过，点名 mandatory 的键当场抛。
- 写链（`IUpdate<T>`/`IDelete<T>`）**没有**对应的忽略口——写侧跳过过滤器的后果比读侧重，没给口子。

## 2. 行为注册（软删除）

`orm.Behaviors.SoftDelete<TEntity>(置位列, 时间列, clock)` 的完整语义见 [05 篇](05-delete-and-soft-delete.md)。同一把键注册两次当场拒；接口键一次覆盖全部实现类。

## 3. AOP 钩子

`QuickSqlAop` 是一组可空委托字段，订阅就是赋值（支持 `=` 与 `+=`）。分三类：**观察**（只读不拦）、**改写**（能改值/改文本）、**决策**（能取消）。

```csharp
orm.Aop.Executed = e => Console.WriteLine($"{e.ElapsedMilliseconds}ms {e.Sql}");
```

### 观察类

| 钩子 | 事件 | 时机 |
| --- | --- | --- |
| `Executing` | `CommandExecutingEvent(Sql, OperationKind, TableNames)` | 执行前（语句挂住时唯一还留得下的现场；耗时/行数/路由这一刻还没有） |
| `Executed` | `CommandLogEvent(Sql, OperationKind, TableNames, ElapsedMilliseconds, AffectedRows, FirstRowSummary, Route)` | 执行成功。**`Route` 说的是这句实际走了哪座库、哪个角色、按哪一档**（`DataSourceRoute(Cluster, Role, Reason)`） |
| `Error` | `CommandErrorEvent(Exception, Sql, ElapsedMilliseconds)` | 执行失败（异常是驱动原话） |
| `SlowQuery` | `SlowCommandEvent(Sql, ElapsedMilliseconds, ThresholdMilliseconds)` | 超过 `SlowQueryThresholdMilliseconds`（默认 500ms） |
| `Materialized` | `MaterializedEvent(Entity, EntityTypeName, OrdinalInBatch)` | 每只实体物化完成 |
| `EntityWritten` | `EntityWrittenEvent(Entity, OperationKind)` | **这一轮写出去的实体逐只点名**（insert/update/delete）。发射在语句建出来之前，订阅者既能观察（发领域事件）也能**改实体**（改出的值会被语句带出去）。只覆盖会话那条路（链式 `Insert/Update(实体)/Delete(实体)` 等不走跟踪器） |
| `BulkDegraded` | `BulkDegradedEvent(Requested, Method, RowCount, Reason)` | 批量高速层真降级时（`Auto` 取到文本层不算） |

`Error`/`SlowQuery` 手上没有 `Route`（路由读数只在成功路径上）——按库分组统计慢/错日志要从 `Sql`/`TableNames` 反推。

### 改写类

| 钩子 | 事件 | 触发点 |
| --- | --- | --- |
| `AuditValue` | `AuditValueEvent(Entity, Column, OperationKind, CurrentValue)` + `SetValue(v)` | 手上有整行实体的三条路：`orm.Insert`/`InsertRange`（insert）、`orm.UpdateRange(集)` 与 `orm.Update(实体)`（update）。经典用途：`CreatedAt`/`CreatedBy` 自动填充。链式 `Update<T>().Set(...)`、两条删除出口与 `SaveChanges` 的 update 档**不问它**（那些出口的改写槽是 `ParameterBinding`） |
| `ParameterBinding` | `ParameterBindingEvent(Parameter, Column?)` + `SetValue(v)` / `ChangeType(DbType, size)` | 全部参数绑定收口点（每颗参数都会经过） |
| `CudBuilding` | `CudBuildingEvent(OperationKind, Sql, Rewritten)` | CUD 语句文本建出来之后——整句文本级改写（软删 SQL 的自定义、加 hint 等） |
| `SoftDeleteValues` | `SoftDeleteValuesEvent(Entity, TableName, ExtraValues)` | 软删语句的额外 SET 项（"谁删的"，见 [05 篇 §3](05-delete-and-soft-delete.md#3-谁删的aopsoftdeletevalues)） |

改写过的步骤进 `HookTrace`，在 `Explain()` 与 `SaveChangesReport.HookReplay` 里回放——"这个值是谁在哪一步改的"可审计。

### 决策类

```csharp
orm.Aop.CommandTerminating = e => e.Sql.Contains("DROP");   // 返回 true = 取消这条语句
// 取消后执行口抛 QuickSqlCommandCancelledException
```

观察类钩子的异常会被吞掉并上报（日志代码不能打挂写入）；改写与决策类的异常原样出洞。

## 4. 缓存口

```csharp
orm.Aop.Cache = new MyRedisCacheAdapter();     // 实现 ICacheAdapter { TryGet<T>, Set<T> }
orm.Select<Order>().Where(...).WithCache("orders:paid", TimeSpan.FromMinutes(5));
```

仓库不带实现（没有进程内缓存默认档）；`WithCache` 在链上随时可写，没配 `Cache` 时是空转。

## 5. DI 里的按请求装配：`AfterBuild`

过滤器/行为/Aop 都挂在 orm 实例上，而"当前登录者属于哪家租户"是按请求的事实——`AddQuickSqlOrm` 的 builder 阶段拿不到它们，`AfterBuild` 清单就是给这个的：

```csharp
builder.Services.AddQuickSqlOrm(DbKind.SqlServer, connectionString, b =>
{
    b.AfterBuild.Add((sp, orm) =>          // 每个作用域解析时按登记顺序跑一次
    {
        var tenant = sp.GetRequiredService<ICurrentTenant>();
        orm.Filters.Add<Ticket>(t => t.TenantId == tenant.Id, mandatory: true);
        orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt);
        orm.Aop.Executed = e => logger.LogInformation("{Sql}", e.Sql);   // Aop 每请求一份
    });
});
```

四条口径：每个作用域解析跑一次（同作用域再取不重跑）；跑的就是交给调用方的那只实例；清单是注册那一刻的快照；不查重（同一颗钩子登记两遍跑两遍）。

---

[← 上一篇：10 事务](10-transactions.md) · [下一篇：12 分表分库与读写分离 →](12-split-tables-and-sharding.md)
