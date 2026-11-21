using System.Windows;
using System.Windows.Controls;

namespace Snow.App.Views;

public sealed record PlannedFeature(string Icon, string Title, string Detail);

/// <summary>Página dos módulos ainda em desenvolvimento do hub (Snow Health, Snow Monitor).</summary>
public partial class ComingSoonView : UserControl
{
    public ComingSoonView() => InitializeComponent();

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ComingSoonView));

    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ComingSoonView));

    public string Footer { get => (string)GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(string), typeof(ComingSoonView));

    public IReadOnlyList<PlannedFeature> Items { get => (IReadOnlyList<PlannedFeature>)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<PlannedFeature>), typeof(ComingSoonView));

    public static IReadOnlyList<PlannedFeature> HealthItems { get; } =
    [
        new("", "Saúde dos discos", "SMART: horas ligado, desgaste e vida útil restante do SSD, setores realocados no HDD."),
        new("", "Bateria do notebook", "Capacidade atual vs. original e ciclos de carga (relatório oficial do Windows)."),
        new("", "Confiabilidade do Windows", "Travamentos, telas azuis e apps que fecham sozinhos nos últimos 30 dias."),
        new("", "Drivers e atualizações", "Driver de vídeo desatualizado, dispositivos com erro e atualizações pendentes."),
        new("", "Espaço e limpeza", "O que ocupa o disco e o que pode ser removido com segurança."),
        new("", "Nota de saúde do PC", "Um resumo de 0 a 100 com o que fazer primeiro."),
    ];

    public static IReadOnlyList<PlannedFeature> MonitorItems { get; } =
    [
        new("", "Uso em tempo real", "CPU, GPU, RAM, disco e rede pelos contadores de desempenho do próprio Windows."),
        new("", "FPS e frametime", "Medição de quadros por ETW (técnica do PresentMon), sem injetar nada no jogo."),
        new("", "Sessões de jogo", "Grava cada partida: FPS médio, 1% low, engasgos e o que estava pesando."),
        new("", "Gargalo", "Indica se o limite é CPU, GPU, RAM ou disco em cada momento."),
        new("", "Antes e depois", "Compara o desempenho antes e depois das otimizações do Snow Gaming."),
        new("", "Seguro para anti-cheat", "Sem drivers de kernel (como o WinRing0, bloqueado pelo Vanguard)."),
    ];
}
