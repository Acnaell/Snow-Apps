using Snow.Core.Hardware;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;

namespace Snow.Tests;

public static class Hw
{
    public static GpuInfo Rtx4070 => new()
    {
        Name = "NVIDIA GeForce RTX 4070", Vendor = GpuVendor.Nvidia, IsDiscrete = true, VramBytes = 12L << 30,
        DriverVersion = "32.0.15.6094", WddmMajorFromDriver = 32, DriverVersionFriendly = "560.94",
    };

    public static HardwareProfile Desktop(Func<HardwareProfile, HardwareProfile>? tweak = null)
    {
        var hw = new HardwareProfile
        {
            Cpu = new CpuInfo { Name = "Intel(R) Core(TM) i7-13700K", Vendor = CpuVendor.Intel, PhysicalCores = 16, LogicalProcessors = 24, IsHybrid = true, PerformanceCores = 8, EfficiencyCores = 8, VirtualizationFirmwareEnabled = true },
            Gpus = [Rtx4070],
            Memory = new MemoryInfo { TotalBytes = 32L << 30, AvailableBytes = 20L << 30, Type = "DDR5", ConfiguredSpeedMts = 6000, RatedSpeedMts = 4800, Channels = MemoryChannelMode.Dual },
            Storage = new StorageInfo
            {
                SystemDrive = "C",
                Disks = [new DiskInfo(0, "Samsung 990 PRO", DiskKind.NvmeSsd, 2L << 40, ["C"]), new DiskInfo(1, "WD Blue", DiskKind.SataSsd, 1L << 40, ["D"])],
                GameLibraries = [@"D:\SteamLibrary\steamapps\common"],
            },
            Os = new OsInfo { Build = 26100, Ubr = 2314, DisplayVersion = "24H2", Edition = "Professional" },
            State = new SystemState { ActivePowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e" },
        };
        return tweak?.Invoke(hw) ?? hw;
    }

    public static HardwareProfile Laptop(bool modernStandby = false) => Desktop(h => h with
    {
        State = h.State with { IsLaptop = true, ModernStandby = modernStandby },
        Gpus = [new GpuInfo { Name = "NVIDIA GeForce RTX 4060 Laptop GPU", Vendor = GpuVendor.Nvidia, IsDiscrete = true, WddmMajorFromDriver = 32, DriverVersion = "32.0.15.6094" }],
    });

    public static HardwareProfile X3DDual => Desktop(h => h with
    {
        Cpu = new CpuInfo { Name = "AMD Ryzen 9 7950X3D 16-Core Processor", Vendor = CpuVendor.Amd, PhysicalCores = 16, LogicalProcessors = 32 },
    });

    public static HardwareProfile Win10Hdd => Desktop(h => h with
    {
        Os = new OsInfo { Build = 19045, DisplayVersion = "22H2" },
        Memory = h.Memory with { TotalBytes = 8L << 30, Channels = MemoryChannelMode.Single, Type = "DDR4", ConfiguredSpeedMts = 2133 },
        Storage = h.Storage with { Disks = [new DiskInfo(0, "ST1000DM010", DiskKind.Hdd, 1L << 40, ["C", "D"])] },
        Gpus = [new GpuInfo { Name = "AMD Radeon RX 580", Vendor = GpuVendor.Amd, IsDiscrete = true, WddmMajorFromDriver = 31, DriverVersion = "31.0.21921.1000" }],
    });

    public static HardwareProfile VirtualMachine => Desktop(h => h with
    {
        Gpus = [new GpuInfo { Name = "Microsoft Basic Display Adapter", Vendor = GpuVendor.Microsoft, IsVirtual = true }],
        Storage = h.Storage with { Disks = [new DiskInfo(0, "Microsoft Virtual Disk", DiskKind.Virtual, 1L << 40, ["C"])], GameLibraries = [] },
        Os = h.Os with { IsServer = true, Build = 20348, ProductName = "Windows Server 2022 Datacenter" },
    });

    public static IEnumerable<(string name, HardwareProfile hw)> All =>
    [("desktop", Desktop()), ("laptop", Laptop()), ("laptopMS", Laptop(true)), ("x3d", X3DDual), ("win10hdd", Win10Hdd), ("vm", VirtualMachine)];
}

public static class GpuTests
{
    [Test] public static void NvidiaSeries()
    {
        Assert.Equal(40, GpuGenerations.NvidiaSeries("NVIDIA GeForce RTX 4070 SUPER"));
        Assert.Equal(50, GpuGenerations.NvidiaSeries("NVIDIA GeForce RTX 5090"));
        Assert.Equal(16, GpuGenerations.NvidiaSeries("NVIDIA GeForce GTX 1660 Ti"));
        Assert.Equal(10, GpuGenerations.NvidiaSeries("NVIDIA GeForce GTX 1080"));
        Assert.Equal(7, GpuGenerations.NvidiaSeries("NVIDIA GeForce GTX 750 Ti"));
        Assert.Equal(9, GpuGenerations.NvidiaSeries("NVIDIA GeForce GTX 980"));
    }

