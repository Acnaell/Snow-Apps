using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Snow.Core.Games;
using Snow.Core.Tweaks;
using Snow.Platform.Executors;
using Snow.Platform.Services;

namespace Snow.Platform.Games;

/// <summary>
/// Verifica se um processo está aberto apenas pelo NOME (lista de processos do sistema).
/// Nunca abre handle, lê memória ou interage com o processo: seguro para anti-cheats como o Vanguard.
/// </summary>
public interface IProcessProbe
{
    bool IsRunning(IEnumerable<string> processNames);
}

public sealed class ProcessProbe : IProcessProbe
{
    public bool IsRunning(IEnumerable<string> processNames)
    {
        foreach (var name in processNames)
        {
            var procs = Process.GetProcessesByName(name);
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (running) return true;
        }
        return false;
    }
}

/// <summary>Identifica jogos instalados em Steam, Epic Games e Riot Client, sem precisar dos launchers abertos.</summary>
public sealed partial class GameScanner(IRegistryStore registry, string? programData = null)
{
    private readonly string _programData = programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public IReadOnlyList<InstalledGame> Scan()
    {
        var games = new List<InstalledGame>();
        games.AddRange(Safe(ScanSteam));
        games.AddRange(Safe(ScanEpic));
        games.AddRange(Safe(ScanRiot));
        return games
            .Where(g => Directory.Exists(g.InstallDir))
            .GroupBy(g => g.Id).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<InstalledGame> Safe(Func<IEnumerable<InstalledGame>> f)
    {
        try { return f().ToList(); }
        catch { return []; }
    }

    // ───────── Steam: libraryfolders.vdf + appmanifest_*.acf ─────────
    [GeneratedRegex("\"(appid|name|installdir)\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)] private static partial Regex AcfField();

    private static readonly HashSet<string> SteamNonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "228980", // Steamworks Common Redistributables
        "1070560", "1391110", "1628350", // Steam Linux Runtime
        "250820", // SteamVR
    };

    public IEnumerable<InstalledGame> ScanSteam()
    {
        var steam = registry.Get(Core.Tweaks.RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")?.Data
                 ?? registry.Get(Core.Tweaks.RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")?.Data;
        if (string.IsNullOrEmpty(steam)) yield break;
        steam = steam.Replace('/', '\\');
        var roots = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) roots.AddRange(GameLibraryFinder.ParseVdfPaths(File.ReadAllText(vdf)));

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var fields = ParseAcf(File.ReadAllText(acf));
                if (!fields.TryGetValue("appid", out var id) || SteamNonGames.Contains(id)) continue;
                if (!fields.TryGetValue("installdir", out var dir)) continue;
                yield return new InstalledGame($"steam:{id}", fields.GetValueOrDefault("name") ?? dir, GameStore.Steam,
                    Path.Combine(apps, "common", dir));
            }
        }
    }

    public static Dictionary<string, string> ParseAcf(string acf)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AcfField().Matches(acf))
            d.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Replace(@"\\", @"\"));
        return d;
    }

    // ───────── Epic: %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item ─────────
    public IEnumerable<InstalledGame> ScanEpic()
    {
        var dir = Path.Combine(_programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            InstalledGame? g = null;
            try { g = ParseEpicManifest(File.ReadAllText(f)); } catch (JsonException) { }
            if (g is not null) yield return g;
        }
    }

