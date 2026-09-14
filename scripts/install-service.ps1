#Requires -Version 5.1
<#
.SYNOPSIS
  Registra o agente MonitoreDB como Serviço do Windows (startup automatico).

.DESCRIPTION
  Cria/recria o servico apontando para Monitoredb.WorkerAgent.exe, configura
  recuperacao automatica em caso de falha (restart) e inicia o servico.

  A configuracao (server, token, SQL) vem do appsettings.json que fica no mesmo
  diretorio do executavel (Monitoredb:*). Variáveis de ambiente do sistema
  MONITOREDB_* tambem sao respeitadas, mas o appsettings tem precedencia.

.PARAMETER InstallDir
  Diretorio onde o agente foi publicado. Padrao: C:\Monitoredb

.PARAMETER ServiceName
  Nome interno do servico. Padrao: MonitoredbAgent

.EXAMPLE
  .\install-service.ps1
  Instala a partir de C:\Monitoredb.

.EXAMPLE
  .\install-service.ps1 -InstallDir "C:\Program Files\Monitoredb" -Start
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Monitoredb",
    [string]$ServiceName = "MonitoredbAgent",
    [string]$DisplayName = "MonitoreDB Agent",
    [switch]$Start
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Execute este script com privilegios de Administrador."
}

$exe = Join-Path $InstallDir "Monitoredb.WorkerAgent.exe"
if (-not (Test-Path $exe)) {
    throw "Executavel nao encontrado: $exe. Publique o agente antes (dotnet publish -r win-x64)."
}

Write-Host "Servico   : $ServiceName"
Write-Host "Executavel: $exe"

# Remove servico anterior, se existir.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removendo servico existente..."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Cria o servico.
$binPath = "`"$exe`""
& sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= $DisplayName | Out-Null
& sc.exe description $ServiceName "Agente de coleta MonitoreDB (Windows / IIS / SQL Server)" | Out-Null

# Recuperacao automatica: reinicia apos 5s em caso de falha.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# Sobe o servico no boot mesmo se demorar (dependencia de rede).
& sc.exe config $ServiceName start= delayed-auto | Out-Null

if ($Start) {
    Write-Host "Iniciando servico..."
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    Get-Service -Name $ServiceName | Format-List Name, DisplayName, Status, StartType
} else {
    Write-Host "Servico registrado. Use 'Start-Service $ServiceName' para iniciar."
}
