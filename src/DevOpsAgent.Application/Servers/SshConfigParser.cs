namespace DevOpsAgent.Application.Servers;

public static class SshConfigParser
{
    private static readonly HashSet<string> SupportedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "HostName", "User", "Port", "IdentityFile", "ProxyJump"
    };

    public static SshConfigParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var globalOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, HostOptions>(StringComparer.OrdinalIgnoreCase);
        var orderedAliases = new List<string>();
        List<string>? activeAliases = null;
        var skippedWildcardHostCount = 0;

        foreach (var line in text.Split('\n'))
        {
            var tokens = Tokenize(line);
            if (tokens.Count < 2)
            {
                continue;
            }

            var directive = tokens[0];
            if (directive.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                activeAliases = [];
                foreach (var pattern in tokens.Skip(1))
                {
                    if (pattern.StartsWith('!'))
                    {
                        continue;
                    }

                    if (pattern.IndexOfAny(['*', '?']) >= 0)
                    {
                        skippedWildcardHostCount++;
                        continue;
                    }

                    if (!hosts.ContainsKey(pattern))
                    {
                        var options = new HostOptions();
                        foreach (var (name, optionValue) in globalOptions)
                        {
                            options.Values[name] = optionValue;
                        }

                        hosts.Add(pattern, options);
                        orderedAliases.Add(pattern);
                    }

                    activeAliases.Add(pattern);
                }

                continue;
            }

            if (!SupportedOptions.Contains(directive))
            {
                continue;
            }

            var value = string.Join(' ', tokens.Skip(1));
            if (activeAliases is null)
            {
                globalOptions.TryAdd(directive, value);
                continue;
            }

            foreach (var alias in activeAliases)
            {
                hosts[alias].Values.TryAdd(directive, value);
            }
        }

        var entries = new List<SshConfigEntry>(orderedAliases.Count);
        foreach (var alias in orderedAliases)
        {
            var values = hosts[alias].Values;
            var port = 22;
            if (values.TryGetValue("Port", out var portValue)
                && (!int.TryParse(portValue, out port) || port is < 1 or > 65535))
            {
                throw new FormatException($"SSH config host '{alias}' has an invalid Port value.");
            }

            values.TryGetValue("HostName", out var hostName);
            values.TryGetValue("User", out var user);
            values.TryGetValue("IdentityFile", out var identityFile);
            values.TryGetValue("ProxyJump", out var proxyJump);

            entries.Add(new SshConfigEntry(
                alias,
                string.IsNullOrWhiteSpace(hostName) ? alias : hostName,
                port,
                user ?? string.Empty,
                identityFile ?? string.Empty,
                string.Equals(proxyJump, "none", StringComparison.OrdinalIgnoreCase) ? null : proxyJump));
        }

        return new SshConfigParseResult(entries, skippedWildcardHostCount);
    }

    private static IReadOnlyList<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var token = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && character == '#')
            {
                break;
            }

            if (character == '\\' && index + 1 < line.Length)
            {
                token.Append(line[++index]);
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(character))
            {
                AddToken();
                continue;
            }

            token.Append(character);
        }

        AddToken();
        return tokens;

        void AddToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            tokens.Add(token.ToString());
            token.Clear();
        }
    }

    private sealed class HostOptions
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record SshConfigEntry(
    string Alias,
    string HostName,
    int Port,
    string User,
    string IdentityFile,
    string? ProxyJump);

public sealed record SshConfigParseResult(
    IReadOnlyList<SshConfigEntry> Entries,
    int SkippedWildcardHostCount);
