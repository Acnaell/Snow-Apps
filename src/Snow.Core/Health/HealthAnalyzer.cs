namespace Snow.Core.Health;

/// <summary>
/// Transforma os dados lidos do Windows em achados e numa nota de 0 a 100.
/// Pesos: crítico −25, atenção −8, informativo −2 (a nota nunca passa de 100 nem fica abaixo de 0).
/// </summary>
public static class HealthAnalyzer
{
    private static string Gb(long b) => $"{b / 1024d / 1024 / 1024:0.#} GB";

    public static HealthReport Analyze(HealthData d)
    {
        var items = new List<HealthItem>();
        Disks(d, items);
        Stability(d, items);
        Devices(d, items);
        Space(d, items);
        Battery(d, items);
        System(d, items);
        items.AddRange(BoardAdvisor.Items(d.Board, d.Security, d.CpuName, d.OsBuild));
        if (d.DriveCare is { } dc) items.AddRange(DriveCareParser.Items(dc, d.Disks.Any(x => x.IsSsd && !x.IsVirtual)));

        var score = 100;
        // Informativos da placa-mãe (modelo, slots, Windows 11) não são problemas: não tiram nota.
        foreach (var i in items.Where(i => !(i.Category == HealthCategory.PlacaMae && i.Level == HealthLevel.Info)))
            score -= i.Level switch { HealthLevel.Critico => 25, HealthLevel.Atencao => 8, HealthLevel.Info => 2, _ => 0 };
        score = Math.Clamp(score, 0, 100);
        var verdict = score switch
        {
            >= 90 => "Excelente",
            >= 75 => "Boa",
            >= 55 => "Regular",
            >= 30 => "Precisa de atenção",
            _ => "Crítica",
        };
        return new HealthReport(score, verdict, items.OrderByDescending(i => i.Level).ThenBy(i => i.Category).ToList(), d);
    }

    private static void Disks(HealthData d, List<HealthItem> items)
    {
        if (d.FailurePredicted)
            items.Add(new(HealthCategory.Discos, HealthLevel.Critico, "O disco prevê falha (SMART)",
                "O próprio disco reportou que pode falhar em breve.", "Faça backup dos seus arquivos agora e planeje a troca do disco."));

        foreach (var disk in d.Disks.Where(x => !x.IsVirtual))
        {
            var name = string.IsNullOrWhiteSpace(disk.Name) ? "Disco" : disk.Name.Trim();
            var problems = 0;
            if (disk.HealthStatus == 2)
            {
                items.Add(new(HealthCategory.Discos, HealthLevel.Critico, $"{name}: estado ruim",
                    "O Windows marca este disco como não saudável.", "Faça backup e substitua o disco."));
                problems++;
            }
            else if (disk.HealthStatus == 1)
            {
                items.Add(new(HealthCategory.Discos, HealthLevel.Atencao, $"{name}: alerta de saúde", "O Windows marca este disco com aviso.", "Faça backup e acompanhe."));
                problems++;
            }

            if (disk.WearPercent is { } wear)
            {
                if (wear >= 90) { items.Add(new(HealthCategory.Discos, HealthLevel.Critico, $"{name}: {wear}% da vida útil usada", "O SSD está perto do fim da vida útil nominal.", "Faça backup e planeje a troca.")); problems++; }
                else if (wear >= 70) { items.Add(new(HealthCategory.Discos, HealthLevel.Atencao, $"{name}: {wear}% da vida útil usada", "O SSD já consumiu boa parte da vida útil nominal.", "Mantenha backup em dia.")); problems++; }
            }

            var uncorrected = (disk.ReadErrorsUncorrected ?? 0) + (disk.WriteErrorsUncorrected ?? 0);
            if (uncorrected > 0)
            {
                items.Add(new(HealthCategory.Discos, HealthLevel.Critico, $"{name}: {uncorrected} erro(s) não corrigido(s)",
                    "O disco não conseguiu ler ou gravar dados em algumas ocasiões. Arquivos nesses pontos podem estar corrompidos.",
                    "Faça backup do que for importante agora. Rode 'chkdsk /scan' e acompanhe: se o número subir, troque o disco."));
                problems++;
            }

            if (disk.TemperatureC is { } t)
            {
                var limit = disk.IsSsd ? 70 : 55;
                if (t >= limit) { items.Add(new(HealthCategory.Discos, HealthLevel.Atencao, $"{name}: {t} °C", "Temperatura alta para este tipo de disco.", "Melhore a ventilação ou use um dissipador no NVMe.")); problems++; }
            }

            if (problems == 0)
            {
                var info = new List<string> { disk.IsSsd ? (disk.BusType == 17 ? "SSD NVMe" : "SSD") : "HDD", Gb(disk.SizeBytes) };
                if (disk.WearPercent is { } w) info.Add($"{100 - w}% de vida útil restante");
                if (disk.PowerOnHours is { } h) info.Add($"{h:N0} h ligado");
                if (disk.TemperatureC is { } tc) info.Add($"{tc} °C");
                items.Add(new(HealthCategory.Discos, HealthLevel.Ok, $"{name}: saudável", string.Join(" · ", info)));
            }
        }

        if (!d.Disks.Any(x => !x.IsVirtual))
            items.Add(new(HealthCategory.Discos, HealthLevel.Info, "Saúde dos discos indisponível", "O Windows não expôs dados SMART (comum em máquinas virtuais e alguns adaptadores USB)."));
    }

