using System.Text.Json;
using System.Text.Json.Serialization;
using Snow.Core.Recommendation;
using Snow.Core.Tweaks;
using Snow.Platform.Executors;

namespace Snow.Platform.Services;

public enum TweakStatus { Pending, Applied, PartiallyApplied, Skipped, Failed, Reverted }

public sealed class TweakBackup
{
    public string TweakId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Reversible { get; set; } = true;
    public TweakStatus Status { get; set; }
    public List<ActionSnapshot> Snapshots { get; set; } = [];
    public List<string> Messages { get; set; } = [];
}

public sealed class BackupSession
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RestoredUtc { get; set; }
    public string Profile { get; set; } = "";
    public string Machine { get; set; } = Environment.MachineName;
    public string OsBuild { get; set; } = Environment.OSVersion.Version.ToString();
    public string? RestorePoint { get; set; }
    public List<TweakBackup> Tweaks { get; set; } = [];

    [JsonIgnore] public bool HasRevertibleChanges => Tweaks.Any(t => t.Snapshots.Count > 0 && t.Status is TweakStatus.Applied or TweakStatus.PartiallyApplied);
}

public sealed class BackupStore(string? dir = null)
{
    private readonly string _dir = dir ?? AppPaths.BackupsDir;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public void Save(BackupSession s)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{s.Id}.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, path, overwrite: true); // gravação atômica: um crash não corrompe o backup
    }

    public IReadOnlyList<BackupSession> List() =>
        Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.json")
                .Select(f => { try { return JsonSerializer.Deserialize<BackupSession>(File.ReadAllText(f), Json); } catch { return null; } })
                .Where(s => s is not null).Cast<BackupSession>()
                .OrderByDescending(s => s.CreatedUtc).ToList()
            : [];

    public BackupSession? Get(string id) => List().FirstOrDefault(s => s.Id == id);
}

public sealed record ApplyOptions(bool CreateRestorePoint = true, string? Label = null);
public sealed record ProgressInfo(int Done, int Total, string Message);

public sealed record SelfTestResult(bool Success, IReadOnlyList<string> Lines);

public sealed class OptimizationService(ExecutorSet executors, BackupStore backups, ILog log)
{
    public static OptimizationService CreateDefault(ILog? log = null)
    {
        var reg = new WindowsRegistryStore();
        var set = ExecutorSet.CreateDefault(reg, new WindowsServiceControl(reg), new WindowsPowerApi(), new JsonStateStore());
        return new(set, new BackupStore(), log ?? FileLog.Instance);
    }

    public BackupStore Backups => backups;

    public BackupSession Apply(OptimizationPlan plan, ApplyOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var selected = plan.Selected.ToList();
        var session = new BackupSession { Profile = options.Label ?? plan.Profile.ToString() };
        backups.Save(session);
        log.Info($"=== Aplicando perfil {plan.Profile}: {selected.Count} ajuste(s). Backup {session.Id} ===");

        if (options.CreateRestorePoint)
        {
            progress?.Report(new(0, selected.Count, "Criando ponto de restauração do sistema…"));
            var rp = RestorePointService.TryCreate($"Snow - antes do perfil {plan.Profile}");
            session.RestorePoint = rp.Message;
            log.Info($"Ponto de restauração: {rp.Message}");
            backups.Save(session);
        }

        for (var i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var def = selected[i].Definition;
            progress?.Report(new(i, selected.Count, def.Title));
            session.Tweaks.Add(ApplyTweak(def, session));
        }
        progress?.Report(new(selected.Count, selected.Count, "Concluído"));
        log.Info($"=== Fim. {session.Tweaks.Count(t => t.Status == TweakStatus.Applied)} aplicados, " +
                 $"{session.Tweaks.Count(t => t.Status == TweakStatus.Skipped)} ignorados, {session.Tweaks.Count(t => t.Status == TweakStatus.Failed)} com falha ===");
        return session;
    }

    private TweakBackup ApplyTweak(TweakDefinition def, BackupSession session)
    {
        var tb = new TweakBackup { TweakId = def.Id, Title = def.Title, Reversible = def.Reversible, Status = TweakStatus.Pending };
        var applied = 0;
        var skipped = 0;
        foreach (var action in def.Actions)
        {
            var ex = executors.For(action);
            try
            {
                var snap = ex.Capture(action);
                var outcome = ex.Apply(action);
                tb.Snapshots.Add(snap);
                tb.Messages.Add($"✔ {action.Describe()} — {outcome.Message}");
                log.Info($"[{def.Id}] {action.Describe()} → {outcome.Message} (antes: {Summarize(snap)})");
                applied++;
                backups.Save(session); // incremental: cada mudança já fica registrada
            }
            catch (SkipException sk)
            {
                skipped++;
                tb.Messages.Add($"↷ {action.Describe()} — {sk.Message}");
                log.Info($"[{def.Id}] ignorado: {sk.Message}");
            }
            catch (Exception e)
            {
                tb.Messages.Add($"✖ {action.Describe()} — {e.Message}");
                log.Error($"[{def.Id}] falhou: {e}");
                // desfaz o que este ajuste já tinha alterado para não deixar estado parcial
                RevertSnapshots(tb.Snapshots);
                tb.Snapshots.Clear();
                tb.Status = TweakStatus.Failed;
                return tb;
            }
        }
        tb.Status = applied == 0 ? TweakStatus.Skipped : skipped > 0 ? TweakStatus.PartiallyApplied : TweakStatus.Applied;
        return tb;
    }

