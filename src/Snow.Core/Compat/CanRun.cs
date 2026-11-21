using Snow.Core.Hardware;

namespace Snow.Core.Compat;

/// <summary>Um nível oficial de requisitos (mínimo, recomendado, alto…).</summary>
/// <param name="Label">O que o fabricante promete nesse nível (ex.: "1080p Baixo · 30 FPS").</param>
/// <param name="Cpus">Processadores citados (o índice usado é o MENOR entre eles: Intel e AMD costumam estar empatados).</param>
/// <param name="Gpus">Placas citadas (idem).</param>
/// <param name="CpuGeneric">Quando o requisito não cita modelo ("4 núcleos", "2,5 GHz dual core"): índice equivalente.</param>
public sealed record ReqTier(
    string Name, string Label,
    string[] Cpus, string[] Gpus,
    double RamGb, double VramGb = 0,
    int? CpuGeneric = null, int? GpuGeneric = null, bool Ssd = false)
{
    public int? CpuIndex => Cpus.Select(HardwareIndex.CpuByName).Where(i => i.HasValue).Select(i => i!.Value).DefaultIfEmpty(CpuGeneric ?? -1).Min() is var v && v >= 0 ? v : CpuGeneric;
    public int? GpuIndex => Gpus.Select(HardwareIndex.Gpu).Where(i => i.HasValue).Select(i => i!.Value).DefaultIfEmpty(GpuGeneric ?? -1).Min() is var v && v >= 0 ? v : GpuGeneric;
}

public sealed record GameRequirements(string Id, string Name, string MonitorKey, string Source, IReadOnlyList<ReqTier> Tiers, string? Note = null);

public enum CanRunLevel { Abaixo, Minimo, Recomendado, Alto }

public sealed record CanRunResult(
    GameRequirements Game, CanRunLevel Level, ReqTier? Reached, ReqTier? Next,
    string Verdict, string Detail, IReadOnlyList<string> Missing, double? Headroom);

