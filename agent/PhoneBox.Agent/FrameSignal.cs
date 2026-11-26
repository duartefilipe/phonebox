namespace PhoneBox.Agent;

/// <summary>
/// "A new frame exists": the requests waiting for one are released together.
/// </summary>
/// <remarks>
/// The Now playing screen's live video needs this. Before, the agent grabbed on its own clock while the panel guessed
/// when to ask, and the two beat against each other: enough frames were made, but they reached the phone 33 to 267 ms
/// apart, which is seen as judder. With this, a request waits here and is answered the moment a frame is grabbed, so
/// the spacing is the capture's own.
/// A <see cref="TaskCompletionSource"/> per waiter would need bookkeeping; one source, replaced on every poke, lets any
/// number of waiters share it. No platform code, so it compiles and is tested everywhere.
/// </remarks>
internal sealed class FrameSignal
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True when a frame arrived within <paramref name="timeoutMs"/>; false when the wait ran out.</summary>
    public async Task<bool> WaitAsync(int timeoutMs, CancellationToken ct)
    {
        Task wait;
        lock (_lock) wait = _next.Task;
        // the timer is cancelled as soon as the frame arrives, so a busy capture leaves no pending delays behind
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(timeoutMs, cts.Token);
        var done = await Task.WhenAny(wait, timer);
        if (done != wait) return false;
        await cts.CancelAsync();
        return true;
    }

    /// <summary>A frame was grabbed (or the capture stopped): everyone waiting goes on.</summary>
    public void Poke()
    {
        TaskCompletionSource release;
        lock (_lock)
        {
            release = _next;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        release.TrySetResult();
    }
}
