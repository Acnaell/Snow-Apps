using Snow.Core.Hardware;
using Snow.Core.Health;
using Snow.Core.Monitor;
using Snow.Core.Recommendation;

namespace Snow.Tests;

public static class UpgradeTests
{
    private static HardwareProfile Thiago() => Hw.Desktop(h => h with
    {
        Cpu = new CpuInfo { Name = "Intel(R) Xeon(R) CPU E5-2620 v3 @ 2.40GHz", Vendor = CpuVendor.Intel, PhysicalCores = 6, LogicalProcessors = 12, MaxClockMhz = 2400 },
        Gpus = [new GpuInfo { Name = "AMD Radeon RX 580 2048SP", Vendor = GpuVendor.Amd, IsDiscrete = true, VramBytes = 8L << 30, RefreshRateHz = 165 }],
        Memory = new MemoryInfo { TotalBytes = 16L << 30, AvailableBytes = 8L << 30, Type = "DDR4", ConfiguredSpeedMts = 1866, Channels = MemoryChannelMode.Single,
            Modules = [new MemoryModule(16L << 30, 2133, "DIMM_A1", "BANK 0", "Samsung", "M393A2G40DB0") { IsRegistered = true, IsEcc = true }] },
        Storage = new StorageInfo { SystemDrive = "C", Disks = [new DiskInfo(0, "KBG40ZNS256G NVMe KIOXIA 256GB", DiskKind.NvmeSsd, 256L << 30, ["C"]), new DiskInfo(1, "SSD", DiskKind.SataSsd, 512L << 30, ["D"])] },
    });

    private static UsageSample S(double gpu, double core, double fps) => new(DateTime.Now, 55, core, gpu, 1.7, 72, fps, null, 165);

    [Test] public static void XeonCpuBoundGetsMemoryAndCpuNotGpu()
    {
        // sessão real: GPU 60–80% com 120–150 FPS e limite 165
        var session = SessionSummary.From([S(70, 75, 136), S(65, 78, 128), S(80, 74, 150), S(96, 70, 120)]);
        var advice = UpgradeAdvisor.Advise(Thiago(), session,
            [new DiskHealthData { Name = "SSD", MediaType = 4, BusType = 11, ReadErrorsUncorrected = 203 }]);
        var text = string.Join("\n", advice.Select(a => $"{a.Priority} {a.Component}: {a.Title}"));
        Assert.Equal(UpgradePriority.Alta, advice[0].Priority, text);
        Assert.True(advice.Any(a => a.Component == "Armazenamento" && a.Title.Contains("(D:)")), text);
        var mem = advice.Single(a => a.Component == "Memória");
        Assert.Equal(UpgradePriority.Alta, mem.Priority);
        Assert.True(mem.Detail.Contains("RDIMM") && mem.Detail.Contains("ECC") && mem.Detail.Contains("16 GB") && mem.Detail.Contains("1866"), mem.Detail);
        Assert.True(advice.Any(a => a.Component == "Processador" && a.Detail.Contains("E5-2667 v4")), text);
        Assert.True(advice.Any(a => a.Priority == UpgradePriority.Evitar && a.Component == "Placa de vídeo"), "não trocar a RX 580 com o jogo preso no processador: " + text);
    }

    [Test] public static void GpuBoundGetsGpuWithCeiling()
    {
        var hw = Thiago() with { Cpu = new CpuInfo { Name = "AMD Ryzen 5 5600", Vendor = CpuVendor.Amd, PhysicalCores = 6, LogicalProcessors = 12 } };
        hw = hw with { Memory = hw.Memory with { Modules = [hw.Memory.Modules[0] with { IsRegistered = false, IsEcc = false }, hw.Memory.Modules[0] with { Slot = "B1", IsRegistered = false, IsEcc = false }] } };
        var session = SessionSummary.From([S(99, 50, 90), S(98, 55, 95), S(97, 52, 92)]);
        var advice = UpgradeAdvisor.Advise(hw, session);
        var gpu = advice.First(a => a.Component == "Placa de vídeo");
        Assert.Equal(UpgradePriority.Alta, gpu.Priority);
        Assert.True(gpu.Detail.Contains("RX 7700 XT"), gpu.Detail);
        Assert.True(advice.Any(a => a.Priority == UpgradePriority.Evitar && a.Component == "Processador"));
        Assert.False(advice.Any(a => a.Component == "Memória"), "dual channel completo");
    }

    [Test] public static void CpuClasses()
    {
        Assert.Equal(CpuClass.Fraco, UpgradeAdvisor.ClassifyCpu(new CpuInfo { Name = "Intel(R) Xeon(R) CPU E5-2620 v3 @ 2.40GHz", LogicalProcessors = 12 }));
        Assert.Equal(CpuClass.Fraco, UpgradeAdvisor.ClassifyCpu(new CpuInfo { Name = "Intel(R) Core(TM) i5-4590 CPU @ 3.30GHz", LogicalProcessors = 4 }));
        Assert.Equal(CpuClass.Medio, UpgradeAdvisor.ClassifyCpu(new CpuInfo { Name = "AMD Ryzen 5 5600 6-Core Processor", LogicalProcessors = 12 }));
        Assert.Equal(CpuClass.Forte, UpgradeAdvisor.ClassifyCpu(new CpuInfo { Name = "AMD Ryzen 7 7800X3D 8-Core Processor", LogicalProcessors = 16 }));
    }

    [Test] public static void DetectsTheRunningGame()
    {
        var frames = new Dictionary<string, int> { ["dwm.exe"] = 990, ["chrome.exe"] = 180, ["wallpaper64.exe"] = 300, ["FortniteClient-Win64-Shipping.exe"] = 420 };
        Assert.Equal("FortniteClient-Win64-Shipping.exe", ActiveGameDetector.Pick(frames, null));
        Assert.Equal("fortnite", ActiveGameDetector.KeyFor("FortniteClient-Win64-Shipping.exe"));
        // jogo desconhecido: vence quem tem mais quadros fora da lista de apps
        Assert.Equal("HollowKnight.exe", ActiveGameDetector.Pick(new Dictionary<string, int> { ["dwm.exe"] = 990, ["HollowKnight.exe"] = 170, ["Discord.exe"] = 400 }, null));
        Assert.Equal("hollowknight", ActiveGameDetector.KeyFor("HollowKnight.exe"));
        // só a área de trabalho: nenhum jogo
        Assert.True(ActiveGameDetector.Pick(new Dictionary<string, int> { ["dwm.exe"] = 990, ["explorer.exe"] = 60 }, null) is null);
        // não troca de jogo por oscilação pequena
        Assert.Equal("Game A.exe", ActiveGameDetector.Pick(new Dictionary<string, int> { ["Game A.exe"] = 150, ["Game B.exe"] = 200 }, "Game A.exe"));
        // menu do LoL aberto com o jogo em partida: o jogo vence
        Assert.Equal("League of Legends.exe", ActiveGameDetector.Pick(new Dictionary<string, int> { ["LeagueClientUxRender.exe"] = 180, ["League of Legends.exe"] = 400 }, null));
    }
}
