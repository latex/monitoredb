#Requires -RunAsAdministrator
$out = "C:\Users\Leandro Teixeira\source\invent\britch\monitoredb\diag-system.log"
"=== Teste como SYSTEM ===" | Set-Content $out
"Whoami: $((whoami).Trim())" | Add-Content $out
$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
"appcmd exists: $(Test-Path $appcmd)" | Add-Content $out
"--- appcmd list apppools ---" | Add-Content $out
& $appcmd list apppools 2>&1 | Add-Content $out
"--- appcmd list apppools /xml (primeiras 500 chars) ---" | Add-Content $out
$xml = (& $appcmd list apppools /xml 2>&1) | Out-String
if ($xml.Length -gt 500) { $xml.Substring(0,500) } else { $xml } | Add-Content $out
"--- agent para testar sqlcmd SYSTEM ---" | Add-Content $out
& "C:\Users\Leandro Teixeira\AppData\Local\Microsoft\sqlcmd\sqlcmd.exe" -S "localhost,1433" -U sa -P "Monitoredb!2026" -C -Q "SELECT 1" -h -1 -W 2>&1 | Add-Content $out
"=== FIM ===" | Add-Content $out