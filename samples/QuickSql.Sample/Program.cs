using QuickSql;
using QuickSql.Aop;
using QuickSql.DriverLoading;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Providers.SqlServer;
using QuickSql.Query;
using QuickSql.Sample;
using QuickSql.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

// ── 0. 准备：删掉旧库文件，保证每次运行都是干净的 ────────────────────────
var dbFile = "quicksql-sample.db";
if (File.Exists(dbFile)) File.Delete(dbFile);
var connectionString = $"Data Source={dbFile}";

// ── 1. DI 注册 ──────────────────────────────────────────────────────────
//
// 简单模式：连接串启动期确定
//
var services = new ServiceCollection();

services.AddQuickSqlOrm(DbKind.Sqlite, connectionString, builder =>
{
    builder.SchemaSync = SchemaSyncPolicy.Execute; // 开发环境自动建表
    builder.ForceSchemaSync = true;                // 示例跳过环境检查
});

// 注册 DbContext（Scoped）
services.AddScoped<AppDbContext>();

using var sp = services.BuildServiceProvider();

// ── 2. 建库 + 同步表结构 ────────────────────────────────────────────────
//
// SQLite 打开连接即建文件，但 SqlServer/MySql 等需要先 EnsureDatabaseExistsAsync。
// SchemaSync = Execute 时 SyncStructureAsync 内部会先调 EnsureDatabaseExistsAsync，
// 所以这一行同时完成"建库 + 建表"两件事。
//
using var scope = sp.CreateScope();
var orm = scope.ServiceProvider.GetRequiredService<QuickSqlOrm>();

// 注册表是惰性的：实体第一次被 Select/Set 触及时才进 RegisteredTypes。
// SyncStructure 按 RegisteredTypes 建表，所以要先"碰一下"每个实体类型。
_ = orm.Select<Customer>();
_ = orm.Select<Order>();
_ = orm.Select<OrderItem>();

var syncResult = await orm.SyncStructureAsync();
Console.WriteLine($"表结构同步完成：新建 {syncResult.CreatedTables.Count} 张表");
foreach (var t in syncResult.CreatedTables)
    Console.WriteLine($"  - {t}");

// 结构同步不止"建缺失的表"：表已在的时候它逐列比（缺的列、长度/标度、可空性、备注、缺失的索引），
// 而比出来的东西分三份交回——发了什么、这一家读不回来所以判不了什么、读得回来而故意不发的什么。
// 本示例跑在 SQLite 上：那一家没有列注释也没有表注释这一档，所以 Customers 上写的两句备注落进"判不了"，
// 而不是被静默丢掉。换 SQL Server 跑同一段代码，这两句会真的写进 sys.extended_properties。
// 库里"多出来"的东西一律只进那两份话单：多一列不删（会带走数据），多一条索引也不删——
// 删索引要 SchemaSyncDropIndexes 显式开，它默认关，所以这一行的最后一格在这里恒为 0。
Console.WriteLine($"补列 {syncResult.AddedColumns.Count} ∶ 改列 {syncResult.AlteredColumns.Count} ∶ 备注 {syncResult.AppliedComments.Count} ∶ 建索引 {syncResult.CreatedIndexes.Count} ∶ 删索引 {syncResult.DroppedIndexes.Count}");

foreach (var line in syncResult.NotJudged)
    Console.WriteLine($"  判不了：{line}");

foreach (var line in syncResult.Refused)
    Console.WriteLine($"  要人工：{line}");

// 同一趟再跑一次：一个字节都不发。这一句是"比的是归一后的结构事实"的唯一裁判——
// 判据写成类型文本逐字符比的话，第二趟会在每张表上重发一句 ALTER（目录里是小写 numeric，我们发的是大写 DECIMAL）。
var secondPass = await orm.SyncStructureAsync();
Console.WriteLine($"第二趟：{(secondPass.IsEmpty ? "0 个动作" : $"还在发：{string.Join("、", secondPass.AddedColumns.Concat(secondPass.AlteredColumns))}")}");

Console.WriteLine();

// ── 3. 通过 DbContext 做增删改查（跟踪路径） ────────────────────────────
Console.WriteLine("═══ 3. DbContext 跟踪路径 ═══");
Console.WriteLine();

var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

// ── 3.1 新增 ──
var customer = new Customer { Name = "张三", City = "上海" };
db.Add(customer);
await db.SaveChangesAsync();
Console.WriteLine($"新增客户：Id={customer.Id}, Name={customer.Name}");

// ── 3.2 查询 ──
var found = await db.Customers
    .Where(c => c.Name == "张三")
    .FirstOrDefaultAsync();
Console.WriteLine($"查询客户：Id={found?.Id}, Name={found?.Name}, City={found?.City}");

// ── 3.3 修改 ──
found!.City = "北京";
await db.SaveChangesAsync(); // AutoDetectChanges 自动侦测到 City 变了

var updated = await db.Customers.Where(c => c.Id == found.Id).FirstOrDefaultAsync();
Console.WriteLine($"修改客户：Id={updated?.Id}, City={updated?.City}");

// ── 3.4 删除 ──
db.Remove(updated!);
await db.SaveChangesAsync();

var count = await db.Customers.Where(c => c.Name == "张三").ToListAsync();
Console.WriteLine($"删除客户后查询：找到 {count.Count} 条");

Console.WriteLine();

// ── 4. 主从明细增删改查（Order + OrderItem） ─────────────────────────────
Console.WriteLine("═══ 4. 主从明细（DbContext） ═══");
Console.WriteLine();

// 先建一个客户
var cust = new Customer { Name = "李四", City = "广州" };
db.Add(cust);
await db.SaveChangesAsync();
Console.WriteLine($"客户：Id={cust.Id}");

// ── 4.1 新增主表 + 明细（一起保存） ──
//
// SaveChanges 按外键边自动排写入先后：先 INSERT Order，拿到自增 Id，
// 再 INSERT OrderItem（此时 OrderId 已回填）。
//
var order = new Order
{
    CustomerId = cust.Id,
    Amount = 299.50m,
    CreatedAt = DateTime.Now,
    Note = "第一笔订单",
};
db.Add(order);

var item1 = new OrderItem { OrderId = order.Id, Sku = "SKU-001", Quantity = 2, Price = 99.75m };
var item2 = new OrderItem { OrderId = order.Id, Sku = "SKU-002", Quantity = 1, Price = 100.00m };
db.Add(item1);
db.Add(item2);

await db.SaveChangesAsync();
Console.WriteLine($"订单：Id={order.Id}, Amount={order.Amount}");
Console.WriteLine($"  明细1：Id={item1.Id}, OrderId={item1.OrderId}, Sku={item1.Sku}");
Console.WriteLine($"  明细2：Id={item2.Id}, OrderId={item2.OrderId}, Sku={item2.Sku}");

// ── 4.2 查询主表 + 按外键查明细 ──
var orders = await db.Orders
    .Where(o => o.CustomerId == cust.Id)
    .ToListAsync();

foreach (var o in orders)
{
    var items = await db.OrderItems
        .Where(i => i.OrderId == o.Id)
        .ToListAsync();
    Console.WriteLine($"订单 {o.Id}：{items.Count} 行明细");
    foreach (var i in items)
        Console.WriteLine($"  - {i.Sku} × {i.Quantity} @ {i.Price}");
}

// ── 4.3 修改明细 ──
item1.Quantity = 5;
db.Update(item1); // 标为全列脏
await db.SaveChangesAsync();

var refreshed = await db.OrderItems.Where(i => i.Id == item1.Id).FirstOrDefaultAsync();
Console.WriteLine($"修改明细后：Sku={refreshed?.Sku}, Quantity={refreshed?.Quantity}");

// ── 4.4 删除明细 ──
db.Remove(item2);
await db.SaveChangesAsync();

var remaining = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync();
Console.WriteLine($"删除明细后：剩余 {remaining.Count} 行");

// ── 4.5 删除主表（级联删除：明细要先在会话里，父才删得动） ──
//
// 会话看不见子行时，它说不出"这一串是真的没有，还是还没捞出来"，所以直接删父会响
// （可达才删，捞不到就拒）。先把那一串捞进会话再删：明细跟着一起标删，
// SaveChanges 按逆序发语句（先明细再父），不必再一只只 db.Remove。
db.Orders.Load(o => o.Items!);
db.Remove(order);
await db.SaveChangesAsync();

var orderCount = await db.Orders.Where(o => o.CustomerId == cust.Id).ToListAsync();
var itemCount = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync();
Console.WriteLine($"删除主从后：订单 {orderCount.Count} 条，明细 {itemCount.Count} 条");

// ── 4.6 级联插入：只 Add 父，明细跟着进会话 ──
//
// 上面那两行明细是一只只 db.Add 进去的；这一节只 Add 父。集合导航 Order.Items
// 上没打 [NoCascade]，会话顺那根边把明细一起带进来，并按边排写入先后：
// 先 INSERT 父拿到自增 Id，再把那颗 Id 落进每一行明细的 OrderId（临时键不进库）。
var bill = new Order
{
    CustomerId = cust.Id,
    Amount = 120.00m,
    CreatedAt = DateTime.Now,
    Note = "级联那一笔",
    Items =
    [
        new OrderItem { Sku = "SKU-A", Quantity = 1, Price = 40.00m },
        new OrderItem { Sku = "SKU-B", Quantity = 2, Price = 40.00m },
    ],
};

db.Add(bill);
Console.WriteLine($"一句 Add 之后会话里：{db.ChangeTracker.Count} 只（1 父 + 2 明细）");

await db.SaveChangesAsync();
Console.WriteLine($"级联插入：父 Id={bill.Id}，两行明细回填到的外键 = {bill.Items[0].OrderId} / {bill.Items[1].OrderId}");

// ── 4.7 摘行：从集合里摘掉一只，就是删掉库里那一行 ──
//
// 先把那一串捞进会话（Load 那根导航），再从集合里 Remove 一只。会话比的是
// "上一次看见的那一串"与"此刻这一串"，缺席的那一只进删除档；留着的那一行不动，父也不动。
db.Orders.Load(o => o.Items!);

var pulled = bill.Items!.First(i => i.Sku == "SKU-B");
bill.Items.Remove(pulled);

var detachReport = await db.SaveChangesAsync();
var survivors = await db.OrderItems.Where(i => i.OrderId == bill.Id).ToListAsync();
Console.WriteLine($"摘掉一行：发了 {string.Join(" → ", detachReport.Operations.Select(op => op.Table + "/" + op.Operation))}，库里剩 {survivors.Count} 行（{string.Join("、", survivors.Select(s => s.Sku))}）");

// ── 4.8 级联删除：明细在手，删父一起走（逆序发语句）──
db.Orders.Load(o => o.Items!);
db.Remove(bill);

