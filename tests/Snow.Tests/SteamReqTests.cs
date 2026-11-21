using System.Text.Json;
using Snow.Core.Compat;
using Snow.Core.Hardware;
using Snow.Platform.Compat;

namespace Snow.Tests;

public static class SteamReqTests
{
    // Formato real do campo pc_requirements da API da Steam (HTML, nomes muitas vezes em maiúsculas)
    private const string Details = """
    {"1245620":{"success":true,"data":{"type":"game","name":"ELDEN RING","pc_requirements":{
      "minimum":"<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li>Requires a 64-bit processor and operating system<br></li><li><strong>OS:</strong> Windows 10<br></li><li><strong>Processor:</strong> INTEL CORE I5-8400 or AMD RYZEN 3 3300X<br></li><li><strong>Memory:</strong> 12 GB RAM<br></li><li><strong>Graphics:</strong> NVIDIA GEFORCE GTX 1060 3 GB or AMD RADEON RX 580 4 GB<br></li><li><strong>DirectX:</strong> Version 12<br></li><li><strong>Storage:</strong> 60 GB available space</li></ul>",
      "recommended":"<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li><strong>Processor:</strong> INTEL CORE I7-8700K or AMD RYZEN 5 3600X<br></li><li><strong>Memory:</strong> 16 GB RAM<br></li><li><strong>Graphics:</strong> NVIDIA GEFORCE GTX 1070 8 GB or AMD RADEON RX VEGA 56 8 GB<br></li></ul>"}}}}
    """;

    [Test] public static void ParsesSteamRequirements()
    {
        using var doc = JsonDocument.Parse(Details);
        var g = SteamStore.ParseDetails(1245620, doc.RootElement)!;
        Assert.Equal("ELDEN RING", g.Name);
        Assert.Equal(2, g.Tiers.Count);
        var min = g.Tiers[0];
        Assert.Equal(12.0, min.RamGb);
        Assert.Equal(2, min.Cpus.Length);
        Assert.Equal(93, min.GpuIndex, "GTX 1060 3 GB é o mais fraco dos dois");
        Assert.Equal(8.0, g.Tiers[1].VramGb);
        Assert.True(g.Note!.StartsWith("Requisitos lidos"), g.Note);
        // mesmo veredito que o catálogo oficial do Snow para o Elden Ring
        var hw = Hw.Desktop(h => h with { Gpus = [new GpuInfo { Name = "NVIDIA GeForce RTX 3060", VramBytes = 12L << 30, IsDiscrete = true }], Memory = new MemoryInfo { TotalBytes = 16L << 30 } });
        Assert.Equal(CanRun.Evaluate(CanRun.Catalog.First(x => x.Id == "eldenring"), hw).Level, CanRun.Evaluate(g, hw).Level);
    }

    [Test] public static void GenericAndUnknownRequirements()
    {
        var t = SteamRequirements.ParseTier("<li><strong>Processor:</strong> 3.0 GHz Quad Core<br></li><li><strong>Memory:</strong> 4096 MB RAM<br></li><li><strong>Graphics:</strong> Weird Card 9000<br></li>", "Mínimo", "x")!;
        Assert.Equal(4.0, t.RamGb);
        Assert.True(t.CpuGeneric is > 40 and < 60, $"{t.CpuGeneric}");
        Assert.True(t.GpuIndex is null);
        var g = SteamRequirements.Parse(1, "X", "<li><strong>Processor:</strong> Potato 3000<br></li><li><strong>Graphics:</strong> Weird Card<br></li><li><strong>Memory:</strong> 8 GB RAM</li>", null)!;
        Assert.True(g.Note!.Contains("processador") && g.Note.Contains("placa de vídeo"), g.Note);
        using var empty = JsonDocument.Parse("""{"5":{"success":true,"data":{"name":"Y","pc_requirements":[]}}}""");
        Assert.True(SteamStore.ParseDetails(5, empty.RootElement) is null);
        using var search = JsonDocument.Parse("""{"total":2,"items":[{"type":"app","name":"ELDEN RING","id":1245620},{"type":"app","name":"ELDEN RING NIGHTREIGN","id":2622380}]}""");
        Assert.Equal(1245620L, SteamStore.ParseSearch(search.RootElement)[0].AppId);
    }
}
