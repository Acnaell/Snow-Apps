using System.Globalization;

namespace Snow.Core.Monitor;

/// <summary>Estatísticas de quadros a partir de frametimes em ms.</summary>
public sealed record FrameStats(int Frames, double AvgFps, double Low1, double Low01, double MedianMs, int Stutters, double DurationSeconds)
{
    public static FrameStats? From(IReadOnlyList<double> frameTimesMs)
    {
        var ft = frameTimesMs.Where(f => f > 0 && f < 1000).ToArray();
        if (ft.Length < 10) return null;
        var total = ft.Sum();
        var sorted = ft.OrderBy(f => f).ToArray();
        double Pct(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(sorted.Length * p))];
        var median = Pct(0.5);
        return new FrameStats(
            ft.Length,
            Math.Round(1000.0 * ft.Length / total, 1),
            Math.Round(1000.0 / Pct(0.99), 1),
            Math.Round(1000.0 / Pct(0.999), 1),
            Math.Round(median, 2),
            ft.Count(f => f > 2 * median),
            Math.Round(total / 1000, 1));
    }
}

public enum Bottleneck { Gpu, Cpu, LimiteFps, Memoria, Equilibrado, SemCarga, Desconhecido }

/// <summary>Uma amostra por segundo do Monitor.</summary>
public sealed record UsageSample(
    DateTime Time,
    double? CpuPercent,
    double? BusiestCorePercent,
    double? GpuPercent,
    double? VramUsedGb,
    double RamUsedPercent,
    double? Fps,
    double? Low1);

public static class BottleneckClassifier
{
    /// <summary>
    /// Heurística por amostra:
    ///  • GPU ≥ 95% → limitado pela GPU (o normal num jogo pesado);
    ///  • núcleo mais ocupado ≥ 90% com GPU &lt; 85% → limitado pela CPU (thread principal do jogo);
    ///  • RAM ≥ 92% → memória;
    ///  • GPU e CPU folgadas com FPS estável → teto de FPS / VSync;
    /// </summary>
    public static Bottleneck Classify(UsageSample s)
    {
        if (s.RamUsedPercent >= 92) return Bottleneck.Memoria;
        if (s.GpuPercent is >= 95) return Bottleneck.Gpu;
        if (s.BusiestCorePercent is >= 90 && s.GpuPercent is < 85) return Bottleneck.Cpu;
        if (s.GpuPercent is null || s.BusiestCorePercent is null) return Bottleneck.Desconhecido;
        if (s.Fps is null && s.GpuPercent < 40 && s.BusiestCorePercent < 50) return Bottleneck.SemCarga;
        if (s.Fps is > 0 && s.GpuPercent < 85 && s.BusiestCorePercent < 85) return Bottleneck.LimiteFps;
        return Bottleneck.Equilibrado;
    }

    public static string Label(this Bottleneck b) => b switch
    {
        Bottleneck.Gpu => "Limitado pela GPU",
        Bottleneck.Cpu => "Limitado pela CPU",
        Bottleneck.LimiteFps => "No teto de FPS / VSync",
        Bottleneck.Memoria => "Limitado pela memória RAM",
        Bottleneck.Equilibrado => "Equilibrado",
        Bottleneck.SemCarga => "Sem carga (nenhum jogo pesando)",
        _ => "Sem dados suficientes",
    };

    public static string Advice(this Bottleneck b) => b switch
    {
        Bottleneck.Gpu => "A placa de vídeo é o limite: reduza resolução 3D, sombras e efeitos, ou use FSR/DLSS.",
        Bottleneck.Cpu => "A thread principal do jogo é o limite: baixe distância de visão e densidade (NPCs, multidões), feche programas em segundo plano. Mais canais de memória ajudam muito aqui.",
        Bottleneck.LimiteFps => "O jogo está no limite de FPS que você definiu (ou VSync). Há folga de hardware.",
        Bottleneck.Memoria => "A RAM está quase cheia: feche navegadores e launchers antes de jogar.",
        Bottleneck.Equilibrado => "CPU e GPU com uso parecido, sem um limite claro.",
        Bottleneck.SemCarga => "O PC está folgado. Abra um jogo para ver onde está o limite.",
        _ => "Abra um jogo para analisar.",
    };
}

public sealed record SessionSummary(FrameStats? Frames, IReadOnlyDictionary<Bottleneck, double> TimeShare, Bottleneck Main, double? AvgCpu, double? AvgGpu)
{
    public static SessionSummary From(IReadOnlyList<UsageSample> samples, IReadOnlyList<double>? frameTimes = null)
    {
        var classes = samples.Select(BottleneckClassifier.Classify).Where(c => c != Bottleneck.Desconhecido).ToList();
        var share = Enum.GetValues<Bottleneck>()
            .ToDictionary(b => b, b => classes.Count == 0 ? 0 : Math.Round(100.0 * classes.Count(c => c == b) / classes.Count, 1));
        var main = classes.Count == 0 ? Bottleneck.Desconhecido : share.OrderByDescending(kv => kv.Value).First().Key;
        double? Avg(Func<UsageSample, double?> f) { var v = samples.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : Math.Round(v.Average(), 1); }
        return new SessionSummary(frameTimes is null ? null : FrameStats.From(frameTimes), share, main, Avg(s => s.CpuPercent), Avg(s => s.GpuPercent));
    }
}

