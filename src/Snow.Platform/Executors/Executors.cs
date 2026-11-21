using System.Globalization;
using Snow.Core.Tweaks;

namespace Snow.Platform.Executors;

/// <summary>
/// Estado original capturado antes de aplicar uma ação. Chaves que começam com "~" são
/// informativas e não entram na comparação de ida-e-volta (ex.: serviço rodando ou não).
/// </summary>
public sealed record ActionSnapshot(string Executor, string Description, Dictionary<string, string?> Data)
{
    public IEnumerable<KeyValuePair<string, string?>> ComparableData => Data.Where(kv => !kv.Key.StartsWith('~')).OrderBy(kv => kv.Key, StringComparer.Ordinal);
}

public sealed record ActionOutcome(bool Skipped, string Message);

public interface IActionExecutor
{
    string Name { get; }
    bool CanHandle(TweakAction action);
    ActionSnapshot Capture(TweakAction action);
    ActionOutcome Apply(TweakAction action);
    /// <summary>true = estado atual corresponde ao alvo; null = não verificável (ex.: limpeza).</summary>
    bool? Verify(TweakAction action);
    void Revert(ActionSnapshot snapshot);
}

public sealed class ExecutorSet(IEnumerable<IActionExecutor> executors)
{
    private readonly List<IActionExecutor> _all = executors.ToList();

    public IActionExecutor For(TweakAction action) =>
        _all.FirstOrDefault(e => e.CanHandle(action)) ?? throw new NotSupportedException($"Sem executor para {action.GetType().Name}");

    public IActionExecutor ByName(string name) =>
        _all.FirstOrDefault(e => e.Name == name) ?? throw new NotSupportedException($"Executor desconhecido: {name}");

    public static ExecutorSet CreateDefault(IRegistryStore reg, IServiceControl svc, IPowerApi power, IStateStore state) => new(
    [
        new RegistryExecutor(reg),
        new ServiceExecutor(svc),
        new PowerPlanExecutor(power, state),
        new PowerSettingExecutor(power),
        new PowerOverlayExecutor(power),
        new NagleExecutor(reg),
        new DirectXGlobalExecutor(reg),
        new StartupExecutor(new StartupManager(reg)),
        new IndexingExecutor(),
        new CleanupExecutor(),
    ]);
}

/// <summary>Estado persistente pequeno (ex.: GUID do plano Ultimate criado pelo Snow).</summary>
public interface IStateStore
{
    string? Get(string key);
    void Set(string key, string value);
}

// ───────────────────────────── Registro ─────────────────────────────
public sealed class RegistryExecutor(IRegistryStore reg) : IActionExecutor
{
    public string Name => "registry";
    public bool CanHandle(TweakAction a) => a is RegistryValueAction;

    public ActionSnapshot Capture(TweakAction action)
    {
        var a = (RegistryValueAction)action;
        return new(Name, a.Describe(), new()
        {
            ["hive"] = a.Hive.ToString(),
            ["path"] = a.Path,
            ["name"] = a.Name,
            ["old"] = reg.Get(a.Hive, a.Path, a.Name)?.Serialize(),
        });
    }

    public ActionOutcome Apply(TweakAction action)
    {
        var a = (RegistryValueAction)action;
        if (a.Value is null) reg.Delete(a.Hive, a.Path, a.Name);
        else reg.Set(a.Hive, a.Path, a.Name, RegValue.From(a.Kind, a.Value));
        return new(false, "ok");
    }

    public bool? Verify(TweakAction action)
    {
        var a = (RegistryValueAction)action;
        var cur = reg.Get(a.Hive, a.Path, a.Name);
        return a.Value is null ? cur is null : cur == RegValue.From(a.Kind, a.Value);
    }

    public void Revert(ActionSnapshot s)
    {
        var hive = Enum.Parse<RegistryHive>(s.Data["hive"]!);
        var old = RegValue.Deserialize(s.Data["old"]);
        if (old is null) reg.Delete(hive, s.Data["path"]!, s.Data["name"]!);
        else reg.Set(hive, s.Data["path"]!, s.Data["name"]!, old);
    }
}

// ───────────────────────────── Serviços ─────────────────────────────
public sealed class ServiceExecutor(IServiceControl svc) : IActionExecutor
{
    public string Name => "service";
    public bool CanHandle(TweakAction a) => a is ServiceStartAction;

