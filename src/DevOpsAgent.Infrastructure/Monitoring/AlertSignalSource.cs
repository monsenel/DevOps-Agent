using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Infrastructure.Monitoring;

public sealed class AlertSignalSource(
    IServerRepository servers,
    IMetricRepository metrics,
    IHealthCheckRepository healthChecks,
    ISnapshotRepository snapshots,
    ISshRunner sshRunner,
    IAuthLogScanSource authLogScanner,
    DriftService drift,
    ContainerRestartMonitor containerRestarts,
    TimeProvider timeProvider,
    MonitoringCycleOptions? cycleOptions = null) : IAlertSignalSource
{
    private const int MaxParallelServers = 8;

    /// <summary>The alert rule of a failed SSH login (D-039): separate from <c>server-unreachable</c>.</summary>
    public const string AuthFailedRule = "ssh-auth-failed";

    /// <summary>The alert rule of a rejected stored sudo password (D-040 amendment 2), Medium, one per server.</summary>
    public const string SudoAuthFailedRule = "sudo-auth-failed";

    private readonly MonitoringCycleOptions _cycle = cycleOptions ?? new MonitoringCycleOptions();

    public async Task<IReadOnlyList<AlertCondition>> GetActiveConditionsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var result = new List<AlertCondition>();
        var inventory = await servers.ListAsync(cancellationToken);

        // SSH phase, bounded in parallel (T7.R1): a host in backoff fails immediately, so one dead server never delays the
        // others. The status probe runs first. When it fails the auth-log scan is not run (T7.R17): a host that cannot answer
        // a 20 s status probe would only hold its slot for another 20 s, so its saved scan state is reported instead and its
        // recent security alerts stay open. The remote phase has a deadline shorter than the 30 s interval: a host still
        // being probed when it passes counts as a failed probe for this cycle, and the others are unaffected. Each scan has
        // its own scope; the database reads below stay sequential because the scoped repositories share one DbContext.
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cycle.CancelAfter(_cycle.AlertCycleDeadline);
        var cycleToken = cycle.Token;
        var cycleEnd = now + _cycle.AlertCycleDeadline;
        using var throttle = new SemaphoreSlim(MaxParallelServers);
        var remote = await Task.WhenAll(inventory.Select(async server =>
        {
            try
            {
                await throttle.WaitAsync(cycleToken);
                try
                {
                    var status = await TryGetRemoteStatusAsync(server, cycleEnd, cycleToken, cancellationToken);
                    var auth = status.Status is null
                        ? await authLogScanner.GetCachedAsync(server, cancellationToken)
                        : await ScanOrCachedAsync(server, cycleEnd, cycleToken, cancellationToken);
                    return (Server: server, Status: status, Auth: auth);
                }
                finally { throttle.Release(); }
            }
            catch (OperationCanceledException) when (cycleToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Out of time before this host's turn: a failed probe for this cycle, nothing else.
                var failed = (Status: (RemoteServiceStatus?)null, HostKeyMismatch: (SshHostKeyMismatchException?)null,
                    Unreachable: (HostUnreachableException?)null, ProbeFailure: (string?)DeadlineText());
                return (Server: server, Status: failed, Auth: await authLogScanner.GetCachedAsync(server, cancellationToken));
            }
        }));

        foreach (var (server, (remoteStatus, hostKeyMismatch, unreachable, probeFailure), auth) in remote)
        {
            result.AddRange(auth);
            result.AddRange(await containerRestarts.GetConditionsAsync(server.Id, server.Name, cancellationToken));
            // The exception names the host whose key mismatched. Behind a jump chain that can be the jump host, in which
            // case this server is merely unreachable (the jump host raises its own mismatch alert).
            var mismatchIsOwn = hostKeyMismatch is not null && hostKeyMismatch.Port == server.Port
                && string.Equals(hostKeyMismatch.Host, server.Host, StringComparison.OrdinalIgnoreCase);
            if (hostKeyMismatch is not null && !mismatchIsOwn)
            {
                result.Add(Condition("server-unreachable", server.Id.ToString("N"), AlertSeverity.High, server.Id, server.Host,
                    $"{server.Name} is unreachable",
                    $"The route to this server goes through {hostKeyMismatch.Host}:{hostKeyMismatch.Port}, which presented an unexpected SSH host key and was refused."));
            }
            else if (hostKeyMismatch is not null)
            {
                // A changed host key is a possible impersonation, not an outage: report it distinctly from "unreachable".
                result.Add(Condition("ssh-host-key-mismatch", server.Id.ToString("N"), AlertSeverity.Critical, server.Id, server.Host,
                    $"{server.Name} presented an unexpected SSH host key",
                    $"The host key of {hostKeyMismatch.Host}:{hostKeyMismatch.Port} does not match the pinned key; the connection was refused. "
                    + $"Presented fingerprint: {hostKeyMismatch.Fingerprint ?? "unknown"}. Verify the server before trusting the new key."));
            }
            else if (remoteStatus is null && unreachable is { IsAuthFailure: true })
            {
                // T7.R19 / D-039: a failed login is its own rule, "ssh-auth-failed" (own key per server, High like the outage
                // rule), so the Alerts page and the notification (rule name only, D-028) say what it is and the owner fixes the
                // key instead of hunting for a network problem. It never shares the key of "server-unreachable": a host that goes
                // from unreachable to auth failed (or back) resolves one alert and opens the other in the same evaluation, and a
                // dependent behind an auth-failed jump host gets its own ssh-auth-failed alert. While the retries are paused
                // (three failed logins in a row) the alert stays open and its details say so. The text is the pool's fixed
                // wording: no key file, path, passphrase or user name.
                result.Add(Condition(AuthFailedRule, server.Id.ToString("N"), AlertSeverity.High, server.Id, server.Host,
                    unreachable.ViaJumpServerName is null
                        ? $"{server.Name}: SSH authentication failed"
                        : $"{server.Name}: SSH authentication failed on jump host {unreachable.ViaJumpServerName}",
                    unreachable.Message));
            }
            else if (remoteStatus is null)
            {
                result.Add(Condition("server-unreachable", server.Id.ToString("N"), AlertSeverity.High, server.Id, server.Host,
                    $"{server.Name} is unreachable", unreachable?.Message
                        ?? $"The scheduled read-only SSH status probe failed: {probeFailure ?? "no usable answer"}."));
            }
            else
            {
                // A Docker permission failure must not look like "no containers" (T7.R18). The message is fixed text: no
                // output of the server is repeated. A stopped daemon or a host without Docker stays silent, as before.
                if (remoteStatus.Docker == DockerFailure.PermissionDenied)
                    result.Add(Condition("docker-status-unavailable", server.Id.ToString("N"), AlertSeverity.Medium, server.Id, "docker",
                        $"{server.Name}: container status could not be read",
                        "Docker refused the SSH user (permission denied on the Docker socket) and the non-interactive sudo fallback "
                        + (server.CanUseSudo ? "did not help" : "is not available for this server")
                        + ". Container alerts for this server are not working. Add the user to the docker group or allow passwordless sudo."));
                // A container caught "restarting" is the restart-loop rule's (medium) business, not a stopped container; paused,
                // created and removing containers have their own rules (container history, T7.R18). Everything else
                // (exited, dead, unknown states) raises the High alert as before.
                foreach (var container in remoteStatus.Containers.Where(item => item.State is not ("running" or "restarting" or "paused" or "created" or "removing")))
                    result.Add(Condition("container-state", $"{server.Id:N}:{container.Name}", AlertSeverity.High, server.Id,
                        container.Name, $"Container {container.Name} is {container.State}", $"Latest read-only Docker status reports: {container.State}."));
                foreach (var unit in remoteStatus.FailedUnits)
                    result.Add(Condition("unit-failed", $"{server.Id:N}:{unit}", AlertSeverity.High, server.Id,
                        unit, $"Service {unit} failed", "systemctl reports this service in its failed state."));

                var latest = await metrics.GetLatestAsync(server.Id, cancellationToken);
                if (latest.Count > 0)
                {
                var recent = await metrics.GetRecentAsync(server.Id, now - TimeSpan.FromMinutes(5) - SustainedThreshold.LookbackSlack, cancellationToken);
                AddThreshold(recent, "disk_percent", "disk", 90, TimeSpan.Zero, AlertSeverity.High, "Disk usage is at least 90%", result, server.Id, server.Name);
                AddThreshold(recent, "memory_percent", "memory", 90, TimeSpan.FromMinutes(5), AlertSeverity.High, "Memory usage has stayed at least 90% for 5 minutes", result, server.Id, server.Name);
                var cpuCores = latest.FirstOrDefault(item => item.Metric == "cpu_cores")?.Value;
                if (cpuCores is > 0)
                    AddThreshold(recent, "load_1m", "load", cpuCores.Value * 2, TimeSpan.FromMinutes(5), AlertSeverity.Medium,
                        $"Load has stayed above {cpuCores.Value * 2:0} for 5 minutes", result, server.Id, server.Name, strict: true);
                }
            }
        }

        foreach (var server in inventory)
        {
            // Read from the stored state, not from a probe, so it neither needs nor waits for SSH: open from the rejection until
            // a password sudo succeeds again or the password is removed (an edit alone is not proof). Fixed text only; never
            // the password, a command or server output.
            if (server.HasSudoPassword && server.SudoAuthFailedSinceUtc is { } sudoFailedSince)
                result.Add(Condition(SudoAuthFailedRule, server.Id.ToString("N"), AlertSeverity.Medium, server.Id, server.Host,
                    server.SudoPasswordOutcomeUnknownAtUtc is not null
                        ? $"{server.Name}: sudo password outcome unknown, paused"
                        : $"{server.Name}: sudo password rejected",
                    SudoAlertDetails(server, sudoFailedSince)));

            foreach (var check in await healthChecks.ListAsync(server.Id, cancellationToken))
            {
                if (check.LastStatus == HealthCheckStatus.Unhealthy)
                    result.Add(Condition("health-check-failing", check.Id.ToString("N"), AlertSeverity.High, server.Id,
                        check.Target, $"Health check {check.Name} is failing", check.LastError ?? "The latest check failed."));
                if (check.Type == HealthCheckType.Https && check.TlsDaysRemaining is int days && days < 14)
                    result.Add(Condition("tls-expiring", check.Id.ToString("N"), days < 0 ? AlertSeverity.High : AlertSeverity.Medium,
                        server.Id, check.Target, $"TLS certificate expires in {days} days", $"Certificate expiry is fewer than 14 days away."));
            }
        }

        var history = (await snapshots.ListAsync(cancellationToken)).Where(item => item.FinishedAtUtc is not null)
            .OrderByDescending(item => item.StartedAtUtc).Take(2).ToArray();
        if (history.Length == 2)
        {
            var changes = await drift.CompareAsync(history[1].Id, history[0].Id, cancellationToken);
            foreach (var change in changes.Changes.Where(item => item.EntityType == DriftEntityType.Listener && item.ChangeType == DriftChangeType.Added))
            {
                var hostId = ReadServerIdFromNodeKey(change.NodeKey);
                result.Add(Condition("new-listener", change.Key, AlertSeverity.Medium, hostId, change.NodeKey ?? change.Key,
                    "A new listener was discovered", change.Description));
            }
        }

        return result;
    }

    // Fixed text only (D-028 for the notification; the alert details never hold the password, a command or server output).
    private static string SudoAlertDetails(Server server, DateTimeOffset since)
    {
        if (server.SudoPasswordRejectedAtUtc is not null)
            return $"The saved sudo password was rejected at {since:yyyy-MM-dd HH:mm:ss} UTC. No sudo is used on this server until its sudo password is edited or removed.";
        if (server.SudoPasswordOutcomeUnknownAtUtc is { } unknownAt)
            return $"A sudo command with the saved password ended at {unknownAt:yyyy-MM-dd HH:mm:ss} UTC without confirming that sudo accepted the password "
                + "(timeout, lost connection, cancellation or an unknown answer). Password sudo is paused until Retry now, or until the password is edited or removed.";
        return $"Sudo with the saved password failed at {since:yyyy-MM-dd HH:mm:ss} UTC. The password has been edited or retried since; the alert resolves when sudo works with it.";
    }

    private string DeadlineText() =>
        $"it did not finish within the {_cycle.AlertCycleDeadline.TotalSeconds:0.#} s alert cycle deadline";

    private async Task<IReadOnlyList<AlertCondition>> ScanOrCachedAsync(Server server, DateTimeOffset cycleEnd, CancellationToken cycleToken, CancellationToken cancellationToken)
    {
        try
        {
            return await authLogScanner.ScanAsync(server, cycleToken, cycleEnd);
        }
        catch (OperationCanceledException exception) when (exception is not SshShutdownException && !cancellationToken.IsCancellationRequested)
        {
            // The cycle deadline passed during the scan: keep what the last scan saw.
            return await authLogScanner.GetCachedAsync(server, cancellationToken);
        }
    }

    /// <summary>
    /// The read-only status probe: Docker container states and failed systemd services. T7.R18: the Docker part reports its own
    /// failure through markers instead of swallowing it. "Permission denied" on the Docker socket prints
    /// <c>__DVA_DOCKER_DENIED__</c> (the caller retries with sudo), any other failure (daemon stopped) prints
    /// <c>__DVA_DOCKER_DOWN__</c>, and a host without Docker prints nothing, as before. The trailing <c>|| true</c> belongs to
    /// <c>systemctl</c> only: a host without systemd must not look like a failed probe.
    /// </summary>
    public const string StatusCommand =
        "printf '__DVA_DOCKER__\\n'; if command -v docker >/dev/null 2>&1; then "
        + "out=$(docker ps -a --format '{{.Names}}\\t{{.State}}' 2>&1); rc=$?; "
        + "if [ $rc -eq 0 ]; then printf '%s\\n' \"$out\"; "
        + "elif printf '%s' \"$out\" | grep -qi 'permission denied'; then printf '__DVA_DOCKER_DENIED__\\n'; "
        + "else printf '__DVA_DOCKER_DOWN__\\n'; fi; fi; "
        + "printf '__DVA_SYSTEMD_FAILED__\\n'; systemctl --failed --type=service --no-legend --plain 2>/dev/null || true";

    private async Task<(RemoteServiceStatus? Status, SshHostKeyMismatchException? HostKeyMismatch, HostUnreachableException? Unreachable, string? ProbeFailure)> TryGetRemoteStatusAsync(
        Server server, DateTimeOffset cycleEnd, CancellationToken cycleToken, CancellationToken cancellationToken)
    {
        var serverId = server.Id;
        try
        {
            var options = new SshRunOptions(_cycle.AlertProbeTimeout);
            var response = await sshRunner.RunAsync(serverId, StatusCommand, options, cycleToken);
            if (response.ExitCode != 0)
                return (null, null, null, $"the status command exited with code {response.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
            var status = RemoteServiceStatusParser.Parse(response.StandardOutput);
            // Same non-interactive sudo fallback as the metric cycle's container query, only when Docker refused the user
            // and the server is known to allow sudo. It is the same read-only command, so no server write happens.
            if (status.Docker == DockerFailure.PermissionDenied && server.CanUseSudo)
            {
                try
                {
                    // Bounded by the cycle, not by the probe timeout, so a password sudo (which needs 20 s left, T7.R25) can run.
                    var retry = await sshRunner.RunAsync(serverId, StatusCommand,
                        options with { Timeout = _cycle.AlertCycleDeadline, UseSudo = true, NotAfterUtc = cycleEnd }, cycleToken);
                    if (retry.ExitCode == 0) status = RemoteServiceStatusParser.Parse(retry.StandardOutput);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not HostUnreachableException
                    and not SshHostKeyMismatchException) { /* keep the refused result: it is reported below */ }
            }

            return (status, null, null, null);
        }
        catch (OperationCanceledException exception) when (exception is SshShutdownException || cancellationToken.IsCancellationRequested) { throw; }
        // The cycle deadline passed while the probe was running.
        catch (OperationCanceledException) { return (null, null, null, DeadlineText()); }
        // While a host is in backoff the original failure (for example a changed host key) travels with the exception,
        // so a host-key mismatch keeps its own alert instead of turning into "unreachable".
        catch (HostUnreachableException exception) { return (null, SshFailures.HostKeyMismatch(exception), exception, null); }
        catch (SshHostKeyMismatchException exception) { return (null, exception, null, null); }
        catch (Exception exception) { return (null, null, null, exception.Message); }
    }

    private static void AddThreshold(IReadOnlyList<MetricSample> samples, string metric, string key, double threshold,
        TimeSpan requiredDuration, AlertSeverity severity, string title, ICollection<AlertCondition> result, Guid serverId, string serverName, bool strict = false)
    {
        if (!SustainedThreshold.IsBreached(samples, metric, threshold, requiredDuration, strict, out var latestValue)) return;
        result.Add(Condition($"threshold-{key}", serverId.ToString("N"), severity, serverId, serverName,
            $"{serverName}: {title}", $"Latest {metric} value is {latestValue:0.0}."));
    }

    private static AlertCondition Condition(string rule, string targetKey, AlertSeverity severity, Guid? serverId,
        string target, string title, string details) => new($"{rule}:{targetKey}", rule, severity, serverId, target, title, details);

    private static Guid? ReadServerIdFromNodeKey(string? key)
    {
        if (key is null) return null;
        var pieces = key.Split(':');
        return pieces.Length > 1 && Guid.TryParse(pieces[1], out var serverId) ? serverId : null;
    }

}
