using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

public sealed class ServerCapabilityService(
    ISshRunner sshRunner,
    IServerRepository serverRepository,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private const string OsReleaseCommand = "cat /etc/os-release";
    private const string UptimeCommand = "uptime -p";
    private const string SudoProbeCommand = "sudo -n true";
    private const string AvailableCommandsCommand = "for cmd in docker nginx systemctl ss ip; do command -v \"$cmd\" >/dev/null 2>&1 && printf '%s\\n' \"$cmd\"; done";

    public async Task<ServerCapabilityResult> TestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var testedAt = timeProvider.GetUtcNow();
        try
        {
            var osRelease = await sshRunner.RunAsync(serverId, OsReleaseCommand,
                new SshRunOptions(ProbeTimeout), cancellationToken);
            var uptime = await sshRunner.RunAsync(serverId, UptimeCommand,
                new SshRunOptions(ProbeTimeout), cancellationToken);
            var sudo = await sshRunner.RunAsync(serverId, SudoProbeCommand,
                new SshRunOptions(ProbeTimeout), cancellationToken);
            var availableCommands = await sshRunner.RunAsync(serverId, AvailableCommandsCommand,
                new SshRunOptions(ProbeTimeout), cancellationToken);

            var osInfo = ParseOsInfo(osRelease.StandardOutput);
            var sudoCapable = sudo.ExitCode == 0;
            var server = await serverRepository.GetAsync(serverId, cancellationToken);
            if (server is null)
            {
                return new ServerCapabilityResult(serverId, false, null, null, null, [], "Server no longer exists.", testedAt);
            }

            server.RecordCapabilities(sudoCapable, osInfo, testedAt);
            await serverRepository.SaveAsync(server, cancellationToken);
            return new ServerCapabilityResult(serverId, true, osInfo, uptime.StandardOutput.Trim(), sudoCapable,
                ParseAvailableCommands(availableCommands.StandardOutput), null, testedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SshHostKeyMismatchException exception)
        {
            var fingerprint = exception.Fingerprint is null ? "Fingerprint unavailable" : $"SHA256:{exception.Fingerprint}";
            return new ServerCapabilityResult(serverId, false, null, null, null, [],
                $"SSH host key for {exception.Host}:{exception.Port} is not pinned ({fingerprint}). Verify it independently, then add its entry to /keys/known_hosts.", testedAt);
        }
        catch (Exception)
        {
            return new ServerCapabilityResult(serverId, false, null, null, null, [],
                "Connection test failed. Check the host, identity key, jump server, and pinned host key.", testedAt);
        }
    }

    public static string? ParseOsInfo(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            if (name is not ("NAME" or "VERSION_ID"))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0)
            {
                values[name] = value;
            }
        }

        if (!values.TryGetValue("NAME", out var osName))
        {
            return null;
        }

        return values.TryGetValue("VERSION_ID", out var version)
            ? $"{osName} {version}"
            : osName;
    }

    public static IReadOnlyList<string> ParseAvailableCommands(string output)
    {
        var expected = new HashSet<string>(["docker", "nginx", "systemctl", "ss", "ip"], StringComparer.Ordinal);
        return output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(expected.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
