using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Snow.Core.Updates;
using Snow.Platform.Services;

namespace Snow.Platform.Updates;

public sealed record UpdateCheck(Version Current, ReleaseInfo? Latest, ReleaseAsset? Asset, string? Error)
{
    public bool Available => Latest is not null && Asset is not null && UpdateLogic.IsNewer(Latest.Version, Current);
}

/// <summary>
/// Atualizador: consulta a última release do GitHub, baixa o pacote da arquitetura certa, confere o SHA-256
/// e troca os arquivos com segurança (renomeia os atuais para .old — permitido mesmo com o programa aberto —,
/// coloca os novos e desfaz tudo se algo falhar). Nunca instala nada sem o usuário pedir.
/// </summary>
public sealed class UpdateService(ILog log, string? appDir = null, HttpMessageHandler? handler = null)
{
    public const string Repo = "Acnaell/Snow-Apps";
    private static readonly string[] RequiredFiles = ["Snow.exe", "snow-cli.exe"];

    private readonly string _appDir = appDir ?? AppContext.BaseDirectory;

    /// <summary>
    /// Origem da release. Normalmente a API do GitHub; SNOW_UPDATE_FEED pode apontar para um arquivo JSON
    /// local no mesmo formato (usado nos testes de ponta a ponta do CI).
    /// </summary>
    private static string Feed => Environment.GetEnvironmentVariable("SNOW_UPDATE_FEED") is { Length: > 0 } f
        ? f : $"https://api.github.com/repos/{Repo}/releases/latest";

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string Rid => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64",
    };

    private HttpClient Http()
    {
        var c = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        c.Timeout = TimeSpan.FromMinutes(5);
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SnowApps", CurrentVersion.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    private async Task<string> ReadText(string source, CancellationToken ct)
    {
        if (File.Exists(source)) return await File.ReadAllTextAsync(source, ct);
        using var http = Http();
        return await http.GetStringAsync(source, ct);
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        try
        {
            var release = UpdateLogic.ParseRelease(await ReadText(Feed, ct));
            return new UpdateCheck(current, release, release is null ? null : UpdateLogic.PickAsset(release.Assets, Rid), null);
        }
        catch (Exception e)
        {
            log.Info($"Atualizações: não foi possível verificar ({e.Message}).");
            return new UpdateCheck(current, null, null, e.Message);
        }
    }

    /// <summary>Baixa, confere e instala. Retorna o caminho do novo Snow.exe.</summary>
    public async Task<string> InstallAsync(UpdateCheck check, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!check.Available || check.Latest is null || check.Asset is null) throw new InvalidOperationException("Nenhuma atualização disponível.");
        var asset = check.Asset;
        var work = Path.Combine(Path.GetTempPath(), "SnowUpdate", check.Latest.Version.ToString(3));
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, asset.Name);

        // 1) download
        progress?.Report($"Baixando {asset.Name}…");
        if (File.Exists(asset.Url)) File.Copy(asset.Url, zip);
        else
        {
            using var http = Http();
            await using var src = await http.GetStreamAsync(asset.Url, ct);
            await using var dst = File.Create(zip);
            await src.CopyToAsync(dst, ct);
        }

        // 2) integridade: digest da API do GitHub e/ou SHA256SUMS.txt da release (pelo menos um é obrigatório)
        progress?.Report("Conferindo a integridade (SHA-256)…");
        var expected = asset.Sha256;
        if (expected is null && check.Latest.Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) is { } sums)
            expected = UpdateLogic.ParseSums(await ReadText(sums.Url, ct)).GetValueOrDefault(asset.Name);
        if (expected is null) throw new InvalidOperationException("A release não informa o SHA-256 do pacote; atualização cancelada por segurança.");
        string actual;
        await using (var fs = File.OpenRead(zip)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SHA-256 não confere (esperado {expected[..12]}…, obtido {actual[..12]}…). Atualização cancelada.");

        // 3) extrai e valida o conteúdo
        progress?.Report("Preparando os arquivos…");
        var staging = Path.Combine(work, "novo");
        ZipFile.ExtractToDirectory(zip, staging);
        foreach (var f in RequiredFiles)
            if (!File.Exists(Path.Combine(staging, f))) throw new InvalidOperationException($"Pacote incompleto: falta {f}.");

        // 4) troca segura
        progress?.Report("Instalando…");
        Swap(staging, _appDir);
        log.Info($"Atualizado de {check.Current.ToString(3)} para {check.Latest.Version.ToString(3)}.");
        return Path.Combine(_appDir, "Snow.exe");
    }

    internal static void Swap(string staging, string appDir)
    {
        var moved = new List<(string target, string? backup)>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, src);
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = target + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(target, backup); // renomear funciona mesmo com o .exe em execução
                }
                moved.Add((target, backup));
                File.Copy(src, target);
            }
        }
        catch
        {
            // desfaz: volta os arquivos antigos
            foreach (var (target, backup) in Enumerable.Reverse(moved))
            {
                try
                {
                    if (File.Exists(target) && backup is not null) File.Delete(target);
                    if (backup is not null && File.Exists(backup)) File.Move(backup, target);
                }
                catch { /* melhor esforço */ }
            }
            throw;
        }
    }

    /// <summary>Remove sobras (.old) de uma atualização anterior. Chamado ao abrir o app.</summary>
    public void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_appDir, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* ainda em uso: fica para a próxima */ }
        }
        catch { }
    }

    /// <summary>Abre a versão nova (já elevada, porque o processo atual é administrador).</summary>
    public static void Relaunch(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }
}
