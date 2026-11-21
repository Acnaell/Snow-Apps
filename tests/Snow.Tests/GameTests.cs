using Snow.Core.Games;
using Snow.Core.Hardware;
using Snow.Core.Tweaks;
using Snow.Platform;
using Snow.Platform.Executors;
using Snow.Platform.Games;
using Snow.Platform.Services;

namespace Snow.Tests;

public sealed class FakeProbe(bool running = false) : IProcessProbe
{
    public bool Running = running;
    public bool IsRunning(IEnumerable<string> names) => Running;
}

internal static class Fx
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static string Path_(string name) => Path.Combine(Dir, name);

    /// <summary>Copia um fixture para uma pasta temporária (os testes editam a cópia).</summary>
    public static string Copy(string name, out string tempDir)
    {
        tempDir = Directory.CreateTempSubdirectory("snowgame").FullName;
        var dst = Path.Combine(tempDir, Path.GetFileName(name));
        File.Copy(Path_(name), dst);
        return dst;
    }

    public static HardwareProfile Hw(int refresh = 165, long vramGb = 12, GpuVendor vendor = GpuVendor.Nvidia) => Hw_(refresh, vramGb, vendor);

    private static HardwareProfile Hw_(int refresh, long vramGb, GpuVendor vendor) => Snow.Tests.Hw.Desktop(h => h with
    {
        Gpus = [new GpuInfo { Name = vendor == GpuVendor.Nvidia ? "NVIDIA GeForce RTX 4070" : "AMD Radeon RX 7800 XT", Vendor = vendor, IsDiscrete = true, VramBytes = vramGb << 30, WddmMajorFromDriver = 32, RefreshRateHz = refresh }],
    });

    public static GameContext Ctx(InstalledGame game, HardwareProfile hw, IGameProfile profile, bool running = false, bool readOnly = false)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in profile.ConfigFiles(game).Where(File.Exists)) files[f] = IniValueExecutor.ReadText(f);
        return new GameContext(game, hw, files, readOnly ? files.Keys.ToHashSet() : new HashSet<string>(), running);
    }
}

public static class IniTests
{
    [Test] public static void RoundTripIsByteExact()
    {
        foreach (var name in new[] { "fortnite_GameUserSettings.ini", "lol_game.cfg" })
        {
            var text = IniValueExecutor.ReadText(Fx.Path_(name));
            Assert.Equal(text, IniDocument.Parse(text).ToString(), name);
        }
    }

    [Test] public static void SetPreservesLayoutAndLineEndings()
    {
        var doc = IniDocument.Parse("; c\r\n[A]\r\nx=1\r\n\r\n[B]\r\ny=2\r\n");
        doc.Set("A", "x", "9");
        doc.Set("A", "new", "5");
        doc.Set("C", "z", "3");
        doc.Set("B", "y", null);
        Assert.Equal("; c\r\n[A]\r\nx=9\r\nnew=5\r\n\r\n[B]\r\n\r\n[C]\r\nz=3\r\n", doc.ToString());
        Assert.Equal("9", doc.Get("a", "X"), "seção/chave sem diferenciar maiúsculas");
    }
}

public static class GameProfileTests
{
    private static InstalledGame Fortnite(string dir) => new(FortniteProfile.Id, "Fortnite", GameStore.Epic, dir);

    [Test] public static void FortniteFindsRealProblems()
    {
        var cfg = Fx.Copy("fortnite_GameUserSettings.ini", out var tmp);
        var p = new FortniteProfile().WithConfigPath(cfg);
        var findings = p.Analyze(Fx.Ctx(Fortnite(tmp), Fx.Hw(165), p));
        var titles = findings.Select(f => f.Title).ToList();
        Assert.True(titles.Contains("VSync ligado"), string.Join(" | ", titles));
        Assert.True(titles.Any(t => t.StartsWith("Resolução 3D em 37%")));
        Assert.True(titles.Contains("Resolução dinâmica ativa"));
        Assert.True(titles.Contains("Modo de renderização: Performance"));
        Assert.True(titles.Contains("Limite de FPS em 165"));
        Directory.Delete(tmp, true);
    }

