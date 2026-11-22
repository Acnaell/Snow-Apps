using System.Text.RegularExpressions;

namespace Snow.Core.Health;

public sealed record BoardInfo
{
    public string Maker { get; init; } = "";
    public string Product { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public DateTime? BiosDate { get; init; }
    public int MemorySlots { get; init; }
    public int MemoryUsed { get; init; }
    /// <summary>Máximo de RAM declarado pela BIOS (SMBIOS). Muitas placas informam errado; serve só de referência.</summary>
    public long? MaxMemoryBytes { get; init; }

    public string Name => $"{Maker} {Product}".Trim();
}

public sealed record TpmInfo(bool Present, bool Enabled, bool Activated, string SpecVersion, string Maker)
{
    /// <summary>Maior versão da lista ("2.0, 0, 1.38" → 2.0).</summary>
    public double Version => double.TryParse(SpecVersion.Split(',')[0].Trim(), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}

public sealed record SecurityState
{
    public TpmInfo? Tpm { get; init; }
    /// <summary>true = UEFI, false = BIOS Legacy (CSM), null = desconhecido.</summary>
    public bool? Uefi { get; init; }
    public bool? SecureBoot { get; init; }
    /// <summary>Estilo de partição do disco do Windows: true = GPT, false = MBR.</summary>
    public bool? SystemDiskGpt { get; init; }
}

public enum TpmRoute { JaTem, LigarNaBios, FirmwareAmd, FirmwareIntel, ModuloNoConector, SemConector, Desconhecido }

public static class BoardAdvisor
{
    private static readonly string[] ChineseX99Makers = ["huananzhi", "machinist", "jingsha", "atermiter", "kllisre", "qiyida", "zx-99", "plexhd", "x99-"];

    /// <summary>Placas X99/X79 "chinesas": fabricante desses nomes, ou fabricante genérico ("INTEL", "Default string") com produto X99/X79.</summary>
    public static bool IsChineseX99(BoardInfo b)
    {
        var all = (b.Maker + " " + b.Product).ToLowerInvariant();
        if (ChineseX99Makers.Any(all.Contains) && !all.Contains("asus") && !all.Contains("asrock") && !all.Contains("gigabyte") && !all.Contains("msi")) return true;
        var genericMaker = b.Maker.Length == 0 || Regex.IsMatch(b.Maker, @"^(intel( corporation)?|default string|to be filled|oem|unknown)$", RegexOptions.IgnoreCase);
        return genericMaker && Regex.IsMatch(b.Product, @"x99|x79|c612|c602", RegexOptions.IgnoreCase);
    }

    /// <summary>Plataformas sem TPM no firmware: Xeon E5/E7 v1–v4 (LGA 2011/2011-3), Core das séries 3/4/5/6 X (X79/X99) e Intel até a 5ª geração.</summary>
    public static bool IsLegacyIntelHedt(string cpu) =>
        Regex.IsMatch(cpu, @"Xeon.*\bE[57]-\d{4}|Core\(TM\) i7-(38|39|48|49|58|59|68|69)\d\d", RegexOptions.IgnoreCase);

    public static TpmRoute Route(string cpu, SecurityState s, BoardInfo? b)
    {
        if (s.Tpm is { Present: true } t)
            return t.Version >= 2 && t.Enabled && t.Activated ? TpmRoute.JaTem : t.Version >= 2 ? TpmRoute.LigarNaBios : Route(cpu, s with { Tpm = null }, b);
        if (Regex.IsMatch(cpu, @"Ryzen|Threadripper|Athlon.*\d{4}", RegexOptions.IgnoreCase)) return TpmRoute.FirmwareAmd;
        if (IsLegacyIntelHedt(cpu)) return b is not null && IsChineseX99(b) ? TpmRoute.SemConector : TpmRoute.ModuloNoConector;
        // Intel PTT existe na prática a partir da 6ª geração (Skylake) nas placas de consumo.
        if (Regex.Match(cpu, @"i[3579]-(\d{4,5})", RegexOptions.IgnoreCase) is { Success: true } m)
        {
            var model = m.Groups[1].Value;
            var gen = model.Length == 5 ? int.Parse(model[..2]) : int.Parse(model[..1]);
            return gen >= 6 ? TpmRoute.FirmwareIntel : TpmRoute.ModuloNoConector;
        }
        if (Regex.IsMatch(cpu, @"Core\(TM\) Ultra|Core Ultra|Xeon.*\b(E|W)-\d{4}|Pentium.*G[4-7]\d{3}|Celeron.*G[4-6]\d{3}", RegexOptions.IgnoreCase)) return TpmRoute.FirmwareIntel;
        return TpmRoute.Desconhecido;
    }

