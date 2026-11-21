using Snow.Core.Hardware;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;
using Snow.Platform;
using Snow.Platform.Executors;
using Snow.Platform.Profiling;
using Snow.Platform.Services;

namespace Snow.Tests;

public static class ExecutorTests
{
    [Test] public static void RegistryRoundTrip()
    {
        var reg = new FakeRegistry();
        var ex = new RegistryExecutor(reg);
        reg.Set(RegistryHive.LocalMachine, "A", "existing", new RegValue(RegistryKind.DWord, "20"));

        var a1 = new RegistryValueAction(RegistryHive.LocalMachine, "A", "existing", RegistryKind.DWord, 10);
        var a2 = new RegistryValueAction(RegistryHive.LocalMachine, "A", "absent", RegistryKind.DWord, unchecked((int)0xFFFFFFFF));
        var before = reg.Dump();
        var s1 = ex.Capture(a1); ex.Apply(a1);
        var s2 = ex.Capture(a2); ex.Apply(a2);
        Assert.Equal(true, ex.Verify(a1));
        Assert.Equal("4294967295", reg.Get(RegistryHive.LocalMachine, "A", "absent")!.Data, "0xFFFFFFFF como DWORD sem sinal");
        ex.Revert(s2); ex.Revert(s1);
        Assert.Equal(before, reg.Dump());
    }

    [Test] public static void ServiceRoundTrip()
    {
        var svc = new FakeServices();
        svc.S["DiagTrack"] = (ServiceStart.Automatic, true);
        var ex = new ServiceExecutor(svc);
        var a = new ServiceStartAction("DiagTrack", ServiceStart.Disabled, true);
        var snap = ex.Capture(a);
        ex.Apply(a);
        Assert.Equal(ServiceStart.Disabled, svc.GetStart("DiagTrack"));
        Assert.False(svc.IsRunning("DiagTrack"), "deveria parar");
        ex.Revert(snap);
        Assert.Equal((ServiceStart.Automatic, true), svc.S["DiagTrack"]);
        Assert.Throws<SkipException>(() => ex.Apply(new ServiceStartAction("NaoExiste", ServiceStart.Disabled, false)));
    }

    [Test] public static void UltimatePlanCreatedOnceAndReverted()
    {
        var power = new FakePower();
        var state = new MemState();
        var ex = new PowerPlanExecutor(power, state);
        var a = new PowerPlanAction(PowerPlan.UltimatePerformance);
        var snap = ex.Capture(a);
        ex.Apply(a);
        Assert.Equal(true, ex.Verify(a));
        Assert.True(power.Active != PowerGuids.Balanced);
        ex.Revert(snap);
        Assert.Equal(PowerGuids.Balanced, power.Active);
        ex.Apply(a); ex.Apply(a);
        Assert.Equal(1, power.Duplications, "o plano Ultimate não deve ser duplicado a cada execução");
    }

    [Test] public static void UltimateNotAllowedIsSkip()
    {
        var power = new FakePower { AllowUltimate = false };
        Assert.Throws<SkipException>(() => new PowerPlanExecutor(power, new MemState()).Apply(new PowerPlanAction(PowerPlan.UltimatePerformance)));
    }

    [Test] public static void PowerSettingRoundTrip()
    {
        var power = new FakePower();
        power.Ac[(PowerGuids.Balanced, TweakCatalog.ProcThrottleMin)] = 5;
        var ex = new PowerSettingExecutor(power);
        var a = new PowerSettingAction(TweakCatalog.SubProcessor, TweakCatalog.ProcThrottleMin, 100, "min");
        var snap = ex.Capture(a);
        ex.Apply(a);
        Assert.Equal(true, ex.Verify(a));
        ex.Revert(snap);
        Assert.Equal(5u, power.Ac[(PowerGuids.Balanced, TweakCatalog.ProcThrottleMin)]);
        Assert.Throws<SkipException>(() => ex.Apply(new PowerSettingAction(TweakCatalog.SubProcessor, TweakCatalog.CpMinCores, 100, "x")));
    }

