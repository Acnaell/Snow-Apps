using System.Text.RegularExpressions;
using Snow.Core.Hardware;
using Snow.Core.Tweaks;

namespace Snow.Core.Games;

/// <summary>
/// OBS Studio (lives e gravação). Não é jogo, mas disputa CPU/GPU com ele.
/// Lê o perfil ativo (user.ini no OBS 31+, global.ini antes) e o basic.ini do perfil.
/// Edita só o basic.ini, com o OBS fechado: codificador (modo Simples), resolução de saída e FPS.
/// No modo Avançado o codificador fica em streamEncoder.json: aí o Snow só orienta.
/// Predefinições: FPS Alto = jogo em primeiro lugar (720p60); FPS Médio = live mais nítida (936p60).
/// </summary>
public sealed partial class ObsProfile : IGameProfile
{
    public const string Id = "app:obs";
    public string GameId => Id;
    public GameSupport Support => GameSupport.Editavel;
    public IReadOnlyList<string> Processes { get; } = ["obs64", "obs32", "obs"];

    private string? _root;
    public static string DefaultRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
    public ObsProfile WithRoot(string root) { _root = root; return this; }
    private string Root => _root ??= DefaultRoot();

    public IReadOnlyList<string> ConfigFiles(InstalledGame game) =>
        [Path.Combine(Root, "user.ini"), Path.Combine(Root, "global.ini"), Path.Combine(Root, "basic", "profiles", "*", "basic.ini")];

