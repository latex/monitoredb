<#
    Versao: 2026-05-15-07 - Corrige retorno de DataTable vazio no PowerShell com return ,$dt.
.SYNOPSIS
    Coletor consolidado e leve para IIS: Requests ativas, AppPools/Workers e Host Snapshot.

.DESCRIPTION
    - Não executa recycle.
    - Usa loop interno, evitando Task Scheduler a cada 30s.
    - Usa SqlBulkCopy para reduzir CPU/overhead no SQL.
    - Correlaciona request ativa com CPU/RAM do PID w3wp no mesmo DataColeta.
    - A CPU da request NÃO é CPU exclusiva da request; é CPU do worker process no momento da coleta.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File .\IIS_Monitoramento_CurtoPrazo.ps1 -SqlServer BRDSQL02 -Database MetricasBasesClientes -IntervalSeconds 30
#>

[CmdletBinding()]
param(
    [string]$SqlServer = "BRDSQL01",
    [string]$Database = "MetricasBasesClientes",
    [string]$RequestTable = "dbo.IIS_RequestSnapshot",
    [string]$AppPoolTable = "dbo.IIS_AppPoolSnapshot",
    [string]$HostTable = "dbo.IIS_HostSnapshot",
    [int]$IntervalSeconds = 30,
    [int]$MaxLoops = 0, # 0 = infinito
    [string]$LogDirectory = "C:\Program Files\PS\Logs\IISMonitorNEW",
    [switch]$ConsoleVerbose
)

$ErrorActionPreference = "Stop"
$ServerName = $env:COMPUTERNAME
$UsuarioWindows = "$env:USERDOMAIN\$env:USERNAME"
$LogicalCpuCount = [Environment]::ProcessorCount
$AppCmdPath = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
$ConnectionString = "Server=$SqlServer;Database=$Database;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;"

if (-not (Test-Path $LogDirectory)) { New-Item -Path $LogDirectory -ItemType Directory -Force | Out-Null }
$LogFile = Join-Path $LogDirectory ("IISMonitor_{0}.log" -f (Get-Date -Format "yyyyMMdd"))

function Write-MonitorLog {
    param([string]$Message, [string]$Level = "INFO")
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    if ($ConsoleVerbose -or $Level -in @("ERROR","WARN")) { Write-Host $line }
}

function New-DataTable {
    param([object[]]$Columns)
    $dt = New-Object System.Data.DataTable
    foreach ($c in $Columns) {
        if ($c -is [hashtable]) {
            $colName = [string]$c.Name
            $colType = $c.Type
            if ($null -eq $colType) { $colType = [string] }
            [void]$dt.Columns.Add($colName, $colType)
        }
        else {
            [void]$dt.Columns.Add([string]$c, [string])
        }
    }
    return ,$dt
}

function Add-DataRow {
    param([System.Data.DataTable]$Table, [hashtable]$Values)
    $row = $Table.NewRow()
    foreach ($key in $Values.Keys) {
        if ($null -eq $Values[$key]) { $row[$key] = [DBNull]::Value }
        else { $row[$key] = $Values[$key] }
    }
    [void]$Table.Rows.Add($row)
}

function Write-DataTableBulk {
    param(
        [System.Data.DataTable]$DataTable,
        [string]$DestinationTable
    )
    if ($DataTable.Rows.Count -eq 0) { return }

    $conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $bulk = New-Object System.Data.SqlClient.SqlBulkCopy($conn)
    $bulk.DestinationTableName = $DestinationTable
    $bulk.BatchSize = 1000
    $bulk.BulkCopyTimeout = 120

    foreach ($col in $DataTable.Columns) {
        [void]$bulk.ColumnMappings.Add($col.ColumnName, $col.ColumnName)
    }

    try {
        $conn.Open()
        $bulk.WriteToServer($DataTable)
    }
    finally {
        $bulk.Close()
        if ($conn.State -ne [System.Data.ConnectionState]::Closed) { $conn.Close() }
        $conn.Dispose()
    }
}

function Get-W3wpMap {
    $mapByPid = @{}
    try {
        $procs = Get-CimInstance Win32_Process -Filter "Name = 'w3wp.exe'"
        foreach ($p in $procs) {
            $pool = $null
            if ($p.CommandLine -match '-ap\s+"([^"]+)"') { $pool = $Matches[1] }
            $mapByPid[[int]$p.ProcessId] = [pscustomobject]@{
                PID = [int]$p.ProcessId
                AppPoolName = $pool
                CommandLine = [string]$p.CommandLine
            }
        }
    }
    catch {
        Write-MonitorLog "Falha ao mapear w3wp/AppPool via CIM: $($_.Exception.Message)" "WARN"
    }
    return $mapByPid
}

