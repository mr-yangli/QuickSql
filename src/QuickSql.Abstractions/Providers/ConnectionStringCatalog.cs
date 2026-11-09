using System.Data.Common;

namespace QuickSql.Providers;

/// <summary>连接串里那颗"目标库名"的读取与摘除，供支持建库的 provider 共用
/// （EnsureDatabaseExistsAsync）。裸 DbConnectionStringBuilder 不做同义词归一——只按字面键比、仅忽略大小写，
/// 只读 Database 的实现面对一条写 Initial Catalog 的合法串就落到"这条串没指名字，本来就不该建库"那一岔
/// 静默返回，最后连不上库的是调用方；所以这一家认哪些同义词必须由构造参数一颗颗列出来，
/// 不能指望 builder 替归一，列了哪几颗就只认哪几颗。keys 是这一家驱动认的库名键的字面写法，按常见程度排，至少一颗。</summary>
public sealed class ConnectionStringCatalog(params string[] keys)
{
    /// <summary>候选键。构造期定死，之后不变更。</summary>
    readonly string[] _keys = keys.Length > 0
        ? [.. keys]
        : throw new ArgumentException("同义词表不能是空的：一颗键都不列就等于这座库名永远读不出来。", nameof(keys));

    /// <summary>那颗目标库名；null 即"这条串没指名字"，调用方按原样跳过建库。
    /// 两颗键同时在场且值不同时抛而不是挑一颗：这一格的产出是"建哪座库"，
    /// 挑错的那一种结果是建了一座没人要的库、而连接还是连不上原来那座——静默挑一颗在这里比抛更贵。</summary>
    public string? Read(DbConnectionStringBuilder builder)
    {
        string? found = null;
        string? foundKey = null;

        foreach (string key in _keys)
        {
            if (!builder.TryGetValue(key, out object? value) || value is not string text || text.Length == 0)
                continue;

            if (found is null)
            {
                found = text;
                foundKey = key;
                continue;
            }

            if (string.Equals(found, text, StringComparison.OrdinalIgnoreCase))
                continue;

            throw new QuickSqlException(
                $"这条连接串里有两颗互相矛盾的库名：{foundKey}={found} 与 {key}={text}。" +
                "本方法要按那个名字建库，所以不能替你挑一颗 —— 请把它们改成同一个名字（或删掉其中一颗）。");
        }

        return found;
    }

    /// <summary>把所有候选键摘干净。必须整颗摘而不是只摘读到的那一颗：留两颗互相矛盾的键
    /// 等于把"哪一颗说了算"交给驱动的解析顺序，而驱动自己会同义词归一，比这一层宽容。
    /// 摘完之后调用方再写上自己要连的那座锚点库（master、postgres，或干脆不写）。</summary>
    public void Clear(DbConnectionStringBuilder builder)
    {
        foreach (string key in _keys)
            builder.Remove(key);
    }
}
