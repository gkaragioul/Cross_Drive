# MacMount.ps1 - Core Logic for Mounting Mac Drives on Windows via WSL2

param (
    [Parameter(Mandatory = $false)]
    [string]$Action = "List",
    [string]$DriveID = "",
    [string]$Password = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$REG_BASE = "HKCU:\Software\MacMount\DriveMap"

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
        $scriptPath = "C:\ProgramData\MacMount\user-mount.ps1"
        $scriptContent = @"
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class UM{[DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Auto)]public static extern bool DefineDosDevice(uint f,string d,string t);}' -ErrorAction SilentlyContinue
[UM]::DefineDosDevice(1, "${letter}:", "$devicePath")
"@
        Set-Content -Path $scriptPath -Value $scriptContent -Force -Encoding UTF8
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$scriptPath`""
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
        $task = New-ScheduledTask -Action $action -Principal $principal
        Register-ScheduledTask -TaskName "MacMountMap$letter" -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName "MacMountMap$letter" | Out-Null
        Start-Sleep -Milliseconds 500
        Unregister-ScheduledTask -TaskName "MacMountMap$letter" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    } catch {}
}

function Remove-UserSessionDriveMapping($letter) {
    # Remove the drive letter from the non-elevated user session.
    # Uses QueryDosDevice to find the exact target, then DefineDosDevice with
    # DDD_REMOVE_DEFINITION|DDD_RAW_TARGET_PATH|DDD_EXACT_MATCH_ON_REMOVE (7)
    # to remove the precise mapping we created during mount.
    try {
        $scriptPath = "C:\ProgramData\MacMount\user-unmount.ps1"
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
        $task = New-ScheduledTask -Action $action -Principal $principal
        Register-ScheduledTask -TaskName "MacMountUnmap$letter" -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName "MacMountUnmap$letter" | Out-Null
        Start-Sleep -Milliseconds 1000
        Unregister-ScheduledTask -TaskName "MacMountUnmap$letter" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    } catch {}
}

function Invoke-InteractiveHiddenCommand($taskName, $command) {
    try {
        $escaped = $command.Replace('"', '`"')
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"$escaped`""
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
        $task = New-ScheduledTask -Action $action -Principal $principal
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
    try { Invoke-InteractiveHiddenCommand "MacMountMapCleanup$letter" "subst /d ${letter}: >`$null 2>&1; net use ${letter}: /delete /y >`$null 2>&1" } catch {}
}

function Hide-MacMetadataItems($rootPath) {
    # APFS via WinFsp/FUSE is read-only — setting hidden attributes is not possible.
    # macOS dot-folders (.fseventsd, .Spotlight-V100, etc.) will remain visible.
    # This is a no-op; kept for future use if a writable mount becomes available.
}

function Cleanup-OrphanedMacMountMappings {
    try {
        $lines = cmd.exe /c subst 2>$null
        foreach ($line in $lines) {
            if ($line -match '^\s*([A-Z]):\\:\s*=>\s*(.+)$') {
                $letter = $matches[1].ToUpper()
                $target = $matches[2].Trim()
                if ($letter -in @('M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z') -and $target -like 'C:\ProgramData\MacMount\Drive*') {
                    Remove-DriveLetterMapping $letter
                }
            }
        }
    }
    catch {}
}

