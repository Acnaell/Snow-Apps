using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Snow.Core.Tweaks;
using Snow.Platform.Native;
using RegistryHive = Snow.Core.Tweaks.RegistryHive;

namespace Snow.Platform.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static ProcessResult Run(string file, string args, TimeSpan? timeout = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Falha ao iniciar {file}");
        if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignorado */ }
            throw new TimeoutException($"{file} {args} excedeu o tempo limite.");
        }
        return new(p.ExitCode, outTask.Result, errTask.Result);
    }

    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}

public sealed class WindowsRegistryStore : IRegistryStore
{
    private static RegistryKey Base(RegistryHive hive) => hive == RegistryHive.LocalMachine
        ? RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64)
        : RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, RegistryView.Default);

    public RegValue? Get(RegistryHive hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, false);
        if (k is null) return null;
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (v is null) return null;
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new(RegistryKind.DWord, unchecked((uint)(int)v).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => new(RegistryKind.QWord, unchecked((ulong)(long)v).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.ExpandString => new(RegistryKind.ExpandString, (string)v),
            RegistryValueKind.MultiString => new(RegistryKind.MultiString, string.Join('\n', (string[])v)),
            RegistryValueKind.Binary => new(RegistryKind.Binary, Convert.ToHexString((byte[])v)),
            _ => new(RegistryKind.String, v.ToString() ?? ""),
        };
    }

    public void Set(RegistryHive hive, string path, string name, RegValue value)
    {
        using var b = Base(hive);
        using var k = b.CreateSubKey(path, true) ?? throw new InvalidOperationException($"Não foi possível abrir {path}");
        switch (value.Kind)
        {
            case RegistryKind.DWord: k.SetValue(name, unchecked((int)uint.Parse(value.Data, CultureInfo.InvariantCulture)), RegistryValueKind.DWord); break;
            case RegistryKind.QWord: k.SetValue(name, unchecked((long)ulong.Parse(value.Data, CultureInfo.InvariantCulture)), RegistryValueKind.QWord); break;
            case RegistryKind.ExpandString: k.SetValue(name, value.Data, RegistryValueKind.ExpandString); break;
            case RegistryKind.MultiString: k.SetValue(name, value.Data.Split('\n'), RegistryValueKind.MultiString); break;
            case RegistryKind.Binary: k.SetValue(name, Convert.FromHexString(value.Data), RegistryValueKind.Binary); break;
            default: k.SetValue(name, value.Data, RegistryValueKind.String); break;
        }
    }

    public void Delete(RegistryHive hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, true);
        k?.DeleteValue(name, false);
    }

    public IReadOnlyList<string> SubKeys(RegistryHive hive, string path)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, false);
        return k?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<string> ValueNames(RegistryHive hive, string path)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, false);
        return k?.GetValueNames() ?? [];
    }
}

/// <summary>Serviços via registro (leitura) + sc.exe (escrita, usa ChangeServiceConfig internamente).</summary>
public sealed partial class WindowsServiceControl(IRegistryStore registry) : IServiceControl
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    [GeneratedRegex(@"STATE\s*:\s*(\d+)")] private static partial Regex StateRegex();

    public bool Exists(string name) => registry.Get(RegistryHive.LocalMachine, ServicesKey + name, "Start") is not null;

    public ServiceStart? GetStart(string name)
    {
        var start = registry.Get(RegistryHive.LocalMachine, ServicesKey + name, "Start");
        if (start is null) return null;
        var s = (ServiceStart)int.Parse(start.Data, CultureInfo.InvariantCulture);
        if (s == ServiceStart.Automatic && registry.Get(RegistryHive.LocalMachine, ServicesKey + name, "DelayedAutostart") is { Data: "1" })
            return ServiceStart.AutomaticDelayed;
        return s;
    }

    public void SetStart(string name, ServiceStart start)
    {
        var mode = start switch
        {
            ServiceStart.Automatic => "auto",
            ServiceStart.AutomaticDelayed => "delayed-auto",
            ServiceStart.Manual => "demand",
            ServiceStart.Disabled => "disabled",
            ServiceStart.Boot => "boot",
            ServiceStart.System => "system",
            _ => throw new ArgumentOutOfRangeException(nameof(start)),
        };
        var r = ProcessRunner.Run(ProcessRunner.System32("sc.exe"), $"config \"{name}\" start= {mode}");
        if (r.ExitCode != 0) throw new InvalidOperationException($"sc config {name} falhou ({r.ExitCode}): {r.StdOut.Trim()} {r.StdErr.Trim()}");
    }

    public bool IsRunning(string name)
    {
        var r = ProcessRunner.Run(ProcessRunner.System32("sc.exe"), $"query \"{name}\"");
        var m = StateRegex().Match(r.StdOut);
        // 2 = START_PENDING, 4 = RUNNING
        return m.Success && m.Groups[1].Value is "2" or "4";
    }

    public void Stop(string name) => ProcessRunner.Run(ProcessRunner.System32("sc.exe"), $"stop \"{name}\"");
    public void Start(string name) => ProcessRunner.Run(ProcessRunner.System32("sc.exe"), $"start \"{name}\"");
}

