# 13 · 结构同步（CodeFirst）

> 本篇解决：把实体同步成表结构的三档策略、已有表逐列比形状、哪些动作永远不做、返回报告怎么读、分表建片与多集群。

## 1. 三档策略

```csharp
public enum SchemaSyncPolicy { Disabled, Execute, Script }
```

| 档 | 行为 | 连库 | 用在 |
| --- | --- | --- | --- |
| `Disabled`（默认） | 不同步；手动 `orm.SyncStructureAsync()` 时交回空结果 | — | DDL 由 DBA 管 |
| `Execute` | 缺表建表；已有表逐列比形状发补差 DDL | ✔ | **开发环境**自动建表 |
| `Script` | 只生成 DDL 脚本，不执行 | ✘ | **生产环境**：脚本交 DBA 审批后手动执行 |

**环境闸门**：`Execute` 档只在 Development 环境（`DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT`）有效；非 Development 要求 `ForceSchemaSync = true`，否则启动期抛 `InvalidOperationException`（消息建议走 Script 档）。分表建片入口（`CreateSplitTables*`）过同一道闸。

```csharp
// 开发环境
services.AddQuickSqlOrm(DbKind.Sqlite, cs, b => b.SchemaSync = SchemaSyncPolicy.Execute);

// 生产环境出脚本
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),
    ConnectionString = cs,
    SchemaSync = SchemaSyncPolicy.Script,
});
var result = orm.SyncStructure();
File.WriteAllText($"schema_{DateTime.Now:yyyyMMddHHmmss}.sql", result.Ddl);
```

## 2. 一张已有的表会改什么

`SchemaSyncColumns = true`（默认）时逐列比对契约与库里的现形状，**只发"没有的那一半"**：

| 会发 | 说明 |
| --- | --- |
| `ADD COLUMN` 补列 | 缺的列补上 |
| `ALTER COLUMN` 改列 | 长度、标度、可空性对不上的改回来 |
| 重发列/表备注 | 新增/被改/备注变了的列（`ALTER` 会抹掉备注，必须重发） |
| `CREATE INDEX` | 契约有、库里没有的索引（判据是列的序列，不是名字） |
| `DROP INDEX` | 库里多出的索引——**只在 `SchemaSyncDropIndexes = true` 才发**（删索引有拖慢查询风险，单独显式开） |

**收紧 `NOT NULL` 之前先问库一句**：把可空列收紧成 `NOT NULL` 的 `ALTER`，发之前逐列探"存量里有没有空值"——有空值整句不发（进 `SkippedNullabilityTightening` 并给出路：回填后下一趟自动收紧）；探针答不出照发并在 `NotJudged` 记一行。

**计划层就拒的（永远不做，进 `Refused`）**：

- 删列（删列带走数据）；
- 主键任何不一致（改主键要连外键索引重建，未建模）;
- 自增/值归库给的列缺在库里或形状不符；
- NOT NULL 新列而库里已有行没值可填（先按可空建、回填、再收紧）;
- 索引撞名、库里有契约外的唯一约束（`DROP INDEX` 删不动约束）。

**这一家读不回来的（进 `NotJudged`）**：列形状判不了的档、备注/主键/索引清单读不回等——逐档点名，**不冒充"判过了"**。没有改列能力位的家（今天只有 SQLite 有 `ALTER COLUMN`）改列整格不出、点名"要人工"。

**幂等**：判据是归一后的结构事实，不是类型文本逐字符比。同一趟连跑两次，第二次一个字节都不发——`result.IsEmpty == true` 是判据写对的唯一裁判。

## 3. 返回报告 `SchemaSyncResult`

```csharp
var result = await orm.SyncStructureAsync();

result.CreatedTables;                   // 建出来的表
result.Executed;                        // 真发出去过至少一句（连了库但没发= false）
result.IsEmpty;                         // 六份动作清单全空（判不了/要人工两份话单不算）
result.AddedColumns;                    // 表.列
result.AlteredColumns;                  // 表.列
result.AppliedComments;                 // 发过备注的列（表自身只写表名）
result.CreatedIndexes; / .DroppedIndexes;
result.DdlStatements;                   // 逐句 DDL 原文（Execute 档也看得到——DDL 不走 Aop 日志钩子）
result.Ddl;                             // Script 档的完整脚本；Execute 档为空串
result.NotJudged;                       // 这一家读不出所以判不了的档
result.Refused;                         // 读得出而故意不发的格（给出路）
result.SkippedSplitTemplates;           // 跳过的分表模板
result.SkippedNullabilityTightening;    // 被存量空值挡住的收紧 ALTER
```

