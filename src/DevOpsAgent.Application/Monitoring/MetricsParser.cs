using System.Globalization;

namespace DevOpsAgent.Application.Monitoring;

public static class MetricsParser
{
    private const string LoadMarker = "__DVA_LOADAVG__";
    private const string MemMarker = "__DVA_MEMINFO__";
    private const string CpuMarker = "__DVA_CPUSTAT__";
    private const string DiskMarker = "__DVA_DISK__";
    private const string NetworkMarker = "__DVA_NETDEV__";

    public static ParsedMetrics Parse(string output, ParsedMetrics? previous = null, TimeSpan? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var sections = SplitSections(output);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var cpu = ParseCpu(sections.GetValueOrDefault(CpuMarker));
        var network = ParseNetwork(sections.GetValueOrDefault(NetworkMarker));

        ParseLoad(sections.GetValueOrDefault(LoadMarker), values);
        ParseMemory(sections.GetValueOrDefault(MemMarker), values);
        ParseDisk(sections.GetValueOrDefault(DiskMarker), values);
        AddRates("network_receive_bytes_per_second", network, previous?.NetworkCounters, values, elapsed);
        AddRates("network_transmit_bytes_per_second", network, previous?.NetworkCounters, values, elapsed, transmit: true);
        if (previous is not null && cpu.Count != 0)
        {
            var currentTotal = cpu.Where(item => item.Key is not ("guest" or "guest_nice")).Sum(item => item.Value);
            var priorTotal = previous.CpuCounters.Where(item => item.Key is not ("guest" or "guest_nice")).Sum(item => item.Value);
            var currentIdle = cpu.GetValueOrDefault("idle") + cpu.GetValueOrDefault("iowait");
            var priorIdle = previous.CpuCounters.GetValueOrDefault("idle") + previous.CpuCounters.GetValueOrDefault("iowait");
            var totalDelta = currentTotal - priorTotal;
            var idleDelta = currentIdle - priorIdle;
            if (totalDelta > 0 && idleDelta >= 0 && idleDelta <= totalDelta)
            {
                values["cpu_percent"] = Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }
        }

        return new ParsedMetrics(values, cpu, network);
    }

    public static string CollectionCommand =>
        $"printf '{LoadMarker}\\n'; cat /proc/loadavg; " +
        $"printf '{MemMarker}\\n'; cat /proc/meminfo; " +
        $"printf '{CpuMarker}\\n'; head -1 /proc/stat; " +
        $"printf '{DiskMarker}\\n'; df -P; " +
        $"printf '{NetworkMarker}\\n'; cat /proc/net/dev";

    private static Dictionary<string, string> SplitSections(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var lines = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed is LoadMarker or MemMarker or CpuMarker or DiskMarker or NetworkMarker)
            {
                if (current is not null) sections[current] = string.Join('\n', lines);
                current = trimmed;
                lines.Clear();
            }
            else if (current is not null)
            {
                lines.Add(trimmed);
            }
        }
        if (current is not null) sections[current] = string.Join('\n', lines);
        return sections;
    }

    private static void ParseLoad(string? section, IDictionary<string, double> values)
    {
        var parts = section?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts is { Length: >= 3 } && TryDouble(parts[0], out var one) && TryDouble(parts[1], out var five) && TryDouble(parts[2], out var fifteen))
        {
            values["load_1m"] = one;
            values["load_5m"] = five;
            values["load_15m"] = fifteen;
        }
    }

    private static void ParseMemory(string? section, IDictionary<string, double> values)
    {
        var memory = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in (section ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2 && TryDouble(parts[1].Trim().Split(' ')[0], out var kib)) memory[parts[0]] = kib * 1024;
        }
        if (!memory.TryGetValue("MemTotal", out var total) || total <= 0) return;
        var available = memory.GetValueOrDefault("MemAvailable",
            memory.GetValueOrDefault("MemFree") + memory.GetValueOrDefault("Buffers") + memory.GetValueOrDefault("Cached"));
        values["memory_percent"] = Math.Clamp(100d * (total - available) / total, 0, 100);
        values["memory_used_bytes"] = Math.Max(0, total - available);
        values["memory_total_bytes"] = total;
    }

    private static void ParseDisk(string? section, IDictionary<string, double> values)
    {
        foreach (var line in (section ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || parts[^1] != "/" || !double.TryParse(parts[^2].TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)) continue;
            values["disk_percent"] = Math.Clamp(percent, 0, 100);
            if (double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)) values["disk_total_bytes"] = total * 1024;
            if (double.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used)) values["disk_used_bytes"] = used * 1024;
            return;
        }
    }

    private static Dictionary<string, long> ParseCpu(string? section)
    {
        var parts = section?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 5 } || parts[0] != "cpu") return new(StringComparer.Ordinal);
        var names = new[] { "user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal", "guest", "guest_nice" };
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < names.Length && i + 1 < parts.Length; i++)
            if (long.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var counter)) result[names[i]] = counter;
        return result;
    }

    private static Dictionary<string, long> ParseNetwork(string? section)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in (section ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var numbers = parts[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length < 9 || !long.TryParse(numbers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var received) ||
                !long.TryParse(numbers[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var transmitted)) continue;
            var iface = parts[0].Trim();
            result[$"{iface}:rx"] = received;
            result[$"{iface}:tx"] = transmitted;
        }
        return result;
    }

    private static void AddRates(string metric, IReadOnlyDictionary<string, long> current, IReadOnlyDictionary<string, long>? previous,
        IDictionary<string, double> values, TimeSpan? elapsed, bool transmit = false)
    {
        if (previous is null || elapsed is null || elapsed.Value.TotalSeconds <= 0) return;
        var ending = transmit ? ":tx" : ":rx";
        var delta = current.Where(item => item.Key.EndsWith(ending, StringComparison.Ordinal) && previous.ContainsKey(item.Key))
            .Sum(item => Math.Max(0, item.Value - previous[item.Key]));
        values[metric] = delta / elapsed.Value.TotalSeconds;
    }

    private static bool TryDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
