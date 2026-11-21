using System.Globalization;
using System.Text.RegularExpressions;
using Snow.Core.Hardware;

namespace Snow.Core.Compat;

/// <summary>
/// Índices aproximados de desempenho em jogos, para comparar com requisitos oficiais.
///  • GPU: desempenho relativo em 1080p (GeForce GTX 1060 6 GB = 100), consolidado de médias públicas de benchmarks.
///  • CPU: desempenho por núcleo em jogos (Core i7-4770 = 100) × fator de threads (jogos atuais usam 6+ threads).
/// São estimativas (±10–15%): servem para dizer "acima/abaixo do mínimo", não para prever FPS exato.
/// </summary>
public static partial class HardwareIndex
{
    // ───────────────────────────── GPU ─────────────────────────────
    // Ordem importa: o primeiro padrão que casar vence (específico antes do genérico).
    private static readonly (string pattern, int index)[] Gpus =
    [
        // NVIDIA RTX 50/40/30/20
        (@"RTX\s*5090", 680), (@"RTX\s*5080", 500), (@"RTX\s*5070\s*Ti", 450), (@"RTX\s*5070", 345), (@"RTX\s*5060\s*Ti", 285), (@"RTX\s*5060", 245), (@"RTX\s*5050", 190),
        (@"RTX\s*4090", 550), (@"RTX\s*4080", 430), (@"RTX\s*4070\s*Ti", 360), (@"RTX\s*4070\s*Super", 340), (@"RTX\s*4070", 290), (@"RTX\s*4060\s*Ti", 240), (@"RTX\s*4060", 200), (@"RTX\s*4050", 165),
        (@"RTX\s*3090", 330), (@"RTX\s*3080\s*Ti", 320), (@"RTX\s*3080", 300), (@"RTX\s*3070\s*Ti", 260), (@"RTX\s*3070", 245), (@"RTX\s*3060\s*Ti", 215), (@"RTX\s*3060", 170), (@"RTX\s*3050", 125),
        (@"RTX\s*2080\s*Ti", 270), (@"RTX\s*2080\s*Super", 225), (@"RTX\s*2080", 215), (@"RTX\s*2070\s*Super", 200), (@"RTX\s*2070", 180), (@"RTX\s*2060\s*Super", 170), (@"RTX\s*2060", 145),
        // NVIDIA GTX 16/10/9/7/6
        (@"GTX\s*1660\s*(Super|Ti)", 127), (@"GTX\s*1660", 113), (@"GTX\s*1650\s*Super", 95), (@"GTX\s*1650", 75), (@"GTX\s*1630", 42),
        (@"GTX\s*1080\s*Ti", 215), (@"GTX\s*1080", 165), (@"GTX\s*1070\s*Ti", 150), (@"GTX\s*1070", 133), (@"GTX\s*1060.*3\s*GB", 93), (@"GTX\s*1060", 100),
        (@"GTX\s*1050\s*Ti", 62), (@"GTX\s*1050", 50), (@"GT\s*1030", 25),
        (@"GTX\s*980\s*Ti", 130), (@"GTX\s*980", 100), (@"GTX\s*970", 88), (@"GTX\s*960", 55), (@"GTX\s*950", 47),
        (@"GTX\s*780\s*Ti", 85), (@"GTX\s*780", 75), (@"GTX\s*770", 60), (@"GTX\s*760", 48), (@"GTX\s*750\s*Ti", 32), (@"GTX\s*750", 27),
        (@"GTX\s*680", 58), (@"GTX\s*670", 52), (@"GTX\s*660", 42), (@"GTX\s*650", 22), (@"GT\s*730", 10), (@"GT\s*710", 5),
        (@"GTX\s*580", 38), (@"GTX\s*570", 34), (@"GTX\s*560", 33), (@"GTX\s*480", 32), (@"GTX\s*470", 28), (@"GTX\s*460", 28),
        // AMD RX 9000/7000/6000/5000
        (@"RX\s*9070\s*XT", 470), (@"RX\s*9070", 420), (@"RX\s*9060\s*XT", 270),
        (@"RX\s*7900\s*XTX", 500), (@"RX\s*7900\s*XT", 440), (@"RX\s*7900\s*GRE", 380), (@"RX\s*7800\s*XT", 350), (@"RX\s*7700\s*XT", 290), (@"RX\s*7600\s*XT", 220), (@"RX\s*7600", 210),
        (@"RX\s*6950\s*XT", 390), (@"RX\s*6900\s*XT", 370), (@"RX\s*6800\s*XT", 345), (@"RX\s*6800", 300), (@"RX\s*6750\s*XT", 255), (@"RX\s*6700\s*XT", 240), (@"RX\s*6700", 215),
        (@"RX\s*6650\s*XT", 200), (@"RX\s*6600\s*XT", 190), (@"RX\s*6600", 165), (@"RX\s*6500\s*XT", 75), (@"RX\s*6400", 62),
        (@"RX\s*5700\s*XT", 190), (@"RX\s*5700", 170), (@"RX\s*5600\s*XT", 150), (@"RX\s*5500\s*XT", 100),
        // AMD Polaris/Vega/GCN
        (@"Vega\s*64", 150), (@"Vega\s*56", 140), (@"RX\s*590", 108), (@"RX\s*580\s*2048\s*SP", 88), (@"RX\s*580", 100), (@"RX\s*570", 88), (@"RX\s*480", 95), (@"RX\s*470", 85), (@"RX\s*560", 50), (@"RX\s*550", 30), (@"RX\s*460", 45),
        (@"R9\s*Fury", 125), (@"R9\s*390", 90), (@"R9\s*380", 62), (@"R9\s*290", 92), (@"R9\s*280X?", 62), (@"R9\s*270X?", 50), (@"R7\s*370", 50), (@"R7\s*360", 38), (@"R7\s*260X?", 32), (@"R7\s*250", 18), (@"R7\s*240", 11), (@"R5\s*2\d0", 5),
        (@"HD\s*7970", 62), (@"HD\s*7950", 55), (@"HD\s*7870", 48), (@"HD\s*7850", 40), (@"HD\s*7790", 38), (@"HD\s*77\d0", 28), (@"HD\s*6970", 36), (@"HD\s*6950", 33), (@"HD\s*6870", 30), (@"HD\s*6570", 10),
        // Intel Arc e integradas
        (@"Arc\s*B580", 210), (@"Arc\s*B570", 185), (@"Arc\s*A770", 175), (@"Arc\s*A750", 165), (@"Arc\s*A580", 140), (@"Arc\s*A380", 58), (@"Arc\s*A310", 45),
        (@"Radeon\s*890M", 80), (@"Radeon\s*780M", 65), (@"Radeon\s*760M", 52), (@"Radeon\s*680M", 55), (@"Radeon\s*660M", 40),
        (@"Vega\s*11", 28), (@"Vega\s*8|Radeon\(TM\) Graphics", 22), (@"Iris\s*Xe", 27), (@"Arc\(TM\) Graphics|Intel\(R\) Arc Graphics", 45),
        (@"UHD\s*(Graphics\s*)?7\d0", 14), (@"UHD\s*(Graphics\s*)?6[23]0", 11), (@"UHD\s*Graphics", 10), (@"Intel.*HD\s*(Graphics\s*)?5[0-9]0\b", 9), (@"Intel.*HD\s*(Graphics\s*)?4600|^HD\s*Graphics\s*4600", 8), (@"Intel.*HD\s*(Graphics\s*)?4[0-9]00|HD\s*Graphics\s*4[0-9]00", 6), (@"HD\s*Graphics\s*3000|Intel.*HD\s*3000", 3),
    ];