    private static void Stability(HealthData d, List<HealthItem> items)
    {
        var e = d.Events;
        if (!e.Available)
        {
            items.Add(new(HealthCategory.Estabilidade, HealthLevel.Info, "Registros de eventos indisponíveis", "Não foi possível ler o Visualizador de Eventos."));
            return;
        }
        var days = e.Days;
        if (e.BlueScreens > 0)
            items.Add(new(HealthCategory.Estabilidade, e.BlueScreens >= 3 ? HealthLevel.Critico : HealthLevel.Atencao,
                $"{e.BlueScreens} tela(s) azul(is) em {days} dias",
                "O Windows travou com erro fatal. Causas comuns: driver, RAM instável (XMP/overclock), fonte ou temperatura.",
                "Atualize os drivers (principalmente vídeo e chipset) e, se você usa overclock ou XMP, teste sem."));

        if (e.GpuDriverResets > 0)
            items.Add(new(HealthCategory.Estabilidade, e.GpuDriverResets >= 3 ? HealthLevel.Critico : HealthLevel.Atencao,
                $"Driver de vídeo reiniciou {e.GpuDriverResets} vez(es) em {days} dias",
                "A tela pisca ou o jogo fecha quando o driver de vídeo trava e se recupera (TDR). Pode ser driver, overclock da GPU, temperatura ou fonte.",
                "Instale o driver mais recente (limpo), remova overclock da GPU e verifique a temperatura."));

        if (e.UnexpectedShutdowns > 0)
            items.Add(new(HealthCategory.Estabilidade, e.UnexpectedShutdowns >= 3 ? HealthLevel.Atencao : HealthLevel.Info,
                $"{e.UnexpectedShutdowns} desligamento(s) inesperado(s) em {days} dias",
                "O PC desligou sem encerrar o Windows (queda de energia, botão pressionado ou travamento).",
                e.UnexpectedShutdowns >= 3 ? "Se você não desligou na força, investigue fonte e temperatura." : null));

        if (e.HardwareErrors > 0)
            items.Add(new(HealthCategory.Hardware, e.HardwareErrors >= 5 ? HealthLevel.Critico : HealthLevel.Atencao,
                $"{e.HardwareErrors} erro(s) de hardware (WHEA) em {days} dias",
                "O processador, a memória ou o barramento PCIe reportaram erros. Em PCs com overclock/undervolt é sinal de instabilidade.",
                "Volte CPU/RAM para os valores padrão na BIOS e atualize a BIOS e o chipset."));

        if (e.DiskErrors > 0)
            items.Add(new(HealthCategory.Discos, e.DiskErrors >= 10 ? HealthLevel.Critico : HealthLevel.Atencao,
                $"{e.DiskErrors} erro(s) de disco no registro em {days} dias",
                "Falhas de leitura/escrita ou de sistema de arquivos.", "Faça backup e rode o CHKDSK; verifique os cabos SATA."));

        foreach (var app in e.CrashingApps.Where(a => a.Count >= 3).Take(3))
            items.Add(new(HealthCategory.Estabilidade, HealthLevel.Info, $"{app.App} travou {app.Count} vezes", $"Nos últimos {days} dias.", "Atualize ou reinstale o aplicativo."));

        if (d.StabilityIndex is { } idx && idx < 5)
            items.Add(new(HealthCategory.Estabilidade, HealthLevel.Atencao, $"Índice de estabilidade do Windows: {idx:0.0}/10",
                "Calculado pelo próprio Windows a partir de falhas recentes.", "Veja os detalhes no Monitor de Confiabilidade."));

        if (e.BlueScreens == 0 && e.GpuDriverResets == 0 && e.HardwareErrors == 0 && e.UnexpectedShutdowns == 0 && e.DiskErrors == 0)
            items.Add(new(HealthCategory.Estabilidade, HealthLevel.Ok, $"Sistema estável nos últimos {days} dias", "Nenhuma tela azul, reset de driver de vídeo, erro de hardware ou desligamento inesperado."));
    }

