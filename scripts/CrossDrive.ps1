# CrossDrive.ps1 - Core logic for mounting Mac drives on Windows via bundled native helpers

param (
    [Parameter(Mandatory = $false)]
    [string]$Action = "List",
    [string]$DriveID = "",
    [string]$Password = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$REG_BASE = "HKCU:\Software\CrossDrive\DriveMap"

function Get-AvailableDriveLetter {
    $used = ([System.IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1).ToUpper() })
    foreach ($letter in 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z') {
        if ($used -notcontains $letter) { return $letter }
    }
    return $null
}

function Get-AssignedLetter($id) {
    try {
        $val = Get-ItemPropertyValue -Path "$REG_BASE" -Name "Drive$id" -ErrorAction Stop
        if (-not [string]::IsNullOrWhiteSpace($val)) { return $val }
    }
    catch { }
    return $null
}

function Set-AssignedLetter($id, $letter) {
    if (-not (Test-Path $REG_BASE)) { New-Item -Path $REG_BASE -Force | Out-Null }
    Set-ItemProperty -Path $REG_BASE -Name "Drive$id" -Value $letter
}

function Clear-AssignedLetter($id) {
    try { Remove-ItemProperty -Path $REG_BASE -Name "Drive$id" -ErrorAction SilentlyContinue } catch {}
}

function Set-UserSessionDriveMapping($letter, $devicePath) {
    # Create a drive letter mapping in the non-elevated user session via a Scheduled Task.
    # Elevated processes have a separate DOS-device namespace from non-elevated Explorer,
    # so we must also map the drive letter in the user's interactive session.
    try {
        $scriptPath = "C:\ProgramData\CrossDrive\user-mount.ps1"
        $scriptContent = @"
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class UM{[DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Auto)]public static extern bool DefineDosDevice(uint f,string d,string t);}' -ErrorAction SilentlyContinue
[UM]::DefineDosDevice(1, "${letter}:", "$devicePath")
"@
        Set-Content -Path $scriptPath -Value $scriptContent -Force -Encoding UTF8
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$scriptPath`""
        $userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
        $task = New-ScheduledTask -Action $action -Principal $principal -Settings $settings
        Register-ScheduledTask -TaskName "CrossDriveMap$letter" -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName "CrossDriveMap$letter" | Out-Null
        Start-Sleep -Milliseconds 500
        Unregister-ScheduledTask -TaskName "CrossDriveMap$letter" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    } catch {}
}

function Remove-UserSessionDriveMapping($letter) {
    # Remove the drive letter from the non-elevated user session.
    # Uses QueryDosDevice to find the exact target, then DefineDosDevice with
    # DDD_REMOVE_DEFINITION|DDD_RAW_TARGET_PATH|DDD_EXACT_MATCH_ON_REMOVE (7)
    # to remove the precise mapping we created during mount.
    try {
        $scriptPath = "C:\ProgramData\CrossDrive\user-unmount.ps1"
        $scriptContent = @"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class UM {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern bool DefineDosDevice(uint f, string d, string t);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint QueryDosDevice(string d, StringBuilder buf, uint max);
}
'@ -ErrorAction SilentlyContinue
`$sb = New-Object System.Text.StringBuilder 512
`$qLen = [UM]::QueryDosDevice("${letter}:", `$sb, 512)
if (`$qLen -gt 0) {
    # flags: DDD_REMOVE_DEFINITION(2) | DDD_RAW_TARGET_PATH(1) | DDD_EXACT_MATCH_ON_REMOVE(4) = 7
    [UM]::DefineDosDevice(7, "${letter}:", `$sb.ToString())
} else {
    # Fallback: try simple remove
    [UM]::DefineDosDevice(2, "${letter}:", `$null)
}
"@
        Set-Content -Path $scriptPath -Value $scriptContent -Force -Encoding UTF8
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$scriptPath`""
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
        $settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
        $task = New-ScheduledTask -Action $action -Principal $principal -Settings $settings
        Register-ScheduledTask -TaskName "CrossDriveUnmap$letter" -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName "CrossDriveUnmap$letter" | Out-Null
        Start-Sleep -Milliseconds 1000
        Unregister-ScheduledTask -TaskName "CrossDriveUnmap$letter" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    } catch {}
}

