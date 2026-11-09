# QuickSql 文档中心

**QuickSql** 是一个面向 .NET 10 的 ORM：**一套实体与 LINQ 风格链式 API，覆盖 18 家数据库**（SQL Server、PostgreSQL、MySQL、Oracle、SQLite、达梦、人大金仓、虚谷、GBase 等国产库），源生成器在编译期生成实体契约实现零反射物化，内置工作单元、乐观锁、全局过滤器、软删除、AOP 审计、分表分库与读写分离、CodeFirst 结构同步。

> - 目标框架 `net10.0`，全部包为 `-beta` 预发布系列，MIT 协议。
> - 安装与快速上手直接看本页下半部分；分功能详解看下面的目录。

## 文档目录

| 篇 | 内容 |
| --- | --- |
| [01 安装与连接](01-installation-and-connection.md) | NuGet 装包、三种装配形态（一行注册 / 手写注册 / DI）、驱动解析、换数据库 |
| [02 实体定义](02-entity-mapping.md) | 特性标注、约定、Fluent API、主键与自增、并发令牌、导航与外键、契约读取 |
| [03 插入数据](03-insert.md) | 单行/批量插入、自增键回填、批量高速层（装载通道）、分块、InsertOrUpdate（upsert） |
| [04 更新数据](04-update.md) | 按条件更新、按实体稀疏更新、按实体整行批量更新、upsert 档 |
| [05 删除与软删除](05-delete-and-soft-delete.md) | 按条件/按实体/批量删除、软删除注册与「谁删的」扩展 |
| [06 查询数据](06-query.md) | 条件、排序、投影（含行号 `QuickFx.RowNumber`）、Distinct、分页与计数、存在性探针、聚合、流式读取 |
| [07 联表与导航](07-joins-and-navigation.md) | LeftJoin/InnerJoin、Include/ThenInclude、IncludeMany、引用导航自动联表、拆分查询 |
| [08 分组与聚合](08-groupby-aggregate.md) | GroupBy/Having、组内聚合、复合键、分组计数 |
| [09 工作单元与变更跟踪](09-dbcontext-and-tracking.md) | DbContext/DbSet、变更跟踪、SaveChanges、级联（新增/摘行/到位）、共享会话 |
| [10 事务](10-transactions.md) | 四种事务形态、保存点嵌套、跨集群规则、无事务库的降级口径 |
| [11 全局过滤器与 AOP](11-filters-behaviors-aop.md) | 全局查询过滤器、行为注册、全部 AOP 钩子（审计/日志/改写/取消）、缓存口 |
| [12 分表分库与读写分离](12-split-tables-and-sharding.md) | SplitTable 六档分片、具名集群、主从路由、写后读、拒绝规则 |
| [13 结构同步（CodeFirst）](13-schema-sync.md) | 三档策略、逐列比形状、建表/补列/改列、分表建片、DBA 脚本模式 |
| [14 原生 SQL](14-raw-sql.md) | `orm.Ado` 字符串口全家、参数规则、行装配、`WithSql` 表源替换 |
| [15 多数据库支持](15-multi-database.md) | 18 家 provider 总表、驱动安装、标识符策略、能力差异、跨库迁移五坑 |
| [16 诊断与常见问题](16-diagnostics-and-faq.md) | Explain/ToSql、离线产 SQL、异常对照表、高频踩坑 FAQ |

## 五分钟上手

### 1. 装包

以 SQL Server 为例（其他库只换 provider 与驱动两颗包，见[安装篇](01-installation-and-connection.md)）：

```bash
dotnet add package QuickSql.Core --prerelease
dotnet add package QuickSql.Providers.SqlServer --prerelease
dotnet add package QuickSql.Generators --prerelease
dotnet add package Microsoft.Data.SqlClient        # 数据库驱动：provider 不替你装
```

### 2. 定义实体

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;

