using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VrTableManager.Services;
using VrTableManager.Views;

namespace VrTableManager;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        // The safety net: an error nothing else caught is logged and shown, and the app keeps running.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception);
        };
        // A background task that failed with nobody waiting for it (found when it's garbage-collected).
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Report(e.Exception);
        };

        base.OnFrameworkInitializationCompleted();
    }

    static string ErrorLog => Path.Combine(AppSettings.DataDir, "errors.log");

    bool _showingError;

    /// <summary>Writes the error to errors.log and shows it (one dialog at a time; the rest are only logged).</summary>
    void Report(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(
                ErrorLog,
                $"{DateTime.Now:s} {ex}{Environment.NewLine}{Environment.NewLine}"
            );
        }
        catch
        { /* the dialog still says what happened */
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (
                _showingError
                || (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                    is not MainWindow { IsVisible: true } window
            )
            {
                return;
            }

            _showingError = true;
            try
            {
                var inner = ex is AggregateException { InnerExceptions.Count: 1 } agg
                    ? agg.InnerExceptions[0]
                    : ex;
                await window.ChoiceAsync(
                    "Something went wrong",
                    $"{inner.Message}\n\nVR Table Manager keeps running; what you were doing may not have finished. "
                        + $"The details are saved in {ErrorLog}.",
                    ["OK"],
                    ex.ToString()
                );
            }
            catch
            { /* the window is closing */
            }
            finally
            {
                _showingError = false;
            }
        });
    }
}
