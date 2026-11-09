using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.MySqlBase;

/// <summary>批量装载通道：MySqlBulkCopy（驱动包成 LOAD DATA LOCAL INFILE 的流式上传）。
/// 按序号映射源列、按名字映射目标列：源是我们按列序自建的 DataTable（序号由构造保证），
/// 目标列名才是跨表序漂移要防的那一格；DestinationTableName 填不带库名的表名，限定交给连接所在的库。
/// 成员形状按反射面点死（构造子两位、映射 ctor 第三位可省、返回 ValueTask、无 Dispose 面——
/// 与强类型调用面的四格形状差），缺任何一件整家退 L3，与"这一家没有通道"完全同形。</summary>
sealed class MySqlBulkCopyChannel : IBulkCopyChannel
{
    readonly ConstructorInfo _bulkCtor;
    readonly ConstructorInfo _mappingCtor;
    readonly PropertyInfo _destinationTable;
    readonly PropertyInfo _timeout;
    readonly PropertyInfo _mappings;
    readonly PropertyInfo _rowsCopied;
    readonly MethodInfo _addMapping;
    readonly MethodInfo _write;

    /// <summary>把 ValueTask 交回成 Task 的那一颗；驱动直接返 Task 时为 null。</summary>
    readonly MethodInfo? _asTask;

    MySqlBulkCopyChannel(ConstructorInfo bulkCtor, ConstructorInfo mappingCtor, PropertyInfo destinationTable,
        PropertyInfo timeout, PropertyInfo mappings, PropertyInfo rowsCopied, MethodInfo addMapping,
        MethodInfo write, MethodInfo? asTask)
    {
        _bulkCtor = bulkCtor;
        _mappingCtor = mappingCtor;
        _destinationTable = destinationTable;
        _timeout = timeout;
        _mappings = mappings;
        _rowsCopied = rowsCopied;
        _addMapping = addMapping;
        _write = write;
        _asTask = asTask;
    }

    public bool SupportsColumn(ColumnContract column) => NetType(column) is not null;

    /// <summary>这条串过不去的那句话。键名认两颗：AllowLoadLocalInfile（MySqlConnector 正主）与
    /// UseLocalInfile（Oracle 官方驱动的旧名），值认 true/1/yes/on 四种布尔写法（不区分大小写）。
    /// 只报缺哪颗参数，不回显串本身。</summary>
    public string? UnavailableBecauseOf(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        if (Read(builder, "AllowLoadLocalInfile") || Read(builder, "UseLocalInfile"))
            return null;

        return "这条连接串没有放行本地装载（AllowLoadLocalInfile=true）：MySqlBulkCopy 走的是 LOAD DATA LOCAL，缺这颗参数驱动会在执行当场拒";

        static bool Read(DbConnectionStringBuilder builder, string key)
        {
            if (!builder.TryGetValue(key, out var value))
                return false;

            if (value is not string text)
                return value is true;

            return bool.TryParse(text, out var parsed)
                ? parsed
                : text.Equals("1", StringComparison.OrdinalIgnoreCase)
                  || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                  || text.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
    }

    [RequiresUnreferencedCode("本对象的 MemberInfo 全部来自按类型名的反射查找（TryResolve 那一句），裁剪后的驱动走不到这里。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射调用装载类需要运行时生成代码。")]
    public async Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct)
    {
        // 反射调用不认 C# 可省参数的默认值：实参数组要铺满签名。多出来那一位是外部事务，
        // 交 null 就是"没有外部事务"那一格。
        var arguments = new object?[_bulkCtor.GetParameters().Length];

        arguments[0] = target.Connection;

        if (arguments.Length > 1) arguments[1] = target.Transaction;

        var bulk = _bulkCtor.Invoke(arguments);

        _destinationTable.SetValue(bulk, request.DestinationTable);
        _timeout.SetValue(bulk, target.TimeoutSeconds);

        var mappings = _mappings.GetValue(bulk)!;

        for (var column = 0; column < request.Columns.Count; column++)
        {
            // 映射类的第三位是"由表达式算列值"档；本仓库永远交值 ⇒ null。
            var cells = new object?[_mappingCtor.GetParameters().Length];

            cells[0] = column;
            cells[1] = request.Columns[column].ColumnName;

            _addMapping.Invoke(mappings, [_mappingCtor.Invoke(cells)!]);
        }

        var outcome = _write.Invoke(bulk, [Table(request), ct])!;

        // ValueTask 只能在生成它的那一帧上等，而这里恰好就是那一帧；AsTask() 交出的 Task 与直返 Task 同一个 await 形状。
        await (outcome as Task ?? (Task) _asTask!.Invoke(outcome, null)!);

        // RowsCopied 是这一族唯一的"进了几行"，给不出数的形状在 TryResolve 那里已经整家退掉了。
        return checked((int) Convert.ToInt64(_rowsCopied.GetValue(bulk), CultureInfo.InvariantCulture));

        // 这里没有收尾的 finally：这一族的装载类没有 Dispose 面，连接与事务的生命周期在内核那条 using 上。
    }

