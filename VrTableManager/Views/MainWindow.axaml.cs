using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
        Opened += async (_, _) => await _vm.LoadCatalogAsync();
        Closing += async (_, e) =>
        {
            // Downloads running? Ask first; if closing anyway, they're offered again next launch.
            var running = _vm.RunningJobCount;
            if (!_closeConfirmed && running > 0)
            {
                e.Cancel = true;
                var pick = await ChoiceAsync(
                    running == 1 ? "A download is still running" : $"{running} downloads are still running",
                    "If you close now they stop, and VR Table Manager offers to start them again the next time you open it.",
                    ["KEEP OPEN", "CLOSE ANYWAY"]);
                if (pick == 1)
                {
                    _closeConfirmed = true;
                    Close();
                }
                return;
            }
            _vm.PrepareToClose();
            await _vm.DisposeAsync();
        };
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFileAsync(string title, string pattern)
    {
        var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(pattern) { Patterns = [pattern] }],
        });
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message)
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
        var ok = new Button { Content = "Delete", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string pattern, string? startFolder = null)
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(pattern),
            FileTypeChoices = [new FilePickerFileType(pattern) { Patterns = [pattern] }],
        };
        if (startFolder != null) options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public async Task<int> ChoiceAsync(string title, string message, string[] buttons, string? details = null)
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
            if (i == 0) b.Classes.Add("primary");
            b.Click += (_, _) => { result = index; dialog.Close(); };
            row.Children.Add(b);
        }
        var panel = new StackPanel
        {
            Margin = new(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" } },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "body" } },
            },
        };
        if (details != null)
            panel.Children.Add(new Border
            {
                Classes = { "panel" },
                Padding = new(12),
                MaxHeight = 320,
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new SelectableTextBlock { Text = details, Classes = { "mono" }, FontSize = 11.5 },
                },
            });
        panel.Children.Add(row);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
        return result;
    }

    public void HideForPlay() => WindowState = WindowState.Minimized;

    public void ShowAfterPlay()
    {
        WindowState = WindowState.Normal;
        Activate();
    }

    public void OpenExternal(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch { /* nothing registered to open it */ }
    }
}

public static class Converters
{
    public static readonly IValueConverter ErrorBrush =
        new FuncValueConverter<bool, IBrush>(isError => isError ? Brushes.IndianRed : Brushes.Gainsboro);

    public static readonly IValueConverter NonZero = new FuncValueConverter<int, bool>(n => n != 0);
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);
    public static readonly IValueConverter Star = new FuncValueConverter<bool, string>(on => on ? "★" : "☆");
    public static readonly IValueConverter StarBrush =
        new FuncValueConverter<bool, IBrush>(on => on ? new SolidColorBrush(Color.Parse("#FFB547")) : new SolidColorBrush(Color.Parse("#9B9BA4")));
}
