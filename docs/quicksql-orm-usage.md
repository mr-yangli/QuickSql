# QuickSql — .NET 10 的多数据库 ORM

一套实体与 LINQ 风格链式 API，覆盖 **18 家数据库**（SQL Server、PostgreSQL、MySQL、Oracle、SQLite、达梦、人大金仓、虚谷、GBase 等），源生成器编译期生成实体契约实现零反射物化。内置工作单元（变更跟踪 + `SaveChanges`）、乐观锁、全局过滤器、软删除、AOP 审计、分表分库与读写分离、CodeFirst 结构同步、原生 SQL 逃生舱。

> 目标框架 `net10.0`；当前为 `-beta` 预发布系列；MIT 协议；除 ADO.NET 驱动外零第三方依赖。

## 安装

普通应用装三颗 QuickSql 包 + 各自数据库的 ADO.NET 驱动（provider 不自带驱动）：

```bash
dotnet add package QuickSql.Core --prerelease
dotnet add package QuickSql.Providers.SqlServer --prerelease   # 只装你真要连的那家
dotnet add package QuickSql.Generators --prerelease            # 源生成器（实体契约 + provider 登记）
dotnet add package Microsoft.Data.SqlClient                    # 数据库驱动
```

或写死版本号：

```xml
<PackageReference Include="QuickSql.Core" Version="0.39.0-beta.35" />
<PackageReference Include="QuickSql.Providers.SqlServer" Version="0.39.0-beta.35" />
<PackageReference Include="QuickSql.Generators" Version="0.39.0-beta.35" />
```

> 全家都是 `-beta.N` 预发布档，`dotnet add package` 必须带 `--prerelease`；版本以 nuget.org 实际可得的最新一颗为准。

## 快速上手

```csharp
using QuickSql;

// ① 组装根（不依赖 DI；DI 用 services.AddQuickSqlOrm(...)，见文档）
var orm = QuickSqlOrm.Create(DbKind.SqlServer,
    "Server=.;Database=app;User Id=sa;Password=***;TrustServerCertificate=True");

// ② 插入（自增键回填）
var order = new Order { CustomerName = "甲", Amount = 12.5m, State = OrderState.New, CreatedAt = DateTime.Now };
await orm.Insert(order).ExecuteAffrowsAsync();

// ③ 查询
var page = await orm.Select<Order>()
    .Where(o => o.State == OrderState.Paid && o.Amount > 100m)
    .OrderByDescending(o => o.CreatedAt)
    .ToPagedAsync(1, 20);                       // PagedResult<Order> { Items, Total, HasNext, … }

// ④ 更新 / 删除
await orm.Update<Order>().Set(o => o.Note, "已确认").Where(o => o.Id == 7).ExecuteAffrowsAsync();
await orm.Delete<Order>().Where(o => o.CreatedAt < cutoff).ExecuteAffrowsAsync();
```

**要变更跟踪（改属性、一个批次提交）用 `DbContext`：**

```csharp
public class AppDb(QuickSqlOrm orm) : QuickSql.DbContext(orm)
{
    public DbSet<Order> Orders => Set<Order>();
}

await using var db = new AppDb(orm);
var o = await db.Orders.Where(x => x.Id == 7).FirstOrDefaultAsync();
o!.Note = "改一下";
await db.SaveChangesAsync();                    // 只写脏列；干净时不发语句不开事务
```

**DI（ASP.NET Core）：**

```csharp
builder.Services.AddQuickSqlOrm(DbKind.SqlServer, connectionString, b =>
{
    b.CommandTimeoutSeconds = 30;
    b.SchemaSync = SchemaSyncPolicy.Execute;    // 开发环境自动建表
    b.AfterBuild.Add((sp, orm) =>               // 按请求装配：过滤器/软删/AOP 订阅
    {
        orm.Aop.Executed = e => Console.WriteLine($"{e.ElapsedMilliseconds}ms {e.Sql}");
    });
});
```

## 功能一览

- **查询**：条件/排序/投影/Distinct、分页与总数、存在性探针、联表与 `Include`/`IncludeMany` 导航、引用导航自动联表、分组聚合、子查询、流式读取
- **写入**：单行/批量（按参数预算分块）、自增键回填、批量高速层（SQL Server 装载通道）、`InsertOrUpdate` upsert、按实体稀疏/整行更新
- **工作单元**：`DbContext`/`DbSet`/`ChangeTracker`/`SaveChanges`（脏列判定、三档顺序、级联新增/摘行/到位）
- **事务**：四种形态（显式/包段/环境/会话）、保存点嵌套、集群事务
- **过滤与行为**：全局查询过滤器（支持接口键与 mandatory）、软删除（四个删除出口一起改写）、AOP 钩子全家（审计/日志/改写/取消）
- **分库分表**：`[SplitTable]` 六档分片（时间+哈希）、`[DataSource]` 具名集群、读写分离主从路由与写后读
- **CodeFirst**：结构同步三档（关/执行/出脚本）、已有表逐列补差、分表建片
- **原生 SQL**：`orm.Ado` 字符串口（走同一条执行链）、`WithSql` 表源替换
- **工程化**：源生成器零反射物化、AOT 干净注册路径、离线产 SQL（`ToSql()` 不连库）

## 支持的数据库

SQL Server、PostgreSQL、MySQL、Oracle、SQLite、达梦、人大金仓、openGauss、GaussDB、瀚高、GBase 8c/8s、DuckDB、ClickHouse、TDengine、TiDB、OceanBase、神通、虚谷。

每家对应一颗 `QuickSql.Providers.<库种>` 包（血统基类包传递引入，不用直接装）；驱动包应用侧自装。总表与连接串写法见[多数据库支持](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/15-multi-database.md)。

## 文档

- [文档中心（按功能分篇）](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/README.md)
  - [01 安装与连接](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/01-installation-and-connection.md) · [02 实体定义](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/02-entity-mapping.md) · [03 插入数据](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/03-insert.md) · [04 更新数据](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/04-update.md) · [05 删除与软删除](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/05-delete-and-soft-delete.md)
  - [06 查询数据](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/06-query.md) · [07 联表与导航](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/07-joins-and-navigation.md) · [08 分组与聚合](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/08-groupby-aggregate.md) · [09 工作单元](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/09-dbcontext-and-tracking.md) · [10 事务](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/10-transactions.md)
  - [11 过滤器与 AOP](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/11-filters-behaviors-aop.md) · [12 分表分库与读写分离](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/12-split-tables-and-sharding.md) · [13 结构同步](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/13-schema-sync.md) · [14 原生 SQL](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/14-raw-sql.md) · [15 多数据库支持](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/15-multi-database.md) · [16 诊断与 FAQ](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql/16-diagnostics-and-faq.md)
- [设计规格（每个行为背后的判据）](https://github.com/mr-yangli/QuickSql/blob/main/docs/quicksql-orm-design.md)

## License

MIT
