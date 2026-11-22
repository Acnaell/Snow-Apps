using System.Diagnostics;
using System.Runtime.InteropServices;
using Snow.Core.Bench;
using Snow.Platform.Monitor;

namespace Snow.Platform.Bench;

/// <summary>Teste de pontuação (CPU, memória, discos) e de estresse (CPU + RAM com conferência, clock pelo PDH).</summary>
public static class BenchRunner
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SnowBench");

    /// <summary>IA local: mede banda da RAM e cálculo do processador (~8 s) e estima os modelos que cabem.</summary>
    public static AiReport Ai(string? gpuName, long vramBytes, long ramBytes, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var threads = Environment.ProcessorCount;
        progress?.Report("IA: banda da memória…");
        var bw = MemoryWork.Bandwidth(Math.Min(threads, 8));
        ct.ThrowIfCancellationRequested();
        progress?.Report("IA: cálculo de matrizes no processador…");
        var gflops = AiCompute.CpuGflops(threads, TimeSpan.FromSeconds(4), ct);
        GC.Collect();
        return AiBench.Analyze(gpuName, vramBytes / (double)(1L << 30), ramBytes / (double)(1L << 30), bw, gflops);
    }

    public static BenchResult Run(string cpuName, IEnumerable<string> drives, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var threads = Environment.ProcessorCount;
        progress?.Report("Processador: 1 núcleo…");
        var single = CpuWork.RunSingle(TimeSpan.FromSeconds(6), ct).score;
        progress?.Report($"Processador: {threads} threads…");
        var multi = CpuWork.RunMulti(threads, TimeSpan.FromSeconds(8), ct).score;
        ct.ThrowIfCancellationRequested();
        progress?.Report("Memória: banda…");
        var bw = MemoryWork.Bandwidth(Math.Min(threads, 8));
        progress?.Report("Memória: latência…");
        var lat = MemoryWork.LatencyNs();
        GC.Collect();
        var disks = new List<DiskBench>();
        foreach (var d in drives)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Disco {d}: leitura e escrita…");
            try { disks.Add(Disk(d, 512, ct)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { progress?.Report($"Disco {d}: {e.Message}"); }
        }
        var r = new BenchResult
        {
            Cpu = cpuName, CpuSingle = single, CpuMulti = multi, Threads = threads, MemoryGBs = bw, MemoryLatencyNs = lat, Disks = disks,
            Version = typeof(BenchRunner).Assembly.GetName().Version?.ToString(3),
        };
        Save(r);
        return r;
    }

    public static void Save(BenchResult r)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, $"bench-{r.When:yyyyMMdd-HHmmss}.json"), r.ToJson());
    }

    public static IReadOnlyList<BenchResult> History() =>
        !Directory.Exists(Dir) ? [] :
        Directory.EnumerateFiles(Dir, "bench-*.json").Select(f => BenchResult.FromJson(File.ReadAllText(f))).OfType<BenchResult>()
                 .OrderByDescending(r => r.When).ToList();

    // ───────── Disco: arquivo temporário sem cache do Windows (FILE_FLAG_NO_BUFFERING) ─────────
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static unsafe DiskBench Disk(string drive, int megabytes, CancellationToken ct = default)
    {
        var letter = drive.TrimEnd(':', '\\');
        var root = letter + ":\\";
        var sys = Path.GetPathRoot(Environment.SystemDirectory)!;
        var dir = root.Equals(sys, StringComparison.OrdinalIgnoreCase) ? Path.GetTempPath() : root;
        var file = Path.Combine(dir, $"snow-bench-{Environment.ProcessId}.tmp");
        const int block = 1 << 20;
        var buf = (byte*)NativeMemory.AlignedAlloc(block, 4096);
        try
        {
            new Random(7).NextBytes(new Span<byte>(buf, block)); // dados não compressíveis
            var span = new Span<byte>(buf, block);
            long total = (long)megabytes * block;
            double write, read, iops;
            using (var h = File.OpenHandle(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | NoBuffering, total))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < total; off += block) { ct.ThrowIfCancellationRequested(); RandomAccess.Write(h, span, off); }
                write = total / 1e6 / sw.Elapsed.TotalSeconds;

                sw.Restart();
                for (long off = 0; off < total; off += block) { ct.ThrowIfCancellationRequested(); RandomAccess.Read(h, span, off); }
                read = total / 1e6 / sw.Elapsed.TotalSeconds;

                // 4K aleatório, fila 1 (o que mais se parece com carregar arquivos pequenos de jogo)
                var small = new Span<byte>(buf, 4096);
                var rng = new Random(11);
                var blocks4k = total / 4096;
                long n = 0;
                sw.Restart();
                while (sw.Elapsed.TotalSeconds < 3) { RandomAccess.Read(h, small, rng.NextInt64(blocks4k) * 4096); n++; }
                iops = n / sw.Elapsed.TotalSeconds;
            }
            return new DiskBench(letter.ToUpperInvariant() + ":", Math.Round(read), Math.Round(write), Math.Round(iops));
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            try { File.Delete(file); } catch { }
        }
    }

    // ───────── Estresse ─────────
    public static StressReport Stress(TimeSpan duration, bool cpu, bool ram, Action<StressPoint>? onTick = null, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(duration);
        var token = linked.Token;
        var cpuStress = new CpuStress();
        MemoryWork.Tester? tester = null;
        if (ram)
        {
            var ms = new Native.NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.NativeMethods.MEMORYSTATUSEX>() };
            Native.NativeMethods.GlobalMemoryStatusEx(ref ms);
            // usa a memória livre menos 2 GB de folga, no máximo 75% do total
            var bytes = Math.Min((long)ms.ullAvailPhys - (2L << 30), (long)(ms.ullTotalPhys * 0.75));
            if (bytes > 256L << 20) tester = new MemoryWork.Tester(bytes);
        }
        var threads = Environment.ProcessorCount;
        var cpuThread = cpu ? new Thread(() => cpuStress.Run(threads, token)) { IsBackground = true } : null;
        var ramThread = tester is null ? null : new Thread(() =>
        {
            try { while (!token.IsCancellationRequested) tester.Pass(token); } catch (OperationCanceledException) { }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        cpuThread?.Start(); ramThread?.Start();

        var points = new List<StressPoint>();
        var sw = Stopwatch.StartNew();
        PdhSampler? pdh = null;
        try { pdh = new PdhSampler(); } catch { }
        while (!token.IsCancellationRequested)
        {
            token.WaitHandle.WaitOne(1000);
            var s = pdh?.Sample();
            var p = new StressPoint(sw.Elapsed, s?.CpuPercent, pdh?.LastPerformancePercent, Interlocked.Read(ref cpuStress.Errors), tester is null ? 0 : Interlocked.Read(ref tester.Errors));
            points.Add(p);
            onTick?.Invoke(p);
        }
        cpuThread?.Join(); ramThread?.Join();
        pdh?.Dispose();
        var report = StressReport.From(points, sw.Elapsed, cpuStress.Blocks, cpuStress.Errors, tester?.Errors ?? 0, tester?.Gigabytes ?? 0, tester?.Passes ?? 0, tester?.FirstError);
        tester?.Dispose();
        return report;
    }

}
