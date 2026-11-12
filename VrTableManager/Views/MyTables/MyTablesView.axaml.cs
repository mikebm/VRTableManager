using Avalonia.Controls;
using VrTableManager.ViewModels;
using VrTableManager.Views.Shared;

namespace VrTableManager.Views.MyTables;

public partial class MyTablesView : UserControl
{
    LetterRail? _letters;
    CardRows? _rows;

    /// <summary>A card's right and bottom margin (TableCard.axaml).</summary>
    const double CardGap = 16;

    public MyTablesView()
    {
        InitializeComponent();
        // The view model arrives with the window's DataContext.
        DataContextChanged += (_, _) =>
        {
            if (_letters == null && DataContext is MainViewModel vm)
            {
                _rows = new CardRows(vm.Installed, vm.Installed);
                InstalledGrid.ItemsSource = _rows.Rows;
                FitRows(vm);
                InstalledScroll.SizeChanged += (_, _) => FitRows(vm);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.CardWidth))
                    {
                        FitRows(vm);
                    }
                };
                _letters = new LetterRail(
                    InstalledLetterRail,
                    InstalledGrid,
                    InstalledScroll,
                    vm.FirstInstalledAt,
                    () => vm.ShowInstalledLetters,
                    letters => vm.SetActiveLetters(installed: true, letters),
                    _rows.Rows,
                    vm,
                    nameof(MainViewModel.ShowInstalledLetters),
                    _rows
                );
            }
        };
    }

    /// <summary>As many cards across as the grid's width takes, as the WrapPanel it replaced fitted them.</summary>
    void FitRows(MainViewModel vm)
    {
        if (_rows == null)
        {
            return;
        }

        var width =
            InstalledScroll.Bounds.Width - InstalledGrid.Margin.Left - InstalledGrid.Margin.Right;
        if (width <= 0)
        {
            return; // not laid out yet: SizeChanged comes once it is
        }

        _rows.PerRow = (int)Math.Floor((width + 0.5) / (vm.CardWidth + CardGap));
    }
}