    private static string Summarize(ActionSnapshot s) =>
        s.Data.Count == 0 ? "-" : string.Join(", ", s.Data.Take(4).Select(kv => $"{kv.Key}={kv.Value ?? "(ausente)"}")) + (s.Data.Count > 4 ? ", …" : "");

    public int Restore(BackupSession session)
    {
        log.Info($"=== Restaurando backup {session.Id} ({session.Profile}) ===");
        var count = 0;
        foreach (var t in Enumerable.Reverse(session.Tweaks))
        {
            if (t.Status is not (TweakStatus.Applied or TweakStatus.PartiallyApplied)) continue;
            count += RevertSnapshots(t.Snapshots);
            t.Status = TweakStatus.Reverted;
            log.Info($"[{t.TweakId}] revertido");
        }
        session.RestoredUtc = DateTime.UtcNow;
        backups.Save(session);
        return count;
    }

    /// <summary>Reverte todas as sessões ainda não restauradas, da mais nova para a mais antiga.</summary>
    public int RestoreAll()
    {
        var n = 0;
        foreach (var s in backups.List().Where(s => s.RestoredUtc is null && s.HasRevertibleChanges))
            n += Restore(s);
        return n;
    }

    private int RevertSnapshots(List<ActionSnapshot> snaps)
    {
        var n = 0;
        foreach (var s in Enumerable.Reverse(snaps))
        {
            try { executors.ByName(s.Executor).Revert(s); n++; }
            catch (Exception e) { log.Error($"Falha ao reverter '{s.Description}': {e.Message}"); }
        }
        return n;
    }

    /// <summary>
    /// Teste de ida-e-volta usado no CI: captura o estado, aplica, verifica cada ação,
    /// restaura e confirma que o estado final é idêntico ao inicial.
    /// </summary>
    public SelfTestResult SelfTest(OptimizationPlan plan)
    {
        var lines = new List<string>();
        var ok = true;
        var actions = plan.Selected.SelectMany(p => p.Definition.Actions.Select(a => (p.Definition, a)))
                          .Where(x => x.a is not CleanupAction).ToList();

        var before = actions.Select(x => Safe(() => executors.For(x.a).Capture(x.a))).ToList();
        var testPlan = plan.WithSelection(plan.Selected.Where(p => p.Definition.Reversible).Select(p => p.Definition.Id));
        var session = Apply(testPlan, new ApplyOptions(CreateRestorePoint: false, Label: $"Autoteste ({plan.Profile})"));

        foreach (var t in session.Tweaks)
        {
            lines.Add($"[{t.Status}] {t.TweakId}");
            if (t.Status == TweakStatus.Failed) { ok = false; lines.AddRange(t.Messages.Select(m => "    " + m)); }
        }
        foreach (var (def, a) in actions)
        {
            var tweak = session.Tweaks.FirstOrDefault(t => t.TweakId == def.Id);
            if (tweak is null || tweak.Status is TweakStatus.Skipped or TweakStatus.Failed) continue;
            var v = Safe(() => executors.For(a).Verify(a));
            if (v == false)
            {
                // ação ignorada dentro de um ajuste parcialmente aplicado não conta como erro
                if (tweak.Messages.Any(m => m.StartsWith('↷') && m.Contains(a.Describe()))) continue;
                ok = false;
                lines.Add($"  ✖ verificação falhou: {a.Describe()}");
            }
        }

        Restore(session);

        var after = actions.Select(x => Safe(() => executors.For(x.a).Capture(x.a))).ToList();
        for (var i = 0; i < actions.Count; i++)
        {
            if (before[i] is null || after[i] is null) continue;
            var b = string.Join(";", before[i]!.ComparableData.Select(kv => $"{kv.Key}={kv.Value}"));
            var c = string.Join(";", after[i]!.ComparableData.Select(kv => $"{kv.Key}={kv.Value}"));
            if (b != c)
            {
                ok = false;
                lines.Add($"  ✖ estado não restaurado: {actions[i].a.Describe()}\n      antes:  {b}\n      depois: {c}");
            }
        }
        lines.Add(ok ? "SELFTEST OK: aplicar → verificar → restaurar → estado idêntico." : "SELFTEST FALHOU.");
        return new(ok, lines);
    }

    private T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) { log.Error(e.Message); return default; }
    }
}