    /// <summary>
    /// Lista de processadores do Windows 11 (resumo): Intel Core 8ª geração ou mais nova, Core Ultra, Xeon W-2200/W-3200+, Xeon E-2100+;
    /// AMD Ryzen 2000 ou mais novo. null = não sei dizer pelo nome.
    /// </summary>
    public static bool? Windows11Cpu(string cpu)
    {
        if (Regex.IsMatch(cpu, @"Core\(TM\) Ultra|Core Ultra", RegexOptions.IgnoreCase)) return true;
        if (Regex.Match(cpu, @"i[3579]-(\d{4,5})", RegexOptions.IgnoreCase) is { Success: true } m)
        {
            var model = m.Groups[1].Value;
            var gen = model.Length == 5 ? int.Parse(model[..2]) : int.Parse(model[..1]);
            return gen >= 8;
        }
        if (Regex.IsMatch(cpu, @"Xeon.*\bE[357]-\d{4}", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(cpu, @"Xeon.*\bE-2[1-9]\d{2}", RegexOptions.IgnoreCase)) return true;
        if (Regex.Match(cpu, @"Ryzen\s+(?:\d|Threadripper)\s+(?:PRO\s+)?(\d)\d{3}", RegexOptions.IgnoreCase) is { Success: true } r)
            return r.Groups[1].Value != "1";
        if (Regex.IsMatch(cpu, @"Ryzen .*\b1\d{3}", RegexOptions.IgnoreCase)) return false;
        return null;
    }

    public static IEnumerable<HealthItem> Items(BoardInfo? b, SecurityState s, string cpu, int osBuild)
    {
        const HealthCategory C = HealthCategory.PlacaMae;
        var win11 = osBuild >= 22000;

        if (b is not null && b.Name.Length > 0)
        {
            var bios = b.BiosVersion.Length > 0 ? $"BIOS {b.BiosVersion}" + (b.BiosDate is { } d ? $" de {d:MM/yyyy}" : "") : "versão da BIOS desconhecida";
            var kind = IsChineseX99(b) ? " Placa X99 de fabricante chinês." : "";
            yield return new(C, HealthLevel.Info, $"Placa-mãe: {b.Name}", $"{bios}.{kind}");

            if (b.MemorySlots > 0)
            {
                var free = b.MemorySlots - b.MemoryUsed;
                var max = b.MaxMemoryBytes is > 0 and var mx ? $" A BIOS informa até {mx / (1L << 30)} GB (nem sempre é exato)." : "";
                yield return free > 0
                    ? new(C, HealthLevel.Info, $"{b.MemoryUsed} de {b.MemorySlots} slots de memória ocupados",
                        $"Há {free} slot(s) livre(s) para mais memória.{max}",
                        "Veja em Desempenho > Upgrade sem gargalo qual pente combina com o seu.")
                    : new(C, HealthLevel.Info, $"Todos os {b.MemorySlots} slots de memória ocupados",
                        $"Para ter mais memória, troque os pentes por outros maiores.{max}");
            }
        }

        // ── TPM
        var route = Route(cpu, s, b);
        var needsNote = win11
            ? "No Windows 11, o Valorant (Vanguard) só abre com TPM 2.0 e Secure Boot ligados."
            : "No Windows 10, o Valorant roda sem TPM. O TPM 2.0 é necessário para o Windows 11 e para alguns jogos novos com anti-cheat mais rígido.";
        if (s.Tpm is { Present: true } old && old.Version is > 0 and < 2)
            yield return new(C, win11 ? HealthLevel.Atencao : HealthLevel.Info, $"TPM {old.Version:0.0} encontrado (versão antiga)",
                "O Windows 11 e o Vanguard pedem TPM 2.0. Alguns módulos e BIOS permitem atualizar o firmware do TPM de 1.2 para 2.0; procure no site do fabricante da placa.");
        switch (route)
        {
            case TpmRoute.JaTem:
                yield return new(C, HealthLevel.Ok, $"TPM {s.Tpm!.Version:0.0} ligado", $"Fabricante: {(s.Tpm.Maker.Length > 0 ? s.Tpm.Maker : "não informado")}. Atende ao Windows 11 e ao Vanguard.");
                break;
            case TpmRoute.LigarNaBios:
                yield return new(C, HealthLevel.Atencao, "TPM 2.0 presente, mas desligado", needsNote,
                    "Ligue o TPM na BIOS (Intel PTT, AMD fTPM ou \"Security Device Support\") e depois abra tpm.msc para conferir \"pronto para uso\".");
                break;
            case TpmRoute.FirmwareAmd:
                yield return new(C, win11 ? HealthLevel.Atencao : HealthLevel.Info, "Sem TPM ativo, mas o processador tem TPM embutido (AMD fTPM)",
                    needsNote + " Não precisa comprar nada.",
                    "Na BIOS, procure \"AMD fTPM\", \"AMD CPU fTPM\" ou \"Firmware TPM\" (em geral em Advanced ou Security) e mude para Enabled.");
                break;
            case TpmRoute.FirmwareIntel:
                yield return new(C, win11 ? HealthLevel.Atencao : HealthLevel.Info, "Sem TPM ativo, mas o processador tem TPM embutido (Intel PTT)",
                    needsNote + " Não precisa comprar nada.",
                    "Na BIOS, procure \"Intel Platform Trust Technology (PTT)\" ou \"Firmware TPM\" (em geral em Security ou Advanced > PCH-FW) e mude para Enabled.");
                break;
            case TpmRoute.ModuloNoConector:
                yield return new(C, win11 ? HealthLevel.Atencao : HealthLevel.Info, "Sem TPM: esta plataforma não tem TPM embutido",
                    $"{cpu} é de uma geração sem TPM no firmware. Só funciona com um módulo TPM 2.0 físico encaixado no conector TPM da placa-mãe. {needsNote}",
                    "Confira no manual se a placa tem conector TPM e qual o padrão de pinos (14-1, 18-1 ou 20-1). Compre um módulo TPM 2.0 do mesmo padrão e, de preferência, da mesma marca da placa.");
                break;
            case TpmRoute.SemConector:
                yield return new(C, win11 ? HealthLevel.Atencao : HealthLevel.Info, "Sem TPM, e esta placa provavelmente não aceita um",
                    $"{cpu} não tem TPM embutido, e placas X99 de fabricante chinês quase nunca têm conector TPM. {needsNote}",
                    "Confira se há um conector chamado TPM ou JTPM na placa (perto dos conectores do painel frontal). Se não houver, o caminho seguro é trocar a plataforma. Existem guias para adaptar um módulo com BIOS modificada e solda, mas há risco real de inutilizar a placa.");
                break;
            default:
                if (s.Tpm is null)
                    yield return new(C, HealthLevel.Info, "Nenhum TPM encontrado", needsNote, "Veja no manual da placa se há TPM no firmware (Intel PTT, AMD fTPM) ou conector para módulo.");
                break;
        }

        // ── UEFI e Secure Boot
        if (s.Uefi == false)
            yield return new(C, win11 ? HealthLevel.Atencao : HealthLevel.Info, "Windows iniciado em modo Legacy (CSM)",
                "O Secure Boot só funciona em modo UEFI, com o disco do Windows em GPT" + (s.SystemDiskGpt == false ? " (o seu está em MBR)." : "."),
                "Faça backup. Converta o disco com \"mbr2gpt /convert /allowFullOS\" (Prompt como Administrador) e, em seguida, troque a BIOS para UEFI e desligue o CSM. Se a BIOS continuar em Legacy depois da conversão, o Windows não inicia até você trocar.");
        else if (s.Uefi == true && s.SecureBoot == false)
            yield return new(C, win11 ? HealthLevel.Atencao : HealthLevel.Info, "Secure Boot desligado",
                (win11 ? "O Valorant não abre no Windows 11 sem Secure Boot. " : "") + "Alguns jogos novos também exigem.",
                "Na BIOS (Boot ou Security), mude Secure Boot para Enabled. Se pedir chaves, use \"Restore Factory Keys\" / \"Install default keys\". Desligue o CSM.");
        else if (s.SecureBoot == true)
            yield return new(C, HealthLevel.Ok, "Secure Boot ligado", "O Windows inicia só com carregadores assinados.");

        // ── Windows 11
        if (!win11 && Windows11Cpu(cpu) == false)
            yield return new(C, HealthLevel.Info, "Processador fora da lista do Windows 11",
                $"Mesmo com TPM 2.0, o Windows 11 não aceita oficialmente o {cpu}. O Windows 10 continua recebendo correções de segurança pelo programa ESU (inscrição em Configurações > Windows Update) até 12/10/2027.",
                "Para ir ao Windows 11 com suporte oficial, a troca de plataforma resolve CPU, TPM e Secure Boot de uma vez.");
    }
}
