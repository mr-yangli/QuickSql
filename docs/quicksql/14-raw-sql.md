# 14 · 原生 SQL

> 本篇解决：链式写不出的句子（除行号以外的窗口函数、递归 CTE、方言专有语法、分组后聚合）怎么用 `orm.Ado` 字符串口发；只换 FROM 段的 `WithSql`。行号那一格今天有函数入口了（`QuickFx.RowNumber`，见 [06 篇 §4](06-query.md)），本篇剩下的是"按行号筛"那条出路——把内层那句带 `ROW_NUMBER` 的 SQL 交进 `WithSql`。

两条逃生舱，射程不同：

- **`orm.Ado` 一族**：整条查询自己写，但走库的同一条执行链（AOP、日志、事务环境、连接复用全在）；
- **`WithSql`**：只把 FROM 那一段换掉，外面一层（选列、条件、分页、计数）仍归库。

## 1. `orm.Ado` 字符串口

```csharp
// 类型化查询：行按「列名对属性名」装配；参数名 = 本家前缀 + 属性名
var report = await orm.Ado.QueryAsync<CustomerReport>(
    """
    SELECT o.CustomerId AS CustomerId, c.Name AS Name, COUNT(*) AS Orders, SUM(o.Amount) AS Total
      FROM Orders o JOIN Customers c ON c.Id = o.CustomerId
     WHERE o.Amount >= @minAmount
     GROUP BY o.CustomerId, c.Name
    """,
    new { minAmount = 100m });

var one    = await orm.Ado.FirstAsync<CustomerReport>(sql, args);      // 第一行或 null
var scalar = await orm.Ado.ExecuteScalarAsync("SELECT COUNT(*) FROM Orders");  // 第一行第一列，DBNull → null
var n      = await orm.Ado.ExecuteNonQueryAsync("UPDATE Orders SET Note = @note WHERE Id = @id",
                                                new { note = "甲", id = 7 });
await orm.Ado.ExecuteBatchAsync(["CREATE TABLE …", "INSERT …"]);       // 多句共用一条连接；不开事务

// 流式：大结果集逐行
await foreach (var row in orm.Ado.StreamAsync<CustomerReport>(sql, args))
    Console.WriteLine(row.Name);
```

### 三条纪律

1. **文本一颗不动**：不重写占位符、不补引号、不校验语法——发出去的就是你写的那一句（方言差异自己担）；
2. **参数名 = 本家前缀 + 属性名**：占位符必须与参数对象的属性名同形（连大小写）。前缀按家换：`@`（SQL Server / PG 系 / MySQL 系）、`:`（Oracle 系 / 虚谷）、`?`（GBase 8s，**位置式**——带参数对象当场拒）；
3. **行的落点**：「可直读第 0 列的标量形状」或「公共无参构造 + 公共可写属性」按名字装配。

### 装配规则与报错

- 结果集里对不上属性的**列忽略**（报表 SQL 常带辅助列）；属性拿不到列**留默认值**；**一颗属性都没对上时抛**（整行空壳读起来像"查到了"不像"列名对不上"）。
- 当场抛的还有：位置式参数家带参数对象；`CancellationToken` 出现在参数对象那一位或藏在属性里；参数对象没有公共实例属性；形状是 record/构造器 DTO（这一口没有参数顺序对应物——record 走低层口 `ToRawSql` + 带行委托的 `QueryAsync`）。
- 值转换与链式口同一份代码（枚举、Guid 字节序、`DateOnly/TimeOnly` 折自 `DateTime` 等）。
- `CancellationToken` 正确用法是方法参数那一颗，不是塞进 args。

### 会话与事务

`orm.Ado` 与链式口走同一条执行链：**会话连接复用、事务环境、`CommandTimeout`、AOP/日志全在**。环境事务开着时，`orm.Ado` 的语句也在同一场里。

## 2. `WithSql`：只换 FROM 那一段

```csharp
var query = orm.Select<Order>()                                     // 外层按 Order 的列契约选列
    .WithSql("SELECT * FROM Orders WHERE CustomerId = @customerId",
             new { customerId = 7 })                                // 内层这一段一颗不动
    .Where(o => o.Amount >= 200m)                                   // → 外层 WHERE [t0].[Amount] >= @p1
    .OrderBy(o => o.Id);

var rows = await query.ToListAsync();
var total = await query.CountAsync();   // 派生表套原生源：SELECT COUNT(*) FROM ( … ) AS [t0]
```

- 参数折法与 `orm.Ado` 同一份实现；多一条：**参数名不许长成 `前缀 + p + 数字`**——那是生成参数的保留号段（撞上是两处绑定抢同一个名字）。
- 原生参数先登记、生成的接着编号：同一句里出现 `@customerId`、`@p1`、`@p2`（没有 `@p0`）。`ToSql(out parameters)` 里看得到。
- 外层按契约选列，所以内层必须交回契约里那些列名（`SELECT *` 天然成立；内层列名要改名就接 `Select` 投影）。
- 只有单表链（arity 1）有这一颗口；`As("别名")` 写在 `WithSql` 之后也安全。

## 3. 低层口（record / 自定义行映射）

```csharp
using QuickSql.Sql;          // DialectSql
using QuickSql.Execution;    // RawSql / ExecutionTag

DialectSql dialect = orm.Ado.ToRawSql(sql, args);        // (文本, 参数对象) → 方言 SQL
var rows = await orm.Ado.QueryAsync(dialect, (DbDataReader r) => new MyDto(r.GetInt32(0), r.GetString(1)),
                                    tag: RawSql.Tag, expectedRows: null, ct: default);
```

这一族带 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`（属性清单与读写委托按运行期类型建）；AOT 发布时这一格要显式承认（链式 DTO 投影同一档）。

---

[← 上一篇：13 结构同步](13-schema-sync.md) · [下一篇：15 多数据库支持 →](15-multi-database.md)
