using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Snow.App.Infrastructure;
using Snow.Core.Compat;
using Snow.Core.Games;
using Snow.Core.Hardware;

namespace Snow.App.ViewModels;

public sealed class CanRunRow(CanRunResult r, bool installed, string? measured)
{
    public string Name => r.Game.Name;
    public string Level => r.Level.Label();
    public CanRunLevel LevelValue => r.Level;
    public bool IsBelow => r.Level == CanRunLevel.Abaixo;
    public bool IsMin => r.Level == CanRunLevel.Minimo;
    public bool IsGood => r.Level >= CanRunLevel.Recomendado;
    public string Verdict => r.Verdict;
    public string Detail => r.Detail;
    public string? Note => r.Game.Note;
    public bool HasNote => r.Game.Note is not null;
    public bool Installed => installed;
    public string? Measured => measured;
    public bool HasMeasured => measured is not null;
    public ICommand OpenSourceCommand { get; } = new RelayCommand(() => Process.Start(new ProcessStartInfo(r.Game.Source) { UseShellExecute = true }));
}

public sealed class CanRunViewModel : ObservableObject
{
    /// <summary>Ids das lojas para marcar "instalado" (Steam app id, Epic, Riot).</summary>
    private static readonly Dictionary<string, string[]> StoreIds = new()
    {
        ["fortnite"] = ["epic:fortnite"], ["lol"] = ["riot:league_of_legends"], ["valorant"] = ["riot:valorant"],
        ["rocketleague"] = ["epic:sugar", "steam:252950"], ["cs2"] = ["steam:730"], ["apex"] = ["steam:1172470"],
        ["marvelrivals"] = ["steam:2767030"], ["pubg"] = ["steam:578080"], ["eldenring"] = ["steam:1245620"],
        ["cyberpunk"] = ["steam:1091500"], ["rdr2"] = ["steam:1174180"], ["gtav"] = ["steam:3240220", "steam:271590"],
        ["overwatch2"] = ["steam:2357570"],
    };

    public ObservableCollection<CanRunRow> Rows { get; } = [];
    private string _summary = "Faça o diagnóstico para avaliar.";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    public void Load(HardwareProfile hw, IEnumerable<string> installedIds)
    {
        var installed = installedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bench = MonitorViewModel.BenchDir;
        Rows.Clear();
        foreach (var r in CanRun.EvaluateAll(hw).OrderByDescending(r => StoreIds.TryGetValue(r.Game.Id, out var ids) && ids.Any(installed.Contains)).ThenByDescending(r => r.Level).ThenBy(r => r.Game.Name))
        {
            var isInstalled = StoreIds.TryGetValue(r.Game.Id, out var ids) && ids.Any(installed.Contains);
            Rows.Add(new CanRunRow(r, isInstalled, Measured(bench, r.Game.MonitorKey)));
        }
        var cpu = HardwareIndex.Cpu(hw.Cpu);
        var gpu = HardwareIndex.Gpu(hw.PrimaryGpu?.Name);
        Summary = $"Processador: índice {(cpu?.ToString() ?? "?")} (Core i7-4770 = 100) · placa de vídeo: índice {(gpu?.ToString() ?? "?")} (GTX 1060 = 100) · {hw.Memory.TotalGb:0} GB de RAM. " +
                  $"{Rows.Count(r => r.IsGood)} de {Rows.Count} jogos no recomendado ou acima.";
    }

    private static string? Measured(string dir, string key)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var f = new DirectoryInfo(dir).EnumerateFiles($"{key}-uso-*.csv").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            if (f is null) return null;
            var fps = MeasuredInsight.FpsColumn(File.ReadAllText(f.FullName));
            if (fps.Count < 30) return null;
            var sorted = fps.OrderBy(x => x).ToList();
            return $"Medido no seu PC em {f.LastWriteTime:dd/MM}: {fps.Average():0} FPS em média, piores momentos ~{sorted[sorted.Count / 20]:0}.";
        }
        catch (IOException) { return null; }
    }
}
