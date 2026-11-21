namespace Snow.Core.Games;

/// <summary>
/// Editor de INI que preserva tudo o que não foi alterado (comentários, ordem, linhas
/// desconhecidas, cabeçalho ";METADATA" do Unreal, quebras de linha CRLF).
/// Serve para GameUserSettings.ini (Unreal) e game.cfg (League of Legends).
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly bool _bom;

    private IniDocument(List<string> lines, string newline, bool bom)
    {
        _lines = lines;
        _newline = newline;
        _bom = bom;
    }

    public static IniDocument Parse(string text)
    {
        var bom = text.Length > 0 && text[0] == '﻿';
        if (bom) text = text[1..];
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        return new IniDocument(lines, newline, bom);
    }

    public override string ToString() => (_bom ? "﻿" : "") + string.Join(_newline, _lines);

    private static bool IsSection(string line, out string name)
    {
        var t = line.Trim();
        if (t.Length > 2 && t[0] == '[' && t[^1] == ']') { name = t[1..^1]; return true; }
        name = "";
        return false;
    }

    private static bool IsKey(string line, string key, out int eq)
    {
        eq = line.IndexOf('=');
        if (eq <= 0) return false;
        var k = line[..eq].Trim();
        return k.Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Faixa [início, fim) das linhas da seção (sem o cabeçalho). -1 se não existir.</summary>
    private (int start, int end) SectionRange(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!IsSection(_lines[i], out var name) || !name.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var end = i + 1;
            while (end < _lines.Count && !IsSection(_lines[end], out _)) end++;
            return (i + 1, end);
        }
        return (-1, -1);
    }

    public IReadOnlyList<string> Sections =>
        _lines.Select(l => IsSection(l, out var n) ? n : null).Where(n => n is not null).Cast<string>().ToList();

    public bool HasSection(string section) => SectionRange(section).start >= 0;

    public string? Get(string section, string key)
    {
        var (start, end) = SectionRange(section);
        if (start < 0) return null;
        for (var i = start; i < end; i++)
            if (IsKey(_lines[i], key, out var eq)) return _lines[i][(eq + 1)..].Trim();
        return null;
    }

    /// <summary>Define o valor; cria a chave (ou a seção, no fim do arquivo) se não existir. null remove a chave.</summary>
    public void Set(string section, string key, string? value)
    {
        var (start, end) = SectionRange(section);
        if (start < 0)
        {
            if (value is null) return;
            // garante uma linha em branco antes da nova seção
            while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
            if (_lines.Count > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            _lines.Add("");
            return;
        }
        for (var i = start; i < end; i++)
        {
            if (!IsKey(_lines[i], key, out var eq)) continue;
            if (value is null) _lines.RemoveAt(i);
            else _lines[i] = _lines[i][..(eq + 1)] + value;
            return;
        }
        if (value is null) return;
        // insere depois da última linha não vazia da seção
        var insertAt = end;
        while (insertAt > start && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, $"{key}={value}");
    }
}
