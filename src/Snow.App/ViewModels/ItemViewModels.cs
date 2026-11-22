using System.Windows.Media;
using Snow.App.Infrastructure;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;
using Snow.Platform.Executors;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

public sealed record SpecCard(string Icon, string Title, string Value, string Detail);
public sealed record StatusRow(string Label, string Value, bool Good);

public sealed class ProfileCardViewModel(OptimizationProfile profile, string icon, string title, string subtitle, IReadOnlyList<string> bullets) : ObservableObject
{
    public OptimizationProfile Profile { get; } = profile;
    public string Icon { get; } = icon;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public IReadOnlyList<string> Bullets { get; } = bullets;

    private bool _isRecommended;
    public bool IsRecommended { get => _isRecommended; set => Set(ref _isRecommended, value); }

    public static IReadOnlyList<ProfileCardViewModel> All() =>
    [
        new(OptimizationProfile.Competitivo, "", "Alto Desempenho", "Competitivo · FPS máximo · menor latência",
            ["Plano Desempenho Máximo e sem core parking", "Telemetria, SysMain (só com SSD) e Game DVR desligados", "Rede sem Nagle/limitação MMCSS", "HAGS e otimizações de janela", "Limpeza de temporários e shaders antigos"]),
        new(OptimizationProfile.Desempenho, "", "Desempenho", "Estável e fluido, sem abrir mão de recursos",
            ["Alto Desempenho / Melhor desempenho (notebook)", "Mantém Bluetooth, impressora e Game Bar", "Só desativa telemetria e launchers pesados", "HAGS, Modo de Jogo e MMCSS", "Limpeza de temporários"]),
        new(OptimizationProfile.Equilibrado, "", "Equilibrado", "Uso diário, notebook na bateria",
            ["Plano Equilibrado (padrão Microsoft)", "Modo de Jogo ativado", "Nenhum serviço desativado", "Limpeza de temporários", "Ideal para reverter excessos"]),
    ];
}

public sealed class TweakItemViewModel : ObservableObject
{
    private readonly Action<TweakItemViewModel> _onChanged;
    private bool _isSelected;

    public TweakItemViewModel(PlannedTweak planned, Action<TweakItemViewModel> onChanged, TweakState? state = null)
    {
        Definition = planned.Definition;
        State = state?.State ?? AppliedState.Desconhecido;
        LastApplied = state?.LastAppliedUtc?.ToLocalTime();
        // Já valendo no PC: não marca de novo (dá para marcar à mão se quiser reaplicar).
        _isSelected = planned.Selected && State != AppliedState.Aplicado;
        IsApplicable = planned.Applicability.IsApplicable;
        Reason = planned.Applicability.Reason ?? planned.Note;
        _onChanged = onChanged;
    }

    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string HonestNote => Definition.HonestNote;
    public string Category => Definition.Category switch
    {
        TweakCategory.Energia => "Energia e CPU",
        TweakCategory.ServicosPrivacidade => "Serviços e privacidade",
        TweakCategory.Rede => "Rede e agendador multimídia",
        TweakCategory.Graficos => "Gráficos e jogos",
        TweakCategory.Inicializacao => "Inicialização",
        _ => "Limpeza",
    };
    public string ActionsText => string.Join("\n", Definition.Actions.Select(a => "→ " + a.Describe()));
    public bool IsApplicable { get; }
    public string? Reason { get; }
    public string ImpactText => $"Impacto {Definition.Impact switch { Impact.Alto => "alto", Impact.Medio => "médio", _ => "baixo" }}";
    public bool IsRisky => Definition.Risk != Risk.Seguro;
    public bool RequiresReboot => Definition.RequiresReboot;
    public bool Irreversible => !Definition.Reversible;
    public AppliedState State { get; }
    public DateTime? LastApplied { get; }
    public bool IsAlreadyApplied => State == AppliedState.Aplicado;
    public bool WasUndone => State == AppliedState.Desfeito;
    public bool HasState => State is AppliedState.Aplicado or AppliedState.Desfeito;
    public string StateText => State switch
    {
        AppliedState.Aplicado => "✔ Já está aplicado neste PC" + (LastApplied is { } d ? $" (pelo Snow em {d:dd/MM})" : ""),
        AppliedState.Desfeito => $"↺ O Snow aplicou{(LastApplied is { } d ? $" em {d:dd/MM}" : "")}, mas o valor voltou ao anterior (atualização do Windows, driver ou outro programa). Aplique de novo.",
        _ => "",
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsApplicable) value = false;
            if (Set(ref _isSelected, value)) _onChanged(this);
        }
    }

    /// <summary>Usado para desmarcar sem disparar a regra de grupo novamente.</summary>
    public void SetSelectedSilently(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}

public sealed class StartupItemViewModel(StartupItem item, StartupManager manager, ILog log) : ObservableObject
{
    private bool _enabled = item.Enabled;
    public string Name => item.Name;
    public string Location => item.Location;
    public string Command => item.Command;
    public bool IsKnownHeavy { get; } = TweakCatalog.KnownHeavyStartup.Any(p =>
        item.Name.Contains(p, StringComparison.OrdinalIgnoreCase) || item.Command.Contains(p, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            try
            {
                manager.SetEnabled(item, value);
                log.Info($"Inicialização: {item.Name} → {(value ? "ativado" : "desativado")}");
            }
            catch (Exception e)
            {
                log.Error($"Inicialização: falha em {item.Name}: {e.Message}");
                _enabled = !value;
                OnPropertyChanged();
            }
        }
    }
}

public sealed class BackupViewModel(BackupSession s)
{
    public BackupSession Session { get; } = s;
    public string Title => $"{s.Profile} · {s.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}";
    public string Detail => $"{s.Tweaks.Count(t => t.Snapshots.Count > 0)} ajuste(s) com backup · ponto de restauração: {s.RestorePoint ?? "não solicitado"}";
    public bool CanRestore => s.RestoredUtc is null && s.HasRevertibleChanges;
    public string State => s.RestoredUtc is { } r ? $"Restaurado em {r.ToLocalTime():dd/MM HH:mm}" : CanRestore ? "Ativo" : "Sem alterações reversíveis";
}

public sealed record InsightViewModel(Insight Insight)
{
    public string Title => Insight.Title;
    public string Detail => Insight.Detail;
    public string Icon => Insight.Level switch { InsightLevel.Critico => "", InsightLevel.Atencao => "", _ => "" };
    public string Level => Insight.Level.ToString();
}

public sealed record ResultLine(string Text, Brush Brush);
