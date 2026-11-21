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
    private readonly List<(DateTime t, double ft)> _frames = [];
    private int _ftCol = -1;

    public string? LastError { get; private set; }
    public bool IsRunning => !_proc.HasExited;
    public IReadOnlyList<double> AllFrameTimes { get { lock (_lock) return _all.ToList(); } }
    private readonly List<double> _all = [];

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
    public PresentMonLive(string presentMonExe, string processName)
    {
        StopOrphanSessions();
        _session = $"{SessionPrefix}_{Environment.ProcessId}_{DateTime.UtcNow:HHmmss}";
        var psi = new ProcessStartInfo(presentMonExe,
            $"--process_name \"{processName}\" --output_stdout --v2_metrics --session_name {_session}")
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
            return;
        }
        if (cells.Length <= _ftCol || !double.TryParse(cells[_ftCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ft) || ft <= 0 || ft > 1000) return;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _frames.Add((now, ft));
            _all.Add(ft);
            _frames.RemoveAll(f => (now - f.t).TotalSeconds > 30);
        }
    }

    /// <summary>Sem quadros depois de 15 s com o PresentMon rodando: o jogo não está aberto ou a captura falhou.</summary>
    public bool NoFramesYet { get { lock (_lock) return _all.Count == 0 && (DateTime.UtcNow - _started).TotalSeconds > 15; } }

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

    /// <summary>FPS do último segundo e 1% low dos últimos 30 s.</summary>
    public (double? fps, double? low1) Current()
    {
        lock (_lock)
        {
            if (_frames.Count < 5) return (null, null);
            var now = DateTime.UtcNow;
            var lastSecond = _frames.Where(f => (now - f.t).TotalSeconds <= 1).Select(f => f.ft).ToList();
            double? fps = lastSecond.Count > 0 ? Math.Round(1000.0 * lastSecond.Count / lastSecond.Sum(), 0) : null;
            var stats = Core.Monitor.FrameStats.From(_frames.Select(f => f.ft).ToList());
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
