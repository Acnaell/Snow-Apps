using System.Text.RegularExpressions;
using Snow.Core.Games;
using Snow.Core.Hardware;
using Snow.Core.Health;
using Snow.Core.Monitor;

namespace Snow.Core.Recommendation;

public enum UpgradePriority { Alta, Media, Baixa, Evitar }

/// <summary>Uma sugestão de upgrade. "Evitar" = compra que não renderia nada com o gargalo atual.</summary>
public sealed record UpgradeAdvice(UpgradePriority Priority, string Component, string Title, string Detail);

public enum CpuClass { Fraco, Medio, Forte }

/// <summary>
/// Indica a próxima peça a trocar sem criar (ou manter) um gargalo: olha o hardware e, se houver,
/// a última sessão do Monitor (quanto tempo o jogo ficou preso na CPU ou na GPU) e a saúde dos discos.
/// Sem preços (mudam toda semana): só classes de peça e o porquê.
/// </summary>
public static partial class UpgradeAdvisor
{
    [GeneratedRegex(@"Xeon.*\b(E[35]-\d{4}|X\d{4}|L\d{4})|Core\(TM\)2|Core\(TM\) i[3579]-[2-7]\d{3}|\bFX-|Ryzen [3579] 1\d{3}|Athlon|Pentium|Celeron|\bA\d{1,2}-\d{4}", RegexOptions.IgnoreCase)]
    private static partial Regex WeakCpu();

