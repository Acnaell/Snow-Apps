using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snow.App.Infrastructure;
using Snow.App.ViewModels;

namespace Snow.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            await _vm.RefreshAsync();
            if (App.Options.ScreenshotDir is { } dir) await CaptureAllPagesAsync(dir);
        };
    }

    /// <summary>
    /// Modo de verificação (CI): renderiza cada página em PNG e fecha o app.
    /// Prova que o XAML, os temas e os bindings carregam de verdade num Windows real.
    /// </summary>
    private async Task CaptureAllPagesAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var pages = new (RadioButton nav, string name)[]
        {
            (NavDiag, "1-diagnostico"), (NavOpt, "2-otimizar"), (NavGames, "3-jogos"), (NavStartup, "4-inicializacao"),
            (NavHealth, "5-health"), (NavMonitor, "6-monitor"), (NavRestore, "7-restaurar"), (NavLog, "8-log"),
        };
        foreach (var (nav, name) in pages)
        {
            nav.IsChecked = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(Root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(dir, $"{name}-{ThemeManager.Current.ToString().ToLowerInvariant()}.png"));
            enc.Save(fs);
        }
        Close();
    }
}
