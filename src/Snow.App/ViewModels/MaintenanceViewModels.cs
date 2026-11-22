using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Snow.App.Infrastructure;
using Snow.Core.Health;
using Snow.Platform.Executors;
using Snow.Platform.Health;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

// ───────────────────────────── Limpeza ─────────────────────────────

public sealed class CleanupItemViewModel(CleanupCategory c) : ObservableObject
{
    public CleanupCategory Category { get; } = c;
    public string Title => c.Title;
    public string Description => c.Description;
    public string? Note => c.Note;
    public bool HasNote => !string.IsNullOrEmpty(c.Note);
    public bool IsManual => c.Kind == CleanupKind.Manual;

    private bool _selected = c.DefaultOn;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private CleanupMeasure? _measure;
    public CleanupMeasure? Measure
    {
        get => _measure;
        set { Set(ref _measure, value); OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(Skipped)); OnPropertyChanged(nameof(HasSkipped)); OnPropertyChanged(nameof(IsEmpty)); }
    }

    public string SizeText => _measure is null ? "…" : _measure.Bytes == 0 ? "vazio" : CleanupCatalog.Size(_measure.Bytes);
    public string? Skipped => _measure?.Skipped;
    public bool HasSkipped => !string.IsNullOrEmpty(_measure?.Skipped);
    public bool IsEmpty => _measure is { Bytes: 0 };
}

