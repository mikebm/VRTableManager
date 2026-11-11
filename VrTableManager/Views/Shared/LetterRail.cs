using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VrTableManager.Lobby;
using VrTableManager.ViewModels;

namespace VrTableManager.Views.Shared;

/// <summary>
/// Drives an A–Z rail beside a card grid (My Tables, Get Tables): a letter click scrolls the grid so the letter's
/// first table is at the top, and the letters with cards on screen light up as it scrolls or its cards change.
/// </summary>
public sealed class LetterRail
{
    readonly ItemsControl _grid;
    readonly ScrollViewer _scroll;
    readonly Func<string, object?> _firstAt;
    readonly Func<bool> _shown;
    readonly Action<IReadOnlySet<string>> _light;
    bool _queued;

    /// <param name="rail">The rail's ItemsControl of <see cref="LetterChoice"/> buttons.</param>
    /// <param name="grid">The card grid beside it.</param>
    /// <param name="scroll">The ScrollViewer around the grid.</param>
    /// <param name="firstAt">The item a letter jumps to (it may show more of the list first), or null.</param>
    /// <param name="shown">Whether the rail is showing (only while the list is A–Z).</param>
    /// <param name="light">Lights these letters and no others.</param>
    /// <param name="items">The grid's items: a change looks at the letters again.</param>
    /// <param name="owner">Raises <paramref name="shownProperty"/> when the rail shows or hides.</param>
    public LetterRail(
        ItemsControl rail,
        ItemsControl grid,
        ScrollViewer scroll,
        Func<string, object?> firstAt,
        Func<bool> shown,
        Action<IReadOnlySet<string>> light,
        INotifyCollectionChanged items,
        INotifyPropertyChanged owner,
        string shownProperty
    )
    {
        _grid = grid;
        _scroll = scroll;
        _firstAt = firstAt;
        _shown = shown;
        _light = light;

        rail.AddHandler(Button.ClickEvent, OnLetterClick);
        // A filter can change the cards without changing the scroll size, and a list refiltered while its tab was
        // hidden only lays out when shown again, so those look again too, once the cards are in place.
        scroll.ScrollChanged += (_, _) => Track();
        items.CollectionChanged += (_, _) => TrackSoon();
        scroll.AttachedToVisualTree += (_, _) => TrackSoon();
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == shownProperty)
            {
                TrackSoon();
            }
        };
    }

    void OnLetterClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { DataContext: LetterChoice choice })
        {
            return;
        }

        if (_firstAt(choice.Letter) is not { } item)
        {
            return;
        }

        _grid.UpdateLayout(); // cards a page just added need their place before it's measured
        if (
            _grid.ContainerFromItem(item) is { } card
            && _scroll.Content is Visual content
            && card.TranslatePoint(default, content) is { } at
        )
        {
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Max(0, at.Y - 8));
        }
    }

    /// <summary>
    /// Lights the letters of the cards on screen: every card at least half showing. Cards are laid out in list
    /// order, row by row, so two binary searches find the first and last of them however long the list is.
    /// </summary>
    void Track()
    {
        if (!_shown() || _scroll.Content is not Visual content)
        {
            return;
        }

        // Where a card's middle sits in the list, or null while it isn't laid out.
        double? Middle(int index) =>
            _grid.ContainerFromIndex(index) is { } card
            && card.TranslatePoint(default, content) is { } at
                ? at.Y + card.Bounds.Height / 2
                : null;

        // The first card whose middle is below y (cards' middles only grow down the list).
        int FirstBelow(double y)
        {
            int lo = 0,
                hi = _grid.ItemCount;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Middle(mid) is not { } m)
                {
                    return -1;
                }

                if (m > y)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            return lo;
        }

        var first = FirstBelow(_scroll.Offset.Y);
        var end = FirstBelow(_scroll.Offset.Y + _scroll.Viewport.Height);
        var letters = new HashSet<string>();
        for (var i = Math.Max(first, 0); first >= 0 && i < end; i++)
        {
            if (
                _grid.Items[i] switch
                {
                    InstalledItem t => t.Title,
                    GameItem g => g.Title,
                    _ => null,
                } is
                { } title
            )
            {
                letters.Add(LobbyNav.LetterOf(title));
            }
        }
        _light(letters);
    }

    /// <summary>One look once layout has caught up, however many cards a refill adds one by one.</summary>
    void TrackSoon()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _queued = false;
                Track();
            },
            DispatcherPriority.Loaded
        );
    }
}
