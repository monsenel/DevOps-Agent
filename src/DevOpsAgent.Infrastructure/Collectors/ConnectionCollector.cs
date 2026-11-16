using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class ConnectionCollector : ICollector
{
    public static string NamespaceCommand(int pid) => $"nsenter -t {pid} -n ss -tnH state established";

    public static string NamespaceNetstatCommand(int pid) => $"nsenter -t {pid} -n {NetstatCommand}";

    /// <summary>Fallback for hosts without <c>ss</c> (BusyBox/Alpine): established and other non-listening TCP sockets.</summary>
    public const string NetstatCommand = "netstat -tn";

    /// <summary>Read-only; sshd sets <c>SSH_CLIENT</c> for every session. Not run with sudo.</summary>
    public const string SshClientCommand = "printf '%s' \"$SSH_CLIENT\"";

    private const string HostCommand = "ss -tnH state established";
    private const int MaxSshClientLength = 200;
    private const int CommandNotFound = 127;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "connections";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) =>
        await HasCommandAsync(context, "ss", cancellationToken) || await HasCommandAsync(context, "netstat", cancellationToken);

    private static async Task<bool> HasCommandAsync(ServerContext context, string command, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, $"command -v {command} >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var collectedAtUtc = DateTimeOffset.UtcNow;
        var useNetstat = false;
        var host = await context.SshRunner.RunAsync(context.Server.Id, HostCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        if (host.ExitCode == CommandNotFound)
        {
            useNetstat = true;
            var ssDuration = host.Duration;
            host = await context.SshRunner.RunAsync(context.Server.Id, NetstatCommand,
                new SshRunOptions(CommandTimeout), cancellationToken);
            host = host with { Duration = host.Duration + ssDuration };
        }

        if (host.ExitCode is not 0)
        {
            return new CollectorOutput(host.StandardOutput, host.StandardError, host.ExitCode, host.Duration);
        }

        var duration = host.Duration;
        var agentSshClient = await ReadAgentSshClientAsync(context, cancellationToken);
        var containerOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        if (context.TryGetPreviousResult<DockerDiscoveryResult>("docker", out var docker) && docker is not null)
        {
            var containers = docker.Containers.Where(container => container.MainPid > 0).ToArray();
            if (containers.Length > 0 && !context.Server.CanUseSudo)
            {
                notes.Add("Container network connections were skipped because sudo is unavailable.");
            }
            else
            {
                foreach (var container in containers)
                {
                    var result = await context.SshRunner.RunAsync(context.Server.Id,
                        useNetstat ? NamespaceNetstatCommand(container.MainPid) : NamespaceCommand(container.MainPid),
                        new SshRunOptions(CommandTimeout, UseSudo: true), cancellationToken);
                    duration += result.Duration;
                    if (result.ExitCode == 0)
                    {
                        containerOutputs[container.Name] = result.StandardOutput;
                    }
                    else
                    {
                        notes.Add($"Container network connections for {container.Name} could not be read (exit {result.ExitCode}).");
                    }
                }
            }
        }

        var raw = JsonSerializer.Serialize(new ConnectionCollectorRawOutput(host.StandardOutput, containerOutputs, notes, collectedAtUtc, agentSshClient));
        return new CollectorOutput(raw, host.StandardError, host.ExitCode, duration);
    }

    /// <summary>
    /// Reads <c>$SSH_CLIENT</c> ("client-ip client-port server-port") of the agent's own SSH session. Best effort: any failure or
    /// empty value means "do not filter" and never fails the collector.
    /// </summary>
    private static async Task<string?> ReadAgentSshClientAsync(ServerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id, SshClientCommand,
                new SshRunOptions(CommandTimeout), cancellationToken);
            if (result.ExitCode != 0) return null;
            var value = result.StandardOutput.Trim();
            return value.Length is > 0 and <= MaxSshClientLength ? value : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<ConnectionCollectorRawOutput>(output.StandardOutput);
            if (raw is null) return new CollectorResult(Error: "Connection collector output is empty.");

            var result = ConnectionCollectorParser.Parse(raw.HostOutput, raw.ContainerOutputs, raw.Notes, raw.CollectedAtUtc,
                raw.AgentSshClient);
            return new CollectorResult(JsonSerializer.SerializeToElement(result));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid connection collector output: {exception.Message}");
        }
    }

    private sealed record ConnectionCollectorRawOutput(
        string HostOutput,
        IReadOnlyDictionary<string, string> ContainerOutputs,
        IReadOnlyList<string> Notes,
        DateTimeOffset CollectedAtUtc,
        string? AgentSshClient = null);
}
