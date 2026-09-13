#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
Start-Transcript -Path "C:\Users\Leandro Teixeira\source\invent\britch\monitoredb\create-site.log" -Force

Import-Module WebAdministration

$siteName = "MonitoreDB-Demo"
$sitePath = "C:\inetpub\wwwroot\monitoredb"
New-Item -ItemType Directory -Path $sitePath -Force | Out-Null
@"
<!DOCTYPE html>
<html lang="pt-BR">
<head><meta charset="UTF-8"><title>MonitoreDB - Site Demo</title>
<style>body{font-family:system-ui;background:#0f1117;color:#e1e4eb;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}div{text-align:center}h1{color:#6366f1}p{color:#8b8fa3}</style></head>
<body><div><h1>MonitoreDB IIS Demo</h1><p>Este site esta sendo servido pelo IIS do host davi.</p></div></body>
</html>
"@ | Set-Content -Path "$sitePath\index.html" -Encoding UTF8

$site = Get-Website -Name $siteName -ErrorAction SilentlyContinue
if ($site) {
    Set-ItemProperty "IIS:\Sites\$siteName" -Name physicalPath -Value $sitePath
    Start-Website -Name $siteName
} else {
    New-Website -Name $siteName -PhysicalPath $sitePath -Port 8080 -Force | Out-Null
}

$a = Get-Website -Name $siteName
"Site: $($a.name) | Estado: $($a.state) | Porta: 8080 | Path: $($a.physicalPath)"

Stop-Transcript