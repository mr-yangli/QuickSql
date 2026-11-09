using System.Data.Common;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.Gbase8c;

/// <summary>GBase 8c 的驱动槽位：PG 血统第五颗叶子，零覆写，产出与血统另四叶逐字同形。
/// 厂商行明写的"分片语法差异"是本族第一颗落在发射面上的分岔，但它住在 CREATE TABLE 的句内子句位
/// 而本仓库恰好不发那一格（分片子句不写），所以"文档明写的分岔才配覆写"不触发——重开要等仓库自己长出分片 DDL。
/// IDENTITY 那一问的前件是借来的：本行只写"与 openGauss 高度相似"，像到哪一层没有读数，真实例到场先问它。</summary>
[QuickSqlProvider(DbKind.Gbase8c)]
public sealed class Gbase8cProvider : PostgreSqlProviderBase
{
    /// <summary>内建表里的 key。assembly 填 Npgsql（来路同为行内"兼容 PG"，与瀚高同档）；
    /// GBase 自己分发的托管驱动叫什么没有读数，不猜名字。整行未实测，刻意不写 factoryType。</summary>
    public const string DriverKey = "gbase8c";

    public Gbase8cProvider(DbProviderFactory driverFactory)
        : base(DbKind.Gbase8c, driverFactory)
    {
    }
}
