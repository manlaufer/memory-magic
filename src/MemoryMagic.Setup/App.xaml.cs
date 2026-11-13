using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MemoryMagic.Setup;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemoryMagic",
        "Setup.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Log("Setup starting...");

        try
        {
            base.OnStartup(e);
            string? initialDir = ParseArgValue(e.Args, "--install-dir");
            var window = new SetupWindow(initialDir);
            MainWindow = window;
            window.Show();
            Log("SetupWindow shown.");
        }
        catch (Exception ex)
        {
            LogException("Startup failure", ex);
            ShowFatalError(ex);
            Shutdown(1);
        }
    }

    private static string? ParseArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("Dispatcher unhandled exception", e.Exception);
        ShowFatalError(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("AppDomain unhandled exception", ex);
            ShowFatalError(ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    internal static void Log(string message)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    internal static void LogException(string context, Exception ex)
    {
        Log($"ERROR ({context}): {ex}");
    }

    private static void ShowFatalError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"Memory Magic Setup encountered an unexpected error:\n\n{ex.Message}\n\nDetails were written to:\n{LogPath}",
                "Memory Magic Setup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
        }
    }
}
