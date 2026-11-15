namespace DevOpsAgent.Infrastructure.Ssh;

public sealed class SshConnectionOptions
{
    public const string SectionName = "Ssh";

    /// <summary>The owner's known_hosts (read-only mount). A host listed here is checked against these keys only.</summary>
    public string KnownHostsFile { get; set; } = "/keys/known_hosts";

    /// <summary>The app-managed known_hosts (T8.4): keys recorded on first use and keys the owner accepted after a change.</summary>
    public string ManagedKnownHostsFile { get; set; } = "/data/known_hosts";
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public int KeepAliveSeconds { get; set; } = 30;
}
