using System.Text;
using Snow.Core.Games;
using Snow.Core.Tweaks;
using Snow.Platform.Executors;

namespace Snow.Platform.Games;

/// <summary>
/// Edita uma chave de arquivo INI/CFG de jogo preservando o resto do arquivo.
/// Exige o jogo fechado (verificação só pelo nome do processo) e não força arquivos somente leitura.
/// </summary>
public sealed class IniValueExecutor(IProcessProbe processes) : IActionExecutor
{
    public string Name => "ini";
    public bool CanHandle(TweakAction a) => a is IniValueAction;

    /// <summary>Lê o arquivo; um BOM (UTF-8 ou UTF-16) vira '\uFEFF' no início do texto para ser preservado.</summary>
    public static string ReadText(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return "\uFEFF" + Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return "\uFEFF" + Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return Encoding.UTF8.GetString(b);
    }

    private static void WriteText(string path, string text, bool utf16)
    {
        var bom = text.Length > 0 && text[0] == '\uFEFF';
        var body = bom ? text[1..] : text;
        byte[] data = utf16
            ? [.. (bom ? new byte[] { 0xFF, 0xFE } : []), .. Encoding.Unicode.GetBytes(body)]
            : [.. (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. new UTF8Encoding(false).GetBytes(body)];
        // Escrita atômica: grava ao lado e substitui, para nunca deixar o arquivo do jogo pela metade.
        var tmp = path + ".snow-tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    public ActionSnapshot Capture(TweakAction action)
    {
        var a = (IniValueAction)action;
        string? old = null;
        if (File.Exists(a.FilePath)) old = IniDocument.Parse(ReadText(a.FilePath)).Get(a.Section, a.Key);
        return new(Name, a.Describe(), new()
        {
            ["file"] = a.FilePath,
            ["section"] = a.Section,
            ["key"] = a.Key,
            ["old"] = old,
        });
    }

    public ActionOutcome Apply(TweakAction action)
    {
        var a = (IniValueAction)action;
        if (processes.IsRunning(a.BlockingProcesses))
            throw new SkipException("O jogo (ou o launcher dele) está aberto. Feche e aplique de novo.");
        if (!File.Exists(a.FilePath))
            throw new SkipException("Arquivo de configuração não encontrado: abra o jogo uma vez para criá-lo.");
        if (File.GetAttributes(a.FilePath).HasFlag(FileAttributes.ReadOnly))
            throw new SkipException("Arquivo marcado como somente leitura: o Snow não força a alteração. Desmarque nas propriedades do arquivo.");
        Write(a.FilePath, a.Section, a.Key, a.Value);
        return new(false, "ok");
    }

    public bool? Verify(TweakAction action)
    {
        var a = (IniValueAction)action;
        if (!File.Exists(a.FilePath)) return null;
        return IniDocument.Parse(ReadText(a.FilePath)).Get(a.Section, a.Key) == a.Value;
    }

    public void Revert(ActionSnapshot s)
    {
        var file = s.Data["file"]!;
        if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)) return;
        Write(file, s.Data["section"]!, s.Data["key"]!, s.Data["old"]);
    }

    private static void Write(string path, string section, string key, string? value)
    {
        var head = new byte[2];
        using (var fs = File.OpenRead(path)) _ = fs.Read(head, 0, 2);
        var utf16 = head[0] == 0xFF && head[1] == 0xFE;
        var doc = IniDocument.Parse(ReadText(path));
        doc.Set(section, key, value);
        WriteText(path, doc.ToString(), utf16);
    }
}
