using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.Xugu;

/// <summary>连接与命令的创建。类体与另四族逐字同形且是故意的重复：provider 之间禁止互引，
/// "只有 ADO 抽象层虚调用"这一段没法共用——它同时是第五份证据：连接层从头到尾不需要知道对面是哪一家。
/// 驱动由 Core 的 DriverRegistry 在运行期解析后注入，热路径无反射。
/// 注意：厂商 4.0.0.8 起交付只有托管一颗。原生同伴（xugusql.dll/libxugusql.so）缺失时
/// CreateConnection 与 TryEnsureLoaded 照样成功，缺失到 Open 那一步才以 DllNotFoundException（0x8007007E）现身——
/// TryEnsureLoaded 只挡得住托管那一半。</summary>
public sealed class XuguConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动托管类型加载不了时会在 CreateConnection 里抛 TypeLoadException，
    /// 这正是"装不上且报不出原因"的那类故障，要能在启动期说清。原生依赖缺失时这里不抛（见类注释）。</summary>
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