function Get-ProcessCpuMap {
    <#
        Coleta CPU por processo sem usar variáveis chamadas PID/Pid.
        Usa duas amostras de Get-Process para evitar o erro da variável automática $PID
        e para reduzir dependência de Win32_PerfFormattedData_PerfProc_Process.
    #>
    $cpuMap = @{}
    try {
        $sampleSeconds = 0.5
        $firstSample = @{}

        foreach ($procItem in Get-Process -ErrorAction SilentlyContinue) {
            try {
                if ($null -ne $procItem.CPU) {
                    $firstSample[[int]$procItem.Id] = [double]$procItem.CPU
                }
            } catch {}
        }

        Start-Sleep -Milliseconds ([int]($sampleSeconds * 1000))

        foreach ($procItem in Get-Process -ErrorAction SilentlyContinue) {
            try {
                $processIdValue = [int]$procItem.Id
                if ($firstSample.ContainsKey($processIdValue) -and $null -ne $procItem.CPU) {
                    $deltaCpuSeconds = [double]$procItem.CPU - [double]$firstSample[$processIdValue]
                    $cpuPercent = ($deltaCpuSeconds / $sampleSeconds) * 100 / [math]::Max($LogicalCpuCount, 1)
                    if ($cpuPercent -lt 0) { $cpuPercent = 0 }
                    $cpuMap[$processIdValue] = [math]::Round($cpuPercent, 2)
                }
            } catch {}
        }
    }
    catch {
        Write-MonitorLog "Falha ao coletar CPU por processo: $($_.Exception.Message)" "WARN"
    }
    return $cpuMap
}

function Get-ProcessMemoryMap {
    $memMap = @{}
    try {
        foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
            $memMap[[int]$p.Id] = [math]::Round(($p.WorkingSet64 / 1MB), 2)
        }
    }
    catch {
        Write-MonitorLog "Falha ao coletar memória por processo: $($_.Exception.Message)" "WARN"
    }
    return $memMap
}

function Get-FriendlyName {
    param([int]$ProcessIdValue, [string]$DefaultName, [hashtable]$W3wpMap)
    if ($W3wpMap.ContainsKey($ProcessIdValue) -and -not [string]::IsNullOrWhiteSpace($W3wpMap[$ProcessIdValue].AppPoolName)) {
        return "AppPool:$($W3wpMap[$ProcessIdValue].AppPoolName)"
    }
    return $DefaultName
}

function Get-HostMetrics {
    param([hashtable]$CpuMap, [hashtable]$MemMap, [hashtable]$W3wpMap)

    if ($null -eq $CpuMap) { $CpuMap = @{} }
    if ($null -eq $MemMap) { $MemMap = @{} }
    if ($null -eq $W3wpMap) { $W3wpMap = @{} }

    $hostCpu = $null
    $memUsedPct = $null
    $memAvail = $null
    $memTotal = $null

    try {
        $cpuTotal = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor | Where-Object { $_.Name -eq "_Total" } | Select-Object -First 1
        if ($cpuTotal) { $hostCpu = [math]::Round([double]$cpuTotal.PercentProcessorTime, 2) }
    }
    catch { Write-MonitorLog "Falha CPU host: $($_.Exception.Message)" "WARN" }

    try {
        $os = Get-CimInstance Win32_OperatingSystem
        $memTotal = [math]::Round(([double]$os.TotalVisibleMemorySize / 1024), 2)
        $memAvail = [math]::Round(([double]$os.FreePhysicalMemory / 1024), 2)
        if ($memTotal -gt 0) { $memUsedPct = [math]::Round((($memTotal - $memAvail) * 100 / $memTotal), 2) }
    }
    catch { Write-MonitorLog "Falha memória host: $($_.Exception.Message)" "WARN" }

    $procBase = @{}
    try {
        foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
            $procBase[[int]$p.Id] = [pscustomobject]@{ ProcessId = [int]$p.Id; Name = $p.ProcessName; RAM_MB = [math]::Round($p.WorkingSet64 / 1MB, 2) }
        }
    } catch {}

    $topCpu = $CpuMap.GetEnumerator() |
        Where-Object { $procBase.ContainsKey([int]$_.Key) } |
        Sort-Object Value -Descending |
        Select-Object -First 3

    $topRam = $procBase.Values |
        Sort-Object RAM_MB -Descending |
        Select-Object -First 3

    return [pscustomobject]@{
        HostCpuPercent = $hostCpu
        HostMemoryUsedPercent = $memUsedPct
        HostMemoryAvailableMB = $memAvail
        HostTotalMemoryMB = $memTotal
        TopCpu = @($topCpu | ForEach-Object {
            $processIdValue = [int]$_.Key
            [pscustomobject]@{ ProcessId = $processIdValue; Name = Get-FriendlyName -ProcessIdValue $processIdValue -DefaultName $procBase[$processIdValue].Name -W3wpMap $W3wpMap; CPU = [double]$_.Value }
        })
        TopRam = @($topRam | ForEach-Object {
            [pscustomobject]@{ ProcessId = [int]$_.ProcessId; Name = Get-FriendlyName -ProcessIdValue ([int]$_.ProcessId) -DefaultName $_.Name -W3wpMap $W3wpMap; RAM = [double]$_.RAM_MB }
        })
    }
}