/// <summary>
/// "Roda no meu PC?": compara o hardware com os requisitos oficiais (fontes em cada jogo).
/// Requisitos conferidos nos sites oficiais em 21/11/2026.
/// </summary>
public static class CanRun
{
    public static readonly IReadOnlyList<GameRequirements> Catalog =
    [
        new("fortnite", "Fortnite", "fortnite", "https://www.epicgames.com/help/en-US/c-202300000001636/a202300000012731",
        [
            new("Mínimo", "Modo Performance · baixo", ["Core i3-3225"], ["Intel HD 4000", "Radeon Vega 8"], 8),
            new("Recomendado", "DirectX 12 · médio/alto", ["Core i5-7300U", "Ryzen 3 3300U"], ["GTX 960", "R9 280"], 16, 2),
            new("Épico", "Predefinição Épico", ["Core i7-8700", "Ryzen 7 3700X"], ["RTX 3070", "RX 6700 XT"], 16, 8),
        ], "No modo Performance o Fortnite roda em quase tudo; o limite de FPS alto depende do processador."),
        new("lol", "League of Legends", "lol", "https://support.riotgames.com/en-us/league-of-legends/performance/minimum-and-recommended-system-requirements-league-of-legends",
        [
            new("Mínimo", "Baixo · 1024×768", ["Core i3-530", "FX-4100"], ["GTX 460", "HD 6570", "HD Graphics 4600"], 2, 1),
            new("Recomendado", "Alto · 1080p", ["Core i5-8250U", "Ryzen 3 1200"], ["GTX 560", "HD 6950", "UHD Graphics 630"], 4, 2),
        ], "Desde o patch 25.21 exige placa com DirectX 11 (feature level 11_0)."),
        new("valorant", "VALORANT", "valorant", "https://support.riotgames.com/en-us/valorant/support-tools/minimum-recommended-pc-specs",
        [
            new("Mínimo", "30 FPS", ["Core i3-540", "Athlon 200GE"], ["Intel HD 4000", "Radeon R5 220"], 4, 1),
            new("Recomendado", "60 FPS", ["Core i3-4150", "Ryzen 3 1200"], ["GT 730", "R7 240"], 4, 1),
            new("Alto", "144+ FPS", ["Core i5-9400F", "Ryzen 5 2600X"], ["GTX 1050 Ti", "R7 370", "Arc A310"], 4, 1),
        ], "No Windows 11 o Vanguard exige TPM 2.0 e Secure Boot."),
        new("cs2", "Counter-Strike 2", "cs2", "https://store.steampowered.com/app/730/",
        [
            new("Mínimo", "A Valve só publica o mínimo", ["Core i5-750"], [], 8, 1, GpuGeneric: 25),
        ], "Sem requisito recomendado oficial: para 144+ FPS o que mais pesa é o processador."),
        new("rocketleague", "Rocket League", "rocketleague", "https://www.epicgames.com/help/en-US/c-Category_RocketLeague/a000084383",
        [
            new("Mínimo", "Baixo", [], ["GTX 760", "R9 270X"], 4, CpuGeneric: 50),
            new("Recomendado", "Alto", [], ["GTX 1060", "RX 470"], 8, CpuGeneric: 70),
        ], "Requisito de CPU genérico (2,5 GHz dual core / 3,0 GHz quad core)."),
        new("apex", "Apex Legends", "apex", "https://www.ea.com/games/apex-legends/about/pc-system-requirements",
        [
            new("Mínimo", "Baixo", ["Core i3-6300", "FX-4350"], ["GTX 950", "HD 7790"], 6, 2),
            new("Recomendado", "60 FPS", ["Core i5-3570K", "Ryzen 5 1600"], ["GTX 970", "R9 290"], 8),
        ]),
        new("marvelrivals", "Marvel Rivals", "marvelrivals", "https://store.steampowered.com/app/2767030/",
        [
            new("Mínimo", "Baixo", ["Core i5-6600K", "Ryzen 5 1600X"], ["GTX 1060", "RX 580", "Arc A380"], 16),
            new("Recomendado", "Médio/alto", ["Core i5-10400", "Ryzen 5 5600X"], ["RTX 2060 Super", "RX 5700 XT", "Arc A750"], 16),
        ]),
        new("eafc26", "EA SPORTS FC 26", "fc26", "https://www.ea.com/games/ea-sports-fc/fc-26/news/pitch-notes-fc26-pc-deep-dive",
        [
            new("Mínimo", "720p · 60 FPS · baixo", ["Core i5-6600K", "Ryzen 5 1600"], ["GTX 1050 Ti", "RX 570"], 8, 4),
            new("Recomendado", "1080p · 60 FPS · médio", ["Core i7-6700", "Ryzen 7 2700X"], ["GTX 1660", "RX 5600 XT"], 12, 6),
        ]),
        new("pubg", "PUBG: Battlegrounds", "pubg", "https://store.steampowered.com/app/578080/",
        [
            new("Mínimo", "Baixo", ["Core i5-4430", "FX-6300"], ["GTX 960", "R7 370"], 8, 2),
            new("Recomendado", "Médio/alto", ["Core i5-6600K", "Ryzen 5 1600"], ["GTX 1060 3GB", "RX 580"], 16, 3),
        ]),
        new("overwatch2", "Overwatch 2", "overwatch", "https://support.blizzard.com/article/65159",
        [
            new("Mínimo", "Baixo · 30 FPS", [], ["GTX 660", "HD 7850"], 6, CpuGeneric: 70),
            new("Recomendado", "Médio/alto", [], ["GTX 1060", "GTX 1650", "R9 380", "RX 6400"], 8, CpuGeneric: 86),
        ], "A Blizzard cita só \"Core i3 / Core i7 / Ryzen 5\": o processador é uma estimativa."),
        new("minecraft", "Minecraft: Java Edition", "minecraft", "https://www.minecraft.net/en-us/article/minecraft-java-edition-system-requirements",
        [
            new("Mínimo", "1080p · 30 FPS · Rápido", [], [], 8, 2, CpuGeneric: 60),
            new("Recomendado", "1080p · 60 FPS · Detalhado", [], [], 16, 6, CpuGeneric: 90),
        ], "Requisitos de 2026: placa com Vulkan 1.3. A Mojang não cita modelos; o Snow compara memória e VRAM."),
        new("gtav", "GTA V Enhanced", "gta5", "https://support.rockstargames.com/articles/lMQXeP2Z1mN3g9oZiBZFR",
        [
            new("Mínimo", "Baixo", ["Core i7-4770", "FX-9590"], ["GTX 1630", "RX 6400"], 8, 4, Ssd: true),
            new("Recomendado", "Alto", ["Core i5-9600K", "Ryzen 5 3600"], ["RTX 3060", "RX 6600 XT"], 16, 8, Ssd: true),
        ], "O GTA V Legacy (versão antiga) continua disponível e pede bem menos."),
        new("rdr2", "Red Dead Redemption 2", "rdr2", "https://support.rockstargames.com/articles/1LqwiYbHeiSyo3dtcVyS1N",
        [
            new("Mínimo", "Baixo", ["Core i5-2500K", "FX-6300"], ["GTX 770", "R9 280"], 8, 2),
            new("Recomendado", "Alto", ["Core i7-4770K", "Ryzen 5 1500X"], ["GTX 1060", "RX 480"], 12, 4),
        ]),
        new("eldenring", "Elden Ring", "eldenring", "https://store.steampowered.com/app/1245620/",
        [
            new("Mínimo", "Baixo", ["Core i5-8400", "Ryzen 3 3300X"], ["GTX 1060 3GB", "RX 580"], 12, 3),
            new("Recomendado", "Alto", ["Core i7-8700K", "Ryzen 5 3600X"], ["GTX 1070", "Vega 56"], 16, 8),
        ]),
        new("cyberpunk", "Cyberpunk 2077", "cyberpunk", "https://support.cdprojektred.com/en/cyberpunk/pc/sp-technical/issue/1556/pc-system-requirements",
        [
            new("Mínimo", "1080p · baixo · 30 FPS", ["Core i7-6700", "Ryzen 5 1600"], ["GTX 1060", "RX 580", "Arc A380"], 12, 6, Ssd: true),
            new("Recomendado", "1080p · alto · 60 FPS", ["Core i7-12700", "Ryzen 7 7800X3D"], ["RTX 2060 Super", "RX 5700 XT", "Arc A770"], 16, 8, Ssd: true),
        ]),
    ];

