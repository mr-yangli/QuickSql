namespace QuickSql.Tracking;

/// <summary>跟踪器里一个实体的状态。Detached 故意排在 0 位：没被赋过值的状态字段（结构体默认、忘设置的局部、
/// 反序列化缺字段）落在 Detached 上是"这一行不进任何语句"，落在 Added 上则是"一次保存把谁都没打算写的实体插进库"
/// ——宁可少做，不要多做。成员名字与个数与常见状态机一致，按名字 switch 的调用方不受影响。</summary>
public enum EntityState
{
    /// <summary>会话不认识这个实例：不在身份映射里、不进 SaveChangesReport，也是 default(EntityState) 那一档。</summary>
    Detached = 0,

    /// <summary>本会话新加入、库里还没有这一行。写入顺序里它在最前。</summary>
    Added,

    /// <summary>从库里读出来（或被 Attach）且相对快照没变：不产生任何语句。</summary>
    Unmodified,

    /// <summary>相对快照有变化：写入时只发变化的那些列（脏列合成）。
    /// 变化被撤销后回到 Unmodified，"曾经碰过"不会变成一次多余的 UPDATE。</summary>
    Modified,

    /// <summary>本会话要删掉这一行；写完之后实例转为 Detached。</summary>
    Deleted,
}
