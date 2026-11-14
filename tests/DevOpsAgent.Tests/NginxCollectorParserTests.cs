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

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