var cascadeReport = await db.SaveChangesAsync();
var leftItems = await db.OrderItems.Where(i => i.OrderId == bill.Id).ToListAsync();
Console.WriteLine($"级联删除：{string.Join(" → ", cascadeReport.Operations.Select(op => op.Table + "/" + op.Operation))}，父 {bill.Id} 名下剩 {leftItems.Count} 行");

Console.WriteLine();

// ── 5. 通过原始 ORM 做增删改查（非跟踪路径） ────────────────────────────
Console.WriteLine("═══ 5. 原始 ORM 非跟踪路径 ═══");
Console.WriteLine();

// ── 5.1 插入 ──
var newCust = new Customer { Name = "王五", City = "深圳" };
var identity = await orm.Insert(newCust).ExecuteIdentityAsync();
newCust.Id = (int)identity;
Console.WriteLine($"Insert 插入：Id={newCust.Id}, Name={newCust.Name}");

// ── 5.2 查询 ──
var queried = await orm.Select<Customer>()
    .Where(c => c.City == "深圳")
    .ToListAsync();
Console.WriteLine($"Select 查询：找到 {queried.Count} 条，Name={queried[0].Name}");

// ── 5.3 更新 ──
var affrows = await orm.Update<Customer>()
    .Set(c => c.City, "杭州")
    .Where(c => c.Id == newCust.Id)
    .ExecuteAffrowsAsync();
Console.WriteLine($"Update 链：影响 {affrows} 行");

var afterUpdate = await orm.Select<Customer>().Where(c => c.Id == newCust.Id).FirstOrDefaultAsync();
Console.WriteLine($"更新后：City={afterUpdate?.City}");

// ── 5.4 删除 ──
var delRows = await orm.Delete<Customer>()
    .Where(c => c.Id == newCust.Id)
    .ExecuteAffrowsAsync();
Console.WriteLine($"Delete 链：影响 {delRows} 行");

Console.WriteLine();

// ── 6. 事务 ─────────────────────────────────────────────────────────────
Console.WriteLine("═══ 6. 事务 ═══");
Console.WriteLine();

// 回调式事务：body 内抛异常自动回滚
var result = await orm.ExecuteInTransactionAsync(async ct =>
{
    var c = new Customer { Name = "赵六", City = "成都" };
    await orm.Insert(c).ExecuteAffrowsAsync(ct);
    Console.WriteLine($"  事务内插入：Name={c.Name}");
});
Console.WriteLine($"事务结果：Success={result.Success}");

// 手动事务：不调 Commit = 回滚
using (var tran = orm.BeginTransaction())
{
    await orm.Insert(new Customer { Name = "临时", City = "临时" }).ExecuteAffrowsAsync();
    // 不调 tran.Commit()，using 结束时自动回滚
}
var tempCount = await orm.Select<Customer>().Where(c => c.Name == "临时").ToListAsync();
Console.WriteLine($"回滚后查询[临时]：找到 {tempCount.Count} 条（应为 0）");

Console.WriteLine();

// ── 7. 子查询（IQuery 捕获 → 标量子查询 / IN 子查询） ─────────────────
Console.WriteLine("═══ 7. 子查询 ═══");
Console.WriteLine();

// ── 7.1 补种子数据 ──
var cA = new Customer { Name = "陈七", City = "上海" };
var cB = new Customer { Name = "周八", City = "北京" };
var cC = new Customer { Name = "吴九", City = "上海" };
await orm.Insert(cA).ExecuteIdentityAsync();
await orm.Insert(cB).ExecuteIdentityAsync();
await orm.Insert(cC).ExecuteIdentityAsync();

var ordersA = new[]
{
    new Order { CustomerId = cA.Id, Amount = 100m,  CreatedAt = DateTime.Now, Note = "A-1" },
    new Order { CustomerId = cA.Id, Amount = 200m,  CreatedAt = DateTime.Now, Note = "A-2" },
    new Order { CustomerId = cA.Id, Amount = 300m,  CreatedAt = DateTime.Now, Note = "A-3" },
};
var ordersB = new[]
{
    new Order { CustomerId = cB.Id, Amount = 500m,  CreatedAt = DateTime.Now, Note = "B-1" },
    new Order { CustomerId = cB.Id, Amount = 600m,  CreatedAt = DateTime.Now, Note = "B-2" },
};
// ExecuteIdentityAsync 的返回值就是那一行的自增 Id，它不回写实体（§5.1 同一姿势），
// 而下面两行要用它给明细挂外键，所以必须接住 —— 漏掉这一步，明细的 OrderId 全留 0。
foreach (var o in ordersA.Concat(ordersB))
    o.Id = (int)await orm.Insert(o).ExecuteIdentityAsync();

foreach (var o in ordersA)
    await orm.Insert(new OrderItem { OrderId = o.Id, Sku = "SKU-A", Quantity = 1, Price = o.Amount }).ExecuteAffrowsAsync();
foreach (var o in ordersB)
    await orm.Insert(new OrderItem { OrderId = o.Id, Sku = "SKU-B", Quantity = 2, Price = o.Amount / 2 }).ExecuteAffrowsAsync();
Console.WriteLine("种子数据：3 客户、5 订单、5 明细");

// ── 7.2 Count 子查询 ──
//
// 捕获一个 IQuery<Order>，在 Where 里调 .Count()，
// 翻译器把它编译成标量子查询 (SELECT COUNT(*) FROM Orders)。
// 注意：子查询要先存进变量再调终端方法，不能在链式调用里直接写。
//
var allOrders = orm.Select<Order>();
var totalOrderCount = await allOrders.CountAsync();
Console.WriteLine($"Count 直接调用：共 {totalOrderCount} 笔订单");

// 子查询用法：找出"订单总数 > 3"时列出的客户（非关联——子查询不引用外层 c）
var countSub = orm.Select<Order>();
var customersWhenManyOrders = await orm.Select<Customer>()
    .Where(c => countSub.Count() > 3)
    .ToListAsync();
Console.WriteLine($"Count 子查询（总订单 > 3 时列出所有客户）：{string.Join(", ", customersWhenManyOrders.Select(c => c.Name))}");

// ── 7.3 Contains 子查询（IN） ──
//
// 捕获一个 IQuery<int>（投影出 CustomerId），在 Where 里调 .Contains(c.Id)，
// 翻译器把它编译成 IN (SELECT CustomerId FROM Orders WHERE Amount >= 500)。
//
var highValueCustomerIds = orm.Select<Order>()
    .Where(o => o.Amount >= 500m)
    .Select(o => o.CustomerId);
var highValueCustomers = await orm.Select<Customer>()
    .Where(c => highValueCustomerIds.Contains(c.Id))
    .ToListAsync();
Console.WriteLine($"Contains 子查询（有 ≥500 订单的客户）：{string.Join(", ", highValueCustomers.Select(c => c.Name))}");

// ── 7.4 Sum / Max / Min / Average 直接调用 ──
//
// 在 IQuery 上直接调聚合方法，翻译成 SELECT SUM/MAX/MIN/AVG(...) FROM ...。
//
var sumAmount = await allOrders.SumAsync(o => o.Amount);
var maxAmount = await allOrders.MaxAsync(o => o.Amount);
var minAmount = await allOrders.MinAsync(o => o.Amount);
var avgAmount = await allOrders.AverageAsync(o => o.Amount);
Console.WriteLine($"Sum：总订单金额 = {sumAmount}");
Console.WriteLine($"Max：最高单笔 = {maxAmount}");
Console.WriteLine($"Min：最低单笔 = {minAmount}");
Console.WriteLine($"Average：平均单笔 = {avgAmount}");

// ── 7.5 Sum / Max 子查询 ──
//
// 在 Where 里当标量子查询用：子查询独立于外层参数，翻译器编译成
// (SELECT SUM(Amount) FROM Orders) 嵌入外层 WHERE。
// 别名作用域已隔离——子查询的表走 t1000+ 序列，不会和外层 t0 撞名。
//
var totalSum = orm.Select<Order>();
var customersWhenBigTotal = await orm.Select<Customer>()
    .Where(c => totalSum.Sum(o => o.Amount) > 1000m)
    .ToListAsync();
Console.WriteLine($"Sum 子查询（总订单金额 > 1000 时列出客户）：{string.Join(", ", customersWhenBigTotal.Select(c => c.Name))}");

var maxSub = orm.Select<Order>();
var customersWhenHighMax = await orm.Select<Customer>()
    .Where(c => maxSub.Max(o => o.Amount) > 500m)
    .ToListAsync();
Console.WriteLine($"Max 子查询（最高单笔 > 500 时列出客户）：{string.Join(", ", customersWhenHighMax.Select(c => c.Name))}");

Console.WriteLine();

// ── 8. 分组查询（GROUP BY / HAVING） ───────────────────────────────────
Console.WriteLine("═══ 8. 分组查询 ═══");
Console.WriteLine();

// ── 8.1 单列分组 + 多个聚合 ──
//
// GroupBy 之后链上能选的只有 g.Key 与聚合：形参类型是 IGrouping<TKey, TValue>，
// 它除了 Key 就没有实体成员，所以"分组后偷偷选一列非键"这种句子在编译期就过不去（§5.3）。
// 聚合选择器里那个 o 是"组里的一行"，落回同一张表位 —— 发的是 SUM([t0].[Amount])，不是相关子查询。
//
var perCustomer = await orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .Select(g => new
    {
        CustomerId = g.Key,
        Orders = g.Count(),
        Total = g.Sum(o => o.Amount),
        Largest = g.Max(o => o.Amount),
        Smallest = g.Min(o => o.Amount),
        Average = g.Average(o => o.Amount),
    })
    .ToListAsync();

Console.WriteLine("按客户分组（CustomerId / 笔数 / 合计 / 最大 / 最小 / 平均）：");
foreach (var row in perCustomer)
    Console.WriteLine($"  客户 {row.CustomerId}：{row.Orders} 笔，合计 {row.Total}，最大 {row.Largest}，最小 {row.Smallest}，平均 {row.Average:0.##}");

// 未选出的那一组也在库里占一行；要"每组只给一个标量"就整条投影成一列。
var orderCountPerCustomer = await orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .Select(g => g.Count())
    .ToListAsync();
Console.WriteLine($"只投影聚合（g => g.Count()）：[{string.Join(", ", orderCountPerCustomer)}]");

// ── 8.2 HAVING：分组之后筛组 ──
//
// Having 的谓词落进 HAVING 而不是 WHERE —— 这件事由"它在 GroupBy 之后"决定。
// 分组之前的条件仍然写在 Where 里（那条链在 GroupBy 之前，见 §8.5）。
//
var regulars = await orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .Having(g => g.Count() > 2)
    .Select(g => new { CustomerId = g.Key, Orders = g.Count(), Total = g.Sum(o => o.Amount) })
    .ToListAsync();

