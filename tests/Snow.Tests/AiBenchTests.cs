using Snow.Core.Bench;

namespace Snow.Tests;

public static class AiBenchTests
{
    [Test] public static void ThiagoRx580()
    {
        var r = AiBench.Analyze("AMD Radeon RX 580 2048SP", 8, 16, 13, 150);
        var m8 = r.Models.Single(m => m.Model.Billions == 8);
        Assert.Equal(AiWhere.PlacaDeVideo, m8.Where);
        Assert.True(m8.TokensPerSecond is > 15 and < 30, $"8B ≈ 20 tok/s, deu {m8.TokensPerSecond}");
        var m12 = r.Models.Single(m => m.Model.Billions == 12);
        Assert.Equal(AiWhere.Dividido, m12.Where, "12B (7,3 GB) não cabe em 8 GB com o contexto");
        Assert.True(m12.TokensPerSecond < m8.TokensPerSecond);
        Assert.Equal(AiWhere.NaoCabe, r.Models.Single(m => m.Model.Billions == 32).Where, "32B não cabe em 8 + 16 GB");
        Assert.True(r.Summary.Contains("8B"), r.Summary);
        Assert.True(r.Notes.Any(n => n.Contains("Vulkan") && n.Contains("ROCm")), "Polaris: sem ROCm");
        Assert.True(r.Notes.Any(n => n.Contains("13 GB/s")), "avisa RAM em canal único");
    }

    [Test] public static void BigGpuAndNoGpu()
    {
        var big = AiBench.Analyze("NVIDIA GeForce RTX 4090", 24, 64, 60, 900);
        Assert.Equal(AiWhere.PlacaDeVideo, big.Models.Single(m => m.Model.Billions == 32).Where);
        Assert.True(big.Models.Single(m => m.Model.Billions == 32).TokensPerSecond > 25);
        var none = AiBench.Analyze(null, 0, 8, 20, 80);
        Assert.Equal(AiWhere.Processador, none.Models[0].Where);
        Assert.Equal(AiWhere.NaoCabe, none.Models.Single(m => m.Model.Billions == 8).Where, "8B + 4 GB do Windows não cabe em 8 GB");
        // Placa fora da tabela usa o índice de jogos
        Assert.True(AiBench.Gpu("NVIDIA GeForce GTX 1060 6GB")!.BandwidthGBs == 192);
    }

    [Test] public static void MatMulIsCorrectAndFast()
    {
        const int n = 37; // não múltiplo do vetor: testa a sobra
        var rnd = new Random(1);
        var a = Enumerable.Range(0, n * n).Select(_ => (float)rnd.NextDouble()).ToArray();
        var b = Enumerable.Range(0, n * n).Select(_ => (float)rnd.NextDouble()).ToArray();
        var c = new float[n * n];
        AiCompute.MatMul(a, b, c, n);
        for (var i = 0; i < n; i += 7)
            for (var j = 0; j < n; j += 5)
            {
                double s = 0; for (var k = 0; k < n; k++) s += a[i * n + k] * b[k * n + j];
                Assert.True(Math.Abs(s - c[i * n + j]) < 1e-3, $"c[{i},{j}]");
            }
        Assert.True(AiCompute.CpuGflops(2, TimeSpan.FromMilliseconds(300)) > 0.5, "GFLOPS");
    }
}