    private static readonly (Regex re, int index)[] GpuRx = Gpus.Select(g => (new Regex(g.pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), g.index)).ToArray();

    public static int? Gpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var (re, idx) in GpuRx) if (re.IsMatch(name)) return idx;
        return null;
    }

    // ───────────────────────────── CPU ─────────────────────────────
    /// <summary>Modelos que aparecem em requisitos oficiais: (desempenho por núcleo, threads).</summary>
    private static readonly (string pattern, int st, int threads)[] KnownCpus =
    [
        (@"i3-530\b", 55, 4), (@"i3-540\b", 57, 4), (@"i5-750\b", 60, 4), (@"Core 2 Quad|Q6600", 40, 4), (@"Phenom.*(X3|8650)", 35, 3), (@"Phenom", 42, 4),
        (@"FX-4100", 50, 4), (@"FX-43\d0", 58, 4), (@"FX-63\d0", 55, 6), (@"FX-83\d0", 60, 8), (@"FX-9590", 68, 8),
        (@"i3-3225", 78, 4), (@"i3-32\d0", 76, 4), (@"i5-2500K", 82, 4), (@"i7-2600", 85, 8), (@"i5-3470", 84, 4), (@"i5-3570K?", 88, 4), (@"i7-3770", 93, 8),
        (@"i3-41\d0", 88, 4), (@"i5-4430", 80, 4), (@"i5-4460", 83, 4), (@"i5-4590", 87, 4), (@"i5-4670", 92, 4), (@"i5-4690", 94, 4), (@"i7-4770K", 102, 8), (@"i7-4770", 100, 8), (@"i7-4790", 103, 8),
        (@"i3-6100", 103, 4), (@"i3-6300", 108, 4), (@"i5-6500", 103, 4), (@"i5-6600K?", 110, 4), (@"i7-6700K", 118, 8), (@"i7-6700", 113, 8), (@"i5-7300U", 95, 4), (@"i5-7500", 110, 4), (@"i7-7700K?", 120, 8),
        (@"i5-8250U", 88, 8), (@"i5-8400", 112, 6), (@"i7-8700K", 131, 12), (@"i7-8700", 128, 12), (@"i5-9400F?", 115, 6), (@"i5-9600K", 128, 6), (@"i7-9700K", 135, 8), (@"i9-9900K", 138, 16),
        (@"i5-10400F?", 120, 12), (@"i5-11400F?", 140, 12), (@"i5-12400F?", 165, 12), (@"i7-12700", 185, 20), (@"i5-13400F?", 170, 16), (@"i5-14400F?", 175, 16),
        (@"Athlon.*200GE", 75, 4), (@"Ryzen 3 1200", 82, 4), (@"Ryzen 5 1400", 85, 8), (@"Ryzen 5 1500X", 88, 8), (@"Ryzen 5 1600X", 93, 12), (@"Ryzen 5 1600", 86, 12), (@"Ryzen 7 1700", 88, 16),
        (@"Ryzen 5 2600X", 100, 12), (@"Ryzen 5 2600", 96, 12), (@"Ryzen 7 2700X", 102, 16), (@"Ryzen 3 3300U", 80, 4), (@"Ryzen 3 3200G", 92, 4),
        (@"Ryzen 3 3300X", 120, 8), (@"Ryzen 5 3600X", 121, 12), (@"Ryzen 5 3600", 118, 12), (@"Ryzen 7 3700X", 122, 16),
        (@"Ryzen 5 5600X3D", 175, 12), (@"Ryzen 7 5700X3D", 175, 16), (@"Ryzen 7 5800X3D", 185, 16), (@"Ryzen 5 5600X?", 150, 12), (@"Ryzen 7 5700X", 150, 16), (@"Ryzen 7 5800X", 155, 16),
        (@"Ryzen 5 7600X?", 175, 12), (@"Ryzen 7 7800X3D", 200, 16), (@"Ryzen 7 9800X3D", 220, 16), (@"Ryzen 5 9600X", 190, 12),
    ];

    private static readonly (Regex re, int st, int threads)[] KnownRx =
        KnownCpus.Select(c => (new Regex(c.pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), c.st, c.threads)).ToArray();

    /// <summary>Turbo máximo (GHz) dos Xeon E5 v3/v4 mais usados em placas X99.</summary>
    private static readonly Dictionary<string, double> XeonTurbo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1620 v3"] = 3.6, ["1650 v3"] = 3.8, ["1660 v3"] = 3.5, ["2620 v3"] = 3.2, ["2630 v3"] = 3.2, ["2640 v3"] = 3.4, ["2643 v3"] = 3.7, ["2650 v3"] = 3.0,
        ["2660 v3"] = 3.3, ["2667 v3"] = 3.6, ["2670 v3"] = 3.1, ["2678 v3"] = 3.3, ["2680 v3"] = 3.3, ["2690 v3"] = 3.5, ["2697 v3"] = 3.6, ["2699 v3"] = 3.6,
        ["1650 v4"] = 4.0, ["1660 v4"] = 3.8, ["2620 v4"] = 3.0, ["2630 v4"] = 3.1, ["2640 v4"] = 3.4, ["2643 v4"] = 3.7, ["2650 v4"] = 2.9, ["2660 v4"] = 3.2,
        ["2667 v4"] = 3.6, ["2680 v4"] = 3.3, ["2683 v4"] = 3.0, ["2689 v4"] = 3.8, ["2690 v4"] = 3.5, ["2697 v4"] = 3.6, ["2699 v4"] = 3.6,
    };

    public static double ThreadFactor(int threads) => threads switch
    {
        >= 12 => 1.0, >= 8 => 0.97, >= 6 => 0.93, >= 4 => 0.85, 3 => 0.72, _ => 0.6,
    };

    [GeneratedRegex(@"i[3579]-(\d{4,5})([A-Z]*)", RegexOptions.IgnoreCase)] private static partial Regex IntelCore();
    [GeneratedRegex(@"Ultra\s*[579]\s*(\d)\d\d", RegexOptions.IgnoreCase)] private static partial Regex IntelUltra();
    [GeneratedRegex(@"E[35]-(\d{4})L?\s*v(\d)", RegexOptions.IgnoreCase)] private static partial Regex Xeon();
    [GeneratedRegex(@"Ryzen\s*\d\s*(PRO\s*)?(\d)(\d)\d\d([A-Z0-9]*)", RegexOptions.IgnoreCase)] private static partial Regex Ryzen();
    [GeneratedRegex(@"@\s*([\d.]+)\s*GHz", RegexOptions.IgnoreCase)] private static partial Regex AtGhz();

    /// <summary>Índice de jogo de um processador pelo nome (requisito) — usa a tabela; sem dado, null.</summary>
    public static int? CpuByName(string name)
    {
        foreach (var (re, st, threads) in KnownRx)
            if (re.IsMatch(name)) return (int)Math.Round(st * ThreadFactor(threads));
        return null;
    }

    /// <summary>Índice de jogo do processador deste PC: tabela, senão estimativa por arquitetura × turbo.</summary>
    public static int? Cpu(CpuInfo cpu)
    {
        var threads = Math.Max(1, cpu.LogicalProcessors);
        foreach (var (re, st, _) in KnownRx)
            if (re.IsMatch(cpu.Name)) return (int)Math.Round(st * ThreadFactor(threads));
        return EstimateSingleThread(cpu) is { } est ? (int)Math.Round(est * ThreadFactor(threads)) : null;
    }

    /// <summary>Desempenho por núcleo estimado: IPC da arquitetura × turbo, normalizado (Haswell a 3,9 GHz = 100).</summary>
    public static double? EstimateSingleThread(CpuInfo cpu)
    {
        var name = cpu.Name;
        double baseGhz = cpu.MaxClockMhz > 0 ? cpu.MaxClockMhz / 1000.0
            : AtGhz().Match(name) is { Success: true } g && double.TryParse(g.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ghz) ? ghz : 0;
        double? ipc = null, turbo = null;

        if (Xeon().Match(name) is { Success: true } x)
        {
            ipc = x.Groups[2].Value switch { "2" => 0.93, "3" => 1.0, "4" => 1.03, "5" or "6" => 1.1, _ => 0.88 };
            if (XeonTurbo.TryGetValue($"{x.Groups[1].Value} v{x.Groups[2].Value}", out var t)) turbo = t;
        }
        else if (IntelUltra().Match(name) is { Success: true }) ipc = 1.6;
        else if (IntelCore().Match(name) is { Success: true } c)
        {
            var model = c.Groups[1].Value;
            var gen = model.Length == 5 ? int.Parse(model[..2]) : int.Parse(model[..1]);
            ipc = gen switch { >= 12 => 1.5, 11 => 1.3, >= 6 => 1.1, 5 => 1.03, 4 => 1.0, 3 => 0.93, _ => 0.88 };
            if (c.Groups[2].Value.Contains('U') || c.Groups[2].Value.Contains('Y')) turbo = baseGhz * 1.6 * 0.9; // notebook: turbo alto, mas limitado por energia
        }
        else if (Ryzen().Match(name) is { Success: true } r)
        {
            var series = r.Groups[2].Value[0];
            var apu = r.Groups[4].Value.StartsWith("G", StringComparison.OrdinalIgnoreCase) || r.Groups[4].Value.StartsWith("U", StringComparison.OrdinalIgnoreCase) || r.Groups[4].Value.StartsWith("H", StringComparison.OrdinalIgnoreCase);
            ipc = series switch
            {
                '1' => 1.0, '2' => apu ? 1.0 : 1.05, '3' => apu ? 1.05 : 1.2, '4' => 1.2, '5' => 1.45, '6' => 1.5, '7' => 1.6, '8' => 1.6, '9' => 1.7, _ => 1.0,
            };
            if (r.Groups[4].Value.Contains("X3D", StringComparison.OrdinalIgnoreCase)) ipc *= 1.15;
        }
        else if (name.Contains("FX-", StringComparison.OrdinalIgnoreCase)) ipc = 0.6;
        if (ipc is null || baseGhz <= 0 && turbo is null) return null;
        var boost = turbo ?? baseGhz * 1.25;
        return Math.Round(ipc.Value * boost / 3.9 * 100, 1);
    }
}
