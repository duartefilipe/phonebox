using PhoneBox.Agent;
using Xunit;

/// <summary>What the agent tells the phone before it goes away: the reason in the state, and the stream's last "bye".</summary>
[Collection("adb session")] // reads the static session-ending window, and PowerSignal is static too
public sealed class PowerSignalTests : IDisposable
{
    private long _now = 1_000_000;

    public PowerSignalTests()
    {
        PowerSignal.Reset();
        AdbReverse.ResetSessionEnding();
        PowerSignal.Clock = () => _now;
        PowerSignal.SystemStopping = () => false;
    }

    public void Dispose()
    {
        PowerSignal.Reset();
        AdbReverse.ResetSessionEnding();
        PowerSignal.Clock = () => Environment.TickCount64;
        PowerSignal.SystemStopping = () => false;
        AppRestart.Requested = false;
    }

    [Fact]
    public void NothingIsKnownByDefault()
    {
        Assert.Null(PowerSignal.Current);
        Assert.Null(PowerSignal.LeaveReason());
    }

    [Fact]
    public void ASleepIsInTheStateUntilItExpiresOrTheComputerResumes()
    {
        PowerSignal.Set(PowerSignal.Sleep);
        Assert.Equal("sleep", PowerSignal.Current);
        _now += PowerSignal.ExpiryMs - 1;
        Assert.Equal("sleep", PowerSignal.Current);
        _now += 1;
        Assert.Null(PowerSignal.Current); // a missed resume does not leave the phone waiting for a sleep forever

        PowerSignal.Set(PowerSignal.Sleep);
        PowerSignal.Set(null);
        Assert.Null(PowerSignal.Current);
    }

    [Fact]
    public void SettingTheReasonPokesTheStreams()
    {
        var before = StateSignal.Version;
        PowerSignal.Set(PowerSignal.Shutdown);
        Assert.True(StateSignal.Version > before);
    }

    [Fact]
    public void AnAnnouncedShutdownWinsOverAQuit()
    {
        PowerSignal.Set(PowerSignal.Shutdown);
        PowerSignal.Quitting();
        Assert.Equal("shutdown", PowerSignal.LeaveReason());
    }

    [Fact]
    public void AQuitOrARestartOfTheAgentLeavesTheComputerOn()
    {
        PowerSignal.Quitting();
        Assert.Equal("stop", PowerSignal.LeaveReason());

        PowerSignal.Reset();
        AppRestart.Requested = true;
        Assert.Equal("stop", PowerSignal.LeaveReason());
    }

    [Fact]
    public void AnEndingWindowsSessionOrAStoppingSystemIsAShutdown()
    {
        AdbReverse.MarkSessionEnding();
        Assert.Equal("shutdown", PowerSignal.LeaveReason());

        AdbReverse.ResetSessionEnding();
        PowerSignal.SystemStopping = () => true;
        Assert.Equal("shutdown", PowerSignal.LeaveReason());
    }

    [Fact]
    public void AFailingSystemCheckMeansUnknown()
    {
        PowerSignal.SystemStopping = () => throw new InvalidOperationException("no systemctl");
        Assert.Null(PowerSignal.LeaveReason());
    }
}
