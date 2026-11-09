# 16 · 诊断与常见问题

> 本篇解决：怎么在不连库/连库两种状态下看见发出去的 SQL；异常对照表；高频踩坑 FAQ。

## 1. 诊断口

| 口 | 给什么 |
| --- | --- |
| `IQuery.Explain()` | `QueryPlan { Ast, Sql, ModelText, HookReplay, Provider }`——`ModelText` 是查询模型文本（联了谁、选了哪些列、过滤器在哪），`Sql` 是方言语句 |
| `ToSql()` / `ToSql(out parameters)` | 语句文本 / 语句 + 参数值（`AdoParameter { Name, Value, DbType, Size, DriverName }`） |
| `ToLogSql()` | 内联字面量版（参数直接拼进文本，调试看值用；别用于执行） |
| `IInsert/IUpdate/IDelete.Explain()` | 各自的 plan（`InsertPlan` 含 `Chunks`/`ParameterBudget`/`Reason`；`DeletePlan.Ast` 与 `Sql` 在软删改写时是两句都真） |
| `orm.Schemas.Contract<T>()` | 映射契约（表名、列、主键、关系、索引、每列的来历 `Origin`），见 [02 篇 §7](02-entity-mapping.md#7-契约读取不用连库就能看映射结果) |
| `Aop.Executed/Error/SlowQuery` | 运行期语句日志（含路由 `Route`），见 [11 篇](11-filters-behaviors-aop.md) |
| `SaveChangesReport.HookReplay` | 会话保存的钩子回放（哪个值在哪一步被谁改的） |

**离线产 SQL**：不需要驱动、不需要库——

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),
    ConnectionString = "Server=offline;Database=never;Integrated Security=True",   // 占位串
});
var sql = orm.Select<Order>().Where(o => o.Id == 7).ToSql();
```

## 2. 异常对照表

| 异常 | 什么时候抛 |
| --- | --- |
| `QuickSqlException` | 全家基类 |
| `UntranslatableExpressionException` | lambda 写了翻译不出的形状（无客户端求值兜底） |
| `MetadataConflictException` | 同一层元数据对同一事实重复声明（编译期 `QuickSql001` 同源） |
| `DriverNotAvailableException` | 驱动解析链走完仍找不到 |
| `ValidationFailedException` | 写入校验被整批拒（语句还没生成，库里什么都没发生；`Failures` 逐条定位，消息按「成员+规则」归并） |
| `DbUpdateException` 及四子类 `UniqueConstraintException` / `NotNullConstraintException` / `ForeignKeyConstraintException` / `CheckConstraintException` | 写入时完整性违例（带 `Sql`、`ConstraintName`；`Columns` 是冲突的库里列名——拿 `ConstraintName` 回契约查唯一索引，查得到就给、查不到（驱动不给名 ∶ 契约外对象）就是 null，不抛不猜） |
| `DbUpdateConcurrencyException` | 乐观锁冲突（带 `Entity`/`Operation`/`CurrentValues`/`DatabaseValues`/`RowIsGone`） |
| `SchemaSyncFailedException` | 结构同步 DDL 被库拒（带失败句、目标格、已生效清单） |
| `QuickSqlCommandCancelledException` | `Aop.CommandTerminating` 取消了语句 |
| `NotSupportedException` | 无事务能力库上调事务口；`WithBulk(CopyChannel)` 后调 `ExecuteAffrowsAsync` 等形状错配 |
| `InvalidOperationException` | 生成器未挂编译就 `Create`；非 Development 未开 `ForceSchemaSync` 就 Execute 等 |

## 4. 命名空间速查

| 命名空间 | 装什么 |
| --- | --- |
| `QuickSql` | `QuickSqlOrm`、`DbContext`/`DbSet`、`QuickSqlOrmOptions`/`ClusterConfig`、异常族、`DbKind` |
| `QuickSql.Query` | `IQuery` 族与 Include 扩展、`PagedOptions`/`PagedResult`、`PagingOrderingPolicy`、`CountMethod` |
| `QuickSql.Write` | `IInsert`/`IUpdate`/`IDelete`/`IUpdateBulk`/`IDeleteBulk`、`BulkWriteMethod`、各 Plan/Report 类型 |
| `QuickSql.Providers` | `BulkMethod`、`FallbackPolicy`、`BulkReport` |
| `QuickSql.Transactions` | `IQuickSqlTransaction`、`TransactionResult`、`TransactionPropagation` |
| `QuickSql.Tracking` | `EntityEntry`/`PropertyEntry`/`EntityState`/`SaveChangesReport` |
| `QuickSql.Execution` | `AdoExecutor`（`orm.Ado`）、`RawSql`、`ExecutionTag` |
| `QuickSql.Sql` | `DialectSql`（`ToSql()`/`ToLogSql()` 的返回类型） |
| `QuickSql.Metadata` / `QuickSql.Metadata.Fluent` | 特性、契约类型（`EntityContract`/`ColumnContract`）/ `ModelConfiguration` 与 builder |
| `QuickSql.Aop` | `QuickSqlAop` 与全部事件/钩子类型 |
| `QuickSql.Generated` | 源生成器产物（`QuickSqlGeneratedMetadata`） |

## 5. FAQ

### Q：装了包，`Create` 报"生成器未挂进编译"？
`QuickSql.Generators` 没装（或源码引用没按 `OutputItemType="Analyzer"` 挂）。NuGet 装法装上即生效；实体不标 `[Table]` 的也要装（provider 登记靠它）。

### Q：为什么我的属性没被映射成列？
按序自查：① `[NotMapped]`？② 只有 getter（判据是"有没有写口"，`{ get; protected set; }` 算有）？③ 是导航属性（被识别为关系）？④ 是集合属性？读 `orm.Schemas.Contract<T>()` 的 `Columns` 一眼定案。

### Q：插入后 `Id` 没回填？
看 [03 篇回填表](03-insert.md#4-自增键回填)：Oracle 系（Oracle/达梦/神通）**要自增键必抛**；批量回填各家档位不同；`ExecuteIdentityAsync` 的返回值是键本身、不回写实体。Oracle 系的替代：客户端生成主键（Guid/雪花）或插入后回查。

### Q：`Update(实体)` 为什么没把我清空的字符串写进库？
`orm.Update(实体)` 的语义是"null 读作这一列没说"——**清空某一格只有链式 `.Set(o => o.Note, (string?)null)` 做得到**。要"整行照我手上这份写"用 `orm.UpdateRange(...)`（null 真写 NULL）。

### Q：`SaveChanges` 没发任何语句？
值没变（快照比对后没有脏列）就是不发、不开事务。强制某一列算脏：`db.Entry(entity).Property(x => x.Note).IsModified = true`。

### Q：分页报"无 ORDER BY"？
T-SQL `OFFSET..FETCH` 硬性要求 `ORDER BY`。要么链上写 `OrderBy`，要么 `PagingOrdering = PagingOrderingPolicy.AutoOrderPrimaryKey`（自动补主键序）/ `Allow`。

### Q：`IncludeMany` 与 `AsSplitQuery` 同时写，集合没数据？
两者同句时拆查分派先赢、集合档被静默丢——别混用；集合导航要么 `IncludeMany` 要么拆查。

### Q：软删注册了，怎么还能查到已删的行？
`Behaviors` 管"删→UPDATE"，`Filters` 管"读侧筛掉已删行"——两只对象各管一半，完整的软删要都注册（见 [05 篇](05-delete-and-soft-delete.md)、[11 篇](11-filters-behaviors-aop.md)）。

### Q：事务里读了从库 / 数据不一致？
事务内一切语句钉在主库连接上（不会读从库）。裸链（非会话）上的"写后读"不生效——写后读只在会话（`DbContext`）里成立；裸链要新鲜读显式 `.FromPrimary()`。

### Q：MySQL 上 `CAST(x AS INT)` 报 1064？
收窄转换会发 `CAST`，目标型名取自 DDL 类型表——MySQL 不认 `AS INT`（认 `AS SIGNED`）。这是已知边界：改写 lambda 避免收窄（比如让两侧同型），别指望 CAST 文本可配。

### Q：PG 上写 `DateTime` 抛驱动错？
Npgsql 6+ 按 `Kind` 分型：`Kind=Utc` 的值进不了裸 `timestamp` 列。本库按列声明折型（裸列摘 Utc 标记、`timestamptz` 列换 `DateTimeOffset`）；列声明成 `timestamptz` 用 `[Column(TypeName = "timestamptz")]`。

### Q：大页码/大 `Take` 直接 OOM？
`Take(n)` 是"最多读 n 行"，读取器预容量有 8192 行天花板；分页偏移按 `long` 防溢出，真超 `int` 会当场抛并点名那一页。超大结果集用 `ToAsyncEnumerable(batchSize)` 流式读。

### Q：AOT/裁剪发布要注意什么？
手写注册那条路（自己 new provider + `GeneratedEntitySchemaRegistry`）构造期零反射；`Create`、`Select<T>` 默认口、`orm.Ado` 字符串口带 `RequiresUnreferencedCode/DynamicCode` 标注，AOT 发布时按编译器提示逐格承认或换链式强类型 DTO。

### Q：想看"这批插入切了几块、为什么"？
`orm.InsertRange(rows).Explain()` → `InsertPlan.Reason` 自陈预算与块数；`ToLogSql()` 打 `-- 块 n：第 a~b 行`（见 [03 篇](03-insert.md#3-分块是看得见的事实)）。

### Q：`ToPagedAsync` 的 `Total` 是 null？
`PagedOptions(CountMethod.SkipTotal)` 不算总数（`HasNext` 按多取一行判断）。跨片分页推荐这档。

---

[← 上一篇：15 多数据库支持](15-multi-database.md) · [回到文档中心](README.md)
