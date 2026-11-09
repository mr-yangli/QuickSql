namespace QuickSql.Execution;

/// <summary>保存点三步：创建 ∶ 回退 ∶ 释放，与 DbTransaction.Save ∶ Rollback(name) ∶ Release(name) 一一对应。
/// 它存在只有一个理由：文本那一路要把"哪一句"传给方言的三个发射口，而驱动那一路已经有三个方法名了。
/// 没有"提交/回滚"两步——那是根事务的事；"嵌套的嵌套"也还是这三步，只是名字不同。</summary>
internal enum SavepointStep
{
    Create,
    Rollback,
    Release
}
