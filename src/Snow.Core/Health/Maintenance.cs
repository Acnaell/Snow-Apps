using System.Text.RegularExpressions;
using Snow.Core.Monitor;

namespace Snow.Core.Health;

// ───────────────────────────── Limpeza ─────────────────────────────

public enum CleanupKind
{
    /// <summary>Apaga arquivos com mais de N dias nas pastas listadas.</summary>
    Files,
    /// <summary>Lixeira do Windows (API do Shell).</summary>
    RecycleBin,
    /// <summary>Só mede e mostra onde limpar no Windows (ex.: Windows.old exige o assistente do sistema).</summary>
    Manual,
}

/// <summary>
/// Uma categoria de limpeza. Caminhos usam tokens ({TEMP}, {WINDIR}, {LOCALAPPDATA}, {LOCALLOW}, {APPDATA}, {PROGRAMDATA})
/// e "\" como separador; o último segmento pode ter * (ex.: webcache*). Um caminho pode ser arquivo (MEMORY.DMP).
/// </summary>
public sealed record CleanupCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    public int MinAgeDays { get; init; }
    public bool DefaultOn { get; init; } = true;
    public string? Note { get; init; }
    /// <summary>Se algum destes processos estiver aberto, a categoria é pulada (o app está usando o cache).</summary>
    public IReadOnlyList<string> SkipIfRunning { get; init; } = [];
    public CleanupKind Kind { get; init; } = CleanupKind.Files;
    public string? ManualTarget { get; init; }
}

public sealed record CleanupMeasure(string Id, long Files, long Bytes, string? Skipped = null);

public sealed record CleanupResult(string Id, long Files, long Bytes, long Failed, string? Skipped = null);

