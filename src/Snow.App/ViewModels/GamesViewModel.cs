using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Snow.App.Infrastructure;
using Snow.Core.Games;
using Snow.Core.Hardware;
using Snow.Platform.Games;
using Snow.Platform.Services;

namespace Snow.App.ViewModels;

public sealed class GameItemViewModel(GameReport report) : ObservableObject
{
    public GameReport Report { get; private set; } = report;
    public string Name => Report.Game.Name;
    public string Store => Report.Game.Store switch { GameStore.Steam => "Steam", GameStore.Epic => "Epic Games", GameStore.Riot => "Riot", _ => Report.Game.Id.StartsWith("app:") ? "App de live" : "Outro" };
    public string InstallDir => Report.Game.InstallDir;
    public string? AntiCheat => Report.Game.AntiCheat;
    public bool HasAntiCheat => AntiCheat is not null;
    public string SupportLabel => Report.Support switch
    {
        GameSupport.Editavel => "Otimização com backup",
        GameSupport.Guiado => "Diagnóstico + passo a passo",
        _ => "Diagnóstico básico",
    };
    public int Improvements => Report.Findings.Count(f => f.Level is FindingLevel.Melhoria or FindingLevel.Importante);
    public bool HasImprovements => Improvements > 0;
    public string ImprovementsText => Improvements == 1 ? "1 ponto de melhora" : $"{Improvements} pontos de melhora";

    public void Update(GameReport r)
    {
        Report = r;
        OnPropertyChanged(string.Empty);
    }
}

public sealed record FindingViewModel(GameFinding Finding)
{
    public string Title => Finding.Title;
    public string Detail => Finding.Detail + (Finding.FixedByPreset ? "  ✓ corrigido pela predefinição" : "");
    public string Level => Finding.Level.ToString();
    public string Icon => Finding.Level switch
    {
        FindingLevel.Ok => "",
        FindingLevel.Importante => "",
        FindingLevel.Melhoria => "",
        _ => "",
    };
}

public sealed record ChangeViewModel(SettingChange Change)
{
    public string Label => Change.Label;
    public string Current => Change.Current ?? "(padrão)";
    public string Target => Change.Target;
    public string Why => Change.Why;
    public bool Changes => !string.Equals(Change.Current, Change.Target, StringComparison.OrdinalIgnoreCase);
}

public sealed class GamesViewModel : ObservableObject
{
    private readonly GameService _service;
    private readonly ILog _log;
    private readonly Action _afterApply;
    private HardwareProfile? _hw;

    public GamesViewModel(OptimizationService optimizer, ILog log, Action afterApply)
    {
        _service = GameService.CreateDefault(optimizer);
        _log = log;
        _afterApply = afterApply;
        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsBusy && _hw is not null);
        ApplyCommand = new RelayCommand(async () => await ApplyAsync(), () => !IsBusy && CanApply);
        OpenConfigCommand = new RelayCommand(OpenConfig, () => Selected is not null);
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = [];
    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<ChangeViewModel> Changes { get; } = [];
    public ObservableCollection<GuidedStep> Steps { get; } = [];

