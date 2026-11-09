using System.Globalization;

namespace QuickSql.Metadata;

/// <summary>默认值的 DDL 字面量辅助，只管数字：字符串走 SqlWriter.AppendStringLiteral（引号、Unicode 前缀与
/// 转义是方言的事），bool 的两个词由各家按本家的布尔存储形给（BIT/TINYINT 落 1/0，原生 BOOLEAN 落 TRUE/FALSE）。
/// 数值必须用固定文化——12.5 在德语区机器上变成 12,5 就是建表当场炸。值域门禁在契约收尾
/// （EntityContractFactory），default 分支是防"将来添档而发射没跟上"的那颗哨兵。</summary>
public static class ColumnDefaultText
{
    public static string Number(object value) => value switch
    {
        byte v => v.ToString(NumberFormatInfo.InvariantInfo),
        sbyte v => v.ToString(NumberFormatInfo.InvariantInfo),
        short v => v.ToString(NumberFormatInfo.InvariantInfo),
        ushort v => v.ToString(NumberFormatInfo.InvariantInfo),
        int v => v.ToString(NumberFormatInfo.InvariantInfo),
        uint v => v.ToString(NumberFormatInfo.InvariantInfo),
        long v => v.ToString(NumberFormatInfo.InvariantInfo),
        ulong v => v.ToString(NumberFormatInfo.InvariantInfo),
        float v => v.ToString("R", NumberFormatInfo.InvariantInfo),
        double v => v.ToString("R", NumberFormatInfo.InvariantInfo),
        decimal v => v.ToString(NumberFormatInfo.InvariantInfo),
        _ => throw new QuickSqlException(
            $"默认值 {value}（{value.GetType().Name}）走到 DDL 字面量那一格：数字/bool/string 三档之外没有可发的形状。" +
            "值域门禁在契约收尾那一格，这一格是它的哨兵 —— 添新档要连发射一起跟上。"),
    };
}
