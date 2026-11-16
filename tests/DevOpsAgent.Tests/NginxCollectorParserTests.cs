using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Tests;

public sealed class NginxCollectorParserTests
{
    [Fact]
    public void Parse_MultipleFilesExtractsHttpAndStreamServersUpstreamsAndProxyDirectives()
    {
        var result = NginxCollectorParser.Parse(Fixture("nginx-T.txt"));

        Assert.Equal(3, result.Servers.Count);
        Assert.Equal(2, result.Upstreams.Count);

        var backend = Assert.Single(result.Upstreams, upstream => upstream.Name == "app_backend");
        Assert.Equal("http", backend.Context);
        Assert.Equal("/etc/nginx/nginx.conf", backend.FilePath);
        Assert.Equal(2, backend.Servers.Count);
        Assert.Equal("10.20.0.11:8080", backend.Servers[0].Address);
        Assert.Equal(3, backend.Servers[0].Weight);
        Assert.Contains("max_fails=2", backend.Servers[0].Parameters);
        Assert.Equal("unix:/run/app/backend.sock", backend.Servers[1].Address);

        var publicServer = Assert.Single(result.Servers, server => server.ServerNames.Any(name => name.Value == "example.test"));
        Assert.Equal("http", publicServer.Context);
        Assert.Equal("/etc/nginx/nginx.conf", publicServer.FilePath);
        Assert.Contains(publicServer.Listens, listen => listen.Value == "80");
        Assert.Contains(publicServer.ServerNames, name => name.Value == "www.example.test");
        Assert.Contains(publicServer.Locations, location => location.Match == "/api/");
        Assert.Contains(publicServer.Proxies, proxy => proxy.Directive == "proxy_pass" && proxy.Target == "http://app_backend"
            && proxy.FilePath == "/etc/nginx/nginx.conf" && proxy.Line == 13);
        Assert.Contains(publicServer.Proxies, proxy => proxy.Directive == "fastcgi_pass" && proxy.Target == "unix:/run/php/php-fpm.sock");

        var secureServer = Assert.Single(result.Servers, server => server.ServerNames.Any(name => name.Value == "secure.example.test"));
        Assert.Equal("/etc/nginx/conf.d/secure.conf", secureServer.FilePath);
        Assert.Equal(2, secureServer.Locations.Count);
        Assert.Contains(secureServer.Proxies, proxy => proxy.Directive == "grpc_pass" && proxy.Target == "grpcs://grpc_pool");
        Assert.Contains(secureServer.Proxies, proxy => proxy.Directive == "uwsgi_pass" && proxy.Target == "127.0.0.1:3031");

        var dynamicProxy = Assert.Single(publicServer.Proxies, proxy => proxy.IsUnresolved);
        Assert.Equal("http://$backend$request_uri", dynamicProxy.Target);
        Assert.Equal("/etc/nginx/nginx.conf", dynamicProxy.FilePath);

        var streamServer = Assert.Single(result.Servers, server => server.Context == "stream");
        Assert.Contains(streamServer.Listens, listen => listen.Value == "5432");
        Assert.Contains(streamServer.Proxies, proxy => proxy.Target == "postgres_cluster");
        var postgres = Assert.Single(result.Upstreams, upstream => upstream.Name == "postgres_cluster");
        Assert.Equal("stream", postgres.Context);
        Assert.Equal(2, postgres.Servers.Count);
        Assert.Equal(2, postgres.Servers[0].Weight);
        Assert.Equal("/etc/nginx/stream.conf", postgres.FilePath);
    }

