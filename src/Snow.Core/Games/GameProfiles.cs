using System.Globalization;
using Snow.Core.Hardware;
using Snow.Core.Tweaks;

namespace Snow.Core.Games;

/// <summary>
/// Política para jogos com anti-cheat (Riot Vanguard, Easy Anti-Cheat, BattlEye):
///  • o Snow nunca abre, injeta, lê memória ou altera prioridade do processo do jogo;
///  • não cria regras por executável (IFEO, compatibilidade, afinidade);
///  • só edita arquivos de configuração do usuário, e apenas com o jogo fechado;
///  • jogos cujas configurações são sincronizadas com a conta ficam somente leitura (guiado).
/// </summary>
public static class AntiCheatPolicy
{
    public const string Summary =
        "Seguro para anti-cheat: o Snow não toca no processo do jogo, não usa drivers e só edita o arquivo de configuração com o jogo fechado.";
}

/// <summary>Ação que edita uma chave de um arquivo INI/CFG de jogo (reversível pelo backup).</summary>
public sealed record IniValueAction(string FilePath, string Section, string Key, string Value, IReadOnlyList<string> BlockingProcesses) : TweakAction
{
    public override string Describe() => $"{Path.GetFileName(FilePath)} [{Section}] {Key}={Value}";
}

internal static class P
{
    public static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
    public static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
}

// ─────────────────────────────────────────── FORTNITE ───────────────────────────────────────────

public sealed class FortniteProfile : IGameProfile
{
    public const string Id = "epic:fortnite";
    public string GameId => Id;
    public GameSupport Support => GameSupport.Editavel;
    public IReadOnlyList<string> Processes { get; } = ["FortniteClient-Win64-Shipping", "FortniteLauncher"];

    private const string Main = "/Script/FortniteGame.FortGameUserSettings";
    private const string Groups = "ScalabilityGroups";
    private const string Rhi = "D3DRHIPreference";

    public static string ConfigPath(string localAppData) =>
        Path.Combine(localAppData, "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");

