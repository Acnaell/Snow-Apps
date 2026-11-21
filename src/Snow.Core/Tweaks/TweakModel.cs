using Snow.Core.Hardware;

namespace Snow.Core.Tweaks;

public enum TweakCategory { Energia, ServicosPrivacidade, Rede, Graficos, Inicializacao, Limpeza }
public enum Impact { Baixo, Medio, Alto }
public enum Risk { Seguro, Moderado, Avancado }
public enum OptimizationProfile { Competitivo, Desempenho, Equilibrado }

public enum RegistryHive { LocalMachine, CurrentUser }
public enum RegistryKind { DWord, QWord, String, ExpandString, MultiString, Binary }
public enum ServiceStart { Boot = 0, System = 1, Automatic = 2, Manual = 3, Disabled = 4, AutomaticDelayed = 99 }
public enum PowerPlan { Balanced, HighPerformance, UltimatePerformance }
public enum PowerOverlay { BetterBattery, Balanced, BetterPerformance, BestPerformance }
public enum CleanupTarget { UserTemp, WindowsTemp, ShaderCaches, StandbyList }

/// <summary>Uma alteração atômica, capturável e (quase sempre) reversível.</summary>
public abstract record TweakAction
{
    public abstract string Describe();
}

/// <summary>Value == null significa "remover o valor".</summary>
public sealed record RegistryValueAction(RegistryHive Hive, string Path, string Name, RegistryKind Kind, object? Value) : TweakAction
{
    public override string Describe() =>
        $@"{(Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{Path} → {Name} = {(Value is null ? "(removido)" : FormatValue())}";

    private string FormatValue() => Kind switch
    {
        RegistryKind.DWord when Value is int i => $"{i} (0x{unchecked((uint)i):X})",
        RegistryKind.DWord when Value is uint u => $"{u} (0x{u:X})",
        _ => $"\"{Value}\"",
    };
}

public sealed record ServiceStartAction(string ServiceName, ServiceStart Start, bool StopNow) : TweakAction
{
    public override string Describe() => $"Serviço {ServiceName} → {Start}{(StopNow ? " (e parar agora)" : "")}";
}

public sealed record PowerPlanAction(PowerPlan Plan) : TweakAction
{
    public override string Describe() => Plan switch
    {
        PowerPlan.UltimatePerformance => "Plano de energia → Desempenho Máximo (Ultimate Performance)",
        PowerPlan.HighPerformance => "Plano de energia → Alto Desempenho",
        _ => "Plano de energia → Equilibrado",
    };
}

/// <summary>Altera um índice do plano ativo (AC = na tomada). Guids do powrprof.</summary>
public sealed record PowerSettingAction(Guid SubGroup, Guid Setting, uint AcValue, string Label) : TweakAction
{
    public override string Describe() => $"Plano ativo: {Label} (na tomada) → {AcValue}";
}

/// <summary>"Modo de energia" do Windows 10/11 (overlay sobre o plano Equilibrado).</summary>
public sealed record PowerOverlayAction(PowerOverlay Overlay) : TweakAction
{
    public override string Describe() => $"Modo de energia → {Overlay switch
    {
        PowerOverlay.BestPerformance => "Melhor desempenho",
        PowerOverlay.BetterPerformance => "Desempenho equilibrado",
        PowerOverlay.BetterBattery => "Maior eficiência",
        _ => "Equilibrado",
    }}";
}

/// <summary>TcpAckFrequency=1 e TCPNoDelay=1 em todas as interfaces com IP.</summary>
public sealed record NagleAction(bool Disable) : TweakAction
{
    public override string Describe() => Disable
        ? @"Todas as interfaces TCP/IP ativas → TcpAckFrequency=1, TCPNoDelay=1 (desativa Nagle/ACK atrasado)"
        : "Restaurar Nagle padrão";
}

/// <summary>Chave dentro de HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings.</summary>
public sealed record DirectXGlobalSettingAction(string Key, string Value) : TweakAction
{
    public override string Describe() => $"DirectXUserGlobalSettings → {Key}={Value};";
}

/// <summary>Desativa (via StartupApproved, igual ao Gerenciador de Tarefas) itens de inicialização que casam com os padrões.</summary>
public sealed record StartupDisableAction(IReadOnlyList<string> NamePatterns) : TweakAction
{
    public override string Describe() => $"Desativar na inicialização: {string.Join(", ", NamePatterns)} (se presentes)";
}

/// <summary>Marca as bibliotecas de jogos como "não indexar conteúdo" (FILE_ATTRIBUTE_NOT_CONTENT_INDEXED).</summary>
public sealed record IndexingExclusionAction : TweakAction
{
    public override string Describe() => "Bibliotecas de jogos → não indexar conteúdo (Windows Search)";
}

/// <summary>Limpeza: irreversível por natureza.</summary>
public sealed record CleanupAction(CleanupTarget Target, int MinAgeDays) : TweakAction
{
    public override string Describe() => Target switch
    {
        CleanupTarget.UserTemp => $"Apagar arquivos de %TEMP% com mais de {MinAgeDays} dia(s)",
        CleanupTarget.WindowsTemp => $@"Apagar arquivos de C:\Windows\Temp com mais de {MinAgeDays} dia(s)",
        CleanupTarget.ShaderCaches => $"Apagar caches de shader (DirectX/NVIDIA/AMD/Intel) com mais de {MinAgeDays} dias",
        CleanupTarget.StandbyList => "Esvaziar a lista Standby da RAM (uma vez)",
        _ => Target.ToString(),
    };
}

public sealed record Applicability(bool IsApplicable, string? Reason = null)
{
    public static readonly Applicability Yes = new(true);
    public static Applicability No(string reason) => new(false, reason);
}

public sealed record TweakDefinition
{
    public required string Id { get; init; }
    /// <summary>Ajustes do mesmo grupo são mutuamente exclusivos.</summary>
    public string? Group { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>Expectativa realista de ganho; sem promessas mágicas.</summary>
    public required string HonestNote { get; init; }
    public required TweakCategory Category { get; init; }
    public Impact Impact { get; init; } = Impact.Baixo;
    public Risk Risk { get; init; } = Risk.Seguro;
    public bool RequiresReboot { get; init; }
    public bool Reversible { get; init; } = true;
    public required IReadOnlyList<TweakAction> Actions { get; init; }
    public Func<HardwareProfile, Applicability> AppliesTo { get; init; } = _ => Applicability.Yes;
    /// <summary>Perfis em que o ajuste vem marcado por padrão.</summary>
    public IReadOnlySet<OptimizationProfile> Profiles { get; init; } = new HashSet<OptimizationProfile>();
}
