using System.Windows;
using System.Windows.Controls;

namespace Snow.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    private void GoToOptimize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow w) w.NavOpt.IsChecked = true;
    }
}