Console.WriteLine($"Having 只留 2 笔以上的客户：{string.Join(", ", regulars.Select(r => $"客户{r.CustomerId}({r.Orders}笔/{r.Total})"))}");

// ── 8.3 复合分组键 + DTO 投影 ──
//
// 键写成 new { … } 就是复合键：GROUP BY 里按声明序发几列，而 g.Key 整体不是一个值，
// 只能逐成员读 g.Key.OrderId / g.Key.Sku（成员名就是匿名类型上那一个）。
//
var skuTotals = await orm.Select<OrderItem>()
    .GroupBy(i => new { i.OrderId, i.Sku })
    .Select(g => new SkuTotal
    {
        OrderId = g.Key.OrderId,
        Sku = g.Key.Sku,
        Units = g.Sum(i => i.Quantity),
        Amount = g.Sum(i => i.Quantity * i.Price),
    })
    .ToListAsync();

Console.WriteLine("按 (订单, 货号) 复合键分组的 DTO 投影：");
foreach (var t in skuTotals)
    Console.WriteLine($"  订单 {t.OrderId} / {t.Sku}：{t.Units} 件，金额 {t.Amount}");

// ── 8.4 分组排序 + 取前 N 名 ──
//
// OrderBy 在 GroupBy 之后排的是"组"，所以键与聚合都能当排序依据。
// Take 的页与组同一单位：LIMIT 2 拿的是前两组。
//
var topTwo = orm.Select<Order>()
    .Where(o => o.Amount > 0m)
    .GroupBy(o => o.CustomerId)
    .OrderByDescending(g => g.Sum(o => o.Amount))
    .Take(2)
    .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.Amount) });

Console.WriteLine("前两组（按合计金额降序）产出的 SQL：");
Console.WriteLine($"  {topTwo.ToLogSql()}");

foreach (var row in await topTwo.ToListAsync())
    Console.WriteLine($"  客户 {row.CustomerId}：合计 {row.Total}");

// ── 8.5 Explain()：分组键与 HAVING 都在模型文本里 ──
var grouped = orm.Select<Order>()
    .Where(o => o.Amount >= 200m)
    .GroupBy(o => o.CustomerId)
    .Having(g => g.Count() > 1)
    .Select(g => new { CustomerId = g.Key, Orders = g.Count(), Total = g.Sum(o => o.Amount) });

var plan = grouped.Explain();

Console.WriteLine("Explain() 模型文本里的分组那几行（摘录）：");
foreach (var line in plan.ModelText.Split('\n').Where(l => l.Contains("WHERE") || l.Contains("GROUP BY") || l.Contains("HAVING")))
    Console.WriteLine($"  {line.TrimEnd()}");

// 上面 HAVING 那一行的 COUNT({0}) 是模型文本打印器的既有缺陷（函数模板的占位没被替掉），
// 不是模型里真写着 {0}；它对任何带函数的投影都这样，与分组无关。产出 SQL 那一行是替好的。
Console.WriteLine($"同一条查询产出的 SQL：  {grouped.ToLogSql()}");

var filteredGroups = await grouped.ToListAsync();

Console.WriteLine($"先 WHERE 再 GROUP BY 再 HAVING：{string.Join(", ", filteredGroups.Select(r => $"客户{r.CustomerId}({r.Orders}笔/{r.Total})"))}");

// 还没接住的两件事，都会当场报错而不是给一个看起来合理的数（不是静默降级）：
//   · 联表之后的分组 —— GroupBy 今天开在单表链（IQuery<T>）上，两表/三表链没有这一颗口；
//   · 组内去重计数 g.CountDistinct(…) —— COUNT(DISTINCT x) 与 FILTER 各家不同，未接入。
//   分组之后的计数与分页总数已经接住（派生表，见下面 §10），而分组之后的 SUM/Max 仍在这一格外 ——
//   这几件今天都能用下面 §9 的原生 SQL 口自己写出来。

Console.WriteLine();

// ── 9. 原生 SQL 逃生舱（orm.Ado 的字符串口） ───────────────────────────
Console.WriteLine("═══ 9. 原生 SQL ═══");
Console.WriteLine();

// ── 9.1 手写一句分组 SQL，行按「列名对属性名」装配 ──
//
// 三个约定：文本一颗不动（不重写占位符、不补引号、不校验语法）；参数名 = 本家前缀 + 属性名，
// 所以文本里的占位符必须写成 @minAmount 这一形；行落在「公共无参构造 + 公共可写属性」的形状上。
// 对不上属性的列被忽略（报表 SQL 常带辅助列），属性拿不到列则留默认值（那句 SQL 少选了一列）。
//
var report = await orm.Ado.QueryAsync<CustomerReport>(
    """
    SELECT o.CustomerId  AS CustomerId,
           c.Name        AS Name,
           COUNT(*)      AS Orders,
           SUM(o.Amount) AS Total
      FROM Orders o
      JOIN Customers c ON c.Id = o.CustomerId
     WHERE o.Amount >= @minAmount
     GROUP BY o.CustomerId, c.Name
     ORDER BY SUM(o.Amount) DESC
    """,
    new { minAmount = 100m });

Console.WriteLine("手写 GROUP BY 的报表行：");
foreach (var row in report)
    Console.WriteLine($"  {row.Name}（客户 {row.CustomerId}）：{row.Orders} 笔，合计 {row.Total}");

// ── 9.2 标量出口：第一行第一列，DBNull 折成 null ──
var totalOrders = await orm.Ado.ExecuteScalarAsync("SELECT COUNT(*) FROM Orders WHERE Amount >= @min", new { min = 100m });

Console.WriteLine($"ExecuteScalarAsync：金额 ≥100 的订单共 {totalOrders} 笔");

// ── 9.3 一行一个标量：直读第 0 列，不进名字匹配 ──
var biggest = await orm.Ado.FirstAsync<decimal>("SELECT MAX(Amount) FROM Orders WHERE CustomerId = @customerId", new { customerId = cA.Id });

Console.WriteLine($"FirstAsync<decimal>：客户 {cA.Id} 的最大单笔 = {biggest}");

// ── 9.4 逐行流式：不先把整张结果集读进 List ──
Console.Write("StreamAsync<string>（上海的客户名）：");

await foreach (var name in orm.Ado.StreamAsync<string>(
                   "SELECT Name FROM Customers WHERE City = @city ORDER BY Name", new { city = "上海" }))
    Console.Write($"{name} ");

Console.WriteLine();

// ── 9.5 非查询：与链式写路径同一个出口，因此同一条 AOP 链、同一次连接复用 ──
var affected = await orm.Ado.ExecuteNonQueryAsync(
    "UPDATE Orders SET Note = @note WHERE CustomerId = @customerId",
    new { note = "原生口", customerId = cA.Id });

// 读回来验证：原生口写的，链式口读得到 —— 这里没有第二条通道，只有一个 ADO 出口。
var noted = await orm.Select<Order>().Where(o => o.CustomerId == cA.Id && o.Note == "原生口").CountAsync();

Console.WriteLine($"ExecuteNonQueryAsync：改了 {affected} 行，链式口读回 {noted} 行 Note=原生口");

// ── 9.6 一次调用发多句：共用一条连接，而它不开事务 ──
//   要把这一串包进事务请显式 BeginTransaction —— 「这一串是不是一个原子单位」只有 SQL 作者知道。
await orm.Ado.ExecuteBatchAsync(
[
    "CREATE TABLE IF NOT EXISTS SampleBatchProbe (Seq INTEGER PRIMARY KEY, Label TEXT)",
    "INSERT INTO SampleBatchProbe (Label) VALUES ('第一句')",
    "INSERT INTO SampleBatchProbe (Label) VALUES ('第二句')",
]);

var probe = await orm.Ado.QueryAsync<string>("SELECT Label FROM SampleBatchProbe ORDER BY Seq");

await orm.Ado.ExecuteNonQueryAsync("DROP TABLE SampleBatchProbe");

Console.WriteLine($"ExecuteBatchAsync 三句：读回 [{string.Join(", ", probe)}]，探针表已清掉");

// ── 9.7 record / 构造器 DTO：走低层口，自己给行委托 ──
//
// 名字装配那一条不接 record（参数顺序在这一口里没有对应物，列序由那句 SQL 决定）。
// 低层口收 (DialectSql, Func<DbDataReader, T>, ExecutionTag)，ToRawSql 负责把文本 + 参数对象折成 DialectSql，
// 行委托里用 ValueConverter（库里唯一的取值门）而不是各型各读一份。
//
var top = await orm.Ado.QueryAsync(
    orm.Ado.ToRawSql(
        "SELECT CustomerId, SUM(Amount) AS Total FROM Orders GROUP BY CustomerId ORDER BY Total DESC LIMIT 1",
        null),
    static reader => new CustomerTop(
        QuickSql.Materialization.ValueConverter.Read<int>(reader, 0),
        QuickSql.Materialization.ValueConverter.Read<decimal>(reader, 1)),
    QuickSql.Execution.RawSql.Tag,
    null,
    default);

Console.WriteLine($"低层口装 record：{top.FirstOrDefault()}");

Console.WriteLine();

// ── 10. 派生表（分组/DISTINCT/带页之后的计数）与 WithSql ────────────────
Console.WriteLine("═══ 10. 派生表与 WithSql ═══");
Console.WriteLine();

// ── 10.1 分组之后的总数：整份分组进内层，外层只发 COUNT(*) ──
//
// 这一格以前会当场报错，因为"照着原模型换一个 COUNT(*)"得到的是每组里的行数（§5.12）。
// 两个数放在一起打印：内层交回几组、外层数出几组，对了才说明包的是内层而不是换掉的整句。
//
var byCustomer = orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .OrderByDescending(g => g.Count())
    .Select(g => new { CustomerId = g.Key, Orders = g.Count() });

var groups = await byCustomer.ToListAsync();
var groupTotal = await byCustomer.CountAsync();

Console.WriteLine($"分组交回 {groups.Count} 组，派生表外层数出 {groupTotal} 组");
Console.WriteLine($"  同一条链自己端出去（{byCustomer.ToLogSql()}），而它 count 的那一句把 ORDER BY 剥掉了 —— 内层没有分页时序就不参与（§5.12）。");

// ── 10.2 分组查询的分页：页与组同一单位，总数也是组数 ──
var groupedPage = await orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .OrderBy(g => g.Key)
    .Select(g => new { CustomerId = g.Key, Orders = g.Count() })
    .ToPagedAsync(page: 1, size: 2, new PagedOptions(CountMethod.CountStar));

Console.WriteLine($"分组分页：第 {groupedPage.Page} 页 {groupedPage.Items.Count} 行，总组数 {groupedPage.Total}（这一格点名了 CountMethod.CountStar；Auto 在首页只多取一行判 HasNext，Total 就是 null）");