/// <summary>Leitura de CSV do PresentMon (1.x e 2.x) e do Snow Bench.</summary>
public sealed record PresentMonCapture(string Application, IReadOnlyList<double> FrameTimes, IReadOnlyList<double> CpuBusy, IReadOnlyList<double> GpuBusy)
{
    public FrameStats? Stats => FrameStats.From(FrameTimes);

    /// <summary>
    /// % de quadros em que a GPU ficou ocupada quase o quadro inteiro (GPU-bound) e em que a CPU foi o limite.
    /// Só existe com métricas 2.x (colunas CPUBusy/GPUBusy).
    /// </summary>
    public (double gpuBound, double cpuBound)? BoundShare
    {
        get
        {
            if (GpuBusy.Count != FrameTimes.Count || CpuBusy.Count != FrameTimes.Count || FrameTimes.Count == 0) return null;
            int gpu = 0, cpu = 0;
            for (var i = 0; i < FrameTimes.Count; i++)
            {
                var ft = FrameTimes[i];
                if (ft <= 0) continue;
                if (GpuBusy[i] >= 0.9 * ft) gpu++;
                else if (CpuBusy[i] >= 0.9 * ft) cpu++;
            }
            return (Math.Round(100.0 * gpu / FrameTimes.Count, 1), Math.Round(100.0 * cpu / FrameTimes.Count, 1));
        }
    }

    public static PresentMonCapture Parse(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return new("", [], [], []);
        var header = SplitCsv(lines[0]);
        int Col(params string[] names) => names.Select(n => Array.FindIndex(header, h => h.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
        var ftCol = Col("FrameTime", "MsBetweenPresents", "msBetweenPresents");
        var cpuCol = Col("CPUBusy", "MsCPUBusy");
        var gpuCol = Col("GPUBusy", "MsGPUBusy", "msGPUActive");
        var appCol = Col("Application", "ProcessName");
        if (ftCol < 0) throw new FormatException("CSV sem coluna de frametime (FrameTime/MsBetweenPresents).");

        var ft = new List<double>(); var cpu = new List<double>(); var gpu = new List<double>();
        string app = "";
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsv(line);
            if (cells.Length <= ftCol || !TryNum(cells[ftCol], out var f) || f <= 0) continue;
            ft.Add(f);
            if (cpuCol >= 0) cpu.Add(cells.Length > cpuCol && TryNum(cells[cpuCol], out var c) ? c : 0);
            if (gpuCol >= 0) gpu.Add(cells.Length > gpuCol && TryNum(cells[gpuCol], out var g) ? g : 0);
            if (app.Length == 0 && appCol >= 0 && cells.Length > appCol) app = cells[appCol];
        }
        return new(app, ft, cpu, gpu);
    }

    private static bool TryNum(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static string[] SplitCsv(string line)
    {
        // CSV simples do PresentMon: vírgulas, campos sem vírgula interna (aspas opcionais)
        return line.Split(',').Select(c => c.Trim().Trim('"')).ToArray();
    }
}

public sealed record ComparisonRow(string Metric, string Before, string After, string Delta, bool Better);

public static class SessionComparer
{
    public static IReadOnlyList<ComparisonRow> Compare(FrameStats before, FrameStats after)
    {
        ComparisonRow Higher(string name, double a, double b, string unit = "") =>
            new(name, $"{a:0.#}{unit}", $"{b:0.#}{unit}", Pct(a, b), b >= a);
        ComparisonRow Lower(string name, double a, double b, string unit = "") =>
            new(name, $"{a:0.##}{unit}", $"{b:0.##}{unit}", Pct(a, b), b <= a);
        return
        [
            Higher("FPS médio", before.AvgFps, after.AvgFps),
            Higher("1% low", before.Low1, after.Low1),
            Higher("0,1% low", before.Low01, after.Low01),
            Lower("Frametime mediano", before.MedianMs, after.MedianMs, " ms"),
            new("Engasgos (por minuto)", $"{PerMin(before):0.#}", $"{PerMin(after):0.#}", Pct(PerMin(before), PerMin(after)), PerMin(after) <= PerMin(before)),
        ];
    }

    private static double PerMin(FrameStats s) => s.DurationSeconds <= 0 ? 0 : s.Stutters * 60.0 / s.DurationSeconds;

    private static string Pct(double a, double b) => a == 0 ? "—" : $"{(b - a) / a * 100:+0.#;-0.#;0}%";
}
