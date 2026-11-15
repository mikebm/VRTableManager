using CommunityToolkit.Mvvm.ComponentModel;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

/// <summary>
/// A screen's or Setup section's view model. It reaches the settings, the window's dialogs and the other view models
/// through <see cref="App"/>, which views also bind through for the odd value another view model owns.
/// </summary>
public abstract class ChildViewModel(MainViewModel app) : ObservableObject
{
    public MainViewModel App { get; } = app;

    protected AppSettings Settings => App.Settings;

    protected IUiHost Ui => App.Ui;

    /// <summary>
    /// Changes a setting and saves the settings file (a Setup field changed). Called from a bound property's setter,
    /// where a failure would be swallowed by the binding (the box stays ticked, the file keeps the old value), so a
    /// failed save is thrown again on the UI thread, where the app logs it to errors.log and shows it.
    /// </summary>
    protected void SaveSetting(Action<AppSettings> change)
    {
        change(Settings);
        try
        {
            Settings.Save();
        }
        catch (Exception e)
        {
            var path = Settings.SourcePath ?? AppSettings.DefaultPath;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                throw new IOException($"Your change couldn't be saved to {path}: {e.Message}", e)
            );
        }
    }

    /// <summary>Shows what the settings say (at startup, and after a backup is restored).</summary>
    internal virtual void LoadSettings(AppSettings s) { }
}