function Remove-CurrentSessionDriveMapping($letter) {
    if (-not $letter) { return }
    try {
        if (-not ([System.Management.Automation.PSTypeName]'CrossDriveDosDevice').Type) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class CrossDriveDosDevice {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
'@ -ErrorAction SilentlyContinue
        }

        $sb = New-Object System.Text.StringBuilder 512
        $qLen = [CrossDriveDosDevice]::QueryDosDevice("${letter}:", $sb, 512)
        if ($qLen -gt 0) {
            # flags: DDD_REMOVE_DEFINITION(2) | DDD_RAW_TARGET_PATH(1) | DDD_EXACT_MATCH_ON_REMOVE(4) = 7
            [CrossDriveDosDevice]::DefineDosDevice(7, "${letter}:", $sb.ToString()) | Out-Null
        }
        else {
            # Fallback when the target cannot be queried but the drive bit still lingers.
            [CrossDriveDosDevice]::DefineDosDevice(2, "${letter}:", $null) | Out-Null
        }
    }
    catch {}
}

function Invoke-InteractiveHiddenCommand($taskName, $command) {
    try {
        $escaped = $command.Replace('"', '`"')
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"$escaped`""
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
        $settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
        $task = New-ScheduledTask -Action $action -Principal $principal -Settings $settings
        Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName | Out-Null
        Start-Sleep -Seconds 3
    }
    catch { }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    }
}

function Remove-DriveLetterMapping($letter) {
    if (-not $letter) { return }
    try { subst /d "${letter}:" 2>$null | Out-Null } catch {}
    try { net use "${letter}:" /delete /y 2>$null | Out-Null } catch {}
    try { Invoke-InteractiveHiddenCommand "CrossDriveMapCleanup$letter" "subst /d ${letter}: >`$null 2>&1; net use ${letter}: /delete /y >`$null 2>&1" } catch {}
}

function Hide-MacMetadataItems($rootPath) {
    # Read-only providers cannot persist hidden attributes on the source volume.
    # macOS dot-folders (.fseventsd, .Spotlight-V100, etc.) will remain visible.
    # This is a no-op; kept for future use if a writable mount becomes available.
}

function Cleanup-OrphanedCrossDriveMappings {
    try {
        $lines = & subst.exe 2>$null
        foreach ($line in $lines) {
            if ($line -match '^\s*([A-Z]):\\:\s*=>\s*(.+)$') {
                $letter = $matches[1].ToUpper()
                $target = $matches[2].Trim()
                if ($letter -in @('M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z') -and $target -like 'C:\ProgramData\CrossDrive\Drive*') {
                    Remove-DriveLetterMapping $letter
                }
            }
        }
    }
    catch {}

    try {
        if (Test-Path $REG_BASE) {
            $props = (Get-ItemProperty -Path $REG_BASE).PSObject.Properties |
                Where-Object { $_.Name -like 'Drive*' -and $_.Value }

            foreach ($prop in $props) {
                $driveId = $prop.Name -replace '^Drive', ''
                $letter = "$($prop.Value)".Trim().ToUpper().Replace(':', '')
                if ($letter -notin @('M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z')) { continue }
                if (Test-Path "${letter}:") { continue }

                Remove-CurrentSessionDriveMapping $letter
                Remove-UserSessionDriveMapping $letter
                Remove-DriveLetterMapping $letter
                Clear-AssignedLetter $driveId
            }
        }
    }
    catch {}
}

