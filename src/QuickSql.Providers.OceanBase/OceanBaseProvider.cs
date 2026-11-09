using System.Data.Common;
using QuickSql.Providers.MySqlBase;

namespace QuickSql.Providers.OceanBase;

/// <summary>OceanBase（MySQL 模）的驱动槽位：MySQL 血统的第二颗分叉库叶子，零覆写，产出与血统及另两叶逐字同形。
/// 它是 MySQL / Oracle 双模，"这条连接建在 MySQL 模"是连接级事实而 SPI 问不出来——与金仓兼容模式开关
/// 是同一颗未建模缺口，因此本类只接 MySQL 模那颗叶子、不接 Oracle 模、也不为"模式"开槽位。
/// 点名的语义分岔（分区表、全局索引、AUTO_ID_CACHE 自增不连续）全在文本层之外，不改发射形状；
/// 覆写点（自增写法、表选项）需要的是与 MySQL 同一颗值（推断，未实测），照 TiDB 同一条纪律选继承。</summary>
[QuickSqlProvider(DbKind.OceanBase)]
public sealed class OceanBaseProvider(DbProviderFactory driverFactory)
    : MySqlProviderBase(DbKind.OceanBase, driverFactory)
{
    /// <summary>内建表里的 key。与 mysql／tidb 共用同一份 assembly（MySqlConnector）；从未解析成功过，刻意不写 factoryType。
    /// 比前两颗多一层不确定：若现场把库建在 Oracle 模，该走的是另一颗驱动——这一格差异今天无处声明（"模式在连接上决定"缺口）。</summary>
    public const string DriverKey = "oceanbase";
}
