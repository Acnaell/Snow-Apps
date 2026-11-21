using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;
using Snow.Platform.Executors;
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
