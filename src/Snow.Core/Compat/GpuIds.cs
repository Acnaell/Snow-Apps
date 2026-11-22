using System.Text.RegularExpressions;

namespace Snow.Core.Compat;

/// <summary>
/// Placas que alguns drivers (e sites como o CYRI) mostram com nome genérico, ex.: "AMD Radeon Graphics Processor (0x6FDF)".
/// Pelo ID PCI (fabricante + dispositivo) dá para saber o modelo. IDs compartilhados por vários modelos ficam com o mais fraco do grupo.
/// </summary>
public static partial class GpuIds
{
    private static readonly Dictionary<(uint ven, uint dev), string> Known = new()
    {
        // AMD (1002)
        [(0x1002, 0x6FDF)] = "AMD Radeon RX 580 2048SP",
        [(0x1002, 0x67DF)] = "AMD Radeon RX 570",          // RX 470/480/570/580 (Polaris 10/20)

        [(0x1002, 0x67EF)] = "AMD Radeon RX 460",          // RX 460/560
        [(0x1002, 0x699F)] = "AMD Radeon RX 550",
        [(0x1002, 0x687F)] = "AMD Radeon RX Vega 56",
        [(0x1002, 0x731F)] = "AMD Radeon RX 5700",
        [(0x1002, 0x7340)] = "AMD Radeon RX 5500 XT",
        [(0x1002, 0x73BF)] = "AMD Radeon RX 6800",
        [(0x1002, 0x73DF)] = "AMD Radeon RX 6700 XT",
        [(0x1002, 0x73FF)] = "AMD Radeon RX 6600",
        [(0x1002, 0x7422)] = "AMD Radeon RX 6400",
        [(0x1002, 0x744C)] = "AMD Radeon RX 7900 XT",
        [(0x1002, 0x7480)] = "AMD Radeon RX 7600",
        // NVIDIA (10DE)
        [(0x10DE, 0x1C03)] = "NVIDIA GeForce GTX 1060 6GB",
        [(0x10DE, 0x1C02)] = "NVIDIA GeForce GTX 1060 3GB",
        [(0x10DE, 0x1C82)] = "NVIDIA GeForce GTX 1050 Ti",
        [(0x10DE, 0x1C81)] = "NVIDIA GeForce GTX 1050",
        [(0x10DE, 0x1B81)] = "NVIDIA GeForce GTX 1070",
        [(0x10DE, 0x1B80)] = "NVIDIA GeForce GTX 1080",
        [(0x10DE, 0x2184)] = "NVIDIA GeForce GTX 1660",
        [(0x10DE, 0x2182)] = "NVIDIA GeForce GTX 1660 Ti",
        [(0x10DE, 0x21C4)] = "NVIDIA GeForce GTX 1660 SUPER",
        [(0x10DE, 0x1F82)] = "NVIDIA GeForce GTX 1650",
        [(0x10DE, 0x13C2)] = "NVIDIA GeForce GTX 970",
        [(0x10DE, 0x1184)] = "NVIDIA GeForce GTX 770",
    };

    /// <summary>Nome sem modelo reconhecível: genérico do driver ou "Microsoft Basic Display".</summary>
    public static bool IsGeneric(string? name) =>
        string.IsNullOrWhiteSpace(name) || GenericRx().IsMatch(name) || HardwareIndex.Gpu(name) is null && name.Contains("(0x", StringComparison.OrdinalIgnoreCase);

    /// <summary>Melhor nome para a placa: o do driver quando reconhecível; senão, pelo ID PCI (do PnP ou do "(0x6FDF)" no próprio nome).</summary>
    public static string Resolve(string? name, string? pnp)
    {
        name ??= "";
        if (!IsGeneric(name) && HardwareIndex.Gpu(name) is not null) return name;
        uint ven = 0, dev = 0;
        if (pnp is not null && PnpRx().Match(pnp) is { Success: true } m)
        {
            ven = Convert.ToUInt32(m.Groups[1].Value, 16);
            dev = Convert.ToUInt32(m.Groups[2].Value, 16);
        }
        else if (HexInNameRx().Match(name) is { Success: true } h)
        {
            dev = Convert.ToUInt32(h.Groups[1].Value, 16);
            ven = name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? 0x1002u
                : name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? 0x10DEu : 0;
        }
        return Known.TryGetValue((ven, dev), out var known) ? known : name;
    }

    [GeneratedRegex(@"Graphics Processor|Microsoft Basic|Standard VGA|Adaptador de Vídeo Básico", RegexOptions.IgnoreCase)] private static partial Regex GenericRx();
    [GeneratedRegex(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)] private static partial Regex PnpRx();
    [GeneratedRegex(@"\(0x([0-9A-F]{4})\)", RegexOptions.IgnoreCase)] private static partial Regex HexInNameRx();
}
