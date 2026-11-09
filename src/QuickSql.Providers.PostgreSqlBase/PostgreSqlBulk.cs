using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>批量高速层：L1 已接——ResolveCopyChannel 交回 PostgreSqlBulkCopyChannel
/// （BeginBinaryImportAsync → 二进制 COPY 的逐行推入面），能力位随之声明；与 T-SQL 那档"同档"坐实：
/// 两家都是反射绑定、第一次取通道才找类型、缺成员整家退 L3（差别在载体：本家不用 DataTable，装载语句就是列清单本身）。
/// Merged 档不表态（false）：扩展协议下一次发送里塞 ; 分隔的多条语句不合法——全仓库唯一有"协议级禁止"靠山的 false；
/// "条文说禁"与"驱动会不会自己拆"是两件事，后者今天没有读数。</summary>
public sealed class PostgreSqlBulk : IBulkStrategy
{
    readonly Assembly _driver;

    IBulkCopyChannel? _channel;

    public PostgreSqlBulk(DbProviderFactory driverFactory)
    {
        // 与 SqlServerBulk / MySqlBulk 同一条纪律：构造期零反射，只记"工厂住在哪个程序集"。
        // PG 血统五颗键共填 Npgsql 一颗 assembly，装载类找不找得到全看现场装的是哪一支。
        _driver = driverFactory.GetType().Assembly;
    }

    /// <summary>32767：PG 前端/后端协议 Parse/Bind 消息的参数个数是有符号 int16——协议上限而非驱动实测，
    /// 接上活库后要重测。L1 通道不吃这颗预算（逐行推、没有参数表），它管退回 L3 之后的分块。</summary>
    public int ParameterBudget => 32_767;

    public string Describe =>
        "L1 二进制 COPY（BeginBinaryImportAsync → NpgsqlBinaryImporter 逐行推，列清单由装载语句点名，"
        + "白名单之外的列整批退 L3；不欠连接串开关）+ L3 多值 VALUES + L5 逐条；"
        + "参数预算 32767（协议 int16 上限，未实测）；Merged 档不表态（扩展协议多语句为 §9.4 点名的禁止面）；"
        + "Upsert 档已接（一句 INSERT … ON CONFLICT 主键 DO UPDATE SET … = EXCLUDED.…，本尊授权实例量过——那是一句，不欠上面那颗表态）";

    /// <summary>惰性形状与另两家同一条：无锁，命中进字段，落空每次重查。本家落空概率结构上更高
    /// （借邻家臂的叶子装的可能是没有导入器的 fork），而落空的下一步是退文本层付 N 次往返——
    /// 为它省一次类型查找没有意义。</summary>
    [RequiresUnreferencedCode(
        "取通道是一次按类型名与形状的反射查找（PostgreSqlBulkCopyChannel.TryResolve），裁剪后的驱动交回 null，阶梯因此从 L3 起。"
    )]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析导入器需要运行时生成代码。")]
    public IBulkCopyChannel? ResolveCopyChannel() =>
        _channel ??= PostgreSqlBulkCopyChannel.TryResolve(_driver);

    /// <summary>一句 upsert 的句尾：ON CONFLICT (键) DO UPDATE SET 列 = EXCLUDED.列。PG 血统六家一起领同一颗。
    /// 这一档是一条语句，不欠 Merged 档那颗"多语句批收不收"的表态。冲突目标点的是主键列清单：
    /// 要求库里有恰好覆盖这几列的唯一索引（复合主键装得下）；现场的表若靠另一颗唯一键去重，本档不接那种姿势。
    /// 六家共一份的原因：这一族借用的都是 PostgreSQL 的内核，而 ON CONFLICT … DO UPDATE 是 9.5 起的语法，
    /// 各家内核版本下限没有活库可量。</summary>
    public string? UpsertClause(UpsertColumns columns)
    {
        var builder = new StringBuilder("ON CONFLICT (");

        for (var key = 0; key < columns.Keys.Count; key++)
        {
            if (key > 0)
                builder.Append(", ");

            builder.Append(columns.Keys[key]);
        }

        builder.Append(") DO UPDATE SET ");

        for (var column = 0; column < columns.Settable.Count; column++)
        {
            if (column > 0)
                builder.Append(", ");

            builder
                .Append(columns.Settable[column])
                .Append(" = EXCLUDED.")
                .Append(columns.Settable[column]);
        }

        return builder.ToString();
    }
}
