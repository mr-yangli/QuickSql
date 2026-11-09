using System.Data.Common;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.OpenGauss;

/// <summary>openGauss 的驱动槽位：PG 血统的分叉库叶子，零覆写，产出与血统逐字同形。
/// 点名的方言分岔（RETURNING 受限、分布式 DN 序列不连续、DISTRIBUTED BY 分片）全在语义层，不改文本；
/// 那颗"9.2 内核不认 IDENTITY"是内核代数推断而非厂商读数，按"文档明写的分岔才配覆写"不覆写
/// ——且血统构造直 new 具体 CodeFirst，三族里只有 Oracle 留了 CreateCodeFirst 虚口，即使想覆写也无从下手。</summary>
[QuickSqlProvider(DbKind.OpenGauss)]
public sealed class OpenGaussProvider(DbProviderFactory driverFactory)
    : PostgreSqlProviderBase(DbKind.OpenGauss, driverFactory)
{
    /// <summary>内建表里的 key。assembly 填 Npgsql：openGauss 厂商分发的是 Npgsql 的 fork，
    /// fork 的托管程序集名没有可引读数，故取与血统同源的那条路而不猜名字。
    /// 与全部"未实测"行同一处境：从未解析成功过，刻意不写 factoryType。</summary>
    public const string DriverKey = "opengauss";
}
