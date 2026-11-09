using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的 SQL 文本层。分页落在句尾且两子句独立可缺省（LIMIT n OFFSET m，
/// VendorDoc·TDengine：SELECT 语法页）；恒假/恒真保持基类的 1 = 0/1 = 1——本家布尔字面量在
/// SELECT 骨架里认不认没有读数，算术谓词是哪一家都收的那一形；布尔列的判定等式落 = true/= false
///（BOOL 是本家原生型，谓词与列型同边）；UPDATE/DELETE 整格拒发——3.x 没有传统 UPDATE 语句
///（VendorDoc·TDengine：变更只有 INSERT 覆盖与 ALTER 那两族），DELETE 的官方形状只认
/// "首列时间窗收束成一个确定闭区间"的条件（VendorDoc·TDengine：DELETE 页），时间窗分析这一格
/// 本层没有，宁可当场红也不发一条猜出来的条件；SupportsJoinedWrites 跟着写格落 false。
/// EmitReturning 转交 TDengineKeys.EmitReturning（那里抛）。稳定表面（v0.39 第六笔）：
/// 契约带 <see cref="EntityContract.Stable"/> 的插入走整句覆写——INSERT INTO 子表 USING 稳定
/// [TAGS 名清单] TAGS (值) (数据列) VALUES (…)（VendorDoc·TDengine：插入页），子表名由
/// Subtable 模板填行内 TAG 值解析，TAG 值一律内联（STMT2 的参数化自动建表有明文行为缺口，
/// 子表不存在时报错【VendorDoc·TDengine：stmt 页】），同批行 TAG 值一致性当场校验
///（一条 INSERT … USING 只服务一颗子表）；普通实体（Stable 为空）走基类原形。</summary>
public sealed class TDengineDialect(TDengineTypes types, TDengineKeys keys, TDengineValues values, TDengineExpressions expressions)
    : DialectBase(IdentifierPolicy.TDengine, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.TDengine;

    /// <summary>问号，且是唯一的一支。这是本文件里最贵的一格：前缀错了在别家红在"名字对不上"，
    /// 在这一家红在值绑定到隔壁列（位置形没有第二道校验）。驱动侧的另一半在 binder：stmt2 按
    /// ParameterName 前缀给值分类，binder 用 DriverName 把 '?p0' 翻成 '@p0'——文本、编号、日志
    /// 都还是位置形那一套，只有跨进驱动对象那一步换名。</summary>
    public override char ParameterPrefix => '?';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>合并档开关跟着写格落 false：UPDATE/DELETE 两整格都抛，读侧 EntityTracker 走"一句一实体"那一支；
    /// 合并那一档在这颗开关 false 时走不到默认出口，所以 EmitJoinedSources 不覆写。
    /// 恒假/恒真也不覆写：基类默认 1 = 0/1 = 1——布尔字面量 true/false 在本家 SELECT 骨架里
    /// 收不收没有读数，算术谓词是哪一家都收的那一形，"没有"要说成"没有"。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>布尔是本家的原生型，与列那一侧 BOOL 同边：判定等式落 = true/= false。
    /// VendorDoc·TDengine：BOOL 型页与 SELECT 示例里布尔列与 true/false 直接比等。</summary>
    protected override void EmitBooleanEquality(SqlWriter writer, bool value)
        => writer.Append(value ? " = true" : " = false");

    /// <summary>本家没有句首限定词那一档：LIMIT/OFFSET 都在句尾（VendorDoc·TDengine：
    /// SELECT 语法页 LIMIT [n OFFSET m]）。两子句独立可缺省。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is { } limit)
        {
            writer.Append(" LIMIT ");

            EmitExpression(limit, writer);
        }

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");

            EmitExpression(offset, writer);
        }
    }

    /// <summary>登记参数而不落地它的名字，只落一颗 ?——位置形与 GBase 8s 同一形状。顺序纪律：
    /// 先登记、后落文本——文本序 = 编号序 = 绑定序，这条耦合在本家更硬：stmt2 按
    /// ParameterCollection 的顺序 BindRow，没有第二道名字校验可兜。</summary>
    protected override void EmitParameter(ParameterValue parameter, SqlWriter writer)
    {
        writer.RegisterParameter(Values.CreateParameter(writer.NextParameterName(), parameter.Value, parameter.Hint));

        writer.Append('?');
    }

    /// <summary>转交 TDengineKeys.EmitReturning（那里抛）。与 GBase 8s 同一立场：宁可当场红，
    /// 也不发一条"看着像在回填"的语句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>变异档：3.x 没有传统 UPDATE 语句（VendorDoc·TDengine：数据变更只有
    /// INSERT 的 upsert 覆盖与 ALTER 那两族）。同 ts 重写覆盖旧行确实像天然 upsert，
    /// 但"重复时间戳的确切语义（整行覆盖还是部分列）"要活库钉死才敢接进 InsertOrUpdate，
    /// 重开面记 notes——这一格今天整格抛。</summary>
    public override void Emit(in UpdateStatement update, SqlWriter writer)
        => throw new QuickSqlException(
            "TDengine 没有 UPDATE 语句：3.x 的数据变更只有 INSERT 的 upsert 覆盖与 ALTER 那两族"
            + "【文档·TDengine】，本仓库今天不发变异语句，所以这里整格抛。"
            + "InsertOrUpdate 那一格也没有接：INSERT 同 ts 覆盖的语义要活库钉死才敢翻。");

    /// <summary>同上一格：DELETE 的官方形状只认"首列时间窗收束成一个确定闭区间"的条件
    ///（VendorDoc·TDengine：DELETE 页——时间窗条件是硬性的，普通列条件只能跟着时间窗走）。
    /// 时间窗分析这一格本层没有：任意 WHERE 的 DELETE 大概率被服务端拒回，
    /// 宁可当场红也不发一条猜出来的条件。</summary>
    public override void Emit(in DeleteStatement delete, SqlWriter writer)
        => throw new QuickSqlException(
            "TDengine 的 DELETE 只认首列时间窗条件（WHERE 必须把主键时间戳收束成一个确定闭区间，"
            + "https://docs.tdengine.com/tdengine-sql/08-delete/），本层不建模时间窗分析，"
            + "任意条件的 DELETE 发出去大概率被服务端拒回——这里整格抛。");

    /// <summary>插入的稳定分岔：契约带 <see cref="EntityContract.Stable"/> 的实体整句覆写，
    /// 其余照基类原形（普通表的插入文本一个字不动）。覆写点与 GBase 8s 的 UpsertStatement
    /// 整句覆写同一模式：全部 INSERT 发射调用点都走接口分派，覆写即接管；
    /// <see cref="TableRef.Contract"/> 让发射现场拿得到整份契约（InsertEngine 那一头装进来的）。</summary>
    public override void Emit(in InsertStatement insert, SqlWriter writer)
    {
        if (insert.Table.Contract is not { Stable: { } stable })
        {
            base.Emit(insert, writer);
            return;
        }

        EmitStableInsert(insert, writer, stable);
    }

    /// <summary>稳定表的插入：INSERT INTO 子表 USING 稳定 [TAGS 名清单] TAGS (内联值) (数据列) VALUES (…)。
    /// 逐格：TAG 列从数据列清单摘出（它们是子表元数据，不进 VALUES）；TAG 值内联而不参数化
    ///——STMT2 的参数化自动建表有明文行为缺口（子表不存在时报错，不像 shell 按 TAGS 建表
    ///【VendorDoc·TDengine：stmt 页】），文本形把自动建表留在 shell 同侧；数据列的参数照常
    /// 位置形 ?（那一半的批量疑点是另一格，与稳定面互不背书）。子表名由 Subtable 模板填
    /// 行内 TAG 值解析，同批行的 TAG 值必须全等——一条 INSERT … USING 只服务一颗子表。
    /// 已知的一格形状差：分块预算（ChunkRows）按契约列数计，TAG 列被本格摘掉后每句的实际
    /// 参数比预算少——块切得比需要的细，是保守方向的差，不是错的差。</summary>
    void EmitStableInsert(in InsertStatement insert, SqlWriter writer, StableDeclaration stable)
    {
        // 空行清单在普通表那条路由取值链挡住（没有行就没有语句）；这一格是形状保真，
        // 防"手工拼 InsertStatement"那条路在 row 0 上落一个下标越界。
        if (insert.Rows.Count == 0)
            throw new QuickSqlException("稳定表插入没有行：一条 INSERT … USING 至少要有一行（走到这里说明"
                + "有人绕过了 InsertEngine 拼语句）。");

        // TAG 列在语句列清单里的下标（契约声明序）。摘列是本发射格的职责：TAG 值进 TAGS 子句，
        // 不进 VALUES——插进去的"TAG 列的值"在那一家是语义错而不是缺省值。
        var tagIndexes = new int[stable.Tags.Count];

        for (var i = 0; i < stable.Tags.Count; i++)
        {
            var index = -1;

            for (var c = 0; c < insert.Columns.Count; c++)
                if (insert.Columns[c].MemberName == stable.Tags[i].MemberName)
                {
                    index = c;
                    break;
                }

            if (index < 0)
                throw new QuickSqlException(
                    $"稳定表插入的列清单里没有 TAG 列 {stable.Tags[i].ColumnName}（{stable.Tags[i].MemberName}）："
                    + "TAG 值走 TAGS 子句、数据列清单把它们摘掉是本发射格的事 —— "
                    + "走到这里说明有人绕过了稳定分岔拼了一条自己的 INSERT。");

            tagIndexes[i] = index;
        }

        // 第一行的 TAG 值就是这条语句的子表身份；其余行逐颗对齐——不同就分句，宁可红也不把
        // 第二颗子表的行静默塞进第一颗（厂商对已存在子表会忽略语句里的 TAG 值，那种错不报）。
        var tagValues = new object?[stable.Tags.Count];

        for (var i = 0; i < tagIndexes.Length; i++)
            tagValues[i] = RowValue(insert, 0, tagIndexes[i], stable.Tags[i].MemberName);

        for (var row = 1; row < insert.Rows.Count; row++)
            for (var i = 0; i < tagIndexes.Length; i++)
                if (!Equals(RowValue(insert, row, tagIndexes[i], stable.Tags[i].MemberName), tagValues[i]))
                    throw new QuickSqlException(
                        $"这一批插入里 TAG 值不一致（{stable.Tags[i].MemberName} 在第 1 行与第 {row + 1} 行不同）："
                        + "一条 INSERT … USING 只服务一颗子表，TAG 值不同的行请分成多次插入"
                        + "（SaveChanges 的批量与多行 VALUES 在这一家同此判；对已存在子表厂商会"
                        + "忽略语句里的 TAG 值，那种错不报，所以这里必须先红）。");

        writer.Append("INSERT INTO ");
        EmitSubtableName(writer, insert.Table.Table, stable, tagValues);
        writer.Append(" USING ");
        EmitTableName(writer, insert.Table.Table);

        EmitTagsClause(writer, stable, tagValues);

        var dataIndexes = new List<int>(insert.Columns.Count - stable.Tags.Count);

        for (var c = 0; c < insert.Columns.Count; c++)
            if (Array.IndexOf(tagIndexes, c) < 0)
                dataIndexes.Add(c);

        writer.Append(" (");

        for (var i = 0; i < dataIndexes.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, insert.Columns[dataIndexes[i]].ColumnName);
        }

        writer.Append(") VALUES ");

        for (var row = 0; row < insert.Rows.Count; row++)
        {
            if (row > 0)
                writer.Append(", ");

            writer.Append('(');

            for (var i = 0; i < dataIndexes.Count; i++)
            {
                if (i > 0)
                    writer.Append(", ");

                // null 格在本家是 NULL 不是 DEFAULT：官方 column_definition 文法里没有 DEFAULT 项，
                // 基类那一颗 "DEFAULT" 在这一家发不出去（现行取值链不产 null 格，这半是形状保真）。
                if (insert.Rows[row][dataIndexes[i]] is { } value)
                    EmitExpression(value, writer);
                else
                    writer.Append("NULL");
            }

            writer.Append(')');
        }

        if (insert.Returning.Count > 0)
            EmitReturning(writer, insert.Returning);
    }

    /// <summary>子表名段：模板填行内 TAG 值，过方言引号（反引号转义形兜住值里的特殊字符）。
    /// 没声明模板的稳定实体在插入面没有子表名的来路——当场指路，不发"插稳定名"那种非法对象。</summary>
    void EmitSubtableName(SqlWriter writer, TableInfo table, StableDeclaration stable, object?[] tagValues)
    {
        if (stable.SubtableTemplate is not { } template)
            throw new QuickSqlException(
                $"稳定表 {table.Name} 没有声明子表名模板（[TDengineStable] 的 Subtable 那一格）："
                + "插入面发 INSERT INTO 子表 USING … 需要子表名，而\"插稳定名\"在本家不是合法的插入对象 —— "
                + "请在声明里补模板（占位符 {{成员名}} 引用已声明的 TAG），DDL 与查询面不受影响。");

        var builder = new StringBuilder(template.Length + 16);

        for (var at = 0; at < template.Length;)
        {
            var open = template.IndexOf('{', at);

            if (open < 0)
            {
                builder.Append(template[at..]);
                break;
            }

            builder.Append(template[at..open]);

            var close = template.IndexOf('}', open + 1);
            var member = template[(open + 1)..close];

            var tagIndex = -1;

            // 占位符比对大小写不敏感：与 EntityContractFactory.ValidateSubtableTemplate 同一把尺
            //（契约期放行的形状，插入期不许换一副判据——两处不同尺就是"契约期绿、插入期红"的分叉）。
            for (var i = 0; i < stable.Tags.Count; i++)
                if (string.Equals(stable.Tags[i].MemberName, member, StringComparison.OrdinalIgnoreCase))
                {
                    tagIndex = i;
                    break;
                }

            if (tagIndex < 0)
                throw new QuickSqlException(
                    $"子表名模板 {template} 的占位符 {{{member}}} 不是已声明的 TAG 成员："
                    + "契约期已校验过这一格，走到这里说明有人手工拼了 StableDeclaration —— 请过 MapStable 那条收口。");

            var value = tagValues[tagIndex];

            if (value is null)
                throw new QuickSqlException(
                    $"子表名模板 {template} 的占位符 {{{member}}} 在这一行是 NULL："
                    + "子表名里落不进空值 —— 给这颗 TAG 赋值，或把它从模板里摘掉。");

            builder.Append(value switch
            {
                string text => text,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? throw new QuickSqlException(
                    $"子表名模板 {template} 的占位符 {{{member}}} 的值（{value.GetType().Name}）没有文本形"),
            });

            at = close + 1;
        }

        if (table.Schema is { Length: > 0 } schema)
            writer.Append(Identifiers.Quote(schema)).Append('.');

        writer.Append(Identifiers.Quote(builder.ToString()));
    }

    /// <summary>TAGS 子句：值一律内联（参数化的自动建表在 STMT2 上有明文行为缺口）；有 null 的
    /// TAG 走部分标签列形式——只列非空的那些名与值（VendorDoc·TDengine：插入页推荐的 NULL 表达法，
    /// "TAGS(null) 字面量"那一形没有明文）；全空时发全部名清单 + NULL 字面量（两种都没读数，
    /// 取 fuller 那一形，重开条件记 notes）。</summary>
    void EmitTagsClause(SqlWriter writer, StableDeclaration stable, object?[] tagValues)
    {
        var inlined = new string?[tagValues.Length];
        var nonNull = 0;

        for (var i = 0; i < tagValues.Length; i++)
        {
            if (tagValues[i] is not null)
                nonNull++;

            if (!values.TryInlineLiteral(tagValues[i], null, out inlined[i]) || inlined[i] is not { Length: > 0 })
                throw new QuickSqlException(
                    $"TAG {stable.Tags[i].MemberName} 的值不是能内联的那一档：TAGS 子句收文本形"
                    + "（STMT2 的参数化自动建表有明文行为缺口，本家不走参数化那一侧），"
                    + "byte[] 这类形状请先在声明侧定文本形或换成员型。");
        }

        if (nonNull == tagValues.Length)
        {
            writer.Append(" TAGS (");

            for (var i = 0; i < inlined.Length; i++)
            {
                if (i > 0)
                    writer.Append(", ");

                writer.Append(inlined[i]!);
            }

            writer.Append(')');
            return;
        }

        if (nonNull > 0)
        {
            writer.Append(" (");

            var named = false;

            for (var i = 0; i < inlined.Length; i++)
            {
                if (tagValues[i] is null)
                    continue;

                if (named)
                    writer.Append(", ");

                EmitIdentifier(writer, stable.Tags[i].ColumnName);
                named = true;
            }

            writer.Append(") TAGS (");

            var emitted = false;

            for (var i = 0; i < inlined.Length; i++)
            {
                if (tagValues[i] is null)
                    continue;

                if (emitted)
                    writer.Append(", ");

                writer.Append(inlined[i]!);
                emitted = true;
            }

            writer.Append(')');
            return;
        }

        writer.Append(" (");

        for (var i = 0; i < stable.Tags.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, stable.Tags[i].ColumnName);
        }

        writer.Append(") TAGS (");

        for (var i = 0; i < tagValues.Length; i++)
        {
            if (i > 0)
                writer.Append(", ");

            writer.Append("NULL");
        }

        writer.Append(')');
    }

    /// <summary>行值取出：本发射格只认 InsertEngine 装进来的 ParameterValue（null 格 = 这一格没填）。</summary>
    static object? RowValue(in InsertStatement insert, int row, int column, string member)
        => insert.Rows[row][column] switch
        {
            null => null,
            ParameterValue { } parameter => parameter.Value,
            _ => throw new QuickSqlException($"稳定表插入的 TAG 值（{member}）不是参数化的行值："
                + "本发射格只认取值链装进来的 ParameterValue —— 走到这里说明有人绕过了 InsertEngine 拼行。"),
        };
}
