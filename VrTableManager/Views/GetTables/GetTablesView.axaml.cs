using Avalonia.Controls;
using VrTableManager.ViewModels;
using VrTableManager.Views.Shared;

namespace VrTableManager.Views.GetTables;

public partial class GetTablesView : UserControl
{
    LetterRail? _letters;

    public GetTablesView()
    {
        InitializeComponent();
        // The view model arrives with the window's DataContext.
        DataContextChanged += (_, _) =>
        {
            if (_letters == null && DataContext is MainViewModel vm)
            {
                _letters = new LetterRail(
                    CatalogLetterRail,
                    CatalogGrid,
                    CatalogScroll,
                    vm.FirstCatalogAt, // may show more of the list first
                    () => vm.ShowCatalogLetters,
                    letters => vm.SetActiveLetters(installed: false, letters),
                    vm.Games,
                    vm,
                    nameof(MainViewModel.ShowCatalogLetters)
                );
            }
        };
    }
}