public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly ILog _log;

    public MaintenanceViewModel(ILog log)
    {
        _log = log;
        foreach (var c in CleanupCatalog.All) Items.Add(new CleanupItemViewModel(c));
        foreach (var i in Items) i.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CleanupItemViewModel.Selected)) OnPropertyChanged(nameof(SelectedTotal)); };
        NotCleaned = CleanupCatalog.NotCleaned.Select(n => new KeyValuePair<string, string>(n.What, n.Why)).ToList();
        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsBusy);
        CleanCommand = new RelayCommand(async () => await CleanAsync(), () => !IsBusy);
        OpenManualCommand = new RelayCommand(p => { if (p is CleanupItemViewModel { Category.ManualTarget: { } t }) Start(t); });
        EnableTrimCommand = new RelayCommand(async () => await Run("Ligando TRIM…", MaintenanceService.EnableTrim), () => !IsBusy);
        EnableScheduleCommand = new RelayCommand(async () => await Run("Religando otimização semanal…", MaintenanceService.EnableScheduledOptimization), () => !IsBusy);
        OptimizeCommand = new RelayCommand(async () => await Run("Otimizando unidades (TRIM nos SSDs, desfragmentação nos HDs)… pode levar alguns minutos.",
            () => MaintenanceService.OptimizeDrives(TimeSpan.FromMinutes(60))), () => !IsBusy);
        ComponentsCommand = new RelayCommand(async () =>
        {
            if (MessageBox.Show("Limpar componentes antigos do Windows (WinSxS)?\n\nLeva de 5 a 20 minutos e, depois disso, não dá para desinstalar as atualizações que já estão instaladas.",
                    "Snow Health", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await Run("Limpando componentes antigos do Windows… (5 a 20 minutos)", () => MaintenanceService.CleanComponents(TimeSpan.FromMinutes(45)));
        }, () => !IsBusy);
    }

    public ObservableCollection<CleanupItemViewModel> Items { get; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> NotCleaned { get; }
    public ICommand ScanCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand OpenManualCommand { get; }
    public ICommand EnableTrimCommand { get; }
    public ICommand EnableScheduleCommand { get; }
    public ICommand OptimizeCommand { get; }
    public ICommand ComponentsCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }

    private string _status = "Clique em \"Medir\" para ver quanto espaço dá para liberar.";
    public string Status { get => _status; set => Set(ref _status, value); }

    public string SelectedTotal
    {
        get
        {
            var sel = Items.Where(i => i.Selected && !i.IsManual && i.Measure is { Skipped: null }).Sum(i => i.Measure!.Bytes);
            return Items.All(i => i.Measure is null) ? "" : $"Selecionado: {CleanupCatalog.Size(sel)}";
        }
    }

    private DriveCare? _drives;
    public DriveCare? Drives
    {
        get => _drives;
        set
        {
            Set(ref _drives, value);
            OnPropertyChanged(nameof(TrimOff)); OnPropertyChanged(nameof(ScheduleOff)); OnPropertyChanged(nameof(DrivesText));
        }
    }
    public bool TrimOff => _drives?.TrimEnabled == false;
    public bool ScheduleOff => _drives?.ScheduledOptimization == false;
    public string DrivesText => _drives is null ? "Verificando TRIM e otimização semanal…"
        : $"TRIM: {(_drives.TrimEnabled switch { true => "ligado", false => "desligado", _ => "desconhecido" })} · Otimização semanal do Windows: {(_drives.ScheduledOptimization switch { true => "ligada", false => "desligada", _ => "desconhecida" })}";

    public async Task ScanAsync()
    {
        IsBusy = true;
        Status = "Medindo…";
        try
        {
            foreach (var i in Items) i.Measure = null;
            foreach (var i in Items)
                i.Measure = await Task.Run(() => MaintenanceService.Measure(i.Category));
            Drives ??= await Task.Run(MaintenanceService.ReadDriveCare);
            var total = Items.Where(i => !i.IsManual && i.Measure is { Skipped: null }).Sum(i => i.Measure!.Bytes);
            Status = $"Dá para liberar até {CleanupCatalog.Size(total)}. Os itens marcados são seguros; os desmarcados têm um porém, explicado em cada um.";
            OnPropertyChanged(nameof(SelectedTotal));
        }
        catch (Exception e) { _log.Error("Limpeza: medição falhou: " + e.Message); Status = "Não foi possível medir: " + e.Message; }
        finally { IsBusy = false; }
    }

    private async Task CleanAsync()
    {
        var chosen = Items.Where(i => i.Selected && !i.IsManual).ToList();
        if (chosen.Count == 0) { Status = "Marque pelo menos um item."; return; }
        if (chosen.Any(i => i.Category.Kind == CleanupKind.RecycleBin) &&
            MessageBox.Show("A Lixeira será esvaziada e não dá para desfazer. Continuar?", "Snow Health", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        IsBusy = true;
        long files = 0, bytes = 0;
        var skipped = new List<string>();
        try
        {
            foreach (var i in chosen)
            {
                Status = $"Limpando: {i.Title}…";
                var r = await Task.Run(() => MaintenanceService.Clean(i.Category));
                files += r.Files; bytes += r.Bytes;
                if (r.Skipped is not null) skipped.Add($"{i.Title} ({r.Skipped})");
                _log.Info($"Limpeza: {i.Title}: {r.Files:N0} arquivo(s), {CleanupCatalog.Size(r.Bytes)}" + (r.Failed > 0 ? $", {r.Failed} em uso" : "") + (r.Skipped is null ? "" : $", pulado: {r.Skipped}"));
            }
            Status = $"Liberado: {CleanupCatalog.Size(bytes)} ({files:N0} arquivos)." + (skipped.Count > 0 ? " Pulados: " + string.Join("; ", skipped) + "." : "");
            var final = Status;
            IsBusy = false;
            await ScanAsync();
            Status = final;
        }
        catch (Exception e) { _log.Error("Limpeza falhou: " + e.Message); Status = "Falhou: " + e.Message; }
        finally { IsBusy = false; }
    }

    private async Task Run(string busy, Func<string> work)
    {
        IsBusy = true;
        Status = busy;
        try
        {
            var msg = await Task.Run(work);
            Status = msg;
            _log.Info("Manutenção: " + msg);
            Drives = await Task.Run(MaintenanceService.ReadDriveCare);
        }
        catch (Exception e) { Status = "Falhou: " + e.Message; _log.Error("Manutenção: " + e.Message); }
        finally { IsBusy = false; }
    }

    private void Start(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception e) { _log.Error(e.Message); }
    }
}

// ───────────────────────────── Processos ─────────────────────────────

public sealed class ProcessItemViewModel(ProcessFinding f, StartupItem? startup) : ObservableObject
{
    public ProcessFinding Finding { get; } = f;
    public StartupItem? Startup { get; } = startup;
    public string App => f.Usage.App;
    public string Display => f.Display.Equals(f.Usage.App, StringComparison.OrdinalIgnoreCase) ? f.Usage.App : $"{f.Display}  ·  {f.Usage.App}";
    public string Usage => $"CPU {f.Usage.CpuAvg:0.#}% (pico {f.Usage.CpuMax:0}%)  ·  RAM {CleanupCatalog.Size(f.Usage.PrivateBytes)}" + (f.Usage.Instances > 1 ? $"  ·  {f.Usage.Instances} processos" : "");
    public string Advice => f.Advice;
    public string Level => f.Level.ToString();
    public string Kind => f.Kind switch
    {
        ProcessKind.Jogo => "Jogo",
        ProcessKind.AntiCheat => "Anti-cheat",
        ProcessKind.Sistema => "Windows",
        ProcessKind.Seguranca => "Antivírus",
        ProcessKind.Conhecido => "App",
        _ => "Desconhecido",
    };

    private bool _closed;
    public bool Closed { get => _closed; set { Set(ref _closed, value); OnPropertyChanged(nameof(CanClose)); } }
    public bool CanClose => f.CanClose && !_closed;

    private bool _startupOn = startup?.Enabled == true;
    public bool StartupOn { get => _startupOn; set => Set(ref _startupOn, value); }
}

public sealed class ProcessesViewModel : ObservableObject
{
    private readonly ILog _log;
    private readonly StartupManager _startup;
    private readonly Action _startupChanged;
    private CancellationTokenSource? _cts;

    public ProcessesViewModel(ILog log, StartupManager startup, Action startupChanged)
    {
        _log = log; _startup = startup; _startupChanged = startupChanged;
        AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(10), () => !IsBusy);
        CloseCommand = new RelayCommand(async p => { if (p is ProcessItemViewModel i) await CloseAsync(i); });
        DisableStartupCommand = new RelayCommand(p => { if (p is ProcessItemViewModel i) DisableStartup(i); });
    }

    public ObservableCollection<ProcessItemViewModel> Items { get; } = [];
    public ICommand AnalyzeCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand DisableStartupCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }

    private string _summary = "Com os jogos fechados, clique em \"Analisar\". Em 10 segundos o Snow mostra o que roda em segundo plano, quanto pesa e o que fazer.";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool HasItems => Items.Count > 0;

    public async Task AnalyzeAsync(int seconds)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Items.Clear(); OnPropertyChanged(nameof(HasItems));
        Status = "";
        try
        {
            var report = await Task.Run(() => MaintenanceService.SampleProcesses(seconds, _cts.Token,
                t => Application.Current?.Dispatcher.BeginInvoke(() => Summary = $"Medindo… {t}/{seconds} s")));
            List<StartupItem> startup = [];
            try { startup = _startup.List().ToList(); } catch { }
            var commands = startup.Select(s => s.Command).ToList();
            foreach (var f in report.Findings)
            {
                var idx = ProcessAdvisor.MatchStartup(f.Usage.App, commands);
                Items.Add(new ProcessItemViewModel(f, idx is { } i ? startup[i] : null));
            }
            Summary = $"{report.Summary} Memória usada em segundo plano: {CleanupCatalog.Size(report.BackgroundBytes)}.";
            _log.Info($"Processos: {report.BackgroundCpu:0.#}% CPU em segundo plano em {seconds} s; {report.Findings.Count} item(ns).");
        }
        catch (Exception e) { Summary = "Não foi possível medir: " + e.Message; _log.Error("Processos: " + e); }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasItems)); }
    }

    private async Task CloseAsync(ProcessItemViewModel i)
    {
        var r = MessageBox.Show($"Fechar {i.Finding.Display} ({i.App})?\n\nSalve o que estiver aberto nele antes.\n\nSim: fecha e, se não responder em 3 s, encerra.\nNão: só pede para fechar (o app pode perguntar se quer salvar).",
            "Snow Health", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return;
        var msg = await Task.Run(() => MaintenanceService.CloseApp(i.App, force: r == MessageBoxResult.Yes));
        Status = msg;
        _log.Info("Processos: " + msg);
        if (msg.Contains("encerrado") || msg.Contains("já está fechado")) i.Closed = true;
    }

    private void DisableStartup(ProcessItemViewModel i)
    {
        if (i.Startup is null) return;
        try
        {
            _startup.SetEnabled(i.Startup, false);
            i.StartupOn = false;
            Status = $"{i.Startup.Name} não abre mais com o Windows (dá para religar na aba Inicialização).";
            _log.Info($"Inicialização: {i.Startup.Name} → desativado (pela análise de processos)");
            _startupChanged();
        }
        catch (Exception e) { Status = "Falhou: " + e.Message; _log.Error(e.Message); }
    }
}
