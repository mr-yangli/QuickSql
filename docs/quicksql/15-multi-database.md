# 15 · 多数据库支持

> 本篇解决：19 家数据库各自的包、驱动、连接串写法；各家在标识符、自增回填、upsert 形态上的差异；换库时最容易踩的坑。

## 1. Provider 总表

| 数据库 | `DbKind` | provider 包 | 驱动包（NuGet） | 连接串示例 |
| --- | --- | --- | --- | --- |
| SQL Server | `SqlServer` | `QuickSql.Providers.SqlServer` | `Microsoft.Data.SqlClient` | `Server=<host>,1433;Database=<db>;User Id=<u>;Password=<p>;TrustServerCertificate=True`（Windows 认证换 `Integrated Security=True`） |
| PostgreSQL | `PostgreSql` | `QuickSql.Providers.PostgreSql` | `Npgsql` | `Host=<h>;Port=5432;Database=<db>;Username=<u>;Password=<p>` |
| 人大金仓 KingbaseES | `Kingbase` | `QuickSql.Providers.Kingbase` | `Kdbndp`（金仓官方） | `Host=<h>;Port=54321;Database=<db>;Username=<u>;Password=<p>` |
| openGauss | `OpenGauss` | `QuickSql.Providers.OpenGauss` | `Npgsql` | 同 PG 形 |
| 华为 GaussDB | `GaussDb` | `QuickSql.Providers.GaussDb` | `Npgsql` | 同 PG 形 |
| 瀚高 HighGo | `Highgo` | `QuickSql.Providers.Highgo` | `Npgsql` | 同 PG 形 |
| GBase 8c | `Gbase8c` | `QuickSql.Providers.Gbase8c` | `Npgsql` | 同 PG 形 |
| DuckDB | `DuckDb` | `QuickSql.Providers.DuckDb` | `DuckDB.NET.Data.Full` | `Data Source=<文件路径>`（内存库 `:memory:`） |
| Oracle | `Oracle` | `QuickSql.Providers.Oracle` | `Oracle.ManagedDataAccess.Core` | `Data Source=<host>:<port>/<service>;User Id=<u>;Password=<p>` |
| 达梦 DM | `Dameng` | `QuickSql.Providers.Dameng` | `DmProvider`（官方 NuGet 包在 .NET 10 实测还原失败——走 `drivers/` 或 `DriverRegistry.Register` 注入） | `Server=<h>;Port=5236;User ID=<u>;Password=<p>` |
| 神通 Oscar | `ShenTong` | `QuickSql.Providers.ShenTong` | 厂商给件（`Oscar`） | `Server=<h>;Port=<port>;Database=<db>;User ID=<u>;Password=<p>` |
| MySQL | `MySql` | `QuickSql.Providers.MySql` | `MySqlConnector` | `Server=<h>;Port=3306;Database=<db>;User ID=<u>;Password=<p>;Charset=utf8mb4` |
| TiDB | `TiDb` | `QuickSql.Providers.TiDb` | `MySqlConnector` | `Server=<h>;Port=4000;Database=<db>;User ID=<u>;Password=<p>` |
| OceanBase（MySQL 模） | `OceanBase` | `QuickSql.Providers.OceanBase` | `MySqlConnector` | `Server=<h>;Port=2881;Database=<db>;User ID=<u>@<租户>;Password=<p>` |
| 虚谷 XuGu | `Xugu` | `QuickSql.Providers.Xugu` | 厂商给件（`XuguClient`，4.0.0.8 起纯托管一颗，无原生库要摆） | `Server=<h>;Port=<port>;Database=<db>;USER=<u>;Password=<p>`（4.0 的键名校验不认 `User ID`，要 `USER` 或 `UID`；3.3.4.5 两种都认） |
| GBase 8s | `Gbase8s` | `QuickSql.Providers.Gbase8s` | 厂商给件（`GBase.DataDB`） | Informix 形：`Database=<db>;Host=<h>;Service=<port>;Protocol=onsoctcp;Server=<实例>;UID=<u>;PWD=<p>` |
| SQLite | `Sqlite` | `QuickSql.Providers.Sqlite` | `Microsoft.Data.Sqlite` | `Data Source=<文件路径>`（内存库 `:memory:`） |
| ClickHouse | `ClickHouse` | `QuickSql.Providers.ClickHouse` | `ClickHouse.Client` | 按官方客户端文档（`Host`/`Port`/`Database`/`Username`/`Password`） |
| TDengine | `TDengine` | `QuickSql.Providers.TDengine` | `TDengine.Connector`（**驱动程序集名是 `TDengine`**，不是包名） | `protocol=websocket;host=<h>;port=6041;username=root;password=<p>;db=<db>`（websocket 档纯托管不落 taos.dll，走 taosAdapter 6041；native 档要装客户端库且 protocol 缺省即 native） |

