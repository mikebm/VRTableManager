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

    /// <summary>Changes a setting and saves the settings file (a Setup field changed).</summary>
    protected void SaveSetting(Action<AppSettings> change)
    {
        change(Settings);
        Settings.Save();
    }

    /// <summary>Shows what the settings say (at startup, and after a backup is restored).</summary>
    internal virtual void LoadSettings(AppSettings s) { }
}
