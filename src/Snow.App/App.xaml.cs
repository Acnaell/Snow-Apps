using System.Windows;
using System.Windows.Threading;
using Snow.App.Infrastructure;
using Snow.Platform.Services;

namespace Snow.App;

public sealed record StartupOptions(string? ScreenshotDir, AppTheme? Theme)
{
    public static StartupOptions Parse(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        return new(Arg("--screenshot"), Enum.TryParse<AppTheme>(Arg("--theme"), true, out var t) ? t : null);
    }
}

public partial class App : Application
{
    public static StartupOptions Options { get; private set; } = new(null, null);

    protected override void OnStartup(StartupEventArgs e)
    {
        Options = StartupOptions.Parse(e.Args);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => FileLog.Instance.Error("Falha fatal: " + a.ExceptionObject);
        base.OnStartup(e);

        ThemeManager.Apply(Options.Theme ?? ThemeManager.Initial());

        if (!Elevation.IsAdministrator() && Options.ScreenshotDir is null)
        {
            MessageBox.Show("O Snow precisa ser executado como Administrador para alterar energia, serviços e registro do sistema.\n\n" +
                            "Clique com o botão direito em Snow.exe → Executar como administrador.",
                "Snow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLog.Instance.Error("Erro não tratado: " + e.Exception);
        if (Options.ScreenshotDir is not null) { Console.Error.WriteLine(e.Exception); Current.Shutdown(10); return; }
        MessageBox.Show($"Ocorreu um erro inesperado:\n{e.Exception.Message}\n\nDetalhes foram gravados no log.", "Snow",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
