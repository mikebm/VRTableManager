using VrTableManager.Services;

/// <summary>
/// The download queue (DownloadGate): a few at once, the rest in line; a new "Downloads at once" applies to jobs
/// already queued; a job cancelled while queued leaves without taking a slot.
/// </summary>
public static class DownloadGateChecks
{
    public static async Task<List<(bool ok, string name)>> RunAsync()
    {
        var results = new List<(bool, string)>();
        var gate = new DownloadGate(2);

        await gate.WaitAsync();
        await gate.WaitAsync();
        var third = gate.WaitAsync();
        var fourth = gate.WaitAsync();
        results.Add(
            (
                gate.Running == 2 && gate.IsFull && !third.IsCompleted && !fourth.IsCompleted,
                "download queue: two at once, the rest wait"
            )
        );

        // Raised while jobs wait: the queued ones start at once, in order.
        gate.Limit = 3;
        await Task.WhenAny(third, Task.Delay(2000));
        results.Add(
            (
                third.IsCompletedSuccessfully && !fourth.IsCompleted && gate.Running == 3,
                "download queue: raising the limit starts queued jobs at once"
            )
        );

        // Lowered: nothing new starts until fewer than the new limit are running.
        gate.Limit = 1;
        gate.Release();
        gate.Release();
        await Task.Delay(100);
        results.Add(
            (
                !fourth.IsCompleted && gate.Running == 1,
                "download queue: lowering the limit holds the queue"
            )
        );
        gate.Release();
        await Task.WhenAny(fourth, Task.Delay(2000));
        results.Add(
            (
                fourth.IsCompletedSuccessfully && gate.Running == 1,
                "download queue: the next job starts once under the lowered limit"
            )
        );

        // Cancelled while queued: leaves the line, and the slot goes to the job behind it.
        using var cts = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(cts.Token);
        var behind = gate.WaitAsync();
        cts.Cancel();
        try
        {
            await cancelled;
        }
        catch (OperationCanceledException) { }
        gate.Release();
        await Task.WhenAny(behind, Task.Delay(2000));
        results.Add(
            (
                cancelled.IsCanceled && behind.IsCompletedSuccessfully && gate.Running == 1,
                "download queue: a job cancelled while queued takes no slot"
            )
        );
        gate.Release();
        results.Add((gate.Running == 0 && !gate.IsFull, "download queue: every slot given back"));
        return results;
    }
}
