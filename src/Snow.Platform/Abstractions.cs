using System.Globalization;
using System.Runtime.Versioning;
using Snow.Core.Tweaks;

[assembly: SupportedOSPlatform("windows")]

namespace Snow.Platform;

/// <summary>Valor de registro serializável (Data é texto invariável; Binary em hex; MultiString separado por \n).</summary>
public sealed record RegValue(RegistryKind Kind, string Data)
{
    public static RegValue From(RegistryKind kind, object value) => kind switch
    {
        RegistryKind.DWord => new(kind, value switch
        {
            int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
            uint u => u.ToString(CultureInfo.InvariantCulture),
            long l => unchecked((uint)l).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToUInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        }),
        RegistryKind.QWord => new(kind, Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
        RegistryKind.Binary => new(kind, value is byte[] b ? Convert.ToHexString(b) : value.ToString()!),
        _ => new(kind, value.ToString() ?? ""),
    };

    public string Serialize() => $"{Kind}:{Data}";

    public static RegValue? Deserialize(string? s)
    {
        if (s is null) return null;
        var idx = s.IndexOf(':');
        return new(Enum.Parse<RegistryKind>(s[..idx]), s[(idx + 1)..]);
    }

    public override string ToString() => Kind switch
    {
        RegistryKind.DWord => $"{Data} (0x{uint.Parse(Data, CultureInfo.InvariantCulture):X})",
        RegistryKind.Binary => $"hex:{Data}",
        _ => $"\"{Data}\"",
    };
}

public interface IRegistryStore
{
    RegValue? Get(RegistryHive hive, string path, string name);
    void Set(RegistryHive hive, string path, string name, RegValue value);
    void Delete(RegistryHive hive, string path, string name);
    IReadOnlyList<string> SubKeys(RegistryHive hive, string path);
    IReadOnlyList<string> ValueNames(RegistryHive hive, string path);
}

public interface IServiceControl
{
    bool Exists(string name);
    ServiceStart? GetStart(string name);
    void SetStart(string name, ServiceStart start);
    bool IsRunning(string name);
    void Stop(string name);
    void Start(string name);
}

public sealed record PowerCapabilities(bool LidPresent, bool ModernStandby, bool BatteriesPresent);

public interface IPowerApi
{
    Guid GetActiveScheme();
    void SetActiveScheme(Guid scheme);
    IReadOnlyList<Guid> ListSchemes();
    string GetSchemeName(Guid scheme);
    /// <summary>Duplica um esquema (inclusive modelos ocultos como o Ultimate) e retorna o novo GUID.</summary>
    Guid DuplicateScheme(Guid source);
    uint? ReadAcValue(Guid scheme, Guid subGroup, Guid setting);
    void WriteAcValue(Guid scheme, Guid subGroup, Guid setting, uint value);
    /// <summary>Overlay "Modo de energia"; null se a API não existir neste Windows.</summary>
    Guid? GetOverlay();
    void SetOverlay(Guid overlay);
    PowerCapabilities GetCapabilities();
    bool? IsOnAcPower();
}

/// <summary>Sinaliza que uma ação não se aplica a esta máquina (não é erro).</summary>
public sealed class SkipException(string reason) : Exception(reason);

public static class PowerGuids
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static Guid Overlay(PowerOverlay o) => o switch
    {
        PowerOverlay.BetterBattery => new("961cc777-2547-4f9d-8174-7d86181b8a7a"),
        PowerOverlay.BetterPerformance => new("3af9b8d9-7c97-431d-ad78-34a8bfea439f"),
        PowerOverlay.BestPerformance => new("ded574b5-45a0-4f42-8737-46345c09c238"),
        _ => Guid.Empty,
    };
}

/// <summary>Local de dados do app (backups, logs, estado). Sobrescrevível por SNOW_DATA_DIR (testes/CI).</summary>
public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("SNOW_DATA_DIR");
            var dir = !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Snow");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string BackupsDir => Ensure(Path.Combine(DataDir, "backups"));
    public static string LogsDir => Ensure(Path.Combine(DataDir, "logs"));
    public static string StateFile => Path.Combine(DataDir, "state.json");

    private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}
