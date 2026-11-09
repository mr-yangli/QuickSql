using System.Globalization;
using System.Text;
using QuickSql.Aop;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Write;

/// <summary>写侧三条链共用的发射段。只抽「AST → 文本 → 参数改写」这两步：分块、列合成、取值是各链自己的事，
/// 而这一步三家逐字相同。留成多份的代价不是行数——是钩子回放会在某一条链上悄悄缺失。</summary>
static class StatementEmitter
{
    /// <summary>provider 没给参数预算时的保守值。没有任何库种事实可依，取一个低于所有已知上限的数：
    /// 宁可多几次 round-trip，也不要撞上一次就整批失败的硬上限。SQL Server 走不到这条（它自己声明了 2100）。</summary>
    public const int ConservativeParameterBudget = 100;

    /// <summary>分块公式，两处调用共用一份：批量 INSERT 的多值语句与合并 UPDATE。
    /// 留十分之一给文本本身（表名/列名/OUTPUT/ON），再除以每行参数个数。
    /// 分母是"每行几颗参数"而不是"每行几列"——两者在 INSERT 上恰好相等，在合并 UPDATE 上不等
    /// （键列不进 SET 却进 VALUES）。</summary>
    public static int RowsPerStatement(int budget, int parametersPerRow)
        => Math.Max(1, budget * 9 / 10 / parametersPerRow);

    /// <summary>只用于 StringBuilder 的预容量：每颗参数按 "@p123, " 估 7 个字符。</summary>
    public static int EstimateLength(int parameters, int identifierChars) => 96 + parameters * 7 + identifierChars;

    /// <summary>"这一颗键等于没给"的判据：数值零、Guid.Empty 与空串三种。只在主键由库给值时才问它
    /// （非自增主键的 0 是一颗合法键，那种现场不拒）；日期与枚举一类的键落不进这三种，一律放行。
    /// 按主键定位的三条路（UpdateRange 的逐行键、upsert 的冲突键、按实体 UPDATE 的 WHERE）共用这一份：
    /// 判据分成三份时，"哪一格算没填"会在某一条路上悄悄和另外两条不一样。</summary>
    public static bool IsUnassignedKey(object value) => value switch
    {
        Guid guid => guid == Guid.Empty,
        string text => text.Length == 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal or double or float =>
            Convert.ToDouble(value) == 0d,
        _ => false,
    };

    public static SqlWriter Writer(ISqlDialect dialect, int capacity)
        => new(new StringBuilder(capacity), [], dialect.ParameterPrefix);

    /// <summary>删除那一档的发射：发 DELETE 还是发软删改写出来的 UPDATE，在这一颗里分岔，
    /// 三家（链式 Delete、批量 DeleteRange、跟踪器的摘行）共用这一份。分岔放在发射之前：
    /// 形状在 AST 上换，文本仍由方言 Emit 出，本家 UPDATE 的语法、参数前缀与参数登记顺序一处都不必重算，
    /// 而改写钩子拿到的已是改写后的那一句。soft 为 null 时物理删除那条路径不为此多付一分钱；
    /// parameters 是调用方对"这一句几颗参数"的预容量估算，软删那一支另按 SET 的列数加进去。</summary>
    public static DialectSql EmitDelete(ISqlDialect dialect, DeleteStatement delete, UpdateStatement? soft, int identifierChars, int parameters = 0)
    {
        var writer = Writer(dialect,
            EstimateLength(parameters + (soft?.Sets.Count ?? 0), identifierChars + (soft?.Sets.Sum(s => s.Column.ColumnName.Length) ?? 0)));

        if (soft is { } update)
            dialect.Emit(in update, writer);
        else
            dialect.Emit(in delete, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    /// <summary>CudBuilding 的发射点：写侧三条链与跟踪器的两个发射口都走这里，位置在 AST 已经变成文本之后、
    /// ApplyBinding 之前——这一个钩子改的是整句文本（影子表、全局注释、调用方自己的整句改写），
    /// 要看到完整句子才有得改；参数级改写必须在它之后，否则前一个钩子新加的参数没人过钩子。
    /// 库里那颗预置的软删不在这里（它在 EmitDelete 之前换的是 AST），走到这一格的文本已是改写后的那一句。
    /// "按注册顺序串联、后一个能看到前一个的结果"靠逐个派发实现：多播调用会把同一个事件对象递给每个订阅者，
    /// 各自写 Rewritten 就成了最后一个人赢，所以展开调用列表、每人一份事件。改写类钩子抛异常直接失败，不吞。</summary>
    public static DialectSql ApplyCud(QuickSqlOrm orm, DialectSql sql, string operationKind, List<HookTrace> traces, ref int step)
    {
        var cud = orm.Aop.CudBuilding;

        if (cud is null)
            return sql;

        var current = sql;
        var hooks = cud.GetInvocationList();

        for (var i = 0; i < hooks.Length; i++)
        {
            var @event = new CudBuildingEvent { OperationKind = operationKind, Sql = current };

            ((Action<CudBuildingEvent>) hooks[i])(@event);

            var next = @event.Rewritten ?? current;

            if (next.Equals(current))
                continue;

            traces.Add(new HookTrace(step++, "CudBuilding", HookCategory.Rewrite, current.Text, next.Text));
            current = next;
        }

        return current;
    }

    /// <summary>ParameterBinding 的发射点。在这里而不是执行层 bind 之前：回放要求 Explain() 能看到每一颗参数
    /// 被改成了什么，而执行层改的参数在 Explain() 里根本不存在。代价是读侧的参数不过这道钩子（已知缺口）。</summary>
    public static DialectSql ApplyBinding(QuickSqlOrm orm, DialectSql sql, List<HookTrace> traces, ref int step)
    {
        var bind = orm.Aop.ParameterBinding;

        if (bind is null || sql.Parameters.Count == 0)
            return sql;

        AdoParameter[]? rewritten = null;

        for (var i = 0; i < sql.Parameters.Count; i++)
        {
            var parameter = sql.Parameters[i];
            var @event = new ParameterBindingEvent(parameter, parameter.Hint);

            bind(@event);

            var next = @event.Parameter;

            if (next.Equals(parameter))
                continue;

            traces.Add(new HookTrace(step++, "ParameterBinding", HookCategory.Rewrite, $"{parameter.Name} = {Text(parameter.Value)}", $"{next.Name} = {Text(next.Value)}"));

            // 整表拷一次而不是逐格补：改写多半不止一颗，逐格回填循环在宽表上是 n²。
            rewritten ??= [.. sql.Parameters];
            rewritten[i] = next;
        }

        return rewritten is null ? sql : new DialectSql(sql.Text, rewritten);
    }

    /// <summary>值的可读文本——钩子回放与冲突消息里那几颗值都从这里落地。二进制单独一档：
    /// byte[].ToString() 交的是类型名，「会话以为是 System.Byte[]，库里是 System.Byte[]」会把消息的全部用处抹掉
    /// （要能看出哪一列、差在哪）。形状对齐库里的写法：0x + 大写十六进制。</summary>
    public static string Text(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
    };
}
