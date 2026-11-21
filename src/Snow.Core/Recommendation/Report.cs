using System.Text;
using Snow.Core.Hardware;
using Snow.Core.Tweaks;

namespace Snow.Core.Recommendation;

public static class Report
{
    public static string Speed(MemoryInfo m) => m.ConfiguredSpeedMts > 0 ? $"{m.ConfiguredSpeedMts} MT/s" : "velocidade não informada";

    public static string Gb(long bytes) => bytes <= 0 ? "?" : $"{bytes / 1024d / 1024 / 1024:0.#} GB";

    public static string Label(this TriState s) => s switch
    {
        TriState.On => "Ativado",
        TriState.Off => "Desativado",
        TriState.Default => "Padrão do sistema",
        _ => "Desconhecido",
    };

    public static string Label(this ServiceStart s) => s switch
    {
        ServiceStart.Automatic => "Automático",
        ServiceStart.AutomaticDelayed => "Automático (atrasado)",
        ServiceStart.Manual => "Manual",
        ServiceStart.Disabled => "Desativado",
        _ => s.ToString(),
    };

    public static string Label(this DiskKind k) => k switch
    {
        DiskKind.NvmeSsd => "SSD NVMe",
        DiskKind.SataSsd => "SSD SATA",
        DiskKind.OtherSsd => "SSD",
        DiskKind.Hdd => "HDD",
        DiskKind.Virtual => "Disco virtual",
        _ => "Desconhecido",
    };

    public static string Label(this OptimizationProfile p) => p switch
    {
        OptimizationProfile.Competitivo => "Alto Desempenho (Competitivo)",
        OptimizationProfile.Desempenho => "Desempenho",
        _ => "Equilibrado",
    };

    public static string CpuArch(CpuInfo c) =>
        c.IsHybrid ? $"Híbrida: {c.PerformanceCores} P-cores + {c.EfficiencyCores} E-cores"
        : c.IsDualCcdX3D ? "AMD Ryzen X3D (2 CCDs, 3D V-Cache em um deles)"
        : c.IsX3D ? "AMD Ryzen X3D (3D V-Cache)"
        : c.Vendor == CpuVendor.Amd ? "AMD (núcleos homogêneos)"
        : c.Vendor == CpuVendor.Intel ? "Intel (núcleos homogêneos)"
        : c.Vendor.ToString();

