using System.Data.Common;

namespace QuickSql.Providers.OracleBase;

/// <summary>连接与命令的创建。类体与各族那份同形而且这是故意的重复：provider 之间禁止互引，
/// "只有 ADO 抽象层虚调用"这一段没法共用，只能各写一份——这一层没有任何一处需要知道对面是哪一家，
/// 包括"这一家有 OUT 绑定吗"那一格（OracleKeys 要的正是这里给不出的东西）。
/// 驱动实例由 Core 的 DriverRegistry 按 BuiltinDrivers 在运行期解析后注入，热路径上没有反射。</summary>
public sealed class OracleConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：这一族的驱动（ODP.NET / 达梦的那一份）在原生客户端库缺失时
    /// 会在 CreateConnection 里抛 TypeLoadException——它的驱动常带一层非托管依赖，
    /// 这一格是启动期唯一能报出原因的入口。</summary>
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
        var connection = factory.CreateConnection() ?? throw new QuickSqlException($"{factory.GetType().Name} 未实现 CreateConnection");

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
