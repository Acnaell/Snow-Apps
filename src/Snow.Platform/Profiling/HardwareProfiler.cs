using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Snow.Core.Hardware;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;
using Snow.Platform.Executors;
using Snow.Platform.Native;
using Snow.Platform.Services;

namespace Snow.Platform.Profiling;

/// <summary>Dados obtidos por APIs nativas (não via CIM).</summary>
public sealed record NativeInfo(
    int PerformanceCores, int EfficiencyCores, bool Smt, bool HybridKnown,
    ulong TotalPhys, ulong AvailPhys, PowerCapabilities Power, bool? OnAc,
    Guid ActiveScheme, string ActiveSchemeName, string Architecture, string SystemDrive);

public static partial class HardwareProfiler
{
    private const string GpuClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly int[] LaptopChassis = [8, 9, 10, 11, 14, 30, 31, 32];

    /// <summary>Coleta completa (Windows). Leva ~1–3 s, dominado pela inicialização do PowerShell.</summary>
    public static HardwareProfile Collect()
    {
        var warnings = new List<string>();
        var reg = new WindowsRegistryStore();
        var power = new WindowsPowerApi();

        string json = "{}";
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Snow.Collect.ps1")!;
            using var r = new StreamReader(s);
            var result = PowerShell.Run(r.ReadToEnd(), TimeSpan.FromSeconds(90));
            var start = result.StdOut.IndexOf('{');
            if (result.ExitCode == 0 && start >= 0) json = result.StdOut[start..].Trim();
            else warnings.Add($"Coleta CIM incompleta (código {result.ExitCode}): {result.StdErr.Trim()}");
        }
        catch (Exception e) { warnings.Add($"Falha ao executar o PowerShell: {e.Message}"); }

