#Requires -RunAsAdministrator
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"C:\Users\Leandro Teixeira\source\invent\britch\monitoredb\diag-system.ps1`""
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName "MonitoreDBDiag" -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName "MonitoreDBDiag"
Start-Sleep -Seconds 10
"Disparado."