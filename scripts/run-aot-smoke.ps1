# Native AOT 真发布冒烟闸：publish 出原生二进制再跑，退出码与输出同进一份日志。
# 与 run-gate.ps1 同一纪律：代码落在日志里，不靠人记得 echo。
#
# 前置：Windows 需要 VS 的 C++ 构建工具（Microsoft.VisualStudio.Workload.VCTools，含 MSVC 链接器与 Windows SDK）；
# 缺了它 dotnet publish 会在 ILCompiler 链接段失败——那不是闸红，是工具链没装，日志里认得出来。
# Linux CI 用 -Rid linux-x64（需 clang/lld 与对应 sysroot）。
#
#   powershell -NoProfile -File scripts/run-aot-smoke.ps1 -Log obj/logs/aot-smoke-1.log
#   powershell -NoProfile -File scripts/run-aot-smoke.ps1 -Log obj/logs/aot-smoke-linux.log -Rid linux-x64

param(
    [Parameter(Mandatory = $true)] [string] $Log,
    [string] $Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$proj = Join-Path $root 'tests/QuickSql.Tests.AotSmoke/QuickSql.Tests.AotSmoke.csproj'
$pubDir = Join-Path $root "tests/QuickSql.Tests.AotSmoke/bin/Release/net11.0/$Rid/publish"
$exeName = if ($Rid -like 'win-*') { 'QuickSql.Tests.AotSmoke.exe' } else { 'QuickSql.Tests.AotSmoke' }
$exe = Join-Path $pubDir $exeName

$logPath = [System.IO.Path]::GetFullPath($Log)
$logDir = Split-Path -Parent $logPath
if (-not (Test-Path -LiteralPath $logDir)) {
    New-Object -TypeName System.IO.DirectoryInfo -ArgumentList $logDir | Out-Null
    [System.IO.Directory]::CreateDirectory($logDir) | Out-Null
}

$stream = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Create)
$writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))

try {
    # publish 段：原生编译失败（工具链缺、ILC 错）在这里就红，退出码 2 与冒烟主体的 1 分开。
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = 'dotnet'
    $info.Arguments = "publish `"$proj`" -c Release -r $Rid --nologo"
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::Start($info)
    $out = $proc.StandardOutput.ReadToEnd()
    $err = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    $writer.Write($out)
    $writer.Write($err)
    $writer.WriteLine("PUBLISH_EXIT=$($proc.ExitCode)")

    if ($proc.ExitCode -ne 0) {
        $writer.WriteLine('AOT_SMOKE_EXIT=2 (publish failed)')
        $writer.Flush()
        exit 2
    }

    # 冒烟段：跑原生二进制。
    $info2 = New-Object System.Diagnostics.ProcessStartInfo
    $info2.FileName = $exe
    $info2.WorkingDirectory = $pubDir
    $info2.UseShellExecute = $false
    $info2.RedirectStandardOutput = $true
    $info2.RedirectStandardError = $true

    $proc2 = [System.Diagnostics.Process]::Start($info2)
    $out2 = $proc2.StandardOutput.ReadToEnd()
    $err2 = $proc2.StandardError.ReadToEnd()
    $proc2.WaitForExit()
    $writer.Write($out2)
    $writer.Write($err2)
    $writer.WriteLine("AOT_SMOKE_EXIT=$($proc2.ExitCode)")
    $writer.Flush()
    exit $proc2.ExitCode
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}