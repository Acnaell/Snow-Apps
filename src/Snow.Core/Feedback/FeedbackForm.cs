namespace Snow.Core.Feedback;

/// <summary>
/// Formulário "Avaliação SNOW APP" (Google Forms). O app abre o formulário já preenchido no navegador;
/// a pessoa revisa e clica em Enviar lá. Os textos das opções precisam ser idênticos aos do formulário.
/// </summary>
public static class FeedbackForm
{
    public const string Url = "https://docs.google.com/forms/d/e/1FAIpQLSdpVu9ClZNLeHbL3-hfgmBwOg-yMQaCGW9KEL8oQtdpEqPGvw/viewform";

    private const string Score = "entry.1861790430", Difference = "entry.221623890", Parts = "entry.1131368306",
                         Improve = "entry.667646281", Comment = "entry.339867655", System = "entry.395708095";

    public static readonly IReadOnlyList<string> DifferenceOptions =
        ["Muita diferença", "Alguma diferença", "Nenhuma diferença", "Piorou", "Ainda não testei em jogos"];

    public static readonly IReadOnlyList<string> PartOptions =
    [
        "Otimizar o Windows", "Ajustes dos jogos", "Inicialização do Windows", "Saúde do PC", "Limpeza e manutenção",
        "Processos em segundo plano", "Desempenho (monitor de FPS)", "Pontuação e estresse", "Roda no meu PC?",
    ];

    /// <summary>As "dicas" de melhoria mostradas no app (mesmos textos do formulário).</summary>
    public static readonly IReadOnlyList<string> ImproveOptions =
    [
        "Mostrar melhor o progresso (limpeza, melhorias aplicadas)", "Explicações mais simples", "Mais jogos com ajustes automáticos",
        "Visual e organização das telas", "App mais rápido para abrir e analisar", "Aviso do Windows de \"app desconhecido\" ao instalar",
        "Ganho de FPS maior",
    ];

    /// <summary>Faixa da nota (NPS): 0–6 crítico, 7–8 neutro, 9–10 fã.</summary>
    public static string Band(int score) => score switch
    {
        >= 9 => "Fã: obrigado! Conte o que mais gostou para a gente manter.",
        >= 7 => "Neutro: o que faltou para virar 10?",
        >= 0 => "Crítico: conte o que deu errado. É isso que mais ajuda a melhorar.",
        _ => "",
    };

    public static string BuildUrl(int? score, string? difference, IEnumerable<string> parts, IEnumerable<string> improve, string? comment, string system)
    {
        var q = new List<string> { "usp=pp_url" };
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) q.Add($"{k}={Uri.EscapeDataString(v.Trim())}"); }
        if (score is >= 0 and <= 10) Add(Score, score.ToString());
        if (difference is not null && DifferenceOptions.Contains(difference)) Add(Difference, difference);
        foreach (var p in parts.Where(PartOptions.Contains)) Add(Parts, p);
        foreach (var i in improve)
        {
            if (ImproveOptions.Contains(i)) Add(Improve, i);
        }
        Add(Comment, comment is { Length: > 1500 } ? comment[..1500] : comment);
        Add(System, system);
        return Url + "?" + string.Join("&", q);
    }
}
