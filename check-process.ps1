Get-Process | Where-Object { $_.ProcessName -like '*CrossDrive*' -or $_.ProcessName -like '*electron*' } | Select-Object ProcessName, Id, MainWindowTitle

