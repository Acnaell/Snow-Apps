using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Snow.App.Infrastructure;
using Snow.Core.Compat;
using Snow.Core.Games;
using Snow.Core.Hardware;
using Snow.Platform.Compat;

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

public sealed record SteamHitRow(long AppId, string Name, ICommand Command);

public sealed class CanRunViewModel : ObservableObject
{
    private readonly SteamStore _steam = new();
    private HardwareProfile? _hw;
    private List<CanRunRow> _all = [];

    public CanRunViewModel()
    {
        SearchCommand = new RelayCommand(async () => await SearchSteamAsync(), () => !IsSearching && Query.Trim().Length >= 2);
    }

    public ICommand SearchCommand { get; }
    public ObservableCollection<SteamHitRow> Hits { get; } = [];
    public ObservableCollection<CanRunRow> SearchResult { get; } = [];
    public bool HasHits => Hits.Count > 0;
    public bool HasSearchResult => SearchResult.Count > 0;

    private string _query = "";
    /// <summary>Filtra a lista na hora; Enter/botão busca qualquer jogo na Steam.</summary>
    public string Query
    {
        get => _query;
        set { if (Set(ref _query, value)) { Filter(); CommandManager.InvalidateRequerySuggested(); } }
    }
    private bool _isSearching;
    public bool IsSearching { get => _isSearching; set { Set(ref _isSearching, value); CommandManager.InvalidateRequerySuggested(); } }
    private string _searchStatus = "Não achou na lista? Digite o nome e busque na Steam (requisitos da página do jogo).";
    public string SearchStatus { get => _searchStatus; set => Set(ref _searchStatus, value); }

    private void Filter()
    {
        var q = Query.Trim();
        Rows.Clear();
        foreach (var r in _all.Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))) Rows.Add(r);
    }

    private async Task SearchSteamAsync()
    {
        IsSearching = true; Hits.Clear(); SearchResult.Clear();
        SearchStatus = "Buscando na Steam…";
        try
        {
            foreach (var h in await _steam.SearchAsync(Query.Trim()))
                Hits.Add(new SteamHitRow(h.AppId, h.Name, new RelayCommand(async () => await PickAsync(h))));
            SearchStatus = Hits.Count == 0 ? "Nada encontrado na Steam com esse nome." : "Escolha o jogo:";
        }
        catch (Exception e) { SearchStatus = "Não consegui falar com a Steam: " + e.Message; }
        finally { IsSearching = false; OnPropertyChanged(nameof(HasHits)); OnPropertyChanged(nameof(HasSearchResult)); }
    }

    private async Task PickAsync(SteamStore.Hit h)
    {
        if (_hw is null) { SearchStatus = "Faça o diagnóstico primeiro."; return; }
        IsSearching = true; SearchResult.Clear();
        SearchStatus = $"Lendo os requisitos de {h.Name}…";
        try
        {
            var req = await _steam.RequirementsAsync(h.AppId);
            if (req is null) SearchStatus = $"{h.Name} não informa requisitos de PC na Steam.";
            else
            {
                SearchResult.Add(new CanRunRow(CanRun.Evaluate(req, _hw), false, null));
                Hits.Clear();
                SearchStatus = $"Resultado para {req.Name}:";
            }
        }
        catch (Exception e) { SearchStatus = "Não consegui ler os requisitos: " + e.Message; }
        finally { IsSearching = false; OnPropertyChanged(nameof(HasHits)); OnPropertyChanged(nameof(HasSearchResult)); }
    }
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
        _hw = hw;
        var installed = installedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bench = MonitorViewModel.BenchDir;
        Rows.Clear();
        foreach (var r in CanRun.EvaluateAll(hw).OrderByDescending(r => StoreIds.TryGetValue(r.Game.Id, out var ids) && ids.Any(installed.Contains)).ThenByDescending(r => r.Level).ThenBy(r => r.Game.Name))
        {
            var isInstalled = StoreIds.TryGetValue(r.Game.Id, out var ids) && ids.Any(installed.Contains);
            Rows.Add(new CanRunRow(r, isInstalled, Measured(bench, r.Game.MonitorKey)));
        }
        _all = Rows.ToList();
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
