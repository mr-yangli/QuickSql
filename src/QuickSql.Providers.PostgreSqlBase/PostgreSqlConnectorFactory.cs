using System.Data.Common;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>连接与命令的创建。类体与 T-SQL 那一份同形且是故意的重复：provider 之间禁止互引，
/// "只有 ADO 抽象层虚调用"这段没法共用。驱动实例由 Core 的 DriverRegistry 运行期解析后注入，
/// 热路径上没有反射，也没有字符串找类型。</summary>
public sealed class PostgreSqlConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动的原生依赖缺失时会在 CreateConnection 里抛 TypeLoadException，
    /// 这正是"装不上且报不出原因"的那一类故障，要能在启动期说清。</summary>
    public bool TryEnsureLoaded(out string? failure)
    {
        try
        {
            using var probe = factory.CreateConnection();

            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    public DbConnection CreateConnection(string connectionString)
    {
        var connection =
            factory.CreateConnection()
            ?? throw new QuickSqlException($"{factory.GetType().Name} 未实现 CreateConnection");

        connection.ConnectionString = connectionString;

        return connection;
    }

    /// <summary>命令必须由连接创建而不是工厂：连接池与 Transaction 的归属由连接决定，
    /// 用工厂造出来的命令不会自动带上当前事务。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
