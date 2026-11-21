using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Snow.Core.Bench;

// ═══════════════════════════════════════ MODELOS ═══════════════════════════════════════

public sealed record DiskBench(string Drive, double SeqReadMBs, double SeqWriteMBs, double Random4kReadIops);

public sealed record BenchResult
{
    public DateTime When { get; init; } = DateTime.Now;
    public string Label { get; init; } = "";
    public string Cpu { get; init; } = "";
    public int CpuSingle { get; init; }
    public int CpuMulti { get; init; }
    public int Threads { get; init; }
    public double MemoryGBs { get; init; }
    public double MemoryLatencyNs { get; init; }
    public IReadOnlyList<DiskBench> Disks { get; init; } = [];
    public string? Version { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static BenchResult? FromJson(string json) { try { return JsonSerializer.Deserialize<BenchResult>(json); } catch (JsonException) { return null; } }
}

/// <summary>Leitura dos números para leigos: faixas de referência de memória e disco (valores físicos, não pontos).</summary>
public static class BenchVerdicts
{
    public static string Memory(double gbs) => gbs switch
    {
        < 10 => "baixa (típico de memória em canal único lenta)",
        < 20 => "modesta (canal único ou dual channel lento)",
        < 35 => "boa (dual channel)",
        _ => "alta (dual channel rápido ou 4 canais)",
    };

    public static string Latency(double ns) => ns switch
    {
        < 75 => "ótima",
        < 95 => "boa",
        < 120 => "alta",
        _ => "muito alta",
    };

    public static string Disk(DiskBench d) => d.SeqReadMBs switch
    {
        < 250 => "HD mecânico ou SSD com problema",
        < 700 => "SSD SATA",
        < 2500 => "NVMe PCIe 3.0",
        _ => "NVMe rápido",
    };

    /// <summary>Diferença percentual de uma métrica entre duas rodadas (positivo = melhorou).</summary>
    public static double Delta(double before, double after, bool lowerIsBetter = false) =>
        before <= 0 ? 0 : Math.Round(100.0 * (lowerIsBetter ? before - after : after - before) / before, 1);
}

// ═══════════════════════════════════════ CPU ═══════════════════════════════════════

/// <summary>
/// Carga de CPU determinística: inteiros (hash), ponto flutuante (polinômio) e acessos em cache (tabela de 32 KB).
/// O mesmo bloco sempre dá o mesmo resultado: no teste de estresse, um resultado diferente = erro de cálculo
/// (processador ou memória instáveis, overclock, temperatura).
/// </summary>
public static class CpuWork
{
    public const int Iterations = 20_000;

    public static ulong Block(ulong seed)
    {
        Span<uint> table = stackalloc uint[1024];
        var x = seed | 1;
        for (var i = 0; i < table.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; table[i] = (uint)x; }
        double f = (seed % 1000) / 1000.0 + 0.5;
        ulong acc = 0;
        for (var i = 0; i < Iterations; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            var idx = (int)(x & 1023);
            table[idx] = table[idx] * 2654435761u + (uint)i;
            acc += BitOperations.RotateLeft(x * 0x9E3779B97F4A7C15UL, (int)(table[idx] & 63)) ^ table[(idx + 511) & 1023];
            f = f * 0.999999 + ((f * f - 1.5) * 0.25 + 0.0001 * (i & 7));
            if ((i & 255) == 0) acc += (ulong)(BitConverter.DoubleToInt64Bits(f) & 0xFFFF);
        }
        return acc;
    }

    /// <summary>Pontos por thread: blocos por segundo × 10 (escala só para ficar legível; compare rodadas e PCs no Snow).</summary>
    public static int Score(long blocks, TimeSpan elapsed) => (int)Math.Round(blocks / elapsed.TotalSeconds * 10);

    public static (int score, long blocks) RunSingle(TimeSpan duration, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        long n = 0; ulong sink = 0;
        while (sw.Elapsed < duration && !ct.IsCancellationRequested) sink += Block((ulong)n++ + 1);
        GC.KeepAlive(sink);
        return (Score(n, sw.Elapsed), n);
    }

    public static (int score, long blocks) RunMulti(int threads, TimeSpan duration, CancellationToken ct = default)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            long n = 0; ulong sink = 0;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested) sink += Block((ulong)(t * 1_000_003 + n++ + 1));
            GC.KeepAlive(sink);
            Interlocked.Add(ref total, n);
        }) { IsBackground = true }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        return (Score(total, sw.Elapsed), total);
    }
}

