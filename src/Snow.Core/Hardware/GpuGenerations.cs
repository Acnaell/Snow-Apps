using System.Text.RegularExpressions;

namespace Snow.Core.Hardware;

/// <summary>Heurísticas de geração/classe de GPU a partir do nome e PNP ID.</summary>
public static partial class GpuGenerations
{
    [GeneratedRegex(@"\bRTX\s*(?:A)?(\d{1,2})(\d{2})\b", RegexOptions.IgnoreCase)] private static partial Regex RtxRegex();
    [GeneratedRegex(@"\bGTX\s*(\d{1,2})(\d{2})\b", RegexOptions.IgnoreCase)] private static partial Regex GtxRegex();
    [GeneratedRegex(@"\bRX\s*(\d)\d{2,3}", RegexOptions.IgnoreCase)] private static partial Regex RadeonRxRegex();
    [GeneratedRegex(@"\bArc\(?(?:TM)?\)?\s*[AB]\d{3}", RegexOptions.IgnoreCase)] private static partial Regex ArcDiscreteRegex();
    [GeneratedRegex(@"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase)] private static partial Regex VendorRegex();

    public static GpuVendor VendorFromPnp(string pnpId, string name)
    {
        var m = VendorRegex().Match(pnpId ?? "");
        if (m.Success)
        {
            return m.Groups[1].Value.ToUpperInvariant() switch
            {
                "10DE" => GpuVendor.Nvidia,
                "1002" or "1022" => GpuVendor.Amd,
                "8086" => GpuVendor.Intel,
                "1414" => GpuVendor.Microsoft,
                "5143" or "QCOM" => GpuVendor.Qualcomm,
                _ => GpuVendor.Unknown,
            };
        }
        name ??= "";
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Microsoft;
        return GpuVendor.Unknown;
    }

    public static bool IsVirtualAdapter(GpuVendor vendor, string name) =>
        vendor == GpuVendor.Microsoft
        || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    public static bool IsDiscrete(GpuVendor vendor, string name) => vendor switch
    {
        GpuVendor.Nvidia => true,
        GpuVendor.Amd => RadeonRxRegex().IsMatch(name) || name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("Vega 56", StringComparison.OrdinalIgnoreCase) || name.Contains("Vega 64", StringComparison.OrdinalIgnoreCase),
        GpuVendor.Intel => ArcDiscreteRegex().IsMatch(name),
        _ => false,
    };

    /// <summary>Série NVIDIA (10, 16, 20, 30, 40, 50) ou 0 se desconhecida.</summary>
    public static int NvidiaSeries(string name)
    {
        var m = RtxRegex().Match(name);
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = GtxRegex().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>Primeiro dígito da série Radeon RX (5 = RX 5000/RDNA1, 6, 7, 9 = RX 9000).</summary>
    public static int RadeonSeries(string name)
    {
        var m = RadeonRxRegex().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    public static bool SupportsHags(GpuInfo gpu) => gpu.Vendor switch
    {
        GpuVendor.Nvidia => NvidiaSeries(gpu.Name) is >= 10 or 0 && !gpu.Name.Contains("GT 7", StringComparison.OrdinalIgnoreCase),
        // RX 5000+ (RDNA). Polaris (RX 4xx/5xx) e Vega não suportam.
        GpuVendor.Amd => RadeonSeries(gpu.Name) is 5 or 6 or 7 or 9 && !IsPolaris(gpu.Name),
        GpuVendor.Intel => gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool IsPolaris(string name) =>
        Regex.IsMatch(name, @"\bRX\s*(4[6-8]0|5[5-9]0)\b", RegexOptions.IgnoreCase);

    /// <summary>Converte "32.0.15.6094" para "560.94" (padrão NVIDIA).</summary>
    public static string FriendlyNvidiaDriver(string driverVersion)
    {
        var parts = (driverVersion ?? "").Split('.');
        if (parts.Length < 4) return driverVersion ?? "";
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return driverVersion!;
        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    public static int WddmMajor(string driverVersion)
    {
        var first = (driverVersion ?? "").Split('.')[0];
        return int.TryParse(first, out var v) ? v : 0;
    }
}
