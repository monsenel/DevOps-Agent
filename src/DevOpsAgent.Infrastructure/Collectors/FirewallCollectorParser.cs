using System.Text.RegularExpressions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static partial class FirewallCollectorParser
{
    public const string SectionPrefix = "__DVA_FW__:";

    private static readonly Dictionary<string, (string Port, string Protocol)> WellKnownFirewalldServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh"] = ("22", "tcp"),
        ["http"] = ("80", "tcp"),
        ["https"] = ("443", "tcp"),
        ["dns"] = ("53", "udp"),
        ["postgresql"] = ("5432", "tcp"),
        ["mysql"] = ("3306", "tcp"),
        ["redis"] = ("6379", "tcp"),
        ["smtp"] = ("25", "tcp"),
        ["imap"] = ("143", "tcp"),
        ["imaps"] = ("993", "tcp")
    };

    [GeneratedRegex(@"^(?<to>.+?)\s+(?<action>ALLOW|DENY|REJECT|LIMIT)\s+(?<dir>IN|OUT|FWD)\s+(?<from>.+?)(?:\s+#\s*(?<comment>.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UfwRuleRegex();

    [GeneratedRegex(@"(?:ip6?\s+saddr\s+(?<src>\S+)\s+)?(?<proto>tcp|udp)\s+dport\s+(?:\{\s*(?<ports>[^}]+)\s*\}|(?<port>\d+(?:-\d+)?))\s+(?<action>accept|drop|reject)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NftRuleRegex();

    [GeneratedRegex(@"policy\s+(?<policy>accept|drop|reject)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NftPolicyRegex();

    [GeneratedRegex(@"^chain\s+(?<name>\S+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NftChainRegex();

    public static FirewallDiscoveryResult Parse(string output)
    {
        var sections = SplitSections(output);
        if (sections.TryGetValue("ufw", out var ufwText) && TryParseUfw(ufwText, out var ufwResult))
        {
            return ufwResult;
        }

        if (sections.TryGetValue("firewalld", out var firewalldText) && TryParseFirewalld(firewalldText, out var firewalldResult))
        {
            return firewalldResult;
        }

        if (sections.TryGetValue("nft", out var nftText) && TryParseNftables(nftText, out var nftResult))
        {
            return nftResult;
        }

        if (sections.TryGetValue("iptables", out var iptablesText) && TryParseIptables(iptablesText, out var iptablesResult))
        {
            return iptablesResult;
        }

        return new FirewallDiscoveryResult("none", false, null, null, []);
    }

    private static Dictionary<string, string> SplitSections(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (current is not null)
            {
                result[current] = string.Join('\n', buffer);
            }

            buffer.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith(SectionPrefix, StringComparison.Ordinal))
            {
                Flush();
                current = line[SectionPrefix.Length..].Trim();
            }
            else if (current is not null)
            {
                buffer.Add(line);
            }
        }

        Flush();
        return result;
    }

    private static bool TryParseUfw(string text, out FirewallDiscoveryResult result)
    {
        result = default!;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        var statusLine = lines.FirstOrDefault(line => line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase));
        if (statusLine is null) return false;

        var enabled = statusLine.Contains("active", StringComparison.OrdinalIgnoreCase)
            && !statusLine.Contains("inactive", StringComparison.OrdinalIgnoreCase);
        string? defaultIn = null;
        string? defaultOut = null;

        var defaultLine = lines.FirstOrDefault(line => line.StartsWith("Default:", StringComparison.OrdinalIgnoreCase));
        if (defaultLine is not null)
        {
            foreach (var part in defaultLine["Default:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;
                if (part.Contains("incoming", StringComparison.OrdinalIgnoreCase)) defaultIn = tokens[0].ToLowerInvariant();
                else if (part.Contains("outgoing", StringComparison.OrdinalIgnoreCase)) defaultOut = tokens[0].ToLowerInvariant();
            }
        }

        var rules = new List<FirewallRuleInfo>();
        foreach (var line in lines)
        {
            var match = UfwRuleRegex().Match(line);
            if (!match.Success) continue;

            var to = match.Groups["to"].Value.Replace("(v6)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            var from = match.Groups["from"].Value.Replace("(v6)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            var action = match.Groups["action"].Value.ToLowerInvariant();
            var dir = match.Groups["dir"].Value.ToLowerInvariant() switch
            {
                "in" => "in",
                "out" => "out",
                _ => "forward"
            };
            var comment = match.Groups["comment"].Success ? match.Groups["comment"].Value.Trim() : null;
            var (port, protocol) = SplitPortProtocol(to);
            rules.Add(new FirewallRuleInfo(dir, action, protocol, port, from, comment));
        }

        result = new FirewallDiscoveryResult("ufw", enabled, defaultIn, defaultOut, DeduplicateRules(rules));
        return true;
    }

    private static bool TryParseFirewalld(string text, out FirewallDiscoveryResult result)
    {
        result = default!;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;
        if (!lines.Any(line => line.Equals("running", StringComparison.OrdinalIgnoreCase)
            || line.Equals("not running", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var enabled = lines.Any(line => line.Equals("running", StringComparison.OrdinalIgnoreCase));
        var rules = new List<FirewallRuleInfo>();
        string? source = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("sources:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["sources:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value)) source = value;
            }
            else if (line.StartsWith("services:", StringComparison.OrdinalIgnoreCase))
            {
                var services = line["services:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var service in services)
                {
                    if (WellKnownFirewalldServices.TryGetValue(service, out var mapped))
                    {
                        rules.Add(new FirewallRuleInfo("in", "allow", mapped.Protocol, mapped.Port, source ?? "Anywhere", service));
                    }
                    else
                    {
                        rules.Add(new FirewallRuleInfo("in", "allow", "any", service, source ?? "Anywhere", service));
                    }
                }
            }
            else if (line.StartsWith("ports:", StringComparison.OrdinalIgnoreCase))
            {
                var ports = line["ports:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var entry in ports)
                {
                    var (port, protocol) = SplitPortProtocol(entry);
                    rules.Add(new FirewallRuleInfo("in", "allow", protocol, port, source ?? "Anywhere"));
                }
            }
        }

        result = new FirewallDiscoveryResult("firewalld", enabled, enabled ? "reject" : null, enabled ? "allow" : null, DeduplicateRules(rules));
        return true;
    }

    private static bool TryParseNftables(string text, out FirewallDiscoveryResult result)
    {
        result = default!;
        if (!text.Contains("table ", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("chain ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? currentDirection = null;
        string? defaultIn = null;
        string? defaultOut = null;
        var rules = new List<FirewallRuleInfo>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var chainMatch = NftChainRegex().Match(line);
            if (chainMatch.Success)
            {
                var name = chainMatch.Groups["name"].Value.ToLowerInvariant();
                currentDirection = name.Contains("in") ? "in"
                    : name.Contains("out") ? "out"
                    : name.Contains("forward") ? "forward"
                    : null;
                continue;
            }

            if (line == "}")
            {
                currentDirection = null;
                continue;
            }

            if (currentDirection is null) continue;

            var policyMatch = NftPolicyRegex().Match(line);
            if (policyMatch.Success)
            {
                var policy = policyMatch.Groups["policy"].Value.ToLowerInvariant();
                if (currentDirection == "in") defaultIn ??= policy;
                else if (currentDirection == "out") defaultOut ??= policy;
                continue;
            }

            var ruleMatch = NftRuleRegex().Match(line);
            if (!ruleMatch.Success) continue;

            var protocol = ruleMatch.Groups["proto"].Value.ToLowerInvariant();
            var action = ruleMatch.Groups["action"].Value.ToLowerInvariant();
            var source = ruleMatch.Groups["src"].Success ? ruleMatch.Groups["src"].Value : "Anywhere";
            var ports = ruleMatch.Groups["ports"].Success
                ? ruleMatch.Groups["ports"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : [ruleMatch.Groups["port"].Value];

            foreach (var port in ports)
            {
                rules.Add(new FirewallRuleInfo(currentDirection, action, protocol, port, source));
            }
        }

        var enabled = defaultIn is "drop" or "reject" || rules.Count > 0;
        result = new FirewallDiscoveryResult("nftables", enabled, defaultIn, defaultOut, DeduplicateRules(rules));
        return true;
    }

    private static bool TryParseIptables(string text, out FirewallDiscoveryResult result)
    {
        result = default!;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!lines.Any(line => line.StartsWith("-P ", StringComparison.Ordinal) || line.StartsWith("-A ", StringComparison.Ordinal)))
        {
            return false;
        }

        string? defaultIn = null;
        string? defaultOut = null;
        var rules = new List<FirewallRuleInfo>();

        foreach (var line in lines)
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && tokens[0] == "-P")
            {
                var policy = tokens[2].ToLowerInvariant();
                if (tokens[1].Equals("INPUT", StringComparison.OrdinalIgnoreCase)) defaultIn = policy;
                else if (tokens[1].Equals("OUTPUT", StringComparison.OrdinalIgnoreCase)) defaultOut = policy;
                continue;
            }

            if (tokens.Length < 4 || tokens[0] != "-A") continue;
            var chain = tokens[1].ToUpperInvariant();
            var direction = chain switch
            {
                "INPUT" => "in",
                "OUTPUT" => "out",
                "FORWARD" => "forward",
                _ => null
            };
            if (direction is null) continue;

            var protocol = "any";
            string? port = null;
            string? source = null;
            string? action = null;

            for (var index = 2; index < tokens.Length - 1; index++)
            {
                switch (tokens[index])
                {
                    case "-p":
                        protocol = tokens[++index].ToLowerInvariant();
                        break;
                    case "-s":
                        source = tokens[++index];
                        break;
                    case "--dport" or "--dports":
                        port = tokens[++index];
                        break;
                    case "-j":
                        action = tokens[++index].ToLowerInvariant();
                        break;
                }
            }

            if (action is null || port is null) continue;
            if (action is "accept" or "drop" or "reject")
            {
                rules.Add(new FirewallRuleInfo(direction, action, protocol, port, source ?? "Anywhere"));
            }
        }

        var enabled = defaultIn is "drop" or "reject" || rules.Count > 0;
        result = new FirewallDiscoveryResult("iptables", enabled, defaultIn, defaultOut, DeduplicateRules(rules));
        return true;
    }

    private static (string? Port, string Protocol) SplitPortProtocol(string value)
    {
        var slash = value.IndexOf('/');
        if (slash > 0 && slash < value.Length - 1)
        {
            return (value[..slash].Trim(), value[(slash + 1)..].Trim().ToLowerInvariant());
        }

        return (value.Trim(), "any");
    }

    private static IReadOnlyList<FirewallRuleInfo> DeduplicateRules(IReadOnlyList<FirewallRuleInfo> rules) =>
        rules.DistinctBy(rule => (rule.Direction, rule.Action, rule.Protocol, rule.Port, rule.Source)).ToArray();
}
