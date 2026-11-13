namespace DevOpsAgent.Infrastructure.Ssh;

public sealed class SshConnectionOptions
{
    public const string SectionName = "Ssh";

    public string KnownHostsFile { get; set; } = "/keys/known_hosts";
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public int KeepAliveSeconds { get; set; } = 30;
}
