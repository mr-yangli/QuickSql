using System.Data.Common;

namespace QuickSql.Providers.Sqlite;

/// <summary>连接与命令的创建。类体与另六族逐字同形：provider 之间禁止互引，
/// "只有 ADO 抽象层虚调用"那一段没法共用、只能各写一份。本家连接串与另六家差在"它指向什么"：
/// 写的是一个文件的名字而不是"到一台服务器的一条路"——连接串就是"这次连的是哪一个库"这件事本身，
/// 那颗字段一换，建出来的表就落在另一个文件里而同一条查询文本仍然绿，
/// 所以本家 provider 比另六家更需要"不要碰连接串"。</summary>
public sealed class SqliteConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：CreateConnection 交回托管类型只能证明托管半边加载成功，
    /// 真正打通到引擎要靠下一条语句（原生同伴 e_sqlite3 随同一份 NuGet 图落下，装不上是整颗装不上）。
    /// 方法留在这里防的不是本家，是 Core 那条"启动期说清原因"的路。</summary>
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
    /// 用工厂造出来的命令不会自动带上当前事务。本家工厂确实造得出无连接的命令，
    /// 所以"只走连接那一条"不是被库逼出来的；DbConnection.DbProviderFactory 是 protected、
    /// 从连接实例问不出它是谁造的，驱动只能由外部注入。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
