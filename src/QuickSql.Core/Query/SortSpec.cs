namespace QuickSql.Query;

/// <summary>把前端那一串排序写法（"Id,Name desc"）拆成「名字 + 方向」的清单。这一颗只拆，
/// 不认列：认列全仓库只有 QueryEngine.Column 那一份三档（属性名、列名、大小写不敏感的唯一命中），
/// 拆出来的每一颗名字都从那里过，认不出当场抛，串里的文本依旧一个字都不进 SQL。方向词只有挂在
/// 名字后面那一形，取值只有 asc/desc（ascending/descending 同义）四颗、大小写不敏感；认不出方向词
/// 的那一段整颗按列名交出去，而不是猜一个方向——"desc" 指的可以是那一颗名叫 desc 的列，
/// "a; drop table t--" 指的是一颗认不出而当场响的名字。</summary>
static class SortSpec
{
    /// <summary>逗号分颗，空颗（含 "Id,,Name," 那种前端 join(',') 留下的尾巴）跳过。
    /// defaultDescending 是没写方向的颗的方向，写了的以串为准，单颗写法逐字不变。收 null 交回
    /// 空清单：串是 HTTP 查询参数直过来的，"没给排序"要响的是认列那一句（消息里带着表名），
    /// 不是 NullReferenceException。</summary>
    public static IReadOnlyList<(string Name, bool Descending)> Parse(string? sortSpec, bool defaultDescending)
    {
        List<(string Name, bool Descending)> keys = [];

        foreach (var raw in (sortSpec ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 0)
            {
                keys.Add(SplitOne(raw, defaultDescending));
            }
        }

        return keys;
    }

    static (string Name, bool Descending) SplitOne(string token, bool defaultDescending)
    {
        var at = token.Length;

        while (at > 0 && !char.IsWhiteSpace(token[at - 1]))
        {
            at--;
        }

        if (at == 0)
        {
            return (token, defaultDescending);
        }

        var head = token[..(at - 1)].TrimEnd();
        var tail = token[at..];

        if (tail.Equals("desc", StringComparison.OrdinalIgnoreCase) || tail.Equals("descending", StringComparison.OrdinalIgnoreCase))
        {
            return (head, true);
        }

        if (tail.Equals("asc", StringComparison.OrdinalIgnoreCase) || tail.Equals("ascending", StringComparison.OrdinalIgnoreCase))
        {
            return (head, false);
        }

        return (token, defaultDescending);
    }
}