**诚实的能力口径**：上表是"可寻址、可产方言 SQL"的家底。按真库执行见证分档——**SQL Server** 全能力在真库跑过（集成层两台实例）；**SQLite、虚谷、PostgreSQL、MySQL** 有真库执行见证（覆盖各自的部分能力档）；**其余十三家**停留在文本层（方言文本有离线黄金样本锁形，但从未在一台机器上连通过驱动）。第一次连通某家时，建议先用 `ToSql()` 对一遍文本、再跑一轮你用到的读写路径。达梦的 NuGet 驱动包还原不通是已知事实（见驱动列）。TDengine 是时序库这一档的第一颗：无事务、无 UPDATE、无 identity、无二级索引，**每张表第一列必须是 TIMESTAMP 且是主键**（系统自动设）——int Id 血统的模型在这一家建不出表，建模面见 provider 工程的键形状校验。

`DbKind` 枚举 19 个值与 provider 一一对应、没有空档；MariaDB 不设档（MySQL provider 天然可用）。

**TDengine 超级表（v0.39 第六笔）**：`[TDengineStable(Subtable = "meters_{device}")]` 打在类上、`[TDengineTag]` 打在属性上——`[Table]` 名即稳定名（`CREATE STABLE … TAGS (…)` 与查询面都指它），TAG 列照常参与查询与物化；插入走 `INSERT INTO 子表 USING 稳定 TAGS(内联值) (数据列) VALUES(…)` 自动建表，子表名由模板填行内 TAG 值解析（同批行 TAG 值必须一致）。TAG 值刻意内联：STMT2 的参数化自动建表有明文行为缺口（子表不存在时报错【文档·TDengine：stmt 页】）。结构同步对稳定表只管建（`INS_COLUMNS` 含不含 TAG 列官方无明文），补差以库的拒绝收尾；子表预建走 provider 的 `EmitCreateSubtable` 手工口。

## 2. 标识符策略（迁移时最硬的一格）

| 预设 | 引号 | 大小写敏感 | 折大写 | 混合大小写才引 | 一律引 |
| --- | --- | --- | --- | --- | --- |
| SQL Server | `[ ]` | ✘ | ✘ | ✘ | ✔ |
| PostgreSQL（含 PG 系七叶） | `" "` | ✔ | ✘ | ✔ | ✘ |
| Oracle 系（Oracle/达梦/神通） | `" "` | ✔ | **✔** | ✔ | ✘ |
| MySQL 系 | 反引号 | ✘ | ✘ | ✘ | ✔ |
| 虚谷 | `" "` | ✔ | **✔** | ✔ | ✘ |
| GBase 8s | `" "` | ✔ | ✘ | ✔ | ✘ |
| SQLite | `" "` | ✘ | ✘ | ✘ | ✔ |
| TDengine | 反引号 | ✘ | ✘ | ✘ | ✔（与 MySQL 同形：反引号是本家唯一转义符，双引号是字符串字面量引号） |

同一份 `Order` 模型：金仓发 `"t0"."CustomerName"`，达梦与虚谷发 `"T0"."CUSTOMERNAME"`（折大写），MySQL 发反引号原样。**不加引号的名字在 Oracle 系进库即大写**——跨家建的表对不上是这类差异的头号现场。

## 3. 各家差异速查