function Get-Drives {
    $disks = Get-PhysicalDisk | Select-Object DeviceID, FriendlyName, Size, MediaType

    $result = @()
    foreach ($disk in $disks) {
        $id = $disk.DeviceID
        $isMac = $false
        $format = "Windows/Unknown"

        try {
            $partitions = Get-Partition -DiskNumber $id -ErrorAction SilentlyContinue
            if ($partitions) {
                $hfsGuid  = [guid]"48465300-0000-11AA-AA11-00306543ECAC"
                $apfsGuid = [guid]"7C3457EF-0000-11AA-AA11-00306543ECAC"
                foreach ($p in $partitions) {
                    # Normalize GptType to a [guid] for case-insensitive, format-tolerant comparison.
                    # Get-Partition returns GptType in mixed case (with or without braces) depending on
                    # Windows build; substring -match worked by accident but failed when WMI returned
                    # the value with surrounding braces.
                    $type = "$($p.GptType)"
                    $typeGuid = $null
                    try { $typeGuid = [guid]($type -replace '[{}]', '') } catch { }
                    if (($typeGuid -eq $hfsGuid) -or ($p.Type -match "HFS")) {
                        $isMac = $true; $format = "HFS+"; break
                    }
                    if (($typeGuid -eq $apfsGuid) -or ($p.Type -match "APFS")) {
                        $isMac = $true; $format = "APFS"; break
                    }
                    # MBR-based HFS+ (partition type 0xAF) — shows as Unknown in Windows
                    if ($p.Type -eq "Unknown") {
                        $diskInfo = Get-Disk -Number $id -ErrorAction SilentlyContinue
                        if ($diskInfo -and $diskInfo.PartitionStyle -eq "MBR" -and $p.MbrType -eq 175) {
                            $isMac = $true; $format = "HFS+ (MBR)"; break
                        }
                    }
                }
            }
        }
        catch { }

        $isMounted = $false
        $mountPath = $null
        $driveLetter = Get-AssignedLetter $id

        if ($isMac -and $driveLetter) {
            if (Test-Path "${driveLetter}:") {
                $isMounted = $true
                $mountPath = "${driveLetter}:\"
            }
            else {
                Remove-CurrentSessionDriveMapping $driveLetter
                Remove-UserSessionDriveMapping $driveLetter
                Remove-DriveLetterMapping $driveLetter
                $linkPath = "C:\ProgramData\CrossDrive\Drive$id"
                if (Test-Path $linkPath) { Remove-Item -LiteralPath $linkPath -Force -ErrorAction SilentlyContinue }
                Clear-AssignedLetter $id
                $driveLetter = $null
            }
        }

        $result += @{
            id          = $id
            name        = $disk.FriendlyName
            size        = "{0:N2} GB" -f ($disk.Size / 1GB)
            type        = $disk.MediaType
            mounted     = $isMounted
            mountPath   = $mountPath
            uncPath     = $null
            driveLetter = $driveLetter
            format      = $format
            isMac       = $isMac
        }
    }
    return $result | ConvertTo-Json
}

function Initialize-LegacySetup {
    return @{
        success = $true
        ready = $true
        note = "Legacy setup flow has been retired. CrossDrive uses native Windows mount paths only."
    } | ConvertTo-Json
}

function Remove-Drive($id) {
    $letter = Get-AssignedLetter $id
    if ($letter) {
        Remove-CurrentSessionDriveMapping $letter

        # Remove the mapping from the non-elevated user session too
        Remove-UserSessionDriveMapping $letter

        # Clean up registry entries
        Remove-Item -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\$letter" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-DriveLetterMapping $letter
        Clear-AssignedLetter $id
    }

    return @{ success = $true; message = "Drive unmounted natively." } | ConvertTo-Json
}

function Install-Distro {
    return @{
        success = $false
        error = "Legacy setup path has been removed from CrossDrive."
        suggestion = "Install the current CrossDrive package; it uses the bundled native runtime."
    } | ConvertTo-Json
}

function Repair-Drivers {
    return @{
        success = $false
        error = "Legacy runtime repair path has been removed from CrossDrive."
        suggestion = "Install the current CrossDrive package; it uses the bundled native runtime."
    } | ConvertTo-Json
}

function Get-PreflightCheck {
    $winFspInstalled = $false
    try {
        $svc = Get-Service -Name "WinFsp.Launcher" -ErrorAction SilentlyContinue
        $winFspInstalled = $null -ne $svc
    }
    catch {}
    if (-not $winFspInstalled) {
        $winFspInstalled = Test-Path "C:\Program Files (x86)\WinFsp\bin\launchctl-x64.exe"
    }

    $items = @()
    $items += @{
        id = "winfsp"; title = "WinFsp Driver"; ok = $winFspInstalled
        detail = $(if ($winFspInstalled) { "Installed" } else { "WinFsp runtime missing" })
    }

    $ready = ($items | Where-Object { -not $_.ok }).Count -eq 0
    return @{
        success = $true
        ready   = $ready
        items   = $items
        note    = "CrossDrive uses its bundled native runtime. No external runtime is required."
    } | ConvertTo-Json -Depth 5
}