    public ActionSnapshot Capture(TweakAction action)
    {
        var a = (ServiceStartAction)action;
        var exists = svc.Exists(a.ServiceName);
        return new(Name, a.Describe(), new()
        {
            ["service"] = a.ServiceName,
            ["start"] = exists ? svc.GetStart(a.ServiceName)?.ToString() : null,
            ["~running"] = exists && svc.IsRunning(a.ServiceName) ? "1" : "0",
        });
    }

    public ActionOutcome Apply(TweakAction action)
    {
        var a = (ServiceStartAction)action;
        if (!svc.Exists(a.ServiceName)) throw new SkipException($"Serviço {a.ServiceName} não existe nesta edição do Windows.");
        svc.SetStart(a.ServiceName, a.Start);
        if (a.StopNow && a.Start == ServiceStart.Disabled && svc.IsRunning(a.ServiceName)) svc.Stop(a.ServiceName);
        return new(false, "ok");
    }

    public bool? Verify(TweakAction action)
    {
        var a = (ServiceStartAction)action;
        return !svc.Exists(a.ServiceName) ? null : svc.GetStart(a.ServiceName) == a.Start;
    }

    public void Revert(ActionSnapshot s)
    {
        var name = s.Data["service"]!;
        if (s.Data["start"] is not { } start || !svc.Exists(name)) return;
        svc.SetStart(name, Enum.Parse<ServiceStart>(start));
        if (s.Data["~running"] == "1" && !svc.IsRunning(name)) svc.Start(name);
    }
}

// ───────────────────────────── Energia ─────────────────────────────
public sealed class PowerPlanExecutor(IPowerApi power, IStateStore state) : IActionExecutor
{
    public string Name => "powerplan";
    private const string UltimateKey = "ultimateSchemeGuid";
    private const string HighKey = "highSchemeGuid";

    public bool CanHandle(TweakAction a) => a is PowerPlanAction;

    public ActionSnapshot Capture(TweakAction action) =>
        new(Name, action.Describe(), new() { ["previous"] = power.GetActiveScheme().ToString() });

    public ActionOutcome Apply(TweakAction action)
    {
        var target = Resolve(((PowerPlanAction)action).Plan, create: true)!.Value;
        power.SetActiveScheme(target);
        return new(false, $"Plano ativo: {power.GetSchemeName(target)}");
    }

    public bool? Verify(TweakAction action) =>
        Resolve(((PowerPlanAction)action).Plan, create: false) is { } g && power.GetActiveScheme() == g;

    public void Revert(ActionSnapshot s)
    {
        var prev = Guid.Parse(s.Data["previous"]!);
        if (power.ListSchemes().Contains(prev)) power.SetActiveScheme(prev);
    }

    private Guid? Resolve(PowerPlan plan, bool create)
    {
        var schemes = power.ListSchemes();
        switch (plan)
        {
            case PowerPlan.Balanced:
                return PowerGuids.Balanced;
            case PowerPlan.HighPerformance:
                return FindOrCreate(schemes, PowerGuids.HighPerformance, HighKey, create);
            default:
                return FindOrCreate(schemes, PowerGuids.UltimateTemplate, UltimateKey, create);
        }
    }

    private Guid? FindOrCreate(IReadOnlyList<Guid> schemes, Guid template, string key, bool create)
    {
        if (schemes.Contains(template)) return template;
        if (state.Get(key) is { } saved && Guid.TryParse(saved, out var g) && schemes.Contains(g)) return g;
        if (!create) return null;
        var created = power.DuplicateScheme(template);
        state.Set(key, created.ToString());
        return created;
    }
}

public sealed class PowerSettingExecutor(IPowerApi power) : IActionExecutor
{
    public string Name => "powersetting";
    public bool CanHandle(TweakAction a) => a is PowerSettingAction;

    public ActionSnapshot Capture(TweakAction action)
    {
        var a = (PowerSettingAction)action;
        var scheme = power.GetActiveScheme();
        return new(Name, a.Describe(), new()
        {
            ["scheme"] = scheme.ToString(),
            ["sub"] = a.SubGroup.ToString(),
            ["setting"] = a.Setting.ToString(),
            ["old"] = power.ReadAcValue(scheme, a.SubGroup, a.Setting)?.ToString(CultureInfo.InvariantCulture),
        });
    }

