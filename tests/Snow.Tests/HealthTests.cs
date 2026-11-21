using Snow.Core.Health;
using Snow.Platform.Health;

namespace Snow.Tests;

public static class HealthTests
{
    private static HealthData Healthy() => new()
    {
        Disks =
        [
            new DiskHealthData { Name = "KBG40ZNS256G NVMe KIOXIA 256GB", MediaType = 4, BusType = 17, HealthStatus = 0, SizeBytes = 256L << 30, TemperatureC = 38, WearPercent = 3, PowerOnHours = 4120 },
            new DiskHealthData { Name = "SSD SATA 512GB", MediaType = 4, BusType = 11, HealthStatus = 0, SizeBytes = 512L << 30 },
        ],
        Volumes = [new VolumeData("C:", 238L << 30, 90L << 30), new VolumeData("D:", 476L << 30, 200L << 30)],
        Events = new EventCounts(),
        GpuName = "AMD Radeon RX 580 2048SP",
        GpuDriverDate = DateTime.Now.AddMonths(-3),
        UptimeDays = 1,
    };

    [Test] public static void HealthyPcScoresHigh()
    {
        var r = HealthAnalyzer.Analyze(Healthy());
        Assert.True(r.Score >= 90, $"nota {r.Score}: {string.Join(" | ", r.Problems.Select(p => p.Title))}");
        Assert.Equal("Excelente", r.Verdict);
        Assert.True(r.Items.Any(i => i.Title.StartsWith("KBG40ZNS256G") && i.Detail.Contains("97% de vida útil restante")));
        Assert.True(r.Items.Any(i => i.Title.StartsWith("Sistema estável")));
    }

    [Test] public static void ProblemsLowerTheScoreAndExplain()
    {
        var d = Healthy() with
        {
            Disks = [new DiskHealthData { Name = "Velho HDD", MediaType = 3, BusType = 11, HealthStatus = 1, SizeBytes = 1L << 40, ReadErrorsUncorrected = 4 }],
            FailurePredicted = true,
            Volumes = [new VolumeData("C:", 238L << 30, 3L << 30)],
            Events = new EventCounts { BlueScreens = 4, GpuDriverResets = 2, HardwareErrors = 1, CrashingApps = [new("FortniteClient-Win64-Shipping.exe", 5)] },
            ProblemDevices = [new ProblemDevice("Controlador de Ethernet", 28), new ProblemDevice("Desativado pelo usuário", 22)],
            GpuDriverDate = DateTime.Now.AddMonths(-20),
            RebootPending = true,
        };
        var r = HealthAnalyzer.Analyze(d);
        Assert.True(r.Score < 30, $"nota {r.Score}");
        var titles = r.Items.Select(i => i.Title).ToList();
        foreach (var expected in new[] { "O disco prevê falha (SMART)", "4 tela(s) azul(is) em 30 dias", "Driver de vídeo reiniciou 2 vez(es) em 30 dias", "Controlador de Ethernet: driver não instalado", "Reinicialização pendente" })
            Assert.True(titles.Contains(expected), $"faltou '{expected}' em: {string.Join(" | ", titles)}");
        Assert.False(titles.Any(t => t.StartsWith("Desativado pelo usuário")), "dispositivo desativado de propósito não é problema");
        Assert.True(titles.Any(t => t.StartsWith("Driver de vídeo com 20 meses") || t.StartsWith("Driver de vídeo com 19 meses")));
        Assert.True(titles.Any(t => t.StartsWith("C: quase cheio")));
        Assert.Equal(HealthLevel.Critico, r.Items[0].Level, "críticos primeiro");
    }

    [Test] public static void VirtualMachineDoesNotPanic()
    {
        var d = new HealthData { Disks = [new DiskHealthData { Name = "Microsoft Virtual Disk", BusType = 15, HealthStatus = 0 }], Events = new EventCounts { Available = false } };
        var r = HealthAnalyzer.Analyze(d);
        Assert.True(r.Score >= 90, $"nota {r.Score}");
        Assert.True(r.Items.Any(i => i.Title == "Saúde dos discos indisponível"));
    }

    [Test] public static void ParsesPowerShellOutput()
    {
        // Formato do ConvertTo-Json do Windows PowerShell 5.1 (inclui o embrulho {value,Count} e números como string)
        const string json = """
        {"disks":{"value":[{"Name":"KBG40ZNS256G NVMe KIOXIA 256GB","Media":4,"Bus":17,"Health":0,"Size":256060514304,"Temp":41,"TempMax":0,"Wear":2,"Hours":"4120","RUnc":0,"WUnc":0,"RTot":null}],"Count":1},
         "predictFailure":false,
         "volumes":[{"Letter":"C:","Size":255378566656,"Free":91268218880}],
         "devices":[],
         "gpu":{"Name":"AMD Radeon RX 580 2048SP","DriverDate":"2025-03-11"},
         "events":{"Days":30,"Available":true,"UnexpectedShutdowns":1,"BlueScreens":0,"GpuDriverResets":3,"HardwareErrors":0,"DiskErrors":0,"Apps":[{"App":"chrome.exe","Count":2}]},
         "stability":8.7,"uptimeDays":2.3,"rebootPending":false,"warnings":[]}
        """;
        var d = HealthCollector.Parse(json);
        var disk = Assert.Single(d.Disks, "disco");
        Assert.Equal(41, disk.TemperatureC);
        Assert.Equal(null, disk.TemperatureMaxC, "0 = não informado");
        Assert.Equal(4120L, disk.PowerOnHours, "número vindo como string");
        Assert.True(disk.IsSsd);
        Assert.Equal(3, d.Events.GpuDriverResets);
        Assert.Equal(new DateTime(2025, 3, 11), d.GpuDriverDate);
        Assert.Equal(8.7, d.StabilityIndex);
        var r = HealthAnalyzer.Analyze(d);
        Assert.True(r.Items.Any(i => i.Title == "Driver de vídeo reiniciou 3 vez(es) em 30 dias" && i.Level == HealthLevel.Critico));
    }

    [Test] public static void BatteryWear()
    {
        var r = HealthAnalyzer.Analyze(Healthy() with { Battery = new BatteryData(50000, 27000, 640) });
        Assert.True(r.Items.Any(i => i.Category == HealthCategory.Bateria && i.Level == HealthLevel.Atencao && i.Title.Contains("54%")));
        Assert.True(HealthAnalyzer.Analyze(Healthy()).Items.All(i => i.Category != HealthCategory.Bateria), "desktop sem bateria não mostra item");
    }
}