// ── 10.3 DISTINCT 之后计数：数的是去重后的行 ──
var distinctCustomers = await orm.Select<Order>().Select(o => o.CustomerId).Distinct().CountAsync();
var allCustomers = await orm.Select<Order>().Select(o => o.CustomerId).CountAsync();

Console.WriteLine($"去重后的客户数 {distinctCustomers}（不去重是 {allCustomers} 行订单）");

// ── 10.4 带页之后再计数：数的就是那一页的行，不是全表 ──
var paged = orm.Select<Order>().OrderBy(o => o.Id).Skip(1).Take(2);
var pageRows = await paged.CountAsync();
var allRows = await orm.Select<Order>().CountAsync();

Console.WriteLine($"Skip(1).Take(2) 之后 CountAsync = {pageRows}，全表 = {allRows}（分页那两颗粒留在内层：{paged.ToLogSql()}）");

// ── 10.5 WithSql：里面那一段自己写，外面这一层仍然归库 ──
//
// 原生文本一颗不动（连表名都不加引号），而链上的 Where/OrderBy 按列契约端出去，
// 所以参数集合里先是原生那颗（@customerId），生成的那颗接着编号（@p1）—— 两处不可能撞名。
//
var native = orm.Select<Order>()
    .WithSql("SELECT * FROM Orders WHERE CustomerId = @customerId", new { customerId = cA.Id })
    .Where(o => o.Amount >= 200m)
    .OrderBy(o => o.Id);

Console.WriteLine($"WithSql 外层由库写：{native.ToLogSql()}");
Console.WriteLine($"  读回 {await native.CountAsync()} 行，其中最大单笔 {await native.MaxAsync(o => o.Amount)}");

// 外层是 COUNT(*) 时，原生那一段就落在派生表的内层位置上 —— 两格合起来用，谁写内层仍然只有一处含糊。
var nativeCount = orm.Select<Customer>().Where(c => native.Count() > 0);

Console.WriteLine($"派生表套原生源：{nativeCount.ToLogSql()}");

Console.WriteLine();

// ── 11. 批量高速层（ExplainBulk / WithBulk / ExecuteBulkAsync） ─────────
Console.WriteLine("═══ 11. 批量高速层 ═══");
Console.WriteLine();

// 阶梯从上往下取第一个满足条件的层：装载通道（L1）→ 多值语句（L3）→ 逐条（L5）。
// 装载通道那一档今天只有 SQL Server 一家接（src/QuickSql.Providers.SqlServer/SqlServerBulk.cs:40 那颗
// ResolveCopyChannel），而本示例跑在 SQLite 上 —— 那一家的 Bulk 直接是 null
// （src/QuickSql.Providers.Sqlite/SqliteProvider.cs:220）。所以下面每一颗交回的都是文本层那两档，
// 而"退到了哪一层、为什么退"这件事本身是打印得出来的：Reason 那句话就是给现场读的。
var bulk = Enumerable.Range(1, 500).Select(i => new Order
{
    CustomerId = cA.Id,
    Amount = 10m + i,
    CreatedAt = new DateTime(2026, 3, 1).AddMinutes(i),
    Note = "bulk-layer",
}).ToArray();

// 决策与执行共用同一次判定，所以这一颗不连库也报得出块数（它不建文本、不发语句，但要反射问一次"有没有通道"）。
var layer = orm.InsertRange(bulk).ExplainBulk();

Console.WriteLine($"Auto 取到的层：{layer.Method} ∶ {layer.RowCount} 行 / {layer.RoundTrips} 次往返 / 每块 {layer.ChunkSize} 行");
Console.WriteLine($"  为什么：{layer.Reason}");

// 请求了装载通道而这一家没有：默认策略 DegradeAndLog 退 L3，并在订阅位上留一次痕（未订阅时零成本）。
var traces = new List<string>();
orm.Aop.BulkDegraded = e => traces.Add($"{e.Requested} → {e.Method}");

var degraded = await orm.InsertRange(bulk).WithBulk(BulkMethod.CopyChannel).ExecuteBulkAsync();

Console.WriteLine($"请求 {degraded.Requested} 实际走了 {degraded.Method}（Degraded={degraded.Degraded}），留痕 {traces.Count} 次：{traces.FirstOrDefault()}");

// 同一条请求换成 Never：拿不到请求的那一层就抛。这是 CI 里防「批量悄悄变成逐条」的那一闸。
try
{
    await orm.InsertRange(bulk).WithBulk(BulkMethod.CopyChannel, FallbackPolicy.Never).ExecuteBulkAsync();
}
catch (QuickSqlException ex)
{
    Console.WriteLine($"  Never 拦下了：{ex.Message}");
}

// 指向装载通道的请求在行数出口那里也是拒，不是「默默当没看见」：那颗出口返回一颗 int，
// 装不下"走了哪一层、几次往返"，而 COPY 那一档根本不产 SQL 文本（改写钩子会集体失效）。
try
{
    await orm.InsertRange(bulk).WithBulk(BulkMethod.CopyChannel).ExecuteAffrowsAsync();
}
catch (QuickSqlException ex)
{
    Console.WriteLine($"  行数出口拒了：{ex.Message}");
}

// 旋钮 rowsPerRoundTrip 只许往下压、不许往上抬：这里把每块压到 20 行，500 行就成了 25 次往返。
var capped = await orm.InsertRange(bulk).WithBulk(BulkMethod.MultiValue, rowsPerRoundTrip: 20).ExecuteBulkAsync();

Console.WriteLine($"压到每块 20 行：{capped.RoundTrips} 次往返、每块 {capped.ChunkSize} 行");

// 两次真执行 × 500 行进了库，拒掉的那两次一行都没进（拒都在动手之前）。
Console.WriteLine($"库里读回 {await orm.Select<Order>().Where(o => o.Note == "bulk-layer").CountAsync()} 行 Note=bulk-layer");

Console.WriteLine();

// ── 12. 分表（SplitTables / CreateSplitTables / 一片一句写） ─────────────
Console.WriteLine("═══ 12. 分表 ═══");
Console.WriteLine();

// ① 类型名不是表名：类上 [Table] 写的是一张<em>模板</em>，而模板不是一张能建的表。
//    整库同步遇到它是"跳过并记名"，不是"照着名字建一张真的带花括号的表"。
_ = orm.Select<AccessLog>();
_ = orm.Select<TicketRow>();

var splitSync = await orm.SyncStructureAsync();

var template = orm.Schemas.Contract<AccessLog>().Table;

Console.WriteLine($"模板名：{template.Name} ∶ 分片键 {template.Split!.MemberName}（{template.Split!.Kind}）");
Console.WriteLine($"整库同步把这两张跳过了：{string.Join("、", splitSync.SkippedSplitTemplates)}（新建 {splitSync.CreatedTables.Count} 张）");

// ② 建片要一个范围，而"该有哪几段"只有业务知道：日档实体上默认建全部就是凭空造几千张表。
var shards = await orm.CreateSplitTablesAsync<AccessLog>(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));

Console.WriteLine($"三个月的片：{string.Join("、", shards.Requested)}（本次新建 {shards.Created.Count} 张、已在 {shards.AlreadyThere.Count} 张）");
Console.WriteLine($"  片上没有索引：{string.Join("、", shards.IndexesNotCreated)} 一条都没随片建（索引名在同一模式的 N 张片上会撞，规格 §16.4 已裁）");

// 幂等：同一个范围再跑一次，一张都不建 —— 逐张先问存在再发 DDL，而不是撞一条"表已存在"。
var again = await orm.CreateSplitTablesAsync<AccessLog>(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));

Console.WriteLine($"  同一范围第二趟：新建 {again.Created.Count} 张、已在 {again.AlreadyThere.Count} 张");

// ③ 写侧：一片一句。跨三月的六行不是"一条 INSERT 六个 VALUES"，是三句各两个 VALUES。
var logs = new[]
{
    new AccessLog { Id = Guid.NewGuid(), Path = "/a", Status = 200, OccurredAt = new DateTime(2026, 1, 12, 9, 0, 0) },
    new AccessLog { Id = Guid.NewGuid(), Path = "/b", Status = 500, OccurredAt = new DateTime(2026, 1, 28, 9, 0, 0) },
    new AccessLog { Id = Guid.NewGuid(), Path = "/c", Status = 200, OccurredAt = new DateTime(2026, 2, 3, 9, 0, 0) },
    new AccessLog { Id = Guid.NewGuid(), Path = "/d", Status = 404, OccurredAt = new DateTime(2026, 2, 20, 9, 0, 0) },
    new AccessLog { Id = Guid.NewGuid(), Path = "/e", Status = 200, OccurredAt = new DateTime(2026, 3, 5, 9, 0, 0) },
    new AccessLog { Id = Guid.NewGuid(), Path = "/f", Status = 503, OccurredAt = new DateTime(2026, 3, 31, 9, 0, 0) },
};

var insertPlan = orm.InsertRange(logs).Explain();
var written = await orm.InsertRange(logs).ExecuteAffrowsAsync();

Console.WriteLine($"六行 × 三个目标表：{written} 行受影响，{insertPlan.Chunks.Count} 句语句");

foreach (var chunk in insertPlan.Chunks)
    Console.WriteLine($"  → {chunk.Ast.Table.Table.Name}（{chunk.RowCount} 行，入参下标 {string.Join("、", chunk.RowOrdinals!)}）：{chunk.Sql.Text}");

// ④ 读侧：选片范围就是"打哪几张表"。合并读是一句 UNION ALL，分页长在外层而不是每片里。
Console.WriteLine($"全范围读的语句：{orm.Select<AccessLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31)).ToSql()}");

var whole = await orm.Select<AccessLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31)).ToListAsync();

Console.WriteLine($"  三张片合并读回 {whole.Count} 行");

var february = await orm.Select<AccessLog>().SplitTables(new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)).ToListAsync();

Console.WriteLine($"剪枝到二月：{february.Count} 行 —— {string.Join("、", february.Select(o => o.Path))}（一月与三月的行不在这张表里，所以根本不会被问）");

var page = await orm.Select<AccessLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31))
    .OrderBy(o => o.OccurredAt).ToPagedAsync(1, 2, new PagedOptions(CountMethod.SkipTotal));

Console.WriteLine($"跨片翻页第一页：{page.Items.Count} 行、Total={page.Total?.ToString() ?? "(不算总数)"}、HasNext={page.HasNext}");

// ⑤ 跨片聚合是<em>明确拒绝</em>，不是静默按片分组再合并（那是另一件事，规格还没裁）。
try
{
    await orm.Select<AccessLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31)).CountAsync();
}
catch (QuickSqlException ex)
{
    Console.WriteLine($"  跨 3 片数总数被拒：{ex.Message[..Math.Min(48, ex.Message.Length)]}…");
}

// ⑥ 改与删必须钉住一片，而且钉法放在链首（SET 与 WHERE 的列引用是翻译时绑到目标表上的）。
var pinned = orm.Update<AccessLog>().SplitTable(new DateTime(2026, 2, 9)).Set(o => o.Status, 299).Where(o => o.Status == 404);

