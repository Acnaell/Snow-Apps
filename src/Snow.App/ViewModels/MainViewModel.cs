using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snow.App.Infrastructure;
using Snow.Core.Hardware;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;
using Snow.Platform;
using Snow.Platform.Executors;
using Snow.Platform.Profiling;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly OptimizationService _service;
    private readonly ILog _log = FileLog.Instance;
    private readonly StartupManager _startup = new(new WindowsRegistryStore());
    private HardwareProfile? _hw;

    public MainViewModel()
    {
        _service = OptimizationService.CreateDefault(_log);
        _log.Written += line => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 2000) LogLines.RemoveAt(0);
            OnPropertyChanged(nameof(LogText));
        });

        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsBusy);
        ApplyCommand = new RelayCommand(async () => await ApplyAsync(), () => !IsBusy && _hw is not null && Tweaks.Any(t => t.IsSelected));
        CopyReportCommand = new RelayCommand(() => { if (_hw is not null) Clipboard.SetText(Report.ToText(_hw)); });
        RestoreCommand = new RelayCommand(async p => await RestoreAsync(p as BackupViewModel), p => !IsBusy && p is BackupViewModel { CanRestore: true });
        RestoreAllCommand = new RelayCommand(async () => await RestoreAsync(null), () => !IsBusy && Backups.Any(b => b.CanRestore));
        PurgeStandbyCommand = new RelayCommand(async () => await RunBusy("Esvaziando lista Standby…", () => { CleanupExecutor.PurgeStandbyList(); _log.Info("Lista Standby esvaziada manualmente."); }), () => !IsBusy);
        OpenSystemRestoreCommand = new RelayCommand(() => Start(Path.Combine(Environment.SystemDirectory, "rstrui.exe")));
        OpenDataFolderCommand = new RelayCommand(() => Start("explorer.exe", AppPaths.DataDir));
        RefreshStartupCommand = new RelayCommand(LoadStartup);
        ToggleThemeCommand = new RelayCommand(() => { ThemeManager.Toggle(); OnPropertyChanged(nameof(ThemeLabel)); OnPropertyChanged(nameof(ThemeIcon)); });
        SelectAllRecommendedCommand = new RelayCommand(() => BuildPlan(SelectedProfile?.Profile ?? OptimizationProfile.Desempenho));

        Games = new GamesViewModel(_service, _log, LoadBackups);
        Health = new HealthViewModel(_log);
        Monitor = new MonitorViewModel(_log);
        Profiles = new ObservableCollection<ProfileCardViewModel>(ProfileCardViewModel.All());
        LastResult.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResult));
        IsAdmin = Elevation.IsAdministrator();
        LoadBackups();
        LoadStartup();
    }

    // ───────────── Estado geral ─────────────
    public bool IsAdmin { get; }
    public string Version => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string ThemeLabel => ThemeManager.Current == AppTheme.Dark ? "Modo claro" : "Modo escuro";
    public string ThemeIcon => ThemeManager.Current == AppTheme.Dark ? "" : "";

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }
    private string _busyText = "";
    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    // ───────────── Snow Gaming: jogos ─────────────
    public GamesViewModel Games { get; }
    public HealthViewModel Health { get; }
    public MonitorViewModel Monitor { get; }

    // ───────────── Diagnóstico ─────────────
    public ObservableCollection<SpecCard> Specs { get; } = [];
    public ObservableCollection<StatusRow> StatusRows { get; } = [];
    public ObservableCollection<InsightViewModel> InsightItems { get; } = [];
    private string _headline = "Analisando seu PC…";
    public string Headline { get => _headline; set => Set(ref _headline, value); }
    private string _recommendationText = "";
    public string RecommendationText { get => _recommendationText; set => Set(ref _recommendationText, value); }

    // ───────────── Otimização ─────────────
    public ObservableCollection<ProfileCardViewModel> Profiles { get; }
    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];
    private ProfileCardViewModel? _selectedProfile;
    public ProfileCardViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value) && value is not null) BuildPlan(value.Profile); }
    }
    private bool _createRestorePoint = true;
    public bool CreateRestorePoint { get => _createRestorePoint; set => Set(ref _createRestorePoint, value); }
    public string SelectionSummary
    {
        get
        {
            var sel = Tweaks.Where(t => t.IsSelected).ToList();
            return $"{sel.Count} ajuste(s) selecionado(s)" +
                   (sel.Any(t => t.RequiresReboot) ? " · requer reiniciar" : "") +
                   (sel.Any(t => t.Irreversible) ? " · inclui limpeza (irreversível)" : "");
        }
    }
    public ObservableCollection<ResultLine> LastResult { get; } = [];
    public bool HasResult => LastResult.Count > 0;

    // ───────────── Inicialização / Restaurar / Log ─────────────
    public ObservableCollection<StartupItemViewModel> StartupItems { get; } = [];
    public ObservableCollection<BackupViewModel> Backups { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public string LogText => string.Join(Environment.NewLine, LogLines);

    public ICommand RefreshCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RestoreAllCommand { get; }
    public ICommand PurgeStandbyCommand { get; }
    public ICommand OpenSystemRestoreCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand RefreshStartupCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SelectAllRecommendedCommand { get; }

    // ───────────── Lógica ─────────────
    public async Task RefreshAsync()
    {
        IsBusy = true; BusyText = "Lendo hardware (CPU, GPU, RAM, discos, Windows)…";
        try
        {
            _hw = await Task.Run(HardwareProfiler.Collect);
            FillDiagnostics(_hw);
            var rec = RecommendationEngine.Recommend(_hw);
            foreach (var p in Profiles) p.IsRecommended = p.Profile == rec.Profile;
            SelectedProfile = Profiles.First(p => p.Profile == rec.Profile);
            BuildPlan(rec.Profile);
            _log.Info($"Diagnóstico concluído: {_hw.Cpu.Name} · {_hw.PrimaryGpu?.Name} · {_hw.Os.FriendlyName}");
            BusyText = "Procurando jogos instalados…";
            await Games.LoadAsync(_hw);
            BusyText = "Snow Health: lendo discos e registros de eventos…";
            await Health.LoadAsync();
        }
        catch (Exception e)
        {
            _log.Error("Diagnóstico falhou: " + e);
            Headline = "Não foi possível concluir o diagnóstico. Veja o Log.";
        }
        finally { IsBusy = false; BusyText = ""; }
    }

    private void FillDiagnostics(HardwareProfile hw)
    {
        Headline = $"{(hw.State.IsLaptop ? "Notebook" : "Desktop")} · {hw.Os.FriendlyName}";
        Specs.Clear();
        var c = hw.Cpu; var m = hw.Memory; var g = hw.PrimaryGpu;
        Specs.Add(new("", "Processador", c.Name, $"{Report.CpuArch(c)}\n{c.PhysicalCores} núcleos · {c.LogicalProcessors} threads · virtualização {(c.VirtualizationFirmwareEnabled || c.HypervisorPresent ? "ativa" : "desativada")}"));
        Specs.Add(new("", "Placa de vídeo", g?.Name ?? "Não detectada",
            g is null ? "" : $"{Report.Gb(g.VramBytes)} VRAM · driver {g.DriverVersionFriendly}\n{(g.IsDiscrete ? "Dedicada" : g.IsVirtual ? "Adaptador virtual" : "Integrada")}{(g.SupportsHags ? " · suporta HAGS" : "")}" +
                        (hw.Gpus.Count > 1 ? $"\n+ {string.Join(", ", hw.Gpus.Where(x => x != g).Select(x => x.Name))}" : "")));
        Specs.Add(new("", "Memória RAM", $"{Report.Gb(m.TotalBytes)} {(m.Type == "Desconhecido" ? "" : m.Type)}".Trim(),
            $"{Report.Speed(m)} · {ChannelText(m)} · {m.Modules.Count} módulo(s)\n{m.UsedPercent:0}% em uso agora"));
        var sys = hw.Storage.SystemDisk;
        Specs.Add(new("", "Armazenamento", sys is null ? "Desconhecido" : $"{sys.Kind.Label()} (Windows)",
            string.Join("\n", hw.Storage.Disks.Select(d => $"{string.Join(",", d.DriveLetters.Select(l => l + ":"))} {d.Kind.Label()} · {Report.Gb(d.SizeBytes)} · {d.Model}"))
            + (hw.Storage.GameLibraries.Count > 0 ? $"\nJogos: {string.Join(" | ", hw.Storage.GameLibraries)}" : "")));

        StatusRows.Clear();
        var s = hw.State;
        StatusRows.Add(new("Plano de energia", s.ActivePowerPlanName, !s.ActivePowerPlanGuid.StartsWith("a1841308", StringComparison.OrdinalIgnoreCase)));
        StatusRows.Add(new("Modo de Jogo", s.GameMode.Label(), s.GameMode != TriState.Off));
        StatusRows.Add(new("HAGS (agendamento de GPU)", s.Hags.Label(), s.Hags == TriState.On || g is not { SupportsHags: true }));
        if (hw.Os.IsWindows11) StatusRows.Add(new("Otimizações para jogos em janela", s.WindowedGameOptimizations.Label(), s.WindowedGameOptimizations != TriState.Off));
        StatusRows.Add(new("Gravação em 2º plano (Game DVR)", s.GameDvrBackgroundRecording.Label(), s.GameDvrBackgroundRecording != TriState.On));
        StatusRows.Add(new("Integridade de memória (HVCI)", s.MemoryIntegrity.Label(), true));
        StatusRows.Add(new("SysMain / Telemetria", $"{s.SysMainStart} / {s.DiagTrackStart}", true));

        InsightItems.Clear();
        foreach (var i in Insights.Analyze(hw)) InsightItems.Add(new InsightViewModel(i));

        var rec = RecommendationEngine.Recommend(hw);
        RecommendationText = $"Recomendado: {rec.Profile.Label()}\n" + string.Join("\n", rec.Reasons.Select(r => "• " + r));
    }

    private static string ChannelText(MemoryInfo m) => m.Channels switch
    {
        MemoryChannelMode.Single => "single channel",
        MemoryChannelMode.Dual => "dual channel",
        MemoryChannelMode.Quad => "quad channel",
        _ => "canais desconhecidos",
    } + (m.ChannelsEstimated && m.Channels != MemoryChannelMode.Unknown ? " (estimado)" : "");

    private void BuildPlan(OptimizationProfile profile)
    {
        if (_hw is null) return;
        var plan = RecommendationEngine.BuildPlan(_hw, profile);
        Tweaks.Clear();
        foreach (var p in plan.Items) Tweaks.Add(new TweakItemViewModel(p, OnTweakChanged));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void OnTweakChanged(TweakItemViewModel changed)
    {
        // Ajustes do mesmo grupo (ex.: planos de energia) são mutuamente exclusivos.
        if (changed.IsSelected && changed.Definition.Group is { } g)
            foreach (var other in Tweaks.Where(t => t != changed && t.Definition.Group == g && t.IsSelected))
                other.SetSelectedSilently(false);
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private async Task ApplyAsync()
    {
        if (_hw is null || SelectedProfile is null) return;
        var plan = RecommendationEngine.BuildPlan(_hw, SelectedProfile.Profile)
            .WithSelection(Tweaks.Where(t => t.IsSelected).Select(t => t.Id));
        var list = string.Join("\n", plan.Selected.Select(t => "• " + t.Definition.Title));
        var msg = $"Aplicar {plan.Selected.Count()} ajuste(s) do perfil {SelectedProfile.Title}?\n\n{list}\n\n" +
                  "Um backup de cada valor original será salvo e você pode reverter tudo na aba Restaurar." +
                  (CreateRestorePoint ? "\nUm ponto de restauração do Windows também será criado." : "");
        if (MessageBox.Show(msg, "Snow — confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        IsBusy = true; Progress = 0; LastResult.Clear();
        try
        {
            var progress = new Progress<ProgressInfo>(p =>
            {
                BusyText = p.Message;
                Progress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            });
            var rp = CreateRestorePoint;
            var session = await Task.Run(() => _service.Apply(plan, new ApplyOptions(rp), progress));
            ShowResult(session);
            LoadBackups();
            LoadStartup();
            await RefreshDiagnosticsOnlyAsync();
            if (plan.RequiresReboot &&
                MessageBox.Show("Alguns ajustes (HAGS) só valem após reiniciar. Reiniciar agora?", "Snow", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Start("shutdown.exe", "/r /t 5 /c \"Snow: concluindo otimizações\"");
        }
        catch (Exception e)
        {
            _log.Error("Aplicação falhou: " + e);
            MessageBox.Show(e.Message, "Snow — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; BusyText = ""; }
    }

    private async Task RefreshDiagnosticsOnlyAsync()
    {
        try
        {
            var hw = await Task.Run(HardwareProfiler.Collect);
            _hw = hw;
            FillDiagnostics(hw);
        }
        catch (Exception e) { _log.Error(e.Message); }
    }

    private void ShowResult(BackupSession s)
    {
        Brush B(string key) => (Brush)Application.Current.FindResource(key);
        LastResult.Clear();
        LastResult.Add(new($"Backup {s.Id} · ponto de restauração: {s.RestorePoint ?? "não solicitado"}", B("MutedBrush")));
        foreach (var t in s.Tweaks)
        {
            var (icon, brush) = t.Status switch
            {
                TweakStatus.Applied => ("✔", B("GoodBrush")),
                TweakStatus.PartiallyApplied => ("◐", B("GoodBrush")),
                TweakStatus.Skipped => ("↷", B("MutedBrush")),
                _ => ("✖", B("BadBrush")),
            };
            LastResult.Add(new($"{icon} {t.Title}" + (t.Status is TweakStatus.Skipped or TweakStatus.Failed ? $" — {t.Messages.LastOrDefault()?.Split('—').Last().Trim()}" : ""), brush));
        }
    }

    private async Task RestoreAsync(BackupViewModel? b)
    {
        var what = b is null ? "TODAS as otimizações ativas" : $"o backup {b.Title}";
        if (MessageBox.Show($"Reverter {what} para os valores originais?", "Snow — restaurar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunBusy("Restaurando valores originais…", () =>
        {
            var n = b is null ? _service.RestoreAll() : _service.Restore(b.Session);
            _log.Info($"{n} alteração(ões) revertida(s).");
        });
        LoadBackups();
        LoadStartup();
        await RefreshDiagnosticsOnlyAsync();
        BuildPlan(SelectedProfile?.Profile ?? OptimizationProfile.Desempenho);
    }

    private async Task RunBusy(string text, Action work)
    {
        IsBusy = true; BusyText = text;
        try { await Task.Run(work); }
        catch (Exception e)
        {
            _log.Error(e.ToString());
            MessageBox.Show(e.Message, "Snow — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; BusyText = ""; }
    }

    private void LoadBackups()
    {
        Backups.Clear();
        foreach (var s in _service.Backups.List()) Backups.Add(new BackupViewModel(s));
    }

    private void LoadStartup()
    {
        StartupItems.Clear();
        try
        {
            foreach (var i in _startup.List().OrderByDescending(i => TweakCatalog.KnownHeavyStartup.Any(p => i.Name.Contains(p, StringComparison.OrdinalIgnoreCase))).ThenBy(i => i.Name))
                StartupItems.Add(new StartupItemViewModel(i, _startup, _log));
        }
        catch (Exception e) { _log.Error("Inicialização: " + e.Message); }
    }

    private static void Start(string file, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(e.Message, "Snow", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