    public ActionOutcome Apply(TweakAction action)
    {
        var a = (PowerSettingAction)action;
        var scheme = power.GetActiveScheme();
        if (power.ReadAcValue(scheme, a.SubGroup, a.Setting) is null)
            throw new SkipException($"'{a.Label}' não existe neste plano/processador.");
        power.WriteAcValue(scheme, a.SubGroup, a.Setting, a.AcValue);
        power.SetActiveScheme(scheme); // reaplica para o valor entrar em vigor
        return new(false, "ok");
    }

    public bool? Verify(TweakAction action)
    {
        var a = (PowerSettingAction)action;
        var v = power.ReadAcValue(power.GetActiveScheme(), a.SubGroup, a.Setting);
        return v is null ? null : v == a.AcValue;
    }

    public void Revert(ActionSnapshot s)
    {
        if (s.Data["old"] is not { } old) return;
        var scheme = Guid.Parse(s.Data["scheme"]!);
        if (!power.ListSchemes().Contains(scheme)) return;
        power.WriteAcValue(scheme, Guid.Parse(s.Data["sub"]!), Guid.Parse(s.Data["setting"]!), uint.Parse(old, CultureInfo.InvariantCulture));
        var active = power.GetActiveScheme();
        if (active == scheme) power.SetActiveScheme(scheme);
    }
}

public sealed class PowerOverlayExecutor(IPowerApi power) : IActionExecutor
{
    public string Name => "poweroverlay";
    public bool CanHandle(TweakAction a) => a is PowerOverlayAction;

    public ActionSnapshot Capture(TweakAction action) =>
        new(Name, action.Describe(), new() { ["previous"] = power.GetOverlay()?.ToString() });

    public ActionOutcome Apply(TweakAction action)
    {
        if (power.GetOverlay() is null) throw new SkipException("Controle 'Modo de energia' indisponível neste Windows.");
        power.SetOverlay(PowerGuids.Overlay(((PowerOverlayAction)action).Overlay));
        return new(false, "ok");
    }

    public bool? Verify(TweakAction action) =>
        power.GetOverlay() is { } g ? g == PowerGuids.Overlay(((PowerOverlayAction)action).Overlay) : null;

    public void Revert(ActionSnapshot s)
    {
        if (s.Data["previous"] is { } prev)
        {
            try { power.SetOverlay(Guid.Parse(prev)); }
            catch (SkipException) { /* plano anterior não é o Equilibrado: o overlay não se aplica */ }
        }
    }
}

// ───────────────────────────── Rede ─────────────────────────────
public sealed class NagleExecutor(IRegistryStore reg) : IActionExecutor
{
    public string Name => "nagle";
    internal const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private static readonly string[] Values = ["TcpAckFrequency", "TCPNoDelay"];

    public bool CanHandle(TweakAction a) => a is NagleAction;

    /// <summary>Interfaces com endereço IPv4 (DHCP ou estático).</summary>
    public IReadOnlyList<string> ActiveInterfaces() =>
        reg.SubKeys(RegistryHive.LocalMachine, InterfacesKey).Where(id =>
        {
            var p = $@"{InterfacesKey}\{id}";
            var dhcp = reg.Get(RegistryHive.LocalMachine, p, "DhcpIPAddress")?.Data;
            var stat = reg.Get(RegistryHive.LocalMachine, p, "IPAddress")?.Data;
            static bool Valid(string? ip) => !string.IsNullOrWhiteSpace(ip) && ip.Split('\n').Any(x => x.Length > 0 && x != "0.0.0.0");
            return Valid(dhcp) || Valid(stat);
        }).ToList();

    public ActionSnapshot Capture(TweakAction action)
    {
        var data = new Dictionary<string, string?>();
        foreach (var id in ActiveInterfaces())
            foreach (var v in Values)
                data[$"{id}|{v}"] = reg.Get(RegistryHive.LocalMachine, $@"{InterfacesKey}\{id}", v)?.Serialize();
        return new(Name, action.Describe(), data);
    }

    public ActionOutcome Apply(TweakAction action)
    {
        var ifaces = ActiveInterfaces();
        if (ifaces.Count == 0) throw new SkipException("Nenhuma interface de rede com IPv4 encontrada.");
        foreach (var id in ifaces)
            foreach (var v in Values)
                reg.Set(RegistryHive.LocalMachine, $@"{InterfacesKey}\{id}", v, new RegValue(RegistryKind.DWord, "1"));
        return new(false, $"{ifaces.Count} interface(s) ajustada(s)");
    }

