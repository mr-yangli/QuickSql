using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.TDengine;

/// <summary>连接与命令的创建。类体与 ClickHouse 那一份逐字同形而且这是故意的重复：provider 之间禁止互引，
/// "只有 ADO.NET 抽象层的虚调用"这一段没法共用，只能各写一份——连接层从头到尾不需要知道对面是哪一家。
/// 驱动实例由 Core 的 DriverRegistry 按 BuiltinDrivers 在运行期解析后注入，热路径上没有反射。
/// 驱动包纯托管（WebSocket 路走 System.Net.WebSockets，不落 taos.dll；连接串 protocol=websocket、
/// 端口缺省 6041），由"扫导出类型里第一个 DbProviderFactory 子类"那条兜底收走工厂类
///（TDengineFactory 没有静态 Instance 字段，兜底扫描对这一点不敏感）。</summary>
public sealed class TDengineConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动托管类型加载不了时会在 CreateConnection 里抛
    /// TypeLoadException，那是"装不上且报不出原因"这一类故障唯一的启动期入口。</summary>
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
    /// 用工厂造出来的命令不会自动带上当前事务。本家的事务档不存在（不声明 DbCapabilities.Transactions，
    /// 驱动侧 BeginDbTransaction 也是当场 throw），所以这一格的"当前事务"在公开路径上恒为空
    /// ——那格"没有"是诚实条款，不是缺席实现。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
