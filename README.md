# QuickSql

LINQ 风格链式多数据库 ORM：一套实体覆盖 18 家数据库（含达梦/金仓/虚谷/GBase 等信创库），源生成器零反射物化。

MIT 协议。

## 安装

```bash
dotnet add package QuickSql.Core --prerelease
dotnet add package QuickSql.Providers.SqlServer --prerelease
dotnet add package QuickSql.Generators --prerelease
dotnet add package Microsoft.Data.SqlClient     # 驱动由应用侧自装
```

## 快速上手

```csharp
using QuickSql;

var orm = QuickSqlOrm.Create(DbKind.SqlServer,
    "Server=.;Database=app;User Id=sa;Password=***;TrustServerCertificate=True");

await orm.Insert(new Order { CustomerName = "甲", Amount = 12.5m, CreatedAt = DateTime.Now })
         .ExecuteAffrowsAsync();

var page = await orm.Select<Order>()
    .Where(o => o.Amount > 100m)
    .OrderByDescending(o => o.CreatedAt)
    .ToPagedAsync(1, 20);
```

## 特性

- 19 家数据库：SQL Server、PostgreSQL、MySQL、Oracle、SQLite、达梦、人大金仓、openGauss、GaussDB、瀚高、GBase 8c/8s、DuckDB、ClickHouse、TDengine、TiDB、OceanBase、神通、虚谷
- 查询：条件/排序/投影/Distinct/分页计数/存在性探针；Include/IncludeMany 导航联表；分组聚合；子查询；流式读取
- 写入：批量分块、自增键回填、SQL Server 装载通道、InsertOrUpdate upsert
- 工作单元：DbContext/DbSet/ChangeTracker/SaveChanges（脏列判定、级联、乐观锁）
- 事务四形态、保存点嵌套；全局过滤器/软删除/AOP 钩子
- 分库分表 `[SplitTable]`/`[DataSource]`、读写分离；CodeFirst 结构同步三档
- ToSql() 离线产 SQL、AOT 干净注册路径、原生 SQL 逃生舱

## 构建与测试

```bash
dotnet build QuickSql.slnx

# 离线方言册（构建后直接跑 exe）
dotnet build tests/QuickSql.Tests.Dialects/QuickSql.Tests.Dialects.csproj
tests/QuickSql.Tests.Dialects/bin/Debug/net10.0/QuickSql.Tests.Dialects.exe
```

## 文档

- [文档中心（按功能 16 篇）](docs/quicksql/README.md)
- [设计规格](docs/quicksql-orm-design.md)

## License

MIT
