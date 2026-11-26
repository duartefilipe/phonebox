using System.Diagnostics;
using PhoneBox.Agent;
using Xunit;

// The signal that keeps the Now playing screen's live video evenly spaced: a request waits here and is answered the
// moment a frame is grabbed, instead of the panel guessing when to ask.
public sealed class FrameSignalTests
{
    [Fact]
    public async Task AWaitingRequestIsReleasedByTheNextFrame()
    {
        var signal = new FrameSignal();
        var waiting = signal.WaitAsync(5000, CancellationToken.None);
        Assert.False(waiting.IsCompleted); // nothing grabbed yet: it waits
        signal.Poke();
        Assert.True(await waiting);
    }

    [Fact]
    public async Task AWaitThatSeesNoFrameGivesUp()
    {
        var started = Stopwatch.GetTimestamp();
        Assert.False(await new FrameSignal().WaitAsync(80, CancellationToken.None));
        Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 60); // it really waited
    }

    // several phones (and several widgets) share one capture: one frame has to release them all
    [Fact]
    public async Task OneFrameReleasesEveryWaiter()
    {
        var signal = new FrameSignal();
        var waiters = Enumerable.Range(0, 5).Select(_ => signal.WaitAsync(5000, CancellationToken.None)).ToArray();
        signal.Poke();
        Assert.All(await Task.WhenAll(waiters), Assert.True);
    }

    // a frame grabbed before the wait started belongs to the previous round: the waiter wants the *next* one
    [Fact]
    public async Task AnEarlierFrameDoesNotCountForALaterWait()
    {
        var signal = new FrameSignal();
        signal.Poke();
        Assert.False(await signal.WaitAsync(80, CancellationToken.None));
    }

    [Fact]
    public async Task ADroppedRequestStopsWaiting()
    {
        using var cts = new CancellationTokenSource();
        var waiting = new FrameSignal().WaitAsync(5000, cts.Token);
        await cts.CancelAsync();
        // the phone went away mid-request: the wait ends instead of holding the connection to its full timeout
        try { Assert.False(await waiting); }
        catch (OperationCanceledException) { /* either way it stops; the endpoint treats both as "nothing new" */ }
    }

    [Fact]
    public async Task TheCaptureKeepsPacingAfterAFrame()
    {
        var signal = new FrameSignal();
        for (var i = 0; i < 3; i++)
        {
            var waiting = signal.WaitAsync(5000, CancellationToken.None);
            signal.Poke();
            Assert.True(await waiting); // every round works, not only the first
        }
    }

    [Fact]
    public void TheWaitIsLongerThanTheSlowestInterval()
    {
        // at 1 fps a frame is a second apart: a shorter wait would answer "nothing new" before it arrived
        Assert.True(MediaService.FrameWaitMs > 1000 / MediaService.MinFps,
            $"the wait ({MediaService.FrameWaitMs} ms) must outlast one interval at {MediaService.MinFps} fps");
    }
}