    public ICommand ScanCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand OpenConfigCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }

    private string _status = "Procurando jogos instalados…";
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool HasGames => Games.Count > 0;
    public bool NoGames => !IsBusy && Games.Count == 0;
    public string TierText => _hw is null ? "" : $"Seu hardware: nível {HardwareTiers.Classify(_hw).Label()}" + (_hw.MonitorRefreshHz > 0 ? $" · monitor {_hw.MonitorRefreshHz} Hz" : "");

    private GameItemViewModel? _selected;
    public GameItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            Refresh();
        }
    }
    public bool HasSelection => Selected is not null;

    private GamePreset _preset = GamePreset.FpsAlto;
    public GamePreset SelectedPreset
    {
        get => _preset;
        set
        {
            if (!Set(ref _preset, value)) return;
            OnPropertyChanged(nameof(IsAlto));
            OnPropertyChanged(nameof(IsMedio));
            BuildPreset();
        }
    }
    public bool IsAlto { get => SelectedPreset == GamePreset.FpsAlto; set { if (value) SelectedPreset = GamePreset.FpsAlto; } }
    public bool IsMedio { get => SelectedPreset == GamePreset.FpsMedio; set { if (value) SelectedPreset = GamePreset.FpsMedio; } }

    private string _presetSummary = "";
    public string PresetSummary { get => _presetSummary; set => Set(ref _presetSummary, value); }
    public bool CanApply => Selected?.Report.Support == GameSupport.Editavel && Changes.Any(c => c.Changes);
    public bool IsGuided => Selected?.Report.Support == GameSupport.Guiado;
    public bool HasChanges => Changes.Count > 0;
    public bool HasSteps => Steps.Count > 0;
    public bool HasPreset => Selected?.Report.Profile is not null;
    private bool IsObs => Selected?.Report.Game.Id == ObsProfile.Id;
    public string MedioTitle => IsObs ? "Live mais nítida" : "FPS Médio";
    public string MedioCaption => IsObs ? "936p60 · codificador da placa" : "Qualidade e estabilidade";
    public string AltoTitle => IsObs ? "Jogo em primeiro lugar" : "FPS Alto";
    public string AltoCaption => IsObs ? "720p60 · mínimo de FPS perdido" : "Competitivo · menor latência";
    public int ChangeCount => Changes.Count(c => c.Changes);

    public async Task LoadAsync(HardwareProfile hw)
    {
        _hw = hw;
        OnPropertyChanged(nameof(TierText));
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_hw is null) return;
        IsBusy = true; Status = "Procurando jogos instalados (Steam, Epic Games, Riot)…";
        var keep = Selected?.Report.Game.Id;
        try
        {
            var hw = _hw;
            var reports = await Task.Run(() => _service.Analyze(hw));
            Games.Clear();
            foreach (var r in reports.OrderByDescending(r => r.Profile is not null).ThenBy(r => r.Game.Name))
                Games.Add(new GameItemViewModel(r));
            Status = Games.Count == 0 ? "" : $"{Games.Count} jogo(s) encontrado(s) · {Games.Count(g => g.Report.Profile is not null)} com perfil do Snow";
            Selected = Games.FirstOrDefault(g => g.Report.Game.Id == keep) ?? Games.FirstOrDefault();
            _log.Info($"Jogos: {Status}");
        }
        catch (Exception e) { _log.Error("Busca de jogos falhou: " + e); Status = "Não foi possível listar os jogos. Veja o Log."; }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasGames));
            OnPropertyChanged(nameof(NoGames));
        }
    }

    private void Refresh()
    {
        Findings.Clear();
        if (Selected is not null)
            foreach (var f in Selected.Report.Findings.OrderByDescending(f => f.Level)) Findings.Add(new FindingViewModel(f));
        OnPropertyChanged(nameof(IsGuided));
        OnPropertyChanged(nameof(HasPreset));
        OnPropertyChanged(nameof(MedioTitle)); OnPropertyChanged(nameof(MedioCaption));
        OnPropertyChanged(nameof(AltoTitle)); OnPropertyChanged(nameof(AltoCaption));
        BuildPreset();
    }

    private void BuildPreset()
    {
        Changes.Clear(); Steps.Clear(); PresetSummary = "";
        if (Selected?.Report is { Profile: not null } r)
        {
            try
            {
                var plan = _service.BuildPreset(r, SelectedPreset);
                foreach (var c in plan.Changes) Changes.Add(new ChangeViewModel(c));
                foreach (var s in plan.ManualSteps) Steps.Add(s);
                PresetSummary = plan.Summary;
            }
            catch (Exception e) { PresetSummary = "Não foi possível montar a predefinição: " + e.Message; }
        }
        foreach (var n in new[] { nameof(CanApply), nameof(HasChanges), nameof(HasSteps), nameof(ChangeCount) }) OnPropertyChanged(n);
    }

    private async Task ApplyAsync()
    {
        if (Selected is null || _hw is null) return;
        var r = Selected.Report;
        var plan = _service.BuildPreset(r, SelectedPreset);
        var msg = $"Aplicar {SelectedPreset.Label()} no {r.Game.Name}?\n\n" +
                  string.Join("\n", Changes.Where(c => c.Changes).Select(c => $"• {c.Label}: {c.Current} → {c.Target}")) +
                  "\n\nO valor original de cada item fica no backup (aba Restaurar). Feche o jogo e o launcher antes.";
        if (MessageBox.Show(msg, "Snow Gaming", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var session = await Task.Run(() => _service.Apply(r, plan));
            var t = session.Tweaks.FirstOrDefault();
            var text = t?.Status switch
            {
                TweakStatus.Applied => $"{r.Game.Name}: {SelectedPreset.Label()} aplicada. Abra o jogo para conferir.",
                TweakStatus.PartiallyApplied => $"{r.Game.Name}: aplicada em parte.\n\n{string.Join("\n", t.Messages.Where(m => m.StartsWith('↷')))}",
                TweakStatus.Skipped => $"Nada foi alterado.\n\n{t.Messages.LastOrDefault()?.Split('—').Last().Trim()}",
                _ => $"Falhou: {t?.Messages.LastOrDefault()}",
            };
            MessageBox.Show(text, "Snow Gaming", MessageBoxButton.OK, t?.Status is TweakStatus.Applied or TweakStatus.PartiallyApplied ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Selected.Update(_service.Report(r.Game, _hw));
            Refresh();
            _afterApply();
        }
        catch (Exception e)
        {
            _log.Error(e.ToString());
            MessageBox.Show(e.Message, "Snow Gaming", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }

    private void OpenConfig()
    {
        var file = Selected?.Report.Context.Files.Keys.FirstOrDefault();
        var target = file is not null ? Path.GetDirectoryName(file) : Selected?.Report.Game.InstallDir;
        if (target is null || !Directory.Exists(target)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }); }
        catch (Exception e) { _log.Error(e.Message); }
    }
}