[Table("Orders")]
public class Order
{
    [Key] public int Id { get; set; }                    // 整型 Id 约定自增
    [Required, StringLength(64)] public string CustomerName { get; set; } = "";
    public decimal Amount { get; set; }
    public OrderState State { get; set; }
    public DateTime CreatedAt { get; set; }

    [StringLength(256)] public string? Note { get; set; }   // 可空引用 ⇒ 可空列

    [Column(TypeName = "nvarchar(max)")] public string? Detail { get; set; }
}

public enum OrderState { New, Paid, Shipped, Done }
```

### 3. 建立组装根

**不依赖 DI（控制台 / 后台服务）：**

```csharp
using QuickSql;

var orm = QuickSqlOrm.Create(DbKind.SqlServer,
    "Server=.;Database=app;User Id=sa;Password=***;TrustServerCertificate=True");
```

**ASP.NET Core 依赖注入：**

```csharp
builder.Services.AddQuickSqlOrm(DbKind.SqlServer, connectionString, b =>
{
    b.CommandTimeoutSeconds = 30;
    // b.SchemaSync = SchemaSyncPolicy.Execute;   // 开发环境自动建表（见 13 篇）
});
```

### 4. 同步表结构（可选，开发环境）

```csharp
var result = await orm.SyncStructureAsync();   // 缺表建表、已有表逐列补差
```

### 5. 增删改查

```csharp
// 插入（自增键回填到实体）
var order = new Order { CustomerName = "甲", Amount = 12.5m, State = OrderState.New, CreatedAt = DateTime.Now };
await orm.Insert(order).ExecuteAffrowsAsync();

// 查询
var page = await orm.Select<Order>()
    .Where(o => o.State == OrderState.Paid && o.Amount > 100m)
    .OrderByDescending(o => o.CreatedAt)
    .ToPagedAsync(page: 1, size: 20);            // PagedResult<Order> { Items, Total, HasNext, … }

// 更新（只发被点名的列）
await orm.Update<Order>()
    .Set(o => o.Note, "已确认")
    .Where(o => o.Id == page.Items[0].Id)
    .ExecuteAffrowsAsync();

// 删除
await orm.Delete<Order>().Where(o => o.State == OrderState.Done).ExecuteAffrowsAsync();
```

### 6. 工作单元（要变更跟踪时用）

```csharp
public class AppDb(QuickSqlOrm orm) : QuickSql.DbContext(orm)
{
    public DbSet<Order> Orders => Set<Order>();
}

await using var db = new AppDb(orm);
var order = await db.Orders.Where(o => o.Id == 7).FirstOrDefaultAsync();  // 跟踪读
order!.Note = "改一下";                                                   // 改属性
var report = await db.SaveChangesAsync();                                  // 只写脏列
```

## 三条总原则（读文档前先记住）

1. **链式 API 不是 `IQueryable`**：`orm.Select<T>()` 返回的链刻意不实现 `IQueryable`，不能混入 LINQ-to-Objects 管线；所有能力都是链上显式的方法。
2. **翻译不出就报错，不做客户端求值**：lambda 里写了库端表达不了的写法，当场抛 `UntranslatableExpressionException` 并建议改写方式，绝不静默拉全表。
3. **换数据库只动三处**：csproj 里的 provider 包、一处 `DbKind`、一处连接串。实体、链式代码、事务、分表分库声明一字不动——方言差异（引号、分页、参数前缀、`IDENTITY` 措辞）由各 provider 吃掉（个别例外见[多数据库篇](15-multi-database.md)）。

## 相关链接

- [仓库首页](https://github.com/mr-yangli/QuickSql)
- [设计规格（`sql/docs/quicksql-orm-design.md`）](../quicksql-orm-design.md)——每个行为背后的判据与证据
- 姊妹库：[QuickExcel](../../../excel/docs/quickexcel-usage.md) · [QuickWord](../../../word/docs/quickword-usage.md)