public static class CleanupCatalog
{
    public static readonly IReadOnlyList<CleanupCategory> All =
    [
        new()
        {
            Id = "temp.user", Title = "Temporários do usuário",
            Description = "Arquivos com mais de 1 dia na pasta %TEMP%. Arquivos em uso são ignorados.",
            Paths = [@"{TEMP}"], MinAgeDays = 1,
        },
        new()
        {
            Id = "temp.windows", Title = "Temporários do Windows",
            Description = @"Arquivos com mais de 1 dia em C:\Windows\Temp.",
            Paths = [@"{WINDIR}\Temp"], MinAgeDays = 1,
        },
        new()
        {
            Id = "wu.download", Title = "Atualizações já baixadas",
            Description = "Pacotes do Windows Update com mais de 10 dias, já instalados. Se precisar, o Windows baixa de novo.",
            Paths = [@"{WINDIR}\SoftwareDistribution\Download"], MinAgeDays = 10,
        },
        new()
        {
            Id = "delivery", Title = "Cache de Otimização de Entrega",
            Description = "Cópias de atualizações que o Windows guarda para compartilhar com outros PCs.",
            Paths = [@"{WINDIR}\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"], MinAgeDays = 3,
        },
        new()
        {
            Id = "wer", Title = "Relatórios de erro antigos",
            Description = "Relatórios de travamento com mais de 7 dias. O histórico de estabilidade do Snow Health vem do registro de eventos e não é afetado.",
            Paths = [@"{PROGRAMDATA}\Microsoft\Windows\WER\ReportArchive", @"{PROGRAMDATA}\Microsoft\Windows\WER\ReportQueue", @"{LOCALAPPDATA}\Microsoft\Windows\WER"],
            MinAgeDays = 7,
        },
        new()
        {
            Id = "logs.cbs", Title = "Logs antigos de instalação do Windows",
            Description = @"Logs com mais de 30 dias em C:\Windows\Logs\CBS.",
            Paths = [@"{WINDIR}\Logs\CBS"], MinAgeDays = 30,
        },
        new()
        {
            Id = "steam.web", Title = "Cache da loja e da interface da Steam",
            Description = "Páginas e imagens da loja/biblioteca guardadas pela Steam. Não mexe em jogos nem em saves.",
            Paths = [@"{LOCALAPPDATA}\Steam\htmlcache"], MinAgeDays = 0,
            SkipIfRunning = ["steam.exe", "steamwebhelper.exe"],
            Note = "Feche a Steam para limpar.",
        },
        new()
        {
            Id = "epic.web", Title = "Cache da interface da Epic Games",
            Description = "Páginas e imagens da interface do launcher. Não mexe em jogos nem em saves.",
            Paths = [@"{LOCALAPPDATA}\EpicGamesLauncher\Saved\webcache*"], MinAgeDays = 0,
            SkipIfRunning = ["epicgameslauncher.exe", "epicwebhelper.exe"],
            Note = "Feche o launcher da Epic para limpar.",
        },
        new()
        {
            Id = "discord.cache", Title = "Cache do Discord",
            Description = "Imagens, vídeos e código em cache. Você continua logado.",
            Paths = [@"{APPDATA}\discord\Cache", @"{APPDATA}\discord\Code Cache", @"{APPDATA}\discord\GPUCache"], MinAgeDays = 0,
            SkipIfRunning = ["discord.exe"],
            Note = "Feche o Discord (inclusive da bandeja) para limpar.",
        },
        new()
        {
            Id = "spotify.cache", Title = "Cache de músicas do Spotify",
            Description = "Músicas guardadas para tocar mais rápido. Downloads para ouvir offline ficam em outra pasta e não são apagados.",
            Paths = [@"{LOCALAPPDATA}\Spotify\Data"], MinAgeDays = 0, DefaultOn = false,
            SkipIfRunning = ["spotify.exe"],
            Note = "Feche o Spotify para limpar.",
        },
        new()
        {
            Id = "dumps", Title = "Arquivos de despejo de memória",
            Description = "Arquivos gerados em telas azuis e travamentos de programas, com mais de 14 dias.",
            Paths = [@"{WINDIR}\Minidump", @"{WINDIR}\MEMORY.DMP", @"{LOCALAPPDATA}\CrashDumps"], MinAgeDays = 14, DefaultOn = false,
            Note = "Servem para investigar telas azuis. Apague se o problema já foi resolvido.",
        },
        new()
        {
            Id = "shaders", Title = "Caches de shader antigos",
            Description = "Entradas com mais de 30 dias dos caches DirectX, NVIDIA, AMD e Intel.",
            Paths =
            [
                @"{LOCALAPPDATA}\D3DSCache",
                @"{LOCALAPPDATA}\NVIDIA\DXCache", @"{LOCALAPPDATA}\NVIDIA\GLCache",
                @"{LOCALLOW}\NVIDIA\PerDriverVersion\DXCache", @"{LOCALLOW}\NVIDIA\PerDriverVersion\GLCache",
                @"{PROGRAMDATA}\NVIDIA Corporation\NV_Cache",
                @"{LOCALAPPDATA}\AMD\DxCache", @"{LOCALAPPDATA}\AMD\DxcCache", @"{LOCALAPPDATA}\AMD\VkCache", @"{LOCALAPPDATA}\AMD\GLCache",
                @"{LOCALAPPDATA}\Intel\ShaderCache", @"{LOCALLOW}\Intel\ShaderCache",
            ],
            MinAgeDays = 30, DefaultOn = false,
            Note = "Use depois de atualizar o driver ou se um jogo travar ao carregar. Na primeira partida seguinte pode haver engasgos enquanto os shaders são recompilados.",
        },
        new()
        {
            Id = "recycle", Title = "Lixeira", Kind = CleanupKind.RecycleBin, DefaultOn = false,
            Description = "Esvazia a Lixeira de todas as unidades. Não dá para desfazer.",
        },
        new()
        {
            Id = "windows.old", Title = "Instalação anterior do Windows (Windows.old)", Kind = CleanupKind.Manual, DefaultOn = false,
            Description = "Cópia da versão anterior do Windows, mantida após uma grande atualização para permitir voltar.",
            Paths = [@"{SYSTEMDRIVE}\Windows.old"], ManualTarget = "ms-settings:storagepolicies",
            Note = "O Windows protege esta pasta. Remova em Configurações > Armazenamento > Arquivos temporários > Instalação anterior do Windows.",
        },
    ];

    /// <summary>O que o Snow não limpa de propósito (mostrado na tela, para quem vem de outros "otimizadores").</summary>
    public static readonly IReadOnlyList<(string What, string Why)> NotCleaned =
    [
        ("Prefetch", "O Windows usa para abrir programas e jogos mais rápido. Limpar deixa o próximo início mais lento."),
        ("Registro do Windows", "\"Limpeza de registro\" não deixa o PC mais rápido e pode quebrar programas."),
        ("Pastas de jogos e anti-cheat", "Riot/Vanguard, Easy Anti-Cheat e BattlEye verificam os próprios arquivos. O Snow não mexe nelas."),
        ("Cache dos navegadores", "O navegador controla o próprio cache e ele acelera os sites que você usa. Limpe pelo navegador se quiser."),
    ];

