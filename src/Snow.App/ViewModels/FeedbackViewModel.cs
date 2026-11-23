using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Snow.App.Infrastructure;
using Snow.Core.Feedback;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

public sealed class ChoiceItem(string text, bool selected = false) : ObservableObject
{
    public string Text { get; } = text;
    private bool _selected = selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class ScoreItem(int value, FeedbackViewModel owner) : ObservableObject
{
    public int Value { get; } = value;
    public string Band => Value >= 9 ? "Fa" : Value >= 7 ? "Neutro" : "Critico";
    public bool Selected { get => owner.Score == Value; set { if (value) owner.Score = Value; } }
    public void Refresh() => OnPropertyChanged(nameof(Selected));
}

public sealed class FeedbackViewModel : ObservableObject
{
    private readonly ILog _log;
    private readonly Func<string> _systemInfo;

    public FeedbackViewModel(ILog log, Func<string> systemInfo)
    {
        _log = log; _systemInfo = systemInfo;
        Scores = Enumerable.Range(0, 11).Select(i => new ScoreItem(i, this)).ToList();
        Differences = FeedbackForm.DifferenceOptions.Select(t => new ChoiceItem(t)).ToList();
        Parts = new ObservableCollection<ChoiceItem>(FeedbackForm.PartOptions.Select(t => new ChoiceItem(t)));
        Improve = FeedbackForm.ImproveOptions.Select(t => new ChoiceItem(t)).ToList();
        SendCommand = new RelayCommand(Send, () => Score is not null);
        PickDifferenceCommand = new RelayCommand(p => { foreach (var d in Differences) d.Selected = ReferenceEquals(d, p); });
    }

    public IReadOnlyList<ScoreItem> Scores { get; }
    public IReadOnlyList<ChoiceItem> Differences { get; }
    public ObservableCollection<ChoiceItem> Parts { get; }
    public IReadOnlyList<ChoiceItem> Improve { get; }
    public ICommand SendCommand { get; }
    public ICommand PickDifferenceCommand { get; }

    private int? _score;
    public int? Score
    {
        get => _score;
        set
        {
            Set(ref _score, value);
            foreach (var s in Scores) s.Refresh();
            OnPropertyChanged(nameof(BandText)); OnPropertyChanged(nameof(ScoreText));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string ScoreText => _score is { } s ? s.ToString() : "–";
    public string BandText => _score is { } s ? FeedbackForm.Band(s) : "Escolha uma nota de 0 a 10 para continuar.";

    private string _comment = "";
    public string Comment { get => _comment; set => Set(ref _comment, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Marca as partes que a pessoa claramente usou (ex.: já aplicou melhorias).</summary>
    public void MarkUsed(string part)
    {
        var p = Parts.FirstOrDefault(x => x.Text == part);
        if (p is not null) p.Selected = true;
    }

    private void Send()
    {
        var url = FeedbackForm.BuildUrl(Score, Differences.FirstOrDefault(d => d.Selected)?.Text,
            Parts.Where(p => p.Selected).Select(p => p.Text), Improve.Where(i => i.Selected).Select(i => i.Text), Comment, _systemInfo());
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Status = "O formulário abriu no navegador já preenchido. Confira e clique em Enviar lá. Obrigado!";
            _log.Info($"Avaliação: formulário aberto (nota {Score}).");
        }
        catch (Exception e) { Status = "Não foi possível abrir o navegador: " + e.Message; }
    }
}
