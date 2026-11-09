using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using QuickSql.Metadata;

namespace QuickSql.Providers.SqlServer;

/// <summary>L1「批量装载通道」在 T-SQL 那一家的落地：SqlBulkCopy。
/// 用反射而不是直接 new，是因为装载类不在 DbProviderFactory 的抽象面上、provider 程序集不许认识
/// Microsoft.Data.SqlClient；类型从注入的那只工厂所在的程序集里按名字取——现场同时装两家驱动时，
/// 跟着工厂走是唯一可能对的那个答案。数据载体用 DataTable 而不是自写 IDataReader 包装
/// （WriteToServer(IDataReader) 读 reader 的方式不是公开契约，DataTable 那条才是稳定面）；
/// 递交的两份代价（每格多一次装箱、整批一份 DataTable）记在 Describe 与 BulkReport.Reason 里而不藏起来。</summary>
sealed class SqlServerBulkCopy : IBulkCopyChannel
{
    readonly object _options;
    readonly ConstructorInfo _ctor;
    readonly PropertyInfo _destination;
    readonly PropertyInfo _timeout;
    readonly PropertyInfo _mappings;
    readonly PropertyInfo _rowsCopied;
    readonly MethodInfo _addMapping;
    readonly MethodInfo _write;

    SqlServerBulkCopy(
        object options,
        ConstructorInfo ctor,
        PropertyInfo destination,
        PropertyInfo timeout,
        PropertyInfo mappings,
        MethodInfo addMapping,
        PropertyInfo rowsCopied,
        MethodInfo write
    )
    {
        _options = options;
        _ctor = ctor;
        _destination = destination;
        _timeout = timeout;
        _mappings = mappings;
        _addMapping = addMapping;
        _rowsCopied = rowsCopied;
        _write = write;
    }

    public bool SupportsColumn(ColumnContract column) => NetType(column) is not null;

    [RequiresUnreferencedCode(
        "本对象的 MemberInfo 全部来自按类型名的反射查找（TryResolve 那一句），裁剪后的驱动走不到这里。"
    )]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射调用装载类需要运行时生成代码。")]
    public async Task<int> WriteAsync(
        BulkConnection target,
        BulkCopyRequest request,
        CancellationToken ct
    )
    {
        // 构造子只认这三件：连接、开关、外部事务。事务交 null 也是对的形状（那一批归连接自己的隐式事务）。
        var bulk = _ctor.Invoke([target.Connection, _options, target.Transaction]);

        _destination.SetValue(bulk, request.DestinationTable);
        _timeout.SetValue(bulk, target.TimeoutSeconds);

        // 按名字映射，一次都不许省：不给 ColumnMappings 时 SqlBulkCopy 按【序号】对齐，
        // 而我们递交的是列清单那一个子集（自增列与并发列不在里面）——序号对齐会把整批写歪，而且歪得很绿。
        var mappings = _mappings.GetValue(bulk)!;

        for (var column = 0; column < request.Columns.Count; column++)
        {
            var name = request.Columns[column].ColumnName;

            // 源名与目标名同一个串：源那一侧就是 DataTable 的列名，而它取自 ColumnContract.ColumnName，
            // 与文本层用的是同一颗名字。
            _addMapping.Invoke(mappings, [name, name]);
        }

        try
        {
            await (Task)_write.Invoke(bulk, [Table(request), ct])!;

            // RowsCopied 是这一族唯一的"进了几行"。它不存在时 TryResolve 已经交回 null（整家没有通道），
            // 所以走到这里就一定拿得出数——而拿不出数的那一种宁可没有通道，也不报一个看着对的数。
            return checked(
                (int)Convert.ToInt64(_rowsCopied.GetValue(bulk), CultureInfo.InvariantCulture)
            );
        }
        finally
        {
            ((IDisposable)bulk).Dispose();
        }
    }

