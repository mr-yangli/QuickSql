# 02 · 实体定义

> 本篇解决：一张表怎么映射成一个类——特性标注、默认约定、Fluent API、主键与自增策略、并发令牌、导航关系，以及"不用连库就能查看映射结果"的契约读取。

实体映射有两条路：**特性标注**（大多数场景够用）与 **Fluent API**（不想把映射写进实体类时）。两者可以混用，Fluent 优先级最高。

## 1. 特性标注

### 1.1 直接用系统注解

能表达 "名字、类型名、排除、主键、必填、长度、备注" 这些语义的一律复用系统注解，不自造：

```csharp
using System.ComponentModel;                 // Description
using System.ComponentModel.DataAnnotations; // Table/Column/Key/Required/...
using System.ComponentModel.DataAnnotations.Schema;

[Table("Orders", Schema = "app")]            // 表名与模式；[Description("订单主表")] 给表加备注
public class Order
{
    [Key] public int Id { get; set; }                 // 主键；整型 Id 约定自增（见 §3）
    [Required] public string CustomerName { get; set; } = "";
    [StringLength(64)] public string Code { get; set; } = "";
    [Column("Amt", TypeName = "decimal(18,4)")]       // 列名与 provider 原生类型名
    public decimal Amount { get; set; }
    [NotMapped] public bool NeedsGrading => State == OrderState.New;   // 排除（只读计算属性本来也不会成列）
    [Description("下单时间")] public DateTime CreatedAt { get; set; }  // 列备注
}
```

| 系统注解 | 语义 |
| --- | --- |
| `[Table("名", Schema = "模式")]` | 表名 / 模式名 |
| `[Column("列名")]` / `[Column(TypeName = "nvarchar(max)")]` | 列名 / 原生存储类型名（直通 DDL 与参数绑定） |
| `[NotMapped]` | 不映射 |
| `[Key]` | 主键 |
| `[Required]` | 非空列（NOT NULL） |
| `[StringLength(n)]` / `[MaxLength(n)]` | 字符串长度 |
| `[Description("备注")]`（`System.ComponentModel`） | 表/列备注（DDL 里写得出的家会发出去） |

> 命名空间提醒：自研特性在 `QuickSql.Metadata`，系统注解在 `System.ComponentModel.DataAnnotations(.Schema)`。只 `using QuickSql;` 拿不到它们。

### 1.2 自研特性（系统注解表达不了的语义）

| 特性 | 挂在哪 | 语义 |
| --- | --- | --- |
| `[QuickColumn(Length=64, Precision=18, Scale=4)]` | 属性/字段 | 尺寸与三态：`IsNullable` / `IsPrimary` / `IsIdentity`（`TriState.Unset/Yes/No`）。`[Required]` 只有"非空"一个方向、`[Key]` 管不到"值谁给"，这三态补的就是那个缺口 |
| `[Index(nameof(CreatedAt))]` | 类 | 索引：`IsUnique`、`Clustered`、`Descending`。不给索引名，名字按 `IX_{表}_{列}` 生成（与 EF Core `[Index]` 同一版形状） |
| `[SplitTable(SplitKind.Month, nameof(OccurredAt))]` | 类 | 分表声明（见 [12 篇](12-split-tables-and-sharding.md)），只有注解一条路，Fluent 故意不收 |
| `[DataSource("report")]` | 类 | 实体住哪座具名集群（分库见 [12 篇](12-split-tables-and-sharding.md)），注解里只有集群名没有连接串 |
| `[ConcurrencyToken]` / `[ConcurrencyToken(true)]` | 属性 | 乐观锁列：不进 `INSERT`/`SET`，进 `WHERE` 与回读。带 `true` 时新值由**应用**负责给（如 ABP `ConcurrencyStamp` 风格，见 §3.3） |
| `[NoCascade]` | 类或集合导航属性 | 关闭级联（会话摘行不再连带删子行） |
| `[ForeignKey("列名")]`（集合导航上） | 属性 | 集合边的外键成员（见 §5） |

