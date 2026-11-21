using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snow.Core.Games;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;
using Snow.Platform.Executors;
using Snow.Platform.Games;
using Snow.Platform.Profiling;
using Snow.Platform.Services;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Snow.Cli;

public static class Program
{
    private const string Help = """
        Snow Game Optimizer — linha de comando (a interface gráfica é o Snow.exe)

          snow-cli diagnose [--json]                    Diagnóstico de hardware e estado do Windows
          snow-cli plan    --profile <perfil>           Mostra o que cada perfil alteraria (nada é aplicado)
          snow-cli apply   --profile <perfil> [--yes] [--only id1,id2] [--skip id1,id2] [--no-restore-point]
          snow-cli restore [--latest | --id <id> | --all]
          snow-cli backups                              Lista as sessões de backup
          snow-cli startup                              Lista os itens de inicialização
          snow-cli purge-standby                        Esvazia a lista Standby da RAM
          snow-cli health [--raw]                       Snow Health: nota de saúde, discos, estabilidade, drivers
          snow-cli gpu                                  Fontes de VRAM (DXGI, registro, WMI) para diagnóstico
          snow-cli update [--check] [--yes]             Verifica/instala a versão mais nova (SHA-256 conferido)
          snow-cli monitor [--seconds 30] [--game auto|fortnite|lol|valorant]   Snow Monitor: uso, FPS e gargalo
          snow-cli canrun [--search "nome do jogo"]     Roda no meu PC? Requisitos oficiais (ou da Steam) × seu hardware
          snow-cli bench [--drives C,D]                 Pontuação: CPU (1 núcleo / todos), memória e discos
          snow-cli stress [--minutes 10] [--no-ram] [--no-cpu]   Estresse com conferência de erros e queda de clock
          snow-cli upgrade [--seconds 60]               Upgrade sem gargalo (mede o jogo aberto se --seconds > 0)
          snow-cli compare antes.csv depois.csv          Compara duas capturas do PresentMon/Snow Bench
          snow-cli games [--preset alto|medio]          Jogos instalados, pontos de melhora e prévia da predefinição
          snow-cli game-apply --id <id> --preset alto|medio [--yes]   Aplica a predefinição num jogo (com backup)
          snow-cli selftest --profile <perfil> [--skip ids]   Aplica, verifica e reverte (uso em CI)

        Perfis: competitivo | desempenho | equilibrado
        Requer Administrador para apply/restore/selftest/purge-standby.
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("O Snow só funciona no Windows 10/11.");
            return 3;
        }
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Help);
            if (args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.Write("\nAberto com duplo clique? Use o Snow.exe para a interface gráfica. Pressione Enter para fechar.");
                Console.ReadLine();
            }
            return 0;
        }

        var opts = ParseOptions(args.Skip(1).ToArray());
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "diagnose" => Diagnose(opts),
                "plan" => Plan(opts),
                "apply" => RequireAdmin() ?? Apply(opts),
                "restore" => RequireAdmin() ?? Restore(opts),
                "backups" => Backups(),
                "startup" => Startup(),
                "purge-standby" => RequireAdmin() ?? PurgeStandby(),
                "games" => Games(opts),
                "health" => Health(opts),
                "gpu" => Gpu(),
                "update" => RequireAdmin() ?? Update(opts),
                "monitor" => Monitor(opts),
                "upgrade" => Upgrade(opts),
                "bench" => Bench(opts),
                "canrun" => CanRunCmd(opts),
                "stress" => Stress(opts),
                "compare" => Compare(args.Skip(1).Where(a => !a.StartsWith("--")).ToArray()),
                "game-apply" => GameApply(opts),
                "selftest" => RequireAdmin() ?? SelfTest(opts),
                "version" or "--version" => Version(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Erro: {e.Message}");
            FileLog.Instance.Error(e.ToString());
            return 1;
        }
    }

    private static Dictionary<string, string?> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            var key = a[i][2..];
            d[key] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : null;
        }
        return d;
    }

    private static int? RequireAdmin()
    {
        if (Elevation.IsAdministrator()) return null;
        Console.Error.WriteLine("Este comando precisa ser executado como Administrador.");
        return 2;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Diagnose(Dictionary<string, string?> o)
    {
        var hw = HardwareProfiler.Collect();
        if (o.ContainsKey("json"))
        {
            var payload = new
            {
                hardware = hw,
                insights = Insights.Analyze(hw),
                recommendation = RecommendationEngine.Recommend(hw),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts));
        }
        else Console.WriteLine(Report.ToText(hw));
        return 0;
    }

    private static OptimizationPlan BuildPlan(Dictionary<string, string?> o)
    {
        var hw = HardwareProfiler.Collect();
        if (!o.TryGetValue("profile", out var p) || !Report.TryParseProfile(p, out var profile))
        {
            profile = RecommendationEngine.Recommend(hw).Profile;
            Console.WriteLine($"(sem --profile: usando o recomendado, {profile.Label()})");
        }
        var plan = RecommendationEngine.BuildPlan(hw, profile);

        static HashSet<string> Ids(string? v) => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        bool Match(string id, HashSet<string> set) => set.Any(s => s.EndsWith('*') ? id.StartsWith(s[..^1]) : s == id);

        if (o.TryGetValue("only", out var only)) { var set = Ids(only); plan = plan.WithSelection(plan.Items.Where(i => Match(i.Definition.Id, set)).Select(i => i.Definition.Id)); }
        if (o.TryGetValue("skip", out var skip)) { var set = Ids(skip); plan = plan.WithSelection(plan.Selected.Where(i => !Match(i.Definition.Id, set)).Select(i => i.Definition.Id)); }
        return plan;
    }

    private static int Plan(Dictionary<string, string?> o)
    {
        Console.WriteLine(Report.PlanToText(BuildPlan(o)));
        return 0;
    }

    private static int Apply(Dictionary<string, string?> o)
    {
        var plan = BuildPlan(o);
        Console.WriteLine(Report.PlanToText(plan));
        if (!o.ContainsKey("yes"))
        {
            Console.Write("Aplicar estes ajustes? Um backup completo será salvo. [s/N] ");
            if (!(Console.ReadLine() ?? "").Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        var svc = OptimizationService.CreateDefault(new ConsoleLog());
        var progress = new Progress<ProgressInfo>(p => Console.WriteLine($"[{p.Done}/{p.Total}] {p.Message}"));
        var session = svc.Apply(plan, new ApplyOptions(CreateRestorePoint: !o.ContainsKey("no-restore-point")), progress);
        PrintSession(session);
        if (plan.RequiresReboot) Console.WriteLine("\n⚠ Reinicie o PC para concluir (HAGS).");
        return session.Tweaks.Any(t => t.Status == TweakStatus.Failed) ? 4 : 0;
    }

    private static void PrintSession(BackupSession s)
    {
        Console.WriteLine($"\nBackup: {s.Id}  ·  Ponto de restauração: {s.RestorePoint ?? "não solicitado"}");
        foreach (var t in s.Tweaks)
        {
            Console.WriteLine($"  [{t.Status}] {t.Title}");
            foreach (var m in t.Messages) Console.WriteLine($"      {m}");
        }
    }

    private static int Restore(Dictionary<string, string?> o)
    {
        var svc = OptimizationService.CreateDefault(new ConsoleLog());
        if (o.ContainsKey("all")) { Console.WriteLine($"{svc.RestoreAll()} alteração(ões) revertida(s)."); return 0; }
        var session = o.TryGetValue("id", out var id) && id is not null
            ? svc.Backups.Get(id)
            : svc.Backups.List().FirstOrDefault(s => s.RestoredUtc is null && s.HasRevertibleChanges);
        if (session is null) { Console.WriteLine("Nenhum backup pendente para restaurar."); return 0; }
        Console.WriteLine($"{svc.Restore(session)} alteração(ões) revertida(s) do backup {session.Id}.");
        return 0;
    }

    private static int Backups()
    {
        foreach (var s in new BackupStore().List())
            Console.WriteLine($"{s.Id}  {s.Profile,-12} {s.Tweaks.Count(t => t.Snapshots.Count > 0),2} ajustes  {(s.RestoredUtc is null ? "ATIVO" : $"restaurado {s.RestoredUtc.Value.ToLocalTime():g}")}");
        return 0;
    }

    private static int Startup()
    {
        foreach (var i in new StartupManager(new WindowsRegistryStore()).List())
            Console.WriteLine($"{(i.Enabled ? "[on ]" : "[off]")} {i.Name,-30} {i.Location,-28} {i.Command}");
        return 0;
    }

    private static int PurgeStandby()
    {
        CleanupExecutor.PurgeStandbyList();
        Console.WriteLine("Lista Standby esvaziada.");
        return 0;
    }

    private static int SelfTest(Dictionary<string, string?> o)
    {
        var plan = BuildPlan(o);
        Console.WriteLine(Report.PlanToText(plan));
        var result = OptimizationService.CreateDefault(new ConsoleLog()).SelfTest(plan);
        foreach (var l in result.Lines) Console.WriteLine(l);
        return result.Success ? 0 : 5;
    }

    private static GamePreset ParsePreset(Dictionary<string, string?> o) =>
        o.TryGetValue("preset", out var p) && p is not null && p.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? GamePreset.FpsMedio : GamePreset.FpsAlto;

    private static int Games(Dictionary<string, string?> o)
    {
        var hw = HardwareProfiler.Collect();
        var svc = GameService.CreateDefault(OptimizationService.CreateDefault(new ConsoleLog()));
        var preset = ParsePreset(o);
        var reports = svc.Analyze(hw);
        Console.WriteLine($"Hardware: nível {HardwareTiers.Classify(hw).Label()}{(hw.MonitorRefreshHz > 0 ? $" · monitor {hw.MonitorRefreshHz} Hz" : "")} · {reports.Count} jogo(s)\n");
        foreach (var r in reports)
        {
            Console.WriteLine($"■ {r.Game.Name}  [{r.Game.Id}]  {r.Game.Store} · {r.Support}{(r.Game.AntiCheat is { } ac ? $" · {ac}" : "")}");
            Console.WriteLine($"  {r.Game.InstallDir}");
            foreach (var f in r.Findings.OrderByDescending(f => f.Level))
                Console.WriteLine($"  [{f.Level}] {f.Title}: {f.Detail}");
            if (r.Profile is null) { Console.WriteLine(); continue; }
            var plan = svc.BuildPreset(r, preset);
            Console.WriteLine($"  → {plan.Summary}");
            foreach (var c in plan.Changes)
                Console.WriteLine($"     {c.Label,-28} {c.Current ?? "(padrão)",-14} → {c.Target,-12} {c.Why}");
            foreach (var s in plan.ManualSteps)
                Console.WriteLine($"     (no jogo) {s.Where}: {s.Setting} → {s.Value}");
            Console.WriteLine();
        }
        return 0;
    }

    private static int GameApply(Dictionary<string, string?> o)
    {
        if (!o.TryGetValue("id", out var id) || id is null) { Console.Error.WriteLine("Informe --id (veja snow-cli games)."); return 64; }
        var hw = HardwareProfiler.Collect();
        var svc = GameService.CreateDefault(OptimizationService.CreateDefault(new ConsoleLog()));
        var report = svc.Analyze(hw).FirstOrDefault(r => r.Game.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (report is null) { Console.Error.WriteLine($"Jogo {id} não encontrado."); return 3; }
        var plan = svc.BuildPreset(report, ParsePreset(o));
        if (plan.Tweaks.Count == 0) { Console.WriteLine("Este jogo é somente diagnóstico: siga o passo a passo de 'snow-cli games'."); return 0; }
        foreach (var c in plan.Changes) Console.WriteLine($"  {c.Label,-28} {c.Current ?? "(padrão)",-14} → {c.Target}");
        if (!o.ContainsKey("yes"))
        {
            Console.Write("Aplicar? O jogo precisa estar fechado. [s/N] ");
            if (!(Console.ReadLine() ?? "").Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        var session = svc.Apply(report, plan);
        PrintSession(session);
        return session.Tweaks.Any(t => t.Status == TweakStatus.Failed) ? 4 : 0;
    }

    private static int Update(Dictionary<string, string?> o)
    {
        var svc = new Snow.Platform.Updates.UpdateService(new ConsoleLog());
        svc.CleanupOldFiles();
        var check = svc.CheckAsync().GetAwaiter().GetResult();
        if (check.Error is not null) { Console.Error.WriteLine($"Não foi possível verificar: {check.Error}"); return 7; }
        Console.WriteLine($"Instalada: {check.Current.ToString(3)} · Disponível: {check.Latest?.Tag ?? "-"} · Pacote: {check.Asset?.Name ?? "nenhum para " + Snow.Platform.Updates.UpdateService.Rid}");
        if (!check.Available) { Console.WriteLine("Você já está na versão mais recente."); return 0; }
        if (o.ContainsKey("check")) return 0;
        if (!o.ContainsKey("yes"))
        {
            Console.Write("Instalar agora? [s/N] ");
            if (!(Console.ReadLine() ?? "").Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        var path = svc.InstallAsync(check, new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
        Console.WriteLine($"Atualizado. Abra {path} para usar a nova versão.");
        return 0;
    }

    private static int Gpu()
    {
        Console.WriteLine("== DXGI (mesma fonte do Gerenciador de Tarefas) ==");
        var dx = Snow.Platform.Native.Dxgi.Enumerate();
        foreach (var a in dx)
            Console.WriteLine($"  {a.Description} · VEN_{a.VendorId:X4}&DEV_{a.DeviceId:X4} · dedicada {a.DedicatedVideoMemory / 1048576.0:N0} MB{(a.IsSoftware ? " · software" : "")}");
        if (dx.Count == 0) Console.WriteLine("  (nenhum adaptador)");
        var hw = HardwareProfiler.Collect();
        Console.WriteLine("== Snow (valor final) ==");
        foreach (var g in hw.Gpus)
            Console.WriteLine($"  {g.Name} · {g.PnpDeviceId} · VRAM {g.VramBytes / 1048576.0:N0} MB");
        foreach (var w in hw.CollectionWarnings) Console.WriteLine($"  (aviso) {w}");
        return dx.Count > 0 ? 0 : 6;
    }

    private static int Health(Dictionary<string, string?> o)
    {
        if (o.ContainsKey("raw"))
        {
            var data = Snow.Platform.Health.HealthCollector.Collect();
            Console.WriteLine(JsonSerializer.Serialize(data, JsonOpts));
            return 0;
        }
        var r = Snow.Platform.Health.HealthCollector.Run();
        Console.WriteLine($"══════════ SNOW HEALTH: {r.Score}/100 ({r.Verdict}) ══════════");
        foreach (var g in r.Items.GroupBy(i => i.Category))
        {
            Console.WriteLine($"\n── {g.Key} ──");
            foreach (var i in g)
            {
                Console.WriteLine($"[{i.Level}] {i.Title}: {i.Detail}");
                if (i.Action is not null) Console.WriteLine($"         → {i.Action}");
            }
        }
        foreach (var w in r.Data.Warnings) Console.WriteLine($"(aviso de coleta) {w}");
        return 0;
    }

    private static readonly Dictionary<string, string> GameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fortnite"] = "FortniteClient-Win64-Shipping.exe",
        ["lol"] = "League of Legends.exe",
        ["valorant"] = "VALORANT-Win64-Shipping.exe",
    };

    private static int Monitor(Dictionary<string, string?> o)
    {
        var seconds = o.TryGetValue("seconds", out var s) && int.TryParse(s, out var n) ? n : 30;
        using var pdh = new Snow.Platform.Monitor.PdhSampler();
        Snow.Platform.Monitor.PresentMonLive? pm = null;
        if (o.TryGetValue("game", out var g) && g is not null && (g == "auto" || GameProcesses.ContainsKey(g)))
        {
            if (Snow.Platform.Monitor.PresentMonLive.Find() is { } exe) pm = new(exe, g == "auto" ? null : GameProcesses[g]);
            else Console.WriteLine("PresentMon não encontrado (Downloads): só uso de hardware, sem FPS.");
        }
        double? cap = null;
        var refreshHz = 0;
        if (g is not null)
        {
            try { refreshHz = HardwareProfiler.Collect().MonitorRefreshHz; } catch { }
            cap = g == "auto" ? null : Snow.Platform.Monitor.FpsCapReader.ForGame(g, refreshHz);
        }
        Console.WriteLine($"Snow Monitor · {seconds}s · contadores de GPU: {(pdh.HasGpuCounters ? "sim" : "indisponíveis")}");
        var samples = new List<Snow.Core.Monitor.UsageSample>();
        for (var i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            if (pm is { IsAuto: true } && pm.UpdateActive() is { } app)
                cap = Snow.Platform.Monitor.FpsCapReader.ForGame(Snow.Core.Monitor.ActiveGameDetector.KeyFor(app), refreshHz);
            var (fps, low) = pm?.Current() ?? (null, null);
            var x = pdh.Sample(fps, low) with { FpsCap = cap };
            samples.Add(x);
            Console.WriteLine($"{x.Time:HH:mm:ss}  CPU {x.CpuPercent,5:0.0}%  núcleo {x.BusiestCorePercent,5:0.0}%  GPU {(x.GpuPercent?.ToString("0.0") ?? "  -  "),5}%  VRAM {(x.VramUsedGb?.ToString("0.0") ?? "-"),4} GB  RAM {x.RamUsedPercent,3:0}%  FPS {(x.Fps?.ToString("0") ?? "-"),4}  → {Snow.Core.Monitor.BottleneckClassifier.Label(Snow.Core.Monitor.BottleneckClassifier.Classify(x))}");
        }
        var summary = Snow.Core.Monitor.SessionSummary.From(samples, pm?.AllFrameTimes);
        if (pm?.MainApp is { } main) Console.WriteLine($"\nJogo medido: {Snow.Core.Monitor.ActiveGameDetector.DisplayName(main)}");
        pm?.Dispose();
        LastSummary = summary;
        Console.WriteLine($"\nResumo: {Snow.Core.Monitor.BottleneckClassifier.Label(summary.Main)} · CPU média {summary.AvgCpu}% · GPU média {summary.AvgGpu?.ToString() ?? "-"}%");
        foreach (var (b, pct) in summary.TimeShare.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {Snow.Core.Monitor.BottleneckClassifier.Label(b),-28} {pct}% do tempo");
        if (summary.Frames is { } f) Console.WriteLine($"  FPS médio {f.AvgFps} · 1% low {f.Low1} · 0,1% low {f.Low01} · engasgos {f.Stutters}");
        Console.WriteLine($"  {Snow.Core.Monitor.BottleneckClassifier.Advice(summary.Main)}");
        return 0;
    }

    private static Snow.Core.Monitor.SessionSummary? LastSummary;

    private static int CanRunCmd(Dictionary<string, string?> o)
    {
        var hw = HardwareProfiler.Collect();
        if (o.TryGetValue("search", out var term) && !string.IsNullOrWhiteSpace(term))
        {
            var steam = new Snow.Platform.Compat.SteamStore();
            var hits = steam.SearchAsync(term).GetAwaiter().GetResult();
            if (hits.Count == 0) { Console.WriteLine("Nada encontrado na Steam."); return 4; }
            Console.WriteLine("Steam: " + string.Join(" | ", hits.Select(h => $"{h.Name} ({h.AppId})")));
            var req = steam.RequirementsAsync(hits[0].AppId).GetAwaiter().GetResult();
            if (req is null) { Console.WriteLine($"{hits[0].Name} não informa requisitos de PC."); return 4; }
            var res = Snow.Core.Compat.CanRun.Evaluate(req, hw);
            Console.WriteLine($"\n{req.Name}: [{Snow.Core.Compat.CanRun.Label(res.Level)}] {res.Verdict}\n  {res.Detail}\n  {req.Note}");
            foreach (var t in req.Tiers) Console.WriteLine($"  {t.Name}: CPU {string.Join(" / ", t.Cpus)} (índice {t.CpuIndex?.ToString() ?? "?"}) · GPU {string.Join(" / ", t.Gpus)} (índice {t.GpuIndex?.ToString() ?? "?"}) · {t.RamGb:0} GB");
            return 0;
        }
        Console.WriteLine($"{hw.Cpu.Name} → índice {Snow.Core.Compat.HardwareIndex.Cpu(hw.Cpu)?.ToString() ?? "?"} · {hw.PrimaryGpu?.Name} → índice {Snow.Core.Compat.HardwareIndex.Gpu(hw.PrimaryGpu?.Name)?.ToString() ?? "?"} · {hw.Memory.TotalGb:0} GB\n");
        foreach (var r in Snow.Core.Compat.CanRun.EvaluateAll(hw).OrderByDescending(r => r.Level))
            Console.WriteLine($"[{Snow.Core.Compat.CanRun.Label(r.Level),-15}] {r.Game.Name,-24} {r.Verdict}\n{"",18}{r.Detail}");
        return 0;
    }

    private static int Bench(Dictionary<string, string?> o)
    {
        var hw = HardwareProfiler.Collect();
        var drives = o.TryGetValue("drives", out var d) && !string.IsNullOrWhiteSpace(d)
            ? d.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [hw.Storage.SystemDrive];
        var r = Snow.Platform.Bench.BenchRunner.Run(hw.Cpu.Name, drives, new Progress<string>(Console.WriteLine));
        Console.WriteLine($"\nProcessador  1 núcleo: {r.CpuSingle} pts · {r.Threads} threads: {r.CpuMulti} pts");
        Console.WriteLine($"Memória      {r.MemoryGBs} GB/s ({Snow.Core.Bench.BenchVerdicts.Memory(r.MemoryGBs)}) · latência {r.MemoryLatencyNs} ns ({Snow.Core.Bench.BenchVerdicts.Latency(r.MemoryLatencyNs)})");
        foreach (var x in r.Disks)
            Console.WriteLine($"Disco {x.Drive}     leitura {x.SeqReadMBs} MB/s · escrita {x.SeqWriteMBs} MB/s · 4K {x.Random4kReadIops} IOPS ({Snow.Core.Bench.BenchVerdicts.Disk(x)})");
        Console.WriteLine($"Salvo em {Snow.Platform.Bench.BenchRunner.Dir}");
        return 0;
    }

    private static int Stress(Dictionary<string, string?> o)
    {
        var minutes = o.TryGetValue("minutes", out var m) && double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 10;
        var cpu = !o.ContainsKey("no-cpu"); var ram = !o.ContainsKey("no-ram");
        Console.WriteLine($"Estresse por {minutes} min · CPU {(cpu ? "sim" : "não")} · RAM {(ram ? "sim" : "não")} (Ctrl+C para parar)");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var r = Snow.Platform.Bench.BenchRunner.Stress(TimeSpan.FromMinutes(minutes), cpu, ram,
            p => { if ((int)p.At.TotalSeconds % 10 == 0) Console.WriteLine($"{p.At:mm\\:ss}  CPU {p.CpuPercent:0}%  clock {p.PerformancePercent:0}%  erros CPU {p.CpuErrors}  RAM {p.RamErrors}"); }, cts.Token);
        Console.WriteLine();
        foreach (var (ok, title, detail) in r.Verdicts()) Console.WriteLine($"{(ok ? "OK " : "!! ")}{title}\n    {detail}");
        return r.CpuErrors + r.RamErrors > 0 ? 3 : 0;
    }

    private static int Upgrade(Dictionary<string, string?> o)
    {
        var seconds = o.TryGetValue("seconds", out var s) && int.TryParse(s, out var n) ? n : 0;
        if (seconds > 0) Monitor(new Dictionary<string, string?>(o) { ["game"] = "auto" });
        var hw = HardwareProfiler.Collect();
        IReadOnlyList<Snow.Core.Health.DiskHealthData>? disks = null;
        try { disks = Snow.Platform.Health.HealthCollector.Collect().Disks; } catch { }
        var session = LastSummary is { Main: not Snow.Core.Monitor.Bottleneck.Desconhecido and not Snow.Core.Monitor.Bottleneck.SemCarga } ls ? ls : null;
        Console.WriteLine($"\nUpgrade sem gargalo · {hw.Cpu.Name} · {hw.PrimaryGpu?.Name} · {hw.Memory.TotalGb:0} GB {hw.Memory.Type}");
        Console.WriteLine(session is null ? "(estimativa pelo hardware; use --seconds 120 com um jogo aberto para usar o gargalo real)\n" : "(com base na medição acima)\n");
        foreach (var a in Snow.Core.Recommendation.UpgradeAdvisor.Advise(hw, session, disks))
            Console.WriteLine($"[{Snow.Core.Recommendation.UpgradeAdvisor.Label(a.Priority)}] {a.Component}: {a.Title}\n    {a.Detail}\n");
        return 0;
    }

    private static int Compare(string[] files)
    {
        if (files.Length != 2) { Console.Error.WriteLine("Uso: snow-cli compare antes.csv depois.csv"); return 64; }
        var a = Snow.Core.Monitor.PresentMonCapture.Parse(File.ReadAllText(files[0]));
        var b = Snow.Core.Monitor.PresentMonCapture.Parse(File.ReadAllText(files[1]));
        if (a.Stats is not { } sa || b.Stats is not { } sb) { Console.Error.WriteLine("Poucos quadros em um dos arquivos."); return 4; }
        Console.WriteLine($"{"",-24}{"ANTES",12}{"DEPOIS",12}{"Δ",10}");
        foreach (var r in Snow.Core.Monitor.SessionComparer.Compare(sa, sb))
            Console.WriteLine($"{r.Metric,-24}{r.Before,12}{r.After,12}{r.Delta,10}  {(r.Better ? "✔" : "✖")}");
        foreach (var (name, cap) in new[] { ("antes", a), ("depois", b) })
            if (cap.BoundShare is { } bs) Console.WriteLine($"Gargalo ({name}): GPU em {bs.gpuBound}% dos quadros · CPU em {bs.cpuBound}%");
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine($"Snow {typeof(Program).Assembly.GetName().Version}");
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Comando desconhecido: {cmd}\n\n{Help}");
        return 64;
    }

    private sealed class ConsoleLog : ILog
    {
        public event Action<string>? Written;
        public void Info(string m) { FileLog.Instance.Info(m); Written?.Invoke(m); }
        public void Error(string m) { FileLog.Instance.Error(m); Console.Error.WriteLine("  ! " + m); }
    }
}
