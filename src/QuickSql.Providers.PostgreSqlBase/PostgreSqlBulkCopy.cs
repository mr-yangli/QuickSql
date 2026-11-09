using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using QuickSql.Metadata;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>批量装载通道：BeginBinaryImportAsync → NpgsqlBinaryImporter（COPY … FROM STDIN (FORMAT BINARY) 的流式写面）。
/// 与 T-SQL/MySQL 吃整批载体（DataTable）不同，这一家是逐行推——WriteRowAsync 一行进、CompleteAsync 收尾报数：
/// 列形状由 COPY 语句在 BEGIN 那一刻向库要（解析目标列 OID），再建载体只是白抄一遍行 × 列的 object[]。
/// 成员按形状找（不写死 NpgsqlConnection 的名字），缺任何一件交回 null 整家退 L3——
/// 借邻家臂的叶子装的是没有导入器的 fork 时同样缺席，provider 代码一个字不改。
/// 两个已知边界照实写：事务交不出去（导入器挂在连接上、没有外部事务入参面，带活事务的 L1 未实测）；
/// 二进制 COPY 对列类型不匹配的报错落在流的中段，一批里第 n 行不合格给不出"坏在哪一行"。</summary>
sealed class PostgreSqlBulkCopyChannel : IBulkCopyChannel
{
    readonly MethodInfo _begin;
    readonly PropertyInfo _timeout;
    readonly MethodInfo _writeRow;
    readonly MethodInfo _complete;
    readonly MethodInfo _asTask;
    readonly MethodInfo? _disposeAsync;
    readonly MethodInfo? _dispose;

    PostgreSqlBulkCopyChannel(
        MethodInfo begin,
        PropertyInfo timeout,
        MethodInfo writeRow,
        MethodInfo complete,
        MethodInfo asTask,
        MethodInfo? disposeAsync,
        MethodInfo? dispose
    )
    {
        _begin = begin;
        _timeout = timeout;
        _writeRow = writeRow;
        _complete = complete;
        _asTask = asTask;
        _disposeAsync = disposeAsync;
        _dispose = dispose;
    }

    /// <summary>白名单与另两家同一把尺：那五类缺席的理由在本家同样成立，本家还多一条——"坏行定位不了"。
    /// 三通道判据一致，"批量与逐条在库里等价"才可数。</summary>
    public bool SupportsColumn(ColumnContract column) => NetType(column.ClrType) is not null;

    /// <summary>这一家的装载类不欠连接串任何开关（UnavailableBecauseOf 走接口的默认 null）——
    /// 与 MySQL 那家恰成两个样本：那一家真实欠一颗参数，这一家服务端能连上就能装。</summary>
    [RequiresUnreferencedCode(
        "本对象的 MemberInfo 全部来自按类型名与形状的反射查找（TryResolve），裁剪后的驱动走不到这里。"
    )]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射调用导入器需要运行时生成代码。")]
    public async Task<int> WriteAsync(
        BulkConnection target,
        BulkCopyRequest request,
        CancellationToken ct
    )
    {
        var importer = await Await(_begin.Invoke(target.Connection, [Statement(request), ct])!)
            .ConfigureAwait(false);

        // Timeout 的可写性在 TryResolve 里已点死（SetMethod 为空整家不进场），这里直接落值。
        _timeout.SetValue(importer, TimeSpan.FromSeconds(target.TimeoutSeconds));

        try
        {
            foreach (var row in request.Rows)
                await (Task)_writeRow.Invoke(importer!, [ct, row])!;

            // CompleteAsync 交回的就是"进了几行"（给不出数的形状在 TryResolve 那里已整家退掉）。
            var copied = await Await(_complete.Invoke(importer!, [ct])!).ConfigureAwait(false);

            return checked((int)Convert.ToInt64(copied!, CultureInfo.InvariantCulture));
        }
        finally
        {
            // 两条 disposal 至少有一条在 TryResolve 被点死；优先异步——同步 Dispose 会把
            // "没 Complete 就掐断"的清理拉进当前线程的同步上下文。
            if (_disposeAsync is not null)
                await Await(_disposeAsync.Invoke(importer!, null)!).ConfigureAwait(false);
            else
                _dispose!.Invoke(importer!, null);
        }
    }

