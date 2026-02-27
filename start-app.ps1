$proc = Start-Process -FilePath 'dist\MacMount 1.0.0.exe' -PassThru -Verb RunAs
Write-Host "Started with PID: $($proc.Id)"
Start-Sleep -Seconds 5
if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) {
    Write-Host 'Process is running'
} else {
    Write-Host 'Process exited'
}
