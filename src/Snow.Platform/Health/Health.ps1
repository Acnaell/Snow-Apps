# Snow Health: coleta somente leitura (sem drivers). Windows PowerShell 5.1, Windows 10/11.
$ErrorActionPreference = 'SilentlyContinue'
$o = [ordered]@{}
$warn = New-Object System.Collections.ArrayList

# ── Discos: estado e contadores de confiabilidade (SMART exposto pelo Windows)
$o.disks = @(Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_PhysicalDisk | ForEach-Object {
    $r = $null
    try { $r = $_ | Get-StorageReliabilityCounter -ErrorAction Stop } catch { }
    [pscustomobject]@{
        Name = "$($_.FriendlyName)"; Media = [int]$_.MediaType; Bus = [int]$_.BusType; Health = [int]$_.HealthStatus; Size = [uint64]$_.Size
        Temp = $r.Temperature; TempMax = $r.TemperatureMax; Wear = $r.Wear; Hours = $r.PowerOnHours
        RUnc = $r.ReadErrorsUncorrected; WUnc = $r.WriteErrorsUncorrected; RTot = $r.ReadErrorsTotal
    }
})
$o.predictFailure = @(Get-CimInstance -Namespace root/wmi -ClassName MSStorageDriver_FailurePredictStatus | Where-Object { $_.PredictFailure }).Count -gt 0

# ── Volumes
$o.volumes = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    [pscustomobject]@{ Letter = "$($_.DeviceID)"; Size = [uint64]$_.Size; Free = [uint64]$_.FreeSpace }
})

# ── Bateria (notebooks)
$static = Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData | Select-Object -First 1
$full = Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity | Select-Object -First 1
$cyc = Get-CimInstance -Namespace root/wmi -ClassName BatteryCycleCount | Select-Object -First 1
if ($static -and $full) {
    $o.battery = [pscustomobject]@{ Designed = [uint64]$static.DesignedCapacity; Full = [uint64]$full.FullChargedCapacity; Cycles = $cyc.CycleCount }
}

# ── Dispositivos com erro no Gerenciador de Dispositivos
$o.devices = @(Get-CimInstance Win32_PnPEntity -Filter 'ConfigManagerErrorCode <> 0' | ForEach-Object {
    $n = if ($_.Name) { "$($_.Name)" } elseif ($_.Description) { "$($_.Description)" } else { "$($_.PNPDeviceID)" }
    [pscustomobject]@{ Name = $n; Code = [int]$_.ConfigManagerErrorCode }
})

# ── Driver de vídeo
# Ignora adaptadores da Microsoft (Basic Display, Hyper-V, Remote): drivers nativos usam a data simbólica 21/06/2006
$gpu = Get-CimInstance Win32_VideoController | Where-Object {
    $_.DriverDate -and "$($_.PNPDeviceID)" -notmatch 'VEN_1414' -and "$($_.AdapterCompatibility)" -notmatch 'Microsoft' -and "$($_.Name)" -notmatch 'Microsoft|Hyper-V|Basic'
} | Select-Object -First 1
if ($gpu) { $o.gpu = [pscustomobject]@{ Name = "$($gpu.Name)"; DriverDate = $gpu.DriverDate.ToString('yyyy-MM-dd') } }

# ── Eventos dos últimos 30 dias
$since = (Get-Date).AddDays(-30)
function Count-Events($filter) {
    try { return @(Get-WinEvent -FilterHashtable $filter -MaxEvents 2000 -ErrorAction Stop).Count } catch { return 0 }
}
$ev = [ordered]@{ Days = 30; Available = $true }
try { Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop | Out-Null } catch { $ev.Available = $false; [void]$warn.Add("Log de eventos: $($_.Exception.Message)") }
$ev.UnexpectedShutdowns = Count-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 41; StartTime = $since }
$ev.BlueScreens = Count-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WER-SystemErrorReporting'; Id = 1001; StartTime = $since }
$ev.GpuDriverResets = Count-Events @{ LogName = 'System'; ProviderName = 'Display'; Id = 4101; StartTime = $since }
$ev.HardwareErrors = Count-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; Level = 1, 2; StartTime = $since }
$ev.DiskErrors = (Count-Events @{ LogName = 'System'; ProviderName = 'disk'; Id = 7, 51, 153; StartTime = $since }) +
                 (Count-Events @{ LogName = 'System'; ProviderName = 'Ntfs'; Id = 55; StartTime = $since })
$ev.Apps = @()
try {
    $ev.Apps = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error'; Id = 1000; StartTime = $since } -MaxEvents 1000 -ErrorAction Stop |
        ForEach-Object { "$($_.Properties[0].Value)" } | Group-Object | Sort-Object Count -Descending | Select-Object -First 5 |
        ForEach-Object { [pscustomobject]@{ App = $_.Name; Count = $_.Count } })
} catch { }
$o.events = [pscustomobject]$ev

# ── Índice de estabilidade (Monitor de Confiabilidade)
$rel = Get-CimInstance Win32_ReliabilityStabilityMetrics | Sort-Object TimeGenerated -Descending | Select-Object -First 1
if ($rel) { $o.stability = [double]$rel.SystemStabilityIndex }

# ── Sistema
$os = Get-CimInstance Win32_OperatingSystem
if ($os.LastBootUpTime) { $o.uptimeDays = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalDays, 1) }
$o.rebootPending = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
$o.warnings = @($warn)

$o | ConvertTo-Json -Depth 5 -Compress
