namespace Snow.Core.Hardware;

public enum CpuVendor { Unknown, Intel, Amd, Qualcomm }
public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Qualcomm }
public enum DiskKind { Unknown, Hdd, SataSsd, NvmeSsd, OtherSsd, Virtual }
public enum MemoryChannelMode { Unknown, Single, Dual, Quad }

public sealed record CpuInfo
{
    public string Name { get; init; } = "Desconhecido";
    public CpuVendor Vendor { get; init; }
    public int PhysicalCores { get; init; }
    public int LogicalProcessors { get; init; }
    public int MaxClockMhz { get; init; }
    /// <summary>Intel 12ª geração+ (P-cores/E-cores) detectado via EfficiencyClass.</summary>
    public bool IsHybrid { get; init; }
    public int PerformanceCores { get; init; }
    public int EfficiencyCores { get; init; }
    public bool SmtEnabled { get; init; }
    public bool VirtualizationFirmwareEnabled { get; init; }
    public bool HypervisorPresent { get; init; }

    public bool IsX3D => Name.Contains("X3D", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ryzen X3D com dois CCDs (7900X3D/7950X3D/9900X3D/9950X3D): o driver AMD PPM usa
    /// core parking + Game Bar para isolar jogos no CCD com V-Cache. Não mexer em parking aqui.
    /// </summary>
    public bool IsDualCcdX3D => Vendor == CpuVendor.Amd && IsX3D && PhysicalCores >= 12;
}

public sealed record GpuInfo
{
    public string Name { get; init; } = "Desconhecida";
    public GpuVendor Vendor { get; init; }
    public long VramBytes { get; init; }
    public string DriverVersion { get; init; } = "";
    /// <summary>Versão no formato do fabricante (ex.: NVIDIA 566.36).</summary>
    public string DriverVersionFriendly { get; init; } = "";
    public bool IsDiscrete { get; init; }
    public bool IsVirtual { get; init; }
    /// <summary>Major do WDDM derivado da versão do driver (27 = WDDM 2.7, 31 = WDDM 3.1...).</summary>
    public int WddmMajorFromDriver { get; init; }
    public string PnpDeviceId { get; init; } = "";
    /// <summary>Taxa de atualização atual da saída de vídeo (Hz); 0 se desconhecida.</summary>
    public int RefreshRateHz { get; init; }

    /// <summary>HAGS exige WDDM 2.7+ e GPU NVIDIA Pascal+, AMD RDNA (RX 5000+) ou Intel Arc.</summary>
    public bool SupportsHags => !IsVirtual && WddmMajorFromDriver >= 27 && GpuGenerations.SupportsHags(this);
}

public sealed record MemoryModule(long CapacityBytes, int SpeedMts, string Slot, string Bank, string Manufacturer, string PartNumber);

public sealed record MemoryInfo
{
    public long TotalBytes { get; init; }
    public long AvailableBytes { get; init; }
    public int ConfiguredSpeedMts { get; init; }
    public int RatedSpeedMts { get; init; }
    public string Type { get; init; } = "Desconhecido";
    public MemoryChannelMode Channels { get; init; }
    public bool ChannelsEstimated { get; init; } = true;
    public IReadOnlyList<MemoryModule> Modules { get; init; } = [];

    public double TotalGb => TotalBytes / 1024d / 1024 / 1024;
    public double UsedPercent => TotalBytes == 0 ? 0 : 100d * (TotalBytes - AvailableBytes) / TotalBytes;
}

public sealed record DiskInfo(int Number, string Model, DiskKind Kind, long SizeBytes, IReadOnlyList<string> DriveLetters)
{
    public bool IsSsd => Kind is DiskKind.NvmeSsd or DiskKind.SataSsd or DiskKind.OtherSsd;
}

public sealed record StorageInfo
{
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public string SystemDrive { get; init; } = "C";
    /// <summary>Bibliotecas de jogos detectadas (Steam, Epic, etc.).</summary>
    public IReadOnlyList<string> GameLibraries { get; init; } = [];

    public DiskInfo? DiskForDrive(string letter) =>
        Disks.FirstOrDefault(d => d.DriveLetters.Any(l => l.Equals(letter.TrimEnd(':', '\\'), StringComparison.OrdinalIgnoreCase)));

    public DiskInfo? SystemDisk => DiskForDrive(SystemDrive);

    public IReadOnlyList<DiskInfo> GameDisks =>
        GameLibraries.Select(p => p.Length >= 1 ? DiskForDrive(p[..1]) : null)
                     .Where(d => d is not null).Distinct().Cast<DiskInfo>().ToList();

    /// <summary>Verdadeiro quando o disco do sistema e todos os discos de jogos são SSD.</summary>
    public bool AllRelevantDisksAreSsd =>
        SystemDisk is { IsSsd: true } && GameDisks.All(d => d.IsSsd);
}

public sealed record OsInfo
{
    public string ProductName { get; init; } = "Windows";
    public int Build { get; init; }
    public int Ubr { get; init; }
    public string DisplayVersion { get; init; } = "";
    public string Edition { get; init; } = "";
    public bool IsServer { get; init; }
    public string Architecture { get; init; } = "x64";

    public bool IsWindows11 => Build >= 22000;
    public string FriendlyName => (IsServer ? ProductName : IsWindows11 ? "Windows 11" : "Windows 10")
                                  + (string.IsNullOrEmpty(DisplayVersion) ? "" : $" {DisplayVersion}")
                                  + $" (build {Build}.{Ubr})";
}

public enum TriState { Unknown, Off, On, Default }

/// <summary>Estado atual das configurações relevantes para jogos.</summary>
public sealed record SystemState
{
    public string ActivePowerPlanGuid { get; init; } = "";
    public string ActivePowerPlanName { get; init; } = "";
    public TriState GameMode { get; init; }
    /// <summary>HwSchMode: On (2), Off (1), Default (valor ausente).</summary>
    public TriState Hags { get; init; }
    public TriState WindowedGameOptimizations { get; init; }
    public TriState GameDvrBackgroundRecording { get; init; }
    public TriState MemoryIntegrity { get; init; }
    public TriState VbsRunning { get; init; }
    public string SysMainStart { get; init; } = "";
    public string DiagTrackStart { get; init; } = "";
    public bool IsLaptop { get; init; }
    public bool ModernStandby { get; init; }
    public bool OnBattery { get; init; }
}

public sealed record HardwareProfile
{
    public CpuInfo Cpu { get; init; } = new();
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public MemoryInfo Memory { get; init; } = new();
    public StorageInfo Storage { get; init; } = new();
    public OsInfo Os { get; init; } = new();
    public SystemState State { get; init; } = new();
    public DateTime CollectedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> CollectionWarnings { get; init; } = [];

    /// <summary>Maior taxa de atualização entre as saídas de vídeo ativas (Hz); 0 se desconhecida.</summary>
    public int MonitorRefreshHz => Gpus.Where(g => !g.IsVirtual && g.RefreshRateHz is >= 30 and <= 1000).Select(g => g.RefreshRateHz).DefaultIfEmpty(0).Max();

    /// <summary>GPU principal para jogos: dedicada com mais VRAM, senão a primeira não virtual.</summary>
    public GpuInfo? PrimaryGpu =>
        Gpus.Where(g => !g.IsVirtual).OrderByDescending(g => g.IsDiscrete).ThenByDescending(g => g.VramBytes).FirstOrDefault()
        ?? Gpus.FirstOrDefault();
}
