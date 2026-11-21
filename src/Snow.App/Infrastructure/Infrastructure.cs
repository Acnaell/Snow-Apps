using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Snow.Platform.Executors;

namespace Snow.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public enum AppTheme { Dark, Light }

/// <summary>
/// Troca o dicionário de cores em tempo de execução e escurece a barra de título nativa.
/// Padrão: escuro. Se o usuário nunca escolheu, segue o tema de apps do Windows.
/// </summary>
public static class ThemeManager
{
    private const string PrefKey = "theme";
    private static readonly JsonStateStore Prefs = new();

    public static AppTheme Current { get; private set; } = AppTheme.Dark;
    public static event Action? Changed;

    public static AppTheme Initial()
    {
        var saved = Prefs.Get(PrefKey);
        if (Enum.TryParse<AppTheme>(saved, out var t)) return t;
        // Sem preferência salva: escuro, a menos que o Windows esteja explicitamente no tema claro.
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int light && light == 1) return AppTheme.Light;
        }
        catch { /* sem acesso: mantém escuro */ }
        return AppTheme.Dark;
    }

    public static void Apply(AppTheme theme, bool persist = false)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/Snow;component/Themes/{theme}.xaml", UriKind.Absolute);
        var existing = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Dark.xaml") == true
                                              || d.Source?.OriginalString.Contains("/Themes/Light.xaml") == true);
        var fresh = new ResourceDictionary { Source = uri };
        if (existing is null) dicts.Insert(0, fresh);
        else dicts[dicts.IndexOf(existing)] = fresh;

        Current = theme;
        if (persist) Prefs.Set(PrefKey, theme.ToString());
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, persist: true);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Win10 20H1+/Win11); 19 nas builds 1809–1909.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = Current == AppTheme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }
}
