using System.Data.Common;
using QuickSql.Providers.TiDb;

namespace QuickSql.TestInfra;

/// <summary>TiDB 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Kingbase"/>、<see cref="Dameng"/>、
/// <see cref="MySql"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 MySQL 血统的第二颗叶子，存在的理由与 <see cref="MySql"/> 那一份<em>同一条而更弱一档</em>：
/// 本机没有任何一台 TiDB 实例（§13.4 授权的只有两台 SQL Server 与一台虚谷），清单里那颗 <c>tidb</c> 键
/// 与 <c>mysql</c> 键共用同一份 <c>MySqlConnector</c> 程序集名，而这个名字从未被解析成功过
/// （§13.5 台账里这一行整行「未实测」），所以这里同样<em>没有</em> <see cref="SqlServer.Driver"/>
/// 的对应物 —— 有了它就会被误读成"这条解析路径验过"，而这一家连"第一次真库实测最可能落"那句话都轮不上
/// （那句说的是 mysql 那一行：它排在 tidb 之前，理由写在清单自己那两行的 $comment 里）。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：
/// 这条串永远不被任何驱动解析（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。
/// 第四家、第五家不需要第四种、第五种假配置。</para></summary>
public static class TiDb
{
    public static TiDbProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第五家上与前三家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
