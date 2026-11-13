using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VrTableManager.Services;
using VrTableManager.ViewModels;

namespace VrTableManager.Views;

public partial class MainWindow : Window, IUiHost
{
    readonly MainViewModel _vm;
    bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm = new MainViewModel(this);
        RestorePlacement();
        Opened += async (_, _) =>
        {
            await _vm.WarnIfSettingsUnreadableAsync();
            await _vm.Catalog.LoadCatalogAsync();
        };
        Closing += async (_, e) =>
        {
            // Downloads running? Ask first; if closing anyway, they're offered again next launch.
            var running = _vm.Downloads.RunningJobCount;
            if (!_closeConfirmed && running > 0 && !_vm.Updates.ClosingForUpdate)
            {
                e.Cancel = true;
                var pick = await ChoiceAsync(
                    running == 1
                        ? "A download is still running"
                        : $"{running} downloads are still running",
                    "If you close now they stop, and VR Table Manager offers to start them again the next time you open it.",
                    ["KEEP OPEN", "CLOSE ANYWAY"]
                );
                if (pick == 1)
                {
                    _closeConfirmed = true;
                    Close();
                }
                return;
            }
            SavePlacement();
            _vm.Controls.CancelKeyCapture();
            _vm.Controls.ReleaseControllers(); // SDL shut down before the process ends, not torn down under it
            _vm.PrepareToClose();
            await _vm.DisposeAsync();
        };
    }

    // The window's last normal (not maximized or minimized) spot, and whether it's maximized over it.
    PixelPoint _normalPosition;
    Size _normalSize;
    bool _maximized;

    /// <summary>Opens where the window was when the app last closed, if that spot is still on a screen.</summary>
    void RestorePlacement()
    {
        _normalPosition = Position;
        _normalSize = new(Width, Height);
        if (_vm.Settings.MainWindow is { } p && p.Width >= MinWidth && p.Height >= MinHeight)
        {
            // A monitor unplugged since: only use the spot when its title bar is on a screen now.
            var titleBar = new PixelRect(p.X + 40, p.Y + 8, 200, 24);
            if (Screens.All.Any(s => s.WorkingArea.Intersects(titleBar)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(p.X, p.Y);
                _normalPosition = Position;
            }
            Width = p.Width;
            Height = p.Height;
            _normalSize = new(p.Width, p.Height);
            if (p.Maximized)
            {
                WindowState = WindowState.Maximized;
                _maximized = true;
            }
        }

        // Windows reports the new size and spot before the new state: maximizing resizes while the state still says
        // Normal. Look once the state has caught up, so a maximized or minimized spot is never taken as the normal one.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(SampleNormal);
        Resized += (_, _) => Dispatcher.UIThread.Post(SampleNormal);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized)
            {
                _maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
            }
        };
    }

    void SampleNormal()
    {
        if (WindowState == WindowState.Normal)
        {
            _normalPosition = Position;
            _normalSize = ClientSize;
        }
    }

    void SavePlacement()
    {
        try
        {
            SampleNormal();
            var placement = new WindowPlacement
            {
                X = _normalPosition.X,
                Y = _normalPosition.Y,
                Width = _normalSize.Width,
                Height = _normalSize.Height,
                Maximized = _maximized,
            };
            _vm.Settings.MainWindow = placement;
            // Only the placement: the file can hold newer settings than this copy (a backup restored meanwhile).
            var onDisk = AppSettings.LoadFrom(_vm.Settings.SourcePath ?? AppSettings.DefaultPath);
            onDisk.MainWindow = placement;
            onDisk.Save();
        }
        catch
        { /* next launch opens at the default spot */
        }
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var res = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title }
        );
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFileAsync(
        string title,
        string pattern,
        string? preferredName = null,
        string? startFolder = null
    )
    {
        // pattern: one or more globs separated by ';' ("*.png;*.jpg").
        var all = new FilePickerFileType(
            pattern == "*.zip" ? "All ROM zips (*.zip)" : pattern.Replace(";", ", ")
        )
        {
            Patterns = pattern.Split(';'),
        };
        var options = new FilePickerOpenOptions
        {
            Title = title,
            // The first filter is the one selected when the dialog opens.
            FileTypeFilter =
                preferredName != null
                    ? [new FilePickerFileType(preferredName) { Patterns = [preferredName] }, all]
                    : [all],
        };
        if (startFolder != null && Directory.Exists(startFolder))
        {
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(
                startFolder
            );
        }

        var res = await StorageProvider.OpenFilePickerAsync(options);
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okLabel = "Delete")
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = okLabel, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task<string?> AskTextAsync(
        string title,
        string message,
        string initial,
        string okLabel
    )
    {
        string? result = null;
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Icon,
        };
        var box = new TextBox { Text = initial, MaxLength = 80 };
        var ok = new Button
        {
            Content = okLabel,
            Classes = { "primary" },
            IsDefault = true,
        };
        var cancel = new Button { Content = "CANCEL", IsCancel = true };
        ok.Click += (_, _) =>
        {
            result = box.Text ?? "";
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" } },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "body" },
                },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task<string?> PickSaveFileAsync(
        string title,
        string suggestedName,
        string pattern,
        string? startFolder = null
    )
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(pattern),
            FileTypeChoices = [new FilePickerFileType(pattern) { Patterns = [pattern] }],
        };
        if (startFolder != null)
        {
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(
                startFolder
            );
        }

        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public async Task<int> ChoiceAsync(
        string title,
        string message,
        string[] buttons,
        string? details = null
    )
    {
        var result = -1;
        var dialog = new Window
        {
            Title = title,
            Width = details == null ? 560 : 760,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Icon,
        };
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], Margin = new(8, 8, 0, 0) };
            if (i == 0)
            {
                b.Classes.Add("primary");
            }

            b.Click += (_, _) =>
            {
                result = index;
                dialog.Close();
            };
            row.Children.Add(b);
        }
        var panel = new StackPanel
        {
            Margin = new(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" } },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "body" },
                },
            },
        };
        if (details != null)
        {
            panel.Children.Add(
                new Border
                {
                    Classes = { "panel" },
                    Padding = new(12),
                    MaxHeight = 320,
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia
                            .Controls
                            .Primitives
                            .ScrollBarVisibility
                            .Auto,
                        Content = new SelectableTextBlock
                        {
                            Text = details,
                            Classes = { "mono" },
                            FontSize = 11.5,
                        },
                    },
                }
            );
        }

        panel.Children.Add(row);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
        return result;
    }

    public void HideForPlay() => WindowState = WindowState.Minimized;

    public void ShowAfterPlay()
    {
        WindowState = _maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    public void OpenExternal(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch
        { /* nothing registered to open it */
        }
    }
}