function Test-IsAdmin {
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
        IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Start-ElevatedPreflightFix {
    param([object[]]$Items, [string[]]$Actions)

    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }

    if ([string]::IsNullOrWhiteSpace($scriptPath) -or -not (Test-Path $scriptPath)) {
        return @{
            success        = $false
            ready          = $false
            rebootRequired = $false
            actions        = $Actions
            items          = $Items
            message        = "CrossDrive could not locate its setup helper for elevation."
        } | ConvertTo-Json -Depth 6
    }

    try {
        $argList = @(
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            "`"$scriptPath`"",
            "-Action",
            "PreflightFix"
        )
        Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs -WindowStyle Hidden | Out-Null
        return @{
            success         = $true
            ready           = $false
            rebootRequired  = $false
            elevatedStarted = $true
            actions         = @($Actions + "Started elevated native runtime repair")
            items           = $Items
            message         = "CrossDrive is repairing its bundled native runtime with Windows administrator permission."
        } | ConvertTo-Json -Depth 6
    }
    catch {
        return @{
            success        = $false
            ready          = $false
            rebootRequired = $false
            actions        = $Actions
            items          = $Items
            message        = "CrossDrive could not start elevated native runtime repair: $($_.Exception.Message)"
        } | ConvertTo-Json -Depth 6
    }
}

function Install-WinFsp {
    # Repair WinFsp only from the bundled MSI. Customer installs must not depend
    # on package managers, downloads, or separate manual setup.
    if (-not (Test-IsAdmin)) {
        return @{ success = $false; error = "Administrator rights required to install WinFsp." } | ConvertTo-Json
    }

    # 1. Try bundled MSI first
    $msiCandidates = @(
        (Join-Path $PSScriptRoot "..\prereqs\winfsp.msi"),
        (Join-Path $PSScriptRoot "..\prereqs\winfsp-2.1.25156.msi"),
        "C:\ProgramData\CrossDrive\prereqs\winfsp.msi"
    )
    foreach ($msi in $msiCandidates) {
        if (Test-Path $msi) {
            $msiAbs = (Resolve-Path $msi).Path
            [Console]::Error.WriteLine("[CrossDrive] Installing Winfsp from: $msiAbs")
            $proc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$msiAbs`" /quiet /norestart" -Wait -WindowStyle Hidden -PassThru
            if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
                Start-Sleep -Seconds 3
                return @{ success = $true; method = "msi"; path = $msiAbs } | ConvertTo-Json
            } else {
                [Console]::Error.WriteLine("[CrossDrive] WinFsp MSI install failed with exit code $($proc.ExitCode)")
            }
        }
    }

    return @{ success = $false; error = "Bundled WinFsp MSI was not found or could not be installed. Repair or reinstall CrossDrive." } | ConvertTo-Json
}

function Invoke-PreflightFix {
    # First, try to install WinFsp if missing
    $preflight = Get-PreflightCheck | ConvertFrom-Json
    $winFspItem = $preflight.items | Where-Object { $_.id -eq "winfsp" }
    $actions = @()
    $rebootRequired = $false

    if ((-not $winFspItem.ok) -and -not (Test-IsAdmin)) {
        [Console]::Error.WriteLine("[CrossDrive] Native runtime repair needs administrator permission; launching elevated repair helper...")
        return Start-ElevatedPreflightFix -Items $preflight.items -Actions $actions
    }

    if (-not $winFspItem.ok) {
        [Console]::Error.WriteLine("[CrossDrive] WinFsp missing, attempting installation...")
        $installResult = Install-WinFsp | ConvertFrom-Json
        if ($installResult.success) {
            $actions += "Installed WinFsp via $($installResult.method)"
        } else {
            return @{
                success        = $false
                ready          = $false
                rebootRequired = $false
                actions        = $actions
                items          = $preflight.items
                message        = "WinFsp installation failed: $($installResult.error)"
            } | ConvertTo-Json -Depth 6
        }
    }

    # Re-check after installation
    $finalRaw = Get-PreflightCheck
    $final = $finalRaw | ConvertFrom-Json
    return @{
        success        = $true
        ready          = [bool]$final.ready
        rebootRequired = $rebootRequired
        actions        = $actions
        items          = $final.items
        note           = $final.note
        message        = $(if ($final.ready) { "CrossDrive native runtime is ready." } else { "CrossDrive native runtime repair did not complete." })
    } | ConvertTo-Json -Depth 6
}

switch ($Action) {
    "List" { Get-Drives }
    "Mount" {
        @{
            error = "PowerShell mounting is retired."
            suggestion = "Use CrossDrive's bundled native mount engine."
            needsPassword = $false
        } | ConvertTo-Json
        [System.Environment]::Exit(0)
    }
    "Unmount" {
        $result = Remove-Drive $DriveID
        $result
        [System.Environment]::Exit(0)
    }
    "Setup" { Initialize-LegacySetup }
    "Install" { Install-Distro }
    "FixDrivers" { Repair-Drivers }
    "PreflightCheck" { Get-PreflightCheck }
    "PreflightFix" { Invoke-PreflightFix }
}
