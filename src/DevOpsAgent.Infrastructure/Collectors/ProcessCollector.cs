using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class ProcessCollector : ICollector
{
    public const string UptimeCommand = "cat /proc/uptime 2>/dev/null || true";
    public const string PsCommand = "ps -eo pid=,ppid=,user:32=,stat:8=,pcpu=,pmem=,etimes=,comm=,args= --sort=-pcpu";
    public const string PsFallbackCommand = "ps -o pid,ppid,user,stat,comm,args 2>/dev/null || ps -o pid,user,stat,comm 2>/dev/null || ps";
    public const string DockerCheckCommand = "command -v docker >/dev/null 2>&1";
    public const string DockerAllContainersCommand = "docker ps -a --no-trunc --format '{{.ID}}|{{.Names}}|{{.Image}}|{{.State}}|{{.Status}}|{{.Ports}}'";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public string Name => "processes";

    public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.Zero;
        var errors = new List<string>();

        var uptimeResult = await context.SshRunner.RunAsync(context.Server.Id, UptimeCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        duration += uptimeResult.Duration;

        var extended = true;
        var psResult = await context.SshRunner.RunAsync(context.Server.Id, PsCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        duration += psResult.Duration;

        if (psResult.ExitCode != 0 || string.IsNullOrWhiteSpace(psResult.StandardOutput))
        {
            extended = false;
            psResult = await context.SshRunner.RunAsync(context.Server.Id, PsFallbackCommand,
                new SshRunOptions(CommandTimeout), cancellationToken);
            duration += psResult.Duration;
            if (psResult.ExitCode != 0 && !string.IsNullOrWhiteSpace(psResult.StandardError))
            {
                errors.Add($"ps: {psResult.StandardError.Trim()}");
            }
        }

        var topPids = ProcessCollectorParser.ExtractTopPids(psResult.StandardOutput, extended, ProcessCollectorParser.MaxStoredProcesses);
        var cgroupOutput = string.Empty;
        if (topPids.Count > 0)
        {
            var cgroupResult = await context.SshRunner.RunAsync(context.Server.Id,
                SystemdCollector.BuildCgroupCommand(topPids),
                new SshRunOptions(CommandTimeout), cancellationToken);
            duration += cgroupResult.Duration;
            if (cgroupResult.ExitCode == 0)
            {
                cgroupOutput = cgroupResult.StandardOutput;
            }
        }

        var dockerInstalled = false;
        var dockerDaemonRunning = false;
        var dockerOutput = string.Empty;

        var dockerCheck = await context.SshRunner.RunAsync(context.Server.Id, DockerCheckCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        duration += dockerCheck.Duration;
        if (dockerCheck.ExitCode == 0)
        {
            dockerInstalled = true;
            var useSudo = false;
            var infoResult = await context.SshRunner.RunAsync(context.Server.Id, DockerCollector.InfoCommand,
                new SshRunOptions(CommandTimeout), cancellationToken);
            duration += infoResult.Duration;
            if (infoResult.ExitCode == 0)
            {
                dockerDaemonRunning = true;
            }
            else if (context.Server.CanUseSudo)
            {
                var sudoInfo = await context.SshRunner.RunAsync(context.Server.Id, DockerCollector.InfoCommand,
                    new SshRunOptions(CommandTimeout, UseSudo: true), cancellationToken);
                duration += sudoInfo.Duration;
                if (sudoInfo.ExitCode == 0)
                {
                    dockerDaemonRunning = true;
                    useSudo = true;
                }
            }

            if (dockerDaemonRunning)
            {
                var containersResult = await context.SshRunner.RunAsync(context.Server.Id, DockerAllContainersCommand,
                    new SshRunOptions(CommandTimeout, useSudo), cancellationToken);
                duration += containersResult.Duration;
                if (containersResult.ExitCode == 0)
                {
                    dockerOutput = containersResult.StandardOutput;
                }
                else if (!string.IsNullOrWhiteSpace(containersResult.StandardError))
                {
                    errors.Add($"docker ps -a: {containersResult.StandardError.Trim()}");
                }
            }
        }

        // Keep only the top lines and redact command arguments before persisting raw output.
        var redactedPs = string.Join('\n', psResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(ProcessCollectorParser.MaxStoredProcesses + 2)
            .Select(ChatContentRedactor.Redact));

        var raw = JsonSerializer.Serialize(new ProcessCollectorRawOutput(
            uptimeResult.StandardOutput,
            redactedPs,
            cgroupOutput,
            dockerOutput,
            dockerInstalled,
            dockerDaemonRunning,
            extended));

        return new CollectorOutput(raw, string.Join(Environment.NewLine, errors), errors.Count == 0 ? 0 : 1, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<ProcessCollectorRawOutput>(output.StandardOutput);
            if (raw is null)
            {
                return new CollectorResult(Error: "Process collector output is empty.");
            }

            var parsed = ProcessCollectorParser.Parse(
                raw.UptimeOutput,
                raw.PsOutput,
                raw.CgroupOutput,
                raw.DockerPsAllOutput,
                raw.DockerInstalled,
                raw.DockerDaemonRunning,
                raw.ExtendedPsColumns);
            return new CollectorResult(JsonSerializer.SerializeToElement(parsed));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid process collector output: {exception.Message}");
        }
    }

    private sealed record ProcessCollectorRawOutput(
        string UptimeOutput,
        string PsOutput,
        string CgroupOutput,
        string DockerPsAllOutput,
        bool DockerInstalled,
        bool DockerDaemonRunning,
        bool ExtendedPsColumns);
}
