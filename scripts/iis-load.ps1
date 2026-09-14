#Requires -Version 5.1
<#
.SYNOPSIS
  Gerador de carga HTTP contínuo para manter os worker processes (w3wp) do IIS ativos,
  permitindo que o agente MonitoreDB capture métricas de app pools e requisições.

.DESCRIPTION
  O IIS só inicia um w3wp.exe quando um app pool recebe tráfego (ou está em AlwaysRunning).
  Sem requisições, o coletor reporta active_requests=[] e app_pools=[]. Este script fica
  chamando um ou mais endpoints em loop para manter os workers vivos durante a coleta.

.PARAMETER Url
  Lista de URLs a serem chamadas em round-robin. Padrão: http://localhost/ e http://localhost:8080/

.PARAMETER DurationSeconds
  Duração total em segundos. 0 (padrão) = roda indefinidamente até Ctrl+C.

.PARAMETER IntervalMs
  Intervalo entre rodadas (por worker) em milissegundos. Padrão: 200.

.PARAMETER Concurrency
  Número de requisições paralelas (runspaces). Padrão: 4.

.PARAMETER TimeoutSeconds
  Timeout por requisição. Padrão: 5.

.PARAMETER LogFile
  Caminho opcional para gravar um resumo (uma linha por rodada).

.PARAMETER Quiet
  Não imprime o progresso por rodada; apenas o resumo final.

.EXAMPLE
  .\iis-load.ps1
  Roda indefinidamente contra http://localhost/ e http://localhost:8080/.

.EXAMPLE
  .\iis-load.ps1 -Url http://localhost:8080/ -DurationSeconds 300 -Concurrency 8
  Gera carga por 5 minutos em 8 conexões paralelas.

.EXAMPLE
  .\iis-load.ps1 -DurationSeconds 0 -LogFile C:\temp\iis-load.log
  Roda até Ctrl+C e grava o resumo em arquivo.
#>
[CmdletBinding()]
param(
    [string[]]$Url = @("http://localhost/", "http://localhost:8080/"),
    [int]$DurationSeconds = 0,
    [int]$IntervalMs = 200,
    [int]$Concurrency = 4,
    [int]$TimeoutSeconds = 5,
    [string]$LogFile = "",
    [switch]$Quiet
)

$ErrorActionPreference = "Continue"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

if ($Concurrency -lt 1) { $Concurrency = 1 }
if ($IntervalMs -lt 0) { $IntervalMs = 0 }
if ($Url.Count -eq 0) { throw "Informe ao menos uma URL em -Url." }

$deadline = if ($DurationSeconds -gt 0) { (Get-Date).AddSeconds($DurationSeconds) } else { [datetime]::MaxValue }
$stop = $false
$running = $true

$stats = [hashtable]::Synchronized(@{
    Ok = 0; Fail = 0; Bytes = 0; TotalMs = 0; MaxMs = 0
})

# Worker: chama URLs em round-robin até o deadline.
$workerScript = {
    param($WorkerId, $Urls, $Deadline, $IntervalMs, $TimeoutSeconds, $Stats, $StopRef)
    $i = $WorkerId
    $rng = [Random]::new([guid]::NewGuid().GetHashCode())
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ((Get-Date) -lt $Deadline -and -not $StopRef.Value) {
        $u = $Urls[$i % $Urls.Count]
        $i++
        $req = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $resp = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec $TimeoutSeconds
            $req.Stop()
            $ms = $req.Elapsed.TotalMilliseconds
            $Stats.Ok++
            $Stats.Bytes += $resp.RawContentLength
            $Stats.TotalMs += $ms
            if ($ms -gt $Stats.MaxMs) { $Stats.MaxMs = $ms }
        } catch {
            $req.Stop()
            $Stats.Fail++
        }
        if ($IntervalMs -gt 0) { Start-Sleep -Milliseconds $IntervalMs }
    }
}

$iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
$pool = [runspacefactory]::CreateRunspacePool(1, $Concurrency, $iss, $Host)
$pool.Open()

$state = [hashtable]::Synchronized(@{ Value = $false })
$handles = @()
for ($w = 0; $w -lt $Concurrency; $w++) {
    $ps = [powershell]::Create()
    $ps.RunspacePool = $pool
    [void]$ps.AddScript($workerScript).AddArgument($w).AddArgument($Url).AddArgument($deadline).
        AddArgument($IntervalMs).AddArgument($TimeoutSeconds).AddArgument($stats).AddArgument($state)
    $handles += [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
}

if (-not $Quiet) {
    Write-Host "Gerando carga: $($Url -join ', ')"
    Write-Host "concorrencia=$Concurrency intervalo=${IntervalMs}ms duracao=$(if ($DurationSeconds -gt 0) { "${DurationSeconds}s" } else { 'infinita (Ctrl+C p/ parar)' })"
}

$lastOk = 0; $lastFail = 0
try {
    while ($true) {
        Start-Sleep -Seconds 5
        $note = "ok=$($stats.Ok) fail=$($stats.Fail) bytes=$($stats.Bytes)"
        if (-not $Quiet) {
            $rps = ($stats.Ok - $lastOk) / 5.0
            Write-Host ("[{0:HH:mm:ss}] {1} rps={2:N1}" -f (Get-Date), $note, $rps)
        }
        if ($LogFile) { Add-Content -Path $LogFile -Value ("{0:o} {1}" -f (Get-Date), $note) }
        $lastOk = $stats.Ok
        $lastFail = $stats.Fail

        # Encerra se todos os workers terminaram.
        if (($handles | Where-Object { $_.Handle.IsCompleted }).Count -eq $handles.Count) { break }
    }
} finally {
    $state.Value = $true
    foreach ($h in $handles) {
        try { [void]$h.PS.EndInvoke($h.Handle) } catch { }
        try { $h.PS.Dispose() } catch { }
    }
    try { $pool.Close(); $pool.Dispose() } catch { }
}

$total = $stats.Ok + $stats.Fail
$avg = if ($stats.Ok -gt 0) { $stats.TotalMs / $stats.Ok } else { 0 }
Write-Host ""
Write-Host "===== RESUMO ====="
Write-Host ("requisicoes : {0}" -f $total)
Write-Host ("  ok        : {0}" -f $stats.Ok)
Write-Host ("  falhas    : {0}" -f $stats.Fail)
Write-Host ("bytes       : {0}" -f $stats.Bytes)
Write-Host ("lat.media   : {0:N1} ms" -f $avg)
Write-Host ("lat.max     : {0:N1} ms" -f $stats.MaxMs)
if ($total -gt 0) { Write-Host ("taxa        : {0:P1}" -f ($stats.Ok / $total)) }
if ($LogFile) { Add-Content -Path $LogFile -Value ("RESUMO total=$total ok=$($stats.Ok) fail=$($stats.Fail) avg_ms=$([math]::Round($avg,1))") }
