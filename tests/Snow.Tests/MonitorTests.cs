using Snow.Core.Monitor;

namespace Snow.Tests;

public static class MonitorTests
{
    private static List<double> Frames(int n, double ms, int spikes = 0, double spikeMs = 50)
    {
        var l = Enumerable.Repeat(ms, n).ToList();
        for (var i = 0; i < spikes; i++) l[(i * 97) % n] = spikeMs;
        return l;
    }

    [Test] public static void FrameStatsMath()
    {
        var s = FrameStats.From(Frames(1000, 10))!;
        Assert.Equal(100.0, s.AvgFps);
        Assert.Equal(100.0, s.Low1);
        Assert.Equal(0, s.Stutters);
        var withSpikes = FrameStats.From(Frames(1000, 10, spikes: 20))!;
        Assert.True(withSpikes.Low1 < 30, $"1% low deveria cair com 2% de engasgos: {withSpikes.Low1}");
        Assert.Equal(20, withSpikes.Stutters);
        Assert.True(FrameStats.From([16.6, 16.6]) is null, "poucos quadros");
    }

    [Test] public static void BottleneckClassification()
    {
        UsageSample S(double? gpu, double? core, double ram = 50, double? fps = 120, double? cap = null) => new(DateTime.Now, 40, core, gpu, 3, ram, fps, null, cap);
        Assert.Equal(Bottleneck.Gpu, BottleneckClassifier.Classify(S(99, 60)));
        Assert.Equal(Bottleneck.Cpu, BottleneckClassifier.Classify(S(60, 97)));
        Assert.Equal(Bottleneck.LimiteFps, BottleneckClassifier.Classify(S(55, 50, fps: 164, cap: 165)));
        // Fortnite real no Xeon E5-2620 v3 (11:02:20): 136 FPS com limite 165, GPU 70%, núcleo 74% → CPU, não "teto de FPS"
        Assert.Equal(Bottleneck.Cpu, BottleneckClassifier.Classify(S(69.6, 74.2, fps: 136, cap: 165)));
        Assert.Equal(Bottleneck.Cpu, BottleneckClassifier.Classify(S(55, 50)), "sem limite conhecido e GPU folgada");
        Assert.Equal(Bottleneck.Equilibrado, BottleneckClassifier.Classify(S(88, 60)));
        Assert.Equal(Bottleneck.Memoria, BottleneckClassifier.Classify(S(99, 99, ram: 95)));
        Assert.Equal(Bottleneck.Desconhecido, BottleneckClassifier.Classify(S(null, 50)), "sem contador de GPU (VM)");
        Assert.Equal(Bottleneck.SemCarga, BottleneckClassifier.Classify(S(10, 12, fps: null)), "PC parado não é 'equilibrado'");

        var session = SessionSummary.From([S(99, 60), S(98, 70), S(60, 95), S(97, 50)]);
        Assert.Equal(Bottleneck.Gpu, session.Main);
        Assert.Equal(75.0, session.TimeShare[Bottleneck.Gpu]);
        Assert.Equal(25.0, session.TimeShare[Bottleneck.Cpu]);
    }

    [Test] public static void ParsesPresentMonV2AndV1()
    {
        var v2 = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime\n" +
            string.Concat(Enumerable.Range(0, 200).Select(i =>
                $"FortniteClient-Win64-Shipping.exe,1234,0x1,DXGI,0,0,1,Hardware: Independent Flip,Application,{i * 6.06:0.000},6.060,{(i % 4 == 0 ? 5.9 : 2.1):0.0},0.1,0.5,{(i % 4 == 0 ? 3.0 : 5.8):0.0},{(i % 4 == 0 ? 3.0 : 5.8):0.0},0.2,8.1,6.06\n"));
        var c = PresentMonCapture.Parse(v2);
        Assert.Equal("FortniteClient-Win64-Shipping.exe", c.Application);
        Assert.Equal(200, c.FrameTimes.Count);
        Assert.True(c.Stats!.AvgFps is > 164 and < 166, $"fps {c.Stats.AvgFps}");
        var share = c.BoundShare!.Value;
        Assert.Equal(75.0, share.gpuBound);
        Assert.Equal(25.0, share.cpuBound);

        var v1 = "Application,ProcessID,MsBetweenPresents,MsBetweenDisplayChange\nLeague of Legends.exe,1,8.33,8.33\nLeague of Legends.exe,1,8.34,8.34\n";
        var c1 = PresentMonCapture.Parse(string.Concat(Enumerable.Repeat(v1, 1)) + string.Concat(Enumerable.Range(0, 20).Select(_ => "League of Legends.exe,1,8.33,8.33\n")));
        Assert.Equal(22, c1.FrameTimes.Count);
        Assert.True(c1.BoundShare is null, "v1 não tem CPUBusy/GPUBusy");
        Assert.Throws<FormatException>(() => PresentMonCapture.Parse("a,b\n1,2\n"));
    }

    [Test] public static void ComparesBeforeAndAfter()
    {
        var before = FrameStats.From(Frames(3000, 12.5, spikes: 30))!;
        var after = FrameStats.From(Frames(3000, 8.0, spikes: 5))!;
        var rows = SessionComparer.Compare(before, after);
        var fps = rows.First(r => r.Metric == "FPS médio");
        Assert.True(fps.Better && fps.Delta.StartsWith('+'), $"{fps.Before} → {fps.After} ({fps.Delta})");
        Assert.True(rows.First(r => r.Metric.StartsWith("Engasgos")).Better);
        Assert.True(rows.First(r => r.Metric == "Frametime mediano").Better);
    }
}
