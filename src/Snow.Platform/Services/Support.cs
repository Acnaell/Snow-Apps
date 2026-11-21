using System.Text;

namespace Snow.Platform.Services;

public interface ILog
{
    void Info(string message);
    void Error(string message);
    event Action<string>? Written;
}

public sealed class FileLog : ILog
{
    public static readonly FileLog Instance = new();
    private readonly object _lock = new();
    public event Action<string>? Written;

    private static string FilePath => Path.Combine(AppPaths.LogsDir, $"snow-{DateTime.Now:yyyyMMdd}.log");

    private void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level} {msg}";
        lock (_lock)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8); } catch { /* log nunca derruba o app */ }
        }
        Written?.Invoke(line);
    }

    public void Info(string message) => Write("INFO ", message);
    public void Error(string message) => Write("ERRO ", message);
}

public sealed record RestorePointResult(bool Created, string Message);

/// <summary>Ponto de restauração via Checkpoint-Computer (Windows PowerShell 5.1, presente no 10 e no 11).</summary>
public static class RestorePointService
{
    public static RestorePointResult TryCreate(string description)
    {
        var safe = description.Replace("'", "''");
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
  Enable-ComputerRestore -Drive ""$env:SystemDrive\"" -ErrorAction SilentlyContinue
  Checkpoint-Computer -Description '{safe}' -RestorePointType 'MODIFY_SETTINGS' -WarningVariable w -WarningAction SilentlyContinue
  if ($w) {{ 'WARN:' + ($w -join ' ') }} else {{ 'OK' }}
}} catch {{ 'ERR:' + $_.Exception.Message }}";
        try
        {
            var r = PowerShell.Run(script, TimeSpan.FromMinutes(3));
            var o = r.StdOut.Trim();
            if (o.StartsWith("OK")) return new(true, "criado");
            if (o.StartsWith("WARN:"))
                return new(false, "não criado: o Windows só permite um ponto a cada 24 h (o backup do Snow continua valendo). " + o[5..]);
            return new(false, "não criado: " + (o.StartsWith("ERR:") ? o[4..] : o + r.StdErr));
        }
        catch (Exception e) { return new(false, "não criado: " + e.Message); }
    }
}

public static class PowerShell
{
    /// <summary>Executa um script no Windows PowerShell 5.1 via -EncodedCommand (sem arquivos temporários, sem política de execução).</summary>
    public static ProcessResult Run(string script, TimeSpan? timeout = null)
    {
        var full = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n$ProgressPreference='SilentlyContinue'\n" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        return ProcessRunner.Run(exe, $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeout);
    }
}
