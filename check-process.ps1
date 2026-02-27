Get-Process | Where-Object { $_.ProcessName -like '*MacMount*' -or $_.ProcessName -like '*electron*' } | Select-Object ProcessName, Id, MainWindowTitle
