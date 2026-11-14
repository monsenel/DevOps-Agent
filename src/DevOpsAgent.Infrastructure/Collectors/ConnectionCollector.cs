using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class ConnectionCollector : ICollector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "connections";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, "command -v ss >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var collectedAtUtc = DateTimeOffset.UtcNow;
        var host = await context.SshRunner.RunAsync(context.Server.Id, "ss -tnH state established",
            new SshRunOptions(CommandTimeout), cancellationToken);
        if (host.ExitCode is not 0)
        {
            return new CollectorOutput(host.StandardOutput, host.StandardError, host.ExitCode, host.Duration);
        }

        var duration = host.Duration;
        var containerOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        if (context.TryGetPreviousResult<DockerDiscoveryResult>("docker", out var docker) && docker is not null)
        {
            var containers = docker.Containers.Where(container => container.MainPid > 0).ToArray();
            if (containers.Length > 0 && context.Server.SudoCapable != true)
            {
                notes.Add("Container network connections were skipped because passwordless sudo is unavailable.");
            }
            else
            {
                foreach (var container in containers)
                {
                    var result = await context.SshRunner.RunAsync(context.Server.Id,
                        $"nsenter -t {container.MainPid} -n ss -tnH state established",
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

        var raw = JsonSerializer.Serialize(new ConnectionCollectorRawOutput(host.StandardOutput, containerOutputs, notes, collectedAtUtc));
        return new CollectorOutput(raw, host.StandardError, host.ExitCode, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<ConnectionCollectorRawOutput>(output.StandardOutput);
            if (raw is null) return new CollectorResult(Error: "Connection collector output is empty.");

            var result = ConnectionCollectorParser.Parse(raw.HostOutput, raw.ContainerOutputs, raw.Notes, raw.CollectedAtUtc);
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
        DateTimeOffset CollectedAtUtc);
}
