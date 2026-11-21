using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Snow.App.Infrastructure;
using Snow.Core.Hardware;
using Snow.Core.Health;
using Snow.Core.Monitor;
using Snow.Core.Recommendation;
using Snow.Platform.Monitor;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

public sealed record MonitorGame(string Key, string Name, string? Process);
public sealed record ShareRow(string Label, double Percent);
public sealed record UpgradeRow(UpgradeAdvice Advice)
{
    public string Priority => Advice.Priority.Label();
    public string Component => Advice.Component;
    public string Title => Advice.Title;
    public string Detail => Advice.Detail;
    public bool IsHigh => Advice.Priority == UpgradePriority.Alta;
    public bool IsAvoid => Advice.Priority == UpgradePriority.Evitar;
}

public sealed class MonitorViewModel : ObservableObject
{
    private readonly ILog _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PdhSampler? _pdh;
    private PresentMonLive? _pm;
    private readonly List<UsageSample> _samples = [];
    private string? _compareBefore, _compareAfter;
    private double? _fpsCap;
    private string? _activeApp;
    private Snow.Core.Monitor.SessionSummary? _lastSession;

    /// <summary>Hardware do diagnóstico: taxa do monitor (limite de FPS) e sugestões de upgrade.</summary>
    public HardwareProfile? Hardware { get => _hardware; set { _hardware = value; RefreshUpgrades(); } }
    private HardwareProfile? _hardware;
    public int RefreshHz => Hardware?.MonitorRefreshHz ?? 0;
    /// <summary>Discos do Snow Health (erros SMART), quando já analisado.</summary>
    public Func<IReadOnlyList<DiskHealthData>?>? DiskHealth { get; set; }

    public ObservableCollection<UpgradeRow> Upgrades { get; } = [];
    private string _upgradeBasis = "";
    public string UpgradeBasis { get => _upgradeBasis; set => Set(ref _upgradeBasis, value); }

    public void RefreshUpgrades()
    {
        if (Hardware is null) return;
        Upgrades.Clear();
        foreach (var a in UpgradeAdvisor.Advise(Hardware, _lastSession, DiskHealth?.Invoke())) Upgrades.Add(new UpgradeRow(a));
        UpgradeBasis = _lastSession is null
            ? "Estimativa pelo hardware. Meça um jogo (Iniciar → jogar → Parar) para a análise usar o gargalo real."
            : "Com base no hardware e na última medição: o upgrade que tira o gargalo atual sem criar outro.";
    }

    public static string BenchDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SnowBench");

