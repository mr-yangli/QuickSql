# Runs the integration suite and writes the target-instance stamp into the SAME log, before the
# runner speaks. Section 13.1 item 4 defines the connection-source order; this script only reads it.
#
# Why a script and not a habit: "run the probe first, name the log after the instance" was written
# down in v0.15, followed in v0.15/v0.16, missed in v0.18 (probe ran, output never landed) and
# missed again in v0.19 (four logs, zero stamp lines). A step that has to be remembered is a step
# that goes missing; here the stamp is part of the artifact the run produces.
#
# Never prints the connection string or the password -- only SOURCE / DATA_SOURCE / DATABASE / PROBE.
# Child stdout is copied as raw bytes (no PowerShell recoding), which is why bash `>` works and `*>`
# does not; this keeps CJK readable in the log.
#
#   powershell -NoProfile -File scripts/run-integration.ps1 -Log obj/logs/v19-int-localdb-1.log
#   $env:QUICKSQL_TEST_CONNECTION='...'  # set it to hit LocalDB; leave it unset for the repo-root file

param(
    [Parameter(Mandatory = $true)] [string] $Log,
    [string[]] $Arg = @()
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = Join-Path $root 'tests/QuickSql.Tests.Integration/bin/Debug/net10.0/QuickSql.Tests.Integration.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "test host not built: $exe"
}

$logPath = [System.IO.Path]::GetFullPath($Log)
$logDir = Split-Path -Parent $logPath

if (-not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

$stream = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Create)
$writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))

function Write-Stamp {
    $cs = $env:QUICKSQL_TEST_CONNECTION
    $source = 'env'

    if ([string]::IsNullOrWhiteSpace($cs)) {
        $file = Join-Path $root 'quicksql-test.connection.local.txt'
        if (-not (Test-Path -LiteralPath $file)) { throw "no connection source: unset QUICKSQL_TEST_CONNECTION and no $file" }
        $cs = ([System.IO.File]::ReadAllLines($file) | Where-Object { $_ -match '^[A-Za-z]' } | Select-Object -First 1)
        $source = 'file'
    }

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder([string] $cs)

    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    $connection.Open()

    $command = $connection.CreateCommand()
    $command.CommandText = "DECLARE @c nvarchar(128) = ISNULL(CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'NULL'); SELECT 'PROBE=' + CONVERT(varchar(128), SERVERPROPERTY('ProductVersion')) + ' | ' + ISNULL(CONVERT(varchar(64), SERVERPROPERTY('ProductLevel')), 'n/a') + ' | ' + CONVERT(varchar(128), SERVERPROPERTY('Edition')) + ' | ' + CONVERT(varchar(64), DB_NAME()) + ' | ' + CONVERT(varchar(128), @c) + ' | ' + ISNULL(CONVERT(varchar(16), COLLATIONPROPERTY(@c, 'CodePage')), 'NULL')"

    $probe = [string] $command.ExecuteScalar()

    $connection.Close()

    Write-Output ('SOURCE=' + $source)
    Write-Output ('DATA_SOURCE=' + $builder.DataSource + ' DATABASE=' + $builder.InitialCatalog)
    Write-Output $probe
}

try {
    foreach ($line in (Write-Stamp)) { $writer.WriteLine($line) }

    $writer.Flush()

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
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
