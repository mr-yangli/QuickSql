using QuickSql.Metadata;
using QuickSql.Sql;

namespace QuickSql.Values;

/// <summary>参数绑定的差异面：Guid 形态、CLOB 流、JSON、DateTime 时区、字符串的 Unicode 前缀。
/// 同一条 INSERT 在五个库里参数化形状不同，这一层是唯一收口处。</summary>
public interface IValueBinder
{
    /// <summary>产出一个 ADO 参数描述。hint 为 null 时按 CLR 类型猜，但热路径必须传非 null——猜的逻辑走的是反射式查表。</summary>
    AdoParameter CreateParameter(string name, object? value, ColumnContract? hint);

    /// <summary>只取归一后的值（L1 装载通道用），<b>不分配 AdoParameter record</b>。批量装载每格都要归一值进 DataTable，
    /// 走 <see cref="CreateParameter"/> 会为了取 .Value 白白 new 一颗 record（1000×30 = 30000 颗 ≈ 1.4 MB，
    /// 正是 QuickSql L1 相对 SqlSugar Fastest 多出的分配主项）。默认实现退回 CreateParameter(...).Value（仍分配但正确）；
    /// 热 provider 覆写为直取各自的廉价 Normalize，省掉这笔。归一口径必须与 CreateParameter 的 .Value 逐字一致。</summary>
    object? NormalizeValue(object? value, ColumnContract? hint) => CreateParameter(string.Empty, value, hint).Value;

    /// <summary>能否把值内联成字面量。SqlWriter.AppendStringLiteral 与 ToLogSql() 用它；返回 false 时一律退回参数。</summary>
    bool TryInlineLiteral(object? value, ColumnContract? hint, out string sql);
}

/// <summary>参数化时用到的 ADO 值形态决策，由 IValueBinder 读取。</summary>
public sealed record BindingProfile(bool StringsAsUnicode, bool GuidAsText, bool DateAsUtc, bool DecimalAsDouble);
