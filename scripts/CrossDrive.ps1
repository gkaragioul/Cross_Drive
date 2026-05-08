# CrossDrive.ps1 - Core logic for mounting Mac drives on Windows via native bridge helpers

param (
    [Parameter(Mandatory = $false)]
    [string]$Action = "List",
    [string]$DriveID = "",
    [string]$Password = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$REG_BASE = "HKCU:\Software\CrossDrive\DriveMap"

function Resolve-ApfsFuseExe {
    # Optional override for CI, portable installs, or custom layouts.
    $envPath = $env:CROSSDRIVE_APFS_FUSE_EXE
    if ([string]::IsNullOrWhiteSpace($envPath)) {
        $envPath = $env:MACMOUNT_APFS_FUSE_EXE
    }
    if ([string]::IsNullOrWhiteSpace($envPath)) {
        $envPath = [Environment]::GetEnvironmentVariable("CROSSDRIVE_APFS_FUSE_EXE", "User")
        if ([string]::IsNullOrWhiteSpace($envPath)) {
            $envPath = [Environment]::GetEnvironmentVariable("MACMOUNT_APFS_FUSE_EXE", "User")
        }
    }
    if ([string]::IsNullOrWhiteSpace($envPath)) {
        $envPath = [Environment]::GetEnvironmentVariable("CROSSDRIVE_APFS_FUSE_EXE", "Machine")
        if ([string]::IsNullOrWhiteSpace($envPath)) {
            $envPath = [Environment]::GetEnvironmentVariable("MACMOUNT_APFS_FUSE_EXE", "Machine")
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($envPath) -and (Test-Path -LiteralPath $envPath)) {
        return (Resolve-Path -LiteralPath $envPath).Path
    }
    $candidates = @(
        (Join-Path $PSScriptRoot "..\native-bridge\apfs-fuse\build\apfs-fuse.exe"),
        (Join-Path $PSScriptRoot "..\native-bridge-bin\apfs-fuse.exe")
    )
    foreach ($c in $candidates) {
        try {
            if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path }
        }
        catch { }
    }
    return $null
}

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
    # APFS via WinFsp/FUSE is read-only — setting hidden attributes is not possible.
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

function Initialize-WSL {
    return @{
        success = $true
        ready = $true
        note = "WSL setup flow has been retired. CrossDrive now uses native Windows mount paths only."
    } | ConvertTo-Json
}

function Mount-Drive($id, $Password = "") {
    Cleanup-OrphanedCrossDriveMappings

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
        IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        return @{
            error = "Mount requires Administrator privileges."
            suggestion = "Restart CrossDrive as Administrator."
            needsPassword = $false
        } | ConvertTo-Json
    }
    
    $drivePath = "\\.\PHYSICALDRIVE$id"

    # We need an available drive letter.
    $usedLetters = [System.IO.DriveInfo]::GetDrives().Name | ForEach-Object { $_.Substring(0, 1).ToUpper() }
    $letters = "MNOPQRSTUVWXYZ".ToCharArray()
    $freeLetter = $null
    foreach ($l in $letters) {
        if ($usedLetters -notcontains $l) {
            $freeLetter = $l
            break
        }
    }

    if (-not $freeLetter) {
        return @{ error = "No free drive letters available for native Mount." } | ConvertTo-Json
    }

    $mountPoint = "$freeLetter`:"
    $mountPathForCheck = "$freeLetter`:\"

    # Detect drive format to route correctly
    $detectedFormat = "unknown"
    try {
        $mountPartitions = Get-Partition -DiskNumber $id -ErrorAction SilentlyContinue
        foreach ($mp in $mountPartitions) {
            $gType = "$($mp.GptType)"
            if ($gType -match "48465300-0000-11AA-AA11-00306543ECAC" -or $mp.Type -match "HFS") {
                $detectedFormat = "HFS+"; break
            }
            if ($gType -match "7C3457EF-0000-11AA-AA11-00306543ECAC" -or $mp.Type -match "APFS") {
                $detectedFormat = "APFS"; break
            }
            if ($mp.Type -eq "Unknown") {
                $dInfo = Get-Disk -Number $id -ErrorAction SilentlyContinue
                if ($dInfo -and $dInfo.PartitionStyle -eq "MBR" -and $mp.MbrType -eq 175) {
                    $detectedFormat = "HFS+"; break
                }
            }
        }
    } catch {}

    # HFS+ is handled by the native broker, not this PowerShell fallback
    if ($detectedFormat -eq "HFS+" -or $detectedFormat -eq "unknown") {
        return @{
            error = "This drive format ($detectedFormat) requires the native mount engine."
            suggestion = "The native broker handles HFS+/HFSX/APFS mounting. If this message appears, the native broker may not be running."
        } | ConvertTo-Json
    }

    # APFS path via apfs-fuse (dev tree, packaged resources\native-bridge-bin, or CROSSDRIVE_APFS_FUSE_EXE)
    $apfsExe = Resolve-ApfsFuseExe

    if (-not $apfsExe) {
        return @{
            error = "Native apfs-fuse driver not found."
            suggestion = "Build apfs-fuse into native-bridge\apfs-fuse\build\, place apfs-fuse.exe in native-bridge-bin\, or set CROSSDRIVE_APFS_FUSE_EXE to the full path."
        } | ConvertTo-Json
    }

    function Get-LogTail([string]$path, [int]$maxLines = 40) {
        try {
            if (Test-Path $path) {
                return (Get-Content -Path $path -Tail $maxLines -ErrorAction SilentlyContinue) -join "`n"
            }
        }
        catch {}
        return ""
    }

    # Arguments
    $passArgs = if ($Password) { @("-r", "$Password") } else { @() }
    $partitionArgs = @()
    try {
        $apfsPartition = Get-Partition -DiskNumber $id -ErrorAction SilentlyContinue |
            Where-Object { "$($_.GptType)" -match "7C3457EF-0000-11AA-AA11-00306543ECAC" } |
            Select-Object -First 1
        if ($apfsPartition -and $apfsPartition.PartitionNumber) {
            # apfs-fuse uses 0-based GPT array index; Windows PartitionNumber is 1-based
            $apfsFusePartIdx = $apfsPartition.PartitionNumber - 1
            $partitionArgs = @("-p", "$apfsFusePartIdx")
        }
    } catch {}

    $cmdArgs = @()
    $cmdArgs += @("-d", "1")
    $cmdArgs += $partitionArgs
    $cmdArgs += @("-v", "0")
    $cmdArgs += $passArgs
    $cmdArgs += $drivePath
    $cmdArgs += $mountPoint

    $apfsDir = Split-Path $apfsExe
    $errLog = "C:\ProgramData\CrossDrive\apfs_error.log"
    $outLog = "C:\ProgramData\CrossDrive\apfs_out.log"
    New-Item -Path "C:\ProgramData\CrossDrive" -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

    # Kill any orphaned apfs-fuse processes for a clean start
    $orphans = Get-Process apfs-fuse -ErrorAction SilentlyContinue
    if ($orphans) {
        foreach ($op in $orphans) {
            try { $op | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
            try {
                $wmi = Get-WmiObject Win32_Process -Filter "ProcessId = $($op.Id)" -ErrorAction SilentlyContinue
                if ($wmi) { $wmi.Terminate() | Out-Null }
            } catch {}
        }
        Start-Sleep -Milliseconds 500
    }

    # Load DefineDosDevice for manual drive letter assignment.
    # WinFsp's FspMountSet can silently fail to assign the drive letter in elevated contexts,
    # so we poll lsvol for the WinFsp device and assign it manually with the kernel32 API.
    if (-not ([System.Management.Automation.PSTypeName]'CrossDriveDosDevice').Type) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class CrossDriveDosDevice {
    // DDD_RAW_TARGET_PATH = 1: treat lpTargetPath as an NT namespace path
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
'@ -ErrorAction SilentlyContinue
    }

    # WinFsp tool for volume discovery
    $fsptool = "C:\Program Files (x86)\WinFsp\bin\fsptool-x64.exe"

    # Snapshot existing WinFsp volumes BEFORE launch so we can identify our new one
    $preExisting = @()
    if (Test-Path $fsptool) {
        $preExisting = @(& $fsptool lsvol 2>$null | Where-Object { $_ -match '\\Device\\Volume' } |
            ForEach-Object { if ($_ -match '\\Device\\Volume\{[^\}]+\}') { $Matches[0] } })
    }
    [Console]::Error.WriteLine("[CrossDrive] Pre-existing WinFsp devices: $($preExisting -join ',')")

    [Console]::Error.WriteLine("[CrossDrive] Starting apfs-fuse: $($cmdArgs -join ' ')")
    if (Test-Path $errLog) { Remove-Item $errLog -Force }
    if (Test-Path $outLog) { Remove-Item $outLog -Force }

    $proc = Start-Process -FilePath $apfsExe -ArgumentList $cmdArgs -WorkingDirectory $apfsDir -WindowStyle Hidden -RedirectStandardError $errLog -RedirectStandardOutput $outLog -PassThru

    # Poll for up to 20 seconds; bail early if the process exits (crash/error)
    $elapsed = 0
    $deadline = (Get-Date).AddSeconds(20)
    $winfspDevice = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        $elapsed++
        $alive = -not $proc.HasExited
        $mounted = Test-Path $mountPathForCheck

        if ($mounted) {
            [Console]::Error.WriteLine("[CrossDrive] t+${elapsed}s alive=$alive mounted=True")
            break
        }

        # If not yet mounted and process is alive, check lsvol for our new WinFsp device
        if ($alive -and -not $winfspDevice -and (Test-Path $fsptool)) {
            $lsvolLines = @(& $fsptool lsvol 2>$null)
            [Console]::Error.WriteLine("[CrossDrive] t+${elapsed}s lsvol: $($lsvolLines -join ' | ')")

            foreach ($line in $lsvolLines) {
                if ($line -match '\\Device\\Volume\{[^\}]+\}') {
                    $candidate = $Matches[0]
                    # Only use devices that appeared after we started (not pre-existing)
                    if ($preExisting -notcontains $candidate) {
                        # Check it has no mount point yet (line starts with -)
                        if ($line -match '^\s*-\s') {
                            $winfspDevice = $candidate
                            [Console]::Error.WriteLine("[CrossDrive] t+${elapsed}s Found new WinFsp device (no mount point): $winfspDevice")
                            break
                        }
                        elseif ($line -match '^\s*[A-Z]:') {
                            # Already has a drive letter assigned - great, just wait for FS
                            [Console]::Error.WriteLine("[CrossDrive] t+${elapsed}s Device already has drive letter assigned")
                            $winfspDevice = $candidate
                            break
                        }
                    }
                }
            }

            # If we found a device with no drive letter, assign one manually
            if ($winfspDevice -and -not $mounted) {
                $ddResult = [CrossDriveDosDevice]::DefineDosDevice(1, $mountPoint, $winfspDevice)
                $ddErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
                [Console]::Error.WriteLine("[CrossDrive] t+${elapsed}s DefineDosDevice('$mountPoint','$winfspDevice'): $ddResult err=$ddErr")
                Start-Sleep -Milliseconds 200
                $mounted = Test-Path $mountPathForCheck
            }
        }
        elseif (-not $alive) {
            [Console]::Error.WriteLine("[CrossDrive] t+${elapsed}s alive=False mounted=$mounted")
            break
        }
        else {
            [Console]::Error.WriteLine("[CrossDrive] t+${elapsed}s alive=$alive mounted=$mounted device=$winfspDevice")
        }

        if ($mounted) { break }
    }

    if (Test-Path $mountPathForCheck) {
        Set-AssignedLetter $id $freeLetter

        # Set a friendly drive label in registry
        $iconRegPath = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\$freeLetter"
        New-Item -Path "$iconRegPath\DefaultLabel" -Force | Out-Null
        Set-ItemProperty -Path "$iconRegPath\DefaultLabel" -Name "(Default)" -Value "Mac Drive ($id)"

        # Try to set icon
        $iconFile = Join-Path $PSScriptRoot "..\public\favicon.ico"
        if (Test-Path $iconFile) {
            $iconAbsolute = (Resolve-Path $iconFile).Path
            New-Item -Path "$iconRegPath\DefaultIcon" -Force | Out-Null
            Set-ItemProperty -Path "$iconRegPath\DefaultIcon" -Name "(Default)" -Value $iconAbsolute
        }

        # If lsvol didn't find the device during the polling loop (e.g. FspMountSet
        # succeeded and assigned the letter directly), look it up now for the
        # user-session mapping.
        if (-not $winfspDevice -and (Test-Path $fsptool)) {
            $postLines = @(& $fsptool lsvol 2>$null)
            foreach ($pl in $postLines) {
                if ($pl -match '\\Device\\Volume\{[^\}]+\}') {
                    $candidate = $Matches[0]
                    if ($preExisting -notcontains $candidate) {
                        $winfspDevice = $candidate
                        [Console]::Error.WriteLine("[CrossDrive] Post-mount lsvol found device: $winfspDevice")
                        break
                    }
                }
            }
        }

        # Map the drive letter in the non-elevated user session so Explorer can see it.
        # Elevated processes have a separate DOS-device namespace; without this mapping
        # the drive is invisible to Explorer and all non-elevated applications.
        if ($winfspDevice) {
            [Console]::Error.WriteLine("[CrossDrive] Creating user-session mapping: ${freeLetter}: -> $winfspDevice")
            Set-UserSessionDriveMapping $freeLetter $winfspDevice
        } else {
            [Console]::Error.WriteLine("[CrossDrive] WARNING: No WinFsp device found for user-session mapping")
        }

        # APFS containers expose a root/ subfolder containing the actual volume data.
        # Return that as the effective path so the UI opens the right location.
        $effectivePath = $mountPathForCheck
        $rootSub = Join-Path $mountPathForCheck "root"
        if (Test-Path $rootSub) {
            $effectivePath = "$rootSub\"
        }

        # Hide macOS metadata dot-folders (.fseventsd, .Spotlight-V100, etc.)
        Hide-MacMetadataItems $effectivePath

        return @{
            success     = $true
            path        = $effectivePath
            driveLetter = $freeLetter
            mountType   = "native_winfsp"
        } | ConvertTo-Json
    }

    # If it failed to mount — clean up process and any partial DOS device mapping
    try { $proc | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
    try {
        $wmi2 = Get-WmiObject Win32_Process -Filter "ProcessId = $($proc.Id)" -ErrorAction SilentlyContinue
        if ($wmi2) { $wmi2.Terminate() | Out-Null }
    } catch {}
    # Remove any DOS device mapping we created for the drive letter
    if ($winfspDevice) {
        try { [CrossDriveDosDevice]::DefineDosDevice(2+1+4, $mountPoint, $winfspDevice) | Out-Null } catch {}
    }
    
    $errText = Get-LogTail $errLog
    $outText = Get-LogTail $outLog
    $combined = ("$errText`n$outText").ToLowerInvariant()
    $needsPassword = ($combined -match 'passphrase|password|encrypted|crypto|wrong key|invalid key')

    return @{
        error = "Failed to mount drive using Native WinFSP driver."
        needsPassword = $needsPassword
        suggestion = $(if ($needsPassword) { "This APFS volume appears encrypted. Enter the disk password and retry." } else { "Check admin rights, WinFsp runtime, and APFS driver output in ProgramData logs." })
        details = @{
            errTail = $errText
            outTail = $outText
            processExited = $proc.HasExited
            processExitCode = $(try { if ($proc.HasExited) { $proc.Refresh() | Out-Null; $proc.ExitCode } else { $null } } catch { $null })
            drivePath = $drivePath
            mountPoint = $mountPoint
            mountCheckPath = $mountPathForCheck
            args = ($cmdArgs -join ' ')
            partitionHint = $(if ($apfsPartition) { $apfsPartition.PartitionNumber } else { $null })
        }
    } | ConvertTo-Json -Depth 6
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

    # Kill native apfs-fuse process (elevated — needs WMI fallback)
    $procs = Get-Process apfs-fuse -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        try { $p | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
        try {
            $wmi = Get-WmiObject Win32_Process -Filter "ProcessId = $($p.Id)" -ErrorAction SilentlyContinue
            if ($wmi) { $wmi.Terminate() | Out-Null }
        } catch {}
    }

    return @{ success = $true; message = "Drive unmounted natively." } | ConvertTo-Json
}

function Install-Distro {
    return @{
        success = $false
        error = "WSL bootstrap has been removed from CrossDrive."
        suggestion = "Install WinFsp and ship native bridge binaries instead."
    } | ConvertTo-Json
}

function Repair-Drivers {
    return @{
        success = $false
        error = "WSL-based driver repair has been removed from CrossDrive."
        suggestion = "Bundle WinFsp and the native bridge binaries with the app installer."
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

    $nativeBridgePath = Resolve-ApfsFuseExe
    $nativeBridgeReady = -not [string]::IsNullOrWhiteSpace($nativeBridgePath)

    $items = @()
    $items += @{
        id = "winfsp"; title = "WinFsp Driver"; ok = $winFspInstalled
        detail = $(if ($winFspInstalled) { "Installed" } else { "WinFsp runtime missing" })
    }
    $items += @{
        id = "nativeBridge"; title = "Native APFS Bridge (optional fallback)"; ok = $true
        detail = $(if ($nativeBridgeReady) { $nativeBridgePath } else { "Not installed (optional). Native broker handles APFS directly." })
    }

    $ready = ($items | Where-Object { -not $_.ok }).Count -eq 0
    return @{
        success = $true
        ready   = $ready
        items   = $items
        note    = "WSL-based checks have been removed. CrossDrive now validates only native Windows components."
    } | ConvertTo-Json -Depth 5
}

function Install-WinFsp {
    # Try to install WinFsp from the bundled MSI, then fall back to winget.
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
        IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
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

    # 2. Fall back to winget
    [Console]::Error.WriteLine("[CrossDrive] Falling back to winget for WinFsp installation")
    try {
        $wingetProc = Start-Process -FilePath "winget" -ArgumentList "install --id WinFsp.WinFsp --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -PassThru
        if ($wingetProc.ExitCode -eq 0 -or $wingetProc.ExitCode -eq 3010) {
            Start-Sleep -Seconds 3
            return @{ success = $true; method = "winget" } | ConvertTo-Json
        }
    } catch {
        [Console]::Error.WriteLine("[CrossDrive] winget install failed: $_")
    }

    return @{ success = $false; error = "WinFsp installation failed. Please install manually from https://github.com/winfsp/winfsp/releases" } | ConvertTo-Json
}

function Invoke-PreflightFix {
    # First, try to install WinFsp if missing
    $preflight = Get-PreflightCheck | ConvertFrom-Json
    $winFspItem = $preflight.items | Where-Object { $_.id -eq "winfsp" }
    $actions = @()

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
        success        = [bool]$final.ready
        ready          = [bool]$final.ready
        rebootRequired = $false
        actions        = $actions
        items          = $final.items
        message        = $(if ($final.ready) { "Native runtime is ready." } else { "Native runtime is missing required Windows components." })
    } | ConvertTo-Json -Depth 6
}

switch ($Action) {
    "List" { Get-Drives }
    "Mount" {
        # Force-exit after output so Node.js exec() doesn't hang waiting for
        # inherited pipe handles held open by the background apfs-fuse process.
        $result = Mount-Drive $DriveID $Password
        $result
        [System.Environment]::Exit(0)
    }
    "Unmount" {
        $result = Remove-Drive $DriveID
        $result
        [System.Environment]::Exit(0)
    }
    "Setup" { Initialize-WSL }
    "Install" { Install-Distro }
    "FixDrivers" { Repair-Drivers }
    "PreflightCheck" { Get-PreflightCheck }
    "PreflightFix" { Invoke-PreflightFix }
}

