using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Snow.App.Infrastructure;
using Snow.Core.Bench;
using Snow.Core.Hardware;
using Snow.Core.Health;
using Snow.Platform.Bench;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

public sealed class DriveOption(string letter, string label, bool selected, string? warning) : ObservableObject
{
    public string Letter { get; } = letter;
    public string Label { get; } = label;
    public string? Warning { get; } = warning;
    public bool HasWarning => Warning is not null;
    private bool _selected = selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed record ResultRow(string Metric, string Value, string Verdict, string Delta, bool Better);
public sealed record VerdictRow(bool Ok, string Title, string Detail);
public sealed record DurationOption(string Label, int Minutes);
public sealed record AiRow(string Name, string Use, string Verdict, string Where);

public sealed class BenchViewModel : ObservableObject
{
    private readonly ILog _log;
    private CancellationTokenSource? _cts;

    public BenchViewModel(ILog log)
    {
        _log = log;
        RunBenchCommand = new RelayCommand(async () => await RunBenchAsync(), () => !IsBusy);
        RunStressCommand = new RelayCommand(async () => await RunStressAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RunAiCommand = new RelayCommand(async () => await RunAiAsync(), () => !IsBusy);
        SelectedDuration = Durations[1];
        LoadHistory();
    }

    public HardwareProfile? Hardware { get; private set; }

    /// <summary>Discos para o teste: SSDs saudáveis marcados; disco com erro SMART fica desmarcado com aviso.</summary>
    public void Init(HardwareProfile hw, IReadOnlyList<DiskHealthData>? health)
    {
        Hardware = hw;
        Drives.Clear();
        foreach (var d in hw.Storage.Disks.Where(d => d.Kind != DiskKind.Virtual))
            foreach (var letter in d.DriveLetters)
            {
                var h = health?.FirstOrDefault(x => x.Name.Equals(d.Model, StringComparison.OrdinalIgnoreCase));
                var errors = (h?.ReadErrorsUncorrected ?? 0) + (h?.WriteErrorsUncorrected ?? 0);
                var warn = errors > 0 ? $"{errors} erro(s) no SMART: o teste grava 512 MB neste disco, só marque depois do backup." : null;
                Drives.Add(new DriveOption(letter, $"{letter}: {d.Model}", warn is null, warn));
            }
    }

    public ICommand RunBenchCommand { get; }
    public ICommand RunStressCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RunAiCommand { get; }
    public ObservableCollection<AiRow> AiRows { get; } = [];
    public ObservableCollection<string> AiNotes { get; } = [];
    private string _aiSummary = "Mede a memória e o processador (~10 s) e mostra quais modelos de IA rodam no seu PC (LM Studio, Ollama) e a que velocidade.";
    public string AiSummary { get => _aiSummary; set => Set(ref _aiSummary, value); }
    private string _aiHardware = "";
    public string AiHardware { get => _aiHardware; set => Set(ref _aiHardware, value); }

    private async Task RunAiAsync()
    {
        if (Hardware is null) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var g = Hardware.PrimaryGpu;
            var gpuName = g?.IsDiscrete == true ? g.Name : null;
            var vram = g?.IsDiscrete == true ? g.VramBytes : 0;
            var progress = new Progress<string>(m => AiSummary = m);
            var r = await Task.Run(() => BenchRunner.Ai(gpuName, vram, Hardware.Memory.TotalBytes, progress, _cts.Token));
            AiRows.Clear(); AiNotes.Clear();
            foreach (var m in r.Models) AiRows.Add(new AiRow(m.Model.Name, m.Model.Use, m.Verdict, m.Where.ToString()));
            foreach (var n in r.Notes) AiNotes.Add(n);
            AiHardware = $"{(gpuName ?? "Sem placa de vídeo dedicada")}{(vram > 0 ? $" · {vram / (double)(1L << 30):0} GB de VRAM" : "")}{(r.GpuSpec is { } s ? $" ({s.BandwidthGBs:0} GB/s)" : "")} · RAM {r.RamGb:0} GB a {r.RamBandwidthGBs:0} GB/s medidos · processador {r.CpuGflops:0} GFLOPS medidos";
            AiSummary = r.Summary;
            _log.Info($"IA local: {r.Summary}");
        }
        catch (OperationCanceledException) { AiSummary = "Cancelado."; }
        catch (Exception e) { AiSummary = "Falhou: " + e.Message; _log.Error("IA local: " + e); }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasAi)); }
    }

    public bool HasAi => AiRows.Count > 0;

    public ObservableCollection<DriveOption> Drives { get; } = [];
    public ObservableCollection<ResultRow> Results { get; } = [];
    public ObservableCollection<VerdictRow> StressVerdicts { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    public bool HasResults => Results.Count > 0;
    public bool HasVerdicts => StressVerdicts.Count > 0;

    public IReadOnlyList<DurationOption> Durations { get; } = [new("1 min", 1), new("10 min", 10), new("30 min", 30), new("60 min", 60)];
    private DurationOption _selectedDuration = null!;
    public DurationOption SelectedDuration { get => _selectedDuration; set => Set(ref _selectedDuration, value); }
    private bool _stressCpu = true, _stressRam = true;
    public bool StressCpu { get => _stressCpu; set => Set(ref _stressCpu, value); }
    public bool StressRam { get => _stressRam; set => Set(ref _stressRam, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }
    private string _status = "Fecha jogos e programas pesados antes: o teste mede o PC inteiro.";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _live = "";
    public string Live { get => _live; set => Set(ref _live, value); }

    private void LoadHistory()
    {
        History.Clear();
        try
        {
            foreach (var r in BenchRunner.History().Take(6))
                History.Add($"{r.When:dd/MM HH:mm} · CPU {r.CpuSingle} / {r.CpuMulti} · memória {r.MemoryGBs:0.0} GB/s · {string.Join(" · ", r.Disks.Select(d => $"{d.Drive} {d.SeqReadMBs:0} MB/s"))}");
        }
        catch (Exception e) { _log.Error("Benchmark: histórico: " + e.Message); }
    }

    private async Task RunBenchAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var previous = BenchRunner.History().FirstOrDefault();
        var drives = Drives.Where(d => d.Selected).Select(d => d.Letter).ToList();
        var progress = new Progress<string>(s => Status = s);
        try
        {
            var r = await Task.Run(() => BenchRunner.Run(Hardware?.Cpu.Name ?? "", drives, progress, _cts.Token));
            Results.Clear();
            string D(double b, double a, bool lower = false) { if (previous is null) return ""; var d = BenchVerdicts.Delta(b, a, lower); return $"{(d >= 0 ? "+" : "")}{d:0.#}% vs {previous.When:dd/MM}"; }
            bool B(double b, double a, bool lower = false) => BenchVerdicts.Delta(b, a, lower) >= 0;
            Results.Add(new("Processador · 1 núcleo", $"{r.CpuSingle} pts", "o que mais pesa no FPS", D(previous?.CpuSingle ?? 0, r.CpuSingle), B(previous?.CpuSingle ?? 0, r.CpuSingle)));
            Results.Add(new($"Processador · {r.Threads} threads", $"{r.CpuMulti} pts", "renderização, compressão, live", D(previous?.CpuMulti ?? 0, r.CpuMulti), B(previous?.CpuMulti ?? 0, r.CpuMulti)));
            Results.Add(new("Memória · banda", $"{r.MemoryGBs:0.0} GB/s", BenchVerdicts.Memory(r.MemoryGBs), D(previous?.MemoryGBs ?? 0, r.MemoryGBs), B(previous?.MemoryGBs ?? 0, r.MemoryGBs)));
            Results.Add(new("Memória · latência", $"{r.MemoryLatencyNs:0} ns", BenchVerdicts.Latency(r.MemoryLatencyNs), D(previous?.MemoryLatencyNs ?? 0, r.MemoryLatencyNs, true), B(previous?.MemoryLatencyNs ?? 0, r.MemoryLatencyNs, true)));
            foreach (var d in r.Disks)
            {
                var old = previous?.Disks.FirstOrDefault(x => x.Drive == d.Drive);
                Results.Add(new($"Disco {d.Drive}", $"leitura {d.SeqReadMBs:0} · escrita {d.SeqWriteMBs:0} MB/s · {d.Random4kReadIops:0} IOPS 4K",
                    BenchVerdicts.Disk(d), old is null ? "" : D(old.SeqReadMBs, d.SeqReadMBs), old is null || B(old.SeqReadMBs, d.SeqReadMBs)));
            }
            Status = $"Pronto. Resultado salvo em Documentos\\SnowBench (bench-{r.When:yyyyMMdd-HHmmss}.json). Rode de novo depois de um upgrade para comparar.";
            _log.Info($"Benchmark: CPU {r.CpuSingle}/{r.CpuMulti} · memória {r.MemoryGBs} GB/s {r.MemoryLatencyNs} ns · " + string.Join(" · ", r.Disks.Select(d => $"{d.Drive} {d.SeqReadMBs}/{d.SeqWriteMBs} MB/s")));
            LoadHistory();
        }
        catch (OperationCanceledException) { Status = "Teste cancelado."; }
        catch (Exception e) { Status = "Falhou: " + e.Message; _log.Error("Benchmark: " + e); }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasResults)); }
    }

    private async Task RunStressAsync()
    {
        if (!StressCpu && !StressRam) { Status = "Escolha CPU, memória ou os dois."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StressVerdicts.Clear(); OnPropertyChanged(nameof(HasVerdicts));
        var minutes = SelectedDuration.Minutes;
        Status = $"Estresse por {minutes} min: o PC vai ficar lento e o cooler vai acelerar. Pode cancelar quando quiser.";
        _log.Info($"Estresse iniciado: {minutes} min · CPU {StressCpu} · RAM {StressRam}");
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            var report = await Task.Run(() => BenchRunner.Stress(TimeSpan.FromMinutes(minutes), StressCpu, StressRam,
                p => dispatcher.BeginInvoke(() => Live =
                    $"{p.At:mm\\:ss} de {minutes:00}:00 · CPU {p.CpuPercent:0}% · clock {(p.PerformancePercent is { } c ? $"{c:0}% do base" : "—")} · erros CPU {p.CpuErrors} · erros RAM {p.RamErrors}"),
                _cts.Token));
            foreach (var (ok, title, detail) in report.Verdicts()) StressVerdicts.Add(new VerdictRow(ok, title, detail));
            var cancelled = report.Duration < TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(3);
            Status = cancelled ? $"Cancelado aos {report.Duration:mm\\:ss}: resultado parcial abaixo." : "Estresse concluído.";
            _log.Info("Estresse: " + string.Join(" | ", report.Verdicts().Select(v => v.title)));
        }
        catch (Exception e) { Status = "Falhou: " + e.Message; _log.Error("Estresse: " + e); }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasVerdicts)); }
    }
}
