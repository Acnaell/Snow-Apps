using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Snow.Platform.Monitor;

/// <summary>
/// FPS/frametime ao vivo com o Intel PresentMon (console), lendo o CSV que ele escreve no stdout.
/// O PresentMon usa eventos ETW do Windows: não injeta nada no jogo (compatível com anti-cheats).
/// </summary>
public sealed class PresentMonLive : IDisposable
{
    private readonly Process _proc;
    private readonly object _lock = new();
    // por aplicativo: quadros dos últimos 30 s (ao vivo) e todos os quadros (resumo/arquivo)
    private readonly Dictionary<string, List<(DateTime t, double ft)>> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<double>> _all = new(StringComparer.OrdinalIgnoreCase);
    private int _ftCol = -1, _appCol = -1, _pidCol = -1;
    private readonly Dictionary<uint, string> _pidApp = [];
    private readonly string? _fixedApp;

    public string? LastError { get; private set; }
    public bool IsRunning => !_proc.HasExited;
    /// <summary>Modo automático: mede todos os processos e detecta o jogo.</summary>
    public bool IsAuto => _fixedApp is null;
    /// <summary>Jogo em foco agora (modo automático: detectado; modo fixo: o processo escolhido).</summary>
    public string? ActiveApp { get; private set; }

    /// <summary>Quadros do jogo principal da sessão (o que teve mais quadros).</summary>
    public IReadOnlyList<double> AllFrameTimes { get { lock (_lock) return MainApp is { } a ? _all[a].ToList() : []; } }

    public string? MainApp
    {
        get
        {
            lock (_lock)
                return _all.Where(kv => _fixedApp is not null || !Core.Monitor.ActiveGameDetector.IsIgnored(kv.Key))
                           .OrderByDescending(kv => kv.Value.Count).Select(kv => kv.Key).FirstOrDefault();
        }
    }

    /// <summary>Jogos medidos na sessão com pelo menos ~30 s de quadros (para o resumo).</summary>
    public IReadOnlyList<(string App, IReadOnlyList<double> Frames)> GamesMeasured()
    {
        lock (_lock)
            return _all.Where(kv => !Core.Monitor.ActiveGameDetector.IsIgnored(kv.Key) && kv.Value.Sum() > 30_000)
                       .OrderByDescending(kv => kv.Value.Count)
                       .Select(kv => (kv.Key, (IReadOnlyList<double>)kv.Value.ToList())).ToList();
    }

