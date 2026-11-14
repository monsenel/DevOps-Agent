using System.Globalization;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class SystemdCollectorParser
{
    public static IReadOnlyList<string> ParseRunningServiceNames(string output)
    {
        var services = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 5 && columns[0].EndsWith(".service", StringComparison.Ordinal))
            {
                services.Add(columns[0]);
            }
        }

        return services.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static SystemdUnitInfo? ParseUnit(string name, string output, IReadOnlySet<int> listenerPids)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                properties[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
            }
        }

        if (!properties.TryGetValue("MainPID", out var mainPidText) ||
            !int.TryParse(mainPidText, NumberStyles.None, CultureInfo.InvariantCulture, out var mainPid) ||
            mainPid <= 0 || !listenerPids.Contains(mainPid))
        {
            return null;
        }

        return new SystemdUnitInfo(
            name,
            mainPid,
            properties.GetValueOrDefault("ExecStart"),
            properties.GetValueOrDefault("FragmentPath"),
            [mainPid]);
    }

    public static SystemdDiscoveryResult Parse(
        string runningUnitsOutput,
        IReadOnlyDictionary<string, string> unitShowOutputs,
        IReadOnlySet<int> listenerPids)
    {
        var units = ParseRunningServiceNames(runningUnitsOutput)
            .Where(unitShowOutputs.ContainsKey)
            .Select(name => ParseUnit(name, unitShowOutputs[name], listenerPids))
            .Where(unit => unit is not null)
            .Select(unit => unit!)
            .ToArray();
        return new SystemdDiscoveryResult(units);
    }
}
