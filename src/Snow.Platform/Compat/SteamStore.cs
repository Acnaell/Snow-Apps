using System.Net.Http;
using System.Text.Json;
using Snow.Core.Compat;

namespace Snow.Platform.Compat;

/// <summary>Busca na loja da Steam (API pública, sem login): nome → app id → requisitos do PC.</summary>
public sealed class SteamStore(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? Default();

    private static HttpClient Default()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("SnowApps/1.0 (+https://github.com/Acnaell/Snow-Apps)");
        return h;
    }

    public sealed record Hit(long AppId, string Name);

    public async Task<IReadOnlyList<Hit>> SearchAsync(string term, CancellationToken ct = default)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=BR";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        return ParseSearch(doc.RootElement);
    }

    public static IReadOnlyList<Hit> ParseSearch(JsonElement root) =>
        !root.TryGetProperty("items", out var items) ? [] :
        items.EnumerateArray()
             .Where(i => !i.TryGetProperty("type", out var t) || t.GetString() == "app")
             .Select(i => new Hit(i.GetProperty("id").GetInt64(), i.GetProperty("name").GetString() ?? "")).Take(8).ToList();

    public async Task<GameRequirements?> RequirementsAsync(long appId, CancellationToken ct = default)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&filters=basic";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        return ParseDetails(appId, doc.RootElement);
    }

    public static GameRequirements? ParseDetails(long appId, JsonElement root)
    {
        if (!root.TryGetProperty(appId.ToString(), out var app) || !app.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
        var data = app.GetProperty("data");
        var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        // pc_requirements vem como objeto {minimum, recommended} ou como lista vazia quando o jogo não informa
        if (!data.TryGetProperty("pc_requirements", out var req) || req.ValueKind != JsonValueKind.Object) return null;
        string? S(string k) => req.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return SteamRequirements.Parse(appId, name, S("minimum"), S("recommended"));
    }
}
