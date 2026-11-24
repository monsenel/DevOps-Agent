using System.Globalization;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static partial class ReverseProxyCollectorParser
{
    public const string SectionPrefix = "__DVA_PROXY__:";

    [GeneratedRegex(@"Host(?:SNI)?\(\s*[`""']([^`""']+)[`""']\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TraefikHostRuleRegex();

    public static ReverseProxyDiscoveryResult Parse(string output)
    {
        var routes = new List<ReverseProxyRouteInfo>();
        string? currentType = null;
        string? currentPath = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (currentType is null) return;
            switch (currentType.ToLowerInvariant())
            {
                case "haproxy":
                    routes.AddRange(ParseHaProxy(currentPath, buffer));
                    break;
                case "caddy":
                    routes.AddRange(ParseCaddy(currentPath, buffer));
                    break;
                case "traefik":
                    routes.AddRange(ParseTraefik(currentPath, buffer));
                    break;
            }

            buffer.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith(SectionPrefix, StringComparison.Ordinal))
            {
                Flush();
                var rest = line[SectionPrefix.Length..];
                var sep = rest.IndexOf(':');
                if (sep > 0)
                {
                    currentType = rest[..sep].Trim();
                    currentPath = rest[(sep + 1)..].Trim();
                }
                else
                {
                    currentType = null;
                    currentPath = null;
                }
            }
            else if (currentType is not null)
            {
                buffer.Add(line);
            }
        }

        Flush();
        return new ReverseProxyDiscoveryResult(routes);
    }

    private static IReadOnlyList<ReverseProxyRouteInfo> ParseHaProxy(string? filePath, IReadOnlyList<string> lines)
    {
        var frontends = new List<HaFrontend>();
        var backends = new Dictionary<string, List<ReverseProxyTargetInfo>>(StringComparer.OrdinalIgnoreCase);
        var listens = new List<HaListen>();

        HaFrontend? currentFrontend = null;
        List<ReverseProxyTargetInfo>? currentBackend = null;
        HaListen? currentListen = null;

        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var stripped = StripHashComment(lines[index]).Trim();
            if (stripped.Length == 0) continue;

            var tokens = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var keyword = tokens[0].ToLowerInvariant();
            if (keyword is "global" or "defaults" or "resolvers" or "peers" or "mailers" or "userlist")
            {
                currentFrontend = null;
                currentBackend = null;
                currentListen = null;
                continue;
            }

            if (keyword == "frontend" && tokens.Length >= 2)
            {
                currentFrontend = new HaFrontend(tokens[1], lineNumber);
                frontends.Add(currentFrontend);
                currentBackend = null;
                currentListen = null;
                continue;
            }

            if (keyword == "backend" && tokens.Length >= 2)
            {
                currentBackend = new List<ReverseProxyTargetInfo>();
                backends[tokens[1]] = currentBackend;
                currentFrontend = null;
                currentListen = null;
                continue;
            }

            if (keyword == "listen" && tokens.Length >= 2)
            {
                currentListen = new HaListen(tokens[1], lineNumber);
                listens.Add(currentListen);
                currentFrontend = null;
                currentBackend = null;
                continue;
            }

            if (currentFrontend is not null)
            {
                if (keyword == "bind" && tokens.Length >= 2)
                {
                    var (port, isSsl) = ParseHaBind(tokens);
                    currentFrontend.ListenPort ??= port;
                    if (isSsl) currentFrontend.IsSsl = true;
                }
                else if (keyword == "mode" && tokens.Length >= 2)
                {
                    currentFrontend.Mode = tokens[1].ToLowerInvariant();
                }
                else if (keyword == "acl" && tokens.Length >= 4)
                {
                    var aclName = tokens[1];
                    var criterion = tokens[2];
                    if (criterion.Contains("host", StringComparison.OrdinalIgnoreCase)
                        || criterion.Contains("sni", StringComparison.OrdinalIgnoreCase))
                    {
                        var domains = tokens.Skip(3).Where(token => !token.StartsWith('-')).ToArray();
                        if (domains.Length > 0)
                        {
                            if (!currentFrontend.AclDomains.TryGetValue(aclName, out var list))
                            {
                                list = [];
                                currentFrontend.AclDomains[aclName] = list;
                            }

                            list.AddRange(domains);
                        }
                    }
                }
                else if (keyword == "use_backend" && tokens.Length >= 2)
                {
                    var backendName = tokens[1];
                    var ifIndex = Array.FindIndex(tokens, token => token.Equals("if", StringComparison.OrdinalIgnoreCase));
                    var aclRefs = ifIndex >= 0 ? tokens.Skip(ifIndex + 1).ToArray() : [];
                    currentFrontend.UseBackends.Add((backendName, aclRefs, lineNumber));
                }
                else if (keyword == "default_backend" && tokens.Length >= 2)
                {
                    currentFrontend.DefaultBackend = tokens[1];
                }
            }
            else if (currentBackend is not null && keyword == "server" && tokens.Length >= 3)
            {
                currentBackend.Add(new ReverseProxyTargetInfo(UrlUserInfo.Strip(tokens[2]), tokens[1], lineNumber));
            }
            else if (currentListen is not null)
            {
                if (keyword == "bind" && tokens.Length >= 2)
                {
                    var (port, isSsl) = ParseHaBind(tokens);
                    currentListen.ListenPort ??= port;
                    if (isSsl) currentListen.IsSsl = true;
                }
                else if (keyword == "mode" && tokens.Length >= 2)
                {
                    currentListen.Mode = tokens[1].ToLowerInvariant();
                }
                else if (keyword == "server" && tokens.Length >= 3)
                {
                    currentListen.Targets.Add(new ReverseProxyTargetInfo(UrlUserInfo.Strip(tokens[2]), tokens[1], lineNumber));
                }
            }
        }

        var routes = new List<ReverseProxyRouteInfo>();
        foreach (var fe in frontends)
        {
            var protocol = fe.Mode == "tcp" ? "tcp" : (fe.IsSsl || fe.ListenPort == 443 ? "https" : "http");
            var emittedBackends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (backendName, aclRefs, lineNumber) in fe.UseBackends)
            {
                if (!backends.TryGetValue(backendName, out var targets) || targets.Count == 0) continue;
                var domains = aclRefs
                    .SelectMany(acl => fe.AclDomains.TryGetValue(acl, out var list) ? list : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                routes.Add(new ReverseProxyRouteInfo(
                    "haproxy", filePath, lineNumber, fe.Name, domains, fe.ListenPort, protocol, backendName, targets.ToArray()));
                emittedBackends.Add(backendName);
            }

            if (fe.DefaultBackend is not null && !emittedBackends.Contains(fe.DefaultBackend)
                && backends.TryGetValue(fe.DefaultBackend, out var defaultTargets) && defaultTargets.Count > 0)
            {
                routes.Add(new ReverseProxyRouteInfo(
                    "haproxy", filePath, fe.Line, fe.Name, [], fe.ListenPort, protocol, fe.DefaultBackend, defaultTargets.ToArray()));
            }
        }

        foreach (var listen in listens.Where(item => item.Targets.Count > 0))
        {
            var protocol = listen.Mode == "tcp" ? "tcp" : (listen.IsSsl || listen.ListenPort == 443 ? "https" : "http");
            routes.Add(new ReverseProxyRouteInfo(
                "haproxy", filePath, listen.Line, listen.Name, [], listen.ListenPort, protocol, listen.Name, listen.Targets.ToArray()));
        }

        return routes;
    }

    private static IReadOnlyList<ReverseProxyRouteInfo> ParseCaddy(string? filePath, IReadOnlyList<string> lines)
    {
        var routes = new List<ReverseProxyRouteInfo>();
        var depth = 0;
        string? siteHeader = null;
        var siteLine = 0;
        var siteTargets = new List<ReverseProxyTargetInfo>();
        var skipBlock = false;

        void FlushSite()
        {
            if (siteHeader is null || skipBlock || siteTargets.Count == 0)
            {
                siteHeader = null;
                siteTargets.Clear();
                skipBlock = false;
                return;
            }

            var (domains, port, protocol) = ParseCaddySiteHeader(siteHeader);
            routes.Add(new ReverseProxyRouteInfo(
                "caddy", filePath, siteLine, domains.FirstOrDefault() ?? siteHeader.Trim(),
                domains, port, protocol, null, siteTargets.DistinctBy(target => target.Address).ToArray()));
            siteHeader = null;
            siteTargets.Clear();
            skipBlock = false;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var stripped = StripHashComment(lines[index]).Trim();
            if (stripped.Length == 0) continue;

            if (depth == 0)
            {
                var openBrace = stripped.IndexOf('{');
                if (openBrace >= 0)
                {
                    var header = stripped[..openBrace].Trim();
                    skipBlock = header.Length == 0 || header.StartsWith('(');
                    siteHeader = header;
                    siteLine = lineNumber;
                    siteTargets.Clear();
                    depth += CountChar(stripped, '{') - CountChar(stripped, '}');
                    if (depth <= 0)
                    {
                        depth = 0;
                        FlushSite();
                    }
                }
                continue;
            }

            if (!skipBlock && stripped.StartsWith("reverse_proxy", StringComparison.OrdinalIgnoreCase))
            {
                var directiveLine = stripped.Split('{', 2)[0].Trim();
                var tokens = directiveLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var t = 1; t < tokens.Length; t++)
                {
                    var token = tokens[t];
                    // Skip Caddy request matchers (e.g. /api/*, @api, *)
                    if (t == 1 && (token.StartsWith('/') || token.StartsWith('@') || token == "*"))
                    {
                        continue;
                    }

                    if (token.StartsWith('{')) break;
                    siteTargets.Add(new ReverseProxyTargetInfo(UrlUserInfo.Strip(token), null, lineNumber));
                }
            }

            depth += CountChar(stripped, '{') - CountChar(stripped, '}');
            if (depth <= 0)
            {
                depth = 0;
                FlushSite();
            }
        }

        return routes;
    }

    private static IReadOnlyList<ReverseProxyRouteInfo> ParseTraefik(string? filePath, IReadOnlyList<string> lines)
    {
        var routes = new List<ReverseProxyRouteInfo>();
        var routers = new Dictionary<(string Section, string Name), TraefikRouter>();
        var services = new Dictionary<(string Section, string Name), List<ReverseProxyTargetInfo>>();

        string? rootSection = null; // "http" or "tcp"
        string? subSection = null;  // "routers" or "services"
        string? itemName = null;
        var itemIndent = -1;
        var inEntryPoints = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var rawLine = StripHashComment(lines[index]);
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            var indent = CountLeadingSpaces(rawLine);
            var trimmed = rawLine.Trim();

            if (indent == 0 && trimmed.EndsWith(':'))
            {
                var key = trimmed[..^1].Trim().ToLowerInvariant();
                rootSection = key is "http" or "tcp" ? key : null;
                subSection = null;
                itemName = null;
                itemIndent = -1;
                inEntryPoints = false;
                continue;
            }

            if (rootSection is null) continue;

            if (indent <= 2 && trimmed.EndsWith(':'))
            {
                var key = trimmed[..^1].Trim().ToLowerInvariant();
                if (key is "routers" or "services")
                {
                    subSection = key;
                    itemName = null;
                    itemIndent = -1;
                    inEntryPoints = false;
                    continue;
                }
            }

            if (subSection is null) continue;

            if ((itemIndent < 0 || indent == itemIndent) && !trimmed.StartsWith('-') && trimmed.EndsWith(':'))
            {
                itemName = trimmed[..^1].Trim();
                itemIndent = indent;
                inEntryPoints = false;
                if (subSection == "routers")
                {
                    routers[(rootSection, itemName)] = new TraefikRouter(rootSection, itemName, lineNumber);
                }
                else
                {
                    services[(rootSection, itemName)] = [];
                }
                continue;
            }

            if (itemName is null || indent <= itemIndent) continue;

            if (subSection == "routers" && routers.TryGetValue((rootSection, itemName), out var router))
            {
                if (trimmed.StartsWith("entryPoints:", StringComparison.OrdinalIgnoreCase))
                {
                    inEntryPoints = true;
                    var inline = trimmed["entryPoints:".Length..].Trim();
                    if (inline.StartsWith('[') && inline.EndsWith(']'))
                    {
                        foreach (var ep in inline[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            router.EntryPoints.Add(TrimQuotes(ep));
                        }
                        inEntryPoints = false;
                    }
                    continue;
                }

                if (inEntryPoints && trimmed.StartsWith('-'))
                {
                    router.EntryPoints.Add(TrimQuotes(trimmed[1..].Trim()));
                    continue;
                }

                inEntryPoints = false;
                if (TrySplitYamlKeyValue(trimmed, out var key, out var value))
                {
                    if (key.Equals("rule", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (Match match in TraefikHostRuleRegex().Matches(value))
                        {
                            router.Domains.Add(match.Groups[1].Value.Trim());
                        }
                    }
                    else if (key.Equals("service", StringComparison.OrdinalIgnoreCase))
                    {
                        router.ServiceName = TrimQuotes(value);
                    }
                }
            }
            else if (subSection == "services" && services.TryGetValue((rootSection, itemName), out var targets))
            {
                var candidate = trimmed.StartsWith('-') ? trimmed[1..].Trim() : trimmed;
                if (TrySplitYamlKeyValue(candidate, out var key, out var value)
                    && (key.Equals("url", StringComparison.OrdinalIgnoreCase) || key.Equals("address", StringComparison.OrdinalIgnoreCase)))
                {
                    var cleaned = UrlUserInfo.Strip(TrimQuotes(value));
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        targets.Add(new ReverseProxyTargetInfo(cleaned, null, lineNumber));
                    }
                }
            }
        }

        foreach (var router in routers.Values)
        {
            if (router.ServiceName is null
                || !services.TryGetValue((router.Section, router.ServiceName), out var targets)
                || targets.Count == 0)
            {
                continue;
            }

            var isSecure = router.EntryPoints.Any(ep => ep.Contains("secure", StringComparison.OrdinalIgnoreCase)
                || ep.Contains("https", StringComparison.OrdinalIgnoreCase) || ep.Contains("443", StringComparison.Ordinal));
            var protocol = router.Section == "tcp" ? "tcp" : (isSecure ? "https" : "http");
            int? port = router.Section == "tcp" ? null : (isSecure ? 443 : 80);

            routes.Add(new ReverseProxyRouteInfo(
                "traefik", filePath, router.Line, router.Name,
                router.Domains.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                port, protocol, router.ServiceName, targets.ToArray()));
        }

        return routes;
    }

    private static (IReadOnlyList<string> Domains, int? Port, string Protocol) ParseCaddySiteHeader(string header)
    {
        var domains = new List<string>();
        int? port = null;
        var hasHttpScheme = false;
        var hasHttpsScheme = false;

        foreach (var rawEntry in header.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = rawEntry;
            if (entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                hasHttpScheme = true;
                entry = entry["http://".Length..];
            }
            else if (entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                hasHttpsScheme = true;
                entry = entry["https://".Length..];
            }

            var hostPart = entry;
            var colon = entry.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(entry[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
                && parsedPort is > 0 and <= 65535)
            {
                port ??= parsedPort;
                hostPart = entry[..colon];
            }

            hostPart = hostPart.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(hostPart) && !hostPart.StartsWith('*') && hostPart != "localhost")
            {
                domains.Add(hostPart);
            }
        }

        var protocol = hasHttpScheme && !hasHttpsScheme ? "http" : (port == 80 ? "http" : "https");
        port ??= protocol == "https" ? 443 : 80;
        return (domains.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), port, protocol);
    }

    private static (int? Port, bool IsSsl) ParseHaBind(string[] tokens)
    {
        var isSsl = tokens.Any(token => token.Equals("ssl", StringComparison.OrdinalIgnoreCase));
        var bindTarget = tokens[1];
        var colon = bindTarget.LastIndexOf(':');
        if (colon >= 0)
        {
            var portText = bindTarget[(colon + 1)..].Split(',', '-')[0];
            if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
            {
                return (port, isSsl);
            }
        }

        return (null, isSsl);
    }

    private static bool TrySplitYamlKeyValue(string line, out string key, out string value)
    {
        var colon = line.IndexOf(':');
        if (colon > 0)
        {
            key = line[..colon].Trim();
            value = line[(colon + 1)..].Trim();
            return key.Length > 0 && value.Length > 0;
        }

        key = string.Empty;
        value = string.Empty;
        return false;
    }

    private static string TrimQuotes(string value) => value.Trim().Trim('"', '\'');

    private static string StripHashComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch == '\'' && !inDouble) inSingle = !inSingle;
            else if (ch == '"' && !inSingle) inDouble = !inDouble;
            else if (ch == '#' && !inSingle && !inDouble) return line[..index];
        }

        return line;
    }

    private static int CountChar(string value, char target)
    {
        var count = 0;
        foreach (var ch in value) if (ch == target) count++;
        return count;
    }

    private static int CountLeadingSpaces(string value)
    {
        var count = 0;
        foreach (var ch in value)
        {
            if (ch == ' ') count++;
            else if (ch == '\t') count += 2;
            else break;
        }
        return count;
    }

    private sealed class HaFrontend(string name, int line)
    {
        public string Name { get; } = name;
        public int Line { get; } = line;
        public int? ListenPort { get; set; }
        public bool IsSsl { get; set; }
        public string Mode { get; set; } = "http";
        public string? DefaultBackend { get; set; }
        public Dictionary<string, List<string>> AclDomains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Backend, string[] AclRefs, int Line)> UseBackends { get; } = [];
    }

    private sealed class HaListen(string name, int line)
    {
        public string Name { get; } = name;
        public int Line { get; } = line;
        public int? ListenPort { get; set; }
        public bool IsSsl { get; set; }
        public string Mode { get; set; } = "http";
        public List<ReverseProxyTargetInfo> Targets { get; } = [];
    }

    private sealed class TraefikRouter(string section, string name, int line)
    {
        public string Section { get; } = section;
        public string Name { get; } = name;
        public int Line { get; } = line;
        public string? ServiceName { get; set; }
        public List<string> Domains { get; } = [];
        public List<string> EntryPoints { get; } = [];
    }
}
