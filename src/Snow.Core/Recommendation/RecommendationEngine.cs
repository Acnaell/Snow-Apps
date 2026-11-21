using Snow.Core.Hardware;
using Snow.Core.Tweaks;

namespace Snow.Core.Recommendation;

public sealed record PlannedTweak(TweakDefinition Definition, bool Selected, Applicability Applicability, string? Note)
{
    public bool InProfile { get; init; }
}

public sealed record OptimizationPlan(OptimizationProfile Profile, IReadOnlyList<PlannedTweak> Items)
{
    public IEnumerable<PlannedTweak> Selected => Items.Where(i => i.Selected);
    public bool RequiresReboot => Selected.Any(i => i.Definition.RequiresReboot);

    /// <summary>Gera um novo plano com a seleção alterada pelo usuário (respeitando aplicabilidade e grupos).</summary>
    public OptimizationPlan WithSelection(IEnumerable<string> selectedIds)
    {
        var ids = selectedIds.ToHashSet();
        var takenGroups = new HashSet<string>();
        var items = new List<PlannedTweak>();
        foreach (var i in Items)
        {
            var want = ids.Contains(i.Definition.Id) && i.Applicability.IsApplicable;
            if (want && i.Definition.Group is { } g && !takenGroups.Add(g)) want = false;
            items.Add(i with { Selected = want });
        }
        return this with { Items = items };
    }
}

public sealed record ProfileRecommendation(OptimizationProfile Profile, IReadOnlyList<string> Reasons);

public static class RecommendationEngine
{
    public static OptimizationPlan BuildPlan(HardwareProfile hw, OptimizationProfile profile, IReadOnlyList<TweakDefinition>? catalog = null)
    {
        catalog ??= TweakCatalog.Tweaks;
        var takenGroups = new HashSet<string>();
        var items = new List<PlannedTweak>(catalog.Count);

        foreach (var def in catalog)
        {
            Applicability app;
            try { app = def.AppliesTo(hw); }
            catch (Exception ex) { app = Applicability.No($"Falha ao avaliar: {ex.Message}"); }

            var inProfile = def.Profiles.Contains(profile);
            var selected = inProfile && app.IsApplicable;
            string? note = null;

            if (selected && def.Group is { } g && !takenGroups.Add(g))
            {
                selected = false;
                note = "Outro ajuste do mesmo grupo já foi escolhido.";
            }
            items.Add(new PlannedTweak(def, selected, app, note) { InProfile = inProfile });
        }
        return new OptimizationPlan(profile, items);
    }

    public static ProfileRecommendation Recommend(HardwareProfile hw)
    {
        var reasons = new List<string>();
        var gpu = hw.PrimaryGpu;
        var hasDiscrete = gpu is { IsDiscrete: true };

        if (hw.State.IsLaptop)
        {
            reasons.Add("Notebook detectado: o perfil Desempenho usa o Modo de energia 'Melhor desempenho' sem fixar a CPU no clock máximo, evitando throttling térmico.");
            if (hw.State.OnBattery) reasons.Add("Você está na bateria agora: conecte o carregador antes de jogar.");
            return new(OptimizationProfile.Desempenho, reasons);
        }

        if (hw.Memory.TotalGb < 11.5)
        {
            reasons.Add($"Apenas {hw.Memory.TotalGb:0.#} GB de RAM: mantemos o SysMain e a compressão de memória (perfil Desempenho).");
            return new(OptimizationProfile.Desempenho, reasons);
        }

        if (gpu is null || gpu.IsVirtual)
        {
            reasons.Add("Máquina virtual ou sem driver de vídeo: aplicamos só ajustes seguros e genéricos (perfil Desempenho).");
            return new(OptimizationProfile.Desempenho, reasons);
        }

        if (!hasDiscrete)
        {
            reasons.Add("Sem GPU dedicada: o gargalo é a GPU integrada, então ajustes agressivos de CPU/energia rendem pouco.");
            return new(OptimizationProfile.Desempenho, reasons);
        }

        reasons.Add($"Desktop com GPU dedicada ({gpu!.Name}) e {hw.Memory.TotalGb:0.#} GB de RAM: perfil Competitivo para menor latência e FPS máximo.");
        if (hw.Cpu.IsDualCcdX3D)
            reasons.Add($"{hw.Cpu.Name}: core parking e plano Equilibrado são preservados (o driver AMD depende deles).");
        if (hw.Cpu.IsHybrid)
            reasons.Add($"CPU híbrida ({hw.Cpu.PerformanceCores}P + {hw.Cpu.EfficiencyCores}E): {(hw.Os.IsWindows11 ? "o Windows 11 usa o Thread Director corretamente" : "no Windows 10 o agendador não conhece o Thread Director; considere o Windows 11")}.");
        return new(OptimizationProfile.Competitivo, reasons);
    }
}