    public static CleanupCategory? Get(string id) => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Troca tokens e separadores pelo caminho real (usa Path.Combine, então funciona nos testes em Linux).</summary>
    public static string Resolve(string template, IReadOnlyDictionary<string, string> env)
    {
        var parts = template.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var head = parts[0];
        foreach (var (k, v) in env) head = head.Replace("{" + k + "}", v, StringComparison.OrdinalIgnoreCase);
        return parts.Length == 1 ? head : Path.Combine([head, .. parts[1..]]);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        > 0 => $"{Math.Max(1, bytes / 1024)} KB",
        _ => "0 MB",
    };
}

// ───────────────────────────── Unidades (TRIM / otimização agendada) ─────────────────────────────

public sealed record DriveCare(bool? TrimEnabled, bool? ScheduledOptimization);

public static class DriveCareParser
{
    /// <summary>Lê a saída de "fsutil behavior query DisableDeleteNotify" (qualquer idioma). NTFS = 0 → TRIM ligado.</summary>
    public static bool? TrimFromFsutil(string output)
    {
        var m = Regex.Match(output, @"NTFS\s+DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(output, @"DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value == "0" : null;
    }

    public static IEnumerable<HealthItem> Items(DriveCare c, bool anySsd)
    {
        if (anySsd && c.TrimEnabled == false)
            yield return new(HealthCategory.Discos, HealthLevel.Atencao, "TRIM desligado",
                "Sem TRIM, o SSD fica mais lento com o tempo e se desgasta mais.", "Clique em \"Ligar TRIM\".");
        else if (anySsd && c.TrimEnabled == true)
            yield return new(HealthCategory.Discos, HealthLevel.Ok, "TRIM ligado", "O Windows avisa o SSD sobre o espaço livre, o que mantém a velocidade e reduz o desgaste.");
        if (c.ScheduledOptimization == false)
            yield return new(HealthCategory.Discos, HealthLevel.Atencao, "Otimização semanal das unidades desligada",
                "O Windows não está passando TRIM nos SSDs nem desfragmentando os HDs automaticamente.", "Clique em \"Religar otimização semanal\".");
    }
}

// ───────────────────────────── Processos ─────────────────────────────

/// <summary>Uso médio de um app (todas as instâncias somadas) durante a amostragem. CPU em % do processador inteiro.</summary>
public sealed record ProcessUsage(string App, int Instances, double CpuAvg, double CpuMax, long PrivateBytes);

public enum ProcessKind { Jogo, AntiCheat, Sistema, Seguranca, Conhecido, Desconhecido }

public sealed record ProcessFinding(ProcessUsage Usage, ProcessKind Kind, string Display, HealthLevel Level, string Advice, bool CanClose);

public sealed record ProcessReport(IReadOnlyList<ProcessFinding> Findings, double BackgroundCpu, long BackgroundBytes, int Seconds, string Summary);

public static class ProcessAdvisor
{
    /// <summary>
    /// Anti-cheat e clientes da Riot: o Snow só lê o nome (vem da contagem de desempenho do Windows, sem abrir o processo)
    /// e nunca oferece fechar. Comparação por prefixo.
    /// </summary>
    public static readonly IReadOnlyList<string> AntiCheatPrefixes =
    [
        "vgc", "vgk", "vgtray", "vanguard", "riot", "league", "easyanticheat", "eaanticheat", "beservice", "bedaisy", "battleye",
        "faceit", "pnkbstr", "gameguard", "xigncode", "nprotect", "mhyprot", "ace-", "ricochet", "javelin",
    ];

    private static readonly HashSet<string> SystemApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe",
        "dwm.exe", "explorer.exe", "fontdrvhost.exe", "sihost.exe", "ctfmon.exe", "audiodg.exe", "spoolsv.exe", "conhost.exe", "runtimebroker.exe",
        "dllhost.exe", "wmiprvse.exe", "taskhostw.exe", "memory compression", "searchindexer.exe", "searchprotocolhost.exe", "searchfilterhost.exe",
        "tiworker.exe", "trustedinstaller.exe", "mousocoreworker.exe", "usocoreworker.exe", "sgrmbroker.exe", "wudfhost.exe", "dashost.exe",
        "startmenuexperiencehost.exe", "shellexperiencehost.exe", "searchhost.exe", "textinputhost.exe", "compattelrunner.exe", "msiexec.exe",
        "smartscreen.exe", "applicationframehost.exe", "lockapp.exe", "securityhealthsystray.exe", "csrss", "idle", "secure system",
    };

