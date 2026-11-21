using System.IO.Compression;
using System.Security.Cryptography;
using Snow.Core.Updates;
using Snow.Platform.Updates;

namespace Snow.Tests;

public static class UpdateTests
{
    private const string GithubJson = """
    {"html_url":"https://github.com/Acnaell/Snow-Apps/releases/tag/v0.5.0","tag_name":"v0.5.0","draft":false,"prerelease":false,"body":"## Snow","assets":[
      {"name":"Snow-win-arm64.zip","size":91000000,"digest":"sha256:aaaa","browser_download_url":"https://github.com/Acnaell/Snow-Apps/releases/download/v0.5.0/Snow-win-arm64.zip"},
      {"name":"Snow-win-x64.zip","size":96000000,"digest":"sha256:ABCDEF0123","browser_download_url":"https://github.com/Acnaell/Snow-Apps/releases/download/v0.5.0/Snow-win-x64.zip"},
      {"name":"SHA256SUMS.txt","size":300,"digest":null,"browser_download_url":"https://github.com/Acnaell/Snow-Apps/releases/download/v0.5.0/SHA256SUMS.txt"}]}
    """;

    [Test] public static void ParsesGithubReleaseAndPicksArch()
    {
        var r = UpdateLogic.ParseRelease(GithubJson)!;
        Assert.Equal(new Version(0, 5, 0), r.Version);
        var x64 = UpdateLogic.PickAsset(r.Assets, "win-x64")!;
        Assert.Equal("abcdef0123", x64.Sha256, "digest normalizado em minúsculas");
        Assert.True(UpdateLogic.PickAsset(r.Assets, "win-x86") is null, "sem pacote x86 nesta release");
        Assert.True(UpdateLogic.ParseRelease(GithubJson.Replace("\"prerelease\":false", "\"prerelease\":true")) is null, "pré-release nunca é oferecida");
    }

    [Test] public static void VersionComparison()
    {
        Assert.True(UpdateLogic.IsNewer(new Version(0, 4, 2), new Version(0, 4, 1, 0)));
        Assert.False(UpdateLogic.IsNewer(new Version(0, 4, 1), new Version(0, 4, 1, 0)), "mesma versão (revisão ignorada)");
        Assert.False(UpdateLogic.IsNewer(new Version(0, 3, 9), new Version(0, 4, 0)));
        Assert.Equal(new Version(1, 2, 0), UpdateLogic.ParseTag("v1.2"));
        var sums = UpdateLogic.ParseSums("ABC123ABC123ABC123ABC123ABC123ABC123ABC123ABC123ABC123ABC123ABCD  Snow-win-x64.zip\r\n");
        Assert.Equal("abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd", sums["Snow-win-x64.zip"]);
    }

    private static (string feed, string appDir, string root) MakeRelease(string version, bool corruptHash = false, bool missingCli = false)
    {
        var root = Directory.CreateTempSubdirectory("snowupd").FullName;
        var pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(pkg, "Snow.exe"), "NOVO-GUI " + version);
        if (!missingCli) File.WriteAllText(Path.Combine(pkg, "snow-cli.exe"), "NOVO-CLI " + version);
        File.WriteAllText(Path.Combine(pkg, "README.md"), "novo readme");
        var zip = Path.Combine(root, $"Snow-{UpdateService.Rid}.zip");
        ZipFile.CreateFromDirectory(pkg, zip);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
        if (corruptHash) sha = new string('0', 64);
        var sums = Path.Combine(root, "SHA256SUMS.txt");
        File.WriteAllText(sums, $"{sha}  Snow-{UpdateService.Rid}.zip\n");
        var feed = Path.Combine(root, "release.json");
        File.WriteAllText(feed, $$"""
        {"tag_name":"v{{version}}","html_url":"x","body":"","assets":[
          {"name":"Snow-{{UpdateService.Rid}}.zip","size":1,"browser_download_url":{{System.Text.Json.JsonSerializer.Serialize(zip)}}},
          {"name":"SHA256SUMS.txt","size":1,"browser_download_url":{{System.Text.Json.JsonSerializer.Serialize(sums)}}}]}
        """);
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Snow.exe"), "ANTIGO-GUI");
        File.WriteAllText(Path.Combine(app, "snow-cli.exe"), "ANTIGO-CLI");
        return (feed, app, root);
    }

    private static T WithFeed<T>(string feed, Func<T> f)
    {
        var old = Environment.GetEnvironmentVariable("SNOW_UPDATE_FEED");
        Environment.SetEnvironmentVariable("SNOW_UPDATE_FEED", feed);
        try { return f(); }
        finally { Environment.SetEnvironmentVariable("SNOW_UPDATE_FEED", old); }
    }

    [Test] public static void InstallsVerifiedPackageAndKeepsBackup()
    {
        var (feed, app, root) = MakeRelease("99.0.0");
        var svc = new UpdateService(new ListLog(), app);
        var check = WithFeed(feed, () => svc.CheckAsync().GetAwaiter().GetResult());
        Assert.True(check.Available, check.Error ?? "deveria haver atualização");
        var exe = WithFeed(feed, () => svc.InstallAsync(check).GetAwaiter().GetResult());
        Assert.Equal("NOVO-GUI 99.0.0", File.ReadAllText(exe));
        Assert.Equal("NOVO-CLI 99.0.0", File.ReadAllText(Path.Combine(app, "snow-cli.exe")));
        Assert.Equal("ANTIGO-GUI", File.ReadAllText(Path.Combine(app, "Snow.exe.old")), "versão anterior guardada como .old");
        svc.CleanupOldFiles();
        Assert.False(File.Exists(Path.Combine(app, "Snow.exe.old")), ".old removido na próxima abertura");
        Directory.Delete(root, true);
    }

    [Test] public static void RejectsWrongHashAndIncompletePackage()
    {
        foreach (var (hash, missing) in new[] { (true, false), (false, true) })
        {
            var (feed, app, root) = MakeRelease("99.0.0", corruptHash: hash, missingCli: missing);
            var svc = new UpdateService(new ListLog(), app);
            var check = WithFeed(feed, () => svc.CheckAsync().GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(() => WithFeed(feed, () => svc.InstallAsync(check).GetAwaiter().GetResult()));
            Assert.Equal("ANTIGO-GUI", File.ReadAllText(Path.Combine(app, "Snow.exe")), "nada foi trocado");
            Assert.False(File.Exists(Path.Combine(app, "Snow.exe.old")));
            Directory.Delete(root, true);
        }
    }

    [Test] public static void SameVersionIsNotOffered()
    {
        var (feed, app, root) = MakeRelease(UpdateService.CurrentVersion.ToString(3));
        var check = WithFeed(feed, () => new UpdateService(new ListLog(), app).CheckAsync().GetAwaiter().GetResult());
        Assert.False(check.Available);
        Directory.Delete(root, true);
    }
}
