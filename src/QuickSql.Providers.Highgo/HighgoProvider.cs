using System.Data.Common;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.Highgo;

/// <summary>瀚高 HighGo 的驱动槽位：PG 血统第四颗叶子，零覆写，产出与血统另三叶逐字同形。
/// 厂商行坑列只有权限模型与审计策略，一颗方言级分岔都没点名——那颗 IDENTITY 问题对本家
/// 不是"裁不落"而是问都不成立（"文档明写的分岔才配覆写"的另一半：没有明写的分岔，连"裁过"都不该假装做过）。
/// 真实例到场的第一问因此是"这一家兼容的是哪一代 PG"。</summary>
[QuickSqlProvider(DbKind.Highgo)]
public sealed class HighgoProvider : PostgreSqlProviderBase
{
    /// <summary>内建表里的 key。assembly 填 Npgsql：来路是行内"兼容 PG"三个字——线协议就是 PG 的，
    /// Npgsql 是血统协议自己的驱动，不借邻家的臂。瀚高自己分发的托管驱动叫什么没有可引读数，不猜名字；
    /// 整行未实测，刻意不写 factoryType。</summary>
    public const string DriverKey = "highgo";

    public HighgoProvider(DbProviderFactory driverFactory)
        : base(DbKind.Highgo, driverFactory)
    {
    }
}
