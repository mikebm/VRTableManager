using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;

namespace VrTableManager.Views.Shared;

/// <summary>One row of cards in a virtualized card grid.</summary>
public sealed class CardRow(IReadOnlyList<object> items)
{
    public IReadOnlyList<object> Items { get; } = items;

    public bool Holds(object item)
    {
        foreach (var i in Items)
        {
            if (ReferenceEquals(i, item))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// A card list cut into rows as wide as the grid, for a VirtualizingStackPanel: only the rows on screen get built.
/// WrapPanel can't virtualize, so a few hundred tables meant a few hundred cards made up front. A row that holds the
/// same cards as before is kept, so a change near the end doesn't rebuild the cards at the top.
/// </summary>
public sealed class CardRows
{
    readonly IList _source;
    int _perRow = 1;
    bool _queued;

    public ObservableCollection<CardRow> Rows { get; } = [];

    public CardRows(IList source, INotifyCollectionChanged changes)
    {
        _source = source;
        changes.CollectionChanged += (_, _) => RebuildSoon(); // a refilter is many single changes: one rebuild for all
        Rebuild();
    }

    /// <summary>How many cards fit across; the rows are cut again when it changes.</summary>
    public int PerRow
    {
        get => _perRow;
        set
        {
            value = Math.Max(1, value);
            if (value != _perRow)
            {
                _perRow = value;
                Rebuild();
            }
        }
    }

    /// <summary>The row holding <paramref name="item"/>, or -1.</summary>
    public int RowOf(object item)
    {
        for (var r = 0; r < Rows.Count; r++)
        {
            if (Rows[r].Holds(item))
            {
                return r;
            }
        }
        return -1;
    }

    void RebuildSoon()
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
                Rebuild();
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>Cuts the list into rows now (it's otherwise done once the list has stopped changing).</summary>
    public void Rebuild()
    {
        var count = (_source.Count + _perRow - 1) / _perRow;
        for (var r = 0; r < count; r++)
        {
            var start = r * _perRow;
            var items = new object[Math.Min(_perRow, _source.Count - start)];
            for (var k = 0; k < items.Length; k++)
            {
                items[k] = _source[start + k]!;
            }

            if (r < Rows.Count)
            {
                if (!Rows[r].Items.SequenceEqual(items, ReferenceEqualityComparer.Instance))
                {
                    Rows[r] = new CardRow(items);
                }
            }
            else
            {
                Rows.Add(new CardRow(items));
            }
        }
        while (Rows.Count > count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }
}
