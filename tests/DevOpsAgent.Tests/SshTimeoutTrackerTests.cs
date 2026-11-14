using DevOpsAgent.Infrastructure.Ssh;

namespace DevOpsAgent.Tests;

public sealed class SshTimeoutTrackerTests
{
    [Fact]
    public void SessionIsRecycledOnlyAfterConsecutiveTimeouts()
    {
        var tracker = new SshTimeoutTracker();

        Assert.False(tracker.RecordTimeout());
        Assert.False(tracker.RecordTimeout());
        Assert.True(tracker.RecordTimeout());
        Assert.Equal(3, SshTimeoutTracker.DefaultThreshold);
    }

    [Fact]
    public void ASuccessfulCommandResetsTheCountSoOneSlowCommandNeverRecycles()
    {
        var tracker = new SshTimeoutTracker();

        Assert.False(tracker.RecordTimeout());
        Assert.False(tracker.RecordTimeout());
        tracker.RecordSuccess();
        Assert.False(tracker.RecordTimeout());
        Assert.False(tracker.RecordTimeout());
        Assert.True(tracker.RecordTimeout());
    }

    [Fact]
    public async Task ConcurrentTimeoutsAreCountedAtomically()
    {
        var tracker = new SshTimeoutTracker(threshold: 50);
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(tracker.RecordTimeout)));

        Assert.Single(results, reached => reached);
    }
}
