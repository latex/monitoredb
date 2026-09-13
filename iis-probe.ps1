#Requires -RunAsAdministrator
$log = "C:\Users\Leandro Teixeira\source\invent\britch\monitoredb\iis-probe.log"
"=== pools via IIS:\ (WebAdministration) ===" | Out-File $log
try {
  Import-Module WebAdministration
  Get-ChildItem IIS:\AppPools | ForEach-Object { "pool: $($_.Name) state=$($_.State)" } | Out-File $log -Append
} catch { "ERRO: $($_.Exception.Message)" | Out-File $log -Append }

"=== pools via appcmd ===" | Out-File $log -Append
& "$env:windir\System32\inetsrv\appcmd.exe" list apppools 2>&1 | Out-File $log -Append

"=== requests via appcmd ===" | Out-File $log -Append
& "$env:windir\System32\inetsrv\appcmd.exe" list requests 2>&1 | Out-File $log -Append

"=== w3wp commandline ===" | Out-File $log -Append
Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'" | ForEach-Object { "pid=$($_.ProcessId) cmd=$($_.CommandLine)" } | Out-File $log -Append

"=== done ===" | Out-File $log -Append