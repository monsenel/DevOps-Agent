using System.Globalization;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static partial class NginxCollectorParser
{
    private static readonly HashSet<string> ProxyDirectives = new(StringComparer.Ordinal)
    {
        "proxy_pass", "fastcgi_pass", "grpc_pass", "uwsgi_pass"
    };

    // Request-time variables that a `set` cannot turn into a fixed backend; never substituted.
    private static readonly HashSet<string> BuiltInVariables = new(StringComparer.Ordinal)
    {
        "host", "hostname", "scheme", "uri", "document_uri", "request_uri", "request", "request_method",
        "request_filename", "args", "query_string", "is_args", "server_name", "server_addr", "server_port",
        "server_protocol", "remote_addr", "remote_port", "remote_user", "binary_remote_addr", "proxy_host",
        "proxy_port", "proxy_add_x_forwarded_for", "realip_remote_addr", "https", "request_id", "connection",
        "status", "time_local", "time_iso8601", "msec", "pid", "content_type", "content_length", "document_root",
        "realpath_root", "limit_rate", "nginx_version", "upstream_addr", "upstream_status",
        "upstream_response_time", "upstream_connect_time", "upstream_header_time", "upstream_first_byte_time",
        "upstream_queue_time", "upstream_session_time", "upstream_response_length", "upstream_bytes_sent",
        "upstream_bytes_received", "upstream_cache_status"
    };

    // `$upstream_*` is deliberately not a prefix: owners name their own variables `$upstream_app` and the like.
    private static readonly string[] BuiltInVariablePrefixes =
    [
        "arg_", "http_", "sent_http_", "upstream_http_", "cookie_", "upstream_cookie_", "ssl_",
        "geoip_", "sent_trailer_", "upstream_trailer_"
    ];

    public static NginxDiscoveryResult Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var root = new Block("root", [], null, 0);
        var stack = new Stack<Block>();
        stack.Push(root);
        var pending = new List<Token>();
        var includeContexts = new List<(string Pattern, string Context)>();
        var wrapperActive = false;

        var tokens = Tokenize(output).ToArray();
        // nginx resolves a relative include against its prefix, the directory of the main configuration file.
        var prefix = NginxPrefix(tokens);
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Kind == TokenKind.FileMarker)
            {
                AddDirectiveAndTrackInclude();
                if (wrapperActive && stack.Count > 1)
                {
                    stack.Pop();
                    wrapperActive = false;
                }

                var context = FindIncludedContext(token.Value, includeContexts, prefix);
                if (context is not null && !StartsWithContextBlock(tokens, index + 1, context))
                {
                    var wrapper = new Block(context, [], token.Value, 0);
                    stack.Peek().Blocks.Add(wrapper);
                    stack.Push(wrapper);
                    wrapperActive = true;
                }

                continue;
            }

            switch (token.Kind)
            {
                case TokenKind.OpenBrace:
                    if (pending.Count > 0)
                    {
                        var block = new Block(pending[0].Value, pending.Skip(1).Select(item => item.Value).ToArray(),
                            pending[0].FilePath, pending[0].Line);
                        stack.Peek().Blocks.Add(block);
                        stack.Push(block);
                        pending.Clear();
                    }
                    break;
                case TokenKind.Semicolon:
                    AddDirectiveAndTrackInclude();
                    break;
                case TokenKind.CloseBrace:
                    AddDirectiveAndTrackInclude();
                    if (stack.Count > 1) stack.Pop();
                    break;
                case TokenKind.Word:
                    pending.Add(token);
                    break;
            }
        }
        AddDirectiveAndTrackInclude();

        var servers = new List<NginxServerInfo>();
        var upstreams = new List<NginxUpstreamInfo>();
        foreach (var context in root.Blocks.Where(block => block.Name is "http" or "stream"))
        {
            foreach (var block in context.Blocks)
            {
                if (block.Name == "server")
                {
                    servers.Add(ParseServer(context, block));
                }
                else if (block.Name == "upstream" && block.Arguments.Count > 0)
                {
                    upstreams.Add(ParseUpstream(context.Name, block));
                }
            }
        }

        return new NginxDiscoveryResult(servers, upstreams);

        void AddDirectiveAndTrackInclude()
        {
            if (pending.Count > 1 && pending[0].Value == "include")
            {
                var context = stack.FirstOrDefault(block => block.Name is "http" or "stream")?.Name;
                if (context is not null)
                {
                    foreach (var pattern in pending.Skip(1).Select(item => item.Value))
                    {
                        includeContexts.Add((pattern, context));
                    }
                }
            }

            AddDirective(stack.Peek(), pending);
        }
    }

    private static string NginxPrefix(IReadOnlyList<Token> tokens)
    {
        var mainFile = tokens.FirstOrDefault(token => token.Kind == TokenKind.FileMarker)?.Value;
        var directory = string.IsNullOrEmpty(mainFile) ? null : Path.GetDirectoryName(mainFile)?.Replace('\\', '/');
        return string.IsNullOrEmpty(directory) ? "/etc/nginx" : directory;
    }

    private static string? FindIncludedContext(string filePath, IReadOnlyList<(string Pattern, string Context)> includes, string prefix)
    {
        foreach (var (rawPattern, context) in includes)
        {
            // A relative pattern is relative to the nginx prefix; the included file markers carry absolute paths.
            var relative = rawPattern.StartsWith("./", StringComparison.Ordinal) ? rawPattern[2..] : rawPattern;
            var pattern = relative.StartsWith('/') ? relative : prefix.TrimEnd('/') + "/" + relative;
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";
            if (Regex.IsMatch(filePath, regex, RegexOptions.CultureInvariant)) return context;
        }

        return null;
    }

    private static bool StartsWithContextBlock(IReadOnlyList<Token> tokens, int startIndex, string context)
    {
        var words = new List<string>();
        for (var index = startIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == TokenKind.OpenBrace)
            {
                return words.Count > 0 && words[0] == context;
            }

            if (tokens[index].Kind == TokenKind.FileMarker || tokens[index].Kind == TokenKind.CloseBrace)
            {
                return false;
            }

            if (tokens[index].Kind == TokenKind.Word) words.Add(tokens[index].Value);
            if (tokens[index].Kind == TokenKind.Semicolon) words.Clear();
        }

        return false;
    }

    private static NginxServerInfo ParseServer(Block contextBlock, Block block)
    {
        var listens = Values(block, "listen");
        var names = Values(block, "server_name");
        // Scope chain for `set` lookup, innermost first: server, then the http/stream context block.
        Block[] serverScopes = [block, contextBlock];
        var locations = ParseLocations(block, serverScopes).ToArray();
        var proxies = GetProxies(block, serverScopes).Concat(locations.SelectMany(location => location.Proxies)).ToArray();
        return new NginxServerInfo(contextBlock.Name, listens, names, locations, proxies, block.FilePath, block.Line);
    }

    // Nested locations are flattened into the list; each keeps only its own proxies.
    private static IEnumerable<NginxLocationInfo> ParseLocations(Block parent, Block[] parentScopes)
    {
        foreach (var location in parent.Blocks.Where(child => child.Name == "location"))
        {
            Block[] scopes = [location, .. parentScopes];
            yield return new NginxLocationInfo(string.Join(' ', location.Arguments), GetProxies(location, scopes),
                location.FilePath, location.Line);
            foreach (var nested in ParseLocations(location, scopes)) yield return nested;
        }
    }

    private static NginxUpstreamInfo ParseUpstream(string context, Block block)
    {
        var servers = block.Directives.Where(directive => directive.Name == "server" && directive.Arguments.Count > 0)
            .Select(directive =>
            {
                var weight = directive.Arguments.Skip(1)
                    .FirstOrDefault(argument => argument.StartsWith("weight=", StringComparison.Ordinal));
                int? parsedWeight = int.TryParse(weight?[("weight=".Length)..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
                return new NginxUpstreamServerInfo(directive.Arguments[0], parsedWeight,
                    directive.Arguments.Skip(1).ToArray(), directive.FilePath, directive.Line);
            }).ToArray();
        return new NginxUpstreamInfo(context, block.Arguments[0], servers, block.FilePath, block.Line);
    }

    private static IReadOnlyList<NginxSourceValue> Values(Block block, string name) =>
        block.Directives.Where(directive => directive.Name == name)
            .SelectMany(directive => directive.Arguments.Select(argument =>
                new NginxSourceValue(argument, directive.FilePath, directive.Line)))
            .ToArray();

    // Child blocks other than locations (e.g. `if`) are searched for proxies but never add `set` scope:
    // a `set` inside a conditional block is not guaranteed to run, so it must not resolve anything.
    private static IReadOnlyList<NginxProxyInfo> GetProxies(Block block, Block[] scopes)
    {
        var proxies = block.Directives.Where(directive => ProxyDirectives.Contains(directive.Name)
                && directive.Arguments.Count > 0)
            .Select(directive => CreateProxy(directive, scopes));
        return proxies.Concat(block.Blocks.Where(child => child.Name != "location")
            .SelectMany(child => GetProxies(child, scopes))).ToArray();
    }

    private static NginxProxyInfo CreateProxy(Directive directive, Block[] scopes)
    {
        var target = directive.Arguments[0];
        var resolution = ResolveVariableTarget(target, scopes);
        return resolution is null
            ? new NginxProxyInfo(directive.Name, target, target.Contains('$'), directive.FilePath, directive.Line)
            : new NginxProxyInfo(directive.Name, resolution.Target, false, directive.FilePath, directive.Line)
            {
                ResolvedFrom = resolution.Source
            };
    }

    // A target is resolvable only as [scheme://]$name or [scheme://]${name}, optionally followed by a literal
    // URI path (/...). Anything else (two variables, a port after the variable, ...) stays unresolved.
    [GeneratedRegex(@"^(?<prefix>[A-Za-z][A-Za-z0-9+.\-]*://)?\$(?:(?<name>[A-Za-z0-9_]+)|\{(?<braced>[A-Za-z0-9_]+)\})(?<suffix>/[^$]*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VariableTargetPattern();

    private static VariableResolution? ResolveVariableTarget(string target, Block[] scopes)
    {
        var match = VariableTargetPattern().Match(target);
        if (!match.Success) return null;
        var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["braced"].Value;
        if (IsBuiltInVariable(name)) return null;

        foreach (var scope in scopes)
        {
            var sets = scope.Directives
                .Where(directive => directive.Name == "set" && directive.Arguments.Count == 2
                    && directive.Arguments[0].Length == name.Length + 1
                    && directive.Arguments[0][0] == '$'
                    && string.Equals(directive.Arguments[0][1..], name, StringComparison.Ordinal))
                .ToArray();
            if (sets.Length == 0) continue;

            // The nearest scope that sets the variable decides; conflicting values there leave it unresolved.
            var first = sets[0];
            var value = first.Arguments[1];
            if (sets.Any(set => !string.Equals(set.Arguments[1], value, StringComparison.Ordinal))) return null;
            if (value.Length == 0 || value.Contains('$')) return null;

            var prefix = match.Groups["prefix"].Value;
            if (prefix.Length > 0 && value.Contains("://", StringComparison.Ordinal)) return null;
            var resolved = prefix + value + match.Groups["suffix"].Value;
            return new VariableResolution(resolved,
                new NginxVariableResolution(target, "$" + name, value, first.FilePath, first.Line));
        }

        return null;
    }

    private static bool IsBuiltInVariable(string name) =>
        BuiltInVariables.Contains(name) || BuiltInVariablePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private sealed record VariableResolution(string Target, NginxVariableResolution Source);

    private static void AddDirective(Block block, List<Token> pending)
    {
        if (pending.Count == 0) return;
        block.Directives.Add(new Directive(pending[0].Value, pending.Skip(1).Select(token => token.Value).ToArray(),
            pending[0].FilePath, pending[0].Line));
        pending.Clear();
    }

    private static IEnumerable<Token> Tokenize(string output)
    {
        output = output.Replace("\\\r\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);
        string? filePath = null;
        var globalLine = 0;
        var fileLine = 0;
        foreach (var lineText in output.Split('\n'))
        {
            globalLine++;
            var trimmed = lineText.Trim();
            const string marker = "# configuration file ";
            if (trimmed.StartsWith(marker, StringComparison.Ordinal) && trimmed.EndsWith(':'))
            {
                filePath = trimmed[marker.Length..^1].Trim();
                fileLine = 0;
                yield return new Token(TokenKind.FileMarker, filePath, filePath, 0);
                continue;
            }

            if (filePath is not null) fileLine++;
            var line = filePath is null ? globalLine : fileLine;
            var token = new System.Text.StringBuilder();
            var yieldBuffer = new List<Token>();
            var tokenStarted = false;
            var quote = '\0';
            var escaped = false;
            var inVariableBrace = false;
            var tokenLine = line;

            void Flush()
            {
                inVariableBrace = false;
                if (!tokenStarted) return;
                yieldBuffer.Add(new Token(TokenKind.Word, token.ToString(), filePath, tokenLine));
                token.Clear();
                tokenStarted = false;
            }

            for (var index = 0; index < lineText.Length; index++)
            {
                var character = lineText[index];
                if (escaped)
                {
                    token.Append(character);
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    if (!tokenStarted) tokenLine = line;
                    tokenStarted = true;
                    escaped = true;
                    continue;
                }
                if (quote != '\0')
                {
                    if (character == quote) quote = '\0';
                    else token.Append(character);
                    continue;
                }
                if (character is '\'' or '"')
                {
                    if (!tokenStarted) tokenLine = line;
                    tokenStarted = true;
                    quote = character;
                    continue;
                }
                if (character == '#')
                {
                    Flush();
                    break;
                }
                // `${name}` is a variable reference inside a word, not a block delimiter.
                if (character == '{' && token.Length > 0 && token[^1] == '$')
                {
                    token.Append(character);
                    inVariableBrace = true;
                    continue;
                }
                if (character == '}' && inVariableBrace)
                {
                    token.Append(character);
                    inVariableBrace = false;
                    continue;
                }
                if (character is '{' or '}' or ';')
                {
                    Flush();
                    yieldBuffer.Add(new Token(character switch
                    {
                        '{' => TokenKind.OpenBrace,
                        '}' => TokenKind.CloseBrace,
                        _ => TokenKind.Semicolon
                    }, character.ToString(), filePath, line));
                    continue;
                }
                if (char.IsWhiteSpace(character))
                {
                    Flush();
                    continue;
                }
                if (!tokenStarted) tokenLine = line;
                tokenStarted = true;
                token.Append(character);
            }
            if (escaped) token.Append('\\');
            Flush();
            foreach (var item in yieldBuffer) yield return item;
        }
    }

    private sealed class Block(string name, IReadOnlyList<string> arguments, string? filePath, int line)
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Arguments { get; } = arguments;
        public string? FilePath { get; } = filePath;
        public int Line { get; } = line;
        public List<Directive> Directives { get; } = [];
        public List<Block> Blocks { get; } = [];
    }

    private sealed record Directive(string Name, IReadOnlyList<string> Arguments, string? FilePath, int Line);
    private sealed record Token(TokenKind Kind, string Value, string? FilePath, int Line);
    private enum TokenKind { Word, OpenBrace, CloseBrace, Semicolon, FileMarker }
}