public sealed partial class WindowsPowerApi : IPowerApi
{
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")] private static partial Regex GuidRegex();

    private static void Check(uint rc, string op)
    {
        if (rc != 0) throw new InvalidOperationException($"{op} falhou (Win32 {rc}).");
    }

    public Guid GetActiveScheme()
    {
        Check(NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var ptr), "PowerGetActiveScheme");
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { NativeMethods.LocalFree(ptr); }
    }

    public void SetActiveScheme(Guid scheme) => Check(NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme), "PowerSetActiveScheme");

    public IReadOnlyList<Guid> ListSchemes()
    {
        var list = new List<Guid>();
        for (uint i = 0; ; i++)
        {
            uint size = 16;
            var buf = new byte[16];
            var rc = NativeMethods.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.ACCESS_SCHEME, i, buf, ref size);
            if (rc == NativeMethods.ERROR_NO_MORE_ITEMS) break;
            Check(rc, "PowerEnumerate");
            list.Add(new Guid(buf));
        }
        return list;
    }

    public string GetSchemeName(Guid scheme)
    {
        uint size = 0;
        NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return scheme.ToString();
        var buf = new byte[size];
        if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return scheme.ToString();
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    public Guid DuplicateScheme(Guid source)
    {
        // powercfg sabe instanciar "personalidades" ocultas (ex.: Ultimate) que PowerDuplicateScheme não enxerga.
        var r = ProcessRunner.Run(ProcessRunner.System32("powercfg.exe"), $"-duplicatescheme {source}");
        var match = GuidRegex().Matches(r.StdOut).Select(m => Guid.Parse(m.Value)).FirstOrDefault(g => g != source);
        if (r.ExitCode != 0 || match == Guid.Empty)
            throw new SkipException($"Este Windows não permite criar o plano {source} ({r.StdOut.Trim()} {r.StdErr.Trim()}).");
        return match;
    }

    public uint? ReadAcValue(Guid scheme, Guid subGroup, Guid setting)
    {
        var rc = NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out var v);
        return rc == 0 ? v : null;
    }

    public void WriteAcValue(Guid scheme, Guid subGroup, Guid setting, uint value) =>
        Check(NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, value), "PowerWriteACValueIndex");

    public Guid? GetOverlay()
    {
        try { return NativeMethods.PowerGetActualOverlayScheme(out var g) == 0 ? g : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public void SetOverlay(Guid overlay)
    {
        uint rc;
        try { rc = NativeMethods.PowerSetActiveOverlayScheme(overlay); }
        catch (EntryPointNotFoundException) { throw new SkipException("Este Windows não tem o controle 'Modo de energia'."); }
        if (rc != 0) throw new SkipException($"O Windows recusou o Modo de energia (código {rc}). Ele só existe com o plano Equilibrado ativo.");
    }

    public PowerCapabilities GetCapabilities()
    {
        var buf = new byte[76]; // sizeof(SYSTEM_POWER_CAPABILITIES)
        if (NativeMethods.CallNtPowerInformation(NativeMethods.SystemPowerCapabilities, IntPtr.Zero, 0, buf, (uint)buf.Length) != 0)
            return new(false, false, false);
        // Offsets: LidPresent=2, AoAc=20, SystemBatteriesPresent=30
        return new(buf[2] != 0, buf[20] != 0, buf[30] != 0);
    }

    public bool? IsOnAcPower() =>
        NativeMethods.GetSystemPowerStatus(out var s) ? s.ACLineStatus switch { 1 => true, 0 => false, _ => null } : null;
}

public static class Elevation
{
    public static bool IsAdministrator()
    {
        try { return OperatingSystem.IsWindows() && NativeMethods.IsUserAnAdmin(); }
        catch { return false; }
    }
}
