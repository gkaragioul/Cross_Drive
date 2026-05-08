# Maps a drive letter from the elevated session into the interactive user's session
# so File Explorer (non-elevated) sees the same WinFsp volume.
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Z]$')]
    [string]$Letter
)

$ErrorActionPreference = 'Continue'
$dataDir = Join-Path $env:ProgramData 'CrossDrive'
New-Item -ItemType Directory -Path $dataDir -Force -ErrorAction SilentlyContinue | Out-Null
$logPath = Join-Path $dataDir 'user-session-map.log'

function Write-MapLog([string]$Message) {
    try {
        "$(Get-Date -Format o) $Message" | Add-Content -LiteralPath $logPath -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch { }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class CrossDriveQdd {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
'@ -ErrorAction Stop

$deviceFile = Join-Path $dataDir "device-target-$Letter.txt"
$target = $null
for ($i = 0; $i -lt 10; $i++) {
    $sb = New-Object System.Text.StringBuilder 4096
    $len = [CrossDriveQdd]::QueryDosDevice("$Letter`:", $sb, 4096)
    if ($len -gt 0) {
        $target = $sb.ToString().Trim()
        if ($target.Length -gt 0) { break }
    }
    Start-Sleep -Milliseconds 400
}

if (-not $target) {
    Write-MapLog "QueryDosDevice failed for ${Letter}: (no target after retries; WinFsp letter may not exist in this session yet)"
    exit 2
}

Write-MapLog "Resolved ${Letter}: -> $target"
[System.IO.File]::WriteAllText($deviceFile, $target, [System.Text.UTF8Encoding]::new($false))

function Resolve-UserSessionHelper {
    $candidates = @(
        (Join-Path $PSScriptRoot '..\native-bin\user-session\CrossDrive.UserSessionHelper.exe'),
        (Join-Path $PSScriptRoot '..\native-bin\CrossDrive.UserSessionHelper.exe'),
        (Join-Path $PSScriptRoot '..\native\bin\user-session\CrossDrive.UserSessionHelper.exe'),
        (Join-Path $PSScriptRoot '..\native\bin\CrossDrive.UserSessionHelper.exe')
    )

    foreach ($candidate in $candidates) {
        try {
            $resolved = [System.IO.Path]::GetFullPath($candidate)
            if (Test-Path -LiteralPath $resolved) {
                return $resolved
            }
        } catch { }
    }

    return $null
}

function Quote-TaskArgument([string]$Value) {
    '"' + ($Value -replace '"', '\"') + '"'
}

$helperPath = Resolve-UserSessionHelper
if (-not $helperPath) {
    Write-MapLog "CrossDrive.UserSessionHelper.exe not found; cannot silently map ${Letter}:"
    exit 6
}

$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ([string]::IsNullOrWhiteSpace($userId)) {
    Write-MapLog "Could not resolve WindowsIdentity name for scheduled task principal"
    exit 4
}

$taskName = "CrossDriveMap_${Letter}_$([guid]::NewGuid().ToString('N').Substring(0, 12))"
try {
    $helperArgs = @('map', "$Letter`:", $target) | ForEach-Object { Quote-TaskArgument $_ }
    $action = New-ScheduledTaskAction -Execute $helperPath -Argument ($helperArgs -join ' ')
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    $task = New-ScheduledTask -Action $action -Principal $principal -Settings $settings
    Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName | Out-Null

    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 250
        $t = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if (-not $t) { break }
        if ($t.State -ne 'Running') { break }
    } while ((Get-Date) -lt $deadline)

    Start-Sleep -Milliseconds 400
    $info = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
    if ($info -and $info.LastTaskResult -ne 0) {
        Write-MapLog "Scheduled task $taskName LastTaskResult=$($info.LastTaskResult)"
        throw "Scheduled task failed with LastTaskResult=$($info.LastTaskResult)"
    }
} catch {
    Write-MapLog "Scheduled task error: $($_.Exception.Message)"
    exit 5
} finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}

Write-MapLog "User-session map completed for ${Letter}:"
exit 0

