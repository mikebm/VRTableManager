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
        // The view model arrives with the DataContext MainWindow gives it (Catalog).
        DataContextChanged += (_, _) =>
        {
            if (_letters == null && DataContext is CatalogViewModel vm)
            {
                _letters = new LetterRail(
                    CatalogLetterRail,
                    CatalogGrid,
                    CatalogScroll,
                    vm.FirstCatalogAt, // may show more of the list first
                    () => vm.ShowCatalogLetters,
                    letters => vm.SetActiveLetters(letters),
                    vm.Games,
                    vm,
                    nameof(CatalogViewModel.ShowCatalogLetters)
                );
            }
        };
    }
}
