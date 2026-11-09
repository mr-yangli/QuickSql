# 01 · 安装与连接

> 本篇解决：装哪些包、驱动从哪来、`QuickSqlOrm` 怎么建、依赖注入怎么注册、换数据库动哪里。

## 1. 装哪些包

普通应用装 **三颗 QuickSql 包 + 一颗数据库驱动包**：

| 包 | 作用 |
| --- | --- |
| `QuickSql.Core` | 组装根（`QuickSqlOrm`/`DbContext`）、查询与写入链、事务、驱动加载。装 provider 会传递引入它，显式写出只为三颗包版本口径一眼可见 |
| `QuickSql.Providers.<库种>` | 方言、类型映射、键策略、DDL 发射。**只装你真要连的那家**；要连几家装几颗，互不妨碍 |
| `QuickSql.Generators` | 源生成器：编译期生成实体契约（零反射物化）与 provider 登记。**不装则 `Create` 当场抛**，实体退回反射解析 |
| 数据库驱动 | 各家的 ADO.NET 驱动，**provider 不自带**，应用侧显式装（见第 3 节） |

```xml
<ItemGroup>
  <PackageReference Include="QuickSql.Core" Version="0.39.0-beta.35" />
  <PackageReference Include="QuickSql.Providers.SqlServer" Version="0.39.0-beta.35" />
  <PackageReference Include="QuickSql.Generators" Version="0.39.0-beta.35" />
  <PackageReference Include="Microsoft.Data.SqlClient" Version="7.1.1" />
</ItemGroup>
```

或命令行（预发布包必须带 `--prerelease`，否则报"找不到稳定版本"）：

```bash
dotnet add package QuickSql.Core --prerelease
dotnet add package QuickSql.Providers.SqlServer --prerelease
dotnet add package QuickSql.Generators --prerelease
dotnet add package Microsoft.Data.SqlClient
```

版本说明：