Console.WriteLine($"钉住二月那一片的改：{pinned.ToSql()}");
Console.WriteLine($"  改到 {await pinned.ExecuteAffrowsAsync()} 行，二月那片现在读回 {string.Join("、", (await orm.Select<AccessLog>().SplitTables(new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)).ToListAsync()).Select(o => o.Status))}");

// ⑦ 哈希档：桶号是<em>算出来的</em>，所以写与读都必须走同一颗路由；整数键不经 GetHashCode()。
var buckets = await orm.CreateSplitTablesAsync<TicketRow>([0, 1, 2, 3, 3]); // 重复桶号去掉：这一句要的是四张

Console.WriteLine($"四个桶（第五个实参是重复的）：{string.Join("、", buckets.Requested)}");

var tickets = Enumerable.Range(1, 8).Select(i => new TicketRow { Id = Guid.NewGuid(), TenantId = i, Title = $"T{i}" }).ToArray();
var spread = orm.InsertRange(tickets).Explain();
var spreadRows = await orm.InsertRange(tickets).ExecuteAffrowsAsync();

Console.WriteLine($"八个租户 × 四个桶：{spread.Chunks.Count} 句语句、{spreadRows} 行受影响，各片行数 = {string.Join("、", spread.Chunks.Select(c => $"{c.Ast.Table.Table.Name}={c.RowCount}"))}");

var inBucket1 = await orm.Select<TicketRow>().SplitTables(1).ToListAsync();

Console.WriteLine($"  桶 1 里是租户 {string.Join("、", inBucket1.Select(t => t.TenantId))}（1 与 5 同桶，因为 5 mod 4 = 1）");

foreach (var bucket in new[] { 0, 1, 2, 3 })
    Console.WriteLine($"  逐桶读：SplitTables({bucket}) → {string.Join("、", (await orm.Select<TicketRow>().SplitTables(bucket).ToListAsync()).Select(t => t.TenantId))}");

// ── 13. 读写分离与分库（[DataSource] / Replicas / Clusters） ──────────────
Console.WriteLine("═══ 13. 读写分离与分库 ═══");
Console.WriteLine();

// §3 起那只 Scoped 会话一直活到这里（真实应用里请求一结束就收），而会话构造时挂上的那帧「本条流写过哪几座集群」
// 的账本也跟着活着：它写过 main，于是同一条流上 main 的缺省读会被送回主库。
// 本节要的读数是一条没写过 main 的流，所以先把它收掉 —— 这一格本身就是用法：<b>写后读那只帧长在会话上</b>，
// 裸链（orm.Select / orm.Insert）没有会话可问，所以它不知道刚才那条流放进了哪座库。
// 收它用的是 DisposeAsync，而那颗戳子的注销必须留在<em>同步</em>那一段：async 方法里换 AsyncLocal 的值
// 写不回调用方的帧（哪怕排在第一个 await 之前也一样），戳子就会一直挂在调用方这条流上 —— 本节第一跑
// 正是那个形状，读起来像读写分离没生效，于是把源码的 DisposeAsync 拆成"同步注销 + 异步关连接"两段。
await db.DisposeAsync();

// ① 三座库、一个实例：main 的主库与它的一颗从库，加上 report 那座集群的主库。三颗都是文件。
//    从库在这里是<em>另一颗文件</em>而不是一份真复制过来的副本 —— 本机没有配了复制的实例，
//    所以这一格能证的是"缺省读真的换了一条连接、落到另一座库"，它<em>不</em>证复制延迟。
var routedMainFile = "quicksql-sample-routed-main.db";
var replicaFile = "quicksql-sample-routed-replica.db";
var reportFile = "quicksql-sample-routed-report.db";

foreach (var file in new[] { routedMainFile, replicaFile, reportFile })
    if (File.Exists(file)) File.Delete(file);

// 路由的读数是订阅出来的：Executed 手上一定有那句实际走的那条连接（CommandLogEvent.Route 是必填的一格），
// 所以"这一句落在哪座库、哪个角色、按哪一档"是读得出的事实，不是靠翻库猜出来的。
var routes = new List<string>();
var routedAop = new QuickSqlAop();
routedAop.Executed = e => routes.Add($"{e.OperationKind} → {e.Route.Cluster}/{(e.Route.IsPrimary ? "主" : "从")}（{e.Route.Reason}）");

var routed = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = orm.Provider,
    ConnectionString = $"Data Source={routedMainFile}",
    Replicas = [$"Data Source={replicaFile}"],
    Clusters = [new ClusterConfig("report", $"Data Source={reportFile}")],
    Aop = routedAop,
    SchemaSync = SchemaSyncPolicy.Execute,
    ForceSchemaSync = true,
});

// ② 声明在契约上读得回来；没点名的实体落在缺省的 main，与这一格落地之前逐字同形。
Console.WriteLine($"SalesTotal 住在集群 {routed.Schemas.Contract<SalesTotal>().Table.Source!.Name} ∶ Customer 住在 {DataSourceRouting.ClusterOf(routed.Schemas.Contract<Customer>().Table)}");
Console.WriteLine($"  在册的集群：{string.Join("、", routed.Sources.ClusterNames)}（main 之外只有点名那一座；按 Ordinal 排过）");

// ③ 一次建表，DDL 按集群分组：两张表落在两座库里。从库那座<em>另建一遍</em>——一座真的从库有的是
//    同样的表、旧一份的内容，所以这里给它表而不给它行，那才是"落后一个复制窗口"的形状。
_ = routed.Select<Customer>();
_ = routed.Select<SalesTotal>();

var replicaSeed = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = orm.Provider,
    ConnectionString = $"Data Source={replicaFile}",
    SchemaSync = SchemaSyncPolicy.Execute,
    ForceSchemaSync = true,
});

_ = replicaSeed.Select<Customer>();

var routedSync = await routed.SyncStructureAsync();
_ = await replicaSeed.SyncStructureAsync();

Console.WriteLine($"一次 SyncStructure 建出：{string.Join("、", routedSync.CreatedTables)}");
Console.WriteLine($"  Customers：main 有 {HasTable(routedMainFile, "Customers")} ∶ report 有 {HasTable(reportFile, "Customers")}");
Console.WriteLine($"  SalesTotals：report 有 {HasTable(reportFile, "SalesTotals")} ∶ main 有 {HasTable(routedMainFile, "SalesTotals")}");
Console.WriteLine($"  从库那座：表在 {HasTable(replicaFile, "Customers")}（有表而没有下面要写的那一行，才是「落后」而不是「没建」）");

// ④ 写落主库、缺省读落从库：同一句读、同一个实例，差别只在那条连接。
var chainName = "读写分离-" + Guid.NewGuid().ToString("N")[..6];

await routed.Insert(new Customer { Name = chainName, City = "从库本来就没有" }).ExecuteAffrowsAsync();
Console.WriteLine($"  写那一句：{routes[^1]}");

var onReplica = (await routed.Select<Customer>().Where(c => c.Name == chainName).ToListAsync()).Count;
Console.WriteLine($"  缺省读落到从库：{onReplica} 行 ∶ {routes[^1]}");

var onPrimary = (await routed.Select<Customer>().Where(c => c.Name == chainName).FromPrimary().ToListAsync()).Count;
Console.WriteLine($"  同一句点明 FromPrimary()：{onPrimary} 行 ∶ {routes[^1]}");
Console.WriteLine("  链式这一侧写过之后缺省读仍去从库：那张「写过」的帧长在会话（DbContext）上，裸链没有会话可问");

// ⑤ 会话里就是另一回事：本座集群写过之后，缺省读被送回主库，而那一份正是自己刚写的。
var sessionName = "写后读-" + Guid.NewGuid().ToString("N")[..6];

using (var session = new DbContext(routed))
{
    session.Set<Customer>().Add(new Customer { Name = sessionName, City = "自己写的" });
    _ = session.SaveChanges();

    var own = session.Set<Customer>().Where(c => c.Name == sessionName).ToList().Count;
    Console.WriteLine($"  会话里刚写过，缺省读回的是自己那一份：{own} 行 ∶ {routes[^1]}");

    var stale = session.Set<Customer>().Where(c => c.Name == sessionName).FromReplica().ToList().Count;
    Console.WriteLine($"  同一句点明 FromReplica() 就仍然去从库、读到旧的那一份：{stale} 行 ∶ {routes[^1]}");
    Console.WriteLine("  显式表态压过写后读：库不替你兜那份「新读旧」，这一格要由调用方自己负责");
}

// ⑥ report 那座没配从库，所以缺省读落它自己的主库。写过之后同一座库、同一个理由 ——
//    因为「本条流刚写过」那格只有会话才记账（④ 末尾那句），裸链插完之后缺省读仍按「这座不分读写」落主库。
//    落处没变，所以这一格真正要说的是 Reason 读的是<em>决策</em>不是<em>落处</em>：同一座主库能由四种理由抵达。
var gmvBefore = (await routed.Select<SalesTotal>().ToListAsync()).Count;
Console.WriteLine($"  report 没配从库，缺省读落它自己的主库：{gmvBefore} 行 ∶ {routes[^1]}");

await routed.Insert(new SalesTotal { Metric = "GMV", Value = 128m }).ExecuteAffrowsAsync();

var gmvAfter = (await routed.Select<SalesTotal>().ToListAsync()).Count;
Console.WriteLine($"  写过之后落处与理由都不变（裸链不记写后读）：{gmvAfter} 行 ∶ {routes[^1]}");

// ⑦ 四格拒绝，每一格都在动手之前，而报错给的是走得通的那条路。
try
{
    using var ambient = routed.BeginAmbientTransaction();
    await routed.Select<Customer>().FromReplica().ToListAsync();
}
catch (QuickSqlException ex)
{
    Console.WriteLine($"  事务里点 FromReplica()：{Head(ex.Message)}");
}

try
{
    _ = routed.Select<Customer>().InnerJoin<SalesTotal>((c, s) => c.Id == s.Id).ToSql();
}
catch (QuickSqlException ex)
{
    Console.WriteLine($"  跨集群 JOIN：{Head(ex.Message)}");
}

try
{
    using var mixed = new DbContext(routed);

    mixed.Set<Customer>().Add(new Customer { Name = "混批", City = "要拒" });
    mixed.Set<SalesTotal>().Add(new SalesTotal { Metric = "混批", Value = 1m });
    _ = mixed.SaveChanges();
}
catch (QuickSqlException ex)
{
    Console.WriteLine($"  一批跨两座集群：{Head(ex.Message)}");
}