    [GeneratedRegex(@"X3D|Core\(TM\) i[579]-1[2-4]\d{3}|Core\(TM\) Ultra [579] 2\d\d|Ryzen [579] [79]\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex StrongCpu();

    [GeneratedRegex(@"E5-(\d{4})\s*v([34])", RegexOptions.IgnoreCase)] private static partial Regex XeonE5V34();

    /// <summary>Classe do processador para jogos (desempenho por núcleo, que é o que limita o FPS).</summary>
    public static CpuClass ClassifyCpu(CpuInfo cpu) =>
        StrongCpu().IsMatch(cpu.Name) ? CpuClass.Forte
        : WeakCpu().IsMatch(cpu.Name) || cpu.LogicalProcessors is > 0 and <= 4 ? CpuClass.Fraco
        : CpuClass.Medio;

    public static IReadOnlyList<UpgradeAdvice> Advise(HardwareProfile hw, SessionSummary? session = null, IReadOnlyList<DiskHealthData>? disks = null)
    {
        var list = new List<UpgradeAdvice>();
        var cpu = hw.Cpu;
        var mem = hw.Memory;
        var gpu = hw.PrimaryGpu;
        var cpuClass = ClassifyCpu(cpu);
        var gpuTier = HardwareTiers.Classify(hw);

        // O que a medição mostrou (ou, sem medição, o que o hardware sugere)
        double cpuShare = 0, gpuShare = 0;
        var measured = session is { Main: not Bottleneck.Desconhecido and not Bottleneck.SemCarga };
        if (measured)
        {
            cpuShare = session!.TimeShare.GetValueOrDefault(Bottleneck.Cpu);
            gpuShare = session.TimeShare.GetValueOrDefault(Bottleneck.Gpu);
        }
        var cpuBound = measured ? cpuShare >= 40 && cpuShare > gpuShare : cpuClass == CpuClass.Fraco && gpuTier >= HardwareTier.Intermediario;
        var gpuBound = measured ? gpuShare >= 40 && gpuShare > cpuShare : cpuClass != CpuClass.Fraco && gpuTier == HardwareTier.Entrada;
        var evidence = measured
            ? $"Na última medição o jogo ficou {cpuShare:0}% do tempo preso no processador e {gpuShare:0}% na placa de vídeo."
            : "Ainda sem medição: estimativa pelo hardware. Meça um jogo para confirmar.";

        // ── Discos com defeito vêm primeiro: perder dados é pior que perder FPS
        foreach (var d in disks ?? [])
        {
            var errors = (d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0);
            if (errors > 0 || d.HealthStatus is 1 or 2)
            {
                var letters = hw.Storage.Disks.FirstOrDefault(x => x.Model.Equals(d.Name, StringComparison.OrdinalIgnoreCase))?.DriveLetters ?? [];
                var where = letters.Count > 0 ? $" ({string.Join(", ", letters.Select(l => l + ":"))})" : "";
                list.Add(new(UpgradePriority.Alta, "Armazenamento", $"Substituir o disco {d.Name}{where}",
                    $"{(errors > 0 ? $"{errors} erro(s) não corrigido(s) no SMART" : "SMART em alerta")}: o disco pode falhar e, antes disso, travar jogos ao ler arquivos. " +
                    "Faça backup. Um SSD NVMe novo (se a placa tiver slot M.2) ou SATA resolve; jogos instalados nele também param de engasgar por leitura."));
            }
        }

        // ── Memória: canais vazios custam caro justamente quando o jogo está preso no processador
        var modules = mem.Modules.Count;
        var maxChannels = cpu.IsQuadChannelPlatform ? 4 : 2;
        var registered = mem.Modules.Any(m => m.IsRegistered);
        var ecc = mem.Modules.Any(m => m.IsEcc);
        var size = mem.Modules.Count > 0 ? mem.Modules.GroupBy(m => m.CapacityBytes).OrderByDescending(g => g.Count()).First().Key : 0;
        var kind = $"{mem.Type}{(registered ? " registrada (RDIMM)" : "")}{(ecc ? " ECC" : "")}";
        if (modules > 0 && modules < maxChannels)
        {
            var sizeGb = size >> 30;
            var speed = cpu.MaxMemorySpeedMts is { } max ? $" O processador limita a {max} MT/s: pentes mais rápidos funcionam, só não passam disso." : "";
            var how = maxChannels == 4
                ? $"Mais {4 - modules} pente(s) = 4 canais (o máximo da plataforma); mais {Math.Max(1, 2 - modules)} já dobra a banda."
                : $"Mais {2 - modules} pente igual = dual channel.";
            list.Add(new(cpuBound ? UpgradePriority.Alta : UpgradePriority.Media, "Memória",
                $"Completar os canais de memória ({modules} de {maxChannels} em uso)",
                $"{how} Compre {kind} de {sizeGb} GB, igual ao atual{(registered ? " — memória comum de PC (UDIMM) não funciona junto com a registrada" : "")}.{speed} " +
                "É o upgrade mais barato e o que mais sobe o 1% low em jogo limitado pelo processador."));
        }
        if (mem.TotalGb is > 0 and < 15)
            list.Add(new(UpgradePriority.Alta, "Memória", $"Aumentar para 16 GB ({mem.TotalGb:0} GB hoje)",
                "Jogos atuais com navegador e Discord abertos passam de 8 GB: o Windows usa o disco como memória e o jogo engasga."));

        // ── Processador
        var gpuName = gpu?.Name ?? "placa de vídeo";
        if (cpuBound)
        {
            var e5 = XeonE5V34().Match(cpu.Name);
            if (e5.Success)
            {
                var v3 = e5.Groups[2].Value == "3";
                list.Add(new(UpgradePriority.Alta, "Processador", "Xeon de clock alto, na mesma placa-mãe",
                    "O que limita o FPS é o desempenho de cada núcleo, não a quantidade. " +
                    (v3 ? "Com BIOS que aceite v4: Xeon E5-1650 v4 (6 núcleos, até 4,0 GHz), E5-2667 v4 (8 núcleos, até 3,6 GHz) ou E5-2689 v4 (10 núcleos, até 3,8 GHz). Só v3: E5-1650 v3 ou E5-2667 v3. "
                        : "Xeon E5-1650 v4 (até 4,0 GHz), E5-2667 v4 (até 3,6 GHz) ou E5-2689 v4 (até 3,8 GHz). ") +
                    "Confira na página da placa-mãe se o modelo é suportado (os E5-16xx não funcionam em algumas placas). Eles também aceitam memória mais rápida (2400 MT/s)."));
                list.Add(new(UpgradePriority.Media, "Plataforma", "Maior salto: trocar processador, placa-mãe e memória",
                    "Um Ryzen 5 5600/5700X3D (AM4) ou Ryzen/Core atual tem cerca do dobro de desempenho por núcleo de um Xeon E5 (em jogos, às vezes mais). " +
                    $"A {gpuName} vai junto e finalmente trabalha a 95–100%. Atenção: a memória registrada (servidor) não serve em placas comuns; seria preciso DDR4/DDR5 comum."));
            }
            else
                list.Add(new(UpgradePriority.Alta, "Processador", "Processador com mais desempenho por núcleo",
                    $"Com a {gpuName} sobrando, um processador mais rápido sobe o FPS direto. Veja o processador mais forte que a sua placa-mãe aceita (lista de CPUs suportadas do fabricante) antes de trocar a plataforma."));

            if (gpu is not null)
                list.Add(new(UpgradePriority.Evitar, "Placa de vídeo", $"Não troque a {gpuName} agora",
                    $"{evidence} Uma placa mais forte ficaria ainda mais ociosa: o FPS quase não mudaria. Troque depois do processador."));
        }
        else if (gpuBound && gpu is not null)
        {
            var ceiling = cpuClass switch
            {
                CpuClass.Fraco => "até a classe RX 6600 / RTX 3060 (acima disso, o seu processador vira o limite)",
                CpuClass.Medio => "até a classe RX 7700 XT / RTX 4070",
                _ => "qualquer classe: o processador acompanha",
            };
            list.Add(new(UpgradePriority.Alta, "Placa de vídeo", $"Placa de vídeo mais forte que a {gpuName}",
                $"{evidence} Com o seu processador, vale {ceiling}. Para 1080p, 8 GB de VRAM é o mínimo hoje; 12 GB dá folga."));
            list.Add(new(UpgradePriority.Evitar, "Processador", "Não troque o processador primeiro",
                "A placa de vídeo já está no limite: um processador novo não subiria o FPS."));
        }
        else if (gpu is { VramBytes: > 0 } g && g.VramBytes < (4L << 30) && !g.IsVirtual)
            list.Add(new(UpgradePriority.Media, "Placa de vídeo", $"Pouca memória de vídeo ({g.VramBytes >> 30} GB)",
                "Jogos atuais em 1080p usam 6–8 GB de VRAM; com menos, as texturas precisam ficar no mínimo."));

        // ── Discos lentos
        var sys = hw.Storage.SystemDisk;
        if (sys is { Kind: DiskKind.Hdd })
            list.Add(new(UpgradePriority.Alta, "Armazenamento", "Windows em HD mecânico",
                "O Windows num SSD é a mudança mais perceptível no uso do dia a dia (inicialização, abrir programas)."));
        foreach (var d in hw.Storage.GameDisks.Where(d => d.Kind == DiskKind.Hdd).Take(1))
            list.Add(new(UpgradePriority.Media, "Armazenamento", "Jogos em HD mecânico",
                "Jogos de mundo aberto carregam texturas durante a partida: em HD isso vira engasgo. Um SSD de 1 TB resolve."));

        if (list.Count == 0)
            list.Add(new(UpgradePriority.Baixa, "Geral", "Hardware equilibrado",
                $"{evidence} Nenhuma peça segura as outras de forma clara. Upgrade só se um jogo específico pedir mais."));

        return list.OrderBy(a => a.Priority).ToList();
    }

    public static string Label(this UpgradePriority p) => p switch
    {
        UpgradePriority.Alta => "Prioridade alta",
        UpgradePriority.Media => "Prioridade média",
        UpgradePriority.Baixa => "Opcional",
        _ => "Evite agora",
    };
}
