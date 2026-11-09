using System.Data.Common;
using QuickSql.Providers.MySqlBase;

namespace QuickSql.Providers.MySql;

/// <summary>MySQL 本尊的驱动槽位：零覆写壳，产出与 MySqlProviderBase 逐字同形。
/// 文本按 8.0 那一侧写（IS DISTINCT FROM 要 8.0.29、窗口函数与 WITH 要 8.0、DESC 索引 8.0 才生效），
/// SPI 没有"版本"这一格，5.7 现场要自己知道差在哪。MariaDB 档已裁定不立——它的 INSERT … RETURNING
/// 要换整颗 IKeyStrategy 而基类那份 sealed 没有换的口子，键回读退路仍是自生成主键 + ExecuteAffrowsAsync。</summary>
[QuickSqlProvider(DbKind.MySql)]
public sealed class MySqlProvider : MySqlProviderBase
{
    /// <summary>内建表里的 key。assembly 填 MySqlConnector（社区驱动，官方没有 ADO.NET 驱动）；
    /// 这颗名字从未在机器上解析成功过，内建表因此刻意不写 factoryType——
    /// 省略它走"扫导出类型里第一个 DbProviderFactory 子类"那条兜底，比猜一个全名更容易成。</summary>
    public const string DriverKey = "mysql";

    public MySqlProvider(DbProviderFactory driverFactory)
        : base(DbKind.MySql, driverFactory)
    {
    }
}
