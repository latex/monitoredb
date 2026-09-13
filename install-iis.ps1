#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
$log = "C:\Users\Leandro Teixeira\source\invent\britch\monitoredb\install-iis.log"
Start-Transcript -Path $log -Force

$features = @(
    "IIS-WebServerRole",
    "IIS-WebServer",
    "IIS-CommonHttpFeatures",
    "IIS-StaticContent",
    "IIS-DefaultDocument",
    "IIS-DirectoryBrowsing",
    "IIS-HttpErrors",
    "IIS-HealthAndDiagnostics",
    "IIS-HttpLogging",
    "IIS-LoggingLibraries",
    "IIS-RequestMonitor",
    "IIS-HttpTracing",
    "IIS-Security",
    "IIS-RequestFiltering",
    "IIS-IPSecurity",
    "IIS-Performance",
    "IIS-HttpCompressionStatic",
    "IIS-WebServerManagementTools",
    "IIS-ManagementConsole",
    "IIS-ManagementScriptingTools"
)

foreach ($f in $features) {
    $st = Get-WindowsOptionalFeature -Online -FeatureName $f
    if ($st.State -eq "Disabled") {
        Write-Host "Instalando $f ..."
        Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart | Out-Null
    } else {
        Write-Host "$f ja instalado"
    }
}

Stop-Transcript