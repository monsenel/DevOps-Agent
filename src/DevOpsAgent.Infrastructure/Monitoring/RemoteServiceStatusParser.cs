namespace DevOpsAgent.Infrastructure.Monitoring;

/// <summary>Why the status probe could not list containers (T7.R18). Hosts without Docker are <see cref="None"/>.</summary>
public enum DockerFailure
{
    /// <summary>Docker answered, or is not installed on the host.</summary>
    None,

    /// <summary>Docker is installed but the SSH user may not use its socket.</summary>
    PermissionDenied,

    /// <summary>Docker is installed but did not answer for another reason (daemon stopped); treated like "no containers".</summary>
    DaemonUnavailable,
}

public sealed record RemoteContainerStatus(string Name, string State);
public sealed record RemoteServiceStatus(IReadOnlyList<RemoteContainerStatus> Containers, IReadOnlyList<string> FailedUnits,
    DockerFailure Docker = DockerFailure.None);

public static class RemoteServiceStatusParser
{
    public const string DockerMarker = "__DVA_DOCKER__";
    public const string DockerDeniedMarker = "__DVA_DOCKER_DENIED__";
    public const string DockerDownMarker = "__DVA_DOCKER_DOWN__";
    public const string SystemdMarker = "__DVA_SYSTEMD_FAILED__";

    public static RemoteServiceStatus Parse(string output)
    {
        var dockerStart = output.IndexOf(DockerMarker, StringComparison.Ordinal);
        var systemdStart = output.IndexOf(SystemdMarker, StringComparison.Ordinal);
        var dockerText = dockerStart < 0 ? string.Empty : output[(dockerStart + DockerMarker.Length)..(systemdStart < 0 ? output.Length : systemdStart)];
        var systemdText = systemdStart < 0 ? string.Empty : output[(systemdStart + SystemdMarker.Length)..];
        var failure = dockerText.Contains(DockerDeniedMarker, StringComparison.Ordinal) ? DockerFailure.PermissionDenied
            : dockerText.Contains(DockerDownMarker, StringComparison.Ordinal) ? DockerFailure.DaemonUnavailable
            : DockerFailure.None;
        var containers = dockerText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 3))
            .Where(parts => parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .Select(parts => new RemoteContainerStatus(parts[0], parts[1].Trim().ToLowerInvariant()))
            .ToArray();
        var units = systemdText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => parts.FirstOrDefault(part => part.EndsWith(".service", StringComparison.Ordinal)))
            .Where(name => name is not null).Select(name => name!).Distinct(StringComparer.Ordinal).ToArray();
        return new RemoteServiceStatus(containers, units, failure);
    }
}
