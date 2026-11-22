using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snow.App.Infrastructure;
using Snow.Core.Health;
using Snow.Platform.Health;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

public sealed record HealthItemViewModel(HealthItem Item)
{
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string? Action => Item.Action;
    public bool HasAction => !string.IsNullOrEmpty(Item.Action);
    public string Level => Item.Level.ToString();
    public string Category => Item.Category switch
    {
        HealthCategory.Discos => "Discos",
        HealthCategory.Estabilidade => "Estabilidade do Windows",
        HealthCategory.Hardware => "Hardware",
        HealthCategory.Dispositivos => "Drivers e dispositivos",
        HealthCategory.Espaco => "Espaço em disco",
        HealthCategory.Bateria => "Bateria",
        HealthCategory.PlacaMae => "Placa-mãe, TPM e Secure Boot",
        _ => "Sistema",
    };
    public string Icon => Item.Level switch
    {
        HealthLevel.Ok => "",
        HealthLevel.Critico => "",
        HealthLevel.Atencao => "",
        _ => "",
    };
}

public sealed class HealthViewModel : ObservableObject
{
    private readonly ILog _log;

    public HealthViewModel(ILog log)
    {
        _log = log;
        AnalyzeCommand = new RelayCommand(async () => await LoadAsync(), () => !IsBusy);
        OpenReliabilityCommand = new RelayCommand(() => Start("perfmon.exe", "/rel"));
        OpenDeviceManagerCommand = new RelayCommand(() => Start("devmgmt.msc", null));
        OpenStorageCommand = new RelayCommand(() => Start("ms-settings:storagesense", null));
        OpenTpmCommand = new RelayCommand(() => Start("tpm.msc", null));
        OpenSysInfoCommand = new RelayCommand(() => Start("msinfo32.exe", null));
    }

    public ObservableCollection<HealthItemViewModel> Items { get; } = [];
    public ICommand AnalyzeCommand { get; }
    public ICommand OpenReliabilityCommand { get; }
    public ICommand OpenDeviceManagerCommand { get; }
    public ICommand OpenStorageCommand { get; }
    public ICommand OpenTpmCommand { get; }
    public ICommand OpenSysInfoCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }

    private int _score;
    public int Score { get => _score; set => Set(ref _score, value); }
    private string _verdict = "Analisando…";
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }
    private string _summary = "Lendo discos, registros de eventos e dispositivos (somente leitura)…";
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    private Brush _scoreBrush = Brushes.Gray;
    public Brush ScoreBrush { get => _scoreBrush; set => Set(ref _scoreBrush, value); }
    public bool HasReport => Items.Count > 0;
    public HealthReport? LastReport { get; private set; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Verdict = "Analisando…";
        try
        {
            var report = await Task.Run(HealthCollector.Run);
            LastReport = report;
            Items.Clear();
            foreach (var i in report.Items) Items.Add(new HealthItemViewModel(i));
            Score = report.Score;
            Verdict = report.Verdict;
            var problems = report.Problems.Count();
            Summary = problems == 0
                ? "Nenhum problema que exija ação. Itens informativos abaixo."
                : $"{problems} ponto(s) que merecem atenção, do mais importante para o menos importante.";
            ScoreBrush = (Brush)Application.Current.FindResource(report.Score >= 75 ? "GoodBrush" : report.Score >= 55 ? "WarnBrush" : "BadBrush");
            foreach (var w in report.Data.Warnings) _log.Info("Health: " + w);
            _log.Info($"Snow Health: nota {report.Score} ({report.Verdict}), {problems} problema(s).");
        }
        catch (Exception e)
        {
            _log.Error("Snow Health falhou: " + e);
            Verdict = "Não foi possível analisar";
            Summary = e.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasReport));
        }
    }

    private void Start(string file, string? args)
    {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
        catch (Exception e) { _log.Error(e.Message); }
    }
}