    /// <summary>把 Task 与装箱的 ValueTask 折成同一个 await 面并读出它的 Result。
    /// 反射边界上没有静态泛型，只能"先 await、再按名取 Result"；ValueTask 转 Task 走它自己的 AsTask()
    /// （TryResolve 已点存在）。RequiresUnreferencedCode 落在这颗 helper 上：
    /// 它把整段"按名点成员"的反射边界圈在自己体内，调用方本来就带着同一条税。</summary>
    [RequiresUnreferencedCode(
        "装箱 awaitable 的 AsTask 与 Result 按名点取：裁剪产物上这条通道不进场（TryResolve 已把泛型形状点死，走到这里即有源）。"
    )]
    [RequiresDynamicCode("反射调用装箱的 ValueTask/Task 成员需要运行时生成代码。")]
    static async Task<object?> Await(object awaitable)
    {
        var task =
            awaitable as Task
            ?? (Task)
                awaitable.GetType().GetMethod("AsTask", Type.EmptyTypes)!.Invoke(awaitable, null)!;

        await task.ConfigureAwait(false);

        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    /// <summary>装载语句：列清单必须逐列点名——不给列清单时 COPY 装的是全表列序，而我们递交的行只含可写列
    /// （自增列与并发列不在里面），列序错位会把第 3 个值写进第 3 列。本家连名字都只在这条语句里出现一次，
    /// 导入器后面不再有映射面；目标表名由 EmitTableName 取号限定过，与文本层同一张表。</summary>
    static string Statement(BulkCopyRequest request)
    {
        var columns = new StringBuilder();

        foreach (var column in request.Columns)
        {
            if (columns.Length > 0)
                columns.Append(", ");

            columns.Append('"').Append(column.ColumnName.Replace("\"", "\"\"")).Append('"');
        }

        return "COPY " + request.DestinationTable + " (" + columns + ") FROM STDIN (FORMAT BINARY)";
    }

    static Type? NetType(Type clrType) =>
        Flatten(clrType) switch
        {
            var t when t == typeof(bool) => t,
            var t when t == typeof(byte) => t,
            var t when t == typeof(short) => t,
            var t when t == typeof(int) => t,
            var t when t == typeof(long) => t,
            var t when t == typeof(float) => t,
            var t when t == typeof(double) => t,
            var t when t == typeof(decimal) => t,
            var t when t == typeof(string) => t,
            var t when t == typeof(byte[]) => t,
            var t when t == typeof(Guid) => t,
            var t when t == typeof(DateTime) => t,
            var t when t == typeof(DateTimeOffset) => t,
            var t when t == typeof(TimeSpan) => t,
            _ => null,
        };

    static Type Flatten(Type clrType)
    {
        var member = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return member.IsEnum ? Enum.GetUnderlyingType(member) : member;
    }

    /// <summary>整套验一遍，缺任何一件交回 null（阶梯从 L3 起，与"这一家没有通道"同形）。
    /// 五颗成员各有不退的理由：WriteRowAsync 缺了就只能逐格 Write（逐格写会把"缺一颗"错报成"多一列"）；
    /// CompleteAsync 交回的行数是"报不出行数就不算通道"的那一颗，泛型只认 long/ulong 两种（随版本漂，认死一种会把能成的退掉）；
    /// Timeout 缺了批量会静默继承连接的默认超时（与文本层不同的一道默认值），不接；disposal 两条至少一条。</summary>
    [RequiresUnreferencedCode(
        "按类型名与形状在驱动程序集里找导入器与它的成员（TryResolve 本体），被裁剪的驱动会交回 null。"
    )]
    [RequiresDynamicCode("反射调用导入器需要运行时生成代码。")]
    internal static PostgreSqlBulkCopyChannel? TryResolve(Assembly driver)
    {
        var importer = driver.GetType("Npgsql.NpgsqlBinaryImporter", throwOnError: false);

        if (importer is null)
            return null;

        // 那颗「收 (COPY 语句, 取消) 且交回 Task<导入器>」的方法只从连接类上按形状找：
        // 写死 NpgsqlConnection 会把 fork 与将来改名一并赌进去。
        MethodInfo? begin = null;

        foreach (var candidate in driver.GetExportedTypes())
        {
            if (!candidate.IsSubclassOf(typeof(DbConnection)))
                continue;

            var method = candidate.GetMethod(
                "BeginBinaryImportAsync",
                [typeof(string), typeof(CancellationToken)]
            );

            if (
                method is not { ReturnType.IsGenericType: true }
                || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)
                || method.ReturnType.GetGenericArguments()[0] != importer
            )
                continue;

            begin = method;
            break;
        }

        var timeout = importer.GetProperty("Timeout");
        var writeRow = importer.GetMethod(
            "WriteRowAsync",
            [typeof(CancellationToken), typeof(object[])]
        );
        var complete = importer.GetMethod("CompleteAsync", [typeof(CancellationToken)]);

        if (
            begin is null
            || timeout is null
            || writeRow is null
            || complete is null
            || timeout.SetMethod is null
        )
            return null;

        // 行数只认 long/ulong 两种泛型参数；AsTask 是把 ValueTask 折进同一个 await 面的那颗，缺了不接。
        if (
            !complete.ReturnType.IsGenericType
            || complete.ReturnType.GetGenericTypeDefinition() != typeof(ValueTask<>)
        )
            return null;

        var count = complete.ReturnType.GetGenericArguments()[0];

        if (count != typeof(long) && count != typeof(ulong))
            return null;

        var asTask = complete.ReturnType.GetMethod("AsTask", Type.EmptyTypes);

        if (asTask is null || asTask.ReturnType != typeof(Task<>).MakeGenericType(count))
            return null;

        var disposeAsync = importer.GetMethod("DisposeAsync", Type.EmptyTypes);
        var dispose = typeof(IDisposable).IsAssignableFrom(importer)
            ? importer.GetMethod("Dispose", Type.EmptyTypes)
            : null;

        if (disposeAsync is null && dispose is null)
            return null;

        return new PostgreSqlBulkCopyChannel(
            begin,
            timeout,
            writeRow,
            complete,
            asTask,
            disposeAsync,
            dispose
        );
    }
}
