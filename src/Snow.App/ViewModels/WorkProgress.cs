using Snow.App.Infrastructure;

namespace Snow.App.ViewModels;

/// <summary>
/// Estado do painel de progresso grande (aplicar melhorias, restaurar, limpar). Mostra título, passo atual, "3 de 12",
/// porcentagem e, no fim, um "✔ Concluído" por alguns segundos para a pessoa ver que terminou.
/// </summary>
public sealed class WorkProgress : ObservableObject
{
    private bool _visible, _known, _done;
    private string _title = "", _step = "", _count = "";
    private double _percent;
    private int _generation;

    public bool Visible { get => _visible; private set => Set(ref _visible, value); }
    public bool Known { get => _known; private set { Set(ref _known, value); OnPropertyChanged(nameof(PercentText)); } }
    public bool Done { get => _done; private set => Set(ref _done, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Step { get => _step; set => Set(ref _step, value); }
    public string Count { get => _count; private set => Set(ref _count, value); }
    public double Percent { get => _percent; private set { Set(ref _percent, value); OnPropertyChanged(nameof(PercentText)); } }
    public string PercentText => Known ? $"{Percent:0}%" : "";

    /// <summary>known = sabemos o total de passos (barra com %); senão a barra fica em movimento.</summary>
    public void Start(string title, bool known, string step = "")
    {
        _generation++;
        Title = title; Step = step; Count = ""; Done = false; Known = known; Percent = 0; Visible = true;
    }

    public void Report(int done, int total, string step, string unit = "")
    {
        Known = total > 0;
        Percent = total <= 0 ? 0 : Math.Clamp(100.0 * done / total, 0, 100);
        Step = step;
        Count = total > 0 ? $"{Math.Min(done + 1, total)} de {total}{(unit.Length > 0 ? " " + unit : "")}" : "";
    }

    /// <summary>Mostra "Concluído" com o resumo por alguns segundos e some (a menos que outra tarefa comece).</summary>
    public async Task FinishAsync(string summary, int ms = 2500)
    {
        var g = _generation;
        Known = true; Percent = 100; Done = true; Count = ""; Step = summary; Title = "Concluído";
        await Task.Delay(ms);
        if (g == _generation) Visible = false;
    }

    public void Hide() { _generation++; Visible = false; }
}
