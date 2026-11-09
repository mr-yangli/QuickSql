using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>黄金样本比对（§13.1）。判定是<b>逐字符相等</b>，不做"归一化之后差不多"的模糊匹配：
/// 黄金样本的全部价值就在于"文本变了要有人显式承认"，一旦归一化，括号、别名、列序这些真变更
/// 就全被抹平成噪音了。</summary>
static class Golden
{
    /// <summary>设 <c>QUICKSQL_GOLDEN=update</c> 重新生成基线。<b>只在人工读过 diff 之后用</b> ——
    /// 让脚本自动重写基线等于把断言变成回声。</summary>
    public const string UpdateSwitch = "QUICKSQL_GOLDEN";

    static readonly string Root = Path.Combine(RepoRoot(), "tests", "QuickSql.Tests.Dialects", "golden");

    /// <summary>默认那一套：T-SQL 的样本从 v0.5 起就在这里，绝大多数用例只比它，不该为第二个方言族
    /// 在每个调用点多写一个参数。</summary>
    public const string DefaultProvider = "sqlserver";

    /// <summary>从测试输出目录向上找解决方案文件来定位仓库根：跑测试的工作目录不固定
    /// （<c>dotnet test</c>、VS、Rider 各一套），只有"往上找到根"这件事在哪都一样。</summary>
    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "QuickSql.slnx")))
                return dir.FullName;

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上找不到 QuickSql.slnx，无法定位黄金样本目录。");
    }

    /// <summary>样本文件的绝对路径，<paramref name="file"/> 含扩展名。黄金样本之外的第二份读者是
    /// <see cref="CapabilityLedgerTests"/>：它拿同一个函数核"出处写着的那份样本在不在盘上"，
    /// 于是两处不会各自猜一次输出目录。</summary>
    public static string PathFor(string provider, string file) => Path.Combine(Root, provider, file);

    /// <summary>按方言族分目录，而不是给每份样本名加前缀：<c>golden/sqlserver/</c> 与 <c>golden/kingbase/</c>
    /// 各自成一份可数的清单，§13.3 那句"黄金样本从 P0 起就是三套"于是能在目录这一层被核账 ——
    /// 前缀式的组织会把"这一家有几份"退化成一条 grep。</summary>
    public static void Verify(string name, string sql, string provider = DefaultProvider)
    {
        var produced = "\n" + sql.Replace("\r\n", "\n") + "\n";
        var path = PathFor(provider, name + ".sql");

        if (Environment.GetEnvironmentVariable(UpdateSwitch) == "update")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, produced);
            return;
        }

        Assert.True(File.Exists(path), $"缺少黄金样本 {provider}/{name}.sql。先用 {UpdateSwitch}=update 生成，人工确认每条 SQL 确实正确，再提交基线。");

        // 首尾各留一个换行是为了让文件以 \n 结尾（版本库友好），比对时把两侧空白都剔掉，
        // 免得 git 的行尾转换策略变成测试失败原因。参数顺序按 Assert.Equal(expected, actual)：
        // 基线是期望值，本次生成的文本是实际值，反了会把失败消息里的两侧说反。
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n").Trim(), produced.Trim());
    }
}
