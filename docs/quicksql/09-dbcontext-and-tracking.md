# 09 · 工作单元与变更跟踪

> 本篇解决：`DbContext`/`DbSet`、跟踪读与写、`SaveChanges` 的批次顺序、级联（新增/摘行/到位）、乐观锁冲突、共享会话、先建后接。

链式入口（`orm.Insert/Update/Delete`）是"一句一执行"；要"**改一批实体、由库判定脏列、一个批次提交**"用工作单元：派生一个 `DbContext`，末尾 `SaveChangesAsync()`。

## 1. 定义与基本用法

```csharp
public class AppDb(QuickSqlOrm orm) : QuickSql.DbContext(orm)   // 官方推荐形态：主构造器
{
    public DbSet<Order> Orders => Set<Order>();
}

await using var db = new AppDb(orm);

var order = await db.Orders.Where(o => o.Id == 7).FirstOrDefaultAsync();  // 跟踪读
order!.Note = "改一下";                                                   // 改属性
var report = await db.SaveChangesAsync();                                  // 只发脏列的 UPDATE
Console.WriteLine($"{report.StatementCount} 句 / {report.AffectedRows} 行");
```

`DbSet<T>` 直接继承整条查询链（`Where/OrderBy/Include/...`），所以 [06-08 篇](06-query.md)的全部读法在会话里通用；不同在于**默认跟踪**（要非跟踪显式 `.AsNoTracking()`）。

DI 场景把 `AppDb` 注册成 Scoped 即可；`orm.CreateDbContext<AppDb>()` 存在（走 Activator，AOT 场景建议自己 `new`）。

## 1.5 模型钩子：`OnModelCreating`（可选）

派生类覆写它，用 `ModelBuilder` 配实体（EF 同形）——**覆写即整份模型**：这一只上下文走自己配出的那份 Fluent，组装根上的 `QuickSqlOrmOptions.Model`（orm 级全局默认）不再参与；不覆写的上下文零变化。

```csharp
public class AppDbContext(QuickSqlOrm orm) : QuickSql.DbContext(orm)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Order>(e =>
        {
            e.ToTable("Orders_Archive");                       // 表名改判
            e.Property(o => o.CustomerName).HasColumnName("CustName");   // 列名改判
            e.Property(o => o.Amount).HasDefaultValue(0m);     // 默认值（13 篇 §7）
        });
    }
}
```

- **触发时机**：首次需要模型的那一刻（`Set<T>()`/`Add`/…，即跟踪器懒建那一格），不在构造里——基类构造里虚调派生成员是老坑，EF 为同一理由也懒触发。
- **每派生类型恰跑一次**：配置按上下文类型缓存，同型的两只实例共享同一份模型。
- **隔离**：两个上下文类型各配各的，互不串扰；`orm.Select<T>()`（组装根那份模型）逐字不变。
- `mb.Entity<T>()` 点过名的实体会预登记进会话注册表（不摸过的那只也能按覆写模型解析）。
- **已知边界**：`orm.SyncStructureAsync`（结构同步）走组装根那份注册表——覆写模型里新配的实体不在它的清单上，要同步就同时喂给 orm 级 `orm.Schemas.Contract<T>()`。

## 2. `DbSet<T>` 全部成员

| 成员 | 说明 |
| --- | --- |
| `Add(entity)` / `AddRange(...)` | 标记 Added |
| `Attach(entity)` | 标记 Unmodified（无主键的实体拒收并指路） |
| `Update(entity)` | 标记 Modified（整行） |
| `Remove(entity)` / `RemoveRange(...)` | 标记 Deleted |
| `Entry(entity)` | 拿 `EntityEntry<T>`：`State`、`IsModified`、`ModifiedMembers`、`Property(x).IsModified/CurrentValue/OriginalValue` |
| `Find(params keys)` / `FindAsync(ct, keys)` | 先查身份映射（会话内缓存），没有再查库 |
| `Local` | `LocalView<T>`：会话里已跟踪的实体（`Entries`、`Find(keys)` 内存查） |
| `Load<TProp>(导航)` | 补载：对会话里每只实体发 SQL 把子实体拿回来缝回导航属性（引用与集合两档都接） |
| `Refresh(entity)` / `RefreshAsync(...)` | 从数据库重读，覆盖当前值 |

## 3. `ChangeTracker`

| 成员 | 说明 |
| --- | --- |
| `Count` / `HasChanges` | 跟踪数 / 有没有待写变更 |
| `Entries()` / `Entries<T>()` | 全部/按型的 `EntityEntry` |
| `DetectChanges()` | 手动触发快照比对（`AutoDetectChangesEnabled = false` 时自己调） |
| `Clear()` | 清账（共享会话的"结束"就是它或流出作用域） |
| `Entry(entity).Property(x => x.Note).IsModified = true` | 点名列强制算脏（快照没变也写） |

