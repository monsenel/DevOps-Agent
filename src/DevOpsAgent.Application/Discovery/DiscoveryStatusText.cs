using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Application.Discovery;

public static class DiscoveryStatusText
{
    /// <summary>
    /// The overview status line. It is derived from the latest run of any status, so a failed or cancelled run is never
    /// hidden behind an older success; the graph snapshot is only used for the "showing" hint and the change count.
    /// </summary>
    public static string Describe(Snapshot? latestRun, Snapshot? graphSnapshot, int driftChangeCount)
    {
        if (latestRun is null) return "Discovery has not run yet.";
        var when = latestRun.StartedAtUtc.ToLocalTime().ToString("g");
        var showing = graphSnapshot is not null && graphSnapshot.Id != latestRun.Id
            ? $" Showing the topology from the last usable run ({graphSnapshot.StartedAtUtc.ToLocalTime():g})."
            : graphSnapshot is null ? " No usable topology is available yet." : string.Empty;
        return latestRun.Status switch
        {
            SnapshotStatus.Failed => $"Last discovery ({when}) failed.{showing}",
            SnapshotStatus.Cancelled => $"Last discovery ({when}) was cancelled.{showing}",
            SnapshotStatus.Running => $"Discovery started {when} is still running or was interrupted.{showing}",
            SnapshotStatus.Partial => $"Last discovery ran {when} with {latestRun.FailedServerCount} failed server(s) and found {driftChangeCount} changes since the previous snapshot.",
            _ => $"Last discovery ran {when} and found {driftChangeCount} changes since the previous snapshot."
        };
    }
}
