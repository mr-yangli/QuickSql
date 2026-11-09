# Runs the section 12.8 gate and writes its exit code into the SAME log, as the last line, from the
# same stream the child output was copied into. Nothing outside this process can guarantee that line.
#
# Why a script and not a habit (second time this repo writes that sentence): the "land the code in
# the log" habit broke five times (v0.24 / v0.26 / v0.30 / v0.31 / v0.32) by the echo never being
# typed, and a sixth time in v0.33 by being typed AFTER the redirect -- `; echo "EXIT=$?"` lands on
# the console, not in the log, so the runner saw the code while the artifact stayed silent. A step
# that has to be remembered is a step that goes missing; here the code is part of the artifact.
#
# The gate exit code is data, not failure: red rows + 32 * regressions (5 red rows and no
# regression => 5). The script never throws on it; it re-exits with the child's code so the caller
# can pin it too.
#
# Child stdout/stderr are copied as raw bytes (no PowerShell recoding), exactly like
# run-integration.ps1, which keeps CJK readable in the log.
#
#   powershell -NoProfile -File scripts/run-gate.ps1 -Log obj/logs/v33-gate-script-1.log
#   powershell -NoProfile -File scripts/run-gate.ps1 -Log <path> -Arg @('--gate','--warm')  # if ever needed

param(
    [Parameter(Mandatory = $true)] [string] $Log,
    [string[]] $Arg = @('--gate')
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = Join-Path $root 'benchmarks/QuickSql.Benchmarks/bin/Release/net10.0/QuickSql.Benchmarks.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "gate host not built: $exe"
}

$logPath = [System.IO.Path]::GetFullPath($Log)
$logDir = Split-Path -Parent $logPath

if (-not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

$stream = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Create)
$writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))

try {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Arguments = ($Arg -join ' ')
    $info.WorkingDirectory = Split-Path -Parent $exe

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $info
    [void] $proc.Start()

    # Raw byte copy: the child writes UTF-8, the log gets UTF-8, nothing re-encodes the CJK.
    $proc.StandardOutput.BaseStream.CopyTo($stream)
    $proc.StandardError.BaseStream.CopyTo($stream)

    $proc.WaitForExit()

    $writer.Flush()
    $writer.WriteLine('EXIT=' + $proc.ExitCode)
    $writer.Flush()

    Write-Host ('log=' + $logPath + ' exit=' + $proc.ExitCode)

    exit $proc.ExitCode
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
