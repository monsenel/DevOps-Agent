using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class SystemdCollector : ICollector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "systemd";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, "command -v systemctl >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var listeners = context.TryGetPreviousResult<ListenerDiscoveryResult>("listeners", out var listenerResult)
            ? listenerResult!.Listeners
            : [];
        var listenerPids = listeners.Where(listener => listener.ProcessId is > 0)
            .Select(listener => listener.ProcessId!.Value).ToHashSet();

        var listResult = await context.SshRunner.RunAsync(context.Server.Id,
            "systemctl list-units --type=service --state=running --no-legend --plain",
            new SshRunOptions(CommandTimeout), cancellationToken);
        var errors = new List<string>();
        if (listResult.ExitCode != 0)
        {
            errors.Add(string.IsNullOrWhiteSpace(listResult.StandardError)
                ? $"systemctl list-units exited with code {listResult.ExitCode?.ToString() ?? "unknown"}."
                : listResult.StandardError.Trim());
        }

        var showOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var duration = listResult.Duration;
        if (listResult.ExitCode == 0 && listenerPids.Count > 0)
        {
            foreach (var unitName in SystemdCollectorParser.ParseRunningServiceNames(listResult.StandardOutput))
            {
                var showResult = await context.SshRunner.RunAsync(context.Server.Id,
                    $"systemctl show -p MainPID,ExecStart,FragmentPath {ShellQuote(unitName)}",
                    new SshRunOptions(CommandTimeout), cancellationToken);
                duration += showResult.Duration;
                if (showResult.ExitCode == 0)
                {
                    if (SystemdCollectorParser.ParseUnit(unitName, showResult.StandardOutput, listenerPids) is not null)
                    {
                        showOutputs[unitName] = showResult.StandardOutput;
                    }
                }
                else
                {
                    errors.Add(string.IsNullOrWhiteSpace(showResult.StandardError)
                        ? $"systemctl show {unitName} exited with code {showResult.ExitCode?.ToString() ?? "unknown"}."
                        : showResult.StandardError.Trim());
                }
            }
        }

        var raw = JsonSerializer.Serialize(new SystemdCollectorRawOutput(listResult.StandardOutput, showOutputs, listenerPids.ToArray()));
        return new CollectorOutput(raw, string.Join(Environment.NewLine, errors), errors.Count == 0 ? 0 : 1, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<SystemdCollectorRawOutput>(output.StandardOutput);
            return raw is null
                ? new CollectorResult(Error: "systemd collector output is empty.")
                : new CollectorResult(JsonSerializer.SerializeToElement(SystemdCollectorParser.Parse(
                    raw.RunningUnitsOutput, raw.UnitShowOutputs, raw.ListenerPids.ToHashSet())));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid systemd collector output: {exception.Message}");
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed record SystemdCollectorRawOutput(
        string RunningUnitsOutput,
        IReadOnlyDictionary<string, string> UnitShowOutputs,
        int[] ListenerPids);
}
