using System.Globalization;
using Snow.Core.Hardware;
using Snow.Core.Health;
using Snow.Core.Monitor;
using Snow.Core.Tweaks;

namespace Snow.Core.Games;

// ─────────────────────────────────────────── ROCKET LEAGUE ───────────────────────────────────────────

/// <summary>
/// Rocket League (Unreal Engine 3, Easy Anti-Cheat). Configuração local em
/// Documentos\My Games\Rocket League\TAGame\Config\TASystemSettings.ini, seção [SystemSettings].
/// Por segurança só altera chaves que JÁ existem no arquivo (o jogo cria todas na primeira execução);
/// assim uma versão futura com nomes diferentes não ganha chaves inventadas.
/// </summary>
public sealed class RocketLeagueProfile : IGameProfile
{
    public const string Id = "epic:sugar";           // nome interno do Rocket League na Epic
    public const string SteamId = "steam:252950";
    public string GameId => Id;
    public IReadOnlyList<string> Aliases => [SteamId];
    public GameSupport Support => GameSupport.Editavel;
    public IReadOnlyList<string> Processes { get; } = ["RocketLeague"];
    private const string Sys = "SystemSettings";

    private string? _path;
    public static string ConfigPath(string documents) =>
        Path.Combine(documents, "My Games", "Rocket League", "TAGame", "Config", "TASystemSettings.ini");
    public RocketLeagueProfile WithConfigPath(string path) { _path = path; return this; }
    public IReadOnlyList<string> ConfigFiles(InstalledGame game) => [_path ??= ConfigPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))];

    public IReadOnlyList<GameFinding> Analyze(GameContext ctx)
    {
        var list = new List<GameFinding>();
        if (ctx.Text(ConfigFiles(ctx.Game)[0]) is not { } text)
        {
            list.Add(new(FindingLevel.Info, "Configuração ainda não criada", "Abra o Rocket League uma vez e feche para ele criar o TASystemSettings.ini."));
            return list;
        }
        var ini = IniDocument.Parse(text);
        if (ini.Get(Sys, "UseVsync") is "True")
            list.Add(new(FindingLevel.Importante, "VSync ligado", "No Rocket League o atraso do VSync atrapalha o tempo de toque na bola. Desligue e limite o FPS no menu (Vídeo → Taxa de quadros).", true));
        var heavy = new[] { ("MotionBlur", "Desfoque de movimento"), ("DepthOfField", "Profundidade de campo"), ("Bloom", "Bloom"), ("LensFlares", "Reflexo de lente"), ("AmbientOcclusion", "Oclusão de ambiente") }
            .Where(k => ini.Get(Sys, k.Item1) is "True").Select(k => k.Item2).ToList();
        if (heavy.Count > 0)
            list.Add(new(FindingLevel.Melhoria, $"Efeitos ligados: {string.Join(", ", heavy)}", "Pesam na GPU e poluem a imagem sem ganho competitivo.", true));
        if (ini.Get(Sys, "DynamicShadows") is "True" && ctx.Tier <= HardwareTier.Intermediario)
            list.Add(new(FindingLevel.Info, "Sombras dinâmicas ligadas", "Ajudam a ler a altura da bola, mas custam FPS. A predefinição FPS Alto desliga; a FPS Médio mantém.", true));
        if (list.Count == 0)
            list.Add(new(FindingLevel.Ok, "Configuração já leve", "VSync e efeitos pesados desligados."));
        return list;
    }

    public GamePresetPlan BuildPreset(GameContext ctx, GamePreset preset)
    {
        var path = ConfigFiles(ctx.Game)[0];
        var ini = ctx.Text(path) is { } t ? IniDocument.Parse(t) : null;
        var wanted = new List<(string key, string value, string label, string why)>
        {
            ("UseVsync", "False", "VSync", "Remove o atraso de entrada"),
            ("MotionBlur", "False", "Desfoque de movimento", "Imagem nítida em movimento"),
            ("DepthOfField", "False", "Profundidade de campo", "Sem desfoque no fundo"),
            ("LensFlares", "False", "Reflexo de lente", "Menos poluição visual"),
            ("Bloom", "False", "Bloom", "Menos brilho estourado"),
            ("AmbientOcclusion", "False", "Oclusão de ambiente", "Custa GPU, pouco ganho visual"),
        };
        if (preset == GamePreset.FpsAlto)
        {
            wanted.Add(("DynamicShadows", "False", "Sombras dinâmicas", "Maior FPS (você perde a sombra da bola)"));
            wanted.Add(("LightShafts", "False", "Raios de luz", "Efeito pesado"));
        }
        // Só chaves que o jogo já gravou
        var present = ini is null ? [] : wanted.Where(w => ini.Get(Sys, w.key) is not null).ToList();
        var changes = present.Select(w => new SettingChange(w.label, ini!.Get(Sys, w.key), w.value, w.why)).ToList();
        var actions = present.Select(w => (TweakAction)new IniValueAction(path, Sys, w.key, w.value, Processes)).ToList();
        var steps = new List<GuidedStep>
        {
            new("Configurações → Vídeo", "Taxa de quadros", ctx.RefreshHz > 0 ? $"{(ctx.RefreshHz >= 144 ? ctx.RefreshHz : 120)}" : "Taxa do monitor", "Limite estável (o limite do RL fica no menu)"),
            new("Configurações → Vídeo", "Modo de exibição", "Tela cheia", "Menor latência que janela sem bordas"),
        };
        TweakDefinition[] tweaks = actions.Count == 0 ? [] : [new TweakDefinition
        {
            Id = $"game.rocketleague.{preset.ToString().ToLowerInvariant()}",
            Title = $"Rocket League: {preset.Label()}",
            Description = "Ajusta o TASystemSettings.ini (mesmas opções do menu de vídeo).",
            HonestNote = AntiCheatPolicy.Summary,
            Category = TweakCategory.Graficos, Impact = Impact.Medio, Actions = actions,
        }];
        return new(preset, changes, tweaks, steps, $"Rocket League · {preset.Label()} · hardware {ctx.Tier.Label()}");
    }
}