    public static string ToText(HardwareProfile hw)
    {
        var sb = new StringBuilder();
        var c = hw.Cpu; var m = hw.Memory; var s = hw.State;
        sb.AppendLine("══════════ SNOW — DIAGNÓSTICO ══════════");
        sb.AppendLine($"Sistema     : {hw.Os.FriendlyName} {hw.Os.Edition} [{hw.Os.Architecture}]");
        sb.AppendLine($"Tipo        : {(s.IsLaptop ? "Notebook" : "Desktop")}{(s.ModernStandby ? " · Modern Standby" : "")}{(s.OnBattery ? " · NA BATERIA" : "")}");
        sb.AppendLine();
        sb.AppendLine($"CPU         : {c.Name}");
        sb.AppendLine($"              {CpuArch(c)} · {c.PhysicalCores} núcleos / {c.LogicalProcessors} threads · {c.MaxClockMhz} MHz base");
        sb.AppendLine($"              Virtualização: {(c.VirtualizationFirmwareEnabled || c.HypervisorPresent ? "ativa" : "desativada na BIOS")}{(c.HypervisorPresent ? " (hypervisor presente)" : "")}");
        foreach (var g in hw.Gpus)
            sb.AppendLine($"GPU         : {g.Name} · {Gb(g.VramBytes)} · driver {g.DriverVersionFriendly}{(g.IsDiscrete ? " · dedicada" : g.IsVirtual ? " · virtual" : " · integrada")}{(g.SupportsHags ? " · HAGS ok" : "")}");
        if (hw.MonitorRefreshHz > 0) sb.AppendLine($"Monitor     : {hw.MonitorRefreshHz} Hz");
        sb.AppendLine($"RAM         : {Gb(m.TotalBytes)} {(m.Type == "Desconhecido" ? "" : m.Type + " ")}@ {Speed(m)} · {m.Channels}{(m.ChannelsEstimated ? " (estimado)" : "")} channel · {m.Modules.Count} módulo(s) · {m.UsedPercent:0}% em uso");
        foreach (var d in hw.Storage.Disks)
            sb.AppendLine($"Disco {d.Number,-6}: {d.Model} · {d.Kind.Label()} · {Gb(d.SizeBytes)} · {(d.DriveLetters.Count > 0 ? string.Join(", ", d.DriveLetters.Select(l => l + ":")) : "sem letra")}{(d.DriveLetters.Contains(hw.Storage.SystemDrive) ? " (Windows)" : "")}");
        if (hw.Storage.GameLibraries.Count > 0)
            sb.AppendLine($"Jogos       : {string.Join(" | ", hw.Storage.GameLibraries)}");
        sb.AppendLine();
        sb.AppendLine($"Energia     : {s.ActivePowerPlanName} ({s.ActivePowerPlanGuid})");
        sb.AppendLine($"Modo de Jogo: {s.GameMode.Label()} · HAGS: {s.Hags.Label()} · Jogos em janela: {s.WindowedGameOptimizations.Label()}");
        sb.AppendLine($"Game DVR    : gravação em 2º plano {s.GameDvrBackgroundRecording.Label()} · HVCI: {s.MemoryIntegrity.Label()}");
        sb.AppendLine($"Serviços    : SysMain={s.SysMainStart} · DiagTrack={s.DiagTrackStart}");

        var insights = Insights.Analyze(hw);
        if (insights.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("── Achados ──");
            foreach (var i in insights)
                sb.AppendLine($"[{i.Level}] {i.Title}: {i.Detail}");
        }
        var rec = RecommendationEngine.Recommend(hw);
        sb.AppendLine();
        sb.AppendLine($"Perfil recomendado: {rec.Profile.Label()}");
        foreach (var r in rec.Reasons) sb.AppendLine($"  • {r}");
        foreach (var w in hw.CollectionWarnings) sb.AppendLine($"(aviso de coleta) {w}");
        return sb.ToString();
    }

    public static string PlanToText(OptimizationPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"══════════ PLANO: {plan.Profile.Label()} ══════════");
        foreach (var grp in plan.Items.GroupBy(i => i.Definition.Category))
        {
            sb.AppendLine($"\n── {grp.Key} ──");
            foreach (var i in grp)
            {
                var mark = i.Selected ? "[x]" : i.Applicability.IsApplicable ? "[ ]" : "[-]";
                sb.AppendLine($"{mark} {i.Definition.Id,-22} {i.Definition.Title}{(i.Definition.RequiresReboot ? " (reiniciar)" : "")}{(i.Definition.Reversible ? "" : " (irreversível)")}");
                if (i.Selected)
                    foreach (var a in i.Definition.Actions) sb.AppendLine($"        → {a.Describe()}");
                if (!i.Applicability.IsApplicable) sb.AppendLine($"        ✗ {i.Applicability.Reason}");
                else if (i.Note is not null) sb.AppendLine($"        • {i.Note}");
            }
        }
        sb.AppendLine($"\n{plan.Selected.Count()} ajuste(s) selecionado(s).{(plan.RequiresReboot ? " Alguns exigem reiniciar o PC." : "")}");
        return sb.ToString();
    }

    public static bool TryParseProfile(string? s, out OptimizationProfile profile)
    {
        profile = (s ?? "").Trim().ToLowerInvariant() switch
        {
            "competitivo" or "alto" or "alto-desempenho" or "max" or "competitive" => OptimizationProfile.Competitivo,
            "desempenho" or "performance" => OptimizationProfile.Desempenho,
            "equilibrado" or "balanced" => OptimizationProfile.Equilibrado,
            _ => (OptimizationProfile)(-1),
        };
        return (int)profile >= 0;
    }
}
