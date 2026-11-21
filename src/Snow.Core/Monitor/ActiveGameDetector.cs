namespace Snow.Core.Monitor;

/// <summary>
/// Descobre qual jogo está rodando a partir dos quadros que o PresentMon vê em TODOS os processos.
/// Só usa o nome do executável (nada de abrir o processo: regra do anti-cheat).
/// Programas que também desenham na tela (navegador, Discord, OBS, papel de parede animado…) são ignorados.
/// </summary>
public static class ActiveGameDetector
{
    public static readonly IReadOnlySet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "explorer.exe", "snow.exe", "snow-cli.exe",
        "obs64.exe", "obs32.exe", "streamlabs obs.exe", "discord.exe", "spotify.exe", "teams.exe", "ms-teams.exe", "whatsapp.exe", "telegram.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "brave.exe", "vivaldi.exe", "msedgewebview2.exe",
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "epicwebhelper.exe", "battle.net.exe", "eadesktop.exe", "ubisoftconnect.exe", "galaxyclient.exe",
        "leagueclient.exe", "leagueclientux.exe", "leagueclientuxrender.exe", "riotclientservices.exe", "riotclientux.exe", "riotclientuxrender.exe", "riot client.exe",
        "wallpaper32.exe", "wallpaper64.exe", "webwallpaper32.exe", "lively.exe",
        "applicationframehost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe", "searchapp.exe", "textinputhost.exe",
        "lockapp.exe", "systemsettings.exe", "taskmgr.exe", "gamebar.exe", "gamebarftserver.exe", "code.exe", "devenv.exe",
        "radeonsoftware.exe", "amdrsserv.exe", "nvidia share.exe", "nvcontainer.exe", "nvidia app.exe", "overwolf.exe", "medal.exe", "msiafterburner.exe", "rtss.exe",
    };

    /// <summary>Processos de jogo conhecidos → chave usada no Snow (limite de FPS, nome do arquivo).</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["FortniteClient-Win64-Shipping.exe"] = "fortnite",
        ["League of Legends.exe"] = "lol",
        ["VALORANT-Win64-Shipping.exe"] = "valorant",
        ["cs2.exe"] = "cs2",
        ["r5apex.exe"] = "apex", ["r5apex_dx12.exe"] = "apex",
        ["RocketLeague.exe"] = "rocketleague",
        ["javaw.exe"] = "minecraft",
        ["GTA5.exe"] = "gta5",
        ["Brawlhalla.exe"] = "brawlhalla",
    };

    public static bool IsIgnored(string app) => NotGames.Contains(app) || app.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Escolhe o jogo ativo pelos quadros dos últimos segundos. Precisa de ≥ 20 quadros na janela (descarta janelas paradas);
    /// jogo conhecido tem preferência; o atual só é trocado se outro tiver claramente mais quadros (evita pular entre apps).
    /// </summary>
    public static string? Pick(IReadOnlyDictionary<string, int> recentFrames, string? current)
    {
        var candidates = recentFrames.Where(kv => kv.Value >= 20 && !IsIgnored(kv.Key)).ToList();
        if (candidates.Count == 0) return null;
        var known = candidates.Where(kv => KnownGames.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).ToList();
        var best = (known.Count > 0 ? known : candidates.OrderByDescending(kv => kv.Value).ToList())[0];
        if (current is not null && candidates.FirstOrDefault(kv => kv.Key.Equals(current, StringComparison.OrdinalIgnoreCase)) is { Key: not null } cur
            && (KnownGames.ContainsKey(cur.Key) || known.Count == 0) && cur.Value * 2 >= best.Value)
            return cur.Key;
        return best.Key;
    }

    public static string DisplayName(string app) =>
        app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;

    public static string KeyFor(string app) =>
        KnownGames.TryGetValue(app, out var k) ? k
        : new string(DisplayName(app).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } s ? s : "jogo";
}
