namespace QuickSql.Metadata;

/// <summary>元数据来源层，优先级从高到低。</summary>
public enum MetadataLayer
{
    ProviderFallback = 0,
    Convention = 1,
    DataAccessAnnotation = 2,
    QuickSqlAttribute = 3,
    Fluent = 4,
}

/// <summary>长度的量纲。字符与字节两把尺子混用是中文列炸掉的典型根因。</summary>
public enum LengthSemantics
{
    /// <summary>n 表示字符数：PG 系的 varchar(n)、SQL Server 的 nvarchar(n)。</summary>
    Char = 0,

    /// <summary>n 表示字节数：Oracle/DM 的 VARCHAR2(n)、SQL Server 非 UTF-8 排序规则下的 varchar(n)。</summary>
    Byte = 1,
}

public sealed record OriginTrace(MetadataLayer Layer, string Note)
{
    public override string ToString() => $"{Layer}: {Note}";
}