    [Test] public static void FortnitePresetChangesOnlyTargetKeysAndReverts()
    {
        foreach (var preset in Enum.GetValues<GamePreset>())
        {
            var cfg = Fx.Copy("fortnite_GameUserSettings.ini", out var tmp);
            var original = File.ReadAllBytes(cfg);
            var p = new FortniteProfile().WithConfigPath(cfg);
            var plan = p.BuildPreset(Fx.Ctx(Fortnite(tmp), Fx.Hw(144), p), preset);

            var ex = new IniValueExecutor(new FakeProbe());
            var snaps = plan.Tweaks[0].Actions.Select(a => { var s = ex.Capture(a); ex.Apply(a); return s; }).ToList();
            foreach (var a in plan.Tweaks[0].Actions) Assert.Equal(true, ex.Verify(a), a.Describe());

            var after = IniDocument.Parse(IniValueExecutor.ReadText(cfg));
            Assert.Equal("False", after.Get("/Script/FortniteGame.FortGameUserSettings", "bUseVSync"));
            Assert.Equal("144.000000", after.Get("/Script/FortniteGame.FortGameUserSettings", "FrameRateLimit"));
            Assert.Equal("100", after.Get("ScalabilityGroups", "sg.ResolutionQuality"));
            Assert.True(File.ReadAllText(cfg).StartsWith(";METADATA"), "cabeçalho do Unreal preservado");
            Assert.True(File.ReadAllText(cfg).Contains("\r\n") && !File.ReadAllText(cfg).Replace("\r\n", "").Contains('\n'), "CRLF preservado");

            foreach (var s in Enumerable.Reverse(snaps)) ex.Revert(s);
            Assert.True(original.SequenceEqual(File.ReadAllBytes(cfg)), $"{preset}: arquivo não voltou idêntico byte a byte");
            Directory.Delete(tmp, true);
        }
    }

    [Test] public static void FortniteMediumOnEntryHardwareUsesPerformanceMode()
    {
        var cfg = Fx.Copy("fortnite_GameUserSettings.ini", out var tmp);
        var p = new FortniteProfile().WithConfigPath(cfg);
        var weak = Fx.Hw(60, 4);
        Assert.Equal(HardwareTier.Entrada, HardwareTiers.Classify(weak));
        var plan = p.BuildPreset(Fx.Ctx(Fortnite(tmp), weak, p), GamePreset.FpsMedio);
        Assert.True(plan.Changes.Any(c => c.Label == "Modo de renderização" && c.Target == "es31"));
        Assert.False(plan.Changes.Any(c => c.Label == "NVIDIA Reflex") && weak.PrimaryGpu!.Vendor != GpuVendor.Nvidia);
        var amd = p.BuildPreset(Fx.Ctx(Fortnite(tmp), Fx.Hw(144, 16, GpuVendor.Amd), p), GamePreset.FpsAlto);
        Assert.False(amd.Changes.Any(c => c.Label == "NVIDIA Reflex"), "Reflex só em NVIDIA");
        Directory.Delete(tmp, true);
    }

    [Test] public static void LeaguePresetRoundTripAndNoServerKeys()
    {
        var tmp = Directory.CreateTempSubdirectory("snowlol").FullName;
        Directory.CreateDirectory(Path.Combine(tmp, "Config"));
        var cfg = Path.Combine(tmp, "Config", "game.cfg");
        File.Copy(Fx.Path_("lol_game.cfg"), cfg);
        var original = File.ReadAllBytes(cfg);
        var game = new InstalledGame(LeagueProfile.Id, "League of Legends", GameStore.Riot, tmp, AntiCheat: "Riot Vanguard");
        var p = new LeagueProfile();
        foreach (var preset in Enum.GetValues<GamePreset>())
        {
            var plan = p.BuildPreset(Fx.Ctx(game, Fx.Hw(), p), preset);
            var keys = plan.Tweaks[0].Actions.Cast<IniValueAction>().Select(a => a.Key).ToList();
            Assert.False(keys.Contains("EnableHUDAnimations"), "chave sincronizada com o servidor não pode ser tocada");
            Assert.False(keys.Contains("FrameCapType"), "FrameCapType tem significado incerto: só orientação");
            var ex = new IniValueExecutor(new FakeProbe());
            var snaps = plan.Tweaks[0].Actions.Select(a => { var s = ex.Capture(a); ex.Apply(a); return s; }).ToList();
            Assert.Equal("0", IniDocument.Parse(IniValueExecutor.ReadText(cfg)).Get("General", "WaitForVerticalSync"));
            foreach (var s in Enumerable.Reverse(snaps)) ex.Revert(s);
            Assert.True(original.SequenceEqual(File.ReadAllBytes(cfg)), $"{preset}: game.cfg não voltou idêntico");
        }
        Directory.Delete(tmp, true);
    }

