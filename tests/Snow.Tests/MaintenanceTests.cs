using Snow.Core.Health;
using Snow.Platform.Health;

namespace Snow.Tests;

public static class MaintenanceTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "snowmt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Make(string path, int bytes, int ageDays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
    }

    [Test] public static void CatalogIdsAreUniqueAndResolve()
    {
        Assert.Equal(CleanupCatalog.All.Count, CleanupCatalog.All.Select(c => c.Id).Distinct().Count(), "ids únicos");
        var env = new Dictionary<string, string> { ["WINDIR"] = "/w" };
        Assert.Equal(Path.Combine("/w", "SoftwareDistribution", "Download"), CleanupCatalog.Resolve(@"{WINDIR}\SoftwareDistribution\Download", env));
        Assert.Equal("/w", CleanupCatalog.Resolve("{WINDIR}", env));
        // Nada do catálogo aponta para pastas da Riot/Vanguard, Prefetch ou registro
        Assert.False(CleanupCatalog.All.SelectMany(c => c.Paths).Any(p => p.Contains("Riot", StringComparison.OrdinalIgnoreCase) || p.Contains("Prefetch", StringComparison.OrdinalIgnoreCase)));
    }

    [Test] public static void CleanRespectsAgeGlobAndRunningApps()
    {
        var root = NewDir();
        try
        {
            var env = new Dictionary<string, string> { ["LOCALAPPDATA"] = root, ["WINDIR"] = root };
            var cat = new CleanupCategory
            {
                Id = "t", Title = "t", Description = "t", MinAgeDays = 7,
                Paths = [@"{LOCALAPPDATA}\Cache", @"{LOCALAPPDATA}\web*", @"{WINDIR}\MEMORY.DMP"],
            };
            Make(Path.Combine(root, "Cache", "old.bin"), 1000, 30);
            Make(Path.Combine(root, "Cache", "sub", "old2.bin"), 500, 30);
            Make(Path.Combine(root, "Cache", "new.bin"), 777, 0);
            Make(Path.Combine(root, "webcache_4147", "a.bin"), 200, 10);
            Make(Path.Combine(root, "MEMORY.DMP"), 300, 20);
            Make(Path.Combine(root, "outside.bin"), 999, 100);

            var m = MaintenanceService.Measure(cat, env, _ => false);
            Assert.Equal(4L, m.Files, "arquivos medidos");
            Assert.Equal(2000L, m.Bytes, "bytes medidos");

            var blocked = MaintenanceService.Clean(cat with { SkipIfRunning = ["app.exe"] }, env, _ => true);
            Assert.True(blocked.Skipped is not null && blocked.Files == 0, "pula com o app aberto");
            Assert.True(File.Exists(Path.Combine(root, "Cache", "old.bin")));

            var r = MaintenanceService.Clean(cat, env, _ => false);
            Assert.Equal(4L, r.Files, "apagados");
            Assert.Equal(2000L, r.Bytes);
            Assert.True(File.Exists(Path.Combine(root, "Cache", "new.bin")), "arquivo novo fica");
            Assert.True(File.Exists(Path.Combine(root, "outside.bin")), "fora do catálogo fica");
            Assert.True(Directory.Exists(Path.Combine(root, "Cache")), "a pasta raiz fica");
            Assert.False(Directory.Exists(Path.Combine(root, "Cache", "sub")), "subpasta vazia some");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test] public static void TrimParsing()
    {
        Assert.Equal(true, DriveCareParser.TrimFromFsutil("NTFS DisableDeleteNotify = 0  (Allows TRIM operations to be sent to the storage device)\nReFS DisableDeleteNotify = 0"));
        Assert.Equal(false, DriveCareParser.TrimFromFsutil("NTFS DisableDeleteNotify = 1  (Desabilitado)"));
        Assert.Equal((bool?)null, DriveCareParser.TrimFromFsutil("erro"));
        var items = DriveCareParser.Items(new DriveCare(false, false), anySsd: true).ToList();
        Assert.Equal(2, items.Count(i => i.Level == HealthLevel.Atencao), "TRIM e agendamento");
    }

    private static ProcessUsage U(string app, double cpu, long mb = 100) => new(app, 1, cpu, cpu, mb << 20);

    [Test] public static void ProcessAdvisorNeverTouchesGamesOrAntiCheat()
    {
        foreach (var app in new[] { "vgc.exe", "vgtray.exe", "RiotClientServices.exe", "LeagueClientUxRender.exe", "EasyAntiCheat_EOS.exe", "BEService.exe", "FACEITService.exe" })
        {
            var f = ProcessAdvisor.Classify(U(app, 20));
            Assert.Equal(ProcessKind.AntiCheat, f.Kind, app);
            Assert.False(f.CanClose, app);
            Assert.True(ProcessAdvisor.IsProtected(app), app);
            Assert.Equal((int?)null, ProcessAdvisor.MatchStartup(app, [$@"C:\Riot Games\{app}"]), app);
        }
        foreach (var app in new[] { "VALORANT-Win64-Shipping.exe", "FortniteClient-Win64-Shipping.exe", "League of Legends.exe", "Hogwarts Legacy-Win64-Shipping.exe" })
        {
            var f = ProcessAdvisor.Classify(U(app, 40, 3000));
            Assert.Equal(ProcessKind.Jogo, f.Kind, app);
            Assert.False(f.CanClose, app);
        }
        Assert.False(ProcessAdvisor.Classify(U("svchost.exe", 12)).CanClose);
        Assert.False(ProcessAdvisor.Classify(U("MsMpEng.exe", 12)).CanClose);
        Assert.Equal("Não", MaintenanceService.CloseApp("vgc.exe", true).Contains("não fecha") ? "Não" : "Sim", "CloseApp recusa anti-cheat");
    }

    [Test] public static void ProcessReportRanksAndAdvises()
    {
        var r = ProcessAdvisor.Analyze(
        [
            U("chrome.exe", 9, 2500), U("Discord.exe", 3), U("svchost.exe", 2), U("idle", 70), U("snow.exe", 4),
            U("tinyapp.exe", 0.2, 20), U("mysterytool.exe", 6), U("VALORANT-Win64-Shipping.exe", 30, 3000),
        ], 10);
        Assert.Equal("chrome.exe", r.Findings[0].Usage.App, "mais pesado primeiro (jogos vão para o fim)");
        Assert.False(r.Findings.Any(f => f.Usage.App is "idle" or "snow.exe" or "tinyapp.exe"), "sem idle/Snow/insignificantes");
        var chrome = r.Findings.Single(f => f.Usage.App == "chrome.exe");
        Assert.Equal(HealthLevel.Atencao, chrome.Level);
        Assert.True(chrome.CanClose && chrome.Advice.Contains("abas"));
        Assert.Equal(ProcessKind.Desconhecido, r.Findings.Single(f => f.Usage.App == "mysterytool.exe").Kind);
        Assert.Equal(20.2, r.BackgroundCpu, "CPU em segundo plano sem jogo, idle e Snow");
        Assert.True(r.Summary.Contains("jogo aberto"));
    }

    [Test] public static void PdhInstanceNamesAndStartupMatch()
    {
        Assert.Equal("chrome.exe", ProcessAdvisor.AppFromPdhInstance("chrome#12"));
        Assert.Equal("Discord.exe", ProcessAdvisor.AppFromPdhInstance("Discord"));
        Assert.Equal("idle", ProcessAdvisor.AppFromPdhInstance("Idle"));
        Assert.Equal("memory compression", ProcessAdvisor.AppFromPdhInstance("Memory Compression"));
        string[] cmds = [@"""C:\Program Files\Steam\steamwebhelper.exe"" -silent", @"""C:\Users\t\AppData\Local\Discord\Update.exe"" --processStart Discord.exe", @"C:\x\firefox.exe"];
        Assert.Equal((int?)1, ProcessAdvisor.MatchStartup("Discord.exe", cmds));
        Assert.Equal((int?)null, ProcessAdvisor.MatchStartup("steam.exe", cmds), "steam ≠ steamwebhelper");
        Assert.Equal((int?)null, ProcessAdvisor.MatchStartup("fox.exe", cmds), "fox ≠ firefox");
    }

    // ── Placa-mãe / TPM
    private static readonly BoardInfo Huananzhi = new() { Maker = "HUANANZHI", Product = "X99-F8", BiosVersion = "5.11", BiosDate = new DateTime(2023, 3, 1), MemorySlots = 8, MemoryUsed = 1 };

    [Test] public static void ThiagoX99HasNoTpmRoute()
    {
        const string cpu = "Intel(R) Xeon(R) CPU E5-2620 v3 @ 2.40GHz";
        var s = new SecurityState { Uefi = true, SecureBoot = false, SystemDiskGpt = true };
        Assert.Equal(TpmRoute.SemConector, BoardAdvisor.Route(cpu, s, Huananzhi));
        Assert.Equal(TpmRoute.ModuloNoConector, BoardAdvisor.Route(cpu, s, new BoardInfo { Maker = "ASUSTeK COMPUTER INC.", Product = "X99-A II" }));
        Assert.True(BoardAdvisor.IsChineseX99(new BoardInfo { Maker = "INTEL", Product = "X99" }), "fabricante genérico + X99");
        Assert.False(BoardAdvisor.IsChineseX99(new BoardInfo { Maker = "ASRock", Product = "X99 Taichi" }));
        Assert.Equal((bool?)false, BoardAdvisor.Windows11Cpu(cpu));

        var items = BoardAdvisor.Items(Huananzhi, s, cpu, 19045).ToList();
        Assert.True(items.Any(i => i.Title.Contains("HUANANZHI X99-F8")));
        Assert.True(items.Any(i => i.Title.Contains("1 de 8 slots")));
        var tpm = items.Single(i => i.Title.StartsWith("Sem TPM"));
        Assert.Equal(HealthLevel.Info, tpm.Level, "no Windows 10 é informativo");
        Assert.True(tpm.Detail.Contains("Windows 10, o Valorant roda sem TPM"));
        Assert.True(items.Any(i => i.Title == "Processador fora da lista do Windows 11"));
        Assert.Equal(HealthLevel.Atencao, BoardAdvisor.Items(Huananzhi, s, cpu, 26100).Single(i => i.Title.StartsWith("Sem TPM")).Level, "no Windows 11 é atenção (Valorant)");
    }

    [Test] public static void TpmRoutesByPlatform()
    {
        var none = new SecurityState();
        Assert.Equal(TpmRoute.FirmwareAmd, BoardAdvisor.Route("AMD Ryzen 5 1600 Six-Core Processor", none, null));
        Assert.Equal(TpmRoute.FirmwareIntel, BoardAdvisor.Route("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", none, null));
        Assert.Equal(TpmRoute.FirmwareIntel, BoardAdvisor.Route("12th Gen Intel(R) Core(TM) i5-12400F", none, null));
        Assert.Equal(TpmRoute.ModuloNoConector, BoardAdvisor.Route("Intel(R) Core(TM) i7-4770 CPU @ 3.40GHz", none, null));
        Assert.Equal(TpmRoute.JaTem, BoardAdvisor.Route("x", new SecurityState { Tpm = new(true, true, true, "2.0, 0, 1.38", "AMD") }, null));
        Assert.Equal(TpmRoute.LigarNaBios, BoardAdvisor.Route("x", new SecurityState { Tpm = new(true, false, false, "2.0, 0, 1.16", "INTC") }, null));
        Assert.Equal((bool?)false, BoardAdvisor.Windows11Cpu("AMD Ryzen 5 1600 Six-Core Processor"));
        Assert.Equal((bool?)true, BoardAdvisor.Windows11Cpu("AMD Ryzen 5 5600X 6-Core Processor"));
        Assert.Equal((bool?)true, BoardAdvisor.Windows11Cpu("Intel(R) Core(TM) i5-8400 CPU"));
        Assert.Equal((bool?)true, BoardAdvisor.Windows11Cpu("12th Gen Intel(R) Core(TM) i7-12700K"));
        var legacy = BoardAdvisor.Items(null, new SecurityState { Uefi = false, SystemDiskGpt = false }, "AMD Ryzen 5 5600X", 26100).ToList();
        Assert.True(legacy.Any(i => i.Title.Contains("Legacy") && i.Detail.Contains("MBR") && i.Action!.Contains("mbr2gpt")));
    }

    [Test] public static void BoardInfoDoesNotLowerScore()
    {
        var baseData = new HealthData { Events = new EventCounts(), UptimeDays = 1 };
        var a = HealthAnalyzer.Analyze(baseData).Score;
        var b = HealthAnalyzer.Analyze(baseData with { Board = Huananzhi, CpuName = "Intel(R) Xeon(R) CPU E5-2620 v3", OsBuild = 19045, Security = new SecurityState { Uefi = true, SecureBoot = true } }).Score;
        Assert.Equal(a, b, "informativos da placa não tiram nota");
    }
}