function Get-ActiveRequests {
    if (-not (Test-Path $AppCmdPath)) { throw "appcmd.exe não encontrado em $AppCmdPath" }

    $output = & $AppCmdPath list requests /xml 2>&1
    $outputText = ($output -join "`n").Trim()

    # Quando não há requests ativas, o appcmd pode retornar apenas:
    # <?xml version="1.0" encoding="UTF-8"?><appcmd></appcmd>
    # Em alguns servidores o $LASTEXITCODE vem diferente de zero mesmo assim.
    # Isso NÃO é erro de coleta; é snapshot vazio.
    if ([string]::IsNullOrWhiteSpace($outputText)) { return @() }

    $isXmlLike = ($outputText -match '^<\?xml' -or $outputText -match '<appcmd')
    if ($LASTEXITCODE -ne 0 -and -not $isXmlLike) {
        throw "Falha appcmd list requests: $outputText"
    }

    try { [xml]$xml = $outputText }
    catch {
        if ($LASTEXITCODE -ne 0) { throw "Falha appcmd list requests: $outputText" }
        Write-MonitorLog "XML inválido retornado pelo appcmd: $($_.Exception.Message)" "WARN"
        return @()
    }

    if ($null -eq $xml.appcmd -or $null -eq $xml.appcmd.REQUEST) { return @() }
    return @($xml.appcmd.REQUEST)
}