    /// <summary>Procura o PresentMon: pasta do Snow, Downloads ou %LOCALAPPDATA%\Snow\tools.</summary>
    public static string? Find()
    {
        var dirs = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snow", "tools"),
        };
        foreach (var d in dirs.Where(Directory.Exists))
        {
            var exe = Directory.EnumerateFiles(d, "PresentMon*.exe")
                .Where(f => !Path.GetFileName(f).Contains("x86", StringComparison.OrdinalIgnoreCase) || !Environment.Is64BitOperatingSystem)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (exe is not null) return exe;
        }
        return null;
    }

    public const string SessionPrefix = "SnowMonitor";
    private readonly string _session;
    private readonly DateTime _started = DateTime.UtcNow;

    /// <summary>
    /// Sessões ETW sobrevivem ao PresentMon quando ele é encerrado à força. Se a sessão anterior ficou
    /// aberta, a próxima medição não recebe nenhum quadro (aconteceu na 2ª sessão de Fortnite do Thiago).
    /// Por isso: nome único por medição + limpeza das sessões "SnowMonitor*" antes e depois.
    /// </summary>
    /// <param name="processName">Executável do jogo, ou null para medir todos os processos e detectar o jogo.</param>
    public PresentMonLive(string presentMonExe, string? processName)
    {
        StopOrphanSessions();
        _fixedApp = processName;
        ActiveApp = processName;
        _session = $"{SessionPrefix}_{Environment.ProcessId}_{DateTime.UtcNow:HHmmss}";
        var filter = processName is null ? "" : $"--process_name \"{processName}\" ";
        var psi = new ProcessStartInfo(presentMonExe,
            $"{filter}--output_stdout --v2_metrics --session_name {_session}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o PresentMon.");
        _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data; };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    private void OnLine(string line)
    {
        var cells = line.Split(',');
        if (_ftCol < 0)
        {
            _ftCol = Array.FindIndex(cells, c => c.Trim().Equals("FrameTime", StringComparison.OrdinalIgnoreCase)
                                              || c.Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
            _appCol = Array.FindIndex(cells, c => c.Trim().Equals("Application", StringComparison.OrdinalIgnoreCase));
            _pidCol = Array.FindIndex(cells, c => c.Trim().Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
            return;
        }
        if (cells.Length <= _ftCol || !double.TryParse(cells[_ftCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ft) || ft <= 0 || ft > 1000) return;
        var app = _appCol >= 0 && _appCol < cells.Length && cells[_appCol].Length > 0 ? cells[_appCol].Trim() : _fixedApp ?? "?";
        lock (_lock)
        {
            if (_pidCol >= 0 && _pidCol < cells.Length && uint.TryParse(cells[_pidCol], out var pid)) _pidApp[pid] = app;
            var now = DateTime.UtcNow;
            if (!_recent.TryGetValue(app, out var recent)) _recent[app] = recent = [];
            recent.Add((now, ft));
            if (recent.Count > 64 && (now - recent[0].t).TotalSeconds > 30)
                recent.RemoveAll(f => (now - f.t).TotalSeconds > 30);
            if (_fixedApp is not null || !Core.Monitor.ActiveGameDetector.IsIgnored(app)) // apps de fundo: só a contagem recente
            {
                if (!_all.TryGetValue(app, out var all)) _all[app] = all = [];
                all.Add(ft);
            }
        }
    }

    /// <summary>Atualiza o jogo ativo (modo automático). Chamar uma vez por segundo.</summary>
    public string? UpdateActive()
    {
        if (_fixedApp is not null) return ActiveApp;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var counts = _recent.ToDictionary(kv => kv.Key, kv => kv.Value.Count(f => (now - f.t).TotalSeconds <= 3), StringComparer.OrdinalIgnoreCase);
            string? fg = null;
            try
            {
                // id do processo da janela em primeiro plano, comparado com o ProcessID que o PresentMon já informa
                Native.NativeMethods.GetWindowThreadProcessId(Native.NativeMethods.GetForegroundWindow(), out var fgPid);
                _pidApp.TryGetValue(fgPid, out fg);
            }
            catch { }
            ActiveApp = Core.Monitor.ActiveGameDetector.Pick(counts, ActiveApp, fg);
            return ActiveApp;
        }
    }

    /// <summary>Sem quadros depois de 15 s com o PresentMon rodando: o jogo não está aberto ou a captura falhou.</summary>
    public bool NoFramesYet { get { lock (_lock) return (ActiveApp is null || !_recent.ContainsKey(ActiveApp)) && (DateTime.UtcNow - _started).TotalSeconds > 15; } }

    /// <summary>Nomes de sessões ETW do Snow em "logman query -ets" (puro, testável).</summary>
    public static IReadOnlyList<string> ParseSnowSessions(string logmanOutput) =>
        logmanOutput.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Where(n => n.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Logman(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("logman.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch { return ""; }
    }

    public static void StopOrphanSessions()
    {
        foreach (var name in ParseSnowSessions(Logman("query -ets")))
            Logman($"stop \"{name}\" -ets");
    }

    /// <summary>FPS do último segundo e 1% low dos últimos 30 s do jogo ativo.</summary>
    public (double? fps, double? low1) Current()
    {
        lock (_lock)
        {
            if (ActiveApp is null || !_recent.TryGetValue(ActiveApp, out var frames) || frames.Count < 5) return (null, null);
            var now = DateTime.UtcNow;
            frames.RemoveAll(f => (now - f.t).TotalSeconds > 30);
            var lastSecond = frames.Where(f => (now - f.t).TotalSeconds <= 1).Select(f => f.ft).ToList();
            double? fps = lastSecond.Count > 0 ? Math.Round(1000.0 * lastSecond.Count / lastSecond.Sum(), 0) : null;
            var stats = Core.Monitor.FrameStats.From(frames.Select(f => f.ft).ToList());
            return (fps, stats?.Low1);
        }
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
        Logman($"stop \"{_session}\" -ets"); // Kill não fecha a sessão ETW
    }
}