ClickHouse 专属：`[ClickHouseSkipIndex(SkipIndexKind.MinMax, ...)]`，属性或类上可叠。

## 2. 默认约定

不写任何特性时：

| 规则 | 说明 |
| --- | --- |
| 整型主键 `Id` | 主键 + 自增（`[Key]` 在不在都认；客户端生成主键见 §3.4） |
| C# 可空性 | `string?` ⇒ 可空列；非空 ⇒ `NOT NULL`。可空上下文之外（`#nullable disable`、无元数据的外部程序集）按"未表态"处理，**不**强行当非空 |
| 无长度字符串 | 用 provider 默认（SQL Server 落 `NVARCHAR(MAX)`）；配了 `DefaultStringLength` 时按它落宽度 |
| 导航属性 | 不成列（引用类型 + 集合属性被识别为关系，见 §5） |
| 只有 getter 的成员 | 不是列（判据是"有没有写口"：`{ get; protected set; }`、`{ get; init; }` 都算有写口，照常映射） |
| 无参构造器 | 物化用，**不问可见性**——`protected X()` 的 ABP/DDD 实体直接可用；带参构造器只有 record/构造器 DTO 投影走公共那一颗 |

### 2.1 字符串宽度的四态

宽度只有"声明得出来"和"没声明"两档时，一张没写注解的实体在 SQL Server 上全是 `NVARCHAR(MAX)`。
想让"没写长度的列"统一落一个宽度、又不想逐列打注解时，配 orm 级的缺省宽度：

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = connectionString,
    DefaultStringLength = 255,     // null（缺省）= 维持旧行为
});

// DI 同一颗（注册表是 Singleton，注册时定好）
services.AddQuickSqlOrm(DbKind.SqlServer, connectionString, o => o.DefaultStringLength = 255);
```

四态的分界（与 `[StringLength]` / `[MaxLength]` / `[QuickColumn(Length)]` / Fluent `HasMaxLength` 都按这条走）：

| 声明 | 落什么 | 吃 `DefaultStringLength` 吗 |
| --- | --- | --- |
| 什么都不写 | provider 大文本型（SQL Server `NVARCHAR(MAX)`、PG `text`、**虚谷 `CLOB`**）/ 或配置的宽度 | 吃 |
| `[StringLength(50)]`、`HasMaxLength(50)`、`[QuickColumn(Length = 50)]` | `NVARCHAR(50)` | 不吃（显式赢） |
| `[QuickColumn(Length = -1)]`、Fluent `HasMaxLength(-1)`、`[StringLength(-1)]` | 大文本型 | 不吃（显式无宽度） |
| `HasDbType("nvarchar(max)")`、`[Column(TypeName = "nvarchar(max)")]` | 原样点名的那一型 | 不吃（形状已由声明者钉死） |

两点要说清：① 非正数这一态是"这一列明确不要上限"，公告内容、合同正文这类列在配了全局宽度后必须显式标它
（DA 的 `[StringLength(-1)]` 构造得出来，但它自己的 `IsValid` 会抛——实体要过 DataAnnotations 校验管线时
用 `[QuickColumn(Length = -1)]`）；② `0` 与负数是**配置侧**的错误，构造 orm 时抛 `ArgumentOutOfRangeException`
（关掉默认就是不设这一格，而不是设 0）。

## 3. 主键与自增

### 3.1 三种主键来源

1. **库给值（自增）**：`int`/`long` 主键约定自增；插入后按各家能力回填（见 [03 篇键回填表](03-insert.md#4-自增键回填)）。
2. **应用给值**：`Guid` 主键、雪花 `long` 等——写进实体直接插入即可。约定档用 `ContractResolver`/`GeneratedEntitySchemaRegistry` 的 `integralPrimaryKeyAutoIncrement: false` 关掉"整型主键 ⇒ 自增"，关掉后 `ExplainKeys()` 会当场点名"没有自增列"而不是静默回空。
3. **显式声明**：`[QuickColumn(IsIdentity = TriState.Yes/No)]` 或 Fluent `IsIdentity(...)` 永远赢过约定，真自增表不需要为开关改声明。

### 3.2 并发令牌（乐观锁）

列标 `[ConcurrencyToken]` 后：`UPDATE`/`DELETE` 的 `WHERE` 带上它的旧值；冲突时（命中 0 行）`SaveChanges` 抛 `DbUpdateConcurrencyException`，链式更新返回受影响行数 0。

两族写法：

```csharp
[ConcurrencyToken] public int Version { get; set; }   // 整型：ORM 在 UPDATE 里发 Version = Version + 1
[ConcurrencyToken(true)] public string ConcurrencyStamp { get; set; } = "";  // 应用给值：新值在实体上，随行写进 SET
public string ConcurrencyStamp { get; set; } = "";    // 名字约定：成员名恰为 ConcurrencyStamp 时自动升档（ABP IHasConcurrencyStamp 零注解接入）
```

- **整型族**：不进 `INSERT` 列清单、也不接受调用方手写 `SET`；ORM 每次更新自动发 `Version = Version + 1`，并把实体上的值同步 +1。列请配默认值 0（`[DefaultValue(0)]` / `HasDefaultValue(0)`）。
- **应用给值族**：新值在实体上，进 `SET` 与 `INSERT`；`WHERE` 仍比快照旧值，`SaveChanges` 不会替你生成新值。
- 非整型又没标 `AppSetsValue` 的令牌没有可用的维护方式，`ChangeTracker` 构造时直接抛 `QuickSqlException`。
- **`rowversion` / “值归库给”的并发令牌支持已移除**：SQL Server 也不再接受 `[Column(TypeName = "rowversion")]`，请改用上面的整型或应用给值族。

## 4. Fluent API

```csharp
using QuickSql.Metadata.Fluent;

