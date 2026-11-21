using Snow.Core.Compat;
using Snow.Core.Hardware;

namespace Snow.Tests;

public static class CanRunTests
{
    private static HardwareProfile Pc(string cpu, int threads, int baseMhz, string gpu, long vramGb, double ramGb, bool discrete = true) => Hw.Desktop(h => h with
    {
        Cpu = new CpuInfo { Name = cpu, LogicalProcessors = threads, PhysicalCores = threads / 2, MaxClockMhz = baseMhz },
        Gpus = [new GpuInfo { Name = gpu, VramBytes = vramGb << 30, IsDiscrete = discrete, Vendor = GpuVendor.Amd }],
        Memory = new MemoryInfo { TotalBytes = (long)(ramGb * (1L << 30)), AvailableBytes = 4L << 30 },
        Storage = new StorageInfo { Disks = [new DiskInfo(0, "SSD", DiskKind.NvmeSsd, 256L << 30, ["C"])] },
    });

    private static HardwareProfile Thiago => Pc("Intel(R) Xeon(R) CPU E5-2620 v3 @ 2.40GHz", 12, 2400, "AMD Radeon RX 580 2048SP", 8, 15.9);

    [Test] public static void EveryRequirementIsRecognized()
    {
        // todo modelo citado num requisito precisa estar na tabela, senão a comparação fica furada sem ninguém perceber
        foreach (var g in CanRun.Catalog)
            foreach (var t in g.Tiers)
            {
                foreach (var c in t.Cpus) Assert.True(HardwareIndex.CpuByName(c) is not null, $"{g.Name}/{t.Name}: CPU '{c}' sem índice");
                foreach (var v in t.Gpus) Assert.True(HardwareIndex.Gpu(v) is not null, $"{g.Name}/{t.Name}: GPU '{v}' sem índice");
                Assert.True(t.CpuIndex is not null, $"{g.Name}/{t.Name}: sem índice de CPU");
            }
    }

    [Test] public static void IndexesMakeSense()
    {
        Assert.Equal(74, HardwareIndex.Cpu(Thiago.Cpu));
        Assert.Equal(88, HardwareIndex.Gpu("AMD Radeon RX 580 2048SP"));
        Assert.Equal(100, HardwareIndex.Gpu("Radeon RX 580 Series"));
        Assert.Equal(93, HardwareIndex.Gpu("NVIDIA GeForce GTX 1060 3GB"));
        Assert.Equal(360, HardwareIndex.Gpu("NVIDIA GeForce RTX 4070 Ti SUPER"));
        Assert.Equal(6, HardwareIndex.Gpu("Intel HD 4000"));
        Assert.Equal(11, HardwareIndex.Gpu("Intel(R) UHD Graphics 630"));
        // estimativa por arquitetura para um modelo fora da tabela
        var est = HardwareIndex.Cpu(new CpuInfo { Name = "Intel(R) Core(TM) i5-10600K CPU @ 4.10GHz", LogicalProcessors = 12, MaxClockMhz = 4100 });
        Assert.True(est is > 130 and < 160, $"i5-10600K estimado em {est}");
        Assert.True(HardwareIndex.Cpu(new CpuInfo { Name = "AMD Ryzen 7 7700X 8-Core Processor", LogicalProcessors = 16, MaxClockMhz = 4500 }) > 170);
    }

    [Test] public static void ThiagosPcVerdicts()
    {
        var r = CanRun.EvaluateAll(Thiago).ToDictionary(x => x.Game.Id);
        Assert.Equal(CanRunLevel.Recomendado, r["fortnite"].Level, r["fortnite"].Detail);
        Assert.True(r["fortnite"].Missing.Any(m => m.StartsWith("processador")), "Épico: falta processador");
        Assert.Equal(CanRunLevel.Recomendado, r["lol"].Level);
        Assert.Equal(CanRunLevel.Recomendado, r["valorant"].Level, "144 FPS pede i5-9400F/R5 2600X");
        Assert.Equal(CanRunLevel.Abaixo, r["marvelrivals"].Level, r["marvelrivals"].Detail);
        Assert.True(r["marvelrivals"].Missing.Single().StartsWith("processador"), "só o processador fica abaixo");
        // E5-2620 v3 a ~2,9 GHz efetivos fica abaixo de um Ryzen 5 1600 (mínimo do Cyberpunk 2.0)
        Assert.Equal(CanRunLevel.Abaixo, r["cyberpunk"].Level, r["cyberpunk"].Detail);
        Assert.True(r["cyberpunk"].Missing.Single().StartsWith("processador"));
        Assert.Equal(CanRunLevel.Recomendado, r["cs2"].Level, "passa o mínimo com folga");
    }

    [Test] public static void StrongAndWeakPcs()
    {
        var strong = Pc("AMD Ryzen 7 7800X3D 8-Core Processor", 16, 4200, "NVIDIA GeForce RTX 4070 SUPER", 12, 32);
        Assert.True(CanRun.EvaluateAll(strong).All(x => x.Level >= CanRunLevel.Recomendado), string.Join(" | ", CanRun.EvaluateAll(strong).Where(x => x.Level < CanRunLevel.Recomendado).Select(x => x.Game.Name + ": " + x.Detail)));
        Assert.Equal(CanRunLevel.Alto, CanRun.Evaluate(CanRun.Catalog.First(g => g.Id == "fortnite"), strong).Level);
        var weak = Pc("Intel(R) Core(TM) i3-7100U CPU @ 2.40GHz", 4, 2400, "Intel(R) HD Graphics 620", 0, 8, discrete: false);
        var w = CanRun.EvaluateAll(weak).ToDictionary(x => x.Game.Id);
        Assert.True(w["lol"].Level >= CanRunLevel.Minimo);
        Assert.Equal(CanRunLevel.Abaixo, w["cyberpunk"].Level);
    }
}
