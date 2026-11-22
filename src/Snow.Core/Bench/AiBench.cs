using System.Numerics;
using System.Text.RegularExpressions;
using Snow.Core.Compat;

namespace Snow.Core.Bench;

/// <summary>
/// "Roda IA no meu PC?": quais modelos de linguagem locais (formato GGUF, quantização Q4, como no LM Studio / Ollama)
/// cabem no hardware e a que velocidade. Gerar texto é limitado pela banda de memória: cada palavra (token) lê o
/// modelo inteiro uma vez, então tokens/s ≈ eficiência × banda (GB/s) ÷ tamanho do modelo (GB).
/// </summary>
public sealed record AiModel(string Name, string Use, double Billions, double SizeGb);

public enum AiWhere { PlacaDeVideo, Dividido, Processador, NaoCabe }

public sealed record AiEstimate(AiModel Model, AiWhere Where, double TokensPerSecond, string Verdict);

public sealed record GpuAiSpec(double BandwidthGBs, string Runtime, double Efficiency, string? Note);

public sealed record AiReport
{
    public string Gpu { get; init; } = "";
    public double VramGb { get; init; }
    public double RamGb { get; init; }
    public double RamBandwidthGBs { get; init; }
    public double CpuGflops { get; init; }
    public GpuAiSpec? GpuSpec { get; init; }
    public IReadOnlyList<AiEstimate> Models { get; init; } = [];
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public static class AiBench
{
    /// <summary>Tamanhos reais dos arquivos Q4_K_M mais baixados (GB).</summary>
    public static readonly IReadOnlyList<AiModel> Catalog =
    [
        new("Llama 3.2 3B / Qwen 2.5 3B", "Assistente leve, resumos", 3, 2.0),
        new("Llama 3.1 8B / Qwen 3 8B", "Assistente de uso geral", 8, 4.9),
        new("Gemma 3 12B", "Conversa e textos melhores", 12, 7.3),
        new("Qwen 3 14B / Phi-4 14B", "Código e raciocínio", 14, 9.0),
        new("Qwen 3 32B / Gemma 3 27B", "Perto dos modelos online", 32, 19.9),
        new("Llama 3.3 70B", "Topo de linha local", 70, 42.5),
    ];

    // Banda de memória de vídeo (GB/s, especificação do fabricante).
    private static readonly (string rx, double bw)[] GpuBandwidth =
    [
        (@"RTX\s*5090", 1792), (@"RTX\s*5080", 960), (@"RTX\s*5070\s*Ti", 896), (@"RTX\s*5070", 672), (@"RTX\s*5060\s*Ti", 448), (@"RTX\s*5060", 448),
        (@"RTX\s*4090", 1008), (@"RTX\s*4080", 717), (@"RTX\s*4070\s*Ti", 504), (@"RTX\s*4070", 504), (@"RTX\s*4060\s*Ti", 288), (@"RTX\s*4060", 272),
        (@"RTX\s*3090", 936), (@"RTX\s*3080", 760), (@"RTX\s*3070", 448), (@"RTX\s*3060\s*Ti", 448), (@"RTX\s*3060", 360), (@"RTX\s*3050", 224),
        (@"RTX\s*2080", 448), (@"RTX\s*2070", 448), (@"RTX\s*2060", 336),
        (@"GTX\s*1660\s*(Super|Ti)", 336), (@"GTX\s*1660", 192), (@"GTX\s*1650", 128), (@"GTX\s*1080\s*Ti", 484), (@"GTX\s*1080", 320), (@"GTX\s*1070", 256),
        (@"GTX\s*1060", 192), (@"GTX\s*1050\s*Ti", 112), (@"GTX\s*1050", 112), (@"GTX\s*970", 224), (@"GTX\s*960", 112),
        (@"RX\s*9070\s*XT", 640), (@"RX\s*9070", 640), (@"RX\s*9060\s*XT", 320), (@"RX\s*7900\s*XTX", 960), (@"RX\s*7900", 800), (@"RX\s*7800", 624),
        (@"RX\s*7700", 432), (@"RX\s*7600", 288), (@"RX\s*6950", 576), (@"RX\s*6900", 512), (@"RX\s*6800", 512), (@"RX\s*6750", 432), (@"RX\s*6700", 384),
        (@"RX\s*6650", 280), (@"RX\s*6600\s*XT", 256), (@"RX\s*6600", 224), (@"RX\s*6500", 144), (@"RX\s*5700", 448), (@"RX\s*5600", 288), (@"RX\s*5500", 224),
        (@"Vega\s*64", 484), (@"Vega\s*56", 410), (@"RX\s*590", 256), (@"RX\s*580\s*2048\s*SP", 224), (@"RX\s*580", 256), (@"RX\s*570", 224), (@"RX\s*480", 256),
        (@"RX\s*470", 211), (@"RX\s*560", 112), (@"RX\s*550", 112),
        (@"Arc\s*B580", 456), (@"Arc\s*B570", 380), (@"Arc\s*A770", 560), (@"Arc\s*A750", 512), (@"Arc\s*A580", 512), (@"Arc\s*A380", 186),
    ];

    /// <summary>Banda, programa recomendado e eficiência típica (fração da banda teórica que o llama.cpp aproveita).</summary>
    public static GpuAiSpec? Gpu(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return null;
        double? bw = GpuBandwidth.Where(g => Regex.IsMatch(gpuName, g.rx, RegexOptions.IgnoreCase)).Select(g => (double?)g.bw).FirstOrDefault();
        // Fora da tabela: estima pela posição no índice de jogos (GTX 1060 = 100 ≈ 192 GB/s)
        bw ??= HardwareIndex.Gpu(gpuName) is { } idx ? Math.Round(192 * idx / 100.0) : null;
        if (bw is null) return null;
        var n = gpuName;
        if (Regex.IsMatch(n, "NVIDIA|GeForce|RTX|GTX", RegexOptions.IgnoreCase))
            return new(bw.Value, "LM Studio ou Ollama (CUDA)", Regex.IsMatch(n, @"GTX\s*(9|10)\d\d") ? 0.55 : 0.7, null);
        if (Regex.IsMatch(n, @"RX\s*(4|5)[5-9]0|Vega", RegexOptions.IgnoreCase))
            return new(bw.Value, "LM Studio (motor Vulkan)", 0.45,
                "Placas AMD Polaris/Vega não têm suporte ao ROCm: use o motor Vulkan do LM Studio (ou llama.cpp Vulkan). O Ollama roda só no processador nelas.");
        if (Regex.IsMatch(n, "Radeon|RX", RegexOptions.IgnoreCase))
            return new(bw.Value, "LM Studio (Vulkan) ou Ollama", 0.6, null);
        if (Regex.IsMatch(n, "Arc", RegexOptions.IgnoreCase))
            return new(bw.Value, "LM Studio (Vulkan)", 0.5, null);
        return null;
    }

    private const double CpuEfficiency = 0.6;
    private const double VramReserveGb = 1.0;   // contexto (KV cache) + o que o Windows usa da placa
    private const double RamReserveGb = 4.0;    // Windows e apps abertos

    public static AiEstimate Estimate(AiModel m, GpuAiSpec? gpu, double vramGb, double ramGb, double ramBwGBs)
    {
        var cpuTps = ramBwGBs > 0 ? CpuEfficiency * ramBwGBs / m.SizeGb : 0;
        if (gpu is not null && m.SizeGb + VramReserveGb <= vramGb)
        {
            var tps = gpu.Efficiency * gpu.BandwidthGBs / m.SizeGb;
            return new(m, AiWhere.PlacaDeVideo, Math.Round(tps, 1), Speed(tps) + " na placa de vídeo");
        }
        var gpuPart = gpu is null ? 0 : Math.Max(0, vramGb - VramReserveGb);
        var cpuPart = m.SizeGb - gpuPart;
        if (cpuPart + RamReserveGb > ramGb || ramBwGBs <= 0)
            return new(m, AiWhere.NaoCabe, 0, $"Não cabe: precisa de ~{m.SizeGb + RamReserveGb:0} GB entre memória de vídeo e RAM");
        if (gpu is not null && gpuPart >= 1)
        {
            // Parte na placa, parte na RAM: o tempo por token é a soma dos dois pedaços
            var t = gpuPart / (gpu.Efficiency * gpu.BandwidthGBs) + cpuPart / (CpuEfficiency * ramBwGBs);
            var tps = 1 / t;
            return new(m, AiWhere.Dividido, Math.Round(tps, 1), Speed(tps) + $" dividindo placa de vídeo e RAM ({gpuPart:0} + {cpuPart:0} GB)");
        }
        return new(m, AiWhere.Processador, Math.Round(cpuTps, 1), Speed(cpuTps) + " só no processador");
    }

    public static string Speed(double tps) => tps switch
    {
        >= 30 => $"~{tps:0} tokens/s: instantâneo",
        >= 12 => $"~{tps:0} tokens/s: confortável, mais rápido que a leitura",
        >= 5 => $"~{tps:0} tokens/s: usável, no ritmo da leitura",
        >= 1.5 => $"~{tps:0.#} tokens/s: lento, só para respostas curtas",
        _ => $"~{tps:0.#} tokens/s: lento demais para conversar",
    };

    public static AiReport Analyze(string? gpuName, double vramGb, double ramGb, double ramBwGBs, double cpuGflops)
    {
        var gpu = Gpu(gpuName);
        var rows = Catalog.Select(m => Estimate(m, gpu, vramGb, ramGb, ramBwGBs)).ToList();
        var best = rows.Where(r => r.TokensPerSecond >= 10).OrderByDescending(r => r.Model.Billions).FirstOrDefault()
                   ?? rows.Where(r => r.TokensPerSecond >= 5).OrderByDescending(r => r.Model.Billions).FirstOrDefault();
        var summary = best is null
            ? "Este PC roda só modelos pequenos, e devagar. Para IA local, o que mais pesa é memória de vídeo (VRAM) e banda de memória."
            : $"O melhor equilíbrio aqui é {best.Model.Name} ({best.Model.Billions:0} bilhões de parâmetros), {best.Verdict.Split(':')[0].TrimStart('~')}.";
        var notes = new List<string>();
        if (gpu?.Note is { } n) notes.Add(n);
        if (gpu is not null) notes.Add($"Programa sugerido: {gpu.Runtime}. Escolha arquivos GGUF com Q4_K_M.");
        if (rows.Any(r => r.Where == AiWhere.Dividido))
            notes.Add("Modelos maiores que a memória de vídeo ficam divididos com a RAM; a velocidade cai para perto da do processador.");
        if (ramBwGBs is > 0 and < 20)
            notes.Add($"A RAM está entregando {ramBwGBs:0} GB/s. Com mais pentes iguais (dois ou quatro canais), a banda e a velocidade no processador sobem quase na mesma proporção.");
        notes.Add("Geração de imagens (Stable Diffusion): SD 1.5 pede 4 GB de VRAM e SDXL pede 8 GB. Em placas AMD no Windows, use o Amuse ou o ComfyUI com DirectML/ZLUDA.");
        return new AiReport
        {
            Gpu = gpuName ?? "", VramGb = vramGb, RamGb = ramGb, RamBandwidthGBs = ramBwGBs, CpuGflops = cpuGflops,
            GpuSpec = gpu, Models = rows, Summary = summary, Notes = notes,
        };
    }
}

/// <summary>Cálculo de matriz em ponto flutuante (o mesmo tipo de conta da IA) com SIMD em todos os núcleos. Resultado em GFLOPS.</summary>
public static class AiCompute
{
    public static double CpuGflops(int threads, TimeSpan duration, CancellationToken ct = default)
    {
        const int n = 96; // blocos pequenos cabem no cache: mede o cálculo, não a memória
        long total = 0;
        var end = DateTime.UtcNow + duration;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, Math.Max(1, threads), new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, t =>
        {
            var a = new float[n * n]; var b = new float[n * n]; var c = new float[n * n];
            var rnd = new Random(t + 1);
            for (var i = 0; i < a.Length; i++) { a[i] = (float)rnd.NextDouble(); b[i] = (float)rnd.NextDouble(); }
            long local = 0;
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                MatMul(a, b, c, n);
                local += 2L * n * n * n;
            }
            Interlocked.Add(ref total, local);
        });
        return Math.Round(total / sw.Elapsed.TotalSeconds / 1e9, 1);
    }

    /// <summary>C = A × B (linhas por vetores SIMD). Também usado nos testes para conferir a conta.</summary>
    public static void MatMul(float[] a, float[] b, float[] c, int n)
    {
        var w = Vector<float>.Count;
        Array.Clear(c);
        for (var i = 0; i < n; i++)
        {
            var row = i * n;
            for (var k = 0; k < n; k++)
            {
                var aik = new Vector<float>(a[row + k]);
                var bk = k * n;
                var j = 0;
                for (; j <= n - w; j += w)
                {
                    var cv = new Vector<float>(c, row + j);
                    (cv + aik * new Vector<float>(b, bk + j)).CopyTo(c, row + j);
                }
                for (; j < n; j++) c[row + j] += a[row + k] * b[bk + j];
            }
        }
    }
}
