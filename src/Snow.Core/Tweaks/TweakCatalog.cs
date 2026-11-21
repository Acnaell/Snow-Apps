using Snow.Core.Hardware;
using static Snow.Core.Tweaks.OptimizationProfile;

namespace Snow.Core.Tweaks;

/// <summary>
/// Catálogo único de ajustes. A ordem importa: ajustes de plano de energia vêm antes dos
/// ajustes de índices do plano, e ajustes do mesmo <see cref="Group"/> são mutuamente exclusivos
/// (o motor escolhe o primeiro aplicável).
/// </summary>
public static class TweakCatalog
{
    public static class Groups
    {
        public const string PowerPlan = "power.plan";
    }

    // Declarado antes de Tweaks: inicializadores estáticos rodam em ordem textual.
    /// <summary>Padrões (contém, sem diferenciar maiúsculas) para nomes ou comandos de itens de inicialização.</summary>
    public static readonly IReadOnlyList<string> KnownHeavyStartup =
    [
        "Steam", "EpicGamesLauncher", "EADesktop", "Origin", "Battle.net", "UbisoftConnect", "Ubisoft Game Launcher", "GalaxyClient",
        "Discord", "Spotify", "com.squirrel.Teams", "ms-teams", "Skype", "AdobeGCInvoker", "Adobe Creative Cloud", "CCleaner",
        "iTunesHelper", "MicrosoftEdgeAutoLaunch", "Opera Browser Assistant", "RiotClient", "Overwolf", "Razer Synapse",
    ];

    // GUIDs do powrprof (winnt.h)
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    public static readonly Guid CpMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");

    private const string Mmcss = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmcssGames = Mmcss + @"\Tasks\Games";

    private static HashSet<OptimizationProfile> P(params OptimizationProfile[] p) => [.. p];

    private static Applicability Desktop(HardwareProfile h) =>
        h.State.IsLaptop ? Applicability.No("Notebook detectado: evitamos aquecimento e consumo excessivos.") : Applicability.Yes;

    private static Applicability NotDualX3D(HardwareProfile h) =>
        h.Cpu.IsDualCcdX3D ? Applicability.No($"{h.Cpu.Name}: o driver AMD usa core parking para manter o jogo no CCD com 3D V-Cache.") : Applicability.Yes;

    private static Applicability NoModernStandby(HardwareProfile h) =>
        h.State.ModernStandby ? Applicability.No("Sistema com Modern Standby: o Windows só permite o plano Equilibrado (use o Modo de energia).") : Applicability.Yes;

    private static Applicability All(HardwareProfile h, params Func<HardwareProfile, Applicability>[] rules)
    {
        foreach (var r in rules)
        {
            var a = r(h);
            if (!a.IsApplicable) return a;
        }
        return Applicability.Yes;
    }

    public static IReadOnlyList<TweakDefinition> Tweaks { get; } = Build();

    public static TweakDefinition Get(string id) => Tweaks.First(t => t.Id == id);

