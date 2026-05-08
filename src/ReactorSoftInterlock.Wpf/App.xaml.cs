using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ReactorSoftInterlock.Wpf;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();

        try
        {
            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            WriteStartupLog("OnStartup", ex);
            MessageBox.Show(
                $"Application startup failed.\n\n{ex.Message}\n\nA startup log was written next to the executable or to the Windows temp folder.",
                "ReactorSoftInterlock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupLog("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"Unexpected UI error.\n\n{e.Exception.Message}\n\nDetails were written to startup-error.log.",
            "ReactorSoftInterlock",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        WriteStartupLog("CurrentDomainUnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception."));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteStartupLog("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void WriteStartupLog(string phase, Exception ex)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[{DateTimeOffset.Now:O}] {phase}");
        builder.AppendLine(ex.ToString());
        builder.AppendLine();

        foreach (var path in GetStartupLogPaths())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
                break;
            }
            catch
            {
                // Try next path.
            }
        }
    }

    private static IEnumerable<string> GetStartupLogPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "startup-error.log");
        yield return Path.Combine(Path.GetTempPath(), "ReactorSoftInterlock-startup-error.log");
    }
}
