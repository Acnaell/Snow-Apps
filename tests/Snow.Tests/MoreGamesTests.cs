using Snow.Core.Games;
using Snow.Core.Hardware;
using Snow.Core.Health;
using Snow.Core.Monitor;
using Snow.Platform.Executors;
using Snow.Platform.Games;

namespace Snow.Tests;

public static class MoreGamesTests
{
    // TASystemSettings.ini do Rocket League (UE3): trecho no formato que o jogo grava
    private const string RlIni = "[SystemSettings]\r\nStaticDecals=True\r\nDynamicShadows=True\r\nMotionBlur=True\r\nDepthOfField=False\r\nBloom=True\r\nUseVsync=True\r\nLensFlares=True\r\nResX=1920\r\nResY=1080\r\n";

    [Test] public static void EpicAddonsAndSteamToolsAreNotGames()
    {
        Assert.True(GameScanner.ParseEpicManifest("""{"AppName":"Indiana","DisplayName":"Conteúdo do Fortnite: Salve o Mundo","InstallLocation":"D:/Fortnite","MainGameAppName":"Fortnite","AppCategories":["addons","addons/launchable"]}""") is null);
        Assert.True(GameScanner.ParseEpicManifest("""{"AppName":"Fortnite","DisplayName":"Fortnite","InstallLocation":"D:/Fortnite","MainGameAppName":"Fortnite","AppCategories":["public","games","applications"]}""") is not null);
        Assert.True(GameScanner.ParseEpicManifest("""{"AppName":"UE_5.4","DisplayName":"Unreal Engine","InstallLocation":"D:/UE","AppCategories":["engines"]}""") is null);
    }

    [Test] public static void RocketLeagueEditsOnlyExistingKeysAndReverts()
    {
        var dir = Directory.CreateTempSubdirectory("snowrl").FullName;
        var cfg = Path.Combine(dir, "TASystemSettings.ini");
        File.WriteAllText(cfg, RlIni);
        var original = File.ReadAllBytes(cfg);
        var p = new RocketLeagueProfile().WithConfigPath(cfg);
        var game = new InstalledGame(RocketLeagueProfile.Id, "Rocket League®", GameStore.Epic, dir);
        Assert.True(p.Aliases.Contains(RocketLeagueProfile.SteamId));
        var ctx = Fx.Ctx(game, Fx.Hw(165, 8, GpuVendor.Amd), p);
        var findings = p.Analyze(ctx);
        Assert.True(findings.Any(f => f.Title == "VSync ligado"));
        Assert.True(findings.Any(f => f.Title.Contains("Bloom") && f.Title.Contains("Desfoque")));

        var plan = p.BuildPreset(ctx, GamePreset.FpsAlto);
        Assert.False(plan.Changes.Any(c => c.Label == "Oclusão de ambiente" || c.Label == "Raios de luz"), "chave que não existe no arquivo não é criada");
        var ex = new IniValueExecutor(new FakeProbe());
        var snaps = plan.Tweaks[0].Actions.Select(a => { var s = ex.Capture(a); ex.Apply(a); return s; }).ToList();
        var after = IniDocument.Parse(File.ReadAllText(cfg));
        Assert.Equal("False", after.Get("SystemSettings", "UseVsync"));
        Assert.Equal("False", after.Get("SystemSettings", "DynamicShadows"));
        Assert.True(after.Get("SystemSettings", "AmbientOcclusion") is null);
        foreach (var s in Enumerable.Reverse(snaps)) ex.Revert(s);
        Assert.True(original.SequenceEqual(File.ReadAllBytes(cfg)));
        Directory.Delete(dir, true);
    }

    [Test] public static void FindsUnrealConfigOfAnyGame()
    {
        var root = Directory.CreateTempSubdirectory("snowue").FullName;
        var install = Path.Combine(root, "games", "Jogo X");
        Directory.CreateDirectory(Path.Combine(install, "ProjetoX", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(install, "Engine", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(install, "ProjetoX", "EasyAntiCheat"));
        var lad = Path.Combine(root, "local");
        var cfgDir = Path.Combine(lad, "ProjetoX", "Saved", "Config", "Windows");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "GameUserSettings.ini"), "[/Script/ProjetoX.XGameUserSettings]\r\nbUseVSync=True\r\nFrameRateLimit=60.000000\r\nbUseDynamicResolution=False\r\n\r\n[ScalabilityGroups]\r\nsg.ResolutionQuality=50\r\n");
        var game = new InstalledGame("steam:1", "Jogo X", GameStore.Steam, install);
        var found = GameService.UnrealConfig(game, lad);
        Assert.Equal(Path.Combine(cfgDir, "GameUserSettings.ini"), found);
        var f = UnrealConfigAdvisor.Analyze(File.ReadAllText(found!), 165);
        Assert.True(f.Any(x => x.Title == "VSync ligado") && f.Any(x => x.Title.Contains("(60)")) && f.Any(x => x.Title.Contains("50%")), string.Join(" | ", f.Select(x => x.Title)));
        Assert.Equal("Easy Anti-Cheat", GameScanner.DetectAntiCheat(install));
        Directory.Delete(root, true);
    }

    [Test] public static void LastMeasurementExplainsLolOnThiagosPc()
    {
        var csv = File.ReadAllText(Fx.Path_("lol-uso-real.csv"));
        var s = MeasuredInsight.FromUsageCsv(csv)!;
        Assert.Equal(Bottleneck.Cpu, s.Main);
        var f = MeasuredInsight.Finding(s, new DateTime(2026, 11, 21, 14, 41, 0), MeasuredInsight.FpsColumn(csv))!;
        Assert.Equal(FindingLevel.Melhoria, f.Level);
        Assert.True(f.Detail.Contains("processador") && f.Detail.Contains("GPU em ~2"), f.Detail);
        Assert.Equal("lol", MeasuredInsight.MonitorKey(new InstalledGame(LeagueProfile.Id, "LoL", GameStore.Riot, "D:/x")));
    }

    [Test] public static void GameOnFailingDiskIsFlagged()
    {
        var hw = Fx.Hw() with { Storage = new StorageInfo { Disks = [new DiskInfo(1, "SSD", DiskKind.SataSsd, 512L << 30, ["D"])] } };
        var game = new InstalledGame("steam:291550", "Brawlhalla", GameStore.Steam, @"D:\steam\steamapps\common\Brawlhalla");
        var f = MeasuredInsight.DiskHealth(game, hw, [new DiskHealthData { Name = "SSD", ReadErrorsUncorrected = 203 }]);
        Assert.True(f is { Level: FindingLevel.Importante } && f.Detail.Contains("203"));
        Assert.True(MeasuredInsight.DiskHealth(game, hw, [new DiskHealthData { Name = "SSD" }]) is null);
    }
}
