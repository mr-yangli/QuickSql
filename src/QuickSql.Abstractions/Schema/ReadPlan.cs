namespace QuickSql.Schema;

/// <summary>一次取数场次（一个 reader 的一趟读）的票据：列序起点加每列的直读档。
/// 它是读取委托的入参形状，不是每行的东西——把"这列究竟是什么类型"的探测从每值提升到每场次。
/// 实体投影的选列就是契约列序、Include 的子节点是一段连续列，两处都是"从某处起连续 n 列"，
/// 所以序号是 Offset 加 i 这一个加法，不需要每场次一张序号数组；做成 readonly struct 让票本身不落堆。
/// Ops 交出去就是只读的，改一张票等于改所有共用它的行；场次之间不复用，换一个 reader 就重探一次——
/// "驱动交回什么类型"是那只 reader 的性质，不是那个实体的性质。</summary>
public readonly struct ReadPlan(int offset, byte[] ops)
{
    /// <summary>契约第 0 列在这趟读的 reader 上的序号。第 i 列的序号是 Offset 加 i，
    /// 探测与读取用的都是这一式（ReadOps.Build 与各家臂的调用点是同一颗起点）。</summary>
    public int Offset { get; } = offset;

    /// <summary>每列的直读档，取值是 ReadOps 那批常量；0（ReadOps.Boxed）= 这一列走装箱口。
    /// 数字由运行期的 ReadOps.Probe 落，读它的是生成器模板与 G2 的表达式树，
    /// 两边都只经 DirectRead 的臂去解释，所以编号只有一处定义。</summary>
    public byte[] Ops { get; } = ops;
}
