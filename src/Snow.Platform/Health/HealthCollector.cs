using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Snow.Core.Health;
using Snow.Platform.Services;

namespace Snow.Platform.Health;

/// <summary>Snow Health: lê o que o Windows já registra (SMART, eventos, dispositivos, bateria). Nada é alterado.</summary>
public static class HealthCollector
{
    public static HealthReport Run() => HealthAnalyzer.Analyze(Collect());

    public static HealthData Collect()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Snow.Health.ps1")!;
            using var r = new StreamReader(s);
            var result = PowerShell.Run(r.ReadToEnd(), TimeSpan.FromMinutes(3));
            var start = result.StdOut.IndexOf('{');
            if (start < 0) return new HealthData { Warnings = [$"Coleta falhou: {result.StdErr.Trim()}"] };
            var data = Parse(result.StdOut[start..]);
            return data with
            {
                Security = data.Security with { Uefi = FirmwareIsUefi() },
                DriveCare = MaintenanceService.ReadDriveCare(),
                OsBuild = Environment.OSVersion.Version.Build,
            };
        }
        catch (Exception e) { return new HealthData { Warnings = [$"Coleta falhou: {e.Message}"] }; }
    }

    public static HealthData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var disks = Items(root, "disks").Select(d => new DiskHealthData
        {
            Name = Str(d, "Name") ?? "",
            MediaType = Int(d, "Media") ?? 0,
            BusType = Int(d, "Bus") ?? 0,
            HealthStatus = Int(d, "Health") ?? 5,
            SizeBytes = Long(d, "Size") ?? 0,
            TemperatureC = Int(d, "Temp") is > 0 and < 120 and var t ? t : null, // 0 = não informado
            TemperatureMaxC = Int(d, "TempMax") is > 0 and < 120 and var tm ? tm : null,
            WearPercent = Int(d, "Wear") is >= 0 and <= 100 and var w ? w : null,
            PowerOnHours = Long(d, "Hours"),
            ReadErrorsUncorrected = Long(d, "RUnc"),
            WriteErrorsUncorrected = Long(d, "WUnc"),
            ReadErrorsTotal = Long(d, "RTot"),
        }).ToList();

        var volumes = Items(root, "volumes").Select(v => new VolumeData(Str(v, "Letter") ?? "?", Long(v, "Size") ?? 0, Long(v, "Free") ?? 0)).ToList();

        BatteryData? battery = null;
        if (root.TryGetProperty("battery", out var b) && b.ValueKind == JsonValueKind.Object)
            battery = new BatteryData(Long(b, "Designed") ?? 0, Long(b, "Full") ?? 0, Int(b, "Cycles"));

        var devices = Items(root, "devices").Select(d => new ProblemDevice(Str(d, "Name") ?? "Dispositivo", Int(d, "Code") ?? 0)).ToList();

        EventCounts events = new() { Available = false };
        if (root.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Object)
        {
            events = new EventCounts
            {
                Days = Int(ev, "Days") ?? 30,
                Available = !ev.TryGetProperty("Available", out var av) || av.ValueKind != JsonValueKind.False,
                UnexpectedShutdowns = Int(ev, "UnexpectedShutdowns") ?? 0,
                BlueScreens = Int(ev, "BlueScreens") ?? 0,
                GpuDriverResets = Int(ev, "GpuDriverResets") ?? 0,
                HardwareErrors = Int(ev, "HardwareErrors") ?? 0,
                DiskErrors = Int(ev, "DiskErrors") ?? 0,
                CrashingApps = Items(ev, "Apps").Select(a => new CrashingApp(Str(a, "App") ?? "?", Int(a, "Count") ?? 0)).ToList(),
            };
        }

        string? gpuName = null; DateTime? driverDate = null;
        if (root.TryGetProperty("gpu", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            gpuName = Str(g, "Name");
            if (DateTime.TryParseExact(Str(g, "DriverDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd)) driverDate = dd;
        }

        return new HealthData
        {
            Disks = disks,
            FailurePredicted = root.TryGetProperty("predictFailure", out var pf) && pf.ValueKind == JsonValueKind.True,
            Volumes = volumes,
            Battery = battery,
            ProblemDevices = devices,
            Events = events,
            StabilityIndex = Double(root, "stability"),
            GpuName = gpuName,
            GpuDriverDate = driverDate,
            UptimeDays = Double(root, "uptimeDays") ?? 0,
            RebootPending = root.TryGetProperty("rebootPending", out var rp) && rp.ValueKind == JsonValueKind.True,
            Board = ParseBoard(root),
            Security = new SecurityState
            {
                Tpm = root.TryGetProperty("tpm", out var tp) && tp.ValueKind == JsonValueKind.Object
                    ? new TpmInfo(true, Bool(tp, "Enabled"), Bool(tp, "Activated"), Str(tp, "Spec") ?? "", Str(tp, "Maker") ?? "")
                    : null,
                SecureBoot = root.TryGetProperty("secureBoot", out var sbv) && sbv.ValueKind is JsonValueKind.True or JsonValueKind.False ? sbv.ValueKind == JsonValueKind.True : null,
                SystemDiskGpt = root.TryGetProperty("systemDiskGpt", out var gpt) && gpt.ValueKind is JsonValueKind.True or JsonValueKind.False ? gpt.ValueKind == JsonValueKind.True : null,
            },
            CpuName = Str(root, "cpuName") ?? "",
            Warnings = Items(root, "warnings").Select(w => w.ValueKind == JsonValueKind.String ? w.GetString()! : w.ToString()).ToList(),
        };
    }

    private static BoardInfo? ParseBoard(JsonElement root)
    {
        if (!root.TryGetProperty("board", out var b) || b.ValueKind != JsonValueKind.Object) return null;
        DateTime? date = DateTime.TryParseExact(Str(b, "BiosDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return new BoardInfo
        {
            Maker = Str(b, "Maker") ?? "", Product = Str(b, "Product") ?? "", BiosVersion = Str(b, "Bios") ?? "", BiosDate = date,
            MemorySlots = Int(b, "Slots") ?? 0, MemoryUsed = Int(b, "Used") ?? 0,
            MaxMemoryBytes = Long(b, "MaxKB") is > 0 and var kb ? kb * 1024 : null,
        };
    }

    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetFirmwareType(out int type);

    /// <summary>GetFirmwareType: 1 = BIOS Legacy, 2 = UEFI.</summary>
    private static bool? FirmwareIsUefi()
    {
        try { return GetFirmwareType(out var t) ? t switch { 2 => true, 1 => false, _ => null } : null; } catch { return null; }
    }

    // Helpers tolerantes ao JSON do PowerShell 5.1 (arrays às vezes vêm como {value,Count}; números às vezes como string)
    private static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var el)) return [];
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var inner)) el = inner;
        return el.ValueKind switch { JsonValueKind.Array => el.EnumerateArray().ToList(), JsonValueKind.Object => [el], _ => [] };
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    private static double? Double(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private static long? Long(JsonElement e, string n) => Double(e, n) is { } d ? (long)d : null;
    private static int? Int(JsonElement e, string n) => Double(e, n) is { } d ? (int)d : null;
}