    private static List<TweakDefinition> Build() =>
    [
        // ───────────────────────────── ENERGIA ─────────────────────────────
        new()
        {
            Id = "power.ultimate", Group = Groups.PowerPlan, Category = TweakCategory.Energia,
            Title = "Plano Desempenho Máximo (Ultimate Performance)",
            Description = "Cria (se necessário) e ativa o plano oculto Desempenho Máximo: sem parking de núcleos, estado mínimo do processador em 100% e latências de transição de energia mínimas.",
            HonestNote = "Reduz picos de latência ao acordar núcleos; ganho de FPS médio costuma ser pequeno (0–3%), maior em 1% low. Aumenta o consumo em repouso.",
            Impact = Impact.Medio,
            Actions = [new PowerPlanAction(PowerPlan.UltimatePerformance)],
            AppliesTo = h => All(h, Desktop, NoModernStandby, NotDualX3D),
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "power.x3d.balanced", Group = Groups.PowerPlan, Category = TweakCategory.Energia,
            Title = "Plano Equilibrado (recomendação AMD para Ryzen X3D de 2 CCDs)",
            Description = "Mantém o plano Equilibrado, que o driver AMD 3D V-Cache Performance Optimizer usa para estacionar o CCD sem V-Cache durante jogos.",
            HonestNote = "Nesses processadores, planos de alto desempenho podem PIORAR o FPS, porque espalham o jogo pelos dois CCDs.",
            Impact = Impact.Alto,
            Actions = [new PowerPlanAction(PowerPlan.Balanced)],
            AppliesTo = h => h.Cpu.IsDualCcdX3D ? Applicability.Yes : Applicability.No("Somente para Ryzen X3D de dois CCDs."),
            Profiles = P(Competitivo, Desempenho, Equilibrado),
        },
        new()
        {
            Id = "power.high.desktop", Group = Groups.PowerPlan, Category = TweakCategory.Energia,
            Title = "Plano Alto Desempenho",
            Description = "Ativa o plano Alto Desempenho padrão do Windows (recriando-o se estiver oculto).",
            HonestNote = "Clocks sobem mais rápido que no Equilibrado; diferença pequena em CPUs modernas, que já gerenciam boost por hardware.",
            Impact = Impact.Baixo,
            Actions = [new PowerPlanAction(PowerPlan.HighPerformance)],
            AppliesTo = h => All(h, Desktop, NoModernStandby, NotDualX3D),
            Profiles = P(Desempenho),
        },
        new()
        {
            Id = "power.high.laptop", Group = Groups.PowerPlan, Category = TweakCategory.Energia,
            Title = "Plano Alto Desempenho (notebook)",
            Description = "Em notebooks, o perfil Competitivo usa Alto Desempenho em vez do Desempenho Máximo para não travar a CPU no clock máximo o tempo todo.",
            HonestNote = "Use na tomada. Na bateria, a autonomia cai bastante.",
            Impact = Impact.Medio,
            Actions = [new PowerPlanAction(PowerPlan.HighPerformance)],
            AppliesTo = h => h.State.IsLaptop ? All(h, NoModernStandby, NotDualX3D) : Applicability.No("Somente notebooks."),
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "power.overlay.best", Group = Groups.PowerPlan, Category = TweakCategory.Energia,
            Title = "Modo de energia: Melhor desempenho",
            Description = "Mantém o plano Equilibrado e coloca o controle 'Modo de energia' em Melhor desempenho. É o caminho suportado em notebooks e em sistemas com Modern Standby.",
            HonestNote = "Gerenciamento inteligente de clock: performance alta na tomada sem fixar a CPU em 100%.",
            Impact = Impact.Medio,
            Actions = [new PowerPlanAction(PowerPlan.Balanced), new PowerOverlayAction(PowerOverlay.BestPerformance)],
            AppliesTo = h => h.State.IsLaptop || h.State.ModernStandby ? Applicability.Yes : Applicability.No("Somente notebooks/Modern Standby."),
            Profiles = P(Competitivo, Desempenho),
        },
        new()
        {
            Id = "power.balanced", Group = Groups.PowerPlan, Category = TweakCategory.Energia,
            Title = "Plano Equilibrado",
            Description = "Plano padrão da Microsoft, com boost dinâmico e baixo consumo em repouso.",
            HonestNote = "A melhor escolha para uso misto e notebooks na bateria.",
            Actions = [new PowerPlanAction(PowerPlan.Balanced), new PowerOverlayAction(PowerOverlay.Balanced)],
            Profiles = P(Equilibrado),
        },
        new()
        {
            Id = "power.cpu.min100", Category = TweakCategory.Energia,
            Title = "Estado mínimo do processador = 100% (na tomada)",
            Description = "Ajusta PROCTHROTTLEMIN do plano ativo para 100% na tomada, evitando a rampa de clock no início de cada quadro.",
            HonestNote = "Mais calor e consumo em repouso. Em CPUs com Speed Shift/CPPC o ganho é marginal.",
            Actions = [new PowerSettingAction(SubProcessor, ProcThrottleMin, 100, "Estado mínimo do processador (%)")],
            AppliesTo = h => All(h, Desktop, NotDualX3D),
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "power.cpu.unpark", Category = TweakCategory.Energia,
            Title = "Desativar core parking (na tomada)",
            Description = "Ajusta CPMINCORES do plano ativo para 100%: todos os núcleos ficam disponíveis ao agendador.",
            HonestNote = "Pode reduzir micro-engasgos em jogos que criam muitas threads de repente. NUNCA em Ryzen X3D de 2 CCDs.",
            Impact = Impact.Medio,
            Actions = [new PowerSettingAction(SubProcessor, CpMinCores, 100, "Mínimo de núcleos não estacionados (%)")],
            AppliesTo = h => All(h, Desktop, NotDualX3D),
            Profiles = P(Competitivo),
        },

        // ─────────────────────── SERVIÇOS / PRIVACIDADE ───────────────────────
        new()
        {
            Id = "privacy.telemetry", Category = TweakCategory.ServicosPrivacidade,
            Title = "Reduzir telemetria (DiagTrack)",
            Description = "Desativa o serviço 'Experiências do Usuário Conectado e Telemetria' e define a política de telemetria no mínimo permitido pela edição.",
            HonestNote = "Menos atividade de disco e rede em segundo plano. O Windows Update e a Microsoft Store continuam funcionando.",
            Actions =
            [
                new ServiceStartAction("DiagTrack", ServiceStart.Disabled, StopNow: true),
                new RegistryValueAction(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", RegistryKind.DWord, 0),
            ],
            Profiles = P(Competitivo, Desempenho),
        },
        new()
        {
            Id = "services.sysmain", Category = TweakCategory.ServicosPrivacidade,
            Title = "Desativar SysMain (Superfetch)",
            Description = "Desativa o pré-carregamento de aplicativos na RAM. Só é oferecido quando sistema e jogos estão em SSD e há 16 GB de RAM ou mais.",
            HonestNote = "Em SSD o benefício do SysMain é pequeno; desativá-lo evita picos de E/S. Também desliga a compressão de memória, por isso exigimos 16 GB+.",
            Risk = Risk.Moderado,
            Actions = [new ServiceStartAction("SysMain", ServiceStart.Disabled, StopNow: true)],
            AppliesTo = h =>
                !h.Storage.AllRelevantDisksAreSsd ? Applicability.No("Há HDD no sistema/jogos: o SysMain ajuda bastante em HDD.")
                : h.Memory.TotalGb < 15 ? Applicability.No("Menos de 16 GB de RAM: a compressão de memória do SysMain é útil.")
                : Applicability.Yes,
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "search.games.noindex", Category = TweakCategory.ServicosPrivacidade,
            Title = "Não indexar bibliotecas de jogos (Windows Search)",
            Description = "Marca as pastas da Steam/Epic como 'não indexar conteúdo', para que o indexador não varra milhares de arquivos de jogos após instalações e atualizações.",
            HonestNote = "Relevante principalmente com a pesquisa no modo 'Avançado'. A pesquisa do Iniciar continua funcionando.",
            Actions = [new IndexingExclusionAction()],
            AppliesTo = h => h.Storage.GameLibraries.Count > 0 ? Applicability.Yes : Applicability.No("Nenhuma biblioteca de jogos encontrada."),
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "gaming.dvr.off", Category = TweakCategory.ServicosPrivacidade,
            Title = "Desativar gravação em segundo plano (Game DVR)",
            Description = "Desliga a captura/gravação contínua do Xbox Game Bar. A Game Bar (Win+G) e o Modo de Jogo continuam disponíveis.",
            HonestNote = "Quando a gravação em segundo plano está ligada, o custo é real (codificador de vídeo + disco). Desligada, a diferença é nula.",
            Impact = Impact.Medio,
            Actions =
            [
                new RegistryValueAction(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegistryKind.DWord, 0),
                new RegistryValueAction(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegistryKind.DWord, 0),
                new RegistryValueAction(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", RegistryKind.DWord, 0),
            ],
            AppliesTo = NotDualX3D,
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "privacy.bgapps", Category = TweakCategory.ServicosPrivacidade,
            Title = "Bloquear apps da Store em segundo plano",
            Description = "Política LetAppsRunInBackground = Negar. Apps UWP/Store não rodam quando fechados.",
            HonestNote = "Opcional: notificações de apps da Store (Mail, Xbox, Teams Store) podem parar de chegar.",
            Risk = Risk.Moderado,
            Actions = [new RegistryValueAction(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", RegistryKind.DWord, 2)],
            Profiles = P(),
        },

        // ───────────────────────────── REDE ─────────────────────────────
        new()
        {
            Id = "net.nagle", Category = TweakCategory.Rede,
            Title = "Desativar algoritmo de Nagle / ACK atrasado",
            Description = "Define TcpAckFrequency=1 e TCPNoDelay=1 nas interfaces de rede ativas, enviando pacotes TCP pequenos imediatamente.",
            HonestNote = "Afeta SÓ jogos que usam TCP (alguns MMOs/MOBAs antigos). Shooters modernos usam UDP e não mudam.",
            Actions = [new NagleAction(Disable: true)],
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "net.throttling", Category = TweakCategory.Rede,
            Title = "Remover limitação de rede do MMCSS",
            Description = "NetworkThrottlingIndex = 0xFFFFFFFF: o Windows deixa de limitar o processamento de pacotes enquanto há áudio/vídeo multimídia tocando.",
            HonestNote = "Ajuda a evitar jitter com Discord/música abertos durante o jogo. Efeito pequeno em conexões comuns.",
            Actions = [new RegistryValueAction(RegistryHive.LocalMachine, Mmcss, "NetworkThrottlingIndex", RegistryKind.DWord, unchecked((int)0xFFFFFFFF))],
            Profiles = P(Competitivo, Desempenho),
        },
        new()
        {
            Id = "mmcss.responsiveness", Category = TweakCategory.Rede,
            Title = "SystemResponsiveness = 10%",
            Description = "Reserva 10% de CPU (em vez de 20%) para tarefas de baixa prioridade quando há tarefas multimídia registradas.",
            HonestNote = "Microajuste do agendador multimídia. Valor mínimo documentado pela Microsoft (abaixo disso, é tratado como 20).",
            Actions = [new RegistryValueAction(RegistryHive.LocalMachine, Mmcss, "SystemResponsiveness", RegistryKind.DWord, 10)],
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "mmcss.games", Category = TweakCategory.Graficos,
            Title = "Prioridade MMCSS da tarefa 'Games'",
            Description = "Priority=6, Scheduling Category=High e SFIO Priority=High para threads que se registram na tarefa Games do MMCSS.",
            HonestNote = "Só vale para jogos/engines que usam AvSetMmThreadCharacteristics(\"Games\"). Inofensivo para os demais.",
            Actions =
            [
                new RegistryValueAction(RegistryHive.LocalMachine, MmcssGames, "Priority", RegistryKind.DWord, 6),
                new RegistryValueAction(RegistryHive.LocalMachine, MmcssGames, "Scheduling Category", RegistryKind.String, "High"),
                new RegistryValueAction(RegistryHive.LocalMachine, MmcssGames, "SFIO Priority", RegistryKind.String, "High"),
                new RegistryValueAction(RegistryHive.LocalMachine, MmcssGames, "GPU Priority", RegistryKind.DWord, 8),
            ],
            Profiles = P(Competitivo, Desempenho),
        },

        // ───────────────────────────── GRÁFICOS ─────────────────────────────
        new()
        {
            Id = "gfx.gamemode", Category = TweakCategory.Graficos,
            Title = "Ativar Modo de Jogo",
            Description = "Dá prioridade ao jogo em primeiro plano e evita instalações do Windows Update e notificações durante a partida.",
            HonestNote = "Pequeno ganho de estabilidade de frametime em máquinas com muito processo em segundo plano.",
            Actions =
            [
                new RegistryValueAction(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryKind.DWord, 1),
                new RegistryValueAction(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", RegistryKind.DWord, 1),
            ],
            Profiles = P(Competitivo, Desempenho, Equilibrado),
        },
        new()
        {
            Id = "gfx.hags", Category = TweakCategory.Graficos,
            Title = "Ativar Agendamento de GPU Acelerado por Hardware (HAGS)",
            Description = "A própria GPU gerencia sua fila de trabalho (HwSchMode=2). Exigido pelo DLSS Frame Generation nas RTX 40/50.",
            HonestNote = "Pode reduzir levemente a latência e o uso de CPU; em algumas combinações de jogo e driver é neutro. Requer reinicialização.",
            Impact = Impact.Medio, RequiresReboot = true,
            Actions = [new RegistryValueAction(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryKind.DWord, 2)],
            AppliesTo = h =>
                h.Os.Build < 19041 ? Applicability.No("HAGS requer Windows 10 2004 (build 19041) ou superior.")
                : h.PrimaryGpu is not { SupportsHags: true } g ? Applicability.No($"GPU/driver sem suporte a HAGS ({h.PrimaryGpu?.Name ?? "nenhuma GPU"}).")
                : Applicability.Yes,
            Profiles = P(Competitivo, Desempenho),
        },
        new()
        {
            Id = "gfx.windowed", Category = TweakCategory.Graficos,
            Title = "Otimizações para jogos em janela (Windows 11)",
            Description = "Jogos DX10/DX11 em janela/borderless passam a usar o modelo flip, com latência e VRR semelhantes à tela cheia.",
            HonestNote = "Ganho real em jogos antigos rodando em borderless. Já vem ligado por padrão no Windows 11 24H2+.",
            Actions = [new DirectXGlobalSettingAction("SwapEffectUpgradeEnable", "1")],
            AppliesTo = h => h.Os.IsWindows11 ? Applicability.Yes : Applicability.No("Recurso exclusivo do Windows 11."),
            Profiles = P(Competitivo, Desempenho),
        },

        // ─────────────────────────── INICIALIZAÇÃO ───────────────────────────
        new()
        {
            Id = "startup.heavy", Category = TweakCategory.Inicializacao,
            Title = "Desativar launchers pesados na inicialização",
            Description = "Desativa a abertura automática (como no Gerenciador de Tarefas) de launchers e apps conhecidos por pesar no boot. Eles continuam funcionando quando você abre manualmente.",
            HonestNote = "Melhora o tempo de boot e libera RAM/CPU. Você escolhe item a item na aba Inicialização.",
            Actions = [new StartupDisableAction(KnownHeavyStartup)],
            Profiles = P(Competitivo, Desempenho),
        },

        // ───────────────────────────── LIMPEZA ─────────────────────────────
        new()
        {
            Id = "clean.temp", Category = TweakCategory.Limpeza, Reversible = false,
            Title = "Limpar arquivos temporários",
            Description = "Remove arquivos com mais de 1 dia de %TEMP% e de C:\\Windows\\Temp. Arquivos em uso são ignorados.",
            HonestNote = "Libera espaço; não aumenta FPS por si só.",
            Actions = [new CleanupAction(CleanupTarget.UserTemp, 1), new CleanupAction(CleanupTarget.WindowsTemp, 1)],
            Profiles = P(Competitivo, Desempenho, Equilibrado),
        },
        new()
        {
            Id = "clean.shaders", Category = TweakCategory.Limpeza, Reversible = false,
            Title = "Limpar caches de shader antigos",
            Description = "Remove entradas com mais de 30 dias dos caches DirectX (D3DSCache), NVIDIA (DXCache/GLCache), AMD (DxCache/VkCache) e Intel.",
            HonestNote = "Útil após atualizar o driver ou com cache corrompido. Os jogos recompilam shaders na primeira execução (pode haver engasgos temporários).",
            Actions = [new CleanupAction(CleanupTarget.ShaderCaches, 30)],
            Profiles = P(Competitivo),
        },
        new()
        {
            Id = "clean.standby", Category = TweakCategory.Limpeza, Reversible = false,
            Title = "Esvaziar a lista Standby da RAM",
            Description = "Libera de uma vez a memória em cache (Standby). Também disponível como botão avulso.",
            HonestNote = "Efeito temporário: o Windows volta a preencher o cache. Só ajuda quando há pouca RAM livre antes de abrir um jogo.",
            Actions = [new CleanupAction(CleanupTarget.StandbyList, 0)],
            AppliesTo = h => h.Memory.TotalGb <= 16.5 ? Applicability.Yes : Applicability.No("Com mais de 16 GB de RAM, o ganho é desprezível."),
            Profiles = P(Competitivo),
        },
    ];

}
