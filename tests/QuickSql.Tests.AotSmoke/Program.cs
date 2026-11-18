// Native AOT 真发布冒烟闸（§12.7 的实弹验证）：分析器闸量「静态干不干净」，这颗量「AOT 产物里跑不跑得动」。
// 路径口径：G1-only 注册表（零反射元数据）+ 显式 SqliteFactory（不走 DriverRegistry 反射装载），
// 在 scratch SQLite 文件上跑 CRUD / Where（闭包解释档）/ 引用 Include（G1 生成写口）/ IncludeMany（G1 生成集合委托）/ 非生成实体拒绝。
// IncludeMany 的集合缝合走 G1 生成的集合委托（造表/追加/写回都是强类型 lambda，零 MakeGeneric/零表达式树/零
// Expression.New(List<T> ctor)），在本闸里是断言：List<T> 公共写口这一档 AOT 全裁剪下也跑得动（字面 new 裁剪看得见）。
// protected set / 只读 / 数组等其余形状仍退回 MemberAccessors（反射边界），不在本闸靶子形状里，生成器保守不镜像。
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickSql;
using QuickSql.Generated;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Schema;

var dbPath = Path.Combine(Path.GetTempPath(), $"quicksql-aot-smoke-{Guid.NewGuid():N}.db");
var failures = 0;
Console.OutputEncoding = Encoding.UTF8;

void Check(bool ok, string what)
{
    Console.WriteLine($"[aot-smoke] {(ok ? "PASS" : "FAIL")} {what}");
    if (!ok) failures++;
}

try
{
    if (!ProviderRegistry.TryCreate(DbKind.Sqlite, SqliteFactory.Instance, out var provider))
        throw new InvalidOperationException("Sqlite provider 未在 ProviderRegistry 登记（生成器未挂或 provider 未引用）。");

    var orm = new QuickSqlOrm(new QuickSqlOrmOptions
    {
        Provider = provider,
        ConnectionString = $"Data Source={dbPath}",
        SchemaSync = SchemaSyncPolicy.Execute,
        ForceSchemaSync = true,
    }, new G1OnlyEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet));

    // 注册表惰性：先碰一下每个实体，SyncStructure 才枚举得到（与 samples 同一纪律）。
    _ = orm.Select<SmokeCustomer>();
    _ = orm.Select<SmokeOrder>();
    await orm.SyncStructureAsync();

    // ── 插入（自增键回填）──────────────────────────────────────────────
    var customer = new SmokeCustomer { Name = "alice" };
    await orm.Insert(customer).ExecuteIdentityAsync();
    Check(customer.Id > 0, $"Insert 自增键回填 Id={customer.Id}");

    var order = new SmokeOrder { CustomerId = customer.Id, Amount = 42.5m, CreatedAt = DateTime.UtcNow, RowGuid = Guid.NewGuid() };
    await orm.Insert(order).ExecuteIdentityAsync();
    Check(order.Id > 0, $"Insert 明细 Id={order.Id}");

    // ── Where（闭包反射 + 解释编译档）──────────────────────────────────
    var threshold = 10m;
    var found = await orm.Select<SmokeOrder>().Where(o => o.Amount > threshold).ToListAsync();
    Check(found.Count == 1 && found[0].Id == order.Id, "Where 闭包参数查询命中");

    // ── 用户自定义运算符档（AOT 裁剪保留指令的验收，见 QuickSql.Core/ILLink.Descriptors.xml）─────
    // decimal 比较（op_GreaterThan）、decimal 算术（op_Multiply）、DateTime 比较、Guid 相等：
    // 这些运算符 Roslyn 靠反射找，全裁剪会裁掉——保留指令生效则四条都该命中。
    var doubled = await orm.Select<SmokeOrder>().Where(o => o.Amount * 2m > threshold).ToListAsync();
    Check(doubled.Count == 1 && doubled[0].Id == order.Id, "Where decimal 算术（op_Multiply）命中");

    var cutoff = DateTime.UtcNow.AddDays(-1);
    var recent = await orm.Select<SmokeOrder>().Where(o => o.CreatedAt > cutoff).ToListAsync();
    Check(recent.Count == 1 && recent[0].Id == order.Id, "Where DateTime 比较（op_GreaterThan）命中");

    var orderGuid = order.RowGuid;
    var byGuid = await orm.Select<SmokeOrder>().Where(o => o.RowGuid == orderGuid).ToListAsync();
    Check(byGuid.Count == 1 && byGuid[0].Id == order.Id, "Where Guid 相等（op_Equality）命中");

    // ── 引用 Include（G1 生成 NavigationWriter 缝回）───────────────────
    var included = await orm.Select<SmokeOrder>().Include(o => o.Customer!).Where(o => o.Id == order.Id).ToListAsync();
    Check(included.Count == 1 && included[0].Customer?.Name == "alice", "Include 引用导航缝回（生成写口）");

    // ── 更新 / 删除 ────────────────────────────────────────────────────
    var updated = await orm.Update<SmokeCustomer>().Set(c => c.Name, "bob").Where(c => c.Id == customer.Id).ExecuteAffrowsAsync();
    Check(updated == 1, "Update 链影响 1 行");

    var renamed = await orm.Select<SmokeCustomer>().Where(c => c.Id == customer.Id).FirstOrDefaultAsync();
    Check(renamed?.Name == "bob", "Update 后读回新值");

    var deleted = await orm.Delete<SmokeOrder>().Where(o => o.Id == order.Id).ExecuteAffrowsAsync();
    Check(deleted == 1, "Delete 链影响 1 行");

    // ── G1-only 的静态保证：非生成实体当场拒 ───────────────────────────
    var refused = false;
    try { _ = orm.Select<SmokeBare>(); }
    catch (NotSupportedException) { refused = true; }
    Check(refused, "非生成实体被 G1-only 注册表拒绝（NotSupportedException）");

    // ── IncludeMany 集合缝合（G1 生成集合委托：造表/追加/写回，AOT 全裁剪下断言）──────────
    var extra = new SmokeOrder { CustomerId = customer.Id, Amount = 7m };
    await orm.Insert(extra).ExecuteIdentityAsync();

    var withMany = await orm.Select<SmokeCustomer>().IncludeMany(c => c.Orders!).Where(c => c.Id == customer.Id).ToListAsync();
    Check(withMany.Count == 1 && withMany[0].Orders?.Count == 1,
        $"IncludeMany 集合缝合（生成集合委托，customers={withMany.Count}, orders={withMany.FirstOrDefault()?.Orders?.Count.ToString() ?? "null"}）");
}
catch (Exception ex)
{
    Console.WriteLine($"[aot-smoke] FAIL 冒烟主体异常：{ex}");
    failures++;
}
finally
{
    try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* scratch 清理失败不掩主体结论 */ }
}

Console.WriteLine($"[aot-smoke] {(failures == 0 ? "GREEN" : "RED")} failures={failures}");
return failures == 0 ? 0 : 1;

// ── 靶子实体 ─────────────────────────────────────────────────────────────

[Table("AotSmokeCustomer")]
public class SmokeCustomer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<SmokeOrder>? Orders { get; set; }
}

[Table("AotSmokeOrder")]
public class SmokeOrder
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid RowGuid { get; set; }
    public SmokeCustomer? Customer { get; set; }
}

/// <summary>没有 [Table]：永远不在 G1 事实册里，G1-only 必须当场拒。</summary>
public class SmokeBare
{
    public int Id { get; set; }
}