using System.Globalization;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

/// <summary>
/// Deterministic read-only parser for application configs, connection strings, PostgreSQL replication configs,
/// Redis client/replica configs, and Nexus artifact registry references (Maven/npm/pip/env).
/// Never retains credentials — only dependency type, host, port, protocol, database/repo name, and config location.
/// </summary>
public static partial class AppConfigCollectorParser
{
    public const string SectionPrefix = "__DVA_CFG__:";

    // Matches standard & JDBC URIs (credentials already stripped or stripped on parse).
    [GeneratedRegex(@"(?:jdbc:)?(?<scheme>postgresql|postgres|rediss|redis|mysql|mariadb|mongodb\+srv|mongodb|amqps|amqp|https|http)://(?:[^/\s@""'<>]+@)?(?<host>\[[^\]]+\]|[A-Za-z0-9._\-]+)(?::(?<port>\d{1,5}))?(?<path>/[^\s""'<>?#]*)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UriReferenceRegex();

    // Matches ADO.NET / Npgsql / libpq key-value connection strings (Host=...;Port=... or host=... port=...)
    [GeneratedRegex(@"(?:^|[;,\s""'])(?:host|server|data\s+source)\s*=\s*(?<host>[A-Za-z0-9._\-:]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KvHostRegex();

    [GeneratedRegex(@"(?:^|[;,\s""'])port\s*=\s*(?<port>\d{1,5})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KvPortRegex();

    [GeneratedRegex(@"(?:^|[;,\s""'])(?:database|dbname|initial\s+catalog)\s*=\s*(?<db>[A-Za-z0-9._\-]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KvDatabaseRegex();

    // Matches StackExchange.Redis style "10.88.20.30:6379,password=...,ssl=true"
    [GeneratedRegex(@"(?<host>[A-Za-z0-9._\-]+):(?<port>\d{1,5})(?:\s*,|\s*$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostPortRegex();

    // Matches Redis replicaof / slaveof / sentinel monitor directives
    [GeneratedRegex(@"^\s*(?:replicaof|slaveof)\s+(?<host>\S+)\s+(?<port>\d{1,5})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RedisReplicaOfRegex();

    [GeneratedRegex(@"^\s*sentinel\s+monitor\s+(?<name>\S+)\s+(?<host>\S+)\s+(?<port>\d{1,5})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RedisSentinelRegex();

    // Matches key = value or "Key": "value" lines to capture the setting key name
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:""(?<jkey>[^""]+)""\s*:|(?<ekey>[A-Za-z0-9_.\-]+)\s*(?:=|:))",
        RegexOptions.CultureInvariant)]
    private static partial Regex SettingKeyRegex();

    public static AppConfigDiscoveryResult Parse(string output)
    {
        var references = new List<AppDependencyReference>();
        string? currentKind = null;
        string? currentPath = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (currentKind is not null && currentPath is not null)
            {
                ParseSection(currentKind, currentPath, buffer, references);
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
                    currentKind = rest[..sep].Trim();
                    currentPath = rest[(sep + 1)..].Trim();
                }
                else
                {
                    currentKind = null;
                    currentPath = null;
                }
            }
            else if (currentKind is not null)
            {
                buffer.Add(line);
            }
        }

        Flush();

        var deduplicated = references
            .DistinctBy(item => (item.DependencyType, item.Host.ToLowerInvariant(), item.Port, item.SourcePath, item.SettingKey))
            .ToArray();
        return new AppConfigDiscoveryResult(deduplicated);
    }

    private static void ParseSection(
        string sourceKind,
        string sourcePath,
        IReadOnlyList<string> lines,
        ICollection<AppDependencyReference> references)
    {
        var splitVars = new Dictionary<string, (string Value, int Line)>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var settingKey = ExtractSettingKey(line);

            // 1. Redis replicaof / slaveof / sentinel monitor
            if (RedisReplicaOfRegex().Match(line) is { Success: true } replicaMatch)
            {
                var host = replicaMatch.Groups["host"].Value;
                if (IsUsableHost(host) && !host.Equals("no", StringComparison.OrdinalIgnoreCase)
                    && TryParsePort(replicaMatch.Groups["port"].Value, out var port))
                {
                    references.Add(new AppDependencyReference(
                        "redis", host, port, "tcp", sourceKind, sourcePath, "replicaof", null, null, lineNumber));
                }
                continue;
            }

            if (RedisSentinelRegex().Match(line) is { Success: true } sentinelMatch)
            {
                var host = sentinelMatch.Groups["host"].Value;
                if (IsUsableHost(host) && TryParsePort(sentinelMatch.Groups["port"].Value, out var port))
                {
                    references.Add(new AppDependencyReference(
                        "redis", host, port, "tcp", sourceKind, sourcePath, "sentinel monitor", null,
                        sentinelMatch.Groups["name"].Value, lineNumber));
                }
                continue;
            }

            // 2. URI references (postgres, redis, mysql, mongodb, amqp, nexus/registry http/https)
            var matchedUri = false;
            foreach (Match uriMatch in UriReferenceRegex().Matches(line))
            {
                var scheme = uriMatch.Groups["scheme"].Value.ToLowerInvariant();
                var host = uriMatch.Groups["host"].Value.Trim('[', ']');
                if (!IsUsableHost(host)) continue;

                var port = uriMatch.Groups["port"].Success && TryParsePort(uriMatch.Groups["port"].Value, out var parsedPort)
                    ? parsedPort
                    : DefaultPortForScheme(scheme);
                var path = uriMatch.Groups["path"].Success ? uriMatch.Groups["path"].Value : null;

                var (depType, protocol, dbName) = ClassifyUri(scheme, host, path, settingKey, sourceKind);
                if (depType is null) continue;

                matchedUri = true;
                references.Add(new AppDependencyReference(
                    depType, host, port, protocol, sourceKind, sourcePath, settingKey, null, dbName, lineNumber));
            }

            if (matchedUri) continue;

            // 3. Key-value connection strings (Npgsql, ADO.NET, libpq primary_conninfo)
            if (KvHostRegex().Match(line) is { Success: true } kvHostMatch
                && (line.Contains(';') || line.Contains("port=", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("dbname=", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("database=", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("primary_conninfo", StringComparison.OrdinalIgnoreCase)))
            {
                var rawHost = kvHostMatch.Groups["host"].Value.Trim('\'', '"', ';', ',');
                var host = rawHost;
                int? inlinePort = null;
                var colon = rawHost.LastIndexOf(':');
                if (colon > 0 && TryParsePort(rawHost[(colon + 1)..], out var hp))
                {
                    host = rawHost[..colon];
                    inlinePort = hp;
                }

                if (IsUsableHost(host))
                {
                    var port = KvPortRegex().Match(line) is { Success: true } portMatch && TryParsePort(portMatch.Groups["port"].Value, out var kvp)
                        ? kvp
                        : inlinePort;
                    var dbName = KvDatabaseRegex().Match(line) is { Success: true } dbMatch
                        ? dbMatch.Groups["db"].Value
                        : null;
                    var depType = InferKvDependencyType(line, sourceKind, settingKey, port);
                    port ??= DefaultPortForDependencyType(depType);
                    references.Add(new AppDependencyReference(
                        depType, host, port, "tcp", sourceKind, sourcePath, settingKey, null, dbName, lineNumber));
                    continue;
                }
            }

            // 4. StackExchange.Redis style ("Cache": "10.88.20.30:6379,password=[REDACTED],ssl=true")
            if (settingKey is not null
                && (settingKey.Contains("redis", StringComparison.OrdinalIgnoreCase)
                    || settingKey.Contains("cache", StringComparison.OrdinalIgnoreCase))
                && HostPortRegex().Match(line[(line.IndexOf(settingKey, StringComparison.Ordinal) + settingKey.Length)..]) is { Success: true } redisHp)
            {
                var host = redisHp.Groups["host"].Value;
                if (IsUsableHost(host) && TryParsePort(redisHp.Groups["port"].Value, out var port))
                {
                    references.Add(new AppDependencyReference(
                        "redis", host, port, "tcp", sourceKind, sourcePath, settingKey, null, null, lineNumber));
                    continue;
                }
            }

            // 5. Collect split env vars (PGHOST/PGPORT, REDIS_HOST/REDIS_PORT, DB_HOST/DB_PORT)
            if (settingKey is not null)
            {
                var eq = line.IndexOf('=');
                if (eq > 0 && eq < line.Length - 1)
                {
                    var val = line[(eq + 1)..].Trim().Trim('"', '\'');
                    if (val.Length > 0)
                    {
                        splitVars[settingKey] = (val, lineNumber);
                    }
                }
            }
        }

        // Resolve split env var groups within the same file
        TryAddSplitVar(splitVars, "postgresql", "tcp", 5432, sourceKind, sourcePath, references,
            ["PGHOST", "POSTGRES_HOST", "POSTGRESQL_HOST", "DB_HOST"],
            ["PGPORT", "POSTGRES_PORT", "POSTGRESQL_PORT", "DB_PORT"],
            ["PGDATABASE", "POSTGRES_DB", "DB_NAME"]);
        TryAddSplitVar(splitVars, "redis", "tcp", 6379, sourceKind, sourcePath, references,
            ["REDIS_HOST"], ["REDIS_PORT"], []);
        TryAddSplitVar(splitVars, "nexus", "https", 8081, sourceKind, sourcePath, references,
            ["NEXUS_HOST"], ["NEXUS_PORT"], []);
    }

    private static void TryAddSplitVar(
        IReadOnlyDictionary<string, (string Value, int Line)> vars,
        string depType,
        string protocol,
        int defaultPort,
        string sourceKind,
        string sourcePath,
        ICollection<AppDependencyReference> references,
        string[] hostKeys,
        string[] portKeys,
        string[] dbKeys)
    {
        foreach (var hostKey in hostKeys)
        {
            if (!vars.TryGetValue(hostKey, out var hostEntry) || !IsUsableHost(hostEntry.Value)) continue;
            var port = defaultPort;
            foreach (var portKey in portKeys)
            {
                if (vars.TryGetValue(portKey, out var portEntry) && TryParsePort(portEntry.Value, out var parsed))
                {
                    port = parsed;
                    break;
                }
            }

            string? dbName = null;
            foreach (var dbKey in dbKeys)
            {
                if (vars.TryGetValue(dbKey, out var dbEntry) && !string.IsNullOrWhiteSpace(dbEntry.Value))
                {
                    dbName = dbEntry.Value;
                    break;
                }
            }

            references.Add(new AppDependencyReference(
                depType, hostEntry.Value, port, protocol, sourceKind, sourcePath, hostKey, null, dbName, hostEntry.Line));
            return;
        }
    }

    private static (string? DependencyType, string Protocol, string? DbName) ClassifyUri(
        string scheme,
        string host,
        string? path,
        string? settingKey,
        string sourceKind)
    {
        var dbSegment = path?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        switch (scheme)
        {
            case "postgres" or "postgresql":
                return ("postgresql", "tcp", dbSegment);
            case "redis" or "rediss":
                return ("redis", "tcp", dbSegment);
            case "mysql" or "mariadb":
                return ("mysql", "tcp", dbSegment);
            case "mongodb" or "mongodb+srv":
                return ("mongodb", "tcp", dbSegment);
            case "amqp" or "amqps":
                return ("rabbitmq", "tcp", dbSegment);
            case "http" or "https":
                var isNexusOrRegistry =
                    sourceKind is "maven" or "npm" or "pip"
                    || (path is not null && path.Contains("/repository/", StringComparison.OrdinalIgnoreCase))
                    || host.Contains("nexus", StringComparison.OrdinalIgnoreCase)
                    || (settingKey is not null && (settingKey.Contains("nexus", StringComparison.OrdinalIgnoreCase)
                        || settingKey.Contains("registry", StringComparison.OrdinalIgnoreCase)
                        || settingKey.Contains("artifact", StringComparison.OrdinalIgnoreCase)
                        || settingKey.Contains("index-url", StringComparison.OrdinalIgnoreCase)));
                if (!isNexusOrRegistry)
                {
                    return (null, scheme, null);
                }

                var repoName = ExtractNexusRepoName(path);
                return ("nexus", scheme, repoName);
            default:
                return (null, "tcp", null);
        }
    }

    private static string? ExtractNexusRepoName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var marker = "/repository/";
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = path[(idx + marker.Length)..].Trim('/');
        var slash = rest.IndexOf('/');
        var repo = slash < 0 ? rest : rest[..slash];
        return string.IsNullOrWhiteSpace(repo) ? null : repo;
    }

    private static string InferKvDependencyType(string line, string sourceKind, string? settingKey, int? port)
    {
        if (sourceKind == "postgres-conf" || port == 5432
            || line.Contains("primary_conninfo", StringComparison.OrdinalIgnoreCase)
            || line.Contains("sslmode=", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Username=", StringComparison.OrdinalIgnoreCase)
            || (settingKey is not null && (settingKey.Contains("postgres", StringComparison.OrdinalIgnoreCase)
                || settingKey.Contains("npgsql", StringComparison.OrdinalIgnoreCase))))
        {
            return "postgresql";
        }

        if (port == 3306 || (settingKey is not null && settingKey.Contains("mysql", StringComparison.OrdinalIgnoreCase)))
        {
            return "mysql";
        }

        if (port == 6379 || (settingKey is not null && settingKey.Contains("redis", StringComparison.OrdinalIgnoreCase)))
        {
            return "redis";
        }

        return "database";
    }

    private static int? DefaultPortForScheme(string scheme) => scheme switch
    {
        "postgres" or "postgresql" => 5432,
        "redis" or "rediss" => 6379,
        "mysql" or "mariadb" => 3306,
        "mongodb" or "mongodb+srv" => 27017,
        "amqp" => 5672,
        "amqps" => 5671,
        "http" => 80,
        "https" => 443,
        _ => null
    };

    private static int? DefaultPortForDependencyType(string depType) => depType switch
    {
        "postgresql" => 5432,
        "redis" => 6379,
        "mysql" => 3306,
        "mongodb" => 27017,
        "rabbitmq" => 5672,
        "nexus" => 8081,
        _ => null
    };

    private static string? ExtractSettingKey(string line)
    {
        var match = SettingKeyRegex().Match(line);
        if (!match.Success) return null;
        return match.Groups["jkey"].Success ? match.Groups["jkey"].Value : match.Groups["ekey"].Value;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    private static bool IsUsableHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Contains('[') || host.Contains(']') || host.Contains('$') || host.Contains('{')
            || host.Contains('}') || host.Contains('<') || host.Contains('>')
            || host.Equals("REDACTED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