// ═══════════════════════════════════════ MEMÓRIA ═══════════════════════════════════════

public static class MemoryWork
{
    /// <summary>Banda de cópia em GB/s (leitura + escrita), com várias threads para ocupar todos os canais.</summary>
    public static double Bandwidth(int threads, int megabytes = 256, TimeSpan? duration = null)
    {
        var len = megabytes * 1024L * 1024 / sizeof(long);
        var src = GC.AllocateUninitializedArray<long>((int)len);
        var dst = GC.AllocateUninitializedArray<long>((int)len);
        for (long i = 0; i < len; i++) src[i] = i;
        src.AsSpan().CopyTo(dst); // aquece (páginas alocadas de verdade)
        var limit = duration ?? TimeSpan.FromSeconds(2);
        var slice = (int)(len / threads);
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                src.AsSpan(t * slice, slice).CopyTo(dst.AsSpan(t * slice, slice)));
            bytes += 2L * slice * threads * sizeof(long);
        }
        return Math.Round(bytes / sw.Elapsed.TotalSeconds / 1e9, 1);
    }

    /// <summary>Latência em ns: percorre uma lista ligada aleatória maior que o cache (cada acesso depende do anterior).</summary>
    public static double LatencyNs(int megabytes = 128, int hops = 5_000_000)
    {
        var n = megabytes * 1024 * 1024 / sizeof(int);
        var next = new int[n];
        for (var i = 0; i < n; i++) next[i] = i;
        var rng = new Random(12345);
        for (var i = n - 1; i > 0; i--) { var j = rng.Next(i); (next[i], next[j]) = (next[j], next[i]); } // Sattolo: um único ciclo
        var p = 0;
        for (var i = 0; i < 100_000; i++) p = next[p];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < hops; i++) p = next[p];
        sw.Stop();
        GC.KeepAlive(p);
        return Math.Round(sw.Elapsed.TotalMilliseconds * 1e6 / hops, 1);
    }

    /// <summary>
    /// Teste de RAM: preenche blocos com padrões e confere. Qualquer diferença = erro de memória
    /// (pente com defeito, XMP/clock instável, temperatura). Retorna (erros, GB testados por passada).
    /// </summary>
    public sealed class Tester : IDisposable
    {
        private readonly List<ulong[]> _blocks = [];
        public double Gigabytes => _blocks.Sum(b => (double)b.Length) * 8 / (1L << 30);
        public long Errors;
        public int Passes;
        public string? FirstError;

        public Tester(long bytes)
        {
            const int blockLongs = 8 * 1024 * 1024; // 64 MB
            for (long done = 0; done < bytes; done += blockLongs * 8L)
            {
                try { _blocks.Add(GC.AllocateUninitializedArray<ulong>(blockLongs)); }
                catch (OutOfMemoryException) { break; }
            }
        }

        private static ulong Pattern(int pass, int block, long i) => (pass % 4) switch
        {
            0 => 0x5555_5555_5555_5555UL,
            1 => 0xAAAA_AAAA_AAAA_AAAAUL,
            2 => (ulong)(block * 0x1_0000_0000L + i) * 0x9E3779B97F4A7C15UL, // valor depende do endereço
            _ => ~((ulong)(block * 0x1_0000_0000L + i) * 0x9E3779B97F4A7C15UL),
        };

        /// <summary>Uma passada completa (escreve tudo e depois confere tudo).</summary>
        public void Pass(CancellationToken ct, Action? betweenWriteAndVerify = null)
        {
            var pass = Passes;
            Parallel.For(0, _blocks.Count, new ParallelOptions { CancellationToken = ct }, b =>
            {
                var a = _blocks[b];
                for (long i = 0; i < a.Length; i++) a[i] = Pattern(pass, b, i);
            });
            betweenWriteAndVerify?.Invoke();
            Parallel.For(0, _blocks.Count, new ParallelOptions { CancellationToken = ct }, b =>
            {
                var a = _blocks[b];
                for (long i = 0; i < a.Length; i++)
                    if (a[i] != Pattern(pass, b, i))
                    {
                        Interlocked.Increment(ref Errors);
                        FirstError ??= $"bloco {b}, posição {i}: esperado {Pattern(pass, b, i):X16}, lido {a[i]:X16}";
                    }
            });
            Passes++;
        }

        /// <summary>Simula um bit trocado (usado pelos testes do Snow para provar que o erro é detectado).</summary>
        public void CorruptForTest() => _blocks[0][123] ^= 1UL << 17;

        public void Dispose() { _blocks.Clear(); GC.Collect(); }
    }
}