    private static readonly HashSet<string> SecurityApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "msmpeng.exe", "mpdefendercoreservice.exe", "nissrv.exe", "securityhealthservice.exe", "mssense.exe",
        "avp.exe", "avgui.exe", "avastui.exe", "avastsvc.exe", "bdagent.exe", "mcshield.exe", "ekrn.exe", "egui.exe", "nortonsecurity.exe",
    };

    private sealed record Known(string[] Names, string Display, string Advice, bool CanClose = true);

    private static readonly Known[] KnownApps =
    [
        new(["chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "brave.exe", "vivaldi.exe"], "Navegador",
            "Feche as abas pesadas (vídeos, sites com anúncios) antes de jogar. Nas configurações do navegador, desligue \"Continuar executando apps em segundo plano\" (no Edge, também \"Aceleração de inicialização\")."),
        new(["msedgewebview2.exe"], "WebView2 (usado por Widgets, Teams, Copilot e outros)",
            "Não é um app sozinho: pertence a outro programa aberto. Feche o programa dono (Widgets, Teams, launcher) em vez deste processo.", false),
        new(["discord.exe", "discordptb.exe", "discordcanary.exe"], "Discord",
            "Em Configurações > Avançado, desligue a Aceleração de hardware se a placa de vídeo já estiver no limite, e desative o overlay do jogo se não usa."),
        new(["spotify.exe", "deezer.exe"], "Player de música",
            "Desligue a aceleração de hardware nas configurações do app. Isso reduz o uso da placa de vídeo durante o jogo."),
        new(["steamwebhelper.exe"], "Interface da Steam",
            "Em Steam > Configurações > Interface, desligue \"Renderização acelerada por GPU nas visualizações web\". Com o jogo aberto, deixe a Steam minimizada.", false),
        new(["epicgameslauncher.exe", "epicwebhelper.exe"], "Launcher da Epic Games",
            "Nas configurações do launcher, desligue \"Executar ao iniciar o computador\" se você não usa sempre.", false),
        new(["onedrive.exe", "dropbox.exe", "googledrivefs.exe"], "Sincronização de arquivos",
            "Se estiver sincronizando, a sincronização disputa disco e internet com o jogo. Pause a sincronização antes de jogar."),
        new(["wallpaper32.exe", "wallpaper64.exe", "webwallpaper32.exe", "lively.exe"], "Papel de parede animado",
            "Configure para pausar quando outro app estiver em tela cheia ou em primeiro plano."),
        new(["icue.exe", "lghub.exe", "lghub_agent.exe", "razer synapse service.exe", "razerappengine.exe", "armourycrate.exe", "armourycrate.usersessionhelper.exe",
             "msi.centralserver.exe", "msi center.exe", "nzxt cam.exe", "signalrgb.exe", "openrgb.exe"], "Software de RGB e periféricos",
            "Costuma ser pesado. Salve o perfil na memória do mouse/teclado (opção \"onboard\"), tire da inicialização e feche."),
        new(["teams.exe", "ms-teams.exe"], "Microsoft Teams", "Desligue \"Iniciar o Teams automaticamente\" se não usa no PC de jogos."),
        new(["overwolf.exe", "medal.exe", "outplayed.exe"], "Gravação e overlay",
            "Gravar o jogo em segundo plano custa CPU e placa de vídeo. Desligue a gravação automática se não usa."),
        new(["obs64.exe", "obs32.exe"], "OBS Studio", "Se não estiver gravando nem em live, feche. Para live, use o perfil de OBS do Snow (codificador da placa de vídeo)."),
        new(["nvidia share.exe", "nvcontainer.exe", "nvidia app.exe", "nvidia overlay.exe"], "NVIDIA App / overlay",
            "Se não grava nem usa filtros, desligue o overlay no jogo nas configurações do NVIDIA App.", false),
        new(["radeonsoftware.exe", "amdrsserv.exe", "amdow.exe", "amdrssrcext.exe"], "AMD Adrenalin",
            "Se não grava, desligue Gravação instantânea/Replay e o overlay de métricas nas configurações do Adrenalin.", false),
        new(["widgets.exe", "widgetservice.exe"], "Widgets do Windows", "Desligue em Configurações > Personalização > Barra de tarefas > Widgets."),
        new(["phoneexperiencehost.exe"], "Vincular ao Celular", "Se não usa, desligue em Configurações > Bluetooth e dispositivos > Dispositivos móveis."),
        new(["adobecollabsync.exe", "ccxprocess.exe", "creative cloud.exe", "adobeipcbroker.exe", "creative cloud helper.exe", "acrotray.exe", "adobe desktop service.exe"], "Serviços da Adobe",
            "Rodam o tempo todo mesmo sem usar Adobe. Tire da inicialização."),
        new(["whatsapp.exe", "telegram.exe"], "Mensageiro", "Feche durante partidas se não estiver usando."),
        new(["msiafterburner.exe", "rtss.exe"], "MSI Afterburner / RivaTuner", "Normal se você usa o overlay de FPS; o custo é pequeno.", false),
        new(["xboxpcapp.exe", "gamingservices.exe", "gamebar.exe"], "Xbox / Game Bar", "Se não usa, desligue a Xbox Game Bar em Configurações > Jogos.", false),
    ];

    private static readonly Dictionary<string, Known> ByName =
        KnownApps.SelectMany(k => k.Names.Select(n => (n, k))).ToDictionary(x => x.n, x => x.k, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> SystemAdvice = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svchost.exe"] = "Serviços do Windows agrupados. Uso alto e passageiro costuma ser o Windows Update ou a indexação.",
        ["searchindexer.exe"] = "Indexação da pesquisa do Windows. Fica alta depois de copiar muitos arquivos e termina sozinha.",
        ["tiworker.exe"] = "O Windows está instalando atualizações. Deixe terminar antes de jogar.",
        ["trustedinstaller.exe"] = "O Windows está instalando atualizações. Deixe terminar antes de jogar.",
        ["mousocoreworker.exe"] = "O Windows Update está verificando ou baixando atualizações.",
        ["audiodg.exe"] = "Áudio do Windows. Se o uso estiver alto, desligue os \"Aprimoramentos de áudio\" do dispositivo de som.",
        ["dwm.exe"] = "Desenha as janelas na tela. É normal subir com vários monitores ou papel de parede animado.",
        ["explorer.exe"] = "Área de trabalho e Explorador de Arquivos.",
        ["memory compression"] = "O Windows comprimindo memória para caber mais na RAM. Uso alto indica que a RAM está no limite.",
    };

    public static bool IsAntiCheat(string app) =>
        AntiCheatPrefixes.Any(p => app.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Jogo conhecido do Snow ou executável típico de Unreal (…-Shipping.exe).</summary>
    public static bool IsGame(string app) =>
        ActiveGameDetector.KnownGames.ContainsKey(app) || app.Contains("-Shipping", StringComparison.OrdinalIgnoreCase);

    public static bool IsProtected(string app) =>
        IsAntiCheat(app) || IsGame(app) || SystemApps.Contains(app) || SecurityApps.Contains(app)
        || app.StartsWith("snow", StringComparison.OrdinalIgnoreCase) || app.StartsWith("presentmon", StringComparison.OrdinalIgnoreCase);

    public static ProcessFinding Classify(ProcessUsage u)
    {
        var heavy = u.CpuAvg >= 5 || u.PrivateBytes >= 1536L << 20;
        var level = heavy ? HealthLevel.Atencao : HealthLevel.Info;
        if (IsGame(u.App))
            return new(u, ProcessKind.Jogo, ActiveGameDetector.DisplayName(u.App), HealthLevel.Info,
                "Jogo aberto. Para medir o que roda em segundo plano, faça a análise com os jogos fechados.", false);
        if (IsAntiCheat(u.App))
            return new(u, ProcessKind.AntiCheat, "Anti-cheat ou cliente da Riot", HealthLevel.Info,
                u.App.StartsWith("league", StringComparison.OrdinalIgnoreCase) || u.App.StartsWith("riot", StringComparison.OrdinalIgnoreCase)
                    ? "O Snow não mexe nos processos da Riot. Dica: no cliente do LoL, em Configurações > Geral, marque \"Fechar o cliente durante a partida\"."
                    : "Proteção do jogo. O Snow não mexe neste processo.", false);
        if (SecurityApps.Contains(u.App))
            return new(u, ProcessKind.Seguranca, "Antivírus", level,
                "Não desligue o antivírus. Se ele estiver pesado com frequência, agende a verificação completa para um horário em que você não joga.", false);
        if (SystemApps.Contains(u.App))
            return new(u, ProcessKind.Sistema, "Windows", u.App.Equals("memory compression", StringComparison.OrdinalIgnoreCase) && heavy ? HealthLevel.Atencao : HealthLevel.Info,
                SystemAdvice.GetValueOrDefault(u.App, "Processo do Windows. Não precisa (nem deve) ser fechado."), false);
        if (ByName.TryGetValue(u.App, out var k))
            return new(u, ProcessKind.Conhecido, k.Display, level, k.Advice, k.CanClose);
        return new(u, ProcessKind.Desconhecido, u.App, level,
            "Se você não reconhece este programa ou não precisa dele aberto agora, feche. Se ele abre sozinho, veja a aba Inicialização.", true);
    }

    /// <summary>Monta o relatório: só o que pesa (≥ 1% de CPU ou ≥ 300 MB), do mais pesado para o mais leve.</summary>
    public static ProcessReport Analyze(IEnumerable<ProcessUsage> usages, int seconds, int max = 15)
    {
        var all = usages.Where(u => !u.App.Equals("idle", StringComparison.OrdinalIgnoreCase) && !u.App.Equals("_total", StringComparison.OrdinalIgnoreCase)).ToList();
        var background = all.Where(u => !IsGame(u.App) && !u.App.StartsWith("snow", StringComparison.OrdinalIgnoreCase)).ToList();
        var bgCpu = Math.Round(background.Sum(u => u.CpuAvg), 1);
        var bgBytes = background.Sum(u => u.PrivateBytes);

        var findings = all
            .Where(u => u.CpuAvg >= 1 || u.PrivateBytes >= 300L << 20)
            .Where(u => !u.App.StartsWith("snow", StringComparison.OrdinalIgnoreCase) && !u.App.StartsWith("presentmon", StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => IsGame(u.App))
            .ThenByDescending(u => u.CpuAvg * 100 + u.PrivateBytes / (double)(100L << 20))
            .Take(max)
            .Select(Classify)
            .ToList();

        var actionable = findings.Count(f => f.CanClose && f.Level == HealthLevel.Atencao);
        var summary = bgCpu switch
        {
            < 5 => $"Segundo plano leve: {bgCpu:0.#}% de CPU em média. Sobra quase todo o processador para o jogo.",
            < 15 => $"Segundo plano moderado: {bgCpu:0.#}% de CPU em média.",
            _ => $"Segundo plano pesado: {bgCpu:0.#}% de CPU em média. Isso tira desempenho do jogo, principalmente quando o processador é o limite.",
        };
        if (actionable > 0) summary += $" {actionable} programa(s) pesado(s) podem ser fechados.";
        if (findings.Any(f => f.Kind == ProcessKind.Jogo)) summary += " Há um jogo aberto; ele não entra na conta.";
        return new(findings, bgCpu, bgBytes, seconds, summary);
    }

    /// <summary>Junta instâncias pelo nome da contagem do Windows ("chrome#3" → chrome.exe) e soma CPU/memória.</summary>
    public static string AppFromPdhInstance(string instance)
    {
        var name = instance;
        var hash = name.LastIndexOf('#');
        if (hash > 0 && int.TryParse(name[(hash + 1)..], out _)) name = name[..hash];
        if (name.Equals("Idle", StringComparison.OrdinalIgnoreCase) || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System", StringComparison.OrdinalIgnoreCase) || name.Equals("Registry", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase) || name.Equals("Secure System", StringComparison.OrdinalIgnoreCase))
            return name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>Item de inicialização que abre este app (o comando contém o nome do executável).</summary>
    public static int? MatchStartup(string app, IReadOnlyList<string> startupCommands)
    {
        if (IsProtected(app)) return null;
        for (var i = 0; i < startupCommands.Count; i++)
        {
            var cmd = startupCommands[i];
            var idx = cmd.IndexOf(app, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            // Precisa ser o nome inteiro do arquivo (evita "steam.exe" casar com "steamwebhelper.exe"... e "x.exe" com "fox.exe")
            var before = idx == 0 ? '\\' : cmd[idx - 1];
            if (before is '\\' or '/' or '"' or ' ') return i;
        }
        return null;
    }
}