    public static InstalledGame? ParseEpicManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
        var app = S("AppName"); var loc = S("InstallLocation");
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(loc)) return null;
        var exe = S("LaunchExecutable");
        return new InstalledGame($"epic:{app.ToLowerInvariant()}", S("DisplayName") ?? app, GameStore.Epic, Native(loc),
            string.IsNullOrEmpty(exe) ? null : Path.Combine(loc, exe),
            AntiCheat: app.Equals("Fortnite", StringComparison.OrdinalIgnoreCase) ? "Easy Anti-Cheat / BattlEye" : null);
    }

    // ───────── Riot: %ProgramData%\Riot Games\Metadata\<produto>.live\<produto>.live.product_settings.yaml ─────────
    [GeneratedRegex("^product_install_full_path:\\s*\"?([^\"\\r\\n]+)\"?", RegexOptions.Multiline)] private static partial Regex RiotPath();

    private static readonly Dictionary<string, string> RiotProducts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["league_of_legends"] = "League of Legends",
        ["valorant"] = "VALORANT",
        ["bacon"] = "Legends of Runeterra",
        ["lion"] = "2XKO",
    };

    public IEnumerable<InstalledGame> ScanRiot()
    {
        var meta = Path.Combine(_programData, "Riot Games", "Metadata");
        if (!Directory.Exists(meta)) yield break;
        foreach (var (product, name) in RiotProducts)
        {
            var yaml = Path.Combine(meta, $"{product}.live", $"{product}.live.product_settings.yaml");
            if (!File.Exists(yaml)) continue;
            var text = File.ReadAllText(yaml);
            if (ParseRiotInstallPath(text) is { } path)
                yield return new InstalledGame($"riot:{product}", name, GameStore.Riot, path,
                    AntiCheat: UsesVanguard(text) ? "Riot Vanguard" : null);
        }
    }

    [GeneratedRegex("^\\s*vanguard:\\s*true\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)] private static partial Regex VanguardDep();

    /// <summary>O product_settings.yaml da Riot lista "vanguard: true" nas dependências dos jogos protegidos.</summary>
    public static bool UsesVanguard(string yaml) => VanguardDep().IsMatch(yaml);

    public static string? ParseRiotInstallPath(string yaml) =>
        RiotPath().Match(yaml) is { Success: true } m ? Native(m.Groups[1].Value.Trim()) : null;

    /// <summary>Launchers gravam "/" ; normaliza para o separador do sistema (\ no Windows).</summary>
    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}

/// <summary>Relatório de um jogo: achados + suporte.</summary>
public sealed record GameReport(InstalledGame Game, IGameProfile? Profile, GameContext Context, IReadOnlyList<GameFinding> Findings)
{
    public GameSupport Support => Profile?.Support ?? GameSupport.Generico;
}

public sealed class GameService(GameScanner scanner, IProcessProbe processes, OptimizationService optimizer, IReadOnlyList<IGameProfile>? profiles = null)
{
    private readonly IReadOnlyList<IGameProfile> _profiles = profiles ?? GameProfiles.All();

    public static GameService CreateDefault(OptimizationService optimizer) =>
        new(new GameScanner(new WindowsRegistryStore()), new ProcessProbe(), optimizer);

    public IReadOnlyList<GameReport> Analyze(Core.Hardware.HardwareProfile hw) =>
        scanner.Scan().Select(g => Report(g, hw)).ToList();

    public GameReport Report(InstalledGame game, Core.Hardware.HardwareProfile hw)
    {
        var profile = _profiles.FirstOrDefault(p => p.GameId.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
        var running = profile is not null && processes.IsRunning(profile.Processes);
        var ctx = LoadContext(game, hw, profile, running);
        var findings = new List<GameFinding>();
        if (profile is not null)
        {
            try { findings.AddRange(profile.Analyze(ctx)); }
            catch (Exception e) { findings.Add(new(FindingLevel.Info, "Não foi possível ler a configuração", e.Message)); }
        }
        findings.AddRange(GenericGameAdvisor.Analyze(game, hw, running));
        return new GameReport(game, profile, ctx, findings);
    }

    private static GameContext LoadContext(InstalledGame game, Core.Hardware.HardwareProfile hw, IGameProfile? profile, bool running)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var readOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in profile?.ConfigFiles(game) ?? [])
        {
            foreach (var path in Expand(pattern))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    files[path] = IniValueExecutor.ReadText(path);
                    if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) readOnly.Add(path);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return new GameContext(game, hw, files, readOnly, running);
    }

    /// <summary>Expande um único "*" de pasta (ex.: pastas de conta do Valorant).</summary>
    private static IEnumerable<string> Expand(string pattern)
    {
        var parts = pattern.Split(Path.DirectorySeparatorChar);
        var star = Array.IndexOf(parts, "*");
        if (star < 0) return [pattern];
        var parent = string.Join(Path.DirectorySeparatorChar, parts[..star]);
        if (!Directory.Exists(parent)) return [];
        var rest = string.Join(Path.DirectorySeparatorChar, parts[(star + 1)..]);
        return Directory.EnumerateDirectories(parent).Select(d => Path.Combine(d, rest));
    }

    public GamePresetPlan BuildPreset(GameReport report, GamePreset preset) =>
        report.Profile?.BuildPreset(report.Context, preset)
        ?? new GamePresetPlan(preset, [], [], [], "Sem predefinição para este jogo: veja os achados.");

    public BackupSession Apply(GameReport report, GamePresetPlan plan, IProgress<ProgressInfo>? progress = null)
    {
        if (plan.Tweaks.Count == 0) throw new InvalidOperationException("Este jogo é somente diagnóstico: siga o passo a passo.");
        var items = plan.Tweaks.Select(t => new Core.Recommendation.PlannedTweak(t, true, Applicability.Yes, null)).ToList();
        var opt = new Core.Recommendation.OptimizationPlan(OptimizationProfile.Desempenho, items);
        return optimizer.Apply(opt, new ApplyOptions(CreateRestorePoint: false, Label: $"Jogo: {report.Game.Name} ({plan.Preset.Label()})"), progress);
    }
}
