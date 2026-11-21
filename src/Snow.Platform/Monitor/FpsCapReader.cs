using System.Globalization;
using Snow.Core.Games;

namespace Snow.Platform.Monitor;

/// <summary>
/// Limite de FPS em vigor, para o Monitor não confundir "preso no processador" com "no teto de FPS".
/// Fortnite: FrameRateLimit do GameUserSettings.ini (0 = sem limite). Outros: a taxa do monitor (VSync/limitadores comuns).
/// </summary>
public static class FpsCapReader
{
    public static double? ForGame(string? gameKey, int refreshHz, string? fortniteConfigPath = null)
    {
        double? monitor = refreshHz > 0 ? refreshHz : null;
        if (gameKey != "fortnite") return monitor;
        try
        {
            var path = fortniteConfigPath ?? FortniteProfile.ConfigPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (!File.Exists(path)) return monitor;
            var ini = IniDocument.Parse(Games.IniValueExecutor.ReadText(path));
            if (ini.Get("/Script/FortniteGame.FortGameUserSettings", "bUseVSync") is "True") return monitor;
            return double.TryParse(ini.Get("/Script/FortniteGame.FortGameUserSettings", "FrameRateLimit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) && cap > 0
                ? cap : null;
        }
        catch { return monitor; }
    }
}
