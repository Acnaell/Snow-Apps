using System.Diagnostics;
using System.Reflection;
using Snow.Core.Tweaks;
using Snow.Platform;
using Snow.Platform.Executors;
using Snow.Platform.Services;

namespace Snow.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public static class Assert
{
    public static void True(bool cond, string msg = "esperava verdadeiro") { if (!cond) throw new Exception(msg); }
    public static void False(bool cond, string msg = "esperava falso") { if (cond) throw new Exception(msg); }
    public static void Equal<T>(T expected, T actual, string? ctx = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{ctx}{(ctx is null ? "" : ": ")}esperado <{expected}>, obtido <{actual}>");
    }
    public static T Single<T>(IEnumerable<T> items, string ctx)
    {
        var l = items.ToList();
        if (l.Count != 1) throw new Exception($"{ctx}: esperava 1 item, obtido {l.Count} ({string.Join(", ", l)})");
        return l[0];
    }
    public static void Throws<TEx>(Action a) where TEx : Exception
    {
        try { a(); } catch (TEx) { return; }
        throw new Exception($"esperava {typeof(TEx).Name}");
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => filter is null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name).ToList();

        int pass = 0, fail = 0;
        var sw = Stopwatch.StartNew();
        foreach (var t in tests)
        {
            var name = $"{t.DeclaringType!.Name}.{t.Name}";
            try { t.Invoke(null, null); pass++; Console.WriteLine($"  ✔ {name}"); }
            catch (TargetInvocationException e) { fail++; Console.WriteLine($"  ✖ {name}\n      {e.InnerException?.Message}\n{e.InnerException?.StackTrace?.Split('\n').FirstOrDefault()}"); }
        }
        Console.WriteLine($"\n{pass} passaram, {fail} falharam ({sw.ElapsedMilliseconds} ms).");
        return fail == 0 ? 0 : 1;
    }
}

// ───────────────────────────── Fakes ─────────────────────────────

public sealed class FakeRegistry : IRegistryStore
{
    public readonly Dictionary<string, RegValue> Values = new(StringComparer.OrdinalIgnoreCase);
    private static string K(RegistryHive h, string p, string n) => $"{h}\\{p}\\\u0001{n}";

    public RegValue? Get(RegistryHive hive, string path, string name) => Values.GetValueOrDefault(K(hive, path, name));
    public void Set(RegistryHive hive, string path, string name, RegValue value) => Values[K(hive, path, name)] = value;
    public void Delete(RegistryHive hive, string path, string name) => Values.Remove(K(hive, path, name));

    public IReadOnlyList<string> SubKeys(RegistryHive hive, string path)
    {
        var prefix = $"{hive}\\{path}\\";
        return Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..]).Where(r => !r.StartsWith('\u0001'))
            .Select(r => r.Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> ValueNames(RegistryHive hive, string path)
    {
        var prefix = $"{hive}\\{path}\\\u0001";
        return Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(k => k[prefix.Length..]).ToList();
    }

    public string Dump() => string.Join("\n", Values.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value.Serialize()}"));
}

public sealed class FakeServices : IServiceControl
{
    public readonly Dictionary<string, (ServiceStart start, bool running)> S = new(StringComparer.OrdinalIgnoreCase);
    public bool Exists(string n) => S.ContainsKey(n);
    public ServiceStart? GetStart(string n) => S.TryGetValue(n, out var v) ? v.start : null;
    public void SetStart(string n, ServiceStart s)
    {
        if (!Enum.IsDefined(s)) throw new InvalidOperationException($"sc config: valor inválido {(int)s}");
        S[n] = (s, S[n].running);
    }
    public bool IsRunning(string n) => S.TryGetValue(n, out var v) && v.running;
    public void Stop(string n) => S[n] = (S[n].start, false);
    public void Start(string n) => S[n] = (S[n].start, true);
}

public sealed class FakePower : IPowerApi
{
    public Guid Active = PowerGuids.Balanced;
    public readonly List<Guid> Schemes = [PowerGuids.Balanced, PowerGuids.PowerSaver];
    public readonly Dictionary<(Guid, Guid), uint> Ac = new();
    public Guid? Overlay = Guid.Empty;
    public bool AllowUltimate = true;
    public int Duplications;

    public Guid GetActiveScheme() => Active;
    public void SetActiveScheme(Guid s) { if (!Schemes.Contains(s)) throw new InvalidOperationException("esquema inexistente"); Active = s; }
    public IReadOnlyList<Guid> ListSchemes() => Schemes;
    public string GetSchemeName(Guid s) => s.ToString()[..8];
    public Guid DuplicateScheme(Guid src)
    {
        if (src == PowerGuids.UltimateTemplate && !AllowUltimate) throw new SkipException("não permitido");
        var g = Guid.NewGuid(); Schemes.Add(g); Duplications++;
        foreach (var kv in Ac.Where(k => k.Key.Item1 == Active).ToList()) Ac[(g, kv.Key.Item2)] = kv.Value;
        return g;
    }
    public uint? ReadAcValue(Guid scheme, Guid sub, Guid setting) => Ac.TryGetValue((scheme, setting), out var v) ? v : null;
    public void WriteAcValue(Guid scheme, Guid sub, Guid setting, uint value) => Ac[(scheme, setting)] = value;
    public Guid? GetOverlay() => Overlay;
    public void SetOverlay(Guid o) { if (Active != PowerGuids.Balanced) throw new SkipException("só no Equilibrado"); Overlay = o; }
    public PowerCapabilities GetCapabilities() => new(false, false, false);
    public bool? IsOnAcPower() => true;
}

public sealed class MemState : IStateStore
{
    private readonly Dictionary<string, string> _d = new();
    public string? Get(string k) => _d.GetValueOrDefault(k);
    public void Set(string k, string v) => _d[k] = v;
}

public sealed class ListLog : ILog
{
    public readonly List<string> Lines = [];
    public event Action<string>? Written;
    public void Info(string m) { Lines.Add(m); Written?.Invoke(m); }
    public void Error(string m) => Lines.Add("ERR " + m);
}