// ⑧ 脚本模式不连库，而一座集群一段：那几行段头是给 DBA 的"这一段要落在哪座库的主库上"。
var scriptOrm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = orm.Provider,
    ConnectionString = $"Data Source={routedMainFile}",
    Clusters = [new ClusterConfig("report", $"Data Source={reportFile}")],
    SchemaSync = SchemaSyncPolicy.Script,
});

_ = scriptOrm.Select<Customer>();
_ = scriptOrm.Select<SalesTotal>();

var ddl = (await scriptOrm.SyncStructureAsync()).Ddl;

Console.WriteLine("  脚本模式分段（只打段头与建表那一句的头）：");

foreach (var line in ddl.Replace("\r\n", "\n").Split('\n'))
    if (line.StartsWith("-- 集群") || line.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine("    " + Head(line.Trim(), 96));

// ⑨ 这一档今天没有的三格，写在这里而不是等使用者撞：
//    · 原生 SQL 那一族（orm.Ado 的字符串口）只认 main 那座集群——句子里没有"哪座库"这一颗可给；
//      而它在 main 配了从库时，一句<em>读</em>照样按缺省档落到从库上去（判据与链式同一条）。
//    · 从库按声明顺序轮询：没有权重、不做延迟探测、也不问哪颗活着。
//    · 没有"库里现有哪些库、哪些片"的反查口：在册清单读得出（上面那句 ClusterNames），
//      库里的现状读不出 —— 所以建片仍然要业务给范围，分库仍然要启动期给全清单。

// ── 14. 库里多出来的那条索引：另一颗开关（SQL Server；没给连接串就整段跳过） ─
Console.WriteLine();
Console.WriteLine("═══ 14. SchemaSyncDropIndexes：库里多出来的那条索引（SQL Server） ═══");
Console.WriteLine();

// 为什么这一段要换一家库：§2 那一行末尾的「删索引」在 SQLite 上恒为 0，那不是开关没生效，
// 而是那一家的读侧交不回"库里现有哪些索引"（交回 null 是判不了，不是没有），计划层比不出"多出一条"这件事。
// SQL Server 的索引清单来自 sys.indexes，所以那一档在这里才第一次真跑。
// 连接串两种给法，两处都不打印也不入库：环境变量 QUICKSQL_SAMPLE_MSSQL，或仓库根那份本机凭据文件的首行非 # 内容。
var mssql = MssqlConnectionString();

if (mssql is null)
{
    Console.WriteLine("  跳过：这一格故意没有默认库——它要发 DDL，打在哪个实例上得人给。");
    Console.WriteLine("        两种给法（两处都不打印、也不入库）：环境变量 QUICKSQL_SAMPLE_MSSQL，" +
                      "或仓库根那份本机凭据文件 quicksql-test.connection.local.txt 的首行非 # 内容。");
}
else
{
    // 组装根自己造 provider（§12.7 F：内核不做 Type.GetType(string)），且走清单里 "sqlserver" 那一行的
    // 运行期解析，而不是直接 new SqlClientFactory()——绕开清单就等于绕开这里要示范的那条链。
    var mssqlProvider = new SqlServerProvider(new DriverRegistry().Factory(SqlServerProvider.DriverKey));

    // 同一份输入开成两台，而不是中途改一颗开关：那些选项是 init-only，用法上就是"启动期定死"。
    var asUsual = MssqlOrm(mssqlProvider, mssql, false);
    var optedIn = MssqlOrm(mssqlProvider, mssql, true);

    // 契约是惰性登记的：实体第一次被碰到才进在册清单，而整库同步只认在册的那几张。
    _ = asUsual.Select<SyncProbe>();
    _ = optedIn.Select<SyncProbe>();

    // ① 旧形状是<em>裸 T-SQL 摆出来的</em>，不是让库自己建出来的 —— 这一格的裁判是"库里多出来"这件事，
    //    而那件事只有靠手工摆才能摆出来。三处对不上契约（Tag 短一截、Amount 的整数位少、两句注释都没有），
    //    另外库里多出契约不认识的三样：一列 Legacy、一条建在它上面的索引、一条唯一约束。
    await asUsual.Ado.ExecuteBatchAsync(
    [
        "DROP TABLE IF EXISTS [QuickSqlSampleProbe]",
        """
        CREATE TABLE [QuickSqlSampleProbe] (
            [Id] int NOT NULL IDENTITY(1,1) PRIMARY KEY,
            [Tag] nvarchar(16) NULL,
            [Amount] decimal(10, 2) NOT NULL,
            [Legacy] nvarchar(32) NULL
        )
        """,
        "CREATE INDEX [IX_QuickSqlSampleProbe_Legacy] ON [QuickSqlSampleProbe] ([Legacy])",
        "ALTER TABLE [QuickSqlSampleProbe] ADD CONSTRAINT [UQ_QuickSqlSampleProbe_Legacy] UNIQUE ([Legacy])",
        "INSERT INTO [QuickSqlSampleProbe] ([Tag], [Amount], [Legacy]) VALUES (N'留着', 1.25, N'旧数据')",
    ]);

    const string Extra = "IX_QuickSqlSampleProbe_Legacy";
    const string Constraint = "UQ_QuickSqlSampleProbe_Legacy";
    const string Declared = "IX_QuickSqlSampleProbe_Tag";

    // ② 默认那一档：一条索引都不 DROP，只把它指名进"要人工"，而那句话里带着出路。
    var firstPass = await asUsual.SyncStructureAsync();

    Console.WriteLine($"  默认配置（{SyncCounts(firstPass)}）∶ 要人工 {firstPass.Refused.Count} 条");

    foreach (var message in firstPass.Refused)
        Console.WriteLine($"    {message}");

    Console.WriteLine($"    目录里那条还在吗：{(await IndexExists(asUsual, Extra) ? "在" : "不在")}" +
                      $" ∶ 契约上那条建出来了吗：{(await IndexExists(asUsual, Declared) ? "在" : "不在")}");

    // ③ 开了那一档：同一张表、同一份契约，只多那一颗开关。于是那条索引真的从目录里消失了，
    //    而<em>唯一约束不跟着消失</em>（T-SQL 上 DROP INDEX 删不动约束，要拆得走 ALTER TABLE … DROP CONSTRAINT），
    //    Legacy 那一列与它的值也一根手指都没碰——这一格顺带证的是"删索引不顺手把列带走"。
    var dropPass = await optedIn.SyncStructureAsync();

    Console.WriteLine($"  开 SchemaSyncDropIndexes（{SyncCounts(dropPass)}）∶ 删掉的 ∶ {string.Join("、", dropPass.DroppedIndexes)}");
    Console.WriteLine($"    目录里：多出的那条{(await IndexExists(optedIn, Extra) ? "还在" : "没了")}" +
                      $" ∶ 契约上那条{(await IndexExists(optedIn, Declared) ? "还在" : "没了")}" +
                      $" ∶ 唯一约束{(await IndexExists(optedIn, Constraint) ? "还在（开关也删不动它）" : "没了")}");

    var surviving = await optedIn.Ado.ExecuteScalarAsync(
        "SELECT COUNT(*) FROM [QuickSqlSampleProbe] WHERE [Legacy] = @value", new { value = "旧数据" });

    Console.WriteLine($"    那一列与它的值：{surviving} 行 Legacy=旧数据 还在 ∶ 要人工从 {firstPass.Refused.Count} 条剩 {dropPass.Refused.Count} 条（列与约束那两条永久留着）");

    // ④ 同一颗开关下第二趟零动作——"库里多出一条索引"这件事已经不存在了。
    //    这一格钉的是下一版不会在每次同步里把同一颗索引删了又建、建了又删。
    var thirdPass = await optedIn.SyncStructureAsync();

    Console.WriteLine($"  同一档第二趟：{(thirdPass.IsEmpty ? "0 个动作" : $"仍有 {thirdPass.DroppedIndexes.Count} 个删索引动作")}");

    // 探针表用完就收：这一段会在真实例上留一张表，不收是示例的锅。
    await optedIn.Ado.ExecuteNonQueryAsync("DROP TABLE IF EXISTS [QuickSqlSampleProbe]");
}

Console.WriteLine();

// ── 15. 软删（orm.Behaviors.SoftDelete：DELETE 换成置位的 UPDATE） ────────
Console.WriteLine("═══ 15. 软删改写 ═══");
Console.WriteLine();

// 册子挂在 orm 实例上，所以这里建两只实例打同一颗文件库：一只没注册、一只注册了。
// 需要这个对照的理由是这一档的判据不是"少了行"，而是"库里那一格被写了什么"，而它按实体成立 ——
// 只接一处出口就会留下"同一张表走 A 留住行、走 B 丢行"那个洞，所以下面链式与批量两处都注册在同一次。
var plain = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = orm.Provider,
    ConnectionString = $"Data Source={dbFile}",
});

var soft = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = orm.Provider,
    ConnectionString = $"Data Source={dbFile}",
    SchemaSync = SchemaSyncPolicy.Execute,
    ForceSchemaSync = true,
});

soft.Behaviors.SoftDelete<SampleTicket>(t => t.Deleted, t => t.DeletedAt);

_ = soft.Select<SampleTicket>();
_ = await soft.SyncStructureAsync();

// 种子走 plain：那一只没有行为，所以这四行是真 INSERT，而后面每一格读数都有对照。
await plain.InsertRange(new[]
{
    new SampleTicket { Seat = "A1" },
    new SampleTicket { Seat = "A2" },
    new SampleTicket { Seat = "B1" },
    new SampleTicket { Seat = "B2" },
}).ExecuteAffrowsAsync();

// ① 没注册的那一家：Sql 就是 DELETE，那句自陈说的也是这件事。
var physical = plain.Delete<SampleTicket>().Where(t => t.Seat == "A1").Explain();

Console.WriteLine($"没注册：{physical.Sql.Text.Split(' ')[0]} ∶ {physical.Reason}");

// ② 注册过的那一家：同一条链、同一个 Where，换的是形状而不是范围——WHERE 一个字没动。
var rewritten = soft.Delete<SampleTicket>().Where(t => t.Seat == "A1").Explain();

Console.WriteLine($"注册过：{rewritten.Sql.Text.Split(' ')[0]} ∶ {rewritten.Reason}");
Console.WriteLine($"  整句：{rewritten.Sql.Text}");
Console.WriteLine($"  Ast 那一棵仍是 {rewritten.Ast.GetType().Name}（调用方要的是删除），Sql 才是库里执行的那句");

// ③ 真跑一次，再跑一次：同一条谓词第二次仍然命中 1 行。
//    这一格钉的是"这一档刻意没往 WHERE 加『且尚未置位』"——加上去就替"0 行受影响"开出第三种成因，
//    而那种成因要等一条裁决（被过滤器挡住的行算不算一次并发冲突），这里不自决。
var firstStrike = await soft.Delete<SampleTicket>().Where(t => t.Seat == "A1").ExecuteAffrowsAsync();
var secondStrike = await soft.Delete<SampleTicket>().Where(t => t.Seat == "A1").ExecuteAffrowsAsync();