// ─────────────────────────────────── UNREAL ENGINE (QUALQUER JOGO) ───────────────────────────────────

/// <summary>
/// Jogos Unreal Engine 4/5 guardam vídeo em %LOCALAPPDATA%\&lt;Projeto&gt;\Saved\Config\Windows*\GameUserSettings.ini
/// com as mesmas chaves do Fortnite. Para jogos sem perfil próprio o Snow só lê (diagnóstico): cada jogo
/// pode sobrescrever o arquivo ou sincronizar com a conta, então a mudança é feita no menu.
/// </summary>
public static class UnrealConfigAdvisor
{
    private const string Main = "/Script/Engine.GameUserSettings";

    public static IReadOnlyList<GameFinding> Analyze(string iniText, int refreshHz)
    {
        var ini = IniDocument.Parse(iniText);
        // as chaves ficam na seção do jogo (ex.: /Script/ShooterGame.ShooterGameUserSettings) ou na da Engine
        string? Get(string key) => ini.Get(Main, key) ?? ini.Sections.Where(s => s.EndsWith("UserSettings", StringComparison.OrdinalIgnoreCase)).Select(s => ini.Get(s, key)).FirstOrDefault(v => v is not null);
        var list = new List<GameFinding> { new(FindingLevel.Info, "Unreal Engine: configuração lida", "O Snow leu o GameUserSettings.ini do jogo. Ajuste os pontos abaixo no menu de vídeo dele.") };
        if (Get("bUseVSync") is "True")
            list.Add(new(FindingLevel.Importante, "VSync ligado", "Adiciona atraso de entrada. Desligue no menu de vídeo e use um limite de FPS."));
        if (double.TryParse(Get("FrameRateLimit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) && refreshHz > 0)
        {
            if (cap == 0) list.Add(new(FindingLevel.Info, "FPS sem limite", $"Limitar em {refreshHz} (seu monitor) deixa o frametime mais estável."));
            else if (cap + 1 < refreshHz) list.Add(new(FindingLevel.Melhoria, $"Limite de FPS ({cap:0}) abaixo do monitor ({refreshHz} Hz)", "Suba o limite para a taxa do monitor se o PC aguentar."));
        }
        if (Get("bUseDynamicResolution") is "True")
            list.Add(new(FindingLevel.Info, "Resolução dinâmica ligada", "A nitidez varia durante a partida."));
        if (double.TryParse(Get("sg.ResolutionQuality") ?? ini.Get("ScalabilityGroups", "sg.ResolutionQuality"), NumberStyles.Float, CultureInfo.InvariantCulture, out var res) && res is > 0 and < 75)
            list.Add(new(FindingLevel.Melhoria, $"Resolução 3D em {res:0}%", "Imagem borrada. Se a GPU tiver folga (veja no Desempenho), suba para 100%."));
        return list;
    }
}

// ─────────────────────────────────── DADOS REAIS DO SNOW MONITOR ───────────────────────────────────

public static class MeasuredInsight
{
    /// <summary>Lê um arquivo "&lt;jogo&gt;-uso-*.csv" do Monitor (Hora,CPU,Nucleo,GPU,VRAM_GB,RAM,FPS,Low1,Gargalo).</summary>
    public static SessionSummary? FromUsageCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return null;
        var head = lines[0].Split(',');
        int Col(string n) => Array.FindIndex(head, h => h.Equals(n, StringComparison.OrdinalIgnoreCase));
        int cCpu = Col("CPU"), cCore = Col("Nucleo"), cGpu = Col("GPU"), cRam = Col("RAM"), cFps = Col("FPS"), cLow = Col("Low1"), cB = Col("Gargalo");
        if (cGpu < 0 || cFps < 0) return null;
        static double? D(string[] c, int i) => i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var samples = new List<UsageSample>();
        var recorded = new List<Bottleneck>();
        foreach (var l in lines.Skip(1))
        {
            var c = l.Split(',');
            if (D(c, cFps) is not { } fps) continue; // só segundos com o jogo rodando
            samples.Add(new UsageSample(DateTime.MinValue, D(c, cCpu), D(c, cCore), D(c, cGpu), null, D(c, cRam) ?? 0, fps, D(c, cLow)));
            if (cB >= 0 && cB < c.Length && Enum.TryParse<Bottleneck>(c[cB], out var b)) recorded.Add(b);
        }
        if (samples.Count < 30) return null;
        // usa o gargalo que o Monitor gravou (ele conhecia o limite de FPS na hora)
        if (recorded.Count == samples.Count)
        {
            var share = Enum.GetValues<Bottleneck>().ToDictionary(b => b, b => Math.Round(100.0 * recorded.Count(x => x == b) / recorded.Count, 1));
            var main = share.OrderByDescending(kv => kv.Value).First().Key;
            return new SessionSummary(null, share, main, samples.Average(s => s.CpuPercent), samples.Average(s => s.GpuPercent));
        }
        return SessionSummary.From(samples);
    }

    public static GameFinding? Finding(SessionSummary s, DateTime when, IReadOnlyList<double> fps)
    {
        if (fps.Count == 0) return null;
        var avg = fps.Average();
        var sorted = fps.OrderBy(x => x).ToList();
        var p5 = sorted[(int)(sorted.Count * 0.05)];
        var cpu = s.TimeShare.GetValueOrDefault(Bottleneck.Cpu);
        var gpu = s.TimeShare.GetValueOrDefault(Bottleneck.Gpu);
        var head = $"Última medição ({when:dd/MM HH:mm}): {avg:0} FPS em média, piores momentos ~{p5:0}";
        return s.Main switch
        {
            Bottleneck.Cpu => new(FindingLevel.Melhoria, head,
                $"{cpu:0}% do tempo preso no processador, com a GPU em ~{s.AvgGpu:0}%. Baixar gráficos quase não muda o FPS aqui; o ganho vem de processador/memória (veja Desempenho → Upgrade sem gargalo). Um limite de FPS perto da média deixa o jogo mais estável."),
            Bottleneck.Gpu => new(FindingLevel.Melhoria, head,
                $"{gpu:0}% do tempo com a placa de vídeo no limite. Aqui sim baixar resolução 3D, sombras e efeitos sobe o FPS."),
            Bottleneck.LimiteFps => new(FindingLevel.Ok, head, "O jogo passou a maior parte do tempo no limite de FPS: há folga de hardware."),
            _ => new(FindingLevel.Info, head, "Sem um gargalo dominante na medição."),
        };
    }

    public static IReadOnlyList<double> FpsColumn(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return [];
        var i = Array.FindIndex(lines[0].Split(','), h => h.Equals("FPS", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return [];
        return lines.Skip(1).Select(l => l.Split(',')).Where(c => c.Length > i && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    .Select(c => double.Parse(c[i], CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>Jogo instalado num disco com erros SMART ou alerta.</summary>
    public static GameFinding? DiskHealth(InstalledGame game, HardwareProfile hw, IReadOnlyList<DiskHealthData>? disks)
    {
        if (disks is null || game.DriveLetter.Length == 0) return null;
        var disk = hw.Storage.DiskForDrive(game.DriveLetter);
        var h = disks.FirstOrDefault(d => disk is not null && d.Name.Equals(disk.Model, StringComparison.OrdinalIgnoreCase));
        if (h is null) return null;
        var errors = (h.ReadErrorsUncorrected ?? 0) + (h.WriteErrorsUncorrected ?? 0);
        if (errors == 0 && h.HealthStatus is not (1 or 2)) return null;
        return new(FindingLevel.Importante, $"Instalado num disco com problema ({game.DriveLetter}:)",
            $"{(errors > 0 ? $"O disco tem {errors} erro(s) de leitura não corrigidos" : "O SMART do disco está em alerta")}. Ao carregar mapas e texturas o jogo pode travar por instantes. Se couber, reinstale o jogo em outro disco e faça backup do que importa nesse.");
    }

    /// <summary>Chave do jogo nos arquivos do Monitor (fortnite-uso-*.csv…).</summary>
    public static string? MonitorKey(InstalledGame g) => g.Id switch
    {
        FortniteProfile.Id => "fortnite",
        LeagueProfile.Id => "lol",
        ValorantProfile.Id => "valorant",
        RocketLeagueProfile.Id or RocketLeagueProfile.SteamId => "rocketleague",
        "steam:291550" => "brawlhalla",
        "steam:730" => "cs2",
        _ when g.ExecutablePath is { } exe => ActiveGameDetector.KeyFor(Path.GetFileName(exe)),
        _ => null,
    };
}
