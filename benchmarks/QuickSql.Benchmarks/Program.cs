using System.Text;
using QuickSql.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--gate", StringComparer.Ordinal))
{
    // 判据只能在 Release 下出：Debug 里装箱更多、内联更少，红会假红、绿会假绿。
    // 退出码 2 与"门禁判出 N 红"的 N 区分开，免得把"跑错了配置"读成"有两条不达标"。
    // Release 下的读法：红行数（这一半会随负载翻，逐次值看 §12.8 台账），红行比基线差时再 +32 —— 见 Gates.RegressionFlag。
    if (IsDebug())
    {
        Console.Error.WriteLine("--gate 要在 Release 下跑，否则每一个数字都不作数：");
        Console.Error.WriteLine("    dotnet run -c Release --project bench/QuickSql.Benchmarks -- --gate");

        return 2;
    }

    return Gates.Run();
}

// 两种跑法共用同一批调用形状：判据与报告不能各长一套，否则半年后比的不是同一件事。
// <para>DisableOptimizationsValidator 不是绕检查，是检查的前提变了：BDN 会拒绝执行引用了 Debug 构建
// 的程序集，而 Live 三方对照组要引的 FreeSql 3.5.311 与 SqlSugarCore 5.1.4.221，官方 NuGet 交回来的
// 就是 Debug 构建（两家的 DLL 都带 DebuggableAttribute=263，DisableOptimizations，JIT 对它们关优化；
// Dapper 是正常 Release，实测对照）。Live 对照量的恰恰是"用户从 NuGet 拿到的那一份"——所以校验放行，
// "这两臂的时间列偏悲观"记成口径边界，专格写在 plan-cross-orm-bench.md。Offline 那片没有这个前提，
// 但闸门开在同一颗开关上：关掉的只是"拒绝跑"，量具本身一分没动。</para>
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args,
    DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));

return 0;

static bool IsDebug()
{
#if DEBUG
    return true;
#else
    return false;
#endif
}