Console.WriteLine($"同一句连跑两次：影响 {firstStrike} 行、又 {secondStrike} 行（重复删除不是 0 行，因为 WHERE 没加「且尚未置位」）");

// ④ 批量那条出口：两行键变成一句置位的 UPDATE，而整批共用一颗时钟。
var toArchive = await plain.Select<SampleTicket>()
    .Where(t => t.Seat == "B1" || t.Seat == "B2")
    .ToListAsync();

var logged = new List<string>();
soft.Aop.Executed = e => logged.Add($"{e.OperationKind} ∶ {e.Sql.Text.Split(' ')[0]}");

var bulkPlan = soft.DeleteRange(toArchive).Explain();

Console.WriteLine($"批量那一句：{bulkPlan.Commands[0].Text}");

var bulkRun = await soft.DeleteRange(toArchive).ExecuteBulkAsync();

Console.WriteLine($"  执行：{bulkRun.RowCount} 行 / {bulkRun.RoundTrips} 次往返 ∶ 日志里是 {string.Join("、", logged)}");

// ⑤ 裁判走裸连接问库，不走被测的这条链：行一行没少，只是那一格被写了。
var facts = TicketFacts(dbFile);

Console.WriteLine($"库里：{facts.Total} 行（没少）∶ 置位 {facts.Flagged} 行 ∶ 批量那两行上有 {facts.BulkStamps} 个时刻（一次构建一颗）");

// ⑥ 行为只管"删的那一句长什么样"，不管"读到的集合里有没有已删的行"——后者是 orm.Filters 的活。
//     两只对象各装一半，这里先只装行为，所以四行全都还看得见。
Console.WriteLine($"只注册行为时读侧：{await soft.Select<SampleTicket>().CountAsync()} 行可见（库里 {facts.Total} 行都在）");

soft.Filters.Add<SampleTicket>(t => !t.Deleted);

Console.WriteLine($"再注册过滤器之后：{await soft.Select<SampleTicket>().CountAsync()} 行可见，而库里仍是 {facts.Total} 行——过滤器不是删除，它自己不动任何一行");

// 这一档今天演示链式与批量两处出口。第三处（SaveChanges 里被摘掉的行）在这里证不了：本节用的会话
// 绑的是 DI 那只 orm，在它身上注册行为会回头改前面 §3–§5 那些读数。那一处由真库那册钉，
// 判据是"报告里 Operations[].Columns 交出 SET 的那两列"，见 tests/QuickSql.Tests.Integration/SoftDeletePathTests.cs。

// ── 16. 导航读取：自动联表（§5.2）∶ Include 链（§5.6）∶ IncludeMany 链（§5.7）──
Console.WriteLine("═══ 16. 导航读取：自动联表与急切加载 ═══");
Console.WriteLine();

// 这一节自己播种，读数才钉得住：§7 那五笔之外，§11 还往 cA 名下塞了一千笔批量订单，
// 所以下面每一条链都带一个 Note 谓词把自己圈在示范的那几行里。
// 那笔"客户不存在"的订单（CustomerId 指着一张没有的行）是 LEFT 那一格的证人：谓词读不到它，
// 而投影与 Include 必须带着它 —— 换 INNER 就少这一行，而少行在投影结果里读不出形状。
_ = orm.Select<Parcel>();

var layerSync = await orm.SyncStructureAsync();

Console.WriteLine($"第三层那张表：本次新建 {string.Join("、", layerSync.CreatedTables)}（{layerSync.CreatedTables.Count} 张）");

var navCustomer = new Customer { Name = "导航示范", City = "苏州" };
navCustomer.Id = (int)await orm.Insert(navCustomer).ExecuteIdentityAsync();

var navOrder = new Order { CustomerId = navCustomer.Id, Amount = 777m, CreatedAt = new DateTime(2026, 4, 1), Note = "nav-demo" };
navOrder.Id = (int)await orm.Insert(navOrder).ExecuteIdentityAsync();

var orphanOrder = new Order { CustomerId = 0, Amount = 888m, CreatedAt = new DateTime(2026, 4, 2), Note = "nav-demo" };
orphanOrder.Id = (int)await orm.Insert(orphanOrder).ExecuteIdentityAsync();

var navItem = new OrderItem { OrderId = navOrder.Id, Sku = "NAV-A", Quantity = 2, Price = 388m };
navItem.Id = (int)await orm.Insert(navItem).ExecuteIdentityAsync();

var orphanItem = new OrderItem { OrderId = orphanOrder.Id, Sku = "NAV-B", Quantity = 1, Price = 888m };
orphanItem.Id = (int)await orm.Insert(orphanItem).ExecuteIdentityAsync();

await orm.InsertRange(
[
    new Parcel { OrderItemId = navItem.Id, TrackingNo = "TRK-A1" },
    new Parcel { OrderItemId = navItem.Id, TrackingNo = "TRK-A2" },
    new Parcel { OrderItemId = orphanItem.Id, TrackingNo = "TRK-B1" },
]).ExecuteAffrowsAsync();

// 语句计数走观察位，不走"链自己说发了几条"：下面每一格数的是库里实际跑掉的句子。
var said = new List<string>();
orm.Aop.Executed = e => said.Add(e.Sql.Text);

// ── 16.1 谓词里读一根 to-one 边的某一格：库按契约里那根边自己补 LEFT JOIN（§5.2）。
//        调用方既不写 Include 也不写 join —— 这一格翻掉了旧册子上"不会为导航属性自动联表"那一句。
var inSuzhou = orm.Select<Order>().Where(o => o.Note == "nav-demo" && o.Customer!.City == "苏州");

Console.WriteLine($"谓词读导航发的那句：{inSuzhou.ToLogSql()}");
Console.WriteLine($"  LEFT JOIN {CountOf(inSuzhou.ToLogSql(), "LEFT JOIN")} 张 ∶ 读回 {await inSuzhou.CountAsync()} 笔（没有客户那一笔进不来，它那一格是 NULL）");

// ── 16.2 排序键与投影读同一根边：JOIN 还是那一条，而 ORDER BY 指的是联进来的那张表位。
var byCity = orm.Select<Order>()
    .Where(o => o.Note == "nav-demo")
    .OrderBy(o => o.Customer!.City)
    .ThenBy(o => o.Id)
    .Select(o => new { o.Id, City = o.Customer!.City });

Console.WriteLine($"序 + 投影读同一根边：{byCity.ToLogSql()}");

foreach (var row in await byCity.ToListAsync())
    Console.WriteLine($"  订单 {row.Id}：城市 {(row.City ?? "（空：LEFT 留住的那一笔）")}");

// ── 16.3 Include 之后再读同一根边：一张 JOIN，不是两张。
//        收养落成的是<em>模型里那条真 JOIN</em>，不是照抄 Include 那一条 —— Include 的那一条只在
//        "实体投影且没拆查"时才发，照抄过去，投影、AsSplitQuery、count 自己包的那层内行都会把它从 FROM 里摘掉，
//        而欠着它的谓词还在（那是一句引用了不存在表位的话，不是能靠改名救回来的别名冲突）。
var shared = orm.Select<Order>().Include(o => o.Customer).Where(o => o.Note == "nav-demo" && o.Customer!.City == "苏州");

var sharedSql = shared.ToLogSql();

Console.WriteLine($"Include + 读同一条边：LEFT JOIN {CountOf(sharedSql, "LEFT JOIN")} 张 ∶ {sharedSql}");
Console.WriteLine($"  模型里也数得到同一条：{CountOf(shared.Explain().ModelText, "JOIN Left")} 条（进的是模型的 Joins，不是编译期临时补的）");

foreach (var row in await shared.ToListAsync())
    Console.WriteLine($"  缝好的整只客户：{row.Customer?.Name}／{row.Customer?.City}（同一句 SELECT 里带回来，不必再发第二条）");

// ── 16.4 集合导航没有这一档，而且当场拒：一根 IEnumerable 边联进 FROM 会把主表行按明细行数复制，
//        那是页码、行数、聚合三处一起改语义的事，所以消息把两档分开说。
try
{
    _ = orm.Select<Order>().Select(o => new { o.Id, Rows = o.Items!.Count }).ToSql();
}
catch (QuickSqlException ex)
{
    Console.WriteLine($"集合读法仍然被拒：{Head(ex.Message, 150)}");
}

// ── 16.5 Include 链：一层一张 JOIN，第二层的宿主是第一层那一颗表位（§5.6）。
var stacked = orm.Select<OrderItem>()
    .Include(i => i.Order!)
    .ThenInclude(o => o.Customer)
    .Where(i => i.Sku == "NAV-A");

var stackedSql = stacked.ToLogSql();

Console.WriteLine($"Include 两层：LEFT JOIN {CountOf(stackedSql, "LEFT JOIN")} 张 ∶ {stackedSql}");

foreach (var row in await stacked.ToListAsync())
    Console.WriteLine($"  明细 {row.Sku} × {row.Quantity} → 订单 {row.Order?.Id} → 客户 {row.Order?.Customer?.City}");

// ── 16.6 同一根边换 AsSplitQuery：主查询不发扩展列，整只客户是主查询之后那一条 SELECT 的事。
//        谓词那一条 JOIN 照发 —— 它来自模型的 Joins，与拆不拆查无关（16.3 的收养为什么落成一条真 JOIN 就在这里读得出来）。
var apart = orm.Select<Order>().Include(o => o.Customer).AsSplitQuery().Where(o => o.Note == "nav-demo" && o.Customer!.City == "苏州");

var apartSql = apart.ToLogSql();

said.Clear();

var apartRows = await apart.ToListAsync();

Console.WriteLine($"拆查：主查询 {apartSql}");
Console.WriteLine($"  投影清单里不含客户那几列：{CountOf(apartSql[..apartSql.IndexOf("FROM", StringComparison.Ordinal)], "\"t1\"")} 次 ∶ 实际端出 {said.Count} 条 SELECT ∶ 缝回 {apartRows.Count} 笔，客户 {apartRows[0].Customer?.City}");

// ── 16.7 IncludeMany 那一家：主查询一个字符不变，往下每一层各发一条 SELECT，旁边每根引用导航再一条（§5.7）。
//        这一条链把三档挂在一起：集合一层、集合旁边那根引用边、再往下一层集合。
var many = orm.Select<Order>()
    .IncludeMany(o => o.Items!)
    .ThenInclude(i => i.Order!)
    .ThenIncludeMany(i => i.Parcels!)
    .Where(o => o.Note == "nav-demo");

var manySql = many.ToLogSql();

Console.WriteLine($"集合链端出去的主查询：{manySql}");
Console.WriteLine($"  明细与包裹都不在这一句里：{CountOf(manySql, "OrderItems")} 次、{CountOf(manySql, "Parcels")} 次");

said.Clear();

