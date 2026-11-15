using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class DockerCollector : ICollector
{
    public const string InfoCommand = "docker info >/dev/null 2>&1";
    public const string ContainerListCommand = "docker ps -q";
    public const string NetworkListCommand = "docker network ls -q";
    public const string ContainerInspectCommand = "docker inspect";
    public const string NetworkInspectCommand = "docker network inspect";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex DockerIdPattern = new("^[a-fA-F0-9]{12,64}$", RegexOptions.CultureInvariant);

    public string Name => "docker";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var executable = await context.SshRunner.RunAsync(context.Server.Id, "command -v docker >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        if (executable.ExitCode != 0)
        {
            return false;
        }

        var userAccess = await context.SshRunner.RunAsync(context.Server.Id, InfoCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        if (userAccess.ExitCode == 0)
        {
            return true;
        }

        if (context.Server.SudoCapable != true)
        {
            return false;
        }

        var sudoAccess = await context.SshRunner.RunAsync(context.Server.Id, InfoCommand,
            new SshRunOptions(CommandTimeout, UseSudo: true), cancellationToken);
        return sudoAccess.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var duration = TimeSpan.Zero;
        var useSudo = context.Server.SudoCapable == true;

        var containerList = await RunAsync(ContainerListCommand);
        var containerIds = ParseIds("container", containerList, errors);
        var containerInspectJson = await InspectAsync(containerIds, ContainerInspectCommand);

        var networkList = await RunAsync(NetworkListCommand);
        var networkIds = ParseIds("network", networkList, errors);
        var networkInspectJson = await InspectAsync(networkIds, NetworkInspectCommand);

        try
        {
            containerInspectJson = RedactEnvironmentValues(containerInspectJson);
        }
        catch (JsonException)
        {
            containerInspectJson = "[]";
            errors.Add("container inspect: output could not be safely sanitized and was discarded.");
        }

        var raw = JsonSerializer.Serialize(new DockerCollectorRawOutput(containerInspectJson, networkInspectJson));
        return new CollectorOutput(raw, string.Join(Environment.NewLine, errors), errors.Count == 0 ? 0 : 1, duration);

        async Task<RemoteCommandResult> RunAsync(string command)
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id, command,
                new SshRunOptions(CommandTimeout, useSudo), cancellationToken);
            duration += result.Duration;
            if (result.ExitCode != 0)
            {
                errors.Add($"{command}: {(string.IsNullOrWhiteSpace(result.StandardError) ? $"exit {result.ExitCode?.ToString() ?? "unknown"}" : result.StandardError.Trim())}");
            }

            return result;
        }

        async Task<string> InspectAsync(IReadOnlyList<string> ids, string commandPrefix)
        {
            if (ids.Count == 0)
            {
                return "[]";
            }

            var result = await RunAsync($"{commandPrefix} {string.Join(' ', ids)}");
            return result.ExitCode == 0 ? result.StandardOutput : "[]";
        }
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<DockerCollectorRawOutput>(output.StandardOutput);
            if (raw is null)
            {
                return new CollectorResult(Error: "Docker collector output is empty.");
            }

            return new CollectorResult(JsonSerializer.SerializeToElement(
                DockerCollectorParser.Parse(raw.ContainersJson, raw.NetworksJson)));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid Docker collector output: {exception.Message}");
        }
        catch (FormatException exception)
        {
            return new CollectorResult(Error: exception.Message);
        }
    }

    private static IReadOnlyList<string> ParseIds(string kind, RemoteCommandResult result, List<string> errors)
    {
        if (result.ExitCode != 0)
        {
            return [];
        }

        var ids = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Any(id => !DockerIdPattern.IsMatch(id)))
        {
            errors.Add($"{kind} list: Docker returned an invalid ID; inspect was skipped.");
            return [];
        }

        return ids;
    }

    private static string RedactEnvironmentValues(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray containers)
        {
            throw new JsonException("Docker inspect output was not an array.");
        }

        foreach (var container in containers)
        {
            if (container?["Config"]?["Env"] is not JsonArray environment)
            {
                continue;
            }

            for (var index = 0; index < environment.Count; index++)
            {
                if (environment[index] is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var variable))
                {
                    continue;
                }

                var separator = variable.IndexOf('=');
                environment[index] = separator < 0 ? variable : variable[..separator];
            }
        }

        return containers.ToJsonString();
    }

    private sealed record DockerCollectorRawOutput(string ContainersJson, string NetworksJson);
}