状态机 `EntityState`：`Detached / Added / Unmodified / Modified / Deleted`。

## 4. `SaveChanges` 的行为

- **只写脏列**：`UPDATE` 的 `SET` 只含 `ModifiedMembers`；值没变的实体不发语句。
- **干净即空转**：没有欠语句时**不发 SQL、不开事务**。
- **三档顺序**：Insert → Update → Delete。唯一例外：**同一张表这一轮既插又删时，删除整档提前**——腾开业务唯一键（明细行号、顺序位），避免 `INSERT` 撞上本轮自己要删的那行。
- **外键依赖排序**：写入顺序按契约里的外键依赖解出（父先子后），解不出（成环）在发语句前抛。
- **报告**：`SaveChangesReport { Operations[]（Operation/Table/Rows/StatementCount/Columns/Sql）, HookReplay, AffectedRows, StatementCount }`。注意 `AffectedRows` 在 UPDATE/DELETE 档是**谓词命中数**——"没有欠语句"与"发了但没命中行"是两件事。
- **乐观锁**：并发列旧值进 `WHERE`，命中 0 行抛 `DbUpdateConcurrencyException`（带 `Entity`、`Operation`、`CurrentValues`、`DatabaseValues`、`RowIsGone`）。
- **写后读**：会话里这条流上写过的集群，之后的读自动回主库（见 [12 篇](12-split-tables-and-sharding.md)）；`Dispose/DisposeAsync` 时注销这枚戳子（两段式释放，先同步注销再还连接）。
- **分表实体可读不可写**：`SaveChanges` 碰到分表模板上的待写实体整批拒并指路（链式侧不受影响）。

## 5. 级联：主从明细一起管

导航属性默认级联（`[NoCascade]` 退出），`SaveChanges` 时：

```csharp
// ① 带着子实体一起进：父 Add，子实体经导航挂进去，各自建档
db.Orders.Add(new Order
{
    CustomerName = "甲",
    Items = { new OrderItem { Sku = "A", Quantity = 1 }, new OrderItem { Sku = "B", Quantity = 2 } },
});
await db.SaveChangesAsync();       // 一批：先父后子，外键自动补

// ② 摘行：从集合里移除的子行 = 删除
order.Items.Remove(order.Items[0]);
await db.SaveChangesAsync();       // 该子行进删除档（前提：这根边是级联的；摘掉那只名下的子串要先捞进会话，捞不到就拒）

// ③ 到位：写之前把新子实体塞进集合 = 插入
order.Items.Add(new OrderItem { Sku = "C", Quantity = 3 });
await db.SaveChangesAsync();
```

- `IncludeMany` 缝上来的子实体**自己在会话里建档**——"改完子实体再 `SaveChanges`"这条路成立。
- 摘行就是删行（裁决语义）：编辑界面"明细整串换掉"是库的原生语义；不想被删就给那根边挂 `[NoCascade]`。
- 链式写侧没有这些语义，那是会话专属。

## 6. 共享会话：`CreateSharedContext`

不做 DI 注册也要跨方法传会话（控制台、后台服务）：

```csharp
using var db = orm.CreateSharedContext<AppDb>();
// 同一条 async 执行流上再取，拿到的还是这一只
```

三条边界：

1. **共享范围是一条执行流**（AsyncLocal），不是进程/线程；`await` 之后的子任务里建的那只不回父帧。
2. 父帧先建、扇出的并发分支拿到的是**同一只**——`ChangeTracker` 非线程安全，并发各自开流或各自 `CreateDbContext`。
3. 会话没有显式 `Dispose` 要求，"结束"= 流出作用域或 `ChangeTracker.Clear()`。

## 7. 先建后接：`UseOrm`（DI 容器先建类型）

宿主（如 ABP 集成层）要让 DI 容器先构造派生 `DbContext`、运行期才知道用哪只 orm 时：

```csharp
public class AppDb : DbContext
{
    protected AppDb() { }                // 保护构造：先建出来
    public AppDb(QuickSqlOrm orm) : base(orm) { }

    public void Wire(QuickSqlOrm orm) => UseOrm(orm);   // 组装根到手后接线（首次使用前必须接）
}
```

- 接线之前用 `Set<T>()`/`SaveChanges` 会抛可读的 `QuickSqlException`（守卫态，不埋空引用）；
- `HasOrm` 可判断接没接；一只会话只接一场，接第二场当场拒；
- `SaveChangesAsync`/`Dispose`/`DisposeAsync` 都是 `virtual`，宿主可整段覆写（ABP 工作单元接管就是这个口）。

---

[← 上一篇：08 分组与聚合](08-groupby-aggregate.md) · [下一篇：10 事务 →](10-transactions.md)
