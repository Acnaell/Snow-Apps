using System.Net;
using System.Text.RegularExpressions;

namespace Snow.Core.Compat;

/// <summary>
/// Lê os requisitos da página do jogo na Steam (campo pc_requirements da API pública da loja, em inglês)
/// e transforma em níveis comparáveis. Texto livre: o Snow reconhece os modelos de CPU/GPU que conhece e
/// avisa quando não reconhece algum.
/// </summary>
public static partial class SteamRequirements
{
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"(Processor|CPU)\s*:\s*(?<v>[^\n]+)", RegexOptions.IgnoreCase)] private static partial Regex CpuLine();
    [GeneratedRegex(@"(Graphics|Video Card|GPU|Video)\s*:\s*(?<v>[^\n]+)", RegexOptions.IgnoreCase)] private static partial Regex GpuLine();
    [GeneratedRegex(@"(Memory|RAM)\s*:\s*(?<v>[\d.,]+)\s*(?<u>GB|MB)", RegexOptions.IgnoreCase)] private static partial Regex RamLine();
    [GeneratedRegex(@"(?<v>\d+)\s*GB\s*(of\s*)?(VRAM|video|dedicated)", RegexOptions.IgnoreCase)] private static partial Regex Vram();
    [GeneratedRegex(@"(?<v>\d+)\s*GB", RegexOptions.IgnoreCase)] private static partial Regex AnyGb();
    [GeneratedRegex(@"\bSSD\b", RegexOptions.IgnoreCase)] private static partial Regex Ssd();
    [GeneratedRegex(@"(?<v>[\d.,]+)\s*GHz", RegexOptions.IgnoreCase)] private static partial Regex Ghz();
    [GeneratedRegex(@"\b(dual|2)[- ]?core", RegexOptions.IgnoreCase)] private static partial Regex Dual();
    [GeneratedRegex(@"\b(quad|4)[- ]?core", RegexOptions.IgnoreCase)] private static partial Regex Quad();
    [GeneratedRegex(@"\b(hexa|six|6)[- ]?core", RegexOptions.IgnoreCase)] private static partial Regex Hexa();

    /// <summary>Partes de um campo como "Intel Core i5-8400 / AMD Ryzen 3 3300X" ou "GTX 1060 or RX 580".</summary>
    private static IEnumerable<string> Alternatives(string field) =>
        Regex.Split(field, @"\s*(?:/|\bor\b|\bou\b|,|\|)\s*", RegexOptions.IgnoreCase).Select(s => s.Trim()).Where(s => s.Length > 1);

    public static string PlainText(string html) =>
        WebUtility.HtmlDecode(Tags().Replace(html.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("</li>", "\n"), " "))
            .Replace(' ', ' ');

    /// <summary>Um nível (Mínimo ou Recomendado) a partir do HTML da Steam. Null se não houver nada útil.</summary>
    public static ReqTier? ParseTier(string? html, string name, string label)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = PlainText(html);
        var cpuField = CpuLine().Match(text) is { Success: true } c ? c.Groups["v"].Value : "";
        var gpuField = GpuLine().Match(text) is { Success: true } g ? g.Groups["v"].Value : "";

        var cpus = Alternatives(cpuField).Where(p => HardwareIndex.CpuByName(p) is not null).ToArray();
        int? cpuGeneric = null;
        if (cpus.Length == 0 && cpuField.Length > 0)
        {
            // "3.0 GHz Quad Core" etc.: índice aproximado por clock (arquitetura antiga assumida) e núcleos
            var ghz = Ghz().Match(cpuField) is { Success: true } m && double.TryParse(m.Groups["v"].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var threads = Hexa().IsMatch(cpuField) ? 6 : Quad().IsMatch(cpuField) ? 4 : Dual().IsMatch(cpuField) ? 2 : 4;
            if (ghz > 0) cpuGeneric = (int)Math.Round(0.8 * ghz / 3.9 * 100 * HardwareIndex.ThreadFactor(threads));
        }
        var gpus = Alternatives(gpuField).Where(p => HardwareIndex.Gpu(p) is not null).ToArray();

        double ram = RamLine().Match(text) is { Success: true } r && double.TryParse(r.Groups["v"].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rv)
            ? (r.Groups["u"].Value.Equals("MB", StringComparison.OrdinalIgnoreCase) ? rv / 1024 : rv) : 0;
        double vram = Vram().Match(text) is { Success: true } vr ? double.Parse(vr.Groups["v"].Value)
            : gpus.Length > 0 && AnyGb().Match(gpuField) is { Success: true } vg ? double.Parse(vg.Groups["v"].Value) : 0;
        if (vram > 24) vram = 0; // pegou outro número

        if (cpus.Length == 0 && cpuGeneric is null && gpus.Length == 0 && ram == 0) return null;
        return new ReqTier(name, label, cpus, gpus, ram, vram, CpuGeneric: cpuGeneric, Ssd: Ssd().IsMatch(text) && Regex.IsMatch(text, @"(Storage|Hard Drive)\s*:[^\n]*SSD", RegexOptions.IgnoreCase));
    }

    public static GameRequirements? Parse(long appId, string name, string? minimumHtml, string? recommendedHtml)
    {
        var tiers = new[] { ParseTier(minimumHtml, "Mínimo", "requisito mínimo da Steam"), ParseTier(recommendedHtml, "Recomendado", "requisito recomendado da Steam") }
            .OfType<ReqTier>().ToList();
        if (tiers.Count == 0) return null;
        var unknown = new List<string>();
        foreach (var (html, _) in new[] { (minimumHtml, 0), (recommendedHtml, 1) })
        {
            if (string.IsNullOrWhiteSpace(html)) continue;
            var t = PlainText(html);
            if (CpuLine().Match(t) is { Success: true } c && !Alternatives(c.Groups["v"].Value).Any(p => HardwareIndex.CpuByName(p) is not null) && !Ghz().IsMatch(c.Groups["v"].Value))
                unknown.Add("processador");
            if (GpuLine().Match(t) is { Success: true } g && !Alternatives(g.Groups["v"].Value).Any(p => HardwareIndex.Gpu(p) is not null))
                unknown.Add("placa de vídeo");
        }
        var note = unknown.Count == 0 ? "Requisitos lidos da página do jogo na Steam."
            : $"Requisitos lidos da Steam; não reconheci o modelo de {string.Join(" e ", unknown.Distinct())} citado, então essa parte não foi comparada.";
        return new GameRequirements($"steam:{appId}", name, $"steam{appId}", $"https://store.steampowered.com/app/{appId}/", tiers, note);
    }
}
