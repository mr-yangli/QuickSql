using System.Data.Common;
using QuickSql.Providers.TDengine;

namespace QuickSql.TestInfra;

/// <summary>TDengine 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Xugu"/>、<see cref="Gbase8s"/>、
/// <see cref="Sqlite"/>、<see cref="DuckDb"/>、<see cref="ClickHouse"/> 同形：<b>provider 由调用方直接构造并交进来</b>
///（§12.7 F），内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是单层 provider 的第三颗【文档口径行】（继 DuckDb、ClickHouse 而来），时序库这一档的第一颗。
/// 本机没有引擎、驱动程序集从未在机器上解析成功过——离线册这里没有 <see cref="SqlServer.Driver"/>、
/// <see cref="Sqlite.Driver"/> 的对应物，拿 <see cref="SqlServer.NoDriverFactory"/>，
/// 碰驱动就是 §3 那条约束的红；活库那一侧等第一台引擎的探针，不是这里的任何一条。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：离线侧这条串永远不被任何驱动解析。
/// 本家的"建库"整格不可走（provider 侧 <c>CanCreateDatabase=false</c>——CREATE DATABASE 语句存在，
/// 但"连接串不带 db 直连再补建"那条锚点路没有读数，ws 驱动能不能空库连接只有引擎答得了）。
/// 本家的连接串键与主流 SQL 家不同形：<c>protocol=websocket</c>（纯托管档，端口缺省 6041）、
/// 库名键是 <c>db=</c>——两颗都要等探针复核，这里不预支读数。</para></summary>
public sealed class TDengine
{
    public static TDengineProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在这一家与前面各家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
