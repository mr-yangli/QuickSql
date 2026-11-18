namespace QuickSql.Providers;

/// <summary>批量阶梯 Auto 档的公共常数。放 Abstractions 而不是内核，理由同 <see cref="ParameterBudgets"/>：
/// 引用这个名字的还有测试册与册子里的推导表，它们不能反向引用内核。</summary>
public static class BulkLadder
{
    /// <summary>Auto 取装载通道（§12.1 的 L1）的行数门槛，口径是<b>文本层的往返次数</b>：一批按 L3 多值语句
    /// 分块后要发的语句数达到这个数，Auto 才算「请求特征满足」、才换装载通道；没达标时即使通道在场也落
    /// 多值语句。量的是"不切要付多少"，所以每块行数由参数预算推出来（§12.2 同一条公式），宽窄表自适应——
    /// SqlServer 预算 2100：30 列宽表每块 63 行，442 行到数；7 列表每块 270 行，1891 行到数。
    /// MySQL/PG 的预算大（65535/32767），每块行数多、天然少切（30 列分别要 13763/6882 行）；
    /// 没有通道的一家（SQLite）与本数无关。
    /// 取 8 的依据：多值语句那一层稳态走 ArrayPool、单事务包裹，几条语句以内它比装载通道省
    /// （通道要整批 DataTable 加逐格装箱，见 §12.1 那一行的代价清单）；语句数上到个数量级，
    /// 递交次数本身成了主导项，通道的一次递交才把这笔钱赢回来。请求了 <c>BulkMethod.CopyChannel</c>
    /// 的不算请求特征——显式点名的那一档不看本数，3 行也走通道。</summary>
    public const int AutoCopyRoundTrips = 8;
}
