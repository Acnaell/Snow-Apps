namespace Snow.Core.Health;

public enum HealthLevel { Ok, Info, Atencao, Critico }

public enum HealthCategory { Discos, Estabilidade, Hardware, Dispositivos, Espaco, Bateria, Sistema }

/// <summary>Dados brutos lidos do Windows (somente leitura, sem drivers).</summary>
public sealed record DiskHealthData
{
    public string Name { get; init; } = "";
    public int MediaType { get; init; }        // 3 HDD, 4 SSD
    public int BusType { get; init; }          // 17 NVMe, 11 SATA, 7 USB
    public int HealthStatus { get; init; } = 5; // 0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown
    public long SizeBytes { get; init; }
    public int? TemperatureC { get; init; }
    public int? TemperatureMaxC { get; init; }
    /// <summary>Desgaste do SSD em % (0 = novo, 100 = vida útil nominal consumida).</summary>
    public int? WearPercent { get; init; }
    public long? PowerOnHours { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }
    public long? ReadErrorsTotal { get; init; }
    public bool IsSsd => MediaType == 4 || BusType == 17;
    public bool IsVirtual => BusType == 15 || Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
}

public sealed record VolumeData(string Letter, long SizeBytes, long FreeBytes)
{
    public double FreePercent => SizeBytes == 0 ? 100 : 100.0 * FreeBytes / SizeBytes;
}

public sealed record BatteryData(long DesignedCapacity, long FullChargeCapacity, int? CycleCount)
{
    public double HealthPercent => DesignedCapacity <= 0 ? 0 : Math.Min(100, 100.0 * FullChargeCapacity / DesignedCapacity);
}

public sealed record ProblemDevice(string Name, int ErrorCode);

public sealed record CrashingApp(string App, int Count);

/// <summary>Contagens dos registros de eventos do Windows nos últimos N dias.</summary>
public sealed record EventCounts
{
    public int Days { get; init; } = 30;
    public int UnexpectedShutdowns { get; init; }   // Kernel-Power 41
    public int BlueScreens { get; init; }           // WER-SystemErrorReporting 1001
    public int GpuDriverResets { get; init; }       // Display 4101 (TDR)
    public int HardwareErrors { get; init; }        // WHEA-Logger
    public int DiskErrors { get; init; }            // disk 7/51/153, Ntfs 55
    public IReadOnlyList<CrashingApp> CrashingApps { get; init; } = [];
    public bool Available { get; init; } = true;
}

public sealed record HealthData
{
    public IReadOnlyList<DiskHealthData> Disks { get; init; } = [];
    public bool FailurePredicted { get; init; }
    public IReadOnlyList<VolumeData> Volumes { get; init; } = [];
    public BatteryData? Battery { get; init; }
    public IReadOnlyList<ProblemDevice> ProblemDevices { get; init; } = [];
    public EventCounts Events { get; init; } = new();
    public double? StabilityIndex { get; init; }    // 1–10 (Monitor de Confiabilidade)
    public string? GpuName { get; init; }
    public DateTime? GpuDriverDate { get; init; }
    public double UptimeDays { get; init; }
    public bool RebootPending { get; init; }
    public DateTime CollectedAt { get; init; } = DateTime.Now;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record HealthItem(HealthCategory Category, HealthLevel Level, string Title, string Detail, string? Action = null);

public sealed record HealthReport(int Score, string Verdict, IReadOnlyList<HealthItem> Items, HealthData Data)
{
    public IEnumerable<HealthItem> Problems => Items.Where(i => i.Level is HealthLevel.Atencao or HealthLevel.Critico);
}
