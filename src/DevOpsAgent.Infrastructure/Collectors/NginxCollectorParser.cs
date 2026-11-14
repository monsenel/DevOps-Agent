using System.Globalization;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class NginxCollectorParser
{
    private static readonly HashSet<string> ProxyDirectives = new(StringComparer.Ordinal)
    {
        "proxy_pass", "fastcgi_pass", "grpc_pass", "uwsgi_pass"
    };

    public static NginxDiscoveryResult Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var root = new Block("root", [], null, 0);
        var stack = new Stack<Block>();
        stack.Push(root);
        var pending = new List<Token>();

        foreach (var token in Tokenize(output))
        {
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
                    AddDirective(stack.Peek(), pending);
                    break;
                case TokenKind.CloseBrace:
                    AddDirective(stack.Peek(), pending);
                    if (stack.Count > 1) stack.Pop();
                    break;
                case TokenKind.Word:
                    pending.Add(token);
                    break;
            }
        }
        AddDirective(stack.Peek(), pending);

        var servers = new List<NginxServerInfo>();
        var upstreams = new List<NginxUpstreamInfo>();
        foreach (var context in root.Blocks.Where(block => block.Name is "http" or "stream"))
        {
            foreach (var block in context.Blocks)
            {
                if (block.Name == "server")
                {
                    servers.Add(ParseServer(context.Name, block));
                }
                else if (block.Name == "upstream" && block.Arguments.Count > 0)
                {
                    upstreams.Add(ParseUpstream(context.Name, block));
                }
            }
        }

        return new NginxDiscoveryResult(servers, upstreams);
    }

    private static NginxServerInfo ParseServer(string context, Block block)
    {
        var listens = Values(block, "listen");
        var names = Values(block, "server_name");
        var locations = block.Blocks.Where(child => child.Name == "location")
            .Select(location => new NginxLocationInfo(
                string.Join(' ', location.Arguments),
                GetProxies(location),
                location.FilePath,
                location.Line))
            .ToArray();
        var proxies = GetProxies(block).Concat(locations.SelectMany(location => location.Proxies)).ToArray();
        return new NginxServerInfo(context, listens, names, locations, proxies, block.FilePath, block.Line);
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

    private static IReadOnlyList<NginxProxyInfo> GetProxies(Block block)
    {
        var proxies = block.Directives.Where(directive => ProxyDirectives.Contains(directive.Name)
                && directive.Arguments.Count > 0)
            .Select(CreateProxy);
        return proxies.Concat(block.Blocks.Where(child => child.Name != "location")
            .SelectMany(GetProxies)).ToArray();
    }

    private static NginxProxyInfo CreateProxy(Directive directive)
    {
        var target = directive.Arguments[0];
        return new NginxProxyInfo(directive.Name, target, target.Contains('$'),
            directive.FilePath, directive.Line);
    }

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
                continue;
            }

            if (filePath is not null) fileLine++;
            var line = filePath is null ? globalLine : fileLine;
            var token = new System.Text.StringBuilder();
            var yieldBuffer = new List<Token>();
            var tokenStarted = false;
            var quote = '\0';
            var escaped = false;
            var tokenLine = line;

            void Flush()
            {
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
    private enum TokenKind { Word, OpenBrace, CloseBrace, Semicolon }
}