    [Test] public static void NagleOnlyActiveInterfaces()
    {
        var reg = new FakeRegistry();
        var root = NagleExecutor.InterfacesKey;
        reg.Set(RegistryHive.LocalMachine, $@"{root}\{{AAA}}", "DhcpIPAddress", new RegValue(RegistryKind.String, "192.168.0.10"));
        reg.Set(RegistryHive.LocalMachine, $@"{root}\{{BBB}}", "DhcpIPAddress", new RegValue(RegistryKind.String, "0.0.0.0"));
        reg.Set(RegistryHive.LocalMachine, $@"{root}\{{CCC}}", "IPAddress", new RegValue(RegistryKind.MultiString, "10.0.0.5"));
        reg.Set(RegistryHive.LocalMachine, $@"{root}\{{CCC}}", "TcpAckFrequency", new RegValue(RegistryKind.DWord, "2"));
        var before = reg.Dump();
        var ex = new NagleExecutor(reg);
        Assert.Equal(2, ex.ActiveInterfaces().Count);
        var a = new NagleAction(true);
        var snap = ex.Capture(a);
        ex.Apply(a);
        Assert.Equal(true, ex.Verify(a));
        Assert.True(reg.Get(RegistryHive.LocalMachine, $@"{root}\{{BBB}}", "TCPNoDelay") is null, "interface sem IP intocada");
        ex.Revert(snap);
        Assert.Equal(before, reg.Dump());
    }

    [Test] public static void DirectXMergePreservesOtherKeys()
    {
        Assert.Equal("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", DirectXGlobalExecutor.Merge("VRROptimizeEnable=0;", "SwapEffectUpgradeEnable", "1"));
        Assert.Equal("SwapEffectUpgradeEnable=1;AutoHDREnable=1;", DirectXGlobalExecutor.Merge("SwapEffectUpgradeEnable=0;AutoHDREnable=1;", "SwapEffectUpgradeEnable", "1"));

        var reg = new FakeRegistry();
        reg.Set(RegistryHive.CurrentUser, DirectXGlobalExecutor.Path, DirectXGlobalExecutor.ValueName, new RegValue(RegistryKind.String, "AutoHDREnable=1;"));
        var before = reg.Dump();
        var ex = new DirectXGlobalExecutor(reg);
        var a = new DirectXGlobalSettingAction("SwapEffectUpgradeEnable", "1");
        var snap = ex.Capture(a); ex.Apply(a);
        Assert.Equal(true, ex.Verify(a));
        ex.Revert(snap);
        Assert.Equal(before, reg.Dump());
    }