    /// <summary>这一列在装载通道里的 CLR 形状——白名单与取型同一次判断，判法与 SqlServerBulkCopy.NetType 同一把尺：
    /// 缺席的 char/DateOnly/TimeOnly/无符号/object 五类一颗不新增（缺席与驱动无关，是 DataTable 类型面的形状）；
    /// 枚举按底层整数判、可空剥壳。</summary>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    static Type? NetType(ColumnContract column) => Flatten(column.ClrType) switch
    {
        var t when t == typeof(bool) => typeof(bool),
        var t when t == typeof(byte) => typeof(byte),
        var t when t == typeof(short) => typeof(short),
        var t when t == typeof(int) => typeof(int),
        var t when t == typeof(long) => typeof(long),
        var t when t == typeof(float) => typeof(float),
        var t when t == typeof(double) => typeof(double),
        var t when t == typeof(decimal) => typeof(decimal),
        var t when t == typeof(string) => typeof(string),
        var t when t == typeof(byte[]) => typeof(byte[]),
        var t when t == typeof(Guid) => typeof(Guid),
        var t when t == typeof(DateTime) => typeof(DateTime),
        var t when t == typeof(DateTimeOffset) => typeof(DateTimeOffset),
        var t when t == typeof(TimeSpan) => typeof(TimeSpan),
        _ => null,
    };

    static Type Flatten(Type clrType)
    {
        var member = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return member.IsEnum ? Enum.GetUnderlyingType(member) : member;
    }

    static DataTable Table(BulkCopyRequest request)
    {
        var table = new DataTable("QuickSqlBulk");

        foreach (var column in request.Columns)
            table.Columns.Add(column.ColumnName, NetType(column)!);

        foreach (var row in request.Rows)
            table.Rows.Add(row);

        return table;
    }

    /// <summary>整套验一遍，缺任何一件交回 null（阶梯从 L3 起，与"这一家没有通道"同形）。
    /// 两颗构造子按反射面点死而不是按 C# 调用面：必填位类型要对，多出来的一位必须是可省参数（IsOptional）——
    /// 写死"只收连接的一位"在这一家永远点空，而那个失败形状与"根本没有装载类"完全同形。</summary>
    [RequiresUnreferencedCode("按类型名在驱动程序集里找装载类与它的成员（TryResolve 本体），被裁剪的驱动会交回 null。")]
    [RequiresDynamicCode("反射调用装载类需要运行时生成代码。")]
    internal static MySqlBulkCopyChannel? TryResolve(Assembly driver)
    {
        var type = driver.GetType("MySqlConnector.MySqlBulkCopy", throwOnError: false);
        var mapping = driver.GetType("MySqlConnector.MySqlBulkCopyColumnMapping", throwOnError: false);

        if (type is null || mapping is null)
            return null;

        var ctor = ConnectionScopedCtor(type);
        var mappingCtor = OrdinalColumnCtor(mapping);
        var destinationTable = type.GetProperty("DestinationTableName");
        var timeout = type.GetProperty("BulkCopyTimeout");
        var mappings = type.GetProperty("ColumnMappings");
        var rowsCopied = type.GetProperty("RowsCopied");
        var write = type.GetMethod("WriteToServerAsync", [typeof(DataTable), typeof(CancellationToken)]);

        if (ctor is null || mappingCtor is null || destinationTable is null || timeout is null
            || mappings is null || rowsCopied is null || write is null)
            return null;

        // 那颗带取消的出口交回 ValueTask 而不是 Task：要么直接可等，要么给得出 AsTask()，
        // 两者都给不出整家退 L3——"等不到回包"比"少一次收尾"严重（进了几行从此没有证人）。
        MethodInfo? asTask = null;

        if (write.ReturnType != typeof(Task))
        {
            asTask = write.ReturnType.GetMethod("AsTask", Type.EmptyTypes);

            if (asTask is null || !typeof(Task).IsAssignableFrom(asTask.ReturnType))
                return null;
        }

        // Add 收的是映射对象本身（ColumnMappings 是 List<映射类>），不是 T-SQL 收两个字符串的写法。
        var add = mappings.PropertyType.GetMethod("Add", [mapping]);

        return add is null ? null : new MySqlBulkCopyChannel(ctor, mappingCtor, destinationTable, timeout, mappings, rowsCopied, add, write, asTask);

        // 「自家连接 + 可选的外部事务」：第一位收连接子类（通道跟着工厂走），第二位存在时必须可省且是事务子类。
        static ConstructorInfo? ConnectionScopedCtor(Type bulkType)
        {
            foreach (var candidate in bulkType.GetConstructors())
            {
                var parameters = candidate.GetParameters();

                if (parameters.Length is not (1 or 2)) continue;

                if (parameters[0].ParameterType.IsSubclassOf(typeof(DbConnection))
                    && (parameters.Length == 1
                        || parameters[1].IsOptional && parameters[1].ParameterType.IsSubclassOf(typeof(DbTransaction))))
                    return candidate;
            }

            return null;
        }

        // 「序号 + 目标列名（+ 可选的表达式位）」；表达式位存在时交 null，判据与上面那颗同一把尺。
        static ConstructorInfo? OrdinalColumnCtor(Type mappingType)
        {
            foreach (var candidate in mappingType.GetConstructors())
            {
                var parameters = candidate.GetParameters();

                if (parameters.Length is not (2 or 3)) continue;

                if (parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(string)
                    && (parameters.Length == 2
                        || parameters[2].IsOptional && parameters[2].ParameterType == typeof(string)))
                    return candidate;
            }

            return null;
        }
    }
}
