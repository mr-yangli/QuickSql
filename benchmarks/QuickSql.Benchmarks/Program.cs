using System.Text;
using QuickSql.Benchmarks;
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
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

return 0;

static bool IsDebug()
{
#if DEBUG
    return true;
#else
    return false;
#endif
}
