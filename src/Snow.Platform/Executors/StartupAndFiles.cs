using System.Text.Json;
using Snow.Core.Tweaks;
using Snow.Platform.Native;

namespace Snow.Platform.Executors;

public sealed record StartupItem(string Key, string Name, string Command, string Location, bool Enabled,
    RegistryHive ApprovedHive, string ApprovedPath);

/// <summary>
/// Lista e (des)ativa itens de inicialização pelo mesmo mecanismo do Gerenciador de Tarefas
/// (chaves StartupApproved): nada é apagado, a entrada Run original fica intacta.
/// </summary>
public sealed class StartupManager(IRegistryStore reg, Func<IEnumerable<(string folder, bool common)>>? folders = null)
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static readonly (RegistryHive hive, string runPath, string approved, string label)[] RunKeys =
    [
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registro (usuário)"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "Registro (todos os usuários)"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Registro 32 bits (todos)"),
    ];

    private IEnumerable<(string folder, bool common)> Folders() => folders?.Invoke() ??
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.Startup), false),
        (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true),
    ];

    public IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        foreach (var (hive, runPath, approved, label) in RunKeys)
        {
            foreach (var name in reg.ValueNames(hive, runPath))
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = reg.Get(hive, runPath, name)?.Data ?? "";
                var ap = Approved + approved;
                items.Add(new($"{hive}|{ap}|{name}", name, cmd, label, IsEnabled(reg.Get(hive, ap, name)), hive, ap));
            }
        }
        foreach (var (folder, common) in Folders())
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            var hive = common ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var ap = Approved + "StartupFolder";
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fn = Path.GetFileName(file);
                if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new($"{hive}|{ap}|{fn}", Path.GetFileNameWithoutExtension(fn), file,
                    common ? "Pasta Inicializar (todos)" : "Pasta Inicializar", IsEnabled(reg.Get(hive, ap, fn)), hive, ap));
            }
        }
        return items;
    }

    /// <summary>Ausente = ativo. Primeiro byte ímpar (0x03, 0x07…) = desativado.</summary>
    public static bool IsEnabled(RegValue? approved)
    {
        if (approved is not { Kind: RegistryKind.Binary } || approved.Data.Length < 2) return true;
        return (Convert.ToByte(approved.Data[..2], 16) & 1) == 0;
    }

    public RegValue? GetApprovedRaw(StartupItem item) => reg.Get(item.ApprovedHive, item.ApprovedPath, ApprovedName(item.Key));

    public void SetEnabled(StartupItem item, bool enabled)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        reg.Set(item.ApprovedHive, item.ApprovedPath, ApprovedName(item.Key), new RegValue(RegistryKind.Binary, Convert.ToHexString(bytes)));
    }

    public void RestoreApprovedRaw(string key, RegValue? raw)
    {
        var parts = key.Split('|', 3);
        var hive = Enum.Parse<RegistryHive>(parts[0]);
        if (raw is null) reg.Delete(hive, parts[1], parts[2]);
        else reg.Set(hive, parts[1], parts[2], raw);
    }

    private static string ApprovedName(string key) => key.Split('|', 3)[2];
}

// ───────────────────────────── Indexação ─────────────────────────────
public sealed class IndexingExecutor(Func<IReadOnlyList<string>>? libraries = null) : IActionExecutor
{
    public string Name => "indexing";
    public bool CanHandle(TweakAction a) => a is IndexingExclusionAction;

    private IReadOnlyList<string> Libraries() =>
        (libraries?.Invoke() ?? GameLibraryFinder.Find()).Where(Directory.Exists).ToList();

    public ActionSnapshot Capture(TweakAction action)
    {
        var data = new Dictionary<string, string?>();
        foreach (var lib in Libraries())
            data[lib] = File.GetAttributes(lib).HasFlag(FileAttributes.NotContentIndexed) ? "1" : "0";
        return new(Name, action.Describe(), data);
    }

    public ActionOutcome Apply(TweakAction action)
    {
        var libs = Libraries();
        if (libs.Count == 0) throw new SkipException("Nenhuma biblioteca de jogos encontrada.");
        long n = 0;
        foreach (var lib in libs) n += SetRecursive(lib, true);
        return new(false, $"{n:N0} itens marcados em {libs.Count} biblioteca(s)");
    }

    public bool? Verify(TweakAction action)
    {
        var libs = Libraries();
        return libs.Count == 0 ? null : libs.All(l => File.GetAttributes(l).HasFlag(FileAttributes.NotContentIndexed));
    }

    public void Revert(ActionSnapshot s)
    {
        foreach (var (lib, was) in s.Data)
            if (was == "0" && Directory.Exists(lib)) SetRecursive(lib, false);
    }

    internal static long SetRecursive(string root, bool on)
    {
        long count = 0;
        void Touch(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                var next = on ? attr | FileAttributes.NotContentIndexed : attr & ~FileAttributes.NotContentIndexed;
                if (next != attr) { File.SetAttributes(path, next); count++; }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Touch(root);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var p in Directory.EnumerateFileSystemEntries(root, "*", opts)) Touch(p);
        return count;
    }
}