    [Test] public static void ValorantIsReadOnlyAndFindsMultithreadOff()
    {
        var root = Path.Combine(Fx.Dir, "valorant");
        var p = new ValorantProfile().WithConfigRoot(root);
        var game = new InstalledGame(ValorantProfile.Id, "VALORANT", GameStore.Riot, Fx.Dir, AntiCheat: "Riot Vanguard");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)) files[f] = IniValueExecutor.ReadText(f);
        var ctx = new GameContext(game, Fx.Hw(), files, new HashSet<string>(), false);

        Assert.Equal(GameSupport.Guiado, p.Support);
        var titles = p.Analyze(ctx).Select(f => f.Title).ToList();
        Assert.True(titles.Contains("Renderização multithread desligada"), string.Join(" | ", titles));
        foreach (var preset in Enum.GetValues<GamePreset>())
        {
            var plan = p.BuildPreset(ctx, preset);
            Assert.Equal(0, plan.Tweaks.Count, "Valorant nunca edita arquivos");
            Assert.True(plan.ManualSteps.Count >= 5);
        }
    }

    [Test] public static void ExecutorRespectsRunningGameAndReadOnly()
    {
        var cfg = Fx.Copy("fortnite_GameUserSettings.ini", out var tmp);
        var a = new IniValueAction(cfg, "ScalabilityGroups", "sg.ShadowQuality", "3", ["FortniteClient-Win64-Shipping"]);
        Assert.Throws<SkipException>(() => new IniValueExecutor(new FakeProbe(running: true)).Apply(a));
        File.SetAttributes(cfg, FileAttributes.ReadOnly);
        Assert.Throws<SkipException>(() => new IniValueExecutor(new FakeProbe()).Apply(a));
        File.SetAttributes(cfg, FileAttributes.Normal);
        Directory.Delete(tmp, true);
    }

    [Test] public static void BomAndUtf16ArePreserved()
    {
        var tmp = Directory.CreateTempSubdirectory("snowbom").FullName;
        var u16 = Path.Combine(tmp, "u16.ini");
        File.WriteAllBytes(u16, [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("[S]\r\nk=1\r\n")]);
        var original = File.ReadAllBytes(u16);
        var ex = new IniValueExecutor(new FakeProbe());
        var a = new IniValueAction(u16, "S", "k", "2", []);
        var snap = ex.Capture(a); ex.Apply(a);
        Assert.Equal(true, ex.Verify(a));
        Assert.True(File.ReadAllBytes(u16)[0] == 0xFF, "BOM UTF-16 mantido");
        ex.Revert(snap);
        Assert.True(original.SequenceEqual(File.ReadAllBytes(u16)));
        Directory.Delete(tmp, true);
    }

    [Test] public static void GenericAdvisorFlagsHddAndHybridGpu()
    {
        var hw = Hw.Win10Hdd with { Gpus = [new GpuInfo { Name = "Intel UHD", Vendor = GpuVendor.Intel }, new GpuInfo { Name = "RTX 3050 Laptop", Vendor = GpuVendor.Nvidia, IsDiscrete = true }] };
        var titles = GenericGameAdvisor.Analyze(new InstalledGame("steam:1", "Jogo", GameStore.Steam, @"D:\Steam\common\Jogo"), hw, true).Select(f => f.Title).ToList();
        Assert.True(titles.Any(t => t.StartsWith("Instalado em HDD")), string.Join("|", titles));
        Assert.True(titles.Contains("Dois adaptadores de vídeo"));
        Assert.True(titles.Contains("Jogo aberto agora"));
    }

    [Test] public static void TiersFollowHardware()
    {
        Assert.Equal(HardwareTier.Avancado, HardwareTiers.Classify(Fx.Hw(144, 12)));
        Assert.Equal(HardwareTier.Entusiasta, HardwareTiers.Classify(Fx.Hw(144, 24)));
        Assert.Equal(HardwareTier.Intermediario, HardwareTiers.Classify(Fx.Hw(144, 8)));
        Assert.Equal(HardwareTier.Intermediario, HardwareTiers.Classify(Fx.Hw(144, 24) with { Memory = new MemoryInfo { TotalBytes = 8L << 30 } }), "8 GB de RAM limita");
        Assert.Equal(HardwareTier.Entrada, HardwareTiers.Classify(Hw.VirtualMachine));
    }
}

public static class ScannerTests
{
    private static string Native(string p) => p.Replace('\\', Path.DirectorySeparatorChar);

    [Test] public static void ParsesStoreManifests()
    {
        var acf = GameScanner.ParseAcf("\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"\n}");
        Assert.Equal("730", acf["appid"]);
        Assert.Equal("Counter-Strike Global Offensive", acf["installdir"]);

        var epic = GameScanner.ParseEpicManifest("""{"AppName":"Fortnite","DisplayName":"Fortnite","InstallLocation":"D:/Fortnite","LaunchExecutable":"FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe","bIsIncompleteInstall":false}""")!;
        Assert.Equal(FortniteProfile.Id, epic.Id);
        Assert.Equal(Native(@"D:\Fortnite"), epic.InstallDir);
        Assert.Equal("Easy Anti-Cheat / BattlEye", epic.AntiCheat);
        Assert.True(GameScanner.ParseEpicManifest("""{"AppName":"X","InstallLocation":"D:/X","bIsIncompleteInstall":true}""") is null, "instalação incompleta ignorada");

        var yaml = File.ReadAllText(Fx.Path_("lol.product_settings.yaml"));
        Assert.Equal(Native(@"D:\Riot Games\League of Legends"), GameScanner.ParseRiotInstallPath(yaml));
        Assert.True(GameScanner.UsesVanguard(yaml), "LoL declara vanguard: true");
        Assert.False(GameScanner.UsesVanguard("dependencies:\n    Direct X 9:\n        phase: \"Succeeded\"\n"));
    }

