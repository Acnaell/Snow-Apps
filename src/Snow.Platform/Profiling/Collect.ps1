# Snow — coleta de hardware via CIM/WMI. Uma única execução do Windows PowerShell 5.1.
# Saída: um objeto JSON compacto. Compatível com Windows 10 1607+ e Windows 11.
$ErrorActionPreference = 'SilentlyContinue'

function Q([string]$Class, [string]$Ns = 'root/cimv2') {
    Get-CimInstance -ClassName $Class -Namespace $Ns -ErrorAction SilentlyContinue
}

$o = [ordered]@{}

$o.cpu = @(Q Win32_Processor | ForEach-Object {
    [pscustomobject]@{
        Name = "$($_.Name)".Trim(); Manufacturer = "$($_.Manufacturer)"
        Cores = [int]$_.NumberOfCores; Threads = [int]$_.NumberOfLogicalProcessors
        MaxClock = [int]$_.MaxClockSpeed; Virt = [bool]$_.VirtualizationFirmwareEnabled
    }
})

$o.gpu = @(Q Win32_VideoController | ForEach-Object {
    [pscustomobject]@{
        Name = "$($_.Name)"; Pnp = "$($_.PNPDeviceID)"; Driver = "$($_.DriverVersion)"
        AdapterRam = [uint64]$_.AdapterRAM; Compat = "$($_.AdapterCompatibility)"
        Refresh = [int]$_.CurrentRefreshRate
    }
})

$o.mem = @(Q Win32_PhysicalMemory | ForEach-Object {
    [pscustomobject]@{
        Capacity = [uint64]$_.Capacity; Speed = [int]$_.Speed; Configured = [int]$_.ConfiguredClockSpeed
        Locator = "$($_.DeviceLocator)"; Bank = "$($_.BankLabel)"; Maker = "$($_.Manufacturer)".Trim()
        Part = "$($_.PartNumber)".Trim(); SmbiosType = [int]$_.SMBIOSMemoryType
        TypeDetail = [int]$_.TypeDetail; TotalWidth = [int]$_.TotalWidth; DataWidth = [int]$_.DataWidth
    }
})

$cs = Q Win32_ComputerSystem
$o.cs = [pscustomobject]@{ PcType = [int]$cs.PCSystemType; Hypervisor = [bool]$cs.HypervisorPresent; Model = "$($cs.Manufacturer) $($cs.Model)".Trim() }

$o.chassis = @(Q Win32_SystemEnclosure | ForEach-Object { $_.ChassisTypes } | ForEach-Object { [int]$_ })
$o.battery = @(Q Win32_Battery).Count

$o.disks = @(Q MSFT_PhysicalDisk 'root/Microsoft/Windows/Storage' | ForEach-Object {
    [pscustomobject]@{ Id = "$($_.DeviceId)"; Name = "$($_.FriendlyName)"; Media = [int]$_.MediaType; Bus = [int]$_.BusType; Size = [uint64]$_.Size }
})
$o.parts = @(Q MSFT_Partition 'root/Microsoft/Windows/Storage' | Where-Object { [int]$_.DriveLetter -ne 0 } | ForEach-Object {
    [pscustomobject]@{ Letter = [string]$_.DriveLetter; Disk = [int]$_.DiskNumber }
})

$dg = Q Win32_DeviceGuard 'root/Microsoft/Windows/DeviceGuard'
$o.dg = [pscustomobject]@{
    Vbs = [int]$dg.VirtualizationBasedSecurityStatus
    Running = @($dg.SecurityServicesRunning | ForEach-Object { [int]$_ })
}

$o | ConvertTo-Json -Depth 5 -Compress
