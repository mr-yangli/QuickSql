using System.Diagnostics.CodeAnalysis;

namespace QuickSql.Sample;

/// <summary>应用会话：表达式体属性即可，Set&lt;T&gt;() 按类型缓存，不会每次构造新对象。</summary>
public class AppDbContext(QuickSqlOrm orm) : DbContext(orm)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
}