    private string? _path;
    public IReadOnlyList<string> ConfigFiles(InstalledGame game) => [_path ??= ConfigPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))];

    /// <summary>Para testes: aponta para um arquivo específico.</summary>
    public FortniteProfile WithConfigPath(string path) { _path = path; return this; }

    public IReadOnlyList<GameFinding> Analyze(GameContext ctx)
    {
        var path = ConfigFiles(ctx.Game)[0];
        var list = new List<GameFinding>();
        var text = ctx.Text(path);
        if (text is null)
        {
            list.Add(new(FindingLevel.Info, "Configuração ainda não criada", "Abra o Fortnite uma vez e feche: o jogo cria o GameUserSettings.ini na primeira execução."));
            return list;
        }
        var ini = IniDocument.Parse(text);
        if (ctx.ReadOnlyFiles.Contains(path))
            list.Add(new(FindingLevel.Importante, "Arquivo de configuração somente leitura",
                "A Epic avisa que, assim, o jogo não salva mudanças (inclusive o modo Performance). Desmarque 'Somente leitura' nas propriedades do arquivo."));

        var perfMode = string.Equals(ini.Get(Rhi, "PreferredFeatureLevel"), "es31", StringComparison.OrdinalIgnoreCase);
        list.Add(perfMode
            ? new(FindingLevel.Ok, "Modo de renderização: Performance", "O modo mais leve do Fortnite, recomendado pela Epic para PCs modestos e para FPS máximo.")
            : new(ctx.Tier <= HardwareTier.Intermediario ? FindingLevel.Melhoria : FindingLevel.Info, "Modo de renderização: DirectX",
                ctx.Tier <= HardwareTier.Intermediario
                    ? "Para o seu hardware, o modo Performance costuma render muito mais FPS. A predefinição FPS Alto ativa."
                    : "DirectX 12 oferece melhor visual; para FPS máximo, o modo Performance é mais leve.", true));

        if (ini.Get(Main, "bUseVSync") is "True")
            list.Add(new(FindingLevel.Importante, "VSync ligado", "Sincronização vertical adiciona atraso de entrada (input lag). Com um limite de FPS na taxa do monitor você evita o tearing sem esse custo.", true));

        if (double.TryParse(ini.Get(Main, "FrameRateLimit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && ctx.RefreshHz > 0)
        {
            if (limit == 0)
                list.Add(new(FindingLevel.Info, "FPS sem limite", $"Sem limite, o FPS oscila mais (frametime irregular). Limitar em {ctx.RefreshHz} (taxa do seu monitor) estabiliza.", true));
            else if (limit + 1 < ctx.RefreshHz)
                list.Add(new(FindingLevel.Melhoria, $"Limite de FPS ({limit:0}) abaixo do monitor ({ctx.RefreshHz} Hz)", "Você está deixando quadros na mesa: suba o limite para a taxa do monitor.", true));
            else
                list.Add(new(FindingLevel.Ok, $"Limite de FPS em {limit:0}", $"Compatível com o monitor de {ctx.RefreshHz} Hz."));
        }

        if (double.TryParse(ini.Get(Groups, "sg.ResolutionQuality"), NumberStyles.Float, CultureInfo.InvariantCulture, out var res) && res < 75)
            list.Add(new(FindingLevel.Melhoria, $"Resolução 3D em {res:0}%",
                "A cena é renderizada em resolução bem menor que a da tela, deixando a imagem borrada e dificultando ver inimigos ao longe. A predefinição ajusta conforme seu hardware.", true));

        if (ini.Get(Main, "bUseDynamicResolution") is "True")
            list.Add(new(FindingLevel.Info, "Resolução dinâmica ativa", "A nitidez varia durante a partida. Em jogo competitivo, uma resolução fixa dá imagem consistente.", true));

        var nvidia = ctx.Hardware.PrimaryGpu?.Vendor == GpuVendor.Nvidia;
        if (nvidia && ini.Get(Main, "LowInputLatencyModeIsEnabled") is "False")
            list.Add(new(FindingLevel.Melhoria, "NVIDIA Reflex desligado", "O Reflex reduz a latência do sistema em GPUs NVIDIA.", true));

        if (ini.Get(Main, "bMotionBlur") is "True")
            list.Add(new(FindingLevel.Info, "Desfoque de movimento ligado", "Atrapalha a leitura de movimento rápido.", true));
        return list;
    }

    public GamePresetPlan BuildPreset(GameContext ctx, GamePreset preset)
    {
        var path = ConfigFiles(ctx.Game)[0];
        var ini = ctx.Text(path) is { } t ? IniDocument.Parse(t) : null;
        var tier = ctx.Tier;
        var hz = ctx.RefreshHz > 0 ? ctx.RefreshHz : 0;
        var changes = new List<(string section, string key, string value, string label, string why)>();
        void C(string s, string k, string v, string label, string why) => changes.Add((s, k, v, label, why));

        if (preset == GamePreset.FpsAlto)
        {
            C(Rhi, "PreferredFeatureLevel", "es31", "Modo de renderização", "Performance: o modo mais leve do jogo");
            C(Groups, "sg.ResolutionQuality", tier == HardwareTier.Entrada ? "75" : "100", "Resolução 3D (%)", tier == HardwareTier.Entrada ? "75% equilibra nitidez e FPS no seu hardware" : "100%: nitidez total, custo baixo no modo Performance");
            C(Groups, "sg.ViewDistanceQuality", tier == HardwareTier.Entrada ? "1" : "2", "Distância de visão", "Mantém jogadores e construções visíveis de longe");
            foreach (var k in new[] { "sg.ShadowQuality", "sg.GlobalIlluminationQuality", "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.EffectsQuality", "sg.FoliageQuality", "sg.AntiAliasingQuality" })
                C(Groups, k, "0", Label(k), "Mínimo: maior FPS e menos poluição visual");
            C(Groups, "sg.TextureQuality", tier >= HardwareTier.Avancado ? "2" : tier == HardwareTier.Intermediario ? "1" : "0", "Texturas", "Conforme a VRAM da sua GPU");
            C(Main, "bShowGrass", "False", "Grama", "Menos objetos na tela");
        }
        else
        {
            var q = tier switch { HardwareTier.Entrada => 1, HardwareTier.Intermediario => 2, HardwareTier.Avancado => 3, _ => 3 };
            if (tier == HardwareTier.Entrada) C(Rhi, "PreferredFeatureLevel", "es31", "Modo de renderização", "Performance: no seu hardware, o DirectX 12 pesa demais");
            C(Groups, "sg.ResolutionQuality", "100", "Resolução 3D (%)", "Imagem nítida na resolução da tela");
            C(Groups, "sg.ViewDistanceQuality", P.Clamp(q, 1, 3).ToString(), "Distância de visão", "Visual equilibrado");
            C(Groups, "sg.ShadowQuality", P.Clamp(q - 1, 0, 2).ToString(), "Sombras", "Sombras custam caro; nível moderado");
            C(Groups, "sg.TextureQuality", P.Clamp(q, 0, 3).ToString(), "Texturas", "Conforme a VRAM da sua GPU");
            C(Groups, "sg.EffectsQuality", P.Clamp(q - 1, 0, 3).ToString(), "Efeitos", "Explosões e partículas moderadas");
            C(Groups, "sg.PostProcessQuality", P.Clamp(q - 1, 0, 3).ToString(), "Pós-processamento", "Bloom e afins moderados");
            C(Groups, "sg.FoliageQuality", P.Clamp(q - 1, 0, 3).ToString(), "Vegetação", "Nível moderado");
        }

        // Comum aos dois perfis: latência e estabilidade
        C(Main, "bUseVSync", "False", "VSync", "Remove o input lag do VSync");
        C(Main, "bUseDynamicResolution", "False", "Resolução dinâmica", "Imagem consistente");
        C(Main, "bMotionBlur", "False", "Desfoque de movimento", "Movimento mais legível");
        if (hz > 0) C(Main, "FrameRateLimit", P.F(hz), "Limite de FPS", $"Igual ao monitor ({hz} Hz): frametime estável");
        if (ctx.Hardware.PrimaryGpu?.Vendor == GpuVendor.Nvidia)
            C(Main, "LowInputLatencyModeIsEnabled", "True", "NVIDIA Reflex", "Menor latência em GPUs NVIDIA");

        var setting = changes.Select(c => new SettingChange(c.label, ini?.Get(c.section, c.key), c.value, c.why))
                             .ToList();
        var actions = changes.Select(c => (TweakAction)new IniValueAction(path, c.section, c.key, c.value, Processes)).ToList();
        var steps = new List<GuidedStep>();
        if (changes.Any(c => c.key == "PreferredFeatureLevel"))
            steps.Add(new("Epic Games Launcher", "Se o modo não persistir", "-d3d12 -FeatureLevelEs31", "Solução oficial da Epic: argumentos de linha de comando em Configurações → Fortnite."));

        return new(preset, setting, [Tweak(ctx, preset, actions)], steps,
            $"Fortnite · {preset.Label()} · hardware {tier.Label()}{(hz > 0 ? $" · monitor {hz} Hz" : "")}");
    }

    private TweakDefinition Tweak(GameContext ctx, GamePreset preset, IReadOnlyList<TweakAction> actions) => new()
    {
        Id = $"game.fortnite.{preset.ToString().ToLowerInvariant()}",
        Title = $"Fortnite: {preset.Label()}",
        Description = "Ajusta o GameUserSettings.ini do Fortnite (mesmas opções do menu de vídeo).",
        HonestNote = AntiCheatPolicy.Summary,
        Category = TweakCategory.Graficos,
        Impact = Impact.Alto,
        Actions = actions,
    };

    private static string Label(string key) => key switch
    {
        "sg.ShadowQuality" => "Sombras",
        "sg.GlobalIlluminationQuality" => "Iluminação global",
        "sg.ReflectionQuality" => "Reflexos",
        "sg.PostProcessQuality" => "Pós-processamento",
        "sg.EffectsQuality" => "Efeitos",
        "sg.FoliageQuality" => "Vegetação",
        "sg.AntiAliasingQuality" => "Anti-aliasing",
        _ => key,
    };
}

// ─────────────────────────────────────── LEAGUE OF LEGENDS ───────────────────────────────────────

public sealed class LeagueProfile : IGameProfile
{
    public const string Id = "riot:league_of_legends";
    public string GameId => Id;
    public GameSupport Support => GameSupport.Editavel;
    // Fechar também o client evita que ele regrave o game.cfg.
    public IReadOnlyList<string> Processes { get; } = ["League of Legends", "LeagueClient", "LeagueClientUx"];

    public IReadOnlyList<string> ConfigFiles(InstalledGame game) => [Path.Combine(game.InstallDir, "Config", "game.cfg"), Path.Combine(game.InstallDir, "Config", "PersistedSettings.json")];

    // Só chaves gráficas do [Performance]/[General] que o próprio jogo mantém locais (não estão no PersistedSettings).
    private static readonly string[] Qualities = ["CharacterQuality", "EnvironmentQuality", "EffectsQuality", "ShadowQuality"];

    public IReadOnlyList<GameFinding> Analyze(GameContext ctx)
    {
        var cfg = ConfigFiles(ctx.Game)[0];
        var list = new List<GameFinding>();
        if (ctx.Text(cfg) is not { } text)
        {
            list.Add(new(FindingLevel.Info, "game.cfg não encontrado", "Jogue uma partida (ou abra o treino) e feche para o LoL criar o arquivo."));
            return list;
        }
        var ini = IniDocument.Parse(text);
        if (ctx.ReadOnlyFiles.Contains(cfg))
            list.Add(new(FindingLevel.Importante, "game.cfg somente leitura", "O jogo não consegue salvar mudanças feitas no menu. Desmarque 'Somente leitura'."));
        if (ini.Get("General", "WaitForVerticalSync") is "1")
            list.Add(new(FindingLevel.Importante, "VSync ligado", "Adiciona atraso entre o clique e a ação. Desligue e limite o FPS no menu do jogo.", true));
        if (ini.Get("Performance", "ShadowQuality") is { } s && int.TryParse(s, out var sq) && sq >= 2 && ctx.Tier <= HardwareTier.Intermediario)
            list.Add(new(FindingLevel.Melhoria, "Sombras altas para o seu hardware", "Sombras são a opção mais pesada do LoL em lutas de equipe.", true));
        list.Add(new(FindingLevel.Info, "Limite de FPS", ctx.RefreshHz > 0
            ? $"Defina no menu do jogo (Vídeo → Limite de taxa de quadros) o valor mais próximo de {ctx.RefreshHz} FPS, a taxa do seu monitor."
            : "Defina no menu do jogo um limite igual à taxa do seu monitor."));
        return list;
    }

    public GamePresetPlan BuildPreset(GameContext ctx, GamePreset preset)
    {
        var cfg = ConfigFiles(ctx.Game)[0];
        var ini = ctx.Text(cfg) is { } t ? IniDocument.Parse(t) : null;
        var tier = ctx.Tier;
        // 0 = muito baixo … 4 = muito alto (escala do menu de vídeo)
        var (character, environment, effects, shadow) = preset == GamePreset.FpsAlto
            ? (tier >= HardwareTier.Intermediario ? 2 : 1, 1, 1, 0)
            : tier switch
            {
                HardwareTier.Entrada => (2, 2, 2, 0),
                HardwareTier.Intermediario => (3, 3, 3, 1),
                _ => (4, 4, 4, 2),
            };
        var values = new (string section, string key, string value, string label, string why)[]
        {
            ("Performance", "CharacterQuality", character.ToString(), "Qualidade dos personagens", "Mantém campeões e habilidades legíveis"),
            ("Performance", "EnvironmentQuality", environment.ToString(), "Qualidade do ambiente", preset == GamePreset.FpsAlto ? "Ambiente simples ganha FPS em lutas" : "Visual equilibrado"),
            ("Performance", "EffectsQuality", effects.ToString(), "Qualidade dos efeitos", preset == GamePreset.FpsAlto ? "Menos partículas em lutas de equipe" : "Efeitos conforme seu hardware"),
            ("Performance", "ShadowQuality", shadow.ToString(), "Qualidade das sombras", "A opção mais pesada do jogo"),
            ("General", "WaitForVerticalSync", "0", "VSync", "Remove o atraso de entrada"),
        };
        var changes = values.Select(v => new SettingChange(v.label, ini?.Get(v.section, v.key), v.value, v.why)).ToList();
        var actions = values.Select(v => (TweakAction)new IniValueAction(cfg, v.section, v.key, v.value, Processes)).ToList();
        var steps = new List<GuidedStep>
        {
            new("LoL → Configurações → Vídeo", "Limite de taxa de quadros", ctx.RefreshHz > 0 ? $"o mais próximo de {ctx.RefreshHz}" : "a taxa do seu monitor", "FPS estável; o Snow não altera esse campo no arquivo."),
        };
        var tweak = new TweakDefinition
        {
            Id = $"game.lol.{preset.ToString().ToLowerInvariant()}",
            Title = $"League of Legends: {preset.Label()}",
            Description = "Ajusta só as opções gráficas locais do game.cfg (as que o LoL não sincroniza com a conta).",
            HonestNote = AntiCheatPolicy.Summary + " O LoL usa o Riot Vanguard.",
            Category = TweakCategory.Graficos,
            Impact = Impact.Medio,
            Actions = actions,
        };
        return new(preset, changes, [tweak], steps, $"League of Legends · {preset.Label()} · hardware {tier.Label()}");
    }
}

// ─────────────────────────────────────────── VALORANT ───────────────────────────────────────────

/// <summary>
/// Somente leitura: as configurações de vídeo do Valorant ficam no RiotUserSettings.ini da conta,
/// que é sincronizado com o servidor (roaming). Editar seria desfeito e não vale o risco com o Vanguard.
/// </summary>
public sealed class ValorantProfile : IGameProfile
{
    public const string Id = "riot:valorant";
    public string GameId => Id;
    public GameSupport Support => GameSupport.Guiado;
    public IReadOnlyList<string> Processes { get; } = ["VALORANT-Win64-Shipping", "VALORANT"];

    private string? _root;
    public ValorantProfile WithConfigRoot(string root) { _root = root; return this; }
    private string Root => _root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Config");

    /// <summary>O scanner expande "*" para cada pasta de conta (UUID-região).</summary>
    public IReadOnlyList<string> ConfigFiles(InstalledGame game) =>
    [
        Path.Combine(Root, "WindowsClient", "RiotLocalMachine.ini"),
        Path.Combine(Root, "*", "Windows", "RiotUserSettings.ini"),
        Path.Combine(Root, "*", "WindowsClient", "GameUserSettings.ini"),
    ];

    private const string Settings = "Settings";

    /// <summary>Arquivo da última conta usada (RiotLocalMachine.ini → LastKnownUser).</summary>
    private (string? userSettings, string? gameSettings) ActiveAccount(GameContext ctx)
    {
        var lastUser = ctx.Text(Path.Combine(Root, "WindowsClient", "RiotLocalMachine.ini")) is { } t
            ? IniDocument.Parse(t).Get("UserInfo", "LastKnownUser") : null;
        var accountDirs = ctx.Files.Keys
            .Where(k => k.EndsWith("RiotUserSettings.ini", StringComparison.OrdinalIgnoreCase))
            .Select(k => Path.GetDirectoryName(Path.GetDirectoryName(k))!)
            .ToList();
        var dir = accountDirs.FirstOrDefault(d => lastUser is not null && Path.GetFileName(d).StartsWith(lastUser, StringComparison.OrdinalIgnoreCase))
                  ?? accountDirs.FirstOrDefault();
        return dir is null ? (null, null) : (Path.Combine(dir, "Windows", "RiotUserSettings.ini"), Path.Combine(dir, "WindowsClient", "GameUserSettings.ini"));
    }

    public IReadOnlyList<GameFinding> Analyze(GameContext ctx)
    {
        var list = new List<GameFinding>
        {
            new(FindingLevel.Info, "Somente diagnóstico (Riot Vanguard)",
                "As configurações do Valorant ficam na sua conta e são sincronizadas com o servidor da Riot. O Snow só lê os arquivos e mostra o que ajustar no menu do jogo."),
        };
        var (user, game) = ActiveAccount(ctx);
        if (user is null || ctx.Text(user) is not { } userText)
        {
            list.Add(new(FindingLevel.Info, "Configurações da conta não encontradas", "Entre no Valorant uma vez para o jogo criar os arquivos."));
            return list;
        }
        var u = IniDocument.Parse(userText);
        var g = game is not null && ctx.Text(game) is { } gt ? IniDocument.Parse(gt) : null;

        if (u.Get(Settings, "EAresBoolSettingName::MultithreadedRenderingEnabled") is "False" && ctx.Hardware.Cpu.LogicalProcessors >= 4)
            list.Add(new(FindingLevel.Importante, "Renderização multithread desligada",
                $"Seu processador tem {ctx.Hardware.Cpu.LogicalProcessors} threads. Ativar distribui o trabalho de renderização e costuma aumentar bastante o FPS."));
        if (g?.Get("/Script/ShooterGame.ShooterGameUserSettings", "bUseVSync") is "True")
            list.Add(new(FindingLevel.Importante, "VSync ligado", "Adiciona atraso de entrada. Desligue em Vídeo → Qualidade gráfica."));
        if (u.Get(Settings, "EAresBoolSettingName::VignetteEnabled") is "True")
            list.Add(new(FindingLevel.Info, "Vinheta ligada", "Escurece as bordas da tela sem ganho competitivo."));
        if (u.Get(Settings, "EAresIntSettingName::BloomQuality") is { } bloom && bloom != "0")
            list.Add(new(FindingLevel.Info, "Bloom ligado", "Brilho extra que pode atrapalhar a visibilidade."));
        if (u.Get(Settings, "EAresBoolSettingName::DisableDistortion") is "False")
            list.Add(new(FindingLevel.Info, "Distorção ligada", "Efeito visual que pesa e atrapalha a leitura."));
        if (list.Count == 1)
            list.Add(new(FindingLevel.Ok, "Nada crítico nas configurações locais", "Veja o passo a passo da predefinição para afinar o restante."));
        return list;
    }

    public GamePresetPlan BuildPreset(GameContext ctx, GamePreset preset)
    {
        var high = preset == GamePreset.FpsAlto;
        var tier = ctx.Tier;
        var quality = high ? "Baixa" : tier >= HardwareTier.Avancado ? "Alta" : tier == HardwareTier.Intermediario ? "Média" : "Baixa";
        var steps = new List<GuidedStep>
        {
            new("Vídeo → Qualidade gráfica", "Renderização multithread", "Ativado", "Usa todos os núcleos da CPU"),
            new("Vídeo → Qualidade gráfica", "Qualidade do material / textura / detalhes", quality, high ? "Máximo de FPS" : "Equilíbrio para o seu hardware"),
            new("Vídeo → Qualidade gráfica", "Qualidade da interface", "Baixa", "Sem impacto competitivo"),
            new("Vídeo → Qualidade gráfica", "Vinheta, Distorção (desativar), Bloom", "Desligado / Desativar distorção: Ligado / Bloom: Desligado", "Imagem mais limpa"),
            new("Vídeo → Qualidade gráfica", "VSync", "Desligado", "Remove o atraso de entrada"),
            high
                ? new("Vídeo → Geral", "Limitar FPS sempre", "Desligado (com Reflex) ou um teto bem acima do monitor", "Mais FPS = menor latência; o teto só evita aquecer à toa")
                : new("Vídeo → Geral", "Limitar FPS sempre", ctx.RefreshHz > 0 ? $"Ligado · {ctx.RefreshHz}" : "Ligado · taxa do monitor", "Frametime estável na taxa do monitor"),
        };
        if (ctx.Hardware.PrimaryGpu?.Vendor == GpuVendor.Nvidia)
            steps.Add(new("Vídeo → Qualidade gráfica", "NVIDIA Reflex Low Latency", high ? "Ativado + Boost" : "Ativado", "Menor latência em GPUs NVIDIA"));
        return new(preset, [], [], steps, $"Valorant · {preset.Label()} · hardware {tier.Label()} · passo a passo (sem editar arquivos)");
    }
}

// ─────────────────────────────────────── PONTOS GENÉRICOS ───────────────────────────────────────

/// <summary>Achados que valem para qualquer jogo detectado (instalação, GPU, disco).</summary>
public static class GenericGameAdvisor
{
    public static IReadOnlyList<GameFinding> Analyze(InstalledGame game, HardwareProfile hw, bool isRunning)
    {
        var list = new List<GameFinding>();
        var disk = hw.Storage.DiskForDrive(game.DriveLetter);
        if (disk is { Kind: DiskKind.Hdd })
            list.Add(new(FindingLevel.Importante, $"Instalado em HDD ({game.DriveLetter}:)",
                "Jogos atuais carregam texturas durante a partida; em HDD isso causa engasgos e carregamentos longos. Mova para um SSD pelo próprio launcher."));
        else if (disk is { IsSsd: true })
            list.Add(new(FindingLevel.Ok, $"Instalado em {(disk.Kind == DiskKind.NvmeSsd ? "SSD NVMe" : "SSD")} ({game.DriveLetter}:)", "Carregamentos rápidos."));

        var physical = hw.Gpus.Where(g => !g.IsVirtual).ToList();
        if (physical.Count > 1 && physical.Any(g => g.IsDiscrete) && physical.Any(g => !g.IsDiscrete))
            list.Add(new(FindingLevel.Melhoria, "Dois adaptadores de vídeo",
                "Garanta que o jogo use a GPU dedicada: Configurações → Sistema → Tela → Gráficos → escolha o jogo → Alto desempenho. (O Snow não altera regras por executável de jogos com anti-cheat.)"));

        if (isRunning)
            list.Add(new(FindingLevel.Info, "Jogo aberto agora", "Feche o jogo (e o launcher) para aplicar predefinições."));
        return list;
    }
}

public static class GameProfiles
{
    public static IReadOnlyList<IGameProfile> All() => [new FortniteProfile(), new LeagueProfile(), new ValorantProfile(), new RocketLeagueProfile(), new ObsProfile()];
}
