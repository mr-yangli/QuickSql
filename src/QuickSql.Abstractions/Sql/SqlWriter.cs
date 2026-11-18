using QuickSql.Metadata;
using QuickSql.Values;
using System.Data;
using System.Text;

namespace QuickSql.Sql;

/// <summary>一条参数。Hint 不是给驱动用的（驱动只认 Name/Value/DbType/Size），它是参数自带的出处：
/// binder 本来就收了列契约再决定 DbType，扔掉之后"这一颗参数属于哪一列"就只能靠
/// "参数按 AST 顺序生成"这种隐含约定重建——一旦有方言在 Emit 里多插一颗参数（分页 hint、并发谓词），
/// 按位置配对的 ParameterBinding 钩子就会把值改到隔壁列上，那是"三个月后才现形"的错，
/// 所以出处必须跟着参数走。DriverName 默认 null 说的是"provider 没有表态"，此时执行器按方言那颗
/// DriverAcceptsPrefixedParameterNames 决定摘不摘前缀；非 null 就是点名交给驱动的名字，
/// 执行器一个字都不再动（今天五家 binder 全发 null、消费点只有执行器一处，是留口的形状）。</summary>
public sealed record AdoParameter(string Name, object? Value, DbType DbType, int Size, ColumnContract? Hint = null, string? DriverName = null);

