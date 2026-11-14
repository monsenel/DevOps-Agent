using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Tests;

public sealed class DiscoveryStatusTextTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoRunAtAllMeansDiscoveryHasNotRun() =>
        Assert.Equal("Discovery has not run yet.", DiscoveryStatusText.Describe(null, null, 0));

    [Fact]
    public void AFailedOnlyRunIsReportedAsFailedNotAsNeverRun()
    {
        var failed = Run(Start, SnapshotStatus.Failed);

        var text = DiscoveryStatusText.Describe(failed, null, 0);

        Assert.Contains("failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("has not run", text, StringComparison.Ordinal);
        Assert.Contains("No usable topology", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRunAfterASuccessDoesNotHideBehindThePreviousSuccess()
    {
        var good = Run(Start, SnapshotStatus.Completed);
        var failed = Run(Start.AddHours(1), SnapshotStatus.Failed);
        var cancelled = Run(Start.AddHours(2), SnapshotStatus.Cancelled);

        var failedText = DiscoveryStatusText.Describe(failed, good, 3);
        var cancelledText = DiscoveryStatusText.Describe(cancelled, good, 3);

        Assert.Contains("failed", failedText, StringComparison.Ordinal);
        Assert.Contains("Showing the topology from the last usable run", failedText, StringComparison.Ordinal);
        Assert.Contains("cancelled", cancelledText, StringComparison.Ordinal);
        Assert.DoesNotContain("found 3 changes", failedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulRunsKeepTheChangeSummary()
    {
        var good = Run(Start, SnapshotStatus.Completed);

        Assert.Contains("found 2 changes", DiscoveryStatusText.Describe(good, good, 2), StringComparison.Ordinal);
        Assert.Contains("1 failed server", DiscoveryStatusText.Describe(Run(Start, SnapshotStatus.Partial, failedServers: 1), good, 0), StringComparison.Ordinal);
    }

    private static Snapshot Run(DateTimeOffset startedAt, SnapshotStatus status, int failedServers = 0)
    {
        var snapshot = new Snapshot([], startedAt);
        switch (status)
        {
            case SnapshotStatus.Failed: snapshot.Fail(startedAt.AddMinutes(1), 0, 0); break;
            case SnapshotStatus.Cancelled: snapshot.Cancel(startedAt.AddMinutes(1), 0, 0); break;
            default: snapshot.Complete(startedAt.AddMinutes(1), 1, failedServers); break;
        }

        return snapshot;
    }
}
