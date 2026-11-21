using Snow.Core.Games;
using Snow.Core.Hardware;
using Snow.Core.Tweaks;
using Snow.Platform;
using Snow.Platform.Executors;
using Snow.Platform.Games;
using Snow.Platform.Monitor;
using Snow.Platform.Services;

namespace Snow.Tests;

public static class ObsTests
{
    // basic.ini como o OBS 30 grava (UTF-8 com BOM, CRLF), modo Simples com x264
    private const string BasicIni = "﻿[General]\r\nName=Sem título\r\n\r\n[Video]\r\nBaseCX=1920\r\nBaseCY=1080\r\nOutputCX=1920\r\nOutputCY=1080\r\nFPSType=0\r\nFPSCommon=60\r\n\r\n[Output]\r\nMode=Simple\r\n\r\n[SimpleOutput]\r\nVBitrate=2500\r\nStreamEncoder=x264\r\nPreset=veryfast\r\n";

    private static string FakeObs(out string basic, string mode = "Simple")
    {
        var root = Directory.CreateTempSubdirectory("snowobs").FullName;
        var obs = Path.Combine(root, "obs-studio");
        var prof = Path.Combine(obs, "basic", "profiles", "Sem_T_tulo");
        Directory.CreateDirectory(prof);
        Directory.CreateDirectory(Path.Combine(obs, "basic", "profiles", "Outro"));
        File.WriteAllText(Path.Combine(obs, "basic", "profiles", "Outro", "basic.ini"), "[Video]\r\nOutputCY=720\r\n");
        File.WriteAllText(Path.Combine(obs, "user.ini"), "[Basic]\r\nProfile=Sem título\r\nProfileDir=Sem_T_tulo\r\n");
        basic = Path.Combine(prof, "basic.ini");
        File.WriteAllText(basic, BasicIni.Replace("Mode=Simple", "Mode=" + mode));
        return root;
    }

    private static HardwareProfile Rx580 => Fx.Hw(165, 8, GpuVendor.Amd) with
    {
        Gpus = [new GpuInfo { Name = "AMD Radeon RX 580 2048SP", Vendor = GpuVendor.Amd, IsDiscrete = true, VramBytes = 8L << 30, RefreshRateHz = 165 }],
    };

    [Test] public static void PicksTheEncoderOfThePrimaryGpu()
    {
        Assert.Equal("amd", ObsProfile.HardwareEncoder(Rx580));
        Assert.Equal("nvenc", ObsProfile.HardwareEncoder(Fx.Hw()));
        var gt1030 = Fx.Hw() with { Gpus = [new GpuInfo { Name = "NVIDIA GeForce GT 1030", Vendor = GpuVendor.Nvidia, IsDiscrete = true }] };
        Assert.True(ObsProfile.HardwareEncoder(gt1030) is null, "GT 1030 não tem NVENC");
    }

    [Test] public static void ObsScanAnalyzeApplyAndRestore()
    {
        var root = FakeObs(out var basic);
        var original = File.ReadAllBytes(basic);
        var scanner = new GameScanner(new FakeRegistry(), root, appData: root);
        var obs = scanner.ScanApps().Single();
        Assert.Equal(ObsProfile.Id, obs.Id);

        var reg = new FakeRegistry();
        var backups = Directory.CreateTempSubdirectory("snowbk").FullName;
        var optimizer = new OptimizationService(new ExecutorSet([new RegistryExecutor(reg), new IniValueExecutor(new FakeProbe())]), new BackupStore(backups), new ListLog());
        var profile = new ObsProfile().WithRoot(Path.Combine(root, "obs-studio"));
        var svc = new GameService(scanner, new FakeProbe(), optimizer, [profile]);
        var report = svc.Report(obs, Rx580);
        Assert.True(report.Findings.Any(f => f.Level == FindingLevel.Importante && f.Title.Contains("processador")), "x264 com GPU AMD deve ser apontado");
        Assert.True(!report.Findings.Any(f => f.Title.Contains("Instalado em")), "conselho de disco não vale para o OBS");

        var plan = svc.BuildPreset(report, GamePreset.FpsAlto);
        var session = svc.Apply(report, plan);
        Assert.Equal(TweakStatus.Applied, session.Tweaks.Single().Status);
        var after = IniDocument.Parse(IniValueExecutor.ReadText(basic));
        Assert.Equal("amd", after.Get("SimpleOutput", "StreamEncoder"));
        Assert.Equal("6000", after.Get("SimpleOutput", "VBitrate"));
        Assert.Equal("1280", after.Get("Video", "OutputCX"));
        Assert.Equal("720", after.Get("Video", "OutputCY"));
        Assert.Equal("1080", after.Get("Video", "BaseCY"), "tela base não muda");
        Assert.True(File.ReadAllBytes(basic).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM preservado");

        var medio = svc.BuildPreset(svc.Report(obs, Rx580), GamePreset.FpsMedio);
        Assert.True(medio.Changes.Any(c => c.Label == "Largura da live" && c.Target == "1664"), "936p mantém 16:9 com largura par");

        optimizer.Restore(new BackupStore(backups).Get(session.Id)!);
        Assert.True(original.SequenceEqual(File.ReadAllBytes(basic)), "restaurar devolve o basic.ini original byte a byte");
        Directory.Delete(root, true); Directory.Delete(backups, true);
    }

    [Test] public static void AdvancedModeOnlyGuidesTheEncoder()
    {
        var root = FakeObs(out var basic, "Advanced");
        var profile = new ObsProfile().WithRoot(Path.Combine(root, "obs-studio"));
        var game = new InstalledGame(ObsProfile.Id, "OBS Studio", GameStore.Outro, root);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(root, "obs-studio", "user.ini")] = File.ReadAllText(Path.Combine(root, "obs-studio", "user.ini")),
            [basic] = IniValueExecutor.ReadText(basic),
        };
        var plan = profile.BuildPreset(new GameContext(game, Rx580, files, new HashSet<string>(), false), GamePreset.FpsAlto);
        Assert.True(!plan.Changes.Any(c => c.Label.Contains("Codificador")), "modo Avançado: codificador não é editado no basic.ini");
        Assert.True(plan.ManualSteps.Any(s => s.Value.Contains("AMD")), "passo a passo do codificador AMD");
        Directory.Delete(root, true);
    }

    [Test] public static void FindsOrphanPresentMonSessions()
    {
        const string logman = "Data Collector Set                      Type                          Status\r\n-------------------------------------------------------------------------------\r\nCircular Kernel Context Logger           Trace                         Running\r\nSnowMonitor                             Trace                         Running\r\nSnowMonitor_4120_110712                 Trace                         Running\r\nEventLog-System                         Trace                         Running\r\nThe command completed successfully.\r\n";
        var s = PresentMonLive.ParseSnowSessions(logman);
        Assert.Equal(2, s.Count, string.Join(",", s));
    }
}