    [Test] public static void StartupDisableLikeTaskManager()
    {
        var reg = new FakeRegistry();
        reg.Set(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Discord", new RegValue(RegistryKind.String, @"C:\Users\x\AppData\Local\Discord\Update.exe --processStart Discord.exe"));
        reg.Set(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "SecurityHealth", new RegValue(RegistryKind.String, "SecurityHealthSystray.exe"));
        reg.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Steam", new RegValue(RegistryKind.String, @"""C:\Program Files (x86)\Steam\steam.exe"" -silent"));
        var before = reg.Dump();
        var mgr = new StartupManager(reg, () => []);
        var ex = new StartupExecutor(mgr);
        var a = new StartupDisableAction(TweakCatalog.KnownHeavyStartup);
        var snap = ex.Capture(a);
        var outcome = ex.Apply(a);
        Assert.True(outcome.Message.Contains("Discord") && outcome.Message.Contains("Steam"), outcome.Message);
        var items = mgr.List();
        Assert.False(items.First(i => i.Name == "Discord").Enabled);
        Assert.False(items.First(i => i.Name == "Steam").Enabled);
        Assert.True(items.First(i => i.Name == "SecurityHealth").Enabled, "itens do sistema nunca são tocados");
        Assert.True(reg.Get(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Discord") is not null, "a entrada Run não é apagada");
        ex.Revert(snap);
        Assert.Equal(before, reg.Dump());
        Assert.Throws<SkipException>(() => ex.Apply(new StartupDisableAction(["NadaInstalado"])));
    }

    [Test] public static void IndexingAttributeRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return; // atributo NotContentIndexed só existe no NTFS/Windows
        var dir = Directory.CreateTempSubdirectory("snowidx").FullName;
        File.WriteAllText(Path.Combine(dir, "game.pak"), "x");
        var ex = new IndexingExecutor(() => [dir]);
        var snap = ex.Capture(new IndexingExclusionAction());
        ex.Apply(new IndexingExclusionAction());
        Assert.Equal(true, ex.Verify(new IndexingExclusionAction()));
        ex.Revert(snap);
        Assert.False(File.GetAttributes(Path.Combine(dir, "game.pak")).HasFlag(FileAttributes.NotContentIndexed));
        Directory.Delete(dir, true);
    }

    [Test] public static void CleanupKeepsRecentFiles()
    {
        var dir = Directory.CreateTempSubdirectory("snowclean").FullName;
        var old = Path.Combine(dir, "sub", "old.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, new string('x', 1000));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        var recent = Path.Combine(dir, "recent.tmp");
        File.WriteAllText(recent, "y");
        var (files, bytes) = CleanupExecutor.DeleteOlderThan(dir, TimeSpan.FromDays(1));
        Assert.Equal(1L, files);
        Assert.Equal(1000L, bytes);
        Assert.True(File.Exists(recent) && !Directory.Exists(Path.Combine(dir, "sub")));
        Directory.Delete(dir, true);
    }

    [Test] public static void VdfParsing()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam" "label" "" }
                "1" { "path"  "D:\\SteamLibrary" "apps" { "730" "123" } }
            }
            """;
        var paths = GameLibraryFinder.ParseVdfPaths(vdf).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Equal(@"D:\SteamLibrary", paths[1]);
    }
}

public static class ServiceTests
{
    private static (OptimizationService svc, FakeRegistry reg, FakeServices svcs, FakePower power, string dir) Make()
    {
        var reg = new FakeRegistry();
        var svcs = new FakeServices();
        svcs.S["DiagTrack"] = (ServiceStart.Automatic, true);
        svcs.S["SysMain"] = (ServiceStart.Automatic, true);
        var power = new FakePower();
        power.Ac[(PowerGuids.Balanced, TweakCatalog.ProcThrottleMin)] = 5;
        power.Ac[(PowerGuids.Balanced, TweakCatalog.CpMinCores)] = 10;
        reg.Set(RegistryHive.LocalMachine, $@"{NagleExecutor.InterfacesKey}\{{NIC}}", "DhcpIPAddress", new RegValue(RegistryKind.String, "192.168.1.2"));
        var set = new ExecutorSet(
        [
            new RegistryExecutor(reg), new ServiceExecutor(svcs), new PowerPlanExecutor(power, new MemState()),
            new PowerSettingExecutor(power), new PowerOverlayExecutor(power), new NagleExecutor(reg),
            new DirectXGlobalExecutor(reg), new StartupExecutor(new StartupManager(reg, () => [])),
            new IndexingExecutor(() => []), new NoopCleanup(),
        ]);
        var dir = Directory.CreateTempSubdirectory("snowbk").FullName;
        return (new OptimizationService(set, new BackupStore(dir), new ListLog()), reg, svcs, power, dir);
    }

    private sealed class NoopCleanup : IActionExecutor
    {
        public string Name => "cleanup";
        public bool CanHandle(TweakAction a) => a is CleanupAction;
        public ActionSnapshot Capture(TweakAction a) => new(Name, "", new());
        public ActionOutcome Apply(TweakAction a) => new(false, "ok");
        public bool? Verify(TweakAction a) => null;
        public void Revert(ActionSnapshot s) { }
    }

    [Test] public static void FullProfileApplyAndRestoreIsLossless()
    {
        foreach (var profile in Enum.GetValues<OptimizationProfile>())
        {
            var (svc, reg, svcs, power, dir) = Make();
            var before = (reg.Dump(), string.Join(";", svcs.S.OrderBy(k => k.Key)), power.Active, string.Join(";", power.Ac.Where(k => k.Key.Item1 == PowerGuids.Balanced).OrderBy(k => k.Key.Item2)));
            var plan = RecommendationEngine.BuildPlan(Hw.Desktop(), profile);
            var session = svc.Apply(plan, new ApplyOptions(CreateRestorePoint: false));
            Assert.False(session.Tweaks.Any(t => t.Status == TweakStatus.Failed), $"{profile}: " + string.Join("\n", session.Tweaks.SelectMany(t => t.Messages)));
            Assert.True(reg.Dump() != before.Item1, $"{profile}: nada mudou no registro?");

            // backup persistido e recarregável do disco (simula fechar e reabrir o app)
            var reloaded = new BackupStore(dir).Get(session.Id)!;
            svc.Restore(reloaded);
            var after = (reg.Dump(), string.Join(";", svcs.S.OrderBy(k => k.Key)), power.Active, string.Join(";", power.Ac.Where(k => k.Key.Item1 == PowerGuids.Balanced).OrderBy(k => k.Key.Item2)));
            Assert.Equal(before, after, $"{profile}: estado não restaurado");
            Assert.True(new BackupStore(dir).Get(session.Id)!.RestoredUtc is not null);
            Directory.Delete(dir, true);
        }
    }

    [Test] public static void FailingActionRollsBackItsTweak()
    {
        var (svc, reg, _, _, dir) = Make();
        var before = reg.Dump();
        var bad = new TweakDefinition
        {
            Id = "test.bad", Title = "t", Description = "d", HonestNote = "nota de teste", Category = TweakCategory.Graficos,
            Actions =
            [
                new RegistryValueAction(RegistryHive.CurrentUser, "X", "ok", RegistryKind.DWord, 1),
                new ServiceStartAction("DiagTrack", (ServiceStart)42, false), // valor inválido → exceção
            ],
            Profiles = new HashSet<OptimizationProfile> { OptimizationProfile.Equilibrado },
        };
        var plan = RecommendationEngine.BuildPlan(Hw.Desktop(), OptimizationProfile.Equilibrado, [bad]);
        var session = svc.Apply(plan, new ApplyOptions(false));
        Assert.Equal(TweakStatus.Failed, session.Tweaks[0].Status);
        Assert.Equal(before, reg.Dump(), "a primeira ação deveria ter sido desfeita");
        Directory.Delete(dir, true);
    }

    [Test] public static void LastTweakIsPersistedAndInterruptedTweakIsReverted()
    {
        var reg = new FakeRegistry();
        var dir = Directory.CreateTempSubdirectory("snowlast").FullName;
        var svc = new OptimizationService(new ExecutorSet([new RegistryExecutor(reg)]), new BackupStore(dir), new ListLog());
        var only = new TweakDefinition
        {
            Id = "t.only", Title = "t", Description = "d", HonestNote = "nota de teste", Category = TweakCategory.Graficos,
            Actions = [new RegistryValueAction(RegistryHive.CurrentUser, "K", "v", RegistryKind.DWord, 1)],
            Profiles = new HashSet<OptimizationProfile> { OptimizationProfile.Equilibrado },
        };
        var session = svc.Apply(RecommendationEngine.BuildPlan(Hw.Desktop(), OptimizationProfile.Equilibrado, [only]), new ApplyOptions(false));
        var fromDisk = new BackupStore(dir).Get(session.Id)!;
        Assert.Equal(TweakStatus.Applied, Assert.Single(fromDisk.Tweaks, "tweaks no disco").Status, "o último ajuste precisa estar gravado");
        svc.Restore(fromDisk);
        Assert.True(reg.Get(RegistryHive.CurrentUser, "K", "v") is null, "restaurado a partir do disco");

        // queda de energia no meio do ajuste: status Pending com snapshot gravado
        reg.Set(RegistryHive.CurrentUser, "K", "v", new RegValue(RegistryKind.DWord, "1"));
        var crashed = new BackupSession { Id = "crash", Tweaks = [new TweakBackup { TweakId = "x", Status = TweakStatus.Pending,
            Snapshots = [new ActionSnapshot("registry", "d", new() { ["hive"] = "CurrentUser", ["path"] = "K", ["name"] = "v", ["old"] = null })] }] };
        Assert.True(crashed.HasRevertibleChanges);
        svc.Restore(crashed);
        Assert.True(reg.Get(RegistryHive.CurrentUser, "K", "v") is null, "ajuste interrompido também é revertido");
        Directory.Delete(dir, true);
    }

    [Test] public static void SelfTestPassesWithFakes()
    {
        var (svc, _, _, _, dir) = Make();
        var r = svc.SelfTest(RecommendationEngine.BuildPlan(Hw.Desktop(), OptimizationProfile.Competitivo));
        Assert.True(r.Success, string.Join("\n", r.Lines));
        Directory.Delete(dir, true);
    }
}

public static class ProfilerTests
{
    // Formato real do Windows PowerShell 5.1, incluindo o embrulho {value,Count} que ele às vezes gera.
    private const string Json = """
    {"cpu":[{"Name":"Intel(R) Core(TM) i5-12400F","Manufacturer":"GenuineIntel","Cores":6,"Threads":12,"MaxClock":2500,"Virt":true}],
     "gpu":{"value":[{"Name":"NVIDIA GeForce RTX 3060","Pnp":"PCI\\VEN_10DE&DEV_2504&SUBSYS_397D1462&REV_A1\\4&1","Driver":"32.0.15.6094","AdapterRam":4293918720,"Compat":"NVIDIA"}],"Count":1},
     "mem":[{"Capacity":8589934592,"Speed":3200,"Configured":3200,"Locator":"DIMM 0","Bank":"P0 CHANNEL A","Maker":"Kingston","Part":"KF432C16","SmbiosType":26},
            {"Capacity":8589934592,"Speed":3200,"Configured":3200,"Locator":"DIMM 0","Bank":"P0 CHANNEL B","Maker":"Kingston","Part":"KF432C16","SmbiosType":26}],
     "cs":{"PcType":1,"Hypervisor":false,"Model":"MSI MS-7D42"},
     "chassis":[3],"battery":0,
     "disks":[{"Id":"0","Name":"KINGSTON SNV2S1000G","Media":4,"Bus":17,"Size":1000204886016},{"Id":"1","Name":"ST2000DM008","Media":3,"Bus":11,"Size":2000398934016}],
     "parts":[{"Letter":"C","Disk":0},{"Letter":"E","Disk":1}],
     "dg":{"Vbs":2,"Running":[2]}}
    """;

    private static NativeInfo Native => new(6, 0, true, true, 16UL << 30, 8UL << 30, new PowerCapabilities(false, false, false), true, PowerGuids.HighPerformance, "Alto desempenho", "x64", "C");

    [Test] public static void ParsesRealisticCimOutput()
    {
        var reg = new FakeRegistry();
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000";
        reg.Set(RegistryHive.LocalMachine, cls, "MatchingDeviceId", new RegValue(RegistryKind.String, @"pci\ven_10de&dev_2504"));
        reg.Set(RegistryHive.LocalMachine, cls, "HardwareInformation.qwMemorySize", new RegValue(RegistryKind.QWord, (12UL << 30).ToString()));
        reg.Set(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", new RegValue(RegistryKind.String, "22631"));
        reg.Set(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", new RegValue(RegistryKind.String, "23H2"));
        reg.Set(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", new RegValue(RegistryKind.DWord, "0"));
        var svcs = new FakeServices();
        svcs.S["SysMain"] = (ServiceStart.Automatic, true);

        var hw = HardwareProfiler.Build(Json, Native, reg, svcs, [@"E:\SteamLibrary\steamapps\common"]);

        Assert.Equal("Intel(R) Core(TM) i5-12400F", hw.Cpu.Name);
        Assert.Equal(CpuVendor.Intel, hw.Cpu.Vendor);
        Assert.False(hw.Cpu.IsHybrid, "12400F não tem E-cores");
        var gpu = Assert.Single(hw.Gpus, "gpu (embrulho value/Count)");
        Assert.Equal(12L << 30, gpu.VramBytes, "VRAM real via registro (AdapterRAM é limitado a 4 GB)");
        Assert.Equal("560.94", gpu.DriverVersionFriendly);
        Assert.True(gpu.IsDiscrete && gpu.SupportsHags);
        Assert.Equal(MemoryChannelMode.Dual, hw.Memory.Channels);
        Assert.False(hw.Memory.ChannelsEstimated);
        Assert.Equal("DDR4", hw.Memory.Type);
        Assert.Equal(DiskKind.NvmeSsd, hw.Storage.SystemDisk!.Kind);
        Assert.Equal(DiskKind.Hdd, Assert.Single(hw.Storage.GameDisks, "game disk").Kind);
        Assert.True(hw.Os.IsWindows11);
        Assert.Equal(TriState.Off, hw.State.GameMode);
        Assert.Equal(TriState.On, hw.State.MemoryIntegrity);
        Assert.False(hw.State.IsLaptop);
        Assert.True(Insights.Analyze(hw).Any(i => i.Title.Contains("Jogos em HDD")));
        Assert.False(RecommendationEngine.BuildPlan(hw, OptimizationProfile.Competitivo).Items.First(i => i.Definition.Id == "services.sysmain").Applicability.IsApplicable,
            "jogos em HDD → manter SysMain");
    }

    [Test] public static void VramComesFromDxgiWhenAdapterRamIsCapped()
    {
        // Caso real do Thiago: RX 580 2048SP de 8 GB; AdapterRAM (uint32) para em 4 GB e o driver não grava qwMemorySize.
        const string json = """
        {"cpu":[{"Name":"Intel(R) Xeon(R) CPU E5-2620 v3 @ 2.40GHz","Manufacturer":"GenuineIntel","Cores":6,"Threads":12,"MaxClock":2400,"Virt":true}],
         "gpu":[{"Name":"AMD Radeon RX 580 2048SP","Pnp":"PCI\\VEN_1002&DEV_6FDF&SUBSYS_0B311002&REV_EF\\4&1","Driver":"31.0.21925.1001","AdapterRam":4293918720,"Refresh":75}],
         "mem":[{"Capacity":17179869184,"Speed":2133,"Configured":1866,"Locator":"DIMM_A1","Bank":"BANK 0","SmbiosType":26,"TypeDetail":8320,"TotalWidth":72,"DataWidth":64}],
         "cs":{"PcType":1},"chassis":[3]}
        """;
        var dx = new List<Snow.Platform.Native.DxgiAdapter>
        {
            new("AMD Radeon RX 580 2048SP", 0x1002, 0x6FDF, 8UL << 30, false),
            new("Microsoft Basic Render Driver", 0x1414, 0x8C, 0, true),
        };
        var hw = HardwareProfiler.Build(json, Native, new FakeRegistry(), new FakeServices(), [], dx);
        var gpu = Assert.Single(hw.Gpus, "gpu");
        Assert.Equal(8L << 30, gpu.VramBytes, "VRAM real pelo DXGI");
        Assert.Equal(75, hw.MonitorRefreshHz);
        var noDx = HardwareProfiler.Build(json, Native, new FakeRegistry(), new FakeServices(), []);
        Assert.Equal(4293918720L, noDx.Gpus[0].VramBytes, "sem DXGI continua o valor antigo (fallback)");
        Assert.Equal(Snow.Core.Games.HardwareTier.Intermediario, Snow.Core.Games.HardwareTiers.Classify(hw), "8 GB de VRAM → nível Intermediário");
        var mod = hw.Memory.Modules[0];
        Assert.True(mod.IsRegistered && mod.IsEcc, "RDIMM ECC detectado (TypeDetail 0x2000, 72/64 bits)");
        Assert.True(Insights.Analyze(hw).Any(i => i.Detail.Contains("não funciona junto com memória comum")), "aviso de não misturar RDIMM com UDIMM");
    }

    [Test] public static void XeonQuadChannelInsightsAreRight()
    {
        var hw = Hw.Desktop(h => h with
        {
            Cpu = new CpuInfo { Name = "Intel(R) Xeon(R) CPU E5-2620 v3 @ 2.40GHz", Vendor = CpuVendor.Intel, PhysicalCores = 6, LogicalProcessors = 12 },
            Memory = new MemoryInfo { TotalBytes = 16L << 30, AvailableBytes = 8L << 30, Type = "DDR4", ConfiguredSpeedMts = 1866, RatedSpeedMts = 2133,
                Channels = MemoryChannelMode.Single, ChannelsEstimated = false, Modules = [new MemoryModule(16L << 30, 2133, "DIMM_A1", "BANK 0", "", "")] },
        });
        Assert.True(hw.Cpu.IsQuadChannelPlatform && hw.Cpu.IsXeon);
        Assert.Equal(1866, hw.Cpu.MaxMemorySpeedMts);
        var ins = Insights.Analyze(hw);
        var ch = ins.Single(i => i.Title.Contains("canais"));
        Assert.Equal(InsightLevel.Critico, ch.Level);
        Assert.True(ch.Title == "Memória usando 1 de 4 canais", ch.Title);
        Assert.False(ins.Any(i => i.Detail.Contains("ative o XMP")), "não sugerir XMP em Xeon");
        Assert.True(ins.Any(i => i.Detail.Contains("máximo que o processador suporta (1866")), "explica o limite do processador");
        Assert.False(ins.Any(i => i.Title.StartsWith("Memória em canal único")), "não duplica o aviso");

        Assert.Equal(2400, (hw.Cpu with { Name = "Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz" }).MaxMemorySpeedMts);
        Assert.True((hw.Cpu with { Name = "Intel(R) Core(TM) i7-5820K CPU @ 3.30GHz" }).IsQuadChannelPlatform, "X99");
        Assert.False((hw.Cpu with { Name = "Intel(R) Core(TM) i7-13700K" }).IsQuadChannelPlatform);
        Assert.False((hw.Cpu with { Name = "Intel(R) Xeon(R) CPU E3-1230 v3 @ 3.30GHz" }).IsQuadChannelPlatform, "E3 é dual channel");
    }

    [Test] public static void SurvivesEmptyOrBrokenJson()
    {
        var hw = HardwareProfiler.Build("not json", Native, new FakeRegistry(), new FakeServices(), []);
        Assert.True(hw.CollectionWarnings.Count > 0);
        Assert.Equal(0, hw.Gpus.Count);
        Assert.True(Report.ToText(hw).Length > 0);
        RecommendationEngine.BuildPlan(hw, OptimizationProfile.Competitivo);
    }

    [Test] public static void LaptopDetectionByChassisAndSingleModule()
    {
        const string json = """{"cs":{"PcType":2},"chassis":[10],"mem":[{"Capacity":17179869184,"Speed":5600,"Configured":5600,"Bank":"BANK 0","Locator":"Controller0-ChannelA-DIMM0","SmbiosType":34}]}""";
        var hw = HardwareProfiler.Build(json, Native, new FakeRegistry(), new FakeServices(), []);
        Assert.True(hw.State.IsLaptop);
        Assert.Equal(MemoryChannelMode.Single, hw.Memory.Channels);
        Assert.Equal("DDR5", hw.Memory.Type);
    }
}
