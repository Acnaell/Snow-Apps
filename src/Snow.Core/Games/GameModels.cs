using Snow.Core.Hardware;
using Snow.Core.Tweaks;

namespace Snow.Core.Games;

public enum GameStore { Steam, Epic, Riot, Outro }

/// <summary>Como o Snow trata o jogo.</summary>
public enum GameSupport
{
    /// <summary>Diagnóstico + predefinições aplicadas no arquivo do jogo, com backup.</summary>
    Editavel,
    /// <summary>Diagnóstico + passo a passo (o jogo sincroniza com a conta; editar seria desfeito).</summary>
    Guiado,
    /// <summary>Jogo detectado sem perfil próprio: só achados genéricos (disco, GPU…).</summary>
    Generico,
}

public enum GamePreset { FpsMedio, FpsAlto }

public enum HardwareTier { Entrada, Intermediario, Avancado, Entusiasta }

public sealed record InstalledGame(
    string Id,
    string Name,
    GameStore Store,
    string InstallDir,
    string? ExecutablePath = null,
    string? AntiCheat = null)
{
    public string DriveLetter => InstallDir.Length >= 2 && InstallDir[1] == ':' ? InstallDir[..1].ToUpperInvariant() : "";
}

public enum FindingLevel { Ok, Info, Melhoria, Importante }

/// <summary>Ponto de melhora encontrado num jogo.</summary>
public sealed record GameFinding(FindingLevel Level, string Title, string Detail, bool FixedByPreset = false);

/// <summary>Uma linha da prévia: o que muda no arquivo do jogo.</summary>
public sealed record SettingChange(string Label, string? Current, string Target, string Why);

/// <summary>Passo manual (jogos guiados) ou complemento que só o menu do jogo permite.</summary>
public sealed record GuidedStep(string Where, string Setting, string Value, string Why);

public sealed record GamePresetPlan(
    GamePreset Preset,
    IReadOnlyList<SettingChange> Changes,
    IReadOnlyList<TweakDefinition> Tweaks,
    IReadOnlyList<GuidedStep> ManualSteps,
    string Summary);

/// <summary>Arquivos de configuração já lidos (caminho → texto), para os perfis continuarem puros/testáveis.</summary>
public sealed record GameContext(
    InstalledGame Game,
    HardwareProfile Hardware,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlySet<string> ReadOnlyFiles,
    bool IsRunning)
{
    public HardwareTier Tier => HardwareTiers.Classify(Hardware);
    public int RefreshHz => Hardware.MonitorRefreshHz;
    public string? Text(string path) => Files.TryGetValue(path, out var t) ? t : null;
}

public interface IGameProfile
{
    string GameId { get; }
    GameSupport Support { get; }
    /// <summary>Arquivos que o scanner deve ler para este jogo.</summary>
    IReadOnlyList<string> ConfigFiles(InstalledGame game);
    /// <summary>Processos que precisam estar fechados para aplicar.</summary>
    IReadOnlyList<string> Processes { get; }
    IReadOnlyList<GameFinding> Analyze(GameContext ctx);
    GamePresetPlan BuildPreset(GameContext ctx, GamePreset preset);
}

public static class HardwareTiers
{
    /// <summary>
    /// Classificação aproximada para escolher predefinições. Baseada principalmente na GPU
    /// (VRAM e se é dedicada) e limitada por RAM e número de threads da CPU.
    /// </summary>
    public static HardwareTier Classify(HardwareProfile hw)
    {
        var gpu = hw.PrimaryGpu;
        var vramGb = (gpu?.VramBytes ?? 0) / 1024d / 1024 / 1024;
        var tier = gpu is null || gpu.IsVirtual || !gpu.IsDiscrete ? HardwareTier.Entrada
                 : vramGb >= 15.5 ? HardwareTier.Entusiasta
                 : vramGb >= 9.5 ? HardwareTier.Avancado
                 : vramGb >= 5.5 ? HardwareTier.Intermediario
                 : HardwareTier.Entrada;

        var limit = HardwareTier.Entusiasta;
        if (hw.Memory.TotalGb is > 0 and < 11.5) limit = HardwareTier.Intermediario;
        if (hw.Memory.TotalGb is > 0 and < 7.5) limit = HardwareTier.Entrada;
        if (hw.Cpu.LogicalProcessors is > 0 and < 8 && limit > HardwareTier.Intermediario) limit = HardwareTier.Intermediario;
        return tier > limit ? limit : tier;
    }

    public static string Label(this HardwareTier t) => t switch
    {
        HardwareTier.Entrada => "Entrada",
        HardwareTier.Intermediario => "Intermediário",
        HardwareTier.Avancado => "Avançado",
        _ => "Entusiasta",
    };

    public static string Label(this GamePreset p) => p == GamePreset.FpsAlto ? "FPS Alto (competitivo)" : "FPS Médio (qualidade e estabilidade)";
}
