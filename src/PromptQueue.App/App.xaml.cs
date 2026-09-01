using System.Windows;
using System.Windows.Threading;
using PromptQueue.App.ViewModels;
using PromptQueue.App.Views;
using PromptQueue.Core.Storage;

namespace PromptQueue.App;

/// <summary>Interaction logic for App.xaml.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        var workspace = Workspace.Load();
        var mainViewModel = new MainViewModel(workspace);

        var window = new MainWindow { DataContext = mainViewModel };
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "zProject",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