        var native = CollectNative(power, warnings);
        var libs = GameLibraryFinder.Find(reg);
        var profile = Build(json, native, reg, new WindowsServiceControl(reg), libs);
        return profile with { CollectionWarnings = [.. warnings, .. profile.CollectionWarnings] };
    }

    public static NativeInfo CollectNative(IPowerApi power, List<string> warnings)
    {
        var (p, e, smt, known) = HybridCores();
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref mem);

        Guid scheme = Guid.Empty; var name = "";
        try { scheme = power.GetActiveScheme(); name = power.GetSchemeName(scheme); }
        catch (Exception ex) { warnings.Add($"Plano de energia: {ex.Message}"); }

        return new(p, e, smt, known, mem.ullTotalPhys, mem.ullAvailPhys, power.GetCapabilities(), power.IsOnAcPower(),
            scheme, name, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':'));
    }

    /// <summary>Conta núcleos por EfficiencyClass (GetLogicalProcessorInformationEx). Classe mais alta = P-core.</summary>
    internal static unsafe (int p, int e, bool smt, bool known) HybridCores()
    {
        uint len = 0;
        NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, IntPtr.Zero, ref len);
        if (len == 0) return (0, 0, false, false);
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, buf, ref len)) return (0, 0, false, false);
            var classes = new List<byte>();
            var smt = false;
            var ptr = (byte*)buf;
            uint offset = 0;
            while (offset < len)
            {
                var size = *(uint*)(ptr + offset + 4);
                var flags = *(ptr + offset + 8);
                classes.Add(*(ptr + offset + 9));
                smt |= (flags & 1) != 0;
                if (size == 0) break;
                offset += size;
            }
            if (classes.Count == 0) return (0, 0, smt, false);
            var max = classes.Max();
            var pc = classes.Count(c => c == max);
            return classes.Distinct().Count() > 1 ? (pc, classes.Count - pc, smt, true) : (classes.Count, 0, smt, true);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ───────────────────────── montagem (pura; testável com fakes) ─────────────────────────

    public static HardwareProfile Build(string json, NativeInfo native, IRegistryStore reg, IServiceControl svc, IReadOnlyList<string> gameLibraries)
    {
        var warnings = new List<string>();
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch (JsonException e) { warnings.Add($"JSON do CIM inválido: {e.Message}"); root = JsonDocument.Parse("{}").RootElement; }

        var cs = One(root, "cs");
        var hypervisor = Bool(cs, "Hypervisor");
        var os = ReadOs(reg, native.Architecture);
        var cpu = ReadCpu(root, native, hypervisor);
        var gpus = Items(root, "gpu").Select(g => ReadGpu(g, reg)).ToList();
        var memory = ReadMemory(root, native);
        var storage = ReadStorage(root, native.SystemDrive, gameLibraries);

        var chassis = Items(root, "chassis").Select(c => c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0).ToList();
        var isLaptop = Int(cs, "PcType") == 2 || chassis.Any(LaptopChassis.Contains) || native.Power.LidPresent
                       || (native.Power.BatteriesPresent && Int(root, "battery") > 0);

        var dg = One(root, "dg");
        var running = Items(dg, "Running").Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToList();

        var state = new SystemState
        {
            ActivePowerPlanGuid = native.ActiveScheme == Guid.Empty ? "" : native.ActiveScheme.ToString(),
            ActivePowerPlanName = native.ActiveSchemeName,
            GameMode = reg.Get(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled")?.Data switch
            { null => TriState.Default, "0" => TriState.Off, _ => TriState.On },
            Hags = reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")?.Data switch
            { null => TriState.Default, "2" => TriState.On, _ => TriState.Off },
            WindowedGameOptimizations = !os.IsWindows11 ? TriState.Unknown
                : DirectXGlobalExecutor.Parse(reg.Get(RegistryHive.CurrentUser, DirectXGlobalExecutor.Path, DirectXGlobalExecutor.ValueName)?.Data)
                    .FirstOrDefault(kv => kv.Key.Equals("SwapEffectUpgradeEnable", StringComparison.OrdinalIgnoreCase)).Value switch
                    { null => TriState.Default, "1" => TriState.On, _ => TriState.Off },
            GameDvrBackgroundRecording = reg.Get(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled")?.Data == "1"
                ? TriState.On : TriState.Off,
            MemoryIntegrity = dg.ValueKind == JsonValueKind.Undefined ? TriState.Unknown : running.Contains(2) ? TriState.On : TriState.Off,
            VbsRunning = dg.ValueKind == JsonValueKind.Undefined ? TriState.Unknown : Int(dg, "Vbs") == 2 ? TriState.On : TriState.Off,
            SysMainStart = svc.GetStart("SysMain")?.Label() ?? "ausente",
            DiagTrackStart = svc.GetStart("DiagTrack")?.Label() ?? "ausente",
            IsLaptop = isLaptop,
            ModernStandby = native.Power.ModernStandby,
            OnBattery = native.OnAc == false,
        };

        return new HardwareProfile
        {
            Cpu = cpu, Gpus = gpus, Memory = memory, Storage = storage, Os = os, State = state,
            CollectedAtUtc = DateTime.UtcNow, CollectionWarnings = warnings,
        };
    }

    private static OsInfo ReadOs(IRegistryStore reg, string arch)
    {
        const string k = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string? S(string n) => reg.Get(RegistryHive.LocalMachine, k, n)?.Data;
        var build = int.TryParse(S("CurrentBuildNumber") ?? S("CurrentBuild"), out var b) ? b : Environment.OSVersion.Version.Build;
        var installType = S("InstallationType") ?? "Client";
        return new OsInfo
        {
            ProductName = S("ProductName") ?? "Windows",
            Build = build,
            Ubr = int.TryParse(S("UBR"), out var u) ? u : 0,
            DisplayVersion = S("DisplayVersion") ?? S("ReleaseId") ?? "",
            Edition = S("EditionID") ?? "",
            IsServer = installType.StartsWith("Server", StringComparison.OrdinalIgnoreCase),
            Architecture = arch,
        };
    }

    private static CpuInfo ReadCpu(JsonElement root, NativeInfo n, bool hypervisor)
    {
        var cpus = Items(root, "cpu").ToList();
        var first = cpus.FirstOrDefault();
        var name = Str(first, "Name") ?? "Desconhecido";
        var maker = (Str(first, "Manufacturer") ?? "") + " " + name;
        var vendor = maker.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Intel
                   : maker.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Amd
                   : maker.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) || maker.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Qualcomm
                   : CpuVendor.Unknown;
        var cores = cpus.Sum(c => Int(c, "Cores"));
        var threads = cpus.Sum(c => Int(c, "Threads"));
        var hybrid = n.HybridKnown && n.EfficiencyCores > 0;
        return new CpuInfo
        {
            Name = Regex.Replace(name, @"\s+", " "),
            Vendor = vendor,
            PhysicalCores = cores > 0 ? cores : n.PerformanceCores + n.EfficiencyCores,
            LogicalProcessors = threads > 0 ? threads : Environment.ProcessorCount,
            MaxClockMhz = Int(first, "MaxClock"),
            IsHybrid = hybrid,
            PerformanceCores = hybrid ? n.PerformanceCores : cores,
            EfficiencyCores = hybrid ? n.EfficiencyCores : 0,
            SmtEnabled = n.Smt || threads > cores,
            VirtualizationFirmwareEnabled = Bool(first, "Virt"),
            HypervisorPresent = hypervisor,
        };
    }

    private static GpuInfo ReadGpu(JsonElement g, IRegistryStore reg)
    {
        var name = Str(g, "Name") ?? "Desconhecida";
        var pnp = Str(g, "Pnp") ?? "";
        var driver = Str(g, "Driver") ?? "";
        var vendor = GpuGenerations.VendorFromPnp(pnp, name);
        long vram = (long)(g.ValueKind == JsonValueKind.Object && g.TryGetProperty("AdapterRam", out var ar) && ar.TryGetUInt64(out var arv) ? arv : 0);
        string? amdVersion = null;

        // AdapterRAM é uint32 (limite de 4 GB): o valor real está no registro da classe de vídeo.
        try
        {
            var pnpLower = pnp.ToLowerInvariant();
            foreach (var sub in reg.SubKeys(RegistryHive.LocalMachine, GpuClassKey).Where(s => s.All(char.IsDigit)))
            {
                var path = $@"{GpuClassKey}\{sub}";
                var match = reg.Get(RegistryHive.LocalMachine, path, "MatchingDeviceId")?.Data?.ToLowerInvariant();
                if (string.IsNullOrEmpty(match) || !pnpLower.StartsWith(match.Replace("\\\\", "\\"))) continue;
                var q = ReadSize(reg.Get(RegistryHive.LocalMachine, path, "HardwareInformation.qwMemorySize"))
                     ?? ReadSize(reg.Get(RegistryHive.LocalMachine, path, "HardwareInformation.MemorySize"));
                if (q is > 0) vram = q.Value;
                amdVersion = reg.Get(RegistryHive.LocalMachine, path, "RadeonSoftwareVersion")?.Data;
                break;
            }
        }
        catch { /* mantém AdapterRAM */ }

        var isVirtual = GpuGenerations.IsVirtualAdapter(vendor, name);
        return new GpuInfo
        {
            Name = name.Trim(),
            Vendor = vendor,
            VramBytes = vram,
            DriverVersion = driver,
            DriverVersionFriendly = vendor switch
            {
                GpuVendor.Nvidia => GpuGenerations.FriendlyNvidiaDriver(driver),
                GpuVendor.Amd when !string.IsNullOrEmpty(amdVersion) => $"Adrenalin {amdVersion}",
                _ => driver,
            },
            IsDiscrete = !isVirtual && GpuGenerations.IsDiscrete(vendor, name),
            IsVirtual = isVirtual,
            WddmMajorFromDriver = GpuGenerations.WddmMajor(driver),
            PnpDeviceId = pnp,
        };
    }

    private static long? ReadSize(RegValue? v) => v?.Kind switch
    {
        RegistryKind.QWord => (long)ulong.Parse(v.Data, CultureInfo.InvariantCulture),
        RegistryKind.DWord => uint.Parse(v.Data, CultureInfo.InvariantCulture),
        RegistryKind.Binary when v.Data.Length >= 8 => Convert.FromHexString(v.Data) is var b && b.Length >= 8 ? BitConverter.ToInt64(b, 0) : BitConverter.ToUInt32(b, 0),
        _ => null,
    };

    [GeneratedRegex(@"(?:CHANNEL\s*|Channel|DIMM_?|Controller\d?-?Channel)([A-H])(?:\b|\d|-)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRegex();

    internal static MemoryInfo ReadMemory(JsonElement root, NativeInfo n)
    {
        var mods = Items(root, "mem").Select(m => new
        {
            Module = new MemoryModule((long)(UInt64(m, "Capacity")), Int(m, "Speed"), Str(m, "Locator") ?? "", Str(m, "Bank") ?? "", Str(m, "Maker") ?? "", Str(m, "Part") ?? ""),
            Configured = Int(m, "Configured"),
            Type = Int(m, "SmbiosType"),
        }).ToList();

        var letters = mods.Select(m =>
        {
            var match = ChannelRegex().Match(m.Module.Bank);
            if (!match.Success) match = ChannelRegex().Match(m.Module.Slot);
            return match.Success ? char.ToUpperInvariant(match.Groups[1].Value[0]) : (char?)null;
        }).Where(c => c is not null).Distinct().Count();

        MemoryChannelMode channels; bool estimated;
        if (letters >= 4) { channels = MemoryChannelMode.Quad; estimated = false; }
        else if (letters >= 2) { channels = MemoryChannelMode.Dual; estimated = false; }
        else if (mods.Count == 1) { channels = MemoryChannelMode.Single; estimated = false; }
        else if (letters == 1 && mods.Count > 1) { channels = MemoryChannelMode.Single; estimated = true; }
        else if (mods.Count >= 2) { channels = MemoryChannelMode.Dual; estimated = true; }
        else { channels = MemoryChannelMode.Unknown; estimated = true; }

        var installed = mods.Sum(m => m.Module.CapacityBytes);
        var typeCode = mods.Select(m => m.Type).FirstOrDefault(t => t > 0);
        return new MemoryInfo
        {
            TotalBytes = installed > 0 ? installed : (long)n.TotalPhys,
            AvailableBytes = (long)n.AvailPhys, // total = instalado; a memória reservada pelo HW aparece como "em uso"
            ConfiguredSpeedMts = mods.Select(m => m.Configured).Where(s => s > 0).DefaultIfEmpty(0).Min(),
            RatedSpeedMts = mods.Select(m => m.Module.SpeedMts).Where(s => s > 0).DefaultIfEmpty(0).Min(),
            Type = typeCode switch { 24 => "DDR3", 26 => "DDR4", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "Desconhecido" },
            Channels = channels,
            ChannelsEstimated = estimated,
            Modules = mods.Select(m => m.Module).ToList(),
        };
    }

    internal static StorageInfo ReadStorage(JsonElement root, string systemDrive, IReadOnlyList<string> libraries)
    {
        var parts = Items(root, "parts").Select(p => (Letter: Str(p, "Letter") ?? "", Disk: Int(p, "Disk"))).ToList();
        var disks = Items(root, "disks").Select(d =>
        {
            var num = int.TryParse(Str(d, "Id"), out var n) ? n : -1;
            var media = Int(d, "Media");
            var bus = Int(d, "Bus");
            var model = Str(d, "Name") ?? "";
            var kind = model.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || bus == 15 ? DiskKind.Virtual
                     : bus == 17 && media != 3 ? DiskKind.NvmeSsd
                     : media == 3 ? DiskKind.Hdd
                     : media == 4 && bus is 11 or 3 ? DiskKind.SataSsd
                     : media == 4 ? DiskKind.OtherSsd
                     : DiskKind.Unknown;
            var letters = parts.Where(p => p.Disk == num && p.Letter.Length > 0).Select(p => p.Letter.ToUpperInvariant()).ToList();
            return new DiskInfo(num, model, kind, (long)UInt64(d, "Size"), letters);
        }).ToList();

        return new StorageInfo { Disks = disks, SystemDrive = systemDrive.TrimEnd(':'), GameLibraries = libraries };
    }

    // ───────────── helpers JSON tolerantes (PS 5.1 às vezes embrulha arrays em {value,Count}) ─────────────

    internal static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var el)) return [];
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var inner)) el = inner;
        return el.ValueKind switch
        {
            JsonValueKind.Array => el.EnumerateArray().ToList(),
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            _ => [el],
        };
    }

    private static JsonElement One(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object ? el : default;

    private static string? Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int Int(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static ulong UInt64(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var i) ? i : 0;

    private static bool Bool(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}
