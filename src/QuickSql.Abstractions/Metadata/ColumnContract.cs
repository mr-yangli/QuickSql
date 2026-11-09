namespace QuickSql.Metadata;

/// <summary>一个列的全部已解析事实。DDL 生成、参数绑定、写前校验三处只读这里，各自不允许再读注解。</summary>
public sealed record ColumnContract
{
    public required string MemberName { get; init; }

    public required Type ClrType { get; init; }

    public required string ColumnName { get; init; }

    /// <summary>这一列的 n 是字符还是字节，由 provider 决定。</summary>
    public required LengthSemantics LengthSemantics { get; init; }

    /// <summary>true = Unicode 列（nvarchar/nchar）。SQL Server 默认必须为 true，否则 cp1252 排序规则下中文会静默写成 ?。</summary>
    public required bool Unicode { get; init; }

    public required bool IsNullable { get; init; }

    /// <summary>"非空"是否有人显式说过。false 只来自可空上下文之外的程序集；它只摘必填校验，不改 DDL 形状。</summary>
    public bool NullabilityDeclared { get; init; } = true;

    public required bool IsPrimary { get; init; }

    public required bool IsIdentity { get; init; }

    /// <summary>并发列：只比较不书写——不进 SET、不进列清单、不进脏判定，但进 WHERE 与回填集。</summary>
    public bool IsConcurrencyToken { get; init; }

    /// <summary>应用给值的应用自维护并发列（ABP 的 ConcurrencyStamp 一族）：新值由应用生成，进 SET 与 INSERT，WHERE 比快照。只有 IsConcurrencyToken 为真时有效。</summary>
    public bool AppSetsValue { get; init; }

    /// <summary>provider 说这一型由库自己给值且永不为空（输入位）。今天只有 SQL Server 的 rowversion。</summary>
    public bool StoreFillsValue { get; init; }

    /// <summary>"值归库给"结论位 = 自增 ∪ StoreFillsValue；唯一读者是 NullableInStore。</summary>
    public bool ValueFromStore => IsIdentity || StoreFillsValue;

    /// <summary>字符上限。required：漏算一尺就是事故。</summary>
    public required int? MaxChars { get; init; }

    /// <summary>字节上限（非 Unicode 列按 UTF-8 最坏 3 倍算）。</summary>
    public required int? MaxBytes { get; init; }

    public required int? Precision { get; init; }

    public required int? Scale { get; init; }

    /// <summary>provider 覆写后的存储类型名（如 VARCHAR2(50 CHAR)）。</summary>
    public string? StoreTypeName { get; init; }

    /// <summary>列备注。null 与空串不是一回事：结构同步按这一格决定要不要重发 COMMENT。</summary>
    public string? Comment { get; init; }

    /// <summary>列默认值。null = 没配（不支持配 null）；只收常量字面量档（string/bool/整型/浮点/decimal）。
    /// 只出现在建表与补列的 DDL 里，不参与形状补差比对。</summary>
    public object? DefaultValue { get; init; }

    public required OriginTrace Origin { get; init; }

    /// <summary>写前校验用的字符数尺子。</summary>
    public bool FitsChars(string value) => MaxChars is null || value.Length <= MaxChars;

    /// <summary>UTF-8 字节数尺子（代理对 4 字节、汉字 3 字节）。</summary>
    public bool FitsBytes(string value) => MaxBytes is null || System.Text.Encoding.UTF8.GetByteCount(value) <= MaxBytes;

    /// <summary>DDL 里写 NULL 还是 NOT NULL。不等于 IsNullable：值归库给的列在 CLR 侧可空、库里永不为空，
    /// 照抄 CLR 可空性会得到"这一列可以由人写空值"的假承诺。五族 EmitColumnDefinition 只读这一格。</summary>
    public bool NullableInStore => IsNullable && !ValueFromStore;
}
