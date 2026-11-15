using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Infrastructure.Monitoring;

/// <summary>
/// The fixed list of command lines the app itself sends through sudo (collectors, scans and agent tools). A sudo event by the
/// server's own SSH user is exempt from the "unusual sudo" alert only when its command is exactly one of these. Templates are
/// rendered from the same builders that send the commands, with sentinel values replaced by strictly typed holes (hex IDs,
/// numbers, unit/package names, IP addresses), so a command with anything appended or injected does not match.
/// </summary>
public static class AppSudoCommands
{
    // Declared first: static initializers run in order and BuildTemplates needs it.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int SentinelNumber = 1357924680;
    private const string SentinelUnit = "dvasentinelunit";
    private const string SentinelPackage = "dvasentinelpkg";
    private const string SentinelIpv4 = "192.0.2.254";
    private const string SentinelIpv6 = "2001:db8::fe";
    private const string SentinelPath = "/dvasentinelpath";

    private static readonly (string Sentinel, string Hole)[] Holes =
    [
        (SentinelNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), @"\d{1,12}"),
        ("'" + SentinelUnit + "'", "'[A-Za-z0-9][A-Za-z0-9@_.:-]{0,199}'"),
        ("'" + SentinelPackage + "'", "'[A-Za-z0-9][A-Za-z0-9.+:@_-]{0,199}'(?: '[A-Za-z0-9][A-Za-z0-9.+:@_-]{0,199}'){0,99}"),
        ("'" + SentinelIpv4 + "'", @"'[0-9.]{7,15}'"),
        ("'" + SentinelIpv6 + "'", @"'[0-9A-Fa-f:.]{2,45}'"),
        ("'" + SentinelPath + "'", @"'/[^'\n]{0,1023}'"),
        ("dvasentinelids", "[0-9a-f]{12,64}(?: [0-9a-f]{12,64}){0,499}"),
    ];

    private static readonly Regex[] Patterns = BuildTemplates().Select(ToPattern).ToArray();

    /// <summary>The command templates, for tests and documentation.</summary>
    public static IReadOnlyList<string> Templates { get; } = BuildTemplates();

    /// <summary>True when a command, as sudo logged it, is one of the app's own sudo command lines.</summary>
    public static bool IsAppCommand(string? loggedCommand) =>
        TryUnwrap(loggedCommand, out var inner) && Patterns.Any(pattern => pattern.IsMatch(inner));

    // The app runs every sudo command as `sudo -n -- sh -c '<command>'`; sudo logs it as `/usr/bin/sh -c '<escaped>'`,
    // escaping ' and \ with a backslash.
    private static bool TryUnwrap(string? logged, out string inner)
    {
        inner = string.Empty;
        if (string.IsNullOrEmpty(logged)) return false;
        var prefix = logged.StartsWith("/usr/bin/sh -c '", StringComparison.Ordinal) ? "/usr/bin/sh -c '"
            : logged.StartsWith("/bin/sh -c '", StringComparison.Ordinal) ? "/bin/sh -c '"
            : null;
        if (prefix is null || logged.Length <= prefix.Length || logged[^1] != '\'') return false;
        var body = logged.AsSpan(prefix.Length, logged.Length - prefix.Length - 1);
        var builder = new StringBuilder(body.Length);
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (character == '\\' && index + 1 < body.Length) character = body[++index];
            else if (character == '\'') return false; // an unescaped quote means more than one argument
            builder.Append(character);
        }

        inner = builder.ToString();
        return true;
    }

    private static string[] BuildTemplates()
    {
        var tools = new ToolCommandBuilder(new ToolRegistry());
        string Mutating(string tool, object args)
        {
            var command = tools.Build(new ActionRecord(tool, "sentinel", "[]", JsonSerializer.Serialize(args, JsonOptions), null, DateTimeOffset.UnixEpoch));
            return command.UseSudo ? command.Command : throw new InvalidOperationException($"{tool} does not use sudo.");
        }

        return
        [
            ListenerCollector.Command,
            NginxCollector.Command,
            DockerCollector.InfoCommand,
            DockerCollector.ContainerListCommand,
            DockerCollector.NetworkListCommand,
            DockerCollector.ContainerInspectCommand + " dvasentinelids",
            DockerCollector.NetworkInspectCommand + " dvasentinelids",
            ConnectionCollector.NamespaceCommand(SentinelNumber),
            AuthLogScanner.BuildCommand(DateTimeOffset.FromUnixTimeSeconds(SentinelNumber)),
            UpdatesCollector.AptScanCommand(SentinelNumber),
            Application.Monitoring.ContainerStatusQuery.Command,
            AlertSignalSource.StatusCommand,
            tools.BuildReadOnly("disk_usage", new DiskUsageArgs()).Command,
            tools.BuildReadOnly("disk_usage", new DiskUsageArgs(SentinelPath)).Command,
            Mutating("restart_service", new { unit = SentinelUnit }),
            Mutating("apply_updates", new { packages = new[] { SentinelPackage } }),
            Mutating("block_ip", new { address = SentinelIpv4 }),
            Mutating("block_ip", new { address = SentinelIpv6 }),
            Mutating("unblock_ip", new { address = SentinelIpv4 }),
            Mutating("unblock_ip", new { address = SentinelIpv6 }),
        ];
    }

    private static Regex ToPattern(string template)
    {
        var pattern = Regex.Escape(template);
        foreach (var (sentinel, hole) in Holes) pattern = pattern.Replace(Regex.Escape(sentinel), hole, StringComparison.Ordinal);
        return new Regex(@"\A" + pattern + @"\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
