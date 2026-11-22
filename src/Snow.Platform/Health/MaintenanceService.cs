using System.Diagnostics;
using System.Runtime.InteropServices;
using Snow.Core.Health;
using Snow.Platform.Monitor;
using Snow.Platform.Services;

namespace Snow.Platform.Health;

/// <summary>
/// Limpeza e manutenção do Snow Health. Só apaga dentro das pastas do catálogo, nunca segue links (junctions/symlinks)
/// e ignora arquivos em uso. Processos de jogos e anti-cheat nunca são abertos: o uso vem das contagens de desempenho do Windows.
/// </summary>
public static class MaintenanceService
{
    public static IReadOnlyDictionary<string, string> DefaultEnv()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new Dictionary<string, string>
        {
            ["TEMP"] = Path.GetTempPath().TrimEnd('\\', '/'),
            ["WINDIR"] = win,
            ["LOCALAPPDATA"] = local,
            ["LOCALLOW"] = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["PROGRAMDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["SYSTEMDRIVE"] = Path.GetPathRoot(win)?.TrimEnd('\\') ?? "C:",
        };
    }

    private static readonly EnumerationOptions Opts = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    /// <summary>Expande os caminhos da categoria (inclui curinga no último segmento) para pastas/arquivos existentes.</summary>
    public static IEnumerable<string> Targets(CleanupCategory c, IReadOnlyDictionary<string, string> env)
    {
        foreach (var t in c.Paths)
        {
            var p = CleanupCatalog.Resolve(t, env);
            var leaf = Path.GetFileName(p);
            if (leaf.Contains('*'))
            {
                var dir = Path.GetDirectoryName(p);
                if (dir is null || !Directory.Exists(dir)) continue;
                foreach (var d in Directory.EnumerateDirectories(dir, leaf)) if (!IsLink(d)) yield return d;
            }
            else if (Directory.Exists(p) && !IsLink(p)) yield return p;
            else if (File.Exists(p)) yield return p;
        }
    }

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint); } catch { return true; }
    }

    public static string? RunningBlocker(CleanupCategory c, Func<string, bool>? isRunning = null)
    {
        isRunning ??= IsRunning;
        var app = c.SkipIfRunning.FirstOrDefault(isRunning);
        return app is null ? null : $"{app} está aberto";
    }

    /// <summary>Só o nome, da lista do Windows (não abre o processo).</summary>
    public static bool IsRunning(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    private static IEnumerable<FileInfo> OldFiles(string target, int minAgeDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-minAgeDays);
        IEnumerable<string> files = File.Exists(target) ? [target] : Directory.EnumerateFiles(target, "*", Opts);
        foreach (var f in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(f); if (minAgeDays > 0 && fi.LastWriteTimeUtc > cutoff) continue; }
            catch { continue; }
            yield return fi;
        }
    }

    public static CleanupMeasure Measure(CleanupCategory c, IReadOnlyDictionary<string, string>? env = null, Func<string, bool>? isRunning = null)
    {
        env ??= DefaultEnv();
        if (c.Kind == CleanupKind.RecycleBin)
        {
            var (count, size) = RecycleBinInfo();
            return new(c.Id, count, size);
        }
        long files = 0, bytes = 0;
        foreach (var t in Targets(c, env))
            foreach (var fi in OldFiles(t, c.Kind == CleanupKind.Manual ? 0 : c.MinAgeDays))
            {
                try { bytes += fi.Length; files++; } catch { }
            }
        return new(c.Id, files, bytes, c.Kind == CleanupKind.Files ? RunningBlocker(c, isRunning) : null);
    }

    public static CleanupResult Clean(CleanupCategory c, IReadOnlyDictionary<string, string>? env = null, Func<string, bool>? isRunning = null)
    {
        env ??= DefaultEnv();
        switch (c.Kind)
        {
            case CleanupKind.Manual:
                return new(c.Id, 0, 0, 0, "Feito pelo Windows (Configurações > Armazenamento)");
            case CleanupKind.RecycleBin:
            {
                var (count, size) = RecycleBinInfo();
                if (count == 0) return new(c.Id, 0, 0, 0);
                var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                return hr == 0 ? new(c.Id, count, size, 0) : new(c.Id, 0, 0, count, $"Falha ao esvaziar (0x{hr:X8})");
            }
        }
        if (RunningBlocker(c, isRunning) is { } why) return new(c.Id, 0, 0, 0, why);

        long files = 0, bytes = 0, failed = 0;
        foreach (var t in Targets(c, env).ToList())
        {
            foreach (var fi in OldFiles(t, c.MinAgeDays).ToList())
            {
                try
                {
                    var len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    files++; bytes += len;
                }
                catch (IOException) { failed++; }
                catch (UnauthorizedAccessException) { failed++; }
            }
            if (!Directory.Exists(t)) continue;
            // Remove subpastas que ficaram vazias, de baixo para cima; nunca a pasta raiz da categoria.
            foreach (var d in Directory.EnumerateDirectories(t, "*", Opts).OrderByDescending(p => p.Length).ToList())
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return new(c.Id, files, bytes, failed);
    }

    // ── Lixeira
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);
    private const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;

    public static (long count, long bytes) RecycleBinInfo()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

    // ── Unidades: TRIM e otimização semanal
    public static DriveCare ReadDriveCare()
    {
        bool? trim = null, scheduled = null;
        try
        {
            var r = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "fsutil.exe"), "behavior query DisableDeleteNotify", TimeSpan.FromSeconds(15));
            trim = DriveCareParser.TrimFromFsutil(r.StdOut);
        }
        catch { }
        try
        {
            var r = PowerShell.Run("(Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag' -ErrorAction Stop).State", TimeSpan.FromSeconds(30));
            var s = r.StdOut.Trim();
            if (s.Length > 0) scheduled = !s.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return new(trim, scheduled);
    }

    public static string EnableTrim()
    {
        var r = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "fsutil.exe"), "behavior set DisableDeleteNotify 0", TimeSpan.FromSeconds(15));
        return r.ExitCode == 0 ? "TRIM ligado." : $"Falha ao ligar o TRIM: {r.StdOut}{r.StdErr}".Trim();
    }

    public static string EnableScheduledOptimization()
    {
        var r = PowerShell.Run("Enable-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag' -ErrorAction Stop | Out-Null; 'ok'", TimeSpan.FromSeconds(30));
        return r.StdOut.Contains("ok") ? "Otimização semanal religada." : $"Falha: {r.StdErr.Trim()}";
    }

    /// <summary>defrag /C /O: o Windows decide por tipo de unidade (TRIM no SSD, desfragmentação no HD). Pode demorar em HDs.</summary>
    public static string OptimizeDrives(TimeSpan timeout)
    {
        var r = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "defrag.exe"), "/C /O /U", timeout);
        return r.ExitCode == 0 ? "Unidades otimizadas." : $"Otimização terminou com código {r.ExitCode}. {LastLine(r.StdOut)}".Trim();
    }

    /// <summary>DISM StartComponentCleanup: remove versões antigas de componentes do WinSxS (o próprio Windows faz isso de tempos em tempos).</summary>
    public static string CleanComponents(TimeSpan timeout)
    {
        var r = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup /NoRestart", timeout);
        return r.ExitCode == 0 ? "Componentes antigos do Windows removidos." : $"DISM terminou com código {r.ExitCode}. {LastLine(r.StdOut)}".Trim();
    }

    private static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    // ── Processos
    /// <summary>Amostra o uso por app durante N segundos pelas contagens de desempenho (sem abrir nenhum processo).</summary>
    public static ProcessReport SampleProcesses(int seconds, CancellationToken ct = default, Action<int>? tick = null)
    {
        using var s = new ProcessUsageSampler();
        var cpu = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var mem = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var inst = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cores = Math.Max(1, Environment.ProcessorCount);
        for (var i = 0; i < seconds && !ct.IsCancellationRequested; i++)
        {
            Thread.Sleep(1000);
            var (c, m) = s.Sample();
            foreach (var g in c.GroupBy(x => ProcessAdvisor.AppFromPdhInstance(x.name), StringComparer.OrdinalIgnoreCase))
            {
                if (!cpu.TryGetValue(g.Key, out var l)) cpu[g.Key] = l = [];
                l.Add(g.Sum(x => x.value) / cores);
                inst[g.Key] = Math.Max(inst.GetValueOrDefault(g.Key), g.Count());
            }
            foreach (var g in m.GroupBy(x => ProcessAdvisor.AppFromPdhInstance(x.name), StringComparer.OrdinalIgnoreCase))
                mem[g.Key] = Math.Max(mem.GetValueOrDefault(g.Key), (long)g.Sum(x => x.value));
            tick?.Invoke(i + 1);
        }
        var usages = cpu.Keys.Union(mem.Keys, StringComparer.OrdinalIgnoreCase).Select(app =>
        {
            var l = cpu.GetValueOrDefault(app) ?? [];
            // Média sobre todas as amostras (app que apareceu só no fim conta pouco)
            var avg = l.Count == 0 ? 0 : l.Sum() / Math.Max(l.Count, seconds);
            return new ProcessUsage(app, Math.Max(1, inst.GetValueOrDefault(app)), Math.Round(avg, 1), Math.Round(l.DefaultIfEmpty(0).Max(), 1), mem.GetValueOrDefault(app));
        });
        return ProcessAdvisor.Analyze(usages, seconds);
    }

    /// <summary>
    /// Fecha um app pelo nome: primeiro pede para fechar as janelas (o app pode perguntar se quer salvar);
    /// com force, encerra o que sobrou. Recusa jogos, anti-cheat, Windows e antivírus.
    /// </summary>
    public static string CloseApp(string app, bool force)
    {
        if (ProcessAdvisor.IsProtected(app)) return $"{app}: o Snow não fecha este processo.";
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app));
        if (procs.Length == 0) return $"{app} já está fechado.";
        int asked = 0, killed = 0;
        foreach (var p in procs)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) asked++;
                else if (force) { p.Kill(entireProcessTree: false); killed++; }
            }
            catch { }
        }
        if (asked > 0 && force)
        {
            Thread.Sleep(3000);
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app)))
                try { p.Kill(); killed++; } catch { }
        }
        foreach (var p in procs) p.Dispose();
        return killed > 0 ? $"{app}: encerrado." : asked > 0 ? $"{app}: pedido para fechar enviado." : $"{app}: sem janela para fechar (use Encerrar).";
    }
}

/// <summary>"\Process(*)\% Processor Time" e "Working Set - Private": lidos do Windows, sem abrir handles de processo.</summary>
public sealed class ProcessUsageSampler : IDisposable
{
    private readonly IntPtr _query, _cpu, _mem;

    public ProcessUsageSampler()
    {
        if (PdhSampler.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) throw new InvalidOperationException("PDH indisponível.");
        PdhSampler.PdhAddEnglishCounterW(_query, @"\Process(*)\% Processor Time", IntPtr.Zero, out _cpu);
        PdhSampler.PdhAddEnglishCounterW(_query, @"\Process(*)\Working Set - Private", IntPtr.Zero, out _mem);
        PdhSampler.PdhCollectQueryData(_query);
    }

    public (List<(string name, double value)> cpu, List<(string name, double value)> mem) Sample()
    {
        PdhSampler.PdhCollectQueryData(_query);
        return (PdhSampler.Array(_cpu), PdhSampler.Array(_mem));
    }

    public void Dispose() => PdhSampler.PdhCloseQuery(_query);
}
