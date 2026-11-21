using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snow.Core.Updates;

public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

public static partial class UpdateLogic
{
    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?")] private static partial Regex TagRegex();
    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")] private static partial Regex SumLine();

    public static Version? ParseTag(string tag)
    {
        var m = TagRegex().Match(tag.Trim());
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Compara só major.minor.build (a revisão do assembly é ignorada).</summary>
    public static bool IsNewer(Version candidate, Version current) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(0, candidate.Build)) >
        new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>Escolhe o pacote da arquitetura do processo: Snow-win-x64.zip, Snow-win-x86.zip ou Snow-win-arm64.zip.</summary>
    public static ReleaseAsset? PickAsset(IEnumerable<ReleaseAsset> assets, string rid) =>
        assets.FirstOrDefault(a => a.Name.Equals($"Snow-{rid}.zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>Lê a resposta de /repos/{owner}/{repo}/releases/latest da API do GitHub.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) return null;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var version = ParseTag(tag);
        if (version is null) return null;
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                // A API do GitHub informa "digest": "sha256:<hex>" para cada arquivo da release.
                string? sha = null;
                if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..].ToLowerInvariant();
                if (name.Length > 0 && url.Length > 0) assets.Add(new ReleaseAsset(name, url, size, sha));
            }
        }
        return new ReleaseInfo(version, tag,
            r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            assets);
    }

    /// <summary>Lê um SHA256SUMS.txt (formato do sha256sum: "hash  nome").</summary>
    public static Dictionary<string, string> ParseSums(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = SumLine().Match(line.Trim());
            if (m.Success) d[Path.GetFileName(m.Groups[2].Value.Trim())] = m.Groups[1].Value.ToLowerInvariant();
        }
        return d;
    }
}
