using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.Gbase8s;

/// <summary>连接与命令的创建，六份逐字同形拷贝之一（provider 之间禁止互引）。驱动由 Core 的
/// DriverRegistry 在运行期解析后注入，热路径无反射；程序集名 GBase.DataDB 只来自内建表一行，
/// 从未在一台机器上解析成功，内建表刻意不写 factoryType，走"扫导出类型里第一个 DbProviderFactory 子类"的兜底。
/// 不能反过来写"没有原生依赖"：IBM 的 Informix .NET 提供者历史上要原生库（ifxglu.dll），
/// GBase.DataDB 是移植还是重写没有读数——TryEnsureLoaded 在这一家既不许被当成"够用了"，
/// 也不许被当成"必然挡不住"，它只是探测口。</summary>
public sealed class Gbase8sConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动托管类型加载不了时会在 CreateConnection 里抛 TypeLoadException，
    /// 那是"装不上且报不出原因"这类故障唯一的启动期入口。</summary>
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

    /// <summary>命令必须由连接创建而不是工厂：连接池与事务的归属由连接决定，用工厂造出来的命令不会自动带上当前事务。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