同步失败抛 `SchemaSyncFailedException`：消息带失败那句、改的是哪格、**失败前已生效的句子清单**（DDL 不回滚，MySQL 一族本来也回滚不了 DDL）；驱动原话在 `InnerException`。

## 4. 类型注册

同步的是注册表里**已进册**的实体类型；注册表是惰性的（第一次被 `Select`/`Set`/`Contract` 触及才进册）。同步前把全部实体触一遍：

```csharp
_ = orm.Schemas.Contract(typeof(Customer));
_ = orm.Schemas.Contract(typeof(Order));
_ = orm.Schemas.Contract(typeof(OrderItem));
await orm.SyncStructureAsync();
```

## 5. 多集群

- 实体用 `[DataSource("report")]` 点名住哪座库，同步**按集群分组**：一次调用 = 每座在册集群各一趟，每张表落它自己声明的那座。
- provider 声明 `CanCreateDatabase` 时每座先各自确认库存在（SQLite 开连接即建文件）。
- 实体写了配置里没有的集群名：**同步开始时就抛**（带表名与在册清单），不等到连库。
- Script 档多集群时每段加段头 `-- 集群 report：下面这几句要落在这座集群的主库上`（给 DBA 的正是"这段贴到哪座库"）。
- 没有列级 diff（不连库没读数），`NotJudged` 里写明——要列级脚本，给一个只读连接走 Execute。

## 6. 与 DDL 相关的边界

- 没有迁移层：没有 migrate diff/script 命令、没有 CLI、没有版本表；`CodeFirst` 是"对齐现库"而不是"版本化迁移"。
- 反向工程只有 SPI 形状（`ISchemaReader`），读不回主键/注释/自增标记——别指望 CodeFirst + SchemaReader 做双向。
- DDL 全家（建表、结构同步、建片）**不走 `Aop.Executing` 日志钩子**（连的是自己的连接）；要在外部看见发了什么，读 `result.DdlStatements`。

## 7. 列默认值

```csharp
[DefaultValue(7)]
public int Reserved { get; set; }                  // DataAnnotations：System.ComponentModel 的 [DefaultValue]

fluent.Entity<Order>()
      .Property(o => o.Level).HasDefaultValue(3);  // Fluent 同一格、座次更高（与备注那条链同把尺）
```

- **值域只收常量字面量档**：string ∶ bool ∶ 整型 ∶ 浮点 ∶ decimal。DateTime 会把"首次建表那一刻"冻进 DDL、byte[] 没有字面量形、枚举的字面量形各家不同——声明期（`EntityContractFactory` 收尾，G1 ∶ G2 两侧共用）当场拒。
- **类型要精确同型**（可空列剥掉 `Nullable` 比对）：宽窄转换不经这一格，`[DefaultValue(0.5)]` 落在 decimal 列上会红——`decimal` 本来也不是合法的特性实参，它只能走 Fluent。
- **自增/主键列不收**：库给值的列配默认是自相矛盾的配置。
- **出现在哪**：建表（`CREATE TABLE … DEFAULT …`）与补列两句。**不参与形状补差比对**（库里读不回默认值），"改默认值"这一趟不发任何语句——要换默认值请手工 `ALTER`。
- **它救回的那一格**：NOT NULL 新列此前"存量行没有值可填"永远拒；带默认值就发 `ADD COLUMN … DEFAULT … NOT NULL`，库自己把存量行填上（SQLite 的 313 ∶ 2811 旧门禁同笔翻案）。可空列 + DEFAULT 的存量回填各家口径不同（SQL Server 不回填），别拿它当回填工具。
- 布尔默认按各家的存储形落字：BIT/TINYINT/INTEGER 家落 `1/0`，原生 BOOLEAN 家落 `TRUE/FALSE`，ClickHouse 落 `true/false`。

---

[← 上一篇：12 分表分库与读写分离](12-split-tables-and-sharding.md) · [下一篇：14 原生 SQL →](14-raw-sql.md)
