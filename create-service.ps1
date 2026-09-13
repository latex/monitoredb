#Requires -RunAsAdministrator
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $root "service.log"

# 1. Carrega configuracao do .env (segredos fora do script/repo)
$cfg = @{}
$envFile = Join-Path $root ".env"
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { $cfg[$Matches[1]] = $Matches[2].Trim() }
    }
} else {
    Write-Error "Crie o arquivo .env a partir do .env.example"; exit 1
}
$sqlPass = $cfg["MSSQL_SA_PASSWORD"]
$token = $cfg["MONITOREDB_TOKEN"]
if (-not $sqlPass) { Write-Error "Defina MSSQL_SA_PASSWORD no .env"; exit 1 }

# 2. Interrompe o agente em execucao e atualiza o binario
Stop-ScheduledTask -TaskName "MonitoreDBAgent" -ErrorAction SilentlyContinue
Get-Process monitoredb-agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$newExe = Join-Path $root "target\release\monitoredb-agent.exe"
if (Test-Path $newExe) {
    Copy-Item $newExe (Join-Path $root "monitoredb-agent.exe") -Force
    "Binario atualizado de target\release" | Out-File $log -Append
}

# 3. Remove o servico quebrado
$old = Get-WmiObject Win32_Service -Filter "Name='MonitoreDBAgent'" -ErrorAction SilentlyContinue
if ($old) { [void]$old.Delete(); "Servico antigo removido" | Out-File $log -Append }

# 4. sqlcmd no PATH do SISTEMA (LocalSystem nao herda PATH do usuario)
$sqlcmd = Join-Path $env:LOCALAPPDATA "Microsoft\sqlcmd"
$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
if ($machinePath -notlike "*$sqlcmd*") {
    [Environment]::SetEnvironmentVariable("Path", "$machinePath;$sqlcmd", "Machine")
    "PATH do sistema atualizado com sqlcmd" | Out-File $log -Append
} else {
    "sqlcmd ja no PATH do sistema" | Out-File $log -Append
}

# 5. Tarefa agendada no boot como SYSTEM (privilegios maximos = IIS/SQL ok)
$exe = Join-Path $root "monitoredb-agent.exe"
$agentArgs = "--server http://localhost:3000 --agent-id minha-pc --sql-host localhost --sql-port 1433 --sql-user sa --sql-pass $sqlPass --interval 30"
if ($token) { $agentArgs += " --token $token" }

Unregister-ScheduledTask -TaskName "MonitoreDBAgent" -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute $exe -Argument $agentArgs -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName "MonitoreDBAgent" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description "MonitoreDB - coleta Windows, IIS e SQL Server" | Out-File $log -Append

Start-ScheduledTask -TaskName "MonitoreDBAgent" 2>&1 | Out-File $log -Append
Start-Sleep -Seconds 5
Get-ScheduledTaskInfo -TaskName "MonitoreDBAgent" | Select-Object LastRunTime, LastTaskResult | Out-File $log -Append
Get-Process monitoredb-agent -ErrorAction SilentlyContinue | Select-Object Id, ProcessName | Out-File $log -Append