    private static void Devices(HealthData d, List<HealthItem> items)
    {
        // 22 = desativado pelo usuário; 24 = não presente (ex.: PS/2 fantasma da BIOS); 45 = desconectado. Não são defeitos.
        var broken = d.ProblemDevices.Where(p => p.ErrorCode is not (22 or 24 or 45)).ToList();
        foreach (var p in broken.Take(5))
            items.Add(new(HealthCategory.Dispositivos, p.ErrorCode == 28 ? HealthLevel.Atencao : HealthLevel.Atencao,
                $"{(string.IsNullOrWhiteSpace(p.Name) ? "Dispositivo desconhecido" : p.Name.Trim())}: {DeviceError(p.ErrorCode)}", $"Código de erro {p.ErrorCode} no Gerenciador de Dispositivos.",
                p.ErrorCode == 28 ? "Instale o driver do fabricante." : "Atualize ou reinstale o driver deste dispositivo."));
        if (broken.Count > 5)
            items.Add(new(HealthCategory.Dispositivos, HealthLevel.Info, $"+{broken.Count - 5} dispositivo(s) com problema", "Veja no Gerenciador de Dispositivos."));

        // 2006-06-21 é a data simbólica dos drivers nativos do Windows: não indica idade real.
        if (d.GpuDriverDate is { } date && d.GpuName is { } gpu && date.Year > 2006
            && !gpu.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && !gpu.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
        {
            var months = (int)((d.CollectedAt - date).TotalDays / 30.4);
            if (months >= 12)
                items.Add(new(HealthCategory.Dispositivos, HealthLevel.Atencao, $"Driver de vídeo com {months} meses",
                    $"{gpu}: driver de {date:dd/MM/yyyy}. Drivers novos trazem correções e desempenho para jogos recentes.",
                    "Baixe o driver mais recente no site da AMD, NVIDIA ou Intel."));
            else
                items.Add(new(HealthCategory.Dispositivos, HealthLevel.Ok, "Driver de vídeo recente", $"{gpu}: {date:dd/MM/yyyy}."));
        }
        if (broken.Count == 0)
            items.Add(new(HealthCategory.Dispositivos, HealthLevel.Ok, "Todos os dispositivos funcionando", "Nenhum erro no Gerenciador de Dispositivos."));
    }

    public static string DeviceError(int code) => code switch
    {
        1 => "não configurado corretamente",
        3 => "driver corrompido ou memória insuficiente",
        10 => "não pode ser iniciado",
        12 => "sem recursos livres",
        14 => "precisa reiniciar o PC",
        18 => "precisa reinstalar o driver",
        19 => "registro do driver corrompido",
        21 => "sendo removido",
        24 => "não presente ou sem driver",
        28 => "driver não instalado",
        31 => "driver não carregou",
        32 => "serviço do driver desativado",
        37 => "falha ao iniciar o driver",
        39 => "driver corrompido ou ausente",
        43 => "parou por erro reportado",
        45 => "desconectado",
        52 => "assinatura do driver inválida",
        _ => "com problema",
    };

    private static void Space(HealthData d, List<HealthItem> items)
    {
        foreach (var v in d.Volumes.Where(v => v.SizeBytes > 0))
        {
            var drive = v.Letter.TrimEnd(':') + ":";
            if (v.FreePercent < 5 || v.FreeBytes < 5L << 30)
                items.Add(new(HealthCategory.Espaco, HealthLevel.Critico, $"{drive} quase cheio ({Gb(v.FreeBytes)} livres)",
                    "Pouco espaço causa lentidão, falhas em atualizações e no arquivo de paginação.", "Libere espaço (temporários, jogos que não usa, Lixeira)."));
            else if (v.FreePercent < 12)
                items.Add(new(HealthCategory.Espaco, HealthLevel.Atencao, $"{drive} {v.FreePercent:0}% livre ({Gb(v.FreeBytes)})",
                    "SSDs perdem desempenho e o Windows fica sem folga para atualizações.", "Tente manter pelo menos 15% livre."));
        }
        if (!items.Any(i => i.Category == HealthCategory.Espaco) && d.Volumes.Count > 0)
            items.Add(new(HealthCategory.Espaco, HealthLevel.Ok, "Espaço em disco adequado",
                string.Join(" · ", d.Volumes.Select(v => $"{v.Letter.TrimEnd(':')}: {v.FreePercent:0}% livre"))));
    }

    private static void Battery(HealthData d, List<HealthItem> items)
    {
        if (d.Battery is not { DesignedCapacity: > 0 } b) return;
        var h = b.HealthPercent;
        var cycles = b.CycleCount is > 0 ? $" · {b.CycleCount} ciclos" : "";
        if (h < 60)
            items.Add(new(HealthCategory.Bateria, HealthLevel.Atencao, $"Bateria com {h:0}% da capacidade original", $"Desgaste alto{cycles}.", "Considere trocar a bateria."));
        else
            items.Add(new(HealthCategory.Bateria, h < 80 ? HealthLevel.Info : HealthLevel.Ok, $"Bateria com {h:0}% da capacidade original", $"Desgaste normal para o uso{cycles}."));
    }

    private static void System(HealthData d, List<HealthItem> items)
    {
        if (d.RebootPending)
            items.Add(new(HealthCategory.Sistema, HealthLevel.Info, "Reinicialização pendente", "Uma atualização do Windows está esperando o PC reiniciar.", "Reinicie quando puder."));
        if (d.UptimeDays >= 14)
            items.Add(new(HealthCategory.Sistema, HealthLevel.Info, $"Ligado há {d.UptimeDays:0} dias sem reiniciar",
                "Com a Inicialização Rápida, \"desligar\" não reinicia o Windows de verdade.", "Use Reiniciar de vez em quando."));
    }
}