var manyRows = await many.ToListAsync();

Console.WriteLine($"  一句链发出 {said.Count} 条 SELECT：根一条、往下每层一条、旁边那根引用边再一条");

foreach (var line in said.Skip(1))
    Console.WriteLine($"    子查询：{WherePart(line)}");

foreach (var row in manyRows)
{
    Console.WriteLine($"  订单 {row.Id}：{row.Items!.Count} 行明细");

    foreach (var line in row.Items!)
        Console.WriteLine($"    明细 {line.Sku}：包裹 {string.Join("、", line.Parcels?.Select(p => p.TrackingNo) ?? [])} ∶ 缝回来的父订单 Note={line.Order?.Note}");
}

// ── 17. 一句 upsert：InsertOrUpdate（跑真 SQLite，命中改 ∶ 缺键插两支各验一次）────────
//
// orm.InsertOrUpdate<T>().SetSource(...) 按主键决定"有就改、没有就插"。SQLite 这一家发的是
// INSERT … ON CONFLICT ("Id") DO UPDATE SET 列 = excluded.列（那颗 Bulk 是 null，句尾长在方言上，
// 见使用册 §3.2 那句"接不到句尾槽"）。这里先看句子、再看落库：改的那一支不许多出一行，
// 缺的那一支要真插进来——"没欠语句"与"库里落没落"是两件事，裁判问库不问返回值。
Console.WriteLine();
Console.WriteLine("── 17. 一句 upsert：InsertOrUpdate ──");

var upsertProbe = new Customer { Name = "upsert 探针", City = "初始城市" };
var probeId = (int)await orm.Insert(upsertProbe).ExecuteIdentityAsync();   // 先落一行，拿到它的主键

// 一批两支：同一颗 Id 改了城市（命中→改），另一颗 Id 库里没有（缺键→插）。ghostId 取一颗不撞序列的显式键。
var existing = new Customer { Id = probeId, Name = "upsert 探针", City = "改成新城市" };
const int ghostId = 987654;
var missing = new Customer { Id = ghostId, Name = "ghost", City = "插进来的" };

Console.WriteLine($"  发出的句子：{Head(orm.InsertOrUpdate<Customer>().SetSource(existing).ToSql(), 130)}");

var upserted = await orm.InsertOrUpdate<Customer>().SetSource([existing, missing]).ExecuteAffrowsAsync();

var afterExisting = await orm.Select<Customer>().Where(c => c.Id == probeId).FirstOrDefaultAsync();
var afterMissing = await orm.Select<Customer>().Where(c => c.Id == ghostId).FirstOrDefaultAsync();
var landedRows = await orm.Select<Customer>().Where(c => c.Id == probeId || c.Id == ghostId).CountAsync();

Console.WriteLine($"  执行受影响行数（各家命中计数口径不同，只当参考）：{upserted}");
Console.WriteLine($"  命中那一支：Id={probeId} 城市={afterExisting?.City}（应=改成新城市）");
Console.WriteLine($"  缺键那一支：Id={ghostId} 城市={afterMissing?.City}（应=插进来的）");
Console.WriteLine($"  两颗键各只一行、合计读回 {landedRows} 行（应=2：证明改的那支没又插一条）");

Console.WriteLine();
Console.WriteLine("═══ 示例完成 ═══");


// ── 附注：多租户每库一串的注册姿势 ──────────────────────────────────────
//
// services.AddQuickSqlOrm(DbKind.SqlServer, sp =>
// {
//     var tenant = sp.GetRequiredService<ICurrentTenant>();
//     return configuration.GetConnectionString(tenant.Name);
// }, builder =>
// {
//     builder.SchemaSync = SchemaSyncPolicy.Execute;
// });
//
// 每个请求的 Scoped QuickSqlOrm 会调一次工厂，按当前租户拿到不同的主库串。
//
// ── 附注：那一颗工厂与 Replicas / Clusters 不同时在场 ────────────────────
//
// builder.Replicas = ["…"]; builder.Clusters = [new ClusterConfig("report", "…")];
//
// 与按请求解析的那颗工厂同时给出时，注册期就抛（不是运行到某一句才发现）：从库与具名集群是<em>启动期常量</em>，
// 而那颗工厂每个请求换一个串。要多租户各配一座从库，就为每一座租户另建一颗 orm，而不是在这一格里塞工厂。
//
// 上面 §13 用的是直给形状（new QuickSqlOrm(new QuickSqlOrmOptions { Replicas = …, Clusters = … })），
// 与 DI 那两格是同一份输入：AddQuickSqlOrm 的 builder 上就是这两颗公开属性。

// ── 18. 排序键是前端那一整串：OrderBy("CustomerId desc, Id") ──────────────
//
// 链上那颗按名字的口收的是运行期才知道的一段文本：逗号分颗，每一颗可以自带方向词 asc / desc。
// 句子对不对归方言册钉，这里只演示三件在示例里读得出来的事：同一串与逐颗接链交回同一串行、
// 那颗 bool 只是"串里没点方向的那些颗"的方向、以及认不出的一颗照样响而不是退成"少一颗 ORDER BY"。
// 行序的比较一律落在 Id / CustomerId 这两颗数值列上（本家的定点列落 TEXT 载体，金额按字典序排）。
Console.WriteLine();
Console.WriteLine("── 18. 按名字排序：一颗口吃下前端那一整串 ──");

const string sorting = "CustomerId desc, Id";   // HTTP 查询串上原样过来的那一形

var byName = await orm.Select<Order>().OrderBy(sorting).ToListAsync();
var byCall = await orm.Select<Order>().OrderByDescending(o => o.CustomerId).ThenBy(o => o.Id).ToListAsync();

Console.WriteLine($"  发出的句子：{Head(orm.Select<Order>().OrderBy(sorting).ToSql(), 130)}");
Console.WriteLine($"  同一串与逐颗接链行序一致：{byName.Select(o => o.Id).SequenceEqual(byCall.Select(o => o.Id))}（应=True）");

// 形参当缺省方向：串里写了 DESC 的那一颗以串为准，没写的那一颗吃形参。
Console.WriteLine($"  Amount 写了 asc 而形参给 true：{Head(orm.Select<Order>().OrderBy("Amount asc", true).ToSql(), 130)}");

// 半串认得出、半串认不出也照样响：排序键错了在页面上只读得出"顺序不对"，所以它必须在这里响。
string refusal;

try
{
    _ = orm.Select<Order>().OrderBy("Id, 不存在的列 desc").ToSql();
    refusal = "（没响——不该出现）";
}
catch (QuickSqlException failure)
{
    refusal = failure.Message;
}

Console.WriteLine($"  认不出那一颗：{Head(refusal, 120)}");

static bool HasTable(string file, string table)
{
    // 问库而不是问被测的那条路由：这一格的裁判要是"路由说它落在 report"，那它说什么都是绿。
    using var connection = new SqliteConnection($"Data Source={file}");

    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
    command.Parameters.AddWithValue("$name", table);

    return Convert.ToInt32(command.ExecuteScalar()) > 0;
}

static string Head(string text, int take = 64) => text.Length <= take ? text : text[..take] + "…";

// §16 的读数尺：一句里某个记号出现几次。这一节的判据是"一根边一张 JOIN"，
// 而 SQL 文本上数得出来的只有计数 —— 模型侧那一份判据由方言册钉，示例只给人看句子。
static int CountOf(string text, string token) => (text.Length - text.Replace(token, string.Empty).Length) / token.Length;

// 集合链那几条子查询要给人看的是 WHERE 那一段（IN 的键清单）：前面那一串列名在几条里长得一样。
static string WherePart(string sql, int take = 72)
{
    var at = sql.IndexOf("WHERE", StringComparison.Ordinal);

    return at < 0 ? "（这一条没有 WHERE 段）" : Head(sql[at..], take);
}

// §15 的裁判：同一句"库里现在是什么形状"由裸连接问，不经那只注册了行为的 orm——
// 被测的那条链说"我置位了"不算证，库里那一格是 1 才算。
static (int Total, int Flagged, int BulkStamps) TicketFacts(string file)
{
    using var connection = new SqliteConnection($"Data Source={file}");

    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText =
        "SELECT COUNT(*), COALESCE(SUM(Deleted), 0), " +
        "(SELECT COUNT(DISTINCT DeletedAt) FROM SampleTickets WHERE Seat IN ('B1', 'B2')) FROM SampleTickets";

    using var reader = command.ExecuteReader();
    reader.Read();

    return (reader.GetInt32(0), Convert.ToInt32(reader.GetValue(1)), reader.GetInt32(2));
}

// ── §14 的那台 SQL Server：连接串、组装、目录那三颗口子 ────────────────────

// 示例自己那一颗环境变量优先，其次复用集成测试那一份本机凭据文件（含密码，不入库）。
// 两处都没有就返回 null，由调用方整段跳过 —— 这一格不退回某个"肯定连得上"的默认实例。
static string? MssqlConnectionString()
{
    var fromEnv = Environment.GetEnvironmentVariable("QUICKSQL_SAMPLE_MSSQL");

    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var file = Path.Combine(dir.FullName, "quicksql-test.connection.local.txt");

        if (!File.Exists(file)) continue;

        var line = File.ReadLines(file).FirstOrDefault(static l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l));

        if (line is not null) return line.Trim();
    }

    return null;
}

// §14 那两台的组装：同一份输入只差那颗 opt-in 的开关。ForceSchemaSync 是因为示例不在 Development
// 环境下跑，而发 DDL 就是那一格要的示范 —— 真实应用里这一颗该由环境给。
static QuickSqlOrm MssqlOrm(IDbProvider provider, string connectionString, bool dropIndexes)
    => new(new QuickSqlOrmOptions
    {
        Provider = provider,
        ConnectionString = connectionString,
        SchemaSync = SchemaSyncPolicy.Execute,
        ForceSchemaSync = true,
        SchemaSyncDropIndexes = dropIndexes,
    });

// 问<em>目录</em>而不是问返回值：「我发过 DROP INDEX」与「那条索引现在不在 sys.indexes 里」是两件事，
// 而 §14 那两格要的正是后者。type IN (1,2) = 簇索引与非簇索引，hypothetical 那种统计用的不算。
static async Task<bool> IndexExists(QuickSqlOrm target, string index)
{
    var found = await target.Ado.ExecuteScalarAsync(
        "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name = @index AND type IN (1, 2)",
        new { table = "QuickSqlSampleProbe", index });

    return Convert.ToInt32(found) > 0;
}

static string SyncCounts(QuickSql.Core.Schema.SchemaSyncResult result)
    => $"补列 {result.AddedColumns.Count} ∶ 改列 {result.AlteredColumns.Count} ∶ 备注 {result.AppliedComments.Count} ∶ 建索引 {result.CreatedIndexes.Count} ∶ 删索引 {result.DroppedIndexes.Count}";
