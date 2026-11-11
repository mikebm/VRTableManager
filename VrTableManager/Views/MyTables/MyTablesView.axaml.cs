using Avalonia.Controls;
using VrTableManager.ViewModels;
using VrTableManager.Views.Shared;

namespace VrTableManager.Views.MyTables;

public partial class MyTablesView : UserControl
{
    LetterRail? _letters;

    public MyTablesView()
    {
        InitializeComponent();
        // The view model arrives with the window's DataContext.
        DataContextChanged += (_, _) =>
        {
            if (_letters == null && DataContext is MainViewModel vm)
            {
                _letters = new LetterRail(
                    InstalledLetterRail,
                    InstalledGrid,
                    InstalledScroll,
                    vm.FirstInstalledAt,
                    () => vm.ShowInstalledLetters,
                    letters => vm.SetActiveLetters(installed: true, letters),
                    vm.Installed,
                    vm,
                    nameof(MainViewModel.ShowInstalledLetters)
                );
            }
        };
    }
}
