namespace DevOpsAgent.Application.Updates;

/// <summary>
/// The UPD-2 report read from the output of <c>apply_updates</c>: which services restarted and whether a reboot is required.
/// The markers are printed by the command <c>ToolCommandBuilder</c> builds for that tool.
/// </summary>
public sealed record UpdateApplyReport(string ServerName, int? ExitCode, IReadOnlyList<string> RestartedServices, bool? RebootRequired, string? Error)
{
    private const string RestartedBegin = "RESTARTED_SERVICES_BEGIN";
    private const string RestartedEnd = "RESTARTED_SERVICES_END";
    private const string RebootPrefix = "REBOOT_REQUIRED=";

    public static UpdateApplyReport Parse(string serverName, int? exitCode, string standardOutput, string? error)
    {
        var lines = standardOutput.Split('\n', StringSplitOptions.TrimEntries);
        var begin = Array.IndexOf(lines, RestartedBegin);
        var end = begin < 0 ? -1 : Array.IndexOf(lines, RestartedEnd, begin + 1);
        IReadOnlyList<string> restarted = begin >= 0 && end > begin
            ? lines[(begin + 1)..end].Where(line => line.Length > 0).ToArray()
            : [];
        var reboot = lines.LastOrDefault(line => line.StartsWith(RebootPrefix, StringComparison.Ordinal)) switch
        {
            RebootPrefix + "true" => true,
            RebootPrefix + "false" => (bool?)false,
            _ => null
        };
        return new(serverName, exitCode, restarted, reboot, error);
    }

    public string Describe()
    {
        if (Error is not null || ExitCode is not 0)
            return $"{ServerName}: update failed ({Error ?? $"exit {ExitCode?.ToString() ?? "unknown"}"}).";
        var services = RestartedServices.Count == 0 ? "none" : string.Join(", ", RestartedServices);
        var reboot = RebootRequired switch { true => "yes", false => "no", null => "unknown" };
        return $"{ServerName}: updated (exit 0). Restarted services: {services}. Reboot required: {reboot}.";
    }
}
