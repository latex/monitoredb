#Requires -RunAsAdministrator
$log = "C:\Users\Leandro Teixeira\source\invent\britch\monitoredb\diag-all.log"
"=== Diagnostico elevado ===" | Set-Content $log
"whoami: $((whoami).Trim())" | Add-Content $log

# 1) Dono do processo do agente
$agent = Get-CimInstance Win32_Process -Filter "Name='monitoredb-agent.exe'" | Select-Object -First 1
if ($agent) {
    "agent PID: $($agent.ProcessId) criado: $($agent.CreationDate)" | Add-Content $log
    $o = $agent.GetOwner()
    "agent owner: $($o.Domain)\$($o.User)" | Add-Content $log
} else { "agent NAO esta rodando" | Add-Content $log }

# 2) Tasks
"--- Scheduled Tasks ---" | Add-Content $log
foreach ($tn in @("MonitoreDBAgent","MonitoreDBDiag")) {
    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if ($t) {
        $i = $t | Get-ScheduledTaskInfo
        "TASK $tn state=$($t.State) lastResult=0x{0:X} lastRun=$($i.LastRunTime)" -f $i.LastTaskResult | Add-Content $log
        "  principal: $($t.Principal.UserId) runlevel=$($t.Principal.RunLevel)" | Add-Content $log
    } else { "TASK $tn: NAO EXISTE" | Add-Content $log }
}

# 3) appcmd direto (elevado)
"--- appcmd list apppools ---" | Add-Content $log
$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
& $appcmd list apppools 2>&1 | Add-Content $log
"--- appcmd XML estrutura ---" | Add-Content $log
$xmlOut = (& $appcmd list apppools /xml 2>&1) | Out-String
$xmlOut.Substring(0, [Math]::Min(700, $xmlOut.Length)) | Add-Content $log

# 4) w3wp processos
"--- w3wp ---" | Add-Content $log
Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'" | ForEach-Object {
    "PID $($_.ProcessId): $($_.CommandLine)" | Add-Content $log
}

# 5) EXATO script do iis.rs (get_w3wp_info)
"--- script iis.rs (get_w3wp_info) ---" | Add-Content $log
$script = @'
$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
$stateMap = @{}
$xml = & $appcmd list apppools /xml 2>$null
if ($xml) {
  [xml]$am = ($xml -join "`n")
  if ($null -ne $am.appcmd.APPPOOL) {
    foreach ($p in @($am.appcmd.APPPOOL)) {
      $stateMap[$p.name] = $p.@state
    }
  }
}
$w3wp = Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'"
$result = @{ pools = $stateMap; workers = @() }
$w3wp | %{
    $pool = ""
    if ($_.CommandLine -match '-ap\s+"([^"]+)"') { $pool = $Matches[1] }
    if ($pool -ne "" -and -not $stateMap.ContainsKey($pool)) { $stateMap[$pool] = "Running" }
    $result.workers += @{ pid = $_.ProcessId; pool = $pool }
}
$result.pools = $stateMap
$result | ConvertTo-Json -Compress
'@
$r = Invoke-Expression $script 2>&1
"JSON: $r" | Add-Content $log

"=== FIM ===" | Add-Content $log