using Snow.Core.Bench;

namespace Snow.Tests;

public static class BenchTests
{
    [Test] public static void CpuWorkIsDeterministicAndScored()
    {
        Assert.Equal(CpuWork.Block(42), CpuWork.Block(42));
        Assert.True(CpuWork.Block(42) != CpuWork.Block(43));
        var (single, n) = CpuWork.RunSingle(TimeSpan.FromMilliseconds(400));
        Assert.True(n > 0 && single > 0, $"single {single}");
        var (multi, _) = CpuWork.RunMulti(Math.Max(2, Environment.ProcessorCount), TimeSpan.FromMilliseconds(400));
        Assert.True(multi > single * 0.9, $"multi {multi} vs single {single}");
    }

    [Test] public static void CpuStressDetectsWrongResults()
    {
        var ok = new CpuStress();
        using (var cts = new CancellationTokenSource(300)) ok.Run(2, cts.Token);
        Assert.True(ok.Blocks > 0 && ok.Errors == 0);
        // processador "instável": 1 em cada 50 blocos sai errado
        var bad = new CpuStress();
        long calls = 0;
        using (var cts = new CancellationTokenSource(300))
            bad.Run(2, cts.Token, seed => Interlocked.Increment(ref calls) % 50 == 0 ? CpuWork.Block(seed) ^ 1 : CpuWork.Block(seed));
        Assert.True(bad.Errors > 0, "erro de cálculo deveria ser contado");
    }

    [Test] public static void RamTesterFindsAFlippedBit()
    {
        using var t = new MemoryWork.Tester(128L << 20);
        Assert.True(t.Gigabytes >= 0.12);
        t.Pass(CancellationToken.None);
        Assert.Equal(0L, t.Errors);
        // passada 1 escreve AAAA…; trocamos um bit entre a escrita e a conferência simulando o pente com defeito
        var t2 = new MemoryWork.Tester(64L << 20);
        t2.Pass(CancellationToken.None);
        t2.CorruptForTest();
        Assert.Equal(0L, t2.Errors, "corrupção depois da conferência não conta");
        t2.Pass(CancellationToken.None, t2.CorruptForTest); // bit trocado entre escrever e conferir = pente com defeito
        Assert.Equal(1L, t2.Errors);
        Assert.True(t2.FirstError!.Contains("bloco 0, posição 123"), t2.FirstError);
        Assert.True(MemoryWork.LatencyNs(16, 200_000) > 0);
        Assert.True(MemoryWork.Bandwidth(2, 32, TimeSpan.FromMilliseconds(200)) > 0);
    }

    [Test] public static void StressVerdicts()
    {
        var pts = Enumerable.Range(1, 300).Select(i => new StressPoint(TimeSpan.FromSeconds(i), 100, i < 120 ? 125 : 100, 0, 0)).ToList();
        var r = StressReport.From(pts, TimeSpan.FromSeconds(300), 1000, 0, 0, 8, 3, null);
        Assert.Equal(20.0, r.ThrottlePercent);
        Assert.True(r.Verdicts().Any(v => !v.ok && v.title.Contains("Clock caiu 20%")));
        Assert.True(r.Verdicts().Any(v => v.ok && v.title == "Processador estável"));
        var bad = StressReport.From(pts, TimeSpan.FromSeconds(300), 1000, 3, 2, 8, 3, "bloco 0");
        Assert.True(bad.Verdicts().Count(v => !v.ok) == 3);
        Assert.Equal(25.0, BenchVerdicts.Delta(100, 125));
        Assert.Equal(10.0, BenchVerdicts.Delta(100, 90, lowerIsBetter: true));
        Assert.Equal("SSD SATA", BenchVerdicts.Disk(new DiskBench("D:", 520, 480, 9000)));
    }
}