public static class Converters
{
    public static readonly IValueConverter ErrorBrush = new FuncValueConverter<bool, IBrush>(
        isError => isError ? Brushes.IndianRed : Brushes.Gainsboro
    );

    public static readonly IValueConverter NonZero = new FuncValueConverter<int, bool>(n => n != 0);
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);
    public static readonly IValueConverter Star = new FuncValueConverter<bool, string>(on =>
        on ? "★" : "☆"
    );

    /// <summary>
    /// Setup's section rail and pages: true when the bound section is the parameter. Back from a rail button that
    /// was switched on it picks that section; one switched off (because another was picked) changes nothing.
    /// </summary>
    public static readonly IValueConverter SectionIs = new SectionConverter();

    sealed class SectionConverter : IValueConverter
    {
        public object? Convert(
            object? value,
            Type targetType,
            object? parameter,
            System.Globalization.CultureInfo culture
        ) => value is string s && s == parameter as string;

        public object? ConvertBack(
            object? value,
            Type targetType,
            object? parameter,
            System.Globalization.CultureInfo culture
        ) => value is true ? parameter : Avalonia.Data.BindingOperations.DoNothing;
    }

    /// <summary>
    /// An update line ("v10.8.0 → v10.8.1 (40 MB)") as "You have v10.8.0 · NEW v10.8.1 (40 MB)", the new version
    /// in bright green so it stands out. Text without " → " is shown as it is.
    /// </summary>
    public static readonly IValueConverter VersionChange = new FuncValueConverter<
        string?,
        InlineCollection
    >(text =>
    {
        var inlines = new InlineCollection();
        var at = text?.IndexOf(" → ", StringComparison.Ordinal) ?? -1;
        if (text == null || at < 0)
        {
            inlines.Add(new Run(text ?? ""));
            return inlines;
        }
        var old = text[..at];
        var next = text[(at + 3)..];
        var space = next.IndexOf(' ');
        var (tag, rest) = space < 0 ? (next, "") : (next[..space], next[space..]);
        var green = new SolidColorBrush(Color.Parse("#63C46E"));
        inlines.Add(
            new Run($"You have {old}   ")
            {
                Foreground = new SolidColorBrush(Color.Parse("#9B9BA4")),
            }
        );
        inlines.Add(new Run("NEW ") { Foreground = green, FontWeight = FontWeight.Bold });
        inlines.Add(new Run(tag) { Foreground = green, FontWeight = FontWeight.Bold });
        inlines.Add(new Run(rest));
        return inlines;
    });

    public static readonly IValueConverter StarBrush = new FuncValueConverter<bool, IBrush>(on =>
        on
            ? new SolidColorBrush(Color.Parse("#FFB547"))
            : new SolidColorBrush(Color.Parse("#9B9BA4"))
    );
}