    public bool? Verify(TweakAction action)
    {
        var ifaces = ActiveInterfaces();
        if (ifaces.Count == 0) return null;
        return ifaces.All(id => Values.All(v => reg.Get(RegistryHive.LocalMachine, $@"{InterfacesKey}\{id}", v) is { Kind: RegistryKind.DWord, Data: "1" }));
    }

    public void Revert(ActionSnapshot s)
    {
        foreach (var (key, old) in s.Data)
        {
            var parts = key.Split('|');
            var path = $@"{InterfacesKey}\{parts[0]}";
            if (RegValue.Deserialize(old) is { } val) reg.Set(RegistryHive.LocalMachine, path, parts[1], val);
            else reg.Delete(RegistryHive.LocalMachine, path, parts[1]);
        }
    }
}

// ───────────────────────────── DirectX (Win11) ─────────────────────────────
public sealed class DirectXGlobalExecutor(IRegistryStore reg) : IActionExecutor
{
    public string Name => "dxglobal";
    internal const string Path = @"Software\Microsoft\DirectX\UserGpuPreferences";
    internal const string ValueName = "DirectXUserGlobalSettings";

    public bool CanHandle(TweakAction a) => a is DirectXGlobalSettingAction;

    public static List<KeyValuePair<string, string>> Parse(string? s) =>
        (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(p => p.Split('=', 2))
                 .Select(kv => new KeyValuePair<string, string>(kv[0], kv.Length > 1 ? kv[1] : ""))
                 .ToList();

    public static string Merge(string? current, string key, string value)
    {
        var items = Parse(current);
        var idx = items.FindIndex(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) items[idx] = new(key, value); else items.Add(new(key, value));
        return string.Concat(items.Select(kv => $"{kv.Key}={kv.Value};"));
    }

    public ActionSnapshot Capture(TweakAction action) =>
        new(Name, action.Describe(), new() { ["old"] = reg.Get(RegistryHive.CurrentUser, Path, ValueName)?.Serialize() });

    public ActionOutcome Apply(TweakAction action)
    {
        var a = (DirectXGlobalSettingAction)action;
        var cur = reg.Get(RegistryHive.CurrentUser, Path, ValueName)?.Data;
        reg.Set(RegistryHive.CurrentUser, Path, ValueName, new RegValue(RegistryKind.String, Merge(cur, a.Key, a.Value)));
        return new(false, "ok");
    }

    public bool? Verify(TweakAction action)
    {
        var a = (DirectXGlobalSettingAction)action;
        return Parse(reg.Get(RegistryHive.CurrentUser, Path, ValueName)?.Data)
            .Any(kv => kv.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase) && kv.Value == a.Value);
    }

    public void Revert(ActionSnapshot s)
    {
        if (RegValue.Deserialize(s.Data["old"]) is { } old) reg.Set(RegistryHive.CurrentUser, Path, ValueName, old);
        else reg.Delete(RegistryHive.CurrentUser, Path, ValueName);
    }
}

// ───────────────────────────── Inicialização ─────────────────────────────
public sealed class StartupExecutor(StartupManager startup) : IActionExecutor
{
    public string Name => "startup";
    public bool CanHandle(TweakAction a) => a is StartupDisableAction;

    private IEnumerable<StartupItem> Matching(StartupDisableAction a) =>
        startup.List().Where(i => a.NamePatterns.Any(p =>
            i.Name.Contains(p, StringComparison.OrdinalIgnoreCase) || i.Command.Contains(p, StringComparison.OrdinalIgnoreCase)));

    public ActionSnapshot Capture(TweakAction action)
    {
        var data = new Dictionary<string, string?>();
        foreach (var i in Matching((StartupDisableAction)action))
            data[i.Key] = startup.GetApprovedRaw(i)?.Serialize();
        return new(Name, action.Describe(), data);
    }

    public ActionOutcome Apply(TweakAction action)
    {
        var items = Matching((StartupDisableAction)action).Where(i => i.Enabled).ToList();
        if (items.Count == 0) throw new SkipException("Nenhum app pesado está configurado para iniciar com o Windows.");
        foreach (var i in items) startup.SetEnabled(i, false);
        return new(false, "Desativados: " + string.Join(", ", items.Select(i => i.Name)));
    }

    public bool? Verify(TweakAction action) => Matching((StartupDisableAction)action).All(i => !i.Enabled);

    public void Revert(ActionSnapshot s)
    {
        foreach (var (key, raw) in s.Data)
            startup.RestoreApprovedRaw(key, RegValue.Deserialize(raw));
    }
}
