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

    public PresentMonLive(string presentMonExe, string processName)
    {
        var psi = new ProcessStartInfo(presentMonExe,
            $"--process_name \"{processName}\" --output_stdout --v2_metrics --session_name SnowMonitor")
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
    }
}
