namespace QuickSql;

/// <summary>
/// 方言能力的唯一真相源。禁止用 try/catch 试探数据库支持什么语法。
/// </summary>
[Flags]
public enum DbCapabilities : uint
{
    None = 0,

    Paging_LimitOffset = 1 << 0,
    Paging_RowNum = 1 << 1,
    Paging_OffsetFetch = 1 << 2,

    Returning = 1 << 3,
    Returning_OnlyExpression = 1 << 4,

    Upsert_OnConflict = 1 << 5,
    Upsert_MergeInto = 1 << 6,
    Upsert_OnDuplicate = 1 << 7,

    Boolean_Type = 1 << 8,
    Guid_Raw16 = 1 << 9,
    Guid_Char36 = 1 << 10,

    Sequences = 1 << 11,
    Identity_ExplicitInsert = 1 << 12,
    Schemas = 1 << 13,
    Cte = 1 << 14,
    WindowFunctions = 1 << 15,
    SkipLocked = 1 << 16,
    Lock_NoWait = 1 << 17,

    /// <summary>本家有原生 JSON 型。今天映射层产不出 json 列、没有一格有消费者，声明已摘；
    /// 真实现（对象与字符串整体读写）单独一批落地时把这一位与声明一起重开。</summary>
    Json_Type = 1 << 18,
    Array_Type = 1 << 19,

    Bulk_Copy = 1 << 20,
    Bulk_ArrayBind = 1 << 21,

    RefCursor = 1 << 22,
    MultiStatement = 1 << 23,
    MultiResultSets = 1 << 24,
    Count_Over_Window = 1 << 25,
    CaseSensitive_Identifiers = 1 << 26,

    Comment_On_Column = 1 << 27,
    Alter_Column_Precision = 1 << 28,
    Truncate_Cascade = 1 << 29,
    TempTable = 1 << 30,

    /// <summary>躺着的事务：provider 的驱动能接住 BeginTransaction 与真的 DbTransaction 时，内核写路径
    /// 才允许把"一次保存 / 一次 InsertOrUpdate / 一次批量"包进一次提交。没有它在位时，内核写路径不开事务、
    /// 逐句按库的 autocommit 直发，公开的 BeginTransaction 照 CanCreateDatabase=false 的先例当场抛
    /// NotSupportedException——严禁静默假事务，"没有的事"要说成"没有的事"。
    /// 行为诚实条款（没有这一位时）：一次保存 / 批量里发出的多条语句不原子——中途失败时已经发出去的语句
    /// 不回滚，留在库里的是半批；单条语句自身的事务性按各家库的文档记账，本层不替它打包票。</summary>
    Transactions = 1u << 31,
}
