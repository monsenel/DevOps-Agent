using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

public sealed class ServerCapabilityService(
    ISshRunner sshRunner,
    IServerRepository serverRepository,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    // A password sudo is not started with less than 20 s left (T7.R25), so the sudo probe gets more than that.
    private static readonly TimeSpan SudoProbeTimeout = TimeSpan.FromSeconds(30);
    private const string OsReleaseCommand = "cat /etc/os-release";
    private const string UptimeCommand = "uptime -p";
    // Admin tools such as nginx, iptables and nft live in /usr/sbin or /sbin, which a non-root SSH session's PATH often omits.
    private static readonly string[] ProbedCommands = ["docker", "nginx", "systemctl", "ss", "ip", "iptables", "nft"];
    public const string AvailableCommandsCommand = "PATH=\"$PATH:/usr/sbin:/sbin\"; for cmd in docker nginx systemctl ss ip iptables nft; do command -v \"$cmd\" >/dev/null 2>&1 && printf '%s\\n' \"$cmd\"; done";

    public async Task<ServerCapabilityResult> TestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var testedAt = timeProvider.GetUtcNow();
        try
        {
            var osRelease = await sshRunner.RunAsync(serverId, OsReleaseCommand,
                new SshRunOptions(ProbeTimeout, BypassBackoff: true), cancellationToken);
            var uptime = await sshRunner.RunAsync(serverId, UptimeCommand,
                new SshRunOptions(ProbeTimeout, BypassBackoff: true), cancellationToken);
            var server = await serverRepository.GetAsync(serverId, cancellationToken);
            if (server is null)
            {
                return new ServerCapabilityResult(serverId, false, null, null, null, [], "Server no longer exists.", testedAt);
            }

            // `sudo -n true`, or with a stored sudo password one `sudo -S` attempt. A password that was already rejected is not
            // tried again, not even from here (D-040 amendment): one wrong password can cost two pam_faillock failures, so only
            // an edit or removal of the password lifts the pause. The previous sudo capability is then kept. An attempt whose
            // outcome was unknown is tried once more: that is what this button is for (T7.R25).
            var sudoSkipped = server.HasSudoPassword && server.SudoPasswordRejectedAtUtc is not null;
            RemoteCommandResult? sudo = null;
            if (!sudoSkipped)
            {
                try
                {
                    sudo = await sshRunner.ProbeSudoAsync(serverId, new SshRunOptions(SudoProbeTimeout, BypassBackoff: true), cancellationToken);
                }
                catch (Exception exception) when (server.HasSudoPassword && exception is not OperationCanceledException)
                {
                    // The password probe timed out or lost its connection: its outcome is recorded as unknown and shown below.
                }
            }

            var availableCommands = await sshRunner.RunAsync(serverId, AvailableCommandsCommand,
                new SshRunOptions(ProbeTimeout, BypassBackoff: true), cancellationToken);

            var osInfo = ParseOsInfo(osRelease.StandardOutput);
            // A probe that did not run to a confirmed or rejected answer keeps the previous capability, like a skipped one.
            bool? sudoCapable = sudo is null ? null : sudo.ExitCode == 0;
            // Reloaded: the probe may just have recorded a rejection in another scope.
            server = await serverRepository.GetAsync(serverId, cancellationToken);
            if (server is null)
            {
                return new ServerCapabilityResult(serverId, false, null, null, null, [], "Server no longer exists.", testedAt);
            }

            if (server.HasSudoPassword && server.SudoPasswordOutcomeUnknownAtUtc is not null) sudoCapable = null;
            server.RecordCapabilities(sudoCapable, osInfo, testedAt);
            await serverRepository.SaveAsync(server, cancellationToken);
            return new ServerCapabilityResult(serverId, true, osInfo, uptime.StandardOutput.Trim(), sudoCapable ?? server.SudoCapable,
                ParseAvailableCommands(availableCommands.StandardOutput), null, testedAt,
                server.HasSudoPassword, server.HasSudoPassword && server.SudoPasswordRejectedAtUtc is not null, sudoSkipped,
                server.HasSudoPassword && server.SudoPasswordOutcomeUnknownAtUtc is not null);
        }
        catch (OperationCanceledException exception) when (exception is SshShutdownException || cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (SshFailures.HostKeyMismatch(exception) is { } mismatch)
        {
            var fingerprint = mismatch.Fingerprint is null ? "Fingerprint unavailable" : $"SHA256:{mismatch.Fingerprint}";
            return new ServerCapabilityResult(serverId, false, null, null, null, [],
                $"SSH host key for {mismatch.Host}:{mismatch.Port} is not pinned ({fingerprint}). Verify it independently, then add its entry to /keys/known_hosts.", testedAt);
        }
        catch (HostUnreachableException exception)
        {
            // "Test connection" always attempts the route; this is the failure it just recorded, with the new backoff and
            // its real cause (T7.R19): a bad key is reported as a bad key, not as an unreachable host.
            return new ServerCapabilityResult(serverId, false, null, null, null, [], DescribeFailure(exception), testedAt);
        }
        catch (SshAuthFailedException exception)
        {
            return new ServerCapabilityResult(serverId, false, null, null, null, [],
                $"Authentication failed ({exception.Message}). {AuthHint}", testedAt);
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

    private const string AuthHint = "Check the identity key file, its passphrase, and that the key is authorised for the user on the server.";
    private const string ConnectHint = "Check the host, identity key, jump server, and pinned host key.";

    // Only safe text: the exception's message is built by the pool from fixed wording and the cause comes from a fixed list.
    private static string DescribeFailure(HostUnreachableException exception)
    {
        if (exception.IsAuthFailure) return $"{exception.Message} {AuthHint}";
        var cause = string.IsNullOrWhiteSpace(exception.Cause) ? string.Empty : $" Cause: {exception.Cause}.";
        return $"{exception.Message}{cause} {ConnectHint}";
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
        var expected = new HashSet<string>(ProbedCommands, StringComparer.Ordinal);
        return output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(expected.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
