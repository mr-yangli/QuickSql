using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using QuickSql.Providers;

namespace QuickSql.Providers.MySqlBase;

/// <summary>批量高速层：L1 装载通道已接——ResolveCopyChannel 交得出 MySqlBulkCopyChannel
/// （驱动把 LOAD DATA LOCAL INFILE 包成流式上传，按源序号↔目标名映射，白名单之外的列整批退 L3）。
/// 通道欠连接串一颗 AllowLoadLocalInfile=true 才走得动，那道闸门由通道自己的 UnavailableBecauseOf
/// 在选层之前点名（缺它时 Auto 落 L3 而不是执行时炸）；补参前两条批量路径同抛 NotSupportedException（实测）。
/// 驱动在 C# 调用面与反射面之间有四格形状差（可选参数、ValueTask 返回值、构造子重载、没有 Dispose 面），
/// 照 T-SQL 那份手法写反射版会在真库上把把交回 null 而静默整批退 L3——失败形状与"根本没有装载类"完全同形。</summary>
public sealed class MySqlBulk : IBulkStrategy
{
    readonly Assembly _driver;

    IBulkCopyChannel? _channel;

    public MySqlBulk(DbProviderFactory driverFactory)
    {
        // 与 SqlServerBulk 同一条纪律：构造期零反射，只记"工厂住在哪个程序集"，第一次真取通道才找装载类。
        _driver = driverFactory.GetType().Assembly;
    }

    /// <summary>65535：INSERT 走预处理语句，占位符个数上限就是它（协议/服务端上限而非驱动实测上限，
    /// 报小只多几次往返，报大会在真库上得到"参数过多"）。另有一道不在参数个数上的闸门：整条语句的字节数
    /// 受 max_allowed_packet 管（会话与部署两侧的数，默认随版本 4~64 MB），所以这一格只回答
    /// "能塞多少颗参数"，不回答"能发多大一条语句"（byte[] 因此拒绝内联）。</summary>
    public int ParameterBudget => 65_535;

    /// <summary>已表态 true：本仓库在活 MySQL 上已经在发多值 INSERT 带 ; SELECT LAST_INSERT_ID() 的回填支，
    /// 四句 UPDATE 并成一条命令也实测落库成功、逐列读回一致。省的是往返，省不了那 200 句的执行本身。</summary>
    public bool SupportsMergedStatements => true;

    public string Describe => "L1 MySqlBulkCopy（LOAD DATA LOCAL 流式上传，按源序号↔目标名映射，" +
                              "白名单之外的列整批退 L3；连接串欠一颗 AllowLoadLocalInfile=true，缺它选层时就退而不是执行时炸）" +
                              "+ L3 多值 VALUES + L5 逐条；参数预算 65535（预处理语句占位符上限）；" +
                              "另一道闸门 max_allowed_packet 管的是字节而不是个数，不在本策略内；Merged 档已表态（UpdateRange 的整行批在本家真库跑通过）；" +
                              "Upsert 档已接（一句 INSERT … AS `fs` … ON DUPLICATE KEY UPDATE，8.0.19+ 的行别名写法，本尊 8.4.8 量过）";

    /// <summary>惰性形状与 SqlServerBulk 同一条：无锁，命中进字段，落空每次重查——落空的下一步是退文本层付 N 次往返。
    /// 与 T-SQL 那一家的差别在命中之后还有一道连接串闸门（UnavailableBecauseOf），判它的是内核里的选层。</summary>
    [RequiresUnreferencedCode("取通道是一次按类型名的反射查找（MySqlBulkCopyChannel.TryResolve），裁剪后的驱动交回 null，阶梯因此从 L3 起。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public IBulkCopyChannel? ResolveCopyChannel() => _channel ??= MySqlBulkCopyChannel.TryResolve(_driver);

    /// <summary>一句 upsert 的句尾：VALUES (…) AS `fs`(…) ON DUPLICATE KEY UPDATE 列 = `fs`.列。
    /// 取 8.0.19+ 的行别名写法而不是老的 VALUES(col)：老写法在 8.4.8 上每句带两条 Warning 1287
    /// （厂商写明将移除），行别名写法收下且 SHOW WARNINGS 为空。别名清单按 ValueOrder 写——
    /// 必须与内核拼出的 INSERT 列清单逐位同序，错一位就把值写进别人的列。</summary>
    public string? UpsertClause(UpsertColumns columns)
    {
        var alias = columns.Identifier("fs");

        var builder = new StringBuilder("AS ").Append(alias).Append('(');

        for (var column = 0; column < columns.ValueOrder.Count; column++)
        {
            if (column > 0)
                builder.Append(", ");

            builder.Append(columns.ValueOrder[column]);
        }

        builder.Append(") ON DUPLICATE KEY UPDATE ");

        for (var column = 0; column < columns.Settable.Count; column++)
        {
            if (column > 0)
                builder.Append(", ");

            builder.Append(columns.Settable[column]).Append(" = ").Append(alias).Append('.').Append(columns.Settable[column]);
        }

        return builder.ToString();
    }
}