/// <summary>Detecta bibliotecas Steam (libraryfolders.vdf) e instalações da Epic (manifests).</summary>
public static class GameLibraryFinder
{
    public static IReadOnlyList<string> Find(IRegistryStore? reg = null)
    {
        reg ??= new Services.WindowsRegistryStore();
        var result = new List<string>();
        try
        {
            var steam = reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")?.Data
                     ?? reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")?.Data;
            if (!string.IsNullOrEmpty(steam))
            {
                steam = steam.Replace('/', '\\');
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                var roots = new List<string> { steam };
                if (File.Exists(vdf)) roots.AddRange(ParseVdfPaths(File.ReadAllText(vdf)));
                result.AddRange(roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => Path.Combine(r, "steamapps", "common")));
            }
        }
        catch { /* Steam ausente ou ilegível */ }

        try
        {
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(manifests))
            {
                foreach (var f in Directory.EnumerateFiles(manifests, "*.item"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 3 } l)
                        result.Add(l);
                }
            }
        }
        catch { /* Epic ausente ou manifest inválido */ }

        return result.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Extrai os valores "path" do libraryfolders.vdf (formato KeyValues da Valve).</summary>
    public static IEnumerable<string> ParseVdfPaths(string vdf)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(vdf, "\"path\"\\s*\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }
}

// ───────────────────────────── Limpeza ─────────────────────────────
public sealed class CleanupExecutor : IActionExecutor
{
    public string Name => "cleanup";
    public bool CanHandle(TweakAction a) => a is CleanupAction;

    public ActionSnapshot Capture(TweakAction action) => new(Name, action.Describe(), new());
    public bool? Verify(TweakAction action) => null;
    public void Revert(ActionSnapshot snapshot) { /* irreversível por definição */ }

    public ActionOutcome Apply(TweakAction action)
    {
        var a = (CleanupAction)action;
        if (a.Target == CleanupTarget.StandbyList)
        {
            PurgeStandbyList();
            return new(false, "Lista Standby esvaziada");
        }
        var (files, bytes) = (0L, 0L);
        foreach (var dir in Targets(a.Target).Where(Directory.Exists))
        {
            var (f, b) = DeleteOlderThan(dir, TimeSpan.FromDays(a.MinAgeDays));
            files += f; bytes += b;
        }
        return new(false, $"{files:N0} arquivo(s), {bytes / 1024d / 1024:N1} MB liberados");
    }

    public static IEnumerable<string> Targets(CleanupTarget t)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return t switch
        {
            CleanupTarget.UserTemp => [Path.GetTempPath()],
            CleanupTarget.WindowsTemp => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")],
            CleanupTarget.ShaderCaches =>
            [
                Path.Combine(local, "D3DSCache"),
                Path.Combine(local, @"NVIDIA\DXCache"), Path.Combine(local, @"NVIDIA\GLCache"),
                Path.Combine(localLow, @"NVIDIA\PerDriverVersion\DXCache"), Path.Combine(localLow, @"NVIDIA\PerDriverVersion\GLCache"),
                Path.Combine(programData, @"NVIDIA Corporation\NV_Cache"),
                Path.Combine(local, @"AMD\DxCache"), Path.Combine(local, @"AMD\DxcCache"), Path.Combine(local, @"AMD\VkCache"), Path.Combine(local, @"AMD\GLCache"),
                Path.Combine(local, @"Intel\ShaderCache"), Path.Combine(localLow, @"Intel\ShaderCache"),
            ],
            _ => [],
        };
    }

    /// <summary>Apaga arquivos antigos sem seguir junctions/symlinks; arquivos em uso são ignorados.</summary>
    internal static (long files, long bytes) DeleteOlderThan(string root, TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        long files = 0, bytes = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var f in Directory.EnumerateFiles(root, "*", opts))
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.LastWriteTimeUtc > cutoff) continue;
                var len = fi.Length;
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                files++; bytes += len;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // Remove diretórios vazios (de baixo para cima), nunca a raiz.
        foreach (var d in Directory.EnumerateDirectories(root, "*", opts).OrderByDescending(p => p.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return (files, bytes);
    }

    public static void PurgeStandbyList()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out var token))
            throw new InvalidOperationException("OpenProcessToken falhou.");
        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid))
                throw new InvalidOperationException("Privilégio SeProfileSingleProcessPrivilege indisponível.");
            var tp = new NativeMethods.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = NativeMethods.SE_PRIVILEGE_ENABLED };
            NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            var cmd = NativeMethods.MemoryPurgeStandbyList;
            var status = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, ref cmd, sizeof(int));
            if (status != 0) throw new InvalidOperationException($"NtSetSystemInformation retornou 0x{status:X8} (execute como Administrador).");
        }
        finally { NativeMethods.CloseHandle(token); }
    }
}

/// <summary>Estado em JSON simples no diretório de dados.</summary>
public sealed class JsonStateStore(string? file = null) : IStateStore
{
    private readonly string _file = file ?? AppPaths.StateFile;
    private readonly object _lock = new();

    private Dictionary<string, string> Load() =>
        File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new() : new();

    public string? Get(string key) { lock (_lock) return Load().GetValueOrDefault(key); }

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            var d = Load();
            d[key] = value;
            File.WriteAllText(_file, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