var model = new ModelConfiguration();

model.Entity<Order>()
    .ToTable("QuickSqlOrder", "app")              // 表名 + schema
    .HasComment("订单主表")
    .HasIndex(nameof(Order.CreatedAt))            // 索引
    .HasUniqueIndex(nameof(Order.Code));

model.Entity<Order>().Property(o => o.Note).HasMaxLength(256).HasDbType("xml");
model.Entity<Order>().Property(o => o.Amount).HasPrecision(18, 4);
model.Entity<InventoryItem>().Property(i => i.Version).IsConcurrencyToken(false);
model.Entity<Order>().Property(o => o.Id).HasColumnName("OrderId").IsIdentity(true);
```

Fluent 有两个入口：**orm 级全局默认**（`QuickSqlOrmOptions.Model`，对不覆写钩子的上下文生效）与**上下文级**（派生类 `protected override void OnModelCreating(ModelBuilder mb)`，EF 同形；**覆写即整份模型**——这一只上下文不再吃 orm 级那份，细节见 [09 篇 §1.5](09-dbcontext-and-tracking.md)）。两处配的是同一套 builder：

```csharp
protected override void OnModelCreating(ModelBuilder mb)
{
    mb.Entity<Order>(e =>
    {
        e.ToTable("Orders");
        e.Property(o => o.CustomerName).HasColumnName("CustName").HasMaxLength(64).IsRequired();
        e.HasIndex(o => o.CreatedAt);
    });
}
```

`PropertyBuilder` 全量：`HasColumnName`、`HasMaxLength`、`HasPrecision(int, int)`、`HasDbType(string)`、`HasComment`、`HasDefaultValue(object)`（列默认值，见 [13 篇 §7](13-schema-sync.md)；值域只收常量字面量档，DateTime/byte[]/枚举与自增/主键列声明期拒）、`IsRequired(bool = true)`、`IsKey(bool = true)`、`IsIdentity(bool = true)`、`IsIgnore()`、`IsConcurrencyToken(bool = true)`、`AppSetsValue(bool = true)`。

装配：

```csharp
var orm = QuickSqlOrm.Create(DbKind.MySql, cs, options => options.Model = model);
// 手写注册：Model = model 填进 QuickSqlOrmOptions / QuickSqlOrmBuilder.Model
```

刻意**没有**的：关系配置（导航靠约定与 `[ForeignKey]`，见 §5）、鉴别器、影子属性；**分表与分库故意不落 Fluent**（两条路径算出两种表名/两座库的代价太大，只留注解一条路）。同一层重复声明同一事实抛 `MetadataConflictException`（编译期诊断 `QuickSql001`）。

### 层序（低层不覆高层）

`ProviderFallback(0) → Convention(1) → DataAnnotations(2) → QuickSqlAttribute(3) → Fluent(4)`。每列的来历可从 `ColumnContract.Origin` 读出（如"并发 ← Fluent IsConcurrencyToken"）。

## 5. 导航与外键

```csharp
[Table("Customers")]
public class Customer
{
    [Key] public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Order> Orders { get; set; } = new();      // 集合导航
}