- 全系列都是 `-beta.N` 预发布档，安装时以 [nuget.org](https://www.nuget.org/packages/QuickSql.Core) 上实际可得的最新一颗为准（写作时点为 `0.39.0-beta.35`，仓库源码版本见 [Directory.Build.props](../../../Directory.Build.props)）。
- 刚推送的新版本装不到时，先清本机 NuGet HTTP 缓存：`dotnet nuget locals http-cache --clear`；或直接把版本号写死。
- `QuickSql.Providers.PostgreSqlBase` / `OracleBase` / `MySqlBase` 三颗是血统基类包，由叶子包传递引入，**应用侧不直接装**。

## 2. 建立组装根：三种形态

`QuickSqlOrm` 是线程安全的单例级对象（不持有连接），`DbContext` 才是按请求/按流程的。三种建法按场景选：

### 形态一：一行到库（推荐起步）

```csharp
using QuickSql;

// DbKind + 连接串，驱动由内建表现场解析
var orm = QuickSqlOrm.Create(DbKind.SqlServer,
    "Server=.;Database=app;User Id=sa;Password=***;TrustServerCertificate=True");

// 工厂已在手（例如 DI 容器里注册过）也可以显式给
var orm2 = QuickSqlOrm.Create(DbKind.MySql, "Server=...;Database=app;", MySqlConnectorFactory.Instance);
```

`Create` 依赖源生成器生成的 provider 登记（编译期把 provider 注册进 `ProviderRegistry` 静态表，零反射、AOT 安全）。没装 `QuickSql.Generators` 时 `Create` 抛 `InvalidOperationException`，消息点名"生成器未挂进编译"——装上即可，无需其他接线。

### 形态二：手写注册（AOT/裁剪场景）

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),   // 调用方自己构造 provider
    ConnectionString = "Server=.;Database=app;TrustServerCertificate=True",
});
```

这条路径构造期零反射。更进一步的干净口是把生成器产出的实体注册表交给构造器（`Select<T>()` 等入口不再带反射税标注）：

```csharp
var schemas = new GeneratedEntitySchemaRegistry(provider, QuickSql.Generated.QuickSqlGeneratedMetadata.TryGet);
var orm = new QuickSqlOrm(options, schemas);
```

### 形态三：依赖注入（ASP.NET Core）

```csharp
builder.Services.AddQuickSqlOrm(DbKind.PostgreSql, builder.Configuration.GetConnectionString("Default"), b =>
{
    b.CommandTimeoutSeconds = 30;
    b.PagingOrdering = PagingOrderingPolicy.AutoOrderPrimaryKey;  // 无序分页自动补主键序
});
```

四个重载按需选：

```csharp
AddQuickSqlOrm(DbKind kind, string connectionString, Action<QuickSqlOrmBuilder>? configure = null);
AddQuickSqlOrm(DbKind kind, Func<IServiceProvider, string> connectionStringFactory, Action<QuickSqlOrmBuilder>? configure = null);  // 多租户按请求换串
AddQuickSqlOrm(DbKind kind, string connectionString, DbProviderFactory driverFactory, Action<QuickSqlOrmBuilder>? configure = null);
AddQuickSqlOrm(DbKind kind, Func<IServiceProvider, string> connectionStringFactory, DbProviderFactory driverFactory, Action<QuickSqlOrmBuilder>? configure = null);
```

生命周期：`QuickSqlOrm` 注册为 **Scoped**（与 `AddDbContext` 习惯一致），provider 与实体注册表为 Singleton。注意"按请求解析连接串"的工厂重载与 `Replicas`/`Clusters`（读写分离，见 [12 篇](12-split-tables-and-sharding.md)）互斥——混用会在注册期抛，因为从库与具名集群是启动期常量。

请求期内要做的按请求装配（当前租户过滤器、软删注册、日志订阅）走 `AfterBuild` 清单：

```csharp
builder.Services.AddQuickSqlOrm(DbKind.SqlServer, connectionString, b =>
{
    b.AfterBuild.Add((sp, orm) =>        // 每个作用域解析时跑一次
    {
        var tenant = sp.GetRequiredService<ICurrentTenant>();
        orm.Filters.Add<Order>(o => o.TenantId == tenant.Id, mandatory: true);
        orm.Aop.Executed = e => logger.LogInformation("{Sql}", e.Sql);
    });
});
```

## 3. 驱动从哪来

QuickSql 包**不引用任何 ADO.NET 驱动**，驱动由应用侧提供，解析顺序：

1. **NuGet 驱动包**（绝大多数库种）：装上后驱动程序集在应用输出目录，`Assembly.Load` 直接命中；
2. **`drivers/` 目录**：把厂商给的 ADO.NET 程序集放进输出目录的 `drivers/` 子目录（信创现场不装 NuGet 包、不 csproj 时用；虚谷一族 4.0.0.8 起只有托管那一份 `XuguClient.dll`，没有原生库要摆）；
3. **代码注入**：`DriverRegistry.Register("dameng", factory)` 把已在进程里的 `DbProviderFactory` 交进去（要覆盖内建程序集名/工厂类型时唯一手段）。

每家 `DbKind` 对应的驱动包与内建程序集名：

| 数据库 | NuGet 驱动包 | 内建程序集名 |
| --- | --- | --- |
| SQL Server | `Microsoft.Data.SqlClient` | 同名 |
| MySQL / TiDB / OceanBase(MySQL模) | `MySqlConnector` | 同名 |
| PostgreSQL / openGauss / GaussDB / 瀚高 / GBase 8c | `Npgsql` | 同名 |
| SQLite | `Microsoft.Data.Sqlite` | 同名 |
| Oracle | `Oracle.ManagedDataAccess.Core` | `Oracle.ManagedDataAccess` |
| 人大金仓 KingbaseES | `Kdbndp`（金仓官方） | `Kdbndp` |
| 达梦 Dameng | `DmProvider`（官方包在 .NET 10 上实测还原失败，走 `drivers/` 或 `Register` 注入） | `DmProvider` |
| 神通 ShenTong | 无公开 NuGet 包 | `Oscar`（厂商给件） |
| 虚谷 Xugu | 无公开 NuGet 包 | `XuguClient`（厂商给件，4.0.0.8 起纯托管一颗） |
| GBase 8s | 无公开 NuGet 包 | `GBase.DataDB`（厂商给件） |
| DuckDB | `DuckDB.NET.Data.Full` | `DuckDB.NET.Data` |
| ClickHouse | `ClickHouse.Client` | `ClickHouse.Client` |

驱动找不到时抛 `DriverNotAvailableException`，消息写明在内建表里找的是哪个程序集、探测了哪个目录；解析失败会被缓存（同一 key 第二次不再重试）。完整 provider 总表（连接串示例、血统、状态口径）见 [15 篇](15-multi-database.md)。

## 4. `QuickSqlOrmOptions` 全部选项

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `Provider` | 必填 | `IDbProvider` |
| `ConnectionString` | 必填 | 缺省集群 `main` 的主库连接串 |
| `Replicas` | `[]` | `main` 的从库串清单，按声明顺序轮询（读写分离见 [12 篇](12-split-tables-and-sharding.md)） |
| `Clusters` | `[]` | `main` 之外的具名集群，`new ClusterConfig("report", 主库串, 从库串[])` |
| `Aop` | `new()` | AOP 钩子容器（见 [11 篇](11-filters-behaviors-aop.md)） |
| `Model` | `null` | Fluent 映射配置（见 [02 篇](02-entity-mapping.md)） |
| `CommandTimeoutSeconds` | `30` | 透传给每条命令 |
| `PagingOrdering` | `Throw` | `Skip/Take` 无 `OrderBy` 时：`Throw` / `AutoOrderPrimaryKey`（自动补主键序）/ `Allow` |
| `SchemaSync` | `Disabled` | 结构同步策略：`Disabled` / `Execute` / `Script`（见 [13 篇](13-schema-sync.md)） |
| `ForceSchemaSync` | `false` | 非 Development 环境放行 DDL 的额外开关 |
| `SchemaSyncColumns` | `true` | 已有表要不要逐列比形状（补列/改列/备注/索引） |
| `SchemaSyncDropIndexes` | `false` | 库里多出的索引要不要 DROP（删索引有拖慢查询风险，显式开） |

`orm` 上可读的属性：`Options`、`Provider`、`Schemas`（实体注册表）、`Aop`、`Compiler`、`Ado`（原生 SQL 口）、`Sources`（集群清单）、`PagingOrdering`、`Filters`（全局过滤器）、`Behaviors`（软删等行为注册）。

## 5. 换数据库：动的是哪三处

同一份应用、同一副实体、同一批查询写入代码，从一座库挪到另一座库：

1. **csproj**：装目的库的 `QuickSql.Providers.*` 与对应驱动包；
2. **代码一处**：`DbKind.SqlServer` → 目的库枚举值；
3. **配置一处**：连接串。

实体类、lambda、`Select/Insert/Update/Delete` 链、`DbContext`、分表与分库声明**一字不动**。已知的方言差异（Oracle 系折大写、GBase 8s 位置式参数、键回填能力差异等）全部收在 [15 篇的迁移清单](15-multi-database.md#6-跨库迁移最容易踩的五格)里，换库前过一遍。

注意两类"逃生舱"内容换库时**不会被改写也不会报警**：`WithSql`/`orm.Ado` 的手写 SQL 文本、`[Column(TypeName = "jsonb")]` 这类原生类型名。每用一处，换库时就多一处人工核对点。

---

[下一篇：02 实体定义 →](02-entity-mapping.md)