    /// <summary>这一列在装载通道里的 CLR 形状——白名单与取型是同一次判断：点得到名的才交回类型，
    /// 点不到的交回 null（整批退回 L3）。缺席的三类各有理由、都不是"忘了"：char（值门给 DbType.String
    /// 加 Size=1，与 DataTable 那侧的 System.Char 不是同一个说法）、DateOnly/TimeOnly（DataTable 的
    /// 类型面里没有这两位）、无符号族与 object（T-SQL 侧没有对应存储类型）；枚举按底层整数判——值门
    /// 已经折成整数，拿 ClrType 原样判会把每一条带枚举的批量白退一次。名单写成一颗颗 typeof 而不是数组，
    /// 是因为 DataColumnCollection.Add 的参数带 DynamicallyAccessedMembers、分析器只认编译期看得见的
    /// typeof 这一种证据（数组元素在它眼里是无源的，IL2073），白名单因此同时是"裁剪器要保住的那几张类型"的清单。</summary>
    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicFields
            | DynamicallyAccessedMemberTypes.PublicProperties
    )]
    static Type? NetType(ColumnContract column) =>
        Flatten(column.ClrType) switch
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

    /// <summary>整批的载体。列类型取 NetType（值门产出的那个形状），值已经是 DBNull 或实值——
    /// DataTable 允许 DBNull 进任何列，而 NOT NULL 那一列上库里会自己红，这一侧不替它兜。</summary>
    static DataTable Table(BulkCopyRequest request)
    {
        var table = new DataTable("QuickSqlBulk");

        foreach (var column in request.Columns)
            table.Columns.Add(column.ColumnName, NetType(column)!);

        foreach (var row in request.Rows)
            table.Rows.Add(row);

        return table;
    }

    /// <summary>把这一家的装载类整套验一遍，缺任何一件交回 null：阶梯自然从 L3 起，与"这一家没有通道"同形。
    /// 宁缺毋滥的那一半是开关：少了 CheckConstraints 检查约束不验、少了 FireTriggers 插入触发器不跑，
    /// 两者都会让"批量"与"逐条"在库里不等价，而这一档的默认必须是等价的那一个。
    /// 数值一个都没写死：两个开关的位是从驱动的枚举上按名字读的。</summary>
    [RequiresUnreferencedCode(
        "按类型名在驱动程序集里找装载类与它的成员（TryResolve 本体），被裁剪的驱动会交回 null。"
    )]
    [RequiresDynamicCode("反射调用装载类需要运行时生成代码。")]
    internal static SqlServerBulkCopy? TryResolve(Assembly driver)
    {
        var type =
            driver.GetType("Microsoft.Data.SqlClient.SqlBulkCopy", throwOnError: false)
            ?? driver.GetType("System.Data.SqlClient.SqlBulkCopy", throwOnError: false);

        if (type is null || type.Namespace is not { Length: > 0 } ns)
            return null;

        var options = driver.GetType(ns + ".SqlBulkCopyOptions", throwOnError: false);

        if (options is null || Flags(options) is not { } flags)
            return null;

        var ctor = ConnectionScopedCtor(type, options);
        var destination = type.GetProperty("DestinationTableName");
        var timeout = type.GetProperty("BulkCopyTimeout");
        var mappings = type.GetProperty("ColumnMappings");
        var rowsCopied = type.GetProperty("RowsCopied");
        var write = type.GetMethod(
            "WriteToServerAsync",
            [typeof(DataTable), typeof(CancellationToken)]
        );

        if (
            ctor is null
            || destination is null
            || timeout is null
            || mappings is null
            || rowsCopied is null
            || write is null
        )
            return null;

        var add = mappings.PropertyType.GetMethod("Add", [typeof(string), typeof(string)]);

        return add is null
            ? null
            : new SqlServerBulkCopy(
                flags,
                ctor,
                destination,
                timeout,
                mappings,
                add,
                rowsCopied,
                write
            );

        // 那一颗「收连接、开关、外部事务」的构造子：按形状找，两家都能找到——
        // System.Data.SqlClient 收 (DbConnection, options, DbTransaction)，而 Microsoft.Data.SqlClient
        // 收的是同族的具体类型 (SqlConnection, options, SqlTransaction)。写死 typeof(DbConnection) 去点
        // 后面那一家会当场点空、整家退到 L3，而那个失败形状与"这一家根本没有装载类"完全同形——看不见也修不了。
        // 收具体类型对我们只会更严：只有本家的真连接进得去，而那正是"通道跟着工厂走"要保证的那件事。
        static ConstructorInfo? ConnectionScopedCtor(Type type, Type options)
        {
            foreach (var candidate in type.GetConstructors())
            {
                var parameters = candidate.GetParameters();

                if (
                    parameters.Length == 3
                    && parameters[0].ParameterType.IsSubclassOf(typeof(DbConnection))
                    && parameters[1].ParameterType == options
                    && parameters[2].ParameterType.IsSubclassOf(typeof(DbTransaction))
                )
                    return candidate;
            }

            return null;
        }

        static object? Flags(Type enumeration)
        {
            var check = Value(enumeration, "CheckConstraints");
            var fire = Value(enumeration, "FireTriggers");

            // 位是从驱动自己的枚举读的，不是这里写死的数：把 20 这种数字抄进来就等于赌
            // "两家、所有版本的位序相同"，而这个赌注没有复点可付。
            return check is null || fire is null
                ? null
                : Enum.ToObject(
                    enumeration,
                    Convert.ToInt64(check, CultureInfo.InvariantCulture)
                        | Convert.ToInt64(fire, CultureInfo.InvariantCulture)
                );

            static object? Value(Type enumeration, string name) =>
                enumeration.GetField(name)?.GetValue(null);
        }
    }
}