| 能力 | SQL Server | PG 系 | MySQL 系 | Oracle 系 | 虚谷 | GBase 8s | SQLite | ClickHouse | TDengine |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 自增键批量回填 | `OUTPUT` | `RETURNING` | ✘（语句后回读） | ✘✘（**要键即抛**） | 语句后回读 | 语句后回读 | `RETURNING` 形 | — | ✘✘（**要键即抛**：无 identity，主键是那颗时间戳、调用方给值） |
| upsert 形态 | `MERGE` | `ON CONFLICT` | `ON DUPLICATE KEY` | `MERGE` | `MERGE` | 两句一队 | `ON CONFLICT` | ✘ | ✘（INSERT 同 ts 覆盖的语义要活库钉死才敢接） |
| 参数前缀 | `@` | `@` | `@` | `:` | `:` | `?`（位置式） | `@` | `@` | `?`（位置式；驱动侧参数名翻成 `@pN` 走 stmt2 分类绑定） |
| 合并写（UPDATE…FROM） | ✔ | ✔ | ✘ | ✘ | ✘ | ✘ | ✘ | ✘ | ✘ |
| 多语句事务 | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✘（逐句 autocommit） | ✘（驱动 `BeginDbTransaction` 当场 throw） |

未建模的兼容参数（金仓 Oracle 兼容模式、MySQL `GuidFormat`、OceanBase 双模等）不在本表——同一家不同版本上的行为可能整体不同，换库前按自家现场验。

## 4. 驱动安装三条路

1. **NuGet 驱动包**：绝大多数家走这条（表见 [01 篇 §3](01-installation-and-connection.md#3-驱动从哪来)）；
2. **`drivers/` 目录**：输出目录下放厂商 ADO.NET 程序集（文件名 = 内建程序集名），不改代码不 csproj；虚谷要连原生库一起放；
3. **代码注入**：`DriverRegistry.Register(key, factory)`——要覆盖内建程序集名/工厂类型时唯一手段。

驱动解析失败的异常消息会写明找的是哪个程序集、探测了哪个目录；解析失败被缓存（同 key 不重试），修好后重启进程。

## 5. 连接串的三个咬人细节

- 库落点由串上的键决定、不由 provider：SQL Server `Database=` 与 `Initial Catalog=` 并存时驱动取后出现的那颗；MySQL 系的"模式"就是 `Database=` 那个库。
- GBase 8s 用的是 Informix 一族完全不同的键（`Protocol=onsoctcp` / `Server=<实例名>` / `Service=<端口>`），照 PG/MySQL 抄必然连不上。
- 占位连接串能产 SQL 不能连库（文本层不碰连接）；配了真串才走执行层。

## 6. 跨库迁移最容易踩的五格

| 坑 | 现场形状 |
| --- | --- |
| 折大写 | 同一模型在达梦/虚谷得到 `"CUSTOMERNAME"`、在金仓得到 `"CustomerName"`；Oracle 系不加引号的名字进库即大写 |
| 别名与写目标 | GBase 8s / Oracle 系的别名不带 `AS`（`FROM "Orders" "T0"`），UPDATE 目标一律裸表名 |
| 参数占位符 | GBase 8s 是位置式 `?` 且不接受带前缀参数名 |
| 单独 `OFFSET` 要哨兵 | 只有 `Skip` 无 `Take` 时 MySQL 补最大无符号界、虚谷补最大整数界——两颗同形不同义，照别家文本抄会溢出 |
| 自增键与 `IDENTITY` 措辞 | Oracle 血统内部分裂：达梦写 `IDENTITY(1,1)`、血统基类写 `GENERATED BY DEFAULT AS IDENTITY`；要键回填先按 [03 篇回填表](03-insert.md#4-自增键回填)确认那一档开不开 |

另两条日常注意：软删不是默认行为（没注册 `Behaviors.SoftDelete` 的实体 `Delete` 就是物理删除）；"清空某一格"只有链式 `.Set(列, null)` 做得到（[04 篇](04-update.md)）。

## 7. 换库自查清单

1. csproj：目的库 provider + 驱动包；
2. 代码一处：`DbKind` 换枚举值；
3. 连接串一处；
4. 逃生舱内容人工核对：`WithSql` / `orm.Ado` 文本、`[Column(TypeName=…)]` 原生类型名、原生 SQL 里的引号与占位符；
5. 键回填档位：Oracle 系要键的路全部换写法；
6. 有真库后先 `SyncStructure`（Script 档）对一遍形状，再跑一轮冒烟。

---

[← 上一篇：14 原生 SQL](14-raw-sql.md) · [下一篇：16 诊断与常见问题 →](16-diagnostics-and-faq.md)