    [Test] public static void HagsSupportMatrix()
    {
        GpuInfo G(string n, GpuVendor v) => new() { Name = n, Vendor = v, WddmMajorFromDriver = 31 };
        Assert.True(G("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia).SupportsHags, "RTX 3060");
        Assert.True(G("NVIDIA GeForce GTX 1060 6GB", GpuVendor.Nvidia).SupportsHags, "GTX 1060");
        Assert.False(G("NVIDIA GeForce GTX 970", GpuVendor.Nvidia).SupportsHags, "GTX 970 (Maxwell)");
        Assert.True(G("AMD Radeon RX 6700 XT", GpuVendor.Amd).SupportsHags, "RX 6700 XT");
        Assert.True(G("AMD Radeon RX 5700 XT", GpuVendor.Amd).SupportsHags, "RX 5700 XT");
        Assert.True(G("AMD Radeon RX 9070 XT", GpuVendor.Amd).SupportsHags, "RX 9070 XT");
        Assert.False(G("Radeon RX 580 Series", GpuVendor.Amd).SupportsHags, "RX 580 (Polaris)");
        Assert.False(G("AMD Radeon RX Vega 64", GpuVendor.Amd).SupportsHags, "Vega");
        Assert.True(G("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel).SupportsHags, "Arc A770");
        Assert.False(G("Intel(R) UHD Graphics 770", GpuVendor.Intel).SupportsHags, "UHD 770");
        Assert.False((G("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia) with { WddmMajorFromDriver = 26 }).SupportsHags, "driver WDDM 2.6");
    }

    [Test] public static void DiscreteAndVirtual()
    {
        Assert.True(GpuGenerations.IsDiscrete(GpuVendor.Amd, "AMD Radeon RX 7800 XT"));
        Assert.False(GpuGenerations.IsDiscrete(GpuVendor.Amd, "AMD Radeon(TM) Graphics"));
        Assert.False(GpuGenerations.IsDiscrete(GpuVendor.Intel, "Intel(R) Arc(TM) Graphics"));
        Assert.True(GpuGenerations.IsDiscrete(GpuVendor.Intel, "Intel(R) Arc(TM) B580 Graphics"));
        Assert.True(GpuGenerations.IsVirtualAdapter(GpuVendor.Microsoft, "Microsoft Basic Display Adapter"));
        Assert.Equal(GpuVendor.Nvidia, GpuGenerations.VendorFromPnp(@"PCI\VEN_10DE&DEV_2786&SUBSYS_", ""));
        Assert.Equal(GpuVendor.Amd, GpuGenerations.VendorFromPnp(@"PCI\VEN_1002&DEV_744C", ""));
    }

    [Test] public static void NvidiaDriverFriendly()
    {
        Assert.Equal("560.94", GpuGenerations.FriendlyNvidiaDriver("32.0.15.6094"));
        Assert.Equal("552.22", GpuGenerations.FriendlyNvidiaDriver("31.0.15.5222"));
        Assert.Equal("472.12", GpuGenerations.FriendlyNvidiaDriver("30.0.14.7212"));
        Assert.Equal(32, GpuGenerations.WddmMajor("32.0.15.6094"));
    }
}

public static class CatalogTests
{
    [Test] public static void IdsAreUniqueAndComplete()
    {
        var ids = TweakCatalog.Tweaks.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count(), "ids duplicados");
        foreach (var t in TweakCatalog.Tweaks)
        {
            Assert.True(t.Actions.Count > 0, $"{t.Id} sem ações");
            Assert.True(t.HonestNote.Length > 10, $"{t.Id} sem nota honesta");
        }
        foreach (var p in Enum.GetValues<OptimizationProfile>())
            Assert.True(TweakCatalog.Tweaks.Any(t => t.Profiles.Contains(p)), $"perfil {p} vazio");
    }

    [Test] public static void StaticInitOrderIsSafe()
    {
        var action = (StartupDisableAction)TweakCatalog.Get("startup.heavy").Actions[0];
        Assert.True(action.NamePatterns is { Count: > 5 }, "lista de launchers não inicializada");
    }

    [Test] public static void PowerSettingsComeAfterPowerPlans()
    {
        var list = TweakCatalog.Tweaks.ToList();
        var lastPlan = list.FindLastIndex(t => t.Actions.Any(a => a is PowerPlanAction));
        var firstSetting = list.FindIndex(t => t.Actions.Any(a => a is PowerSettingAction));
        Assert.True(lastPlan < firstSetting, "índices do plano devem ser aplicados depois da troca de plano");
    }
}

public static class RecommendationTests
{
    private static IEnumerable<string> Sel(HardwareProfile hw, OptimizationProfile p) =>
        RecommendationEngine.BuildPlan(hw, p).Selected.Select(i => i.Definition.Id);

    [Test] public static void ExactlyOnePowerPlanEverywhere()
    {
        foreach (var (name, hw) in Hw.All)
            foreach (var p in Enum.GetValues<OptimizationProfile>())
                Assert.Single(RecommendationEngine.BuildPlan(hw, p).Selected.Where(i => i.Definition.Group == TweakCatalog.Groups.PowerPlan).Select(i => i.Definition.Id), $"{name}/{p}");
    }

    [Test] public static void DesktopCompetitive()
    {
        var hw = Hw.Desktop();
        Assert.Equal(OptimizationProfile.Competitivo, RecommendationEngine.Recommend(hw).Profile);
        var s = Sel(hw, OptimizationProfile.Competitivo).ToHashSet();
        foreach (var id in new[] { "power.ultimate", "power.cpu.unpark", "gfx.hags", "gfx.windowed", "net.nagle", "services.sysmain", "gaming.dvr.off" })
            Assert.True(s.Contains(id), $"faltou {id}");
        Assert.False(s.Contains("privacy.bgapps"), "bgapps é opcional");
    }

    [Test] public static void LaptopAvoidsAggressivePower()
    {
        var hw = Hw.Laptop();
        Assert.Equal(OptimizationProfile.Desempenho, RecommendationEngine.Recommend(hw).Profile);
        var d = Sel(hw, OptimizationProfile.Desempenho).ToHashSet();
        Assert.True(d.Contains("power.overlay.best"), "notebook em Desempenho deve usar Modo de energia");
        var c = Sel(hw, OptimizationProfile.Competitivo).ToHashSet();
        Assert.True(c.Contains("power.high.laptop"));
        Assert.False(c.Contains("power.cpu.min100") || c.Contains("power.cpu.unpark") || c.Contains("power.ultimate"));
        Assert.True(Sel(Hw.Laptop(true), OptimizationProfile.Competitivo).Contains("power.overlay.best"), "Modern Standby");
    }

    [Test] public static void DualCcdX3DKeepsParkingAndGameBar()
    {
        var hw = Hw.X3DDual;
        Assert.True(hw.Cpu.IsDualCcdX3D);
        var s = Sel(hw, OptimizationProfile.Competitivo).ToHashSet();
        Assert.True(s.Contains("power.x3d.balanced"));
        Assert.False(s.Contains("power.ultimate") || s.Contains("power.cpu.unpark") || s.Contains("gaming.dvr.off"));
        Assert.False((hw with { Cpu = hw.Cpu with { Name = "AMD Ryzen 7 7800X3D 8-Core Processor", PhysicalCores = 8 } }).Cpu.IsDualCcdX3D, "7800X3D tem um CCD");
    }

    [Test] public static void HddAndLowRamProtectSysMain()
    {
        var hw = Hw.Win10Hdd;
        var plan = RecommendationEngine.BuildPlan(hw, OptimizationProfile.Competitivo);
        Assert.False(plan.Items.First(i => i.Definition.Id == "services.sysmain").Applicability.IsApplicable);
        Assert.False(plan.Items.First(i => i.Definition.Id == "gfx.windowed").Applicability.IsApplicable, "Win10 não tem otimização de janela");
        Assert.False(plan.Items.First(i => i.Definition.Id == "gfx.hags").Applicability.IsApplicable, "RX 580 sem HAGS");
        Assert.Equal(OptimizationProfile.Desempenho, RecommendationEngine.Recommend(hw).Profile);
    }

    [Test] public static void InsightsFlagRealBottlenecks()
    {
        var titles = Insights.Analyze(Hw.Win10Hdd).Select(i => i.Title).ToList();
        Assert.True(titles.Any(t => t.Contains("single channel")), "single channel");
        Assert.True(titles.Any(t => t.Contains("HDD")), "HDD");
        Assert.True(titles.Any(t => t.Contains("2133")), "XMP");
        Assert.True(titles.Any(t => t.Contains("Windows 10 fora")), "fim do suporte Win10");
        Assert.False(Insights.Analyze(Hw.Desktop()).Any(i => i.Level == InsightLevel.Critico), "desktop bom não tem críticos");
    }

    [Test] public static void VirtualMachineIsSafe()
    {
        var hw = Hw.VirtualMachine;
        var s = Sel(hw, OptimizationProfile.Competitivo).ToHashSet();
        Assert.False(s.Contains("gfx.hags"));
        Assert.False(s.Contains("search.games.noindex"));
        Assert.False(s.Contains("services.sysmain"), "disco virtual não é SSD confirmado");
    }

    [Test] public static void WithSelectionRespectsGroupsAndApplicability()
    {
        var plan = RecommendationEngine.BuildPlan(Hw.Desktop(), OptimizationProfile.Competitivo);
        var custom = plan.WithSelection(["power.ultimate", "power.high.desktop", "gfx.windowed", "power.x3d.balanced"]);
        Assert.Equal(2, custom.Selected.Count(), string.Join(",", custom.Selected.Select(s => s.Definition.Id)));
    }

    [Test] public static void ReportsRender()
    {
        foreach (var (_, hw) in Hw.All)
        {
            Assert.True(Report.ToText(hw).Contains("DIAGNÓSTICO"));
            foreach (var p in Enum.GetValues<OptimizationProfile>())
                Assert.True(Report.PlanToText(RecommendationEngine.BuildPlan(hw, p)).Length > 100);
        }
        Assert.True(Report.TryParseProfile("Competitivo", out var c) && c == OptimizationProfile.Competitivo);
        Assert.False(Report.TryParseProfile("turbo", out _));
    }
}