function Get-Drives {
    $disks = Get-PhysicalDisk | Select-Object DeviceID, FriendlyName, Size, MediaType

    $wslHealthy = $false
    wsl -u root -e whoami 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $wslHealthy = $true }

    $result = @()
    foreach ($disk in $disks) {
        $id = $disk.DeviceID
        $isMac = $false
        $format = "Windows/Unknown"

        try {
            $partitions = Get-Partition -DiskNumber $id -ErrorAction SilentlyContinue
            if ($partitions) {
                foreach ($p in $partitions) {
                    $type = "$($p.GptType)"
                    if ($type -match "48465300-0000-11AA-AA11-00306543ECAC" -or $p.Type -match "HFS") {
                        $isMac = $true; $format = "HFS+"; break
                    }
                    if ($type -match "7C3457EF-0000-11AA-AA11-00306543ECAC" -or $p.Type -match "APFS") {
                        $isMac = $true; $format = "APFS"; break
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
                Remove-DriveLetterMapping $driveLetter
                $linkPath = "C:\ProgramData\MacMount\Drive$id"
                if (Test-Path $linkPath) { cmd.exe /c "rmdir `"$linkPath`"" 2>$null | Out-Null }
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
            uncPath     = "\\wsl.localhost\Ubuntu\mnt\mac_drive_$id"
            driveLetter = $driveLetter
            format      = $format
            isMac       = $isMac
        }
    }
    return $result | ConvertTo-Json
}

function Initialize-WSL {
    if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
        return @{ error = "WSL2 is not installed." }
    }
    wsl -e whoami 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        return @{ error = "Ubuntu not ready."; suggestion = "wsl --install Ubuntu" }
    }
    return @{ success = $true }
}

function Mount-Drive($id, $Password = "") {
    Cleanup-OrphanedMacMountMappings

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
        IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        return @{
            error = "Mount requires Administrator privileges."
            suggestion = "Restart MacMount as Administrator."
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
    $apfsExe = Join-Path $PSScriptRoot "..\native-bridge\apfs-fuse\build\apfs-fuse.exe"

    if (-not (Test-Path $apfsExe)) {
        return @{ error = "Native apfs-fuse driver not compiled." } | ConvertTo-Json
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
    $errLog = "C:\ProgramData\MacMount\apfs_error.log"
    $outLog = "C:\ProgramData\MacMount\apfs_out.log"
    New-Item -Path "C:\ProgramData\MacMount" -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

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
    if (-not ([System.Management.Automation.PSTypeName]'MacMountDosDevice').Type) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class MacMountDosDevice {
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
    [Console]::Error.WriteLine("[MacMount] Pre-existing WinFsp devices: $($preExisting -join ',')")

    [Console]::Error.WriteLine("[MacMount] Starting apfs-fuse: $($cmdArgs -join ' ')")
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
            [Console]::Error.WriteLine("[MacMount] t+${elapsed}s alive=$alive mounted=True")
            break
        }

        # If not yet mounted and process is alive, check lsvol for our new WinFsp device
        if ($alive -and -not $winfspDevice -and (Test-Path $fsptool)) {
            $lsvolLines = @(& $fsptool lsvol 2>$null)
            [Console]::Error.WriteLine("[MacMount] t+${elapsed}s lsvol: $($lsvolLines -join ' | ')")

            foreach ($line in $lsvolLines) {
                if ($line -match '\\Device\\Volume\{[^\}]+\}') {
                    $candidate = $Matches[0]
                    # Only use devices that appeared after we started (not pre-existing)
                    if ($preExisting -notcontains $candidate) {
                        # Check it has no mount point yet (line starts with -)
                        if ($line -match '^\s*-\s') {
                            $winfspDevice = $candidate
                            [Console]::Error.WriteLine("[MacMount] t+${elapsed}s Found new WinFsp device (no mount point): $winfspDevice")
                            break
                        }
                        elseif ($line -match '^\s*[A-Z]:') {
                            # Already has a drive letter assigned - great, just wait for FS
                            [Console]::Error.WriteLine("[MacMount] t+${elapsed}s Device already has drive letter assigned")
                            $winfspDevice = $candidate
                            break
                        }
                    }
                }
            }

            # If we found a device with no drive letter, assign one manually
            if ($winfspDevice -and -not $mounted) {
                $ddResult = [MacMountDosDevice]::DefineDosDevice(1, $mountPoint, $winfspDevice)
                $ddErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
                [Console]::Error.WriteLine("[MacMount] t+${elapsed}s DefineDosDevice('$mountPoint','$winfspDevice'): $ddResult err=$ddErr")
                Start-Sleep -Milliseconds 200
                $mounted = Test-Path $mountPathForCheck
            }
        }
        elseif (-not $alive) {
            [Console]::Error.WriteLine("[MacMount] t+${elapsed}s alive=False mounted=$mounted")
            break
        }
        else {
            [Console]::Error.WriteLine("[MacMount] t+${elapsed}s alive=$alive mounted=$mounted device=$winfspDevice")
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
                        [Console]::Error.WriteLine("[MacMount] Post-mount lsvol found device: $winfspDevice")
                        break
                    }
                }
            }
        }

        # Map the drive letter in the non-elevated user session so Explorer can see it.
        # Elevated processes have a separate DOS-device namespace; without this mapping
        # the drive is invisible to Explorer and all non-elevated applications.
        if ($winfspDevice) {
            [Console]::Error.WriteLine("[MacMount] Creating user-session mapping: ${freeLetter}: -> $winfspDevice")
            Set-UserSessionDriveMapping $freeLetter $winfspDevice
        } else {
            [Console]::Error.WriteLine("[MacMount] WARNING: No WinFsp device found for user-session mapping")
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
        try { [MacMountDosDevice]::DefineDosDevice(2+1+4, $mountPoint, $winfspDevice) | Out-Null } catch {}
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
        # Remove the DOS device mapping created by DefineDosDevice
        if (-not ([System.Management.Automation.PSTypeName]'MacMountDosDevice').Type) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class MacMountDosDevice {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
'@ -ErrorAction SilentlyContinue
        }
        $sb = New-Object System.Text.StringBuilder 512
        $qLen = [MacMountDosDevice]::QueryDosDevice("${letter}:", $sb, 512)
        if ($qLen -gt 0) {
            # DDD_REMOVE_DEFINITION=2, DDD_RAW_TARGET_PATH=1, DDD_EXACT_MATCH_ON_REMOVE=4
            [MacMountDosDevice]::DefineDosDevice(7, "${letter}:", $sb.ToString()) | Out-Null
        }

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
    $cmd = "wsl --install Ubuntu; pause"
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "`"$cmd`"" -Verb RunAs
    return @{ success = $true } | ConvertTo-Json
}

function Repair-Drivers {
    $pkgs = 'hfsprogs fuse libfuse-dev build-essential cmake git pkg-config libicu-dev bzip2 libbz2-dev zlib1g-dev'
    wsl -u root -e sh -c "DEBIAN_FRONTEND=noninteractive apt-get update -qq 2>&1 && apt-get install -y $pkgs 2>&1" | Out-Null

    $hfsfuseCheck = wsl -u root -e sh -c 'command -v hfsfuse 2>/dev/null' 2>&1
    $hfsfuseInstalled = ($LASTEXITCODE -eq 0 -and $hfsfuseCheck)

    if (-not $hfsfuseInstalled) {
        wsl -u root -e sh -c 'cd /tmp && rm -rf hfsfuse && git clone --depth 1 https://github.com/0x09/hfsfuse.git 2>&1 && cd hfsfuse && make -j$(nproc) 2>&1 && make install 2>&1' | Out-Null
        $hfsfuseCheck2 = wsl -u root -e sh -c 'command -v hfsfuse 2>/dev/null' 2>&1
        $hfsfuseInstalled = ($LASTEXITCODE -eq 0 -and $hfsfuseCheck2)
    }

    $apfsCheck = wsl -u root -e sh -c 'command -v apfs-fuse 2>/dev/null' 2>&1
    $apfsInstalled = ($LASTEXITCODE -eq 0 -and $apfsCheck)

    if (-not $apfsInstalled) {
        wsl -u root -e sh -c 'cd /tmp && rm -rf apfs-fuse && git clone --depth 1 https://github.com/sgan81/apfs-fuse.git 2>&1 && cd apfs-fuse && git submodule update --init 2>&1 && mkdir -p build && cd build && cmake .. 2>&1 && make -j$(nproc) 2>&1 && make install 2>&1' | Out-Null
        $apfsCheck2 = wsl -u root -e sh -c 'command -v apfs-fuse 2>/dev/null' 2>&1
        $apfsInstalled = ($LASTEXITCODE -eq 0 -and $apfsCheck2)
    }

    wsl -u root -e sh -c 'dpkg -l hfsprogs 2>/dev/null | grep -q "^ii"' 2>&1 | Out-Null
    $hfsOk = ($LASTEXITCODE -eq 0)

    # Make FUSE mounts accessible across user boundaries (required for Windows 9P access to WSL mounts)
    wsl -u root -e sh -c "sed -i 's/^#user_allow_other/user_allow_other/g' /etc/fuse.conf" 2>$null | Out-Null

    if ($hfsfuseInstalled -or $hfsOk -or $apfsInstalled) {
        return @{ success = $true; message = "Drivers ready. hfsfuse=$hfsfuseInstalled, apfs-fuse=$apfsInstalled" } | ConvertTo-Json
    }
    return @{ success = $false; error = "Driver build failed. Check WSL internet access." } | ConvertTo-Json
}

function Get-PreflightCheck {
    $items = @()
    $wslCommand = Get-Command wsl -ErrorAction SilentlyContinue
    $wslInstalled = $null -ne $wslCommand
    $ubuntuInstalled = $false
    $wslRootReady = $false
    $hfsfuseInstalled = $false
    $apfsInstalled = $false
    $winFspInstalled = $false

    if ($wslInstalled) {
        try {
            $distros = @(
                wsl -l -q 2>$null |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ -ne '' }
            )
            $ubuntuInstalled = ($distros -contains "Ubuntu")
        }
        catch {}
    }

    if ($wslInstalled -and $ubuntuInstalled) {
        wsl -d Ubuntu -u root -e sh -c 'echo ok' 2>$null | Out-Null
        $wslRootReady = ($LASTEXITCODE -eq 0)
    }

    if ($wslRootReady) {
        wsl -d Ubuntu -u root -e sh -c 'command -v hfsfuse >/dev/null 2>&1' 2>$null | Out-Null
        $hfsfuseInstalled = ($LASTEXITCODE -eq 0)
        wsl -d Ubuntu -u root -e sh -c 'command -v apfs-fuse >/dev/null 2>&1' 2>$null | Out-Null
        $apfsInstalled = ($LASTEXITCODE -eq 0)
    }

    try {
        $svc = Get-Service -Name "WinFsp.Launcher" -ErrorAction SilentlyContinue
        $winFspInstalled = $null -ne $svc
    }
    catch {}
    if (-not $winFspInstalled) {
        $winFspInstalled = Test-Path "C:\Program Files (x86)\WinFsp\bin\launchctl-x64.exe"
    }

    $items += @{
        id = "wsl"; title = "WSL Platform"; ok = $wslInstalled
        detail = $(if ($wslInstalled) { "Installed" } else { "WSL not detected" })
    }
    $items += @{
        id = "ubuntu"; title = "Ubuntu Distro"; ok = $ubuntuInstalled
        detail = $(if ($ubuntuInstalled) { "Installed" } else { "Ubuntu distro missing" })
    }
    $items += @{
        id = "wslRoot"; title = "Ubuntu Root Access"; ok = $wslRootReady
        detail = $(if ($wslRootReady) { "Ready" } else { "Cannot run root commands in Ubuntu yet" })
    }
    $items += @{
        id = "winfsp"; title = "WinFsp Driver"; ok = $winFspInstalled
        detail = $(if ($winFspInstalled) { "Installed" } else { "WinFsp runtime missing" })
    }
    $items += @{
        id = "hfsfuse"; title = "HFS Driver"; ok = $hfsfuseInstalled
        detail = $(if ($hfsfuseInstalled) { "Installed" } else { "hfsfuse missing" })
    }
    $items += @{
        id = "apfsfuse"; title = "APFS Driver"; ok = $apfsInstalled
        detail = $(if ($apfsInstalled) { "Installed" } else { "apfs-fuse missing" })
    }

    $ready = ($items | Where-Object { -not $_.ok }).Count -eq 0
    return @{
        success = $true
        ready   = $ready
        items   = $items
    } | ConvertTo-Json -Depth 5
}

function Invoke-PreflightFix {
    $actions = @()
    $rebootRequired = $false

    $checkRaw = Get-PreflightCheck
    $check = $checkRaw | ConvertFrom-Json

    $hasWsl = ($check.items | Where-Object { $_.id -eq "wsl" -and $_.ok }) -ne $null
    $hasUbuntu = ($check.items | Where-Object { $_.id -eq "ubuntu" -and $_.ok }) -ne $null
    $hasRoot = ($check.items | Where-Object { $_.id -eq "wslRoot" -and $_.ok }) -ne $null
    $hasWinFsp = ($check.items | Where-Object { $_.id -eq "winfsp" -and $_.ok }) -ne $null
    $hasHfs = ($check.items | Where-Object { $_.id -eq "hfsfuse" -and $_.ok }) -ne $null
    $hasApfs = ($check.items | Where-Object { $_.id -eq "apfsfuse" -and $_.ok }) -ne $null

    if (-not $hasWsl -or -not $hasUbuntu) {
        $installOut = wsl --install -d Ubuntu --no-launch 2>&1
        $installText = ($installOut | Out-String).Trim()
        $actions += @{
            step   = "wslInstall"
            ok     = ($LASTEXITCODE -eq 0)
            detail = $(if ($LASTEXITCODE -eq 0) { "WSL/Ubuntu install command completed." } else { $installText })
        }
        if ($LASTEXITCODE -eq 0) { $rebootRequired = $true }
    }

    if (-not $hasWinFsp) {
        $localMsi = @(
            "C:\Program Files\MacMount\resources\prereqs\winfsp.msi",
            "C:\Program Files\MacMount\resources\prereqs\WinFsp.msi",
            (Join-Path $PSScriptRoot "..\prereqs\winfsp.msi"),
            (Join-Path $PSScriptRoot "..\prereqs\WinFsp.msi")
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if ($localMsi) {
            $msiPath = (Resolve-Path $localMsi).Path
            $msiOut = Start-Process msiexec.exe -ArgumentList "/i `"$msiPath`" /qn /norestart" -PassThru -Wait
            $actions += @{
                step   = "winfspInstall"
                ok     = ($msiOut.ExitCode -eq 0)
                detail = $(if ($msiOut.ExitCode -eq 0) { "WinFsp installed from bundled installer." } else { "Bundled WinFsp installer failed with exit code $($msiOut.ExitCode)." })
            }
        }
        else {
            $winget = Get-Command winget -ErrorAction SilentlyContinue
            if ($winget) {
                $wgOut = winget install --id WinFsp.WinFsp -e --silent --accept-package-agreements --accept-source-agreements 2>&1
                $wgText = ($wgOut | Out-String).Trim()
                $actions += @{
                    step   = "winfspInstall"
                    ok     = ($LASTEXITCODE -eq 0)
                    detail = $(if ($LASTEXITCODE -eq 0) { "WinFsp installed through winget." } else { $wgText })
                }
            }
            else {
                $actions += @{
                    step   = "winfspInstall"
                    ok     = $false
                    detail = "WinFsp installer not bundled and winget is unavailable. Install WinFsp manually: https://winfsp.dev/rel/"
                }
            }
        }
    }

    $postRaw = Get-PreflightCheck
    $post = $postRaw | ConvertFrom-Json
    $postHasRoot = ($post.items | Where-Object { $_.id -eq "wslRoot" -and $_.ok }) -ne $null
    $postHasHfs = ($post.items | Where-Object { $_.id -eq "hfsfuse" -and $_.ok }) -ne $null
    $postHasApfs = ($post.items | Where-Object { $_.id -eq "apfsfuse" -and $_.ok }) -ne $null

    if ($postHasRoot -and (-not $postHasHfs -or -not $postHasApfs)) {
        $repairRaw = Repair-Drivers
        try {
            $repair = $repairRaw | ConvertFrom-Json
            $actions += @{
                step   = "driverRepair"
                ok     = [bool]$repair.success
                detail = $(if ($repair.success) { $repair.message } else { $repair.error })
            }
        }
        catch {
            $actions += @{
                step   = "driverRepair"
                ok     = $false
                detail = "Could not parse driver repair response."
            }
        }
    }

    $finalRaw = Get-PreflightCheck
    $final = $finalRaw | ConvertFrom-Json
    return @{
        success        = [bool]$final.ready
        ready          = [bool]$final.ready
        rebootRequired = $rebootRequired
        actions        = $actions
        items          = $final.items
        message        = $(if ($final.ready) { "Environment is ready." } else { "Some prerequisites still need attention." })
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
