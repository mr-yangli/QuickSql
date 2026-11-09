using System.Data.Common;
using QuickSql.Providers.MySqlBase;

namespace QuickSql.Providers.TiDb;

/// <summary>TiDB 的驱动槽位：MySQL 血统的第一颗分叉库叶子，零覆写，产出与 MySqlProviderBase / MySql 壳逐字同形。
/// 两颗点名覆写点（IdentityClause、表选项）逐颗对完都不覆写：自增同词同位置（推断），
/// ENGINE = InnoDB 在 TiDB 上被接受并被忽略（推断，未实测）、CHARSET 与 TiDB 默认一致——不留没人覆写的虚函数。
/// 真分岔全在语义层、文本不替它们说话：自增批量预分配（多行 VALUES 的 id 连续更不成立）、
/// LIMIT 大偏移退化（纯性能层）、旧版无 FK 强制（本仓库 DDL 不发外键，无害）。</summary>
[QuickSqlProvider(DbKind.TiDb)]
public sealed class TiDbProvider : MySqlProviderBase
{
    /// <summary>内建表里的 key。与 mysql 行共用同一份 assembly（MySqlConnector）：驱动按协议族装、库种按方言假设装。
    /// 从未在机器上解析成功过，刻意不写 factoryType（走扫第一个 DbProviderFactory 子类的兜底）。</summary>
    public const string DriverKey = "tidb";

    public TiDbProvider(DbProviderFactory driverFactory)
        : base(DbKind.TiDb, driverFactory)
    {
    }
}
