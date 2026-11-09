using System.Data.Common;
using QuickSql.Providers.DuckDb;

namespace QuickSql.TestInfra;

/// <summary>DuckDB 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Xugu"/>、<see cref="Gbase8s"/>、
/// <see cref="Sqlite"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是 PG 血统的第七颗叶子，存在的理由与 <see cref="TiDb"/> 那一份同一条：本机没有任何一台引擎实例
/// （DuckDB 是内嵌单文件引擎，DuckDB.NET.Data 驱动也从未被解析成功过 —— §13.5 台账里这一行整行「未实测」），
/// 所以这里<em>没有</em> <see cref="SqlServer.Driver"/>、<see cref="Sqlite.Driver"/> 的对应物 ——
/// 有了它就会被误读成"这条解析路径验过"，而本家连"驱动装上能开一颗文件"都还没有第一枚读数。
/// 它与 <see cref="Sqlite"/> 那一档的同（"库"就是文件）隔着两颗：本家没有引擎可开，所以连
/// <see cref="Sqlite.DriverFromManifest"/> 那一条集成侧的通路都没有——只有离线册，离线册拿
/// <see cref="SqlServer.NoDriverFactory"/>，碰驱动就是 §3 那条约束的红。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：这条串永远不被任何驱动解析
///（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。本家与第七家同档而更轻：
/// "建库"整格不存在（provider 侧 <c>CanCreateDatabase=false</c>），连那颗占位串要不要带库名这一问
/// 都没有实体要答。</para></summary>
public sealed class DuckDb
{
    public static DuckDbProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第八家上与前面七家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