[Table("Orders")]
public class Order
{
    [Key] public int Id { get; set; }
    public int CustomerId { get; set; }                    // 外键列
    public Customer? Customer { get; set; }                // 引用导航
    [ForeignKey("Sku")] public List<OrderItem> Items { get; set; } = new();  // 集合边显式指外键
}
```

- **外键解析三档**（每层各判一次）：子实体上指向父类型的回指引用导航 → 集合成员上的 `[ForeignKey("列名")]` → 约定 `{父类型名}Id`。三档都命不中当场抛（编译期诊断 `QuickSql004`），不猜。
- 导航属性不成列；链式查询里读引用导航的成员会自动补 `LEFT JOIN`（见 [07 篇](07-joins-and-navigation.md)）。
- 集合导航默认级联（会话摘行连带删子行），`[NoCascade]` 退出（见 [09 篇](09-dbcontext-and-tracking.md)）。

## 6. 源生成器（G1）与反射兜底（G2）

装了 `QuickSql.Generators` 后，带 `[Table]` 的实体在**编译期**生成契约（物化零反射）。NuGet 装法即挂进编译（包内自带 `analyzers/dotnet/cs`）；仓库内源码引用要按分析器挂载：

```xml
<ProjectReference Include="..\src\QuickSql.Generators\QuickSql.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

- 没被生成器覆盖的实体（如没标 `[Table]`）走运行期反射兜底，行为一致，只多首用一次的解析成本；诊断 `QuickSql003`（Info）提示哪些实体没走 G1。
- 被 Fluent 配置过的实体也走反射兜底。
- 编译期诊断：`QuickSql001` 同层元数据互斥；`QuickSql002` 索引引用了未映射属性；`QuickSql004` 导航外键指向未映射属性；`QuickSql005` ClickHouse 跳过索引声明不完整。
- 生成器可选构建属性 `QuickSqlXmlDocComments=true`：把 `/// <summary>` 变成列/表备注。

## 7. 契约读取：不用连库就能看映射结果

```csharp
var contract = orm.Schemas.Contract<Order>();

Console.WriteLine($"{contract.Table.Schema}.{contract.Table.Name}");
foreach (var c in contract.Columns)
    Console.WriteLine($"{c.MemberName} → {c.ColumnName} {c.ClrType.Name} len={c.MaxChars} null={c.IsNullable} id={c.IsIdentity} :: {c.Origin.Note}");
Console.WriteLine(contract.PrimaryKey.Length);
// 关系：contract.Relationships / contract.Collections / contract.Indexes
```

`ColumnContract` 上可读：`MemberName`、`ColumnName`、`ClrType`、`IsNullable`、`IsPrimary`、`IsIdentity`、`MaxChars`/`MaxBytes`、`Precision`、`Scale`、`IsConcurrencyToken`、`AppSetsValue`、`StoreTypeName`、`Comment`、`Origin` 等。`IEntitySchemaRegistry` 另有 `Schema<T>()`、`Validator<T>()`、`Contract(Type)`、`RegisteredTypes`。校验档位 `ValidationMode { Off, ContractOnly, DataAnnotations, Custom }` 默认 `ContractOnly`。

---

[← 上一篇：01 安装与连接](01-installation-and-connection.md) · [下一篇：03 插入数据 →](03-insert.md)
