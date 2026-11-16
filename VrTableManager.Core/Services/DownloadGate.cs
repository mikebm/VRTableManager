namespace VrTableManager.Services;

/// <summary>
/// Lets a few downloads run at once and queues the rest, first come first served. Unlike a SemaphoreSlim its
/// <see cref="Limit"/> can change while jobs wait: raised, queued jobs start at once; lowered, running ones finish and
/// no more start until fewer than the new limit are running.
/// </summary>
public sealed class DownloadGate(int limit)
{
    readonly object _lock = new();
    readonly LinkedList<TaskCompletionSource> _queue = [];
    int _limit = Math.Max(1, limit);
    int _running;

    public int Limit
    {
        get
        {
            lock (_lock)
            {
                return _limit;
            }
        }
        set
        {
            lock (_lock)
            {
                _limit = Math.Max(1, value);
            }
            StartWaiting();
        }
    }

    /// <summary>Every slot taken (or jobs already queued): a new one would wait.</summary>
    public bool IsFull
    {
        get
        {
            lock (_lock)
            {
                return _running >= _limit || _queue.Count > 0;
            }
        }
    }

    public int Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>Takes a slot, waiting in line for one if need be. Pair with <see cref="Release"/>.</summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        TaskCompletionSource ticket;
        LinkedListNode<TaskCompletionSource> place;
        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
            if (_running < _limit && _queue.Count == 0)
            {
                _running++;
                return;
            }
            ticket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            place = _queue.AddLast(ticket);
        }

        // Cancelled while queued: leaves the line without taking a slot. (Once given one, the ticket is out of the
        // line and cancelling no longer touches it: the caller holds the slot and releases it.)
        using (
            ct.Register(() =>
            {
                lock (_lock)
                {
                    if (place.List == null)
                    {
                        return;
                    }
                    _queue.Remove(place);
                }
                ticket.TrySetCanceled(ct);
            })
        )
        {
            await ticket.Task;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _running--;
        }
        StartWaiting();
    }

    void StartWaiting()
    {
        List<TaskCompletionSource> start = [];
        lock (_lock)
        {
            while (_running < _limit && _queue.First is { } next)
            {
                _queue.RemoveFirst();
                _running++;
                start.Add(next.Value);
            }
        }
        foreach (var t in start)
        {
            t.TrySetResult();
        }
    }
}
