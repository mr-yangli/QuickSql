using System.Runtime.CompilerServices;

/// <summary>钩子事件的构造器是 internal：只能由发射层造，否则调用方伪造一个事件就能绕过 WasSet 的记录、回放不可信。写路径的消费者在 Core，故在此开一扇内部可见的口。</summary>
[assembly: InternalsVisibleTo("QuickSql.Core")]