// ═══════════════════════════════════════ ESTRESSE ═══════════════════════════════════════

public sealed record StressPoint(TimeSpan At, double? CpuPercent, double? PerformancePercent, long CpuErrors, long RamErrors);

public sealed record StressReport(TimeSpan Duration, long CpuBlocks, long CpuErrors, long RamErrors, double RamGb, int RamPasses,
    double? PerfStart, double? PerfEnd, string? FirstRamError)
{
    /// <summary>Queda do clock efetivo do início (1º minuto) para o fim (último minuto), em %.</summary>
    public double? ThrottlePercent => PerfStart is > 0 && PerfEnd is { } e ? Math.Round(100 * (PerfStart.Value - e) / PerfStart.Value, 1) : null;

    public IReadOnlyList<(bool ok, string title, string detail)> Verdicts()
    {
        var list = new List<(bool, string, string)>();
        list.Add(CpuErrors == 0
            ? (true, "Processador estável", $"{CpuBlocks:N0} blocos de cálculo conferidos, nenhum resultado errado.")
            : (false, $"Processador com {CpuErrors} erro(s) de cálculo", "Sinal de instabilidade: overclock/undervolt, temperatura alta ou fonte fraca. Volte a BIOS para o padrão e teste de novo."));
        if (RamGb > 0)
            list.Add(RamErrors == 0
                ? (true, "Memória sem erros", $"{RamGb:0.0} GB conferidos {RamPasses} vez(es).")
                : (false, $"Memória com {RamErrors} erro(s)", $"Um pente pode estar com defeito ou o clock/XMP instável. Primeiro erro: {FirstRamError}. Teste os pentes um por vez."));
        if (ThrottlePercent is { } t)
            list.Add(t >= 10
                ? (false, $"Clock caiu {t:0}% durante o teste", "O processador reduziu a velocidade sob carga contínua: provável temperatura (pasta térmica, poeira, cooler) ou limite de energia da placa.")
                : (true, "Sem queda de clock", $"O clock efetivo ficou estável ({PerfStart:0}% → {PerfEnd:0}% do clock base)."));
        return list;
    }

    public static StressReport From(IReadOnlyList<StressPoint> points, TimeSpan duration, long blocks, long cpuErr, long ramErr, double ramGb, int passes, string? firstRamError)
    {
        double? Avg(IEnumerable<StressPoint> p) { var v = p.Select(x => x.PerformancePercent).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : Math.Round(v.Average(), 1); }
        var minute = TimeSpan.FromMinutes(Math.Min(1, duration.TotalMinutes / 3));
        // ignora os primeiros 5 s (o turbo leva um instante para subir)
        var start = Avg(points.Where(p => p.At > TimeSpan.FromSeconds(5) && p.At <= minute + TimeSpan.FromSeconds(5)));
        var end = Avg(points.Where(p => p.At >= duration - minute));
        return new(duration, blocks, cpuErr, ramErr, ramGb, passes, start, end, firstRamError);
    }
}

/// <summary>Estresse de CPU com conferência de resultado: cada thread recalcula blocos cujo resultado já é conhecido.</summary>
public sealed class CpuStress
{
    private readonly ulong[] _seeds = Enumerable.Range(1, 16).Select(i => (ulong)i * 7919).ToArray();
    private readonly ulong[] _expected;
    public long Blocks;
    public long Errors;

    public CpuStress() => _expected = _seeds.Select(CpuWork.Block).ToArray();

    public void Run(int threads, CancellationToken ct, Func<ulong, ulong>? blockForTest = null)
    {
        var block = blockForTest ?? CpuWork.Block;
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var k = t;
            while (!ct.IsCancellationRequested)
            {
                var i = k++ % _seeds.Length;
                if (block(_seeds[i]) != _expected[i]) Interlocked.Increment(ref Errors);
                Interlocked.Increment(ref Blocks);
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
    }
}

public static class BenchCsv
{
    public static string Line(BenchResult r) => string.Join(',',
        r.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.Label, r.CpuSingle, r.CpuMulti,
        r.MemoryGBs.ToString(CultureInfo.InvariantCulture), r.MemoryLatencyNs.ToString(CultureInfo.InvariantCulture));
}
