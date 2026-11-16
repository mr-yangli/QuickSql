using System.Globalization;

namespace QuickSql.Query.Model;

/// <summary>键值的规范文本形态（一处实现，两处消费：QueryModelPrinter 的锚点行与树形缝合的 Path）。
/// 二进制打十六进制——不然两只内容不同的 byte[] 都渲染成 "System.Byte[]"，那两格就失去了区分能力；
/// 其余走不变文化的 Convert.ToString——规范文本与 Path 要跨库跨机器稳定，不吃进程的文化设定。
/// null（含 ToString 返 null 的病态型）交回 "NULL" 字面：规范文本侧由调用方再包括号
/// （<c>[NULL]</c>），Path 侧原样进段。</summary>
internal static class KeyText
{
    public static string Of(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "NULL",
    };
}
