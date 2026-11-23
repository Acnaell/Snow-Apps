using Snow.Core.Feedback;

namespace Snow.Tests;

public static class FeedbackTests
{
    [Test] public static void BuildsPrefilledUrl()
    {
        var url = FeedbackForm.BuildUrl(9, "Muita diferença", ["Otimizar o Windows", "inventado"],
            ["Mostrar melhor o progresso (limpeza, melhorias aplicadas)", "Ganho de FPS maior"], "Ótimo & rápido", "Snow 0.12.0 · Windows 10 19045");
        Assert.True(url.StartsWith(FeedbackForm.Url + "?usp=pp_url&entry.1861790430=9&"), url);
        Assert.True(url.Contains("entry.221623890=Muita%20diferen%C3%A7a"));
        Assert.True(url.Contains("entry.1131368306=Otimizar%20o%20Windows") && !url.Contains("inventado"), "só opções do formulário");
        Assert.Equal(2, url.Split("entry.667646281=").Length - 1, "duas dicas marcadas");
        Assert.True(url.Contains("entry.339867655=%C3%93timo%20%26%20r%C3%A1pido"), "comentário escapado");
        Assert.True(url.Contains("entry.395708095=Snow%200.12.0"));
        Assert.False(FeedbackForm.BuildUrl(null, null, [], [], null, "x").Contains("entry.1861790430"), "sem nota, sem campo");
        Assert.True(FeedbackForm.Band(10).StartsWith("Fã") && FeedbackForm.Band(7).StartsWith("Neutro") && FeedbackForm.Band(3).StartsWith("Crítico"));
    }
}
