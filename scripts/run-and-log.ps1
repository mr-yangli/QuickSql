# Runs ANY console command and writes its exit code into the log as the last line, from the one
# process that owns the log stream. This is posture 3 of the three named in spec v0.33:
#   1. `exe > log` then `echo EXIT` to the console  -> the code is not in the artifact at all
#   2. `exe > log` then `echo >> log`               -> re-countable, but code and child are two
#      separate transactions: an interrupt between them leaves a log without a code
#   3. this script                                  -> child output and the code land in one stream,
#      held by one writer; there is no window in which the log exists without the code being due
#
# Why a second script when run-gate.ps1 exists: run-gate.ps1 pins the section 12.8 gate exe on
# purpose (its two witnesses are ledgered under that name). Everything else in the battery -- the dialect
# suite, `dotnet build` -- was still writing codes with posture 2. This script retires posture 2
# for them; it hardcodes nothing.
#
# The child's exit code is data, not failure: the script re-exits with it, so in task systems that
# treat "exit != 0" as failed, a normal 5-red gate run will show as failed. The judge is the
# `EXIT=` line in the log, not the runner's color.
#
# Child stdout/stderr are copied as raw bytes (no PowerShell recoding), like the other two scripts,
# which is what keeps CJK readable in the log.
#
# -Arg is joined with single spaces into the child's raw command line. When this script is invoked
# through `powershell -File`, comma arrays do NOT survive argv binding (measured: `-Arg a,b` arrives
# as ONE token "a,b"), so pass the whole command line as ONE quoted string with real spaces:
#
#   powershell -NoProfile -File scripts/run-and-log.ps1 -Exe tests/QuickSql.Tests.Dialects/bin/Release/net10.0/QuickSql.Tests.Dialects.exe -Log obj/logs/v33-dialects-3.log
#   powershell -NoProfile -File scripts/run-and-log.ps1 -Exe dotnet -Arg "build QuickSql.slnx -c Release --no-incremental" -Log obj/logs/v33-build-3.log

param(
    [Parameter(Mandatory = $true)] [string] $Exe,
    [Parameter(Mandatory = $true)] [string] $Log,
    [string[]] $Arg = @()
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# -Exe may be a path relative to the repo root, an absolute path, or a bare command found on PATH
# (e.g. 'dotnet'). WorkingDirectory is the repo root, so relative payloads inside a test bin dir
# still resolve the same way the shell would.
$resolved = $Exe
if (-not [System.IO.Path]::IsPathRooted($resolved)) {
    $candidate = Join-Path $root $Exe
    if (Test-Path -LiteralPath $candidate) { $resolved = $candidate }
}

if ((-not [System.IO.Path]::IsPathRooted($resolved)) -and ($resolved -notmatch '[\\/]')) {
    # bare command name: let the OS search PATH, nothing to pre-check here
}
elseif (-not (Test-Path -LiteralPath $resolved)) {
    throw "executable not found: $Exe (resolved: $resolved)"
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
    $info.FileName = $resolved
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Arguments = ($Arg -join ' ')
    $info.WorkingDirectory = $root

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