    /// <summary>Folga de 5% no índice (as tabelas são aproximadas) e meio GB na RAM (16 GB aparecem como 15,9).</summary>
    private static (bool ok, List<string> missing, double ratio) Check(ReqTier t, int? cpu, int? gpu, double ramGb, double vramGb, bool hasSsd, bool integrated)
    {
        var missing = new List<string>();
        var ratios = new List<double>();
        if (t.CpuIndex is { } rc && rc > 0)
        {
            if (cpu is { } c) { ratios.Add((double)c / rc); if (c < rc * 0.95) missing.Add($"processador (precisa de algo como {Example(t.Cpus, "um processador mais rápido")})"); }
        }
        if (t.GpuIndex is { } rg && rg > 0)
        {
            if (gpu is { } g) { ratios.Add((double)g / rg); if (g < rg * 0.95) missing.Add($"placa de vídeo (precisa de algo como {Example(t.Gpus, "uma placa mais forte")})"); }
        }
        if (t.RamGb > 0 && ramGb + 0.5 < t.RamGb) missing.Add($"memória RAM ({t.RamGb:0} GB)");
        if (t.VramGb > 0 && !integrated && vramGb > 0 && vramGb + 0.25 < t.VramGb) missing.Add($"memória de vídeo ({t.VramGb:0} GB de VRAM)");
        if (t.Ssd && !hasSsd) missing.Add("SSD");
        return (missing.Count == 0, missing, ratios.Count == 0 ? 1 : ratios.Min());
    }

    private static string Example(string[] names, string fallback) => names.Length == 0 ? fallback : string.Join(" ou ", names.Take(2));

    public static CanRunResult Evaluate(GameRequirements game, HardwareProfile hw)
    {
        var gpuInfo = hw.PrimaryGpu;
        var cpu = HardwareIndex.Cpu(hw.Cpu);
        var gpu = HardwareIndex.Gpu(gpuInfo?.Name);
        var integrated = gpuInfo is { IsDiscrete: false };
        var vram = (gpuInfo?.VramBytes ?? 0) / (double)(1L << 30);
        var hasSsd = hw.Storage.Disks.Any(d => d.IsSsd);

        ReqTier? reached = null; ReqTier? next = null; List<string> missing = []; double? headroom = null; var reachedIdx = -1;
        for (var i = 0; i < game.Tiers.Count; i++)
        {
            var (ok, miss, ratio) = Check(game.Tiers[i], cpu, gpu, hw.Memory.TotalGb, vram, hasSsd, integrated);
            if (ok) { reached = game.Tiers[i]; reachedIdx = i; headroom = ratio; continue; }
            next = game.Tiers[i]; missing = miss;
            break;
        }
        var level = reached is null ? CanRunLevel.Abaixo
            : reachedIdx == 0 ? CanRunLevel.Minimo
            : reachedIdx == 1 ? CanRunLevel.Recomendado
            : CanRunLevel.Alto;
        // só o mínimo publicado (CS2): com folga grande, avisa que deve rodar bem acima disso
        if (game.Tiers.Count == 1 && level == CanRunLevel.Minimo && headroom >= 1.4) level = CanRunLevel.Recomendado;

        var verdict = level switch
        {
            CanRunLevel.Abaixo => "Abaixo do mínimo",
            CanRunLevel.Minimo => $"Roda no mínimo · {reached!.Label}",
            CanRunLevel.Recomendado => game.Tiers.Count == 1 ? "Roda com folga" : $"Roda no recomendado · {reached!.Label}",
            _ => $"Roda no {reached!.Name.ToLowerInvariant()} · {reached!.Label}",
        };
        string detail;
        if (next is null)
            detail = game.Tiers.Count == 1 ? $"Passa o mínimo oficial com folga ({headroom:0.0}× o exigido)." : "Atende todos os níveis publicados pelo fabricante.";
        else if (reached is null)
            detail = $"Falta: {string.Join(", ", missing)}. Pode até abrir, mas o fabricante não garante.";
        else
            detail = $"Para o {next.Name.ToLowerInvariant()} ({next.Label}) falta: {string.Join(", ", missing)}.";
        if (cpu is null || gpu is null)
            detail += $" Não reconheci {(cpu is null ? "o processador" : "a placa de vídeo")} na tabela do Snow: comparação parcial.";
        return new(game, level, reached, next, verdict, detail, missing, headroom);
    }

    public static IReadOnlyList<CanRunResult> EvaluateAll(HardwareProfile hw) => Catalog.Select(g => Evaluate(g, hw)).ToList();

    public static string Label(this CanRunLevel l) => l switch
    {
        CanRunLevel.Abaixo => "Não recomendado",
        CanRunLevel.Minimo => "Mínimo",
        CanRunLevel.Recomendado => "Recomendado",
        _ => "Máximo",
    };
}