    [Fact]
    public void Parse_EmptyInputReturnsEmptyResult()
    {
        var result = NginxCollectorParser.Parse(string.Empty);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Upstreams);
    }

    [Fact]
    public void Parse_ExpandedIncludeInheritsItsHttpContext()
    {
        var result = NginxCollectorParser.Parse(Fixture("nginx-T-expanded-includes.txt"));

        var upstream = Assert.Single(result.Upstreams);
        Assert.Equal("api_backend", upstream.Name);
        Assert.Equal("api.internal:5000", Assert.Single(upstream.Servers).Address);
        var server = Assert.Single(result.Servers);
        Assert.Equal("http", server.Context);
        Assert.Contains(server.ServerNames, name => name.Value == "api.test.local");
        Assert.Contains(server.Proxies, proxy => proxy.Target == "http://api_backend");
        Assert.Equal("/etc/nginx/conf.d/demo.conf", server.FilePath);
    }

    [Fact]
    public void Parse_SetVariableAtServerScopeResolvesProxyTargetAndKeepsEvidence()
    {
        var map = MapServer();

        var tiles = Assert.Single(map.Proxies, proxy => proxy.Line == 11);
        Assert.Equal("proxy_pass", tiles.Directive);
        Assert.Equal("http://192.0.2.14:8080", tiles.Target);
        Assert.False(tiles.IsUnresolved);
        Assert.Equal("/etc/nginx/sites-enabled/map", tiles.FilePath);
        var from = Assert.IsType<NginxVariableResolution>(tiles.ResolvedFrom);
        Assert.Equal("$tiles_upstream", from.OriginalTarget);
        Assert.Equal("$tiles_upstream", from.Variable);
        Assert.Equal("http://192.0.2.14:8080", from.Value);
        Assert.Equal("/etc/nginx/sites-enabled/map", from.FilePath);
        Assert.Equal(5, from.Line);

        var mapApp = Assert.Single(map.Proxies, proxy => proxy.Line == 19);
        Assert.Equal("http://192.0.2.14:3030", mapApp.Target);
        Assert.Equal(6, mapApp.ResolvedFrom!.Line);
        Assert.Equal("proxy_pass", mapApp.Directive);
    }

    [Fact]
    public void Parse_NearestScopeWinsAndPathSuffixIsKept()
    {
        var map = MapServer();

        // location sets $api_upstream differently from the server; the location value wins and /v2/ is kept.
        var api = Assert.Single(map.Proxies, proxy => proxy.ResolvedFrom?.OriginalTarget == "$api_upstream/v2/");
        Assert.Equal("http://192.168.20.15:4100/v2/", api.Target);
        Assert.Equal("http://192.168.20.15:4100", api.ResolvedFrom!.Value);
        Assert.Equal(15, api.ResolvedFrom.Line);
    }

    [Fact]
    public void Parse_NestedLocationInheritsAndBracedVariableResolves()
    {
        var map = MapServer();

        var nested = Assert.Single(map.Locations, location => location.Match == "/app/static/");
        var proxy = Assert.Single(nested.Proxies);
        Assert.Equal("http://192.0.2.14:3030/static/", proxy.Target);
        Assert.Equal("${map_app_upstream}/static/", proxy.ResolvedFrom!.OriginalTarget);
        // The nested proxy is no longer attributed to its parent location.
        Assert.DoesNotContain(Assert.Single(map.Locations, location => location.Match == "/").Proxies,
            item => item.Target.EndsWith("/static/", StringComparison.Ordinal));

        var braced = Assert.Single(map.Proxies, item => item.ResolvedFrom?.OriginalTarget == "${tiles_upstream}");
        Assert.Equal("http://192.0.2.14:8080", braced.Target);
    }

    [Fact]
    public void Parse_VariableNamingAnUpstreamBlockResolvesToTheUpstreamName()
    {
        var map = MapServer();

        var pool = Assert.Single(map.Proxies, proxy => proxy.ResolvedFrom?.OriginalTarget == "http://$named_upstream");
        Assert.Equal("http://pool_backend", pool.Target);
        Assert.Equal(NginxEndpointParser.Parse(pool.Target).Address,
            Assert.Single(NginxCollectorParser.Parse(Fixture("nginx-T-set-variables.txt")).Upstreams).Name);
    }

    [Fact]
    public void Parse_UnresolvableVariablesStayUnresolved()
    {
        var edge = Assert.Single(Servers(), server => server.ServerNames.Any(name => name.Value == "edge.example.test"));

        foreach (var original in new[]
        {
            "$conflict",         // set twice with different values in the same scope
            "$from_var",         // value itself contains a variable
            "$only_in_if",       // set only inside an if block
            "http://$never_set", // no set at all
            "$host",             // nginx built-in, even though a set exists
            "http://$host$request_uri",
            "$a$request_uri",    // two variables
            "http://$h:8080",    // text after the variable that is not a path
            "http://$full"       // value already carries a scheme
        })
        {
            var proxy = Assert.Single(edge.Proxies, item => item.Target == original);
            Assert.True(proxy.IsUnresolved, original);
            Assert.Null(proxy.ResolvedFrom);
        }
    }

    [Fact]
    public void Parse_SetInsideIfBlockDoesNotOverrideTheEnclosingScope()
    {
        var edge = Assert.Single(Servers(), server => server.ServerNames.Any(name => name.Value == "edge.example.test"));

        var proxy = Assert.Single(edge.Locations, location => location.Match == "/conditional-override/").Proxies.Single();
        Assert.Equal("http://10.0.0.9:9000", proxy.Target);
        Assert.Equal(8, proxy.ResolvedFrom!.Line);
    }

    [Fact]
    public void Parse_OtherProxyDirectivesAreResolvedToo()
    {
        var edge = Assert.Single(Servers(), server => server.ServerNames.Any(name => name.Value == "edge.example.test"));

        var fastcgi = Assert.Single(edge.Proxies, proxy => proxy.Directive == "fastcgi_pass");
        Assert.Equal("127.0.0.1:9000", fastcgi.Target);
        Assert.Equal("$php", fastcgi.ResolvedFrom!.OriginalTarget);
    }

    [Fact]
    public void Parse_SetAtHttpScopeIsUsedWhenServerAndLocationDoNotSetTheVariable()
    {
        const string config = """
            http {
                set $shared http://10.1.1.1:81;
                server {
                    server_name a.test;
                    location / { proxy_pass $shared; }
                }
            }
            """;

        var proxy = Assert.Single(Assert.Single(NginxCollectorParser.Parse(config).Servers).Proxies);

        Assert.Equal("http://10.1.1.1:81", proxy.Target);
        Assert.Equal(2, proxy.ResolvedFrom!.Line);
    }

    [Fact]
    public void Parse_SameValueSetTwiceInOneScopeStillResolves()
    {
        const string config = """
            http {
                server {
                    set $u http://10.1.1.1:81;
                    set $u http://10.1.1.1:81;
                    location / { proxy_pass $u; }
                }
            }
            """;

        var proxy = Assert.Single(Assert.Single(NginxCollectorParser.Parse(config).Servers).Proxies);

        Assert.Equal("http://10.1.1.1:81", proxy.Target);
    }

    [Fact]
    public void ProxyInfo_DeserializesResultsStoredBeforeSetResolutionExisted()
    {
        const string json = """
            {"Directive":"proxy_pass","Target":"http://10.0.0.1","IsUnresolved":false,"FilePath":"/a","Line":3}
            """;

        var proxy = System.Text.Json.JsonSerializer.Deserialize<NginxProxyInfo>(json);

        Assert.NotNull(proxy);
        Assert.Null(proxy.ResolvedFrom);
    }

    [Fact]
    public async Task CollectAsync_UsesSbinNginxWhenNonLoginPathOmitsSbin()
    {
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "staging");
        server.RecordCapabilities(true, "Debian 12", DateTimeOffset.UtcNow);
        var runner = new FixtureSshRunner();
        var collector = new NginxCollector();
        var context = new ServerContext(server, runner);

        Assert.True(await collector.IsApplicableAsync(context, CancellationToken.None));
        var output = await collector.CollectAsync(context, CancellationToken.None);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains(runner.Calls, call => call.Command.Contains("/usr/sbin/nginx", StringComparison.Ordinal));
        Assert.True(runner.Calls.Single(call => call.Command.Contains("/usr/sbin/nginx -T", StringComparison.Ordinal)).Options.UseSudo);
    }

    private sealed class FixtureSshRunner : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            var output = command.Contains("nginx -T", StringComparison.Ordinal) ? Fixture("nginx-T.txt") : string.Empty;
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }

    private static IReadOnlyList<NginxServerInfo> Servers() =>
        NginxCollectorParser.Parse(Fixture("nginx-T-set-variables.txt")).Servers;

    private static NginxServerInfo MapServer() =>
        Assert.Single(Servers(), server => server.ServerNames.Any(name => name.Value == "map.example.test"));

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