/// <summary>
/// 方言的书写面。所有 provider 只通过这个类型落地文本，因此 Emit 里不可能
/// 出现字符串拼接风格之外的第二套写法。
/// 这是 ref struct：把它改成 class 会让每个方言签名破功。
/// </summary>
public ref struct SqlWriter
{
    const string EscapedQuote = "''";

    readonly StringBuilder _builder;
    readonly ParameterTable? _table;
    readonly List<AdoParameter>? _legacy;
    readonly char _parameterPrefix;
    readonly Func<int, string>? _namer;

    public SqlWriter(StringBuilder builder, List<AdoParameter> parameters, char parameterPrefix, Func<int, string>? namer = null)
    {
        _builder = builder;
        _legacy = parameters;
        _parameterPrefix = parameterPrefix;
        _namer = namer;
    }

    public SqlWriter(StringBuilder builder, ParameterTable parameters, char parameterPrefix, Func<int, string>? namer = null)
    {
        _builder = builder;
        _table = parameters;
        _parameterPrefix = parameterPrefix;
        _namer = namer;
    }

    /// <summary>小表构造口（查询/单行写等参数颗数小的路）：照旧窄引用 List + 每格 record——
    /// 宽 struct 表的最小数组与倍增垃圾在颗数小时反而比窄表贵（净室探针 obj/factspath 账），
    /// 小表不换了；批量热路径走下面给容量的构造口或显式 ParameterTable。</summary>
    public SqlWriter(StringBuilder builder, char parameterPrefix, Func<int, string>? namer = null)
        : this(builder, new List<AdoParameter>(), parameterPrefix, namer)
    {
    }

    /// <summary>给准参数容量的批量热路径构造口（块行数×列数）：宽 struct 表零扩容、零每格 record
    /// （见 ParameterTable 抬头探针账）。record 只在钩子/日志/公开消费读表时懒物化。</summary>
    public SqlWriter(StringBuilder builder, int parameterCapacity, char parameterPrefix, Func<int, string>? namer = null)
        : this(builder, new ParameterTable(parameterCapacity), parameterPrefix, namer)
    {
    }

    public readonly int Length => _builder.Length;

    public IReadOnlyList<AdoParameter> Parameters => (IReadOnlyList<AdoParameter>?)_table ?? _legacy!;

    /// <summary>派生一个临时书写器：文本各自一份，参数表与编号共享（编号本来就长在这张表上，
    /// 见 NextParameterName），所以两侧不可能生成同名参数。</summary>
    public SqlWriter Fork() => _table is { } table
        ? new(new StringBuilder(), table, _parameterPrefix, _namer)
        : new(new StringBuilder(), _legacy!, _parameterPrefix, _namer);

    public SqlWriter Append(string text)
    {
        _builder.Append(text);
        return this;
    }

    public SqlWriter Append(ReadOnlySpan<char> text)
    {
        _builder.Append(text);
        return this;
    }

    public SqlWriter Append(char ch)
    {
        _builder.Append(ch);
        return this;
    }

    public SqlWriter Append(int value)
    {
        _builder.Append(value);
        return this;
    }

    public SqlWriter AppendSpace()
    {
        _builder.Append(' ');
        return this;
    }

    /// <summary>标识符的唯一落地口径：判据、转义与大小写折叠都在 IdentifierPolicy 那一份，
    /// 这里只多给了它一个缓冲区，不让 [t0].[Code1] 先作为一个中间 string 存在。
    /// 发射层每一列都要过一次这条路，那一次拼接就是每列分配里的大头——
    /// 也是 NeedsQuote 那一判为什么不许顺手跑一趟大写映射的理由。</summary>
    public SqlWriter AppendIdentifier(IdentifierPolicy identifiers, string identifier)
    {
        if (identifiers.NeedsQuote(identifier))
            identifiers.AppendQuoted(_builder, identifier);
        else
            identifiers.AppendPlain(_builder, identifier);

        return this;
    }

    /// <summary>字面量字符串。SQL Server 在 cp1252 排序规则下会把非 ASCII 静默变成问号，
    /// 所以 unicode 必须由列契约的 Unicode 决定，不能默认 false。
    /// backslashIsEscape 是 MySQL 那一族的发射档：缺省 sql_mode 下反斜杠在字符串里是转义符，
    /// 注释原文里一颗路径会静默换形，而收尾那颗尾随反斜杠更把闭合引号转义掉——字符串无处闭合，
    /// ALTER 当场语法错（数据值不走这一条：全在参数里，这一档只发生在 DDL 注释那种
    /// "契约文本直接排版进文本"的发射点）。带这一档的发射点把反斜杠翻倍
    /// （引号的翻倍照旧——引号翻倍两种 sql_mode 下都合法）；代价如实记：会话若显式开了
    /// NO_BACKSLASH_ESCAPES，翻倍读回来是两颗反斜杠——显示差一颗与"ALTER 炸掉"之间取前者。</summary>
    public SqlWriter AppendStringLiteral(string value, bool unicode, bool backslashIsEscape = false)
    {
        if (unicode)
            _builder.Append('N');

        _builder.Append('\'');

        var start = 0;

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];

            if (current != '\'' && !(backslashIsEscape && current == '\\'))
                continue;

            _builder.Append(value, start, i - start);
            _builder.Append(current).Append(current);
            start = i + 1;
        }

        _builder.Append(value, start, value.Length - start);
        _builder.Append('\'');
        return this;
    }

    /// <summary>参数的编号就是它在 Parameters 里的下标，而不是书写器自持的一个计数器。
    /// 这不是省事：类型是 ref struct，方言的各 Emit 方法之间按值传递它，字段上的自增在离开
    /// 被调方时就随副本一起消失——于是 OFFSET 与 FETCH NEXT 两处同名，而参数集合里只有一条；
    /// 文本与参数表是引用类型所以活得下来，只有号码会分岔，因此号码必须长在那张共享表上。
    /// 名字只在参数真正进表的那一刻算被占用，连续调用两次会得到同一个名字。
    /// <para>命名方案由 <c>_namer</c> 决定：null 走默认的「前缀 + p + 下标」具名形；非 null 是方言点名的
    /// 编号方案（SQLite 批量插入用「? + (下标+1)」编号位置形，避开 Microsoft.Data.Sqlite 按名查找的平方价）。
    /// 编号 = 发射序 = 绑定序，三者同源于这张共享表的下标，所以位置形按序绑定不会串位。</para></summary>
    public string NextParameterName() => _namer is null ? _parameterPrefix + "p" + ParameterCount : _namer(ParameterCount);

    int ParameterCount => _table?.Count ?? _legacy!.Count;

    public void AddParameter(string name, object? value, DbType dbType = DbType.Object, int size = 0)
    {
        if (_table is { } table) table.Add(name, new ParameterFacts(value, dbType, size), null);
        else _legacy!.Add(new AdoParameter(name, value, dbType, size));
    }

    public void AddParameter(object? value, DbType dbType = DbType.Object, int size = 0)
        => AddParameter(NextParameterName(), value, dbType, size);

    /// <summary>登记参数并落地它的占位符名。方言里所有参数化都必须走这一条，
    /// 否则会出现"文本里有 @p3、参数集合里没有"这类只能靠执行期报错发现的问题。</summary>
    public SqlWriter AppendParameter(AdoParameter parameter)
    {
        if (_table is { } table) table.Add(parameter); else _legacy!.Add(parameter);
        _builder.Append(parameter.Name);
        return this;
    }

    /// <summary> AppendParameter 的零 record 快路径：事实由 <see cref="Values.IValueBinder.FactsOf"/> 直取
    /// （struct，不分配），名字与 hint 存在 entry 上；record 只在钩子/日志/公开消费读表时懒物化。
    /// 小表（legacy）档没有宽表可存事实，照旧落一颗 record——与改判前逐字同价。</summary>
    public SqlWriter AppendParameter(string name, in ParameterFacts facts, ColumnContract? hint)
    {
        if (_table is { } table) table.Add(name, facts, hint);
        else _legacy!.Add(new AdoParameter(name, facts.Value, facts.DbType, facts.Size, hint, facts.DriverName));
        _builder.Append(name);
        return this;
    }

    /// <summary>登记参数而不落地它的名字：位置形占位符（问号）那一家文本里没有名字可落，
    /// 而那颗名字仍然要存在——它是内核侧的编号、是 AdoParameter.Hint 的载体，也是
    /// ParameterBinding 钩子按列对上号的唯一依据（本文件抬头那句"出处必须跟着参数走"）。
    /// 与 AppendParameter 同一张表、同一个编号纪律：调用方仍要先问 NextParameterName，
    /// 否则两次登记会同名。名字与文本脱钩之后，"绑定按先后配"这件事第一次变成了方言层的责任，
    /// 所以 DialectBase 在发射函数模板时会核对实参次序（那里写了后果）。</summary>
    public SqlWriter RegisterParameter(AdoParameter parameter)
    {
        if (_table is { } table) table.Add(parameter); else _legacy!.Add(parameter);
        return this;
    }

    /// <summary>RegisterParameter 的零 record 快路径（位置形占位符那一家）。</summary>
    public SqlWriter RegisterParameter(string name, in ParameterFacts facts, ColumnContract? hint)
    {
        if (_table is { } table) table.Add(name, facts, hint);
        else _legacy!.Add(new AdoParameter(name, facts.Value, facts.DbType, facts.Size, hint, facts.DriverName));
        return this;
    }

    public readonly string ToSqlString() => _builder.ToString();

    public override readonly string ToString() => _builder.ToString();
}