    public MonitorViewModel(ILog log)
    {
        _log = log;
        _timer.Tick += (_, _) => Tick();
        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        DownloadPresentMonCommand = new RelayCommand(() => Open("https://github.com/GameTechDev/PresentMon/releases"));
        PickBeforeCommand = new RelayCommand(() => { _compareBefore = Pick("ANTES"); RunCompare(); });
        PickAfterCommand = new RelayCommand(() => { _compareAfter = Pick("DEPOIS"); RunCompare(); });
        OpenBenchFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(BenchDir); Open(BenchDir); });
        SelectedGame = Games[0];
        PresentMonPath = PresentMonLive.Find();
    }

    public const string Auto = "*";

    public IReadOnlyList<MonitorGame> Games { get; } =
    [
        new("auto", "Automático", Auto),
        new("none", "Só hardware", null),
        new("fortnite", "Fortnite", "FortniteClient-Win64-Shipping.exe"),
        new("lol", "League of Legends", "League of Legends.exe"),
        new("valorant", "VALORANT", "VALORANT-Win64-Shipping.exe"),
    ];

    private MonitorGame? _selectedGame;
    public MonitorGame? SelectedGame { get => _selectedGame; set => Set(ref _selectedGame, value); }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DownloadPresentMonCommand { get; }
    public ICommand PickBeforeCommand { get; }
    public ICommand PickAfterCommand { get; }
    public ICommand OpenBenchFolderCommand { get; }

    private string? _presentMonPath;
    public string? PresentMonPath { get => _presentMonPath; set { Set(ref _presentMonPath, value); OnPropertyChanged(nameof(NeedsPresentMon)); } }
    public bool NeedsPresentMon => PresentMonPath is null;

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { Set(ref _isRunning, value); CommandManager.InvalidateRequerySuggested(); } }

    // ── valores ao vivo
    private string _fps = "—", _low1 = "—", _cpu = "—", _core = "—", _gpu = "—", _vram = "—", _ram = "—";
    public string Fps { get => _fps; set => Set(ref _fps, value); }
    public string Low1 { get => _low1; set => Set(ref _low1, value); }
    public string Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public string Core { get => _core; set => Set(ref _core, value); }
    public string Gpu { get => _gpu; set => Set(ref _gpu, value); }
    public string Vram { get => _vram; set => Set(ref _vram, value); }
    public string Ram { get => _ram; set => Set(ref _ram, value); }
    private string _verdict = "Pronto para medir";
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }
    private string _advice = "Escolha o jogo, clique em Iniciar e jogue normalmente. O Snow não interage com o jogo.";
    public string Advice { get => _advice; set => Set(ref _advice, value); }
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private PointCollection _chart = [];
    public PointCollection Chart { get => _chart; set => Set(ref _chart, value); }
    public string ChartLabel => _pm is not null ? "FPS · últimos 60 s" : "GPU % · últimos 60 s";

    public ObservableCollection<ShareRow> TimeShare { get; } = [];
    private string _sessionSummary = "";
    public string SessionSummary { get => _sessionSummary; set => Set(ref _sessionSummary, value); }
    public bool HasSession => TimeShare.Count > 0;

    public ObservableCollection<ComparisonRow> Comparison { get; } = [];
    private string _compareStatus = "Escolha dois arquivos .csv do Snow Bench ou de sessões do Monitor (pasta Documentos\\SnowBench).";
    public string CompareStatus { get => _compareStatus; set => Set(ref _compareStatus, value); }
    public bool HasComparison => Comparison.Count > 0;

    private void Start()
    {
        try
        {
            _pdh ??= new PdhSampler();
            _samples.Clear();
            TimeShare.Clear(); OnPropertyChanged(nameof(HasSession));
            PresentMonPath = PresentMonLive.Find();
            _fpsCap = FpsCapReader.ForGame(SelectedGame?.Key, RefreshHz);
            _activeApp = null;
            if (SelectedGame?.Process is { } proc && PresentMonPath is { } pmExe)
            {
                var auto = proc == Auto;
                _pm = new PresentMonLive(pmExe, auto ? null : proc);
                Status = auto
                    ? "Modo automático: abra qualquer jogo e o Snow detecta sozinho (PresentMon via ETW, sem injeção)."
                    : $"Medindo {SelectedGame.Name} com o PresentMon (ETW, sem injeção). Abra o jogo se ainda não abriu.";
            }
            else
                Status = SelectedGame?.Process is null ? "Medindo uso de hardware." : "PresentMon não encontrado: medindo só o hardware (sem FPS).";
            if (!_pdh.HasGpuCounters) Status += " Contadores de GPU indisponíveis neste Windows.";
            OnPropertyChanged(nameof(ChartLabel));
            IsRunning = true;
            _timer.Start();
            _log.Info($"Snow Monitor iniciado ({SelectedGame?.Name}).");
        }
        catch (Exception e)
        {
            _log.Error("Monitor: " + e);
            Status = "Não foi possível iniciar: " + e.Message;
        }
    }

    private void Tick()
    {
        if (_pdh is null) return;
        if (_pm is { IsAuto: true })
        {
            var app = _pm.UpdateActive();
            if (!string.Equals(app, _activeApp, StringComparison.OrdinalIgnoreCase))
            {
                _activeApp = app;
                _fpsCap = app is null ? null : FpsCapReader.ForGame(ActiveGameDetector.KeyFor(app), RefreshHz);
                Status = app is null
                    ? "Nenhum jogo na tela agora. Abra um jogo: o Snow detecta sozinho."
                    : $"Jogo detectado: {ActiveGameDetector.DisplayName(app)}" + (_fpsCap is { } c ? $" · limite de FPS {c:0}" : " · sem limite de FPS");
                _log.Info("Snow Monitor: " + Status);
            }
        }
        var (fps, low) = _pm?.Current() ?? (null, null);
        var s = _pdh.Sample(fps, low) with { FpsCap = _fpsCap };
        _samples.Add(s);
        Fps = fps?.ToString("0") ?? "—";
        Low1 = low?.ToString("0") ?? "—";
        Cpu = Pct(s.CpuPercent);
        Core = Pct(s.BusiestCorePercent);
        Gpu = Pct(s.GpuPercent);
        Vram = s.VramUsedGb is { } v ? $"{v:0.0} GB" : "—";
        Ram = $"{s.RamUsedPercent:0}%";
        var b = BottleneckClassifier.Classify(s);
        Verdict = b.Label();
        Advice = b.Advice();

        // gráfico: últimos 60 pontos em 600×90
        var series = _samples.TakeLast(60).Select(x => _pm is not null ? x.Fps ?? 0 : x.GpuPercent ?? 0).ToList();
        var max = Math.Max(1, series.DefaultIfEmpty(0).Max() * 1.15);
        var pts = new PointCollection(series.Select((y, i) => new Point(i * 10, 90 - y / max * 90)));
        pts.Freeze();
        Chart = pts;
        if (_pm is { IsRunning: false }) Status = "O PresentMon parou: " + (_pm.LastError ?? "verifique se o jogo está aberto.");
        else if (_pm is { IsAuto: false, NoFramesYet: true } && fps is null)
            Status = SelectedGame?.Key == "lol"
                ? "Sem quadros do League of Legends ainda: o jogo só existe durante a partida (o cliente/lobby é outro programa)."
                : $"Sem quadros de {SelectedGame?.Name} ainda. Se o jogo já está aberto, pare e inicie de novo a medição.";
    }

    private void Stop()
    {
        _timer.Stop();
        IsRunning = false;
        var frames = _pm?.AllFrameTimes ?? [];
        var mainApp = _pm?.MainApp;
        var auto = _pm?.IsAuto == true;
        var gameName = auto && mainApp is not null ? ActiveGameDetector.DisplayName(mainApp) : SelectedGame?.Name ?? "sessao";
        var others = auto ? _pm!.GamesMeasured().Where(g => !string.Equals(g.App, mainApp, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        _pm?.Dispose(); _pm = null;
        var summary = Snow.Core.Monitor.SessionSummary.From(_samples, frames.Count > 0 ? frames : null);
        TimeShare.Clear();
        foreach (var (k, pct) in summary.TimeShare.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
            TimeShare.Add(new ShareRow(k.Label(), pct));
        OnPropertyChanged(nameof(HasSession));
        var fr = summary.Frames is { } f ? $"{(auto ? gameName + ": " : "")}FPS médio {f.AvgFps} · 1% low {f.Low1} · 0,1% low {f.Low01} · {f.Stutters} engasgos · " : "";
        var extra = string.Concat(others.Select(o => FrameStats.From(o.Frames) is { } st ? $"\nTambém medido: {ActiveGameDetector.DisplayName(o.App)} · FPS médio {st.AvgFps} · 1% low {st.Low1}" : ""));
        SessionSummary = $"{fr}{_samples.Count} s medidos · principal: {summary.Main.Label()}. {summary.Main.Advice()}{extra}";
        if (summary.Main is not Bottleneck.Desconhecido and not Bottleneck.SemCarga) { _lastSession = summary; RefreshUpgrades(); }
        try
        {
            Directory.CreateDirectory(BenchDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var key = auto ? (mainApp is null ? "sessao" : ActiveGameDetector.KeyFor(mainApp)) : SelectedGame?.Key ?? "sessao";
            if (frames.Count > 0)
            {
                // Mesmo formato de coluna do PresentMon: dá para comparar com o Snow Bench
                var sb = new StringBuilder("Application,FrameTime\n");
                foreach (var ft in frames) sb.Append(gameName).Append(',').Append(ft.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(Path.Combine(BenchDir, $"{key}-monitor-{stamp}.csv"), sb.ToString());
            }
            var us = new StringBuilder("Hora,CPU,Nucleo,GPU,VRAM_GB,RAM,FPS,Low1,Gargalo\n");
            foreach (var x in _samples)
                us.AppendLine(string.Join(',', x.Time.ToString("HH:mm:ss"), N(x.CpuPercent), N(x.BusiestCorePercent), N(x.GpuPercent), N(x.VramUsedGb), N(x.RamUsedPercent), N(x.Fps), N(x.Low1), BottleneckClassifier.Classify(x)));
            File.WriteAllText(Path.Combine(BenchDir, $"{key}-uso-{stamp}.csv"), us.ToString());
            Status = $"Sessão salva em Documentos\\SnowBench ({key}-*-{stamp}.csv).";
        }
        catch (Exception e) { _log.Error("Monitor: salvar sessão: " + e.Message); }
        _log.Info("Snow Monitor: " + SessionSummary);
    }

    private static string N(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
    private static string Pct(double? v) => v is { } x ? $"{x:0}%" : "—";

    private string? Pick(string which)
    {
        Directory.CreateDirectory(BenchDir);
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Escolha a captura {which}", Filter = "Capturas (*.csv)|*.csv", InitialDirectory = BenchDir };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void RunCompare()
    {
        Comparison.Clear();
        if (_compareBefore is null || _compareAfter is null)
        {
            CompareStatus = $"ANTES: {Path.GetFileName(_compareBefore) ?? "—"} · DEPOIS: {Path.GetFileName(_compareAfter) ?? "—"}";
            OnPropertyChanged(nameof(HasComparison));
            return;
        }
        try
        {
            var a = PresentMonCapture.Parse(File.ReadAllText(_compareBefore));
            var b = PresentMonCapture.Parse(File.ReadAllText(_compareAfter));
            if (a.Stats is not { } sa || b.Stats is not { } sb) { CompareStatus = "Um dos arquivos tem poucos quadros."; return; }
            foreach (var r in SessionComparer.Compare(sa, sb)) Comparison.Add(r);
            var extra = string.Join(" · ", new[] { ("antes", a), ("depois", b) }
                .Where(x => x.Item2.BoundShare is not null)
                .Select(x => $"{x.Item1}: GPU-bound {x.Item2.BoundShare!.Value.gpuBound}% / CPU-bound {x.Item2.BoundShare!.Value.cpuBound}%"));
            CompareStatus = $"{Path.GetFileName(_compareBefore)} → {Path.GetFileName(_compareAfter)}" + (extra.Length > 0 ? $"\n{extra}" : "");
        }
        catch (Exception e) { CompareStatus = "Não foi possível ler: " + e.Message; }
        OnPropertyChanged(nameof(HasComparison));
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { _log.Error(e.Message); }
    }
}
