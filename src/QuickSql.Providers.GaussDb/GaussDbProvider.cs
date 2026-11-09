using System.Data.Common;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.GaussDb;

/// <summary>GaussDB 的驱动槽位：PG 血统的第二颗分叉库叶子，零覆写，产出与血统另两叶逐字同形。
/// 自增那问与 openGauss 同裁（厂商共行读数是同一句，"文档明写的分岔才配覆写"；覆写入口本就不存在）；
/// GaussDB 与 openGauss fork 的关系、自己的托管驱动叫什么没有可引读数，比 openGauss 更薄一档，不新立任何推断。
/// 真实例到场第一问之前要先问"这台库与 openGauss 是不是同一套文本假设"。</summary>
[QuickSqlProvider(DbKind.GaussDb)]
public sealed class GaussDbProvider : PostgreSqlProviderBase
{
    /// <summary>内建表里的 key。assembly 填 Npgsql（与 opengauss 两颗键共享邻家库种的兼容臂，金仓的 Kdbndp 不共享过来）；
    /// 从未解析成功过，刻意不写 factoryType。现场若装的是 GaussDB 自己分发的那份，第一件事是按厂商读数改回它自己的程序集名。</summary>
    public const string DriverKey = "gaussdb";

    public GaussDbProvider(DbProviderFactory driverFactory)
        : base(DbKind.GaussDb, driverFactory)
    {
    }
}