    [Test] public static void ScansFakeRiotAndEpicInstallsEndToEnd()
    {
        var pd = Directory.CreateTempSubdirectory("snowpd").FullName;
        var lolDir = Path.Combine(pd, "Games", "League of Legends");
        Directory.CreateDirectory(Path.Combine(lolDir, "Config"));
        File.Copy(Fx.Path_("lol_game.cfg"), Path.Combine(lolDir, "Config", "game.cfg"));
        var meta = Path.Combine(pd, "Riot Games", "Metadata", "league_of_legends.live");
        Directory.CreateDirectory(meta);
        File.WriteAllText(Path.Combine(meta, "league_of_legends.live.product_settings.yaml"),
            File.ReadAllText(Fx.Path_("lol.product_settings.yaml")).Replace("D:/Riot Games/League of Legends", lolDir.Replace('\\', '/')));
        var epicDir = Path.Combine(pd, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        Directory.CreateDirectory(epicDir);
        var gameDir = Path.Combine(pd, "Games", "Jogo Epic");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(epicDir, "a.item"), $$"""{"AppName":"Jogo","DisplayName":"Jogo Epic","InstallLocation":"{{gameDir.Replace("\\", "/")}}"}""");

        var scanner = new GameScanner(new FakeRegistry(), pd);
        var games = scanner.Scan();
        Assert.Equal(2, games.Count, string.Join(", ", games.Select(g => g.Id)));
        var lol = games.First(g => g.Id == LeagueProfile.Id);
        Assert.Equal("Riot Vanguard", lol.AntiCheat);

        // fluxo completo: relatório → predefinição → aplicar com backup → restaurar
        var reg = new FakeRegistry();
        var set = new ExecutorSet([new RegistryExecutor(reg), new IniValueExecutor(new FakeProbe())]);
        var backups = Directory.CreateTempSubdirectory("snowbk").FullName;
        var optimizer = new OptimizationService(set, new BackupStore(backups), new ListLog());
        var svc = new GameService(scanner, new FakeProbe(), optimizer);
        var report = svc.Report(lol, Fx.Hw());
        Assert.Equal(GameSupport.Editavel, report.Support);
        var cfg = Path.Combine(lolDir, "Config", "game.cfg");
        var original = File.ReadAllBytes(cfg);
        var plan = svc.BuildPreset(report, GamePreset.FpsAlto);
        var session = svc.Apply(report, plan);
        Assert.Equal(TweakStatus.Applied, session.Tweaks.Single().Status);
        Assert.True(session.Profile.StartsWith("Jogo: League of Legends"));
        Assert.False(original.SequenceEqual(File.ReadAllBytes(cfg)));
        optimizer.Restore(new BackupStore(backups).Get(session.Id)!);
        Assert.True(original.SequenceEqual(File.ReadAllBytes(cfg)), "restaurar pelo backup devolve o game.cfg original");
        Directory.Delete(pd, true);
        Directory.Delete(backups, true);
    }

    [Test] public static void RunningGameIsSkippedNotFailed()
    {
        var tmp = Directory.CreateTempSubdirectory("snowrun").FullName;
        Directory.CreateDirectory(Path.Combine(tmp, "Config"));
        File.Copy(Fx.Path_("lol_game.cfg"), Path.Combine(tmp, "Config", "game.cfg"));
        var game = new InstalledGame(LeagueProfile.Id, "League of Legends", GameStore.Riot, tmp);
        var probe = new FakeProbe(running: true);
        var optimizer = new OptimizationService(new ExecutorSet([new IniValueExecutor(probe)]), new BackupStore(Path.Combine(tmp, "bk")), new ListLog());
        var svc = new GameService(new GameScanner(new FakeRegistry(), tmp), probe, optimizer);
        var report = svc.Report(game, Fx.Hw());
        Assert.True(report.Findings.Any(f => f.Title == "Jogo aberto agora"));
        var session = svc.Apply(report, svc.BuildPreset(report, GamePreset.FpsMedio));
        Assert.Equal(TweakStatus.Skipped, session.Tweaks.Single().Status);
        Directory.Delete(tmp, true);
    }
}