function Run-CollectionCycle {
    $executionId = "IISMON_{0}" -f (Get-Date -Format "yyyyMMdd_HHmmss_fff")
    $dataColeta = Get-Date

    $w3wpMap = Get-W3wpMap
    $cpuMap = Get-ProcessCpuMap
    $memMap = Get-ProcessMemoryMap
    $hostMetrics = Get-HostMetrics -CpuMap $cpuMap -MemMap $memMap -W3wpMap $w3wpMap

    $dtReq = New-DataTable @(
        @{Name="DataColeta";Type=[DateTime]}, @{Name="Servidor";Type=[string]}, @{Name="AppPoolName";Type=[string]}, @{Name="PID";Type=[int]}, @{Name="WebsiteId";Type=[int]},
        @{Name="Url";Type=[string]}, @{Name="Verb";Type=[string]}, @{Name="ClientIp";Type=[string]}, @{Name="Stage";Type=[string]}, @{Name="Module";Type=[string]},
        @{Name="TimeMs";Type=[long]}, @{Name="CPUPercent";Type=[double]}, @{Name="MemoriaMB";Type=[double]}
    )
    $dtPool = New-DataTable @(
        @{Name="DataColeta";Type=[DateTime]}, @{Name="ExecutionId";Type=[string]}, @{Name="Servidor";Type=[string]}, @{Name="UsuarioWindows";Type=[string]},
        @{Name="AppPoolName";Type=[string]}, @{Name="PoolState";Type=[string]}, @{Name="HasWorker";Type=[bool]}, @{Name="WorkerPID";Type=[int]},
        @{Name="RAM_MB";Type=[double]}, @{Name="CPU_Percent";Type=[double]}, @{Name="Uptime_Minutes";Type=[double]}, @{Name="Decision";Type=[string]}, @{Name="Reason";Type=[string]},
        @{Name="MemoryLimitMB";Type=[int]}, @{Name="CpuLimitPercent";Type=[double]}, @{Name="MinUptimeMinutes";Type=[int]}, @{Name="CpuSampleSeconds";Type=[double]},
        @{Name="WhatIfMode";Type=[bool]}, @{Name="LogicalCpuCount";Type=[int]}
    )
    $dtHost = New-DataTable @(
        @{Name="DataColeta";Type=[DateTime]}, @{Name="ExecutionId";Type=[string]}, @{Name="Servidor";Type=[string]}, @{Name="UsuarioWindows";Type=[string]}, @{Name="LogicalCpuCount";Type=[int]},
        @{Name="HostCpuPercent";Type=[double]}, @{Name="HostMemoryUsedPercent";Type=[double]}, @{Name="HostMemoryAvailableMB";Type=[double]}, @{Name="HostTotalMemoryMB";Type=[double]},
        @{Name="TopProcess1Name";Type=[string]}, @{Name="TopProcess1PID";Type=[int]}, @{Name="TopProcess1CPUPercent";Type=[double]},
        @{Name="TopProcess2Name";Type=[string]}, @{Name="TopProcess2PID";Type=[int]}, @{Name="TopProcess2CPUPercent";Type=[double]},
        @{Name="TopProcess3Name";Type=[string]}, @{Name="TopProcess3PID";Type=[int]}, @{Name="TopProcess3CPUPercent";Type=[double]},
        @{Name="TopMemory1Name";Type=[string]}, @{Name="TopMemory1PID";Type=[int]}, @{Name="TopMemory1RAM_MB";Type=[double]},
        @{Name="TopMemory2Name";Type=[string]}, @{Name="TopMemory2PID";Type=[int]}, @{Name="TopMemory2RAM_MB";Type=[double]},
        @{Name="TopMemory3Name";Type=[string]}, @{Name="TopMemory3PID";Type=[int]}, @{Name="TopMemory3RAM_MB";Type=[double]}
    )

    $requests = Get-ActiveRequests
    foreach ($req in $requests) {
        $processIdValue = $null
        if (-not [string]::IsNullOrWhiteSpace([string]$req.'WP.NAME')) { $processIdValue = [int]$req.'WP.NAME' }

        Add-DataRow -Table $dtReq -Values @{
            DataColeta = $dataColeta
            Servidor = $ServerName
            AppPoolName = [string]$req.'APPPOOL.NAME'
            PID = $processIdValue
            WebsiteId = $(if ([string]::IsNullOrWhiteSpace([string]$req.'SITE.ID')) { $null } else { [int]$req.'SITE.ID' })
            Url = [string]$req.Url
            Verb = [string]$req.Verb
            ClientIp = [string]$req.ClientIp
            Stage = [string]$req.Stage
            Module = [string]$req.Module
            TimeMs = $(if ([string]::IsNullOrWhiteSpace([string]$req.Time)) { $null } else { [long]$req.Time })
            CPUPercent = $(if ($processIdValue -and $cpuMap.ContainsKey($processIdValue)) { [double]$cpuMap[$processIdValue] } else { $null })
            MemoriaMB = $(if ($processIdValue -and $memMap.ContainsKey($processIdValue)) { [double]$memMap[$processIdValue] } else { $null })
        }
    }

    foreach ($kv in $w3wpMap.GetEnumerator()) {
        $processIdValue = [int]$kv.Key
        $proc = $null
        try { $proc = Get-Process -Id $processIdValue -ErrorAction Stop } catch {}
        $uptime = $null
        if ($proc) {
            try { $uptime = [math]::Round((New-TimeSpan -Start $proc.StartTime -End $dataColeta).TotalMinutes, 2) } catch {}
        }

        Add-DataRow -Table $dtPool -Values @{
            DataColeta = $dataColeta
            ExecutionId = $executionId
            Servidor = $ServerName
            UsuarioWindows = $UsuarioWindows
            AppPoolName = $kv.Value.AppPoolName
            PoolState = "Unknown"
            HasWorker = $true
            WorkerPID = $processIdValue
            RAM_MB = $(if ($memMap.ContainsKey($processIdValue)) { [double]$memMap[$processIdValue] } else { $null })
            CPU_Percent = $(if ($cpuMap.ContainsKey($processIdValue)) { [double]$cpuMap[$processIdValue] } else { $null })
            Uptime_Minutes = $uptime
            Decision = "MONITOR_ONLY"
            Reason = "Coleta sem recycle automático"
            MemoryLimitMB = $null
            CpuLimitPercent = $null
            MinUptimeMinutes = $null
            CpuSampleSeconds = $null
            WhatIfMode = $true
            LogicalCpuCount = $LogicalCpuCount
        }
    }

    if ($null -eq $hostMetrics) {
        $hostMetrics = [pscustomobject]@{
            HostCpuPercent = $null
            HostMemoryUsedPercent = $null
            HostMemoryAvailableMB = $null
            HostTotalMemoryMB = $null
            TopCpu = @()
            TopRam = @()
        }
    }

    $topCpuList = @($hostMetrics.TopCpu)
    $topRamList = @($hostMetrics.TopRam)

    $tc1 = if ($topCpuList.Count -ge 1) { $topCpuList[0] } else { $null }
    $tc2 = if ($topCpuList.Count -ge 2) { $topCpuList[1] } else { $null }
    $tc3 = if ($topCpuList.Count -ge 3) { $topCpuList[2] } else { $null }
    $tm1 = if ($topRamList.Count -ge 1) { $topRamList[0] } else { $null }
    $tm2 = if ($topRamList.Count -ge 2) { $topRamList[1] } else { $null }
    $tm3 = if ($topRamList.Count -ge 3) { $topRamList[2] } else { $null }

    Add-DataRow -Table $dtHost -Values @{
        DataColeta = $dataColeta; ExecutionId = $executionId; Servidor = $ServerName; UsuarioWindows = $UsuarioWindows; LogicalCpuCount = $LogicalCpuCount
        HostCpuPercent = $hostMetrics.HostCpuPercent; HostMemoryUsedPercent = $hostMetrics.HostMemoryUsedPercent; HostMemoryAvailableMB = $hostMetrics.HostMemoryAvailableMB; HostTotalMemoryMB = $hostMetrics.HostTotalMemoryMB
        TopProcess1Name = $(if ($tc1) { $tc1.Name } else { $null }); TopProcess1PID = $(if ($tc1) { $tc1.ProcessId } else { $null }); TopProcess1CPUPercent = $(if ($tc1) { $tc1.CPU } else { $null })
        TopProcess2Name = $(if ($tc2) { $tc2.Name } else { $null }); TopProcess2PID = $(if ($tc2) { $tc2.ProcessId } else { $null }); TopProcess2CPUPercent = $(if ($tc2) { $tc2.CPU } else { $null })
        TopProcess3Name = $(if ($tc3) { $tc3.Name } else { $null }); TopProcess3PID = $(if ($tc3) { $tc3.ProcessId } else { $null }); TopProcess3CPUPercent = $(if ($tc3) { $tc3.CPU } else { $null })
        TopMemory1Name = $(if ($tm1) { $tm1.Name } else { $null }); TopMemory1PID = $(if ($tm1) { $tm1.ProcessId } else { $null }); TopMemory1RAM_MB = $(if ($tm1) { $tm1.RAM } else { $null })
        TopMemory2Name = $(if ($tm2) { $tm2.Name } else { $null }); TopMemory2PID = $(if ($tm2) { $tm2.ProcessId } else { $null }); TopMemory2RAM_MB = $(if ($tm2) { $tm2.RAM } else { $null })
        TopMemory3Name = $(if ($tm3) { $tm3.Name } else { $null }); TopMemory3PID = $(if ($tm3) { $tm3.ProcessId } else { $null }); TopMemory3RAM_MB = $(if ($tm3) { $tm3.RAM } else { $null })
    }

    Write-DataTableBulk -DataTable $dtHost -DestinationTable $HostTable
    Write-DataTableBulk -DataTable $dtPool -DestinationTable $AppPoolTable
    Write-DataTableBulk -DataTable $dtReq -DestinationTable $RequestTable

    Write-MonitorLog "Ciclo ok | Requests=$($dtReq.Rows.Count) | Workers=$($dtPool.Rows.Count) | Host=1 | CPUHost=$($hostMetrics.HostCpuPercent) | MemHost=$($hostMetrics.HostMemoryUsedPercent)"
}

Write-MonitorLog "Iniciando IIS Monitor v8 | Script=$PSCommandPath | Server=$ServerName | SQL=$SqlServer | DB=$Database | Interval=$IntervalSeconds | MaxLoops=$MaxLoops"

$loop = 0
while ($true) {
    $loop++
    try { Run-CollectionCycle }
    catch {
        $lineInfo = $_.InvocationInfo.ScriptLineNumber
        $cmdInfo = $_.InvocationInfo.Line
        Write-MonitorLog "Erro no ciclo ${loop}: Linha=$lineInfo | $($_.Exception.Message) | Comando=$cmdInfo" "ERROR"
    }

    if ($MaxLoops -gt 0 -and $loop -ge $MaxLoops) { break }
    Start-Sleep -Seconds $IntervalSeconds
}

Write-MonitorLog "Finalizado. Loops executados: $loop"