    /// <summary>basic.ini do perfil ativo (pelo ProfileDir do user.ini/global.ini; senão, o único perfil existente).</summary>
    public string? ActiveBasicIni(GameContext ctx)
    {
        var profilesDir = Path.Combine(Root, "basic", "profiles");
        foreach (var f in new[] { "user.ini", "global.ini" })
        {
            if (ctx.Text(Path.Combine(Root, f)) is not { } t) continue;
            if (IniDocument.Parse(t).Get("Basic", "ProfileDir") is { Length: > 0 } dir)
            {
                var p = Path.Combine(profilesDir, dir, "basic.ini");
                if (ctx.Files.ContainsKey(p)) return p;
            }
        }
        var all = ctx.Files.Keys.Where(k => k.EndsWith("basic.ini", StringComparison.OrdinalIgnoreCase)).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    private const string Out = "Output", Simple = "SimpleOutput", Adv = "AdvOut", Video = "Video";

    [GeneratedRegex(@"\b(GT\s?(7[1-4]0|10[13]0)|MX\s?\d{3})\b", RegexOptions.IgnoreCase)] private static partial Regex NoNvenc();

    /// <summary>Valor do codificador por hardware no modo Simples do OBS para a GPU deste PC (null se não houver).</summary>
    public static string? HardwareEncoder(HardwareProfile hw)
    {
        // a placa principal primeiro: é nela que o jogo roda e a captura acontece
        var gpus = hw.Gpus.Where(g => !g.IsVirtual).OrderByDescending(g => g == hw.PrimaryGpu).ToList();
        foreach (var g in gpus)
        {
            if (g.Vendor == GpuVendor.Nvidia && !NoNvenc().IsMatch(g.Name)) return "nvenc";
            if (g.Vendor == GpuVendor.Amd) return "amd";
            if (g.Vendor == GpuVendor.Intel) return "qsv";
        }
        return null;
    }

    private static string EncoderLabel(string? v) => v?.ToLowerInvariant() switch
    {
        "x264" or "obs_x264" => "x264 (processador)",
        "x264_lowcpu" => "x264 baixo uso (processador)",
        "amd" or "h264_texture_amf" or "amd_amf_h264" => "AMD AMF H.264 (placa de vídeo)",
        "nvenc" or "jim_nvenc" or "obs_nvenc_h264_tex" => "NVIDIA NVENC H.264 (placa de vídeo)",
        "qsv" or "obs_qsv11" or "obs_qsv11_v2" => "Intel Quick Sync (placa de vídeo)",
        null or "" => "padrão (x264, processador)",
        _ => v!,
    };

    private static bool IsCpuEncoder(string? v) => v is null or "" || v.Contains("x264", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<GameFinding> Analyze(GameContext ctx)
    {
        var list = new List<GameFinding>();
        if (ActiveBasicIni(ctx) is not { } path || ctx.Text(path) is not { } text)
        {
            list.Add(new(FindingLevel.Info, "Perfil do OBS não encontrado", "Abra o OBS uma vez e feche para ele criar o perfil."));
            return list;
        }
        var ini = IniDocument.Parse(text);
        var advanced = string.Equals(ini.Get(Out, "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase);
        var enc = advanced ? ini.Get(Adv, "Encoder") : ini.Get(Simple, "StreamEncoder");
        var hwEnc = HardwareEncoder(ctx.Hardware);

        if (IsCpuEncoder(enc) && hwEnc is not null)
            list.Add(new(FindingLevel.Importante, $"Live codificada pelo processador ({EncoderLabel(enc)})",
                $"Em jogo, o processador já é o limite do FPS. O x264 disputa os mesmos núcleos e derruba o FPS do jogo e da live. Sua placa tem codificador próprio ({EncoderLabel(hwEnc)}), que quase não custa FPS.",
                !advanced));
        else if (!IsCpuEncoder(enc))
            list.Add(new(FindingLevel.Ok, $"Codificador: {EncoderLabel(enc)}", "Codificação pela placa de vídeo: quase não afeta o FPS do jogo."));

        if (int.TryParse(ini.Get(Video, "OutputCY"), out var outH) && int.TryParse(ini.Get(Video, "BaseCY"), out var baseH))
        {
            var fps = Fps(ini);
            if (outH > 936 && fps >= 50)
                list.Add(new(FindingLevel.Melhoria, $"Live em {outH}p a {fps} FPS",
                    "1080p60 a ~6000 kbps fica com blocos em cenas rápidas e pesa mais na GPU. 936p ou 720p a 60 FPS dá uma imagem mais limpa com a mesma banda.", true));
            else
                list.Add(new(FindingLevel.Ok, $"Saída em {outH}p a {fps} FPS", outH < baseH ? "Reduzida a partir da tela: bom equilíbrio para live." : "Resolução da live adequada."));
        }

        if (!advanced && int.TryParse(ini.Get(Simple, "VBitrate"), out var kbps) && kbps < 4500)
            list.Add(new(FindingLevel.Melhoria, $"Taxa de bits baixa ({kbps} kbps)", "Jogos rápidos como Fortnite precisam de ~6000 kbps a 60 FPS para não borrar.", true));

        if (advanced)
            list.Add(new(FindingLevel.Info, "Modo de saída Avançado", "Nesse modo o codificador fica em outro arquivo: o Snow ajusta resolução e FPS e mostra o passo a passo do codificador."));
        return list;
    }

    private static int Fps(IniDocument ini) =>
        ini.Get(Video, "FPSType") is "0" or null && int.TryParse(ini.Get(Video, "FPSCommon"), out var f) ? f
        : int.TryParse(ini.Get(Video, "FPSInt"), out var i) ? i : 30;

    public GamePresetPlan BuildPreset(GameContext ctx, GamePreset preset)
    {
        var steps = new List<GuidedStep>
        {
            new("Fontes da cena", "Captura do jogo", "Captura de jogo (não Captura de tela)", "Captura só o jogo, com menos custo e sem pegar notificações da área de trabalho."),
            new("Atalho do OBS → Propriedades → Compatibilidade", "Executar como administrador", "Ligado", "O OBS consegue reservar a placa de vídeo para a live quando o jogo usa a GPU no máximo (evita 'quadros perdidos por renderização')."),
            new("Durante a live", "Estatísticas (Exibir → Estatísticas)", "Quadros perdidos ≈ 0%", "Se subirem 'por renderização', limite o FPS do jogo um pouco abaixo (ex.: 144) para deixar folga para a GPU."),
        };
        if (ActiveBasicIni(ctx) is not { } path || ctx.Text(path) is not { } text)
            return new(preset, [], [], steps, "OBS · perfil não encontrado: abra e feche o OBS uma vez.");

        var ini = IniDocument.Parse(text);
        var advanced = string.Equals(ini.Get(Out, "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase);
        var hwEnc = HardwareEncoder(ctx.Hardware);
        var changes = new List<(string s, string k, string v, string label, string why)>();
        void C(string s, string k, string v, string label, string why) => changes.Add((s, k, v, label, why));

        int.TryParse(ini.Get(Video, "BaseCX"), out var baseW);
        int.TryParse(ini.Get(Video, "BaseCY"), out var baseH);
        if (baseW <= 0 || baseH <= 0) { baseW = 1920; baseH = 1080; }
        var targetH = preset == GamePreset.FpsAlto || ctx.Tier == HardwareTier.Entrada ? 720 : 936;
        if (baseH > targetH)
        {
            // mantém a proporção da tela, com largura par (exigência dos codificadores)
            var w = (int)Math.Round(baseW * (double)targetH / baseH / 2) * 2;
            C(Video, "OutputCX", w.ToString(), "Largura da live", $"{w}×{targetH}: proporção da sua tela");
            C(Video, "OutputCY", targetH.ToString(), "Altura da live", preset == GamePreset.FpsAlto ? "720p: mais leve, jogo em primeiro lugar" : "936p: mais nítido com ~6000 kbps");
        }
        C(Video, "FPSType", "0", "Tipo de FPS", "Valor comum");
        C(Video, "FPSCommon", "60", "FPS da live", "60 FPS: movimento fluido em jogos rápidos");

        if (!advanced)
        {
            if (hwEnc is not null)
                C(Simple, "StreamEncoder", hwEnc, "Codificador da live", $"{EncoderLabel(hwEnc)}: tira a codificação do processador");
            if (!int.TryParse(ini.Get(Simple, "VBitrate"), out var kbps) || kbps < 6000)
                C(Simple, "VBitrate", "6000", "Taxa de bits (kbps)", "6000: o máximo recomendado pela Twitch para 60 FPS");
        }
        else if (hwEnc is not null)
        {
            steps.Insert(0, new("Configurações → Saída → Transmissão", "Codificador de vídeo", EncoderLabel(hwEnc), "Codificação pela placa de vídeo, sem pesar no processador."));
            steps.Insert(1, new("Configurações → Saída → Transmissão", "Controle de taxa / Taxa de bits", "CBR · 6000 kbps · intervalo de quadro-chave 2 s", "Padrão das plataformas de live."));
        }

        var setting = changes.Select(c => new SettingChange(c.label, ini.Get(c.s, c.k), c.v, c.why)).ToList();
        var actions = changes.Select(c => (TweakAction)new IniValueAction(path, c.s, c.k, c.v, Processes)).ToList();
        var tweak = new TweakDefinition
        {
            Id = $"app.obs.{preset.ToString().ToLowerInvariant()}",
            Title = $"OBS: {(preset == GamePreset.FpsAlto ? "jogo em primeiro lugar" : "live mais nítida")}",
            Description = "Ajusta o basic.ini do perfil ativo do OBS (mesmas opções de Configurações → Saída/Vídeo).",
            HonestNote = "Feche o OBS antes. O original fica no backup (aba Restaurar).",
            Category = TweakCategory.Graficos,
            Impact = Impact.Alto,
            Actions = actions,
        };
        var label = preset == GamePreset.FpsAlto ? "jogo em primeiro lugar (720p60)" : $"live mais nítida ({targetH}p60)";
        return new(preset, setting, [tweak], steps, $"OBS · {label} · {EncoderLabel(hwEnc)}{(advanced ? " · modo Avançado: codificador no passo a passo" : "")}");
    }
}
