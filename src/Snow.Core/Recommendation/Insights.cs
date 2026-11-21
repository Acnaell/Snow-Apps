using Snow.Core.Hardware;

namespace Snow.Core.Recommendation;

public enum InsightLevel { Info, Atencao, Critico }

/// <summary>Achados do diagnóstico que software não resolve sozinho (BIOS, hardware, atualização).</summary>
public sealed record Insight(InsightLevel Level, string Title, string Detail);

public static class Insights
{
    public static IReadOnlyList<Insight> Analyze(HardwareProfile hw)
    {
        var list = new List<Insight>();
        var mem = hw.Memory;

        if (mem.TotalBytes > 0 && mem.TotalGb < 7.5)
            list.Add(new(InsightLevel.Critico, "Pouca RAM", $"{mem.TotalGb:0.#} GB é pouco para jogos atuais; 16 GB é o mínimo recomendado hoje."));

        var cpu = hw.Cpu;
        var modules = mem.Modules.Count;
        var registered = mem.Modules.Any(m => m.IsRegistered);
        var ecc = mem.Modules.Any(m => m.IsEcc);
        var serverNote = registered
            ? " Atenção: o seu módulo é de servidor (ECC Registered/RDIMM) e não funciona junto com memória comum de PC (UDIMM). " +
              "Para completar os canais, use módulos RDIMM DDR4 iguais ao atual (costumam ser baratos usados) ou troque tudo por módulos comuns iguais, se a placa aceitar."
            : ecc ? " O seu módulo tem ECC: prefira módulos iguais (mesmo tipo e capacidade)." : "";
        if (cpu.IsQuadChannelPlatform && modules is > 0 and < 4)
            list.Add(new(modules == 1 ? InsightLevel.Critico : InsightLevel.Atencao,
                $"Memória usando {modules} de 4 canais",
                $"O {cpu.Name.Replace("(R)", "").Replace("(TM)", "")} suporta memória em quatro canais, mas há {modules} módulo(s) instalado(s). " +
                "Cada módulo a mais (iguais, nos slots indicados no manual) multiplica a banda de memória, o que conta muito em jogos limitados por CPU. " +
                "Algumas placas X99 de entrada só têm 2 slots: nesse caso, use 2 módulos." + serverNote));
        else if (mem.Channels == MemoryChannelMode.Single)
            list.Add(new(InsightLevel.Critico, "Memória em canal único (single channel)",
                "Single channel pode custar de 10% a 30% de FPS em jogos limitados por CPU e muito mais em GPU integrada. Use dois módulos iguais nos slots indicados no manual da placa-mãe." + serverNote));

        if (mem.ConfiguredSpeedMts > 0 && cpu.IsXeon)
        {
            var max = cpu.MaxMemorySpeedMts;
            list.Add(new(InsightLevel.Info, $"RAM a {mem.ConfiguredSpeedMts} MT/s ({mem.Type})",
                max is { } mx && mem.ConfiguredSpeedMts >= mx - 50
                    ? $"Já está no máximo que o processador suporta ({mx} MT/s). Em Xeon a velocidade é travada pelo processador: XMP não se aplica."
                    : "Em Xeon a velocidade máxima da RAM é definida pelo modelo do processador e o XMP normalmente não se aplica. Mais canais (mais módulos) rendem mais que velocidade nessa plataforma."));
        }
        else if (mem.ConfiguredSpeedMts > 0)
        {
            var lowDdr4 = mem.Type == "DDR4" && mem.ConfiguredSpeedMts <= 2666;
            var lowDdr5 = mem.Type == "DDR5" && mem.ConfiguredSpeedMts <= 4800;
            if (lowDdr4 || lowDdr5)
                list.Add(new(InsightLevel.Atencao, $"RAM a {mem.ConfiguredSpeedMts} MT/s ({mem.Type})",
                    "Parece estar na velocidade JEDEC padrão. Se seus módulos suportam mais, ative o XMP (Intel) ou EXPO/DOCP (AMD) na BIOS. O Windows não consegue alterar isso."));
            else if (mem.RatedSpeedMts > mem.ConfiguredSpeedMts + 200)
                list.Add(new(InsightLevel.Atencao, "RAM abaixo da velocidade nominal",
                    $"Configurada a {mem.ConfiguredSpeedMts} MT/s; os módulos informam {mem.RatedSpeedMts} MT/s. Verifique o XMP/EXPO na BIOS."));
        }

        if (mem.UsedPercent > 85)
            list.Add(new(InsightLevel.Atencao, "Uso de RAM alto agora", $"{mem.UsedPercent:0}% em uso. Feche navegadores e launchers antes de jogar."));

        var sys = hw.Storage.SystemDisk;
        if (sys is { Kind: DiskKind.Hdd })
            list.Add(new(InsightLevel.Critico, "Windows instalado em HDD", "Migrar o sistema para um SSD é a melhoria de fluidez mais perceptível que existe para este PC."));
        foreach (var d in hw.Storage.GameDisks.Where(d => d.Kind == DiskKind.Hdd))
            list.Add(new(InsightLevel.Atencao, $"Jogos em HDD ({string.Join(", ", d.DriveLetters)}:)",
                "Jogos modernos fazem streaming de texturas; em HDD isso causa engasgos (stutter). Prefira um SSD, de preferência NVMe."));

        if (hw.State.MemoryIntegrity == TriState.On)
            list.Add(new(InsightLevel.Info, "Integridade de memória (HVCI/VBS) ativa",
                "É uma proteção de segurança importante. Em alguns jogos custa alguns % de desempenho. O Snow não altera isso automaticamente; avalie em Segurança do Windows > Isolamento de núcleo."));

        if (!hw.Cpu.VirtualizationFirmwareEnabled && !hw.Cpu.HypervisorPresent)
            list.Add(new(InsightLevel.Info, "Virtualização desativada na BIOS", "Não afeta FPS. Só é necessária para WSL2, Android/WSA e emuladores com Hyper-V."));

        if (hw.Cpu.IsHybrid && !hw.Os.IsWindows11)
            list.Add(new(InsightLevel.Atencao, "CPU híbrida no Windows 10",
                "O Windows 10 não usa o Intel Thread Director: threads do jogo podem cair nos E-cores. O Windows 11 agenda esses núcleos melhor."));

        if (!hw.Os.IsWindows11 && !hw.Os.IsServer)
        {
            list.Add(new(InsightLevel.Atencao, "Windows 10 fora do suporte padrão",
                "O suporte gratuito do Windows 10 terminou em 14/10/2025. Considere migrar para o Windows 11 ou aderir ao ESU para continuar recebendo correções de segurança."));
            if (hw.Os.Build < 19045)
                list.Add(new(InsightLevel.Atencao, "Windows 10 desatualizado", $"Build {hw.Os.Build}: atualize para a 22H2 (19045)."));
        }

        var gpu = hw.PrimaryGpu;
        if (gpu is null || gpu.IsVirtual)
            list.Add(new(InsightLevel.Info, "Nenhuma GPU física detectada", "Parece ser uma máquina virtual ou o driver de vídeo não está instalado."));
        else if (gpu.WddmMajorFromDriver is > 0 and < 27)
            list.Add(new(InsightLevel.Atencao, "Driver de vídeo antigo", $"Driver {gpu.DriverVersionFriendly}: instale a versão mais recente do site da {gpu.Vendor}."));

        if (!hw.State.IsLaptop && hw.State.ActivePowerPlanGuid.Equals("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase))
            list.Add(new(InsightLevel.Atencao, "Plano 'Economia de energia' ativo", "Num desktop, isso limita o clock da CPU. Qualquer perfil do Snow corrige."));

        return list;
    }
}
