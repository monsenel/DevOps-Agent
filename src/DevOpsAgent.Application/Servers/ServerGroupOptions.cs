namespace DevOpsAgent.Application.Servers;

public sealed record ServerGroupOption(string Value, string Label);

public static class ServerGroupOptions
{
    /// <summary>The casing the domain stores groups in (trimmed, lower-case); legacy rows may differ.</summary>
    public static string Normalize(string? group) => (group ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The selectable groups: the built-in prod and staging, every group already in use, and the form's current group.</summary>
    public static IReadOnlyList<ServerGroupOption> Build(IEnumerable<string> existingGroups, string? currentGroup)
    {
        var options = new List<ServerGroupOption> { new("prod", "Production"), new("staging", "Staging") };
        foreach (var group in existingGroups.Append(currentGroup ?? string.Empty)
                     .Select(Normalize)
                     .Where(group => group.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!options.Any(option => string.Equals(option.Value, group, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new ServerGroupOption(group, group));
            }
        }

        return options;
    }
}
