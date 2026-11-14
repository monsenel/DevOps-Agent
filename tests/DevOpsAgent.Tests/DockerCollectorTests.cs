using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Tests;

public sealed class DockerCollectorTests
{
    [Fact]
    public void Parse_FixturesExtractContainerNetworkComposePortsAndEnvironmentNames()
    {
        var result = DockerCollectorParser.Parse(Fixture("docker-inspect.json"), Fixture("docker-network-inspect.json"));
        var api = result.Containers.Single(container => container.Name == "api-svc");

        Assert.Equal("nginx:1.27", api.Image);
        Assert.Equal("running", api.State);
        Assert.Equal("app-stack", api.ComposeProject);
        Assert.Equal("api", api.ComposeService);
        Assert.Equal(220, api.MainPid);
        Assert.Equal(new[] { "DB_PASSWORD", "MODE", "PORT" }, api.EnvironmentVariableNames);
        Assert.Contains(api.Networks, network => network.NetworkName == "app-stack_default" &&
            network.IPv4Address == "172.20.0.10" && network.IPv6Address == "2001:db8:20::10" &&
            network.Aliases.Contains("api"));
        Assert.Contains(api.PublishedPorts, port => port.ContainerPort == 8080 && port.HostPort == 80 &&
            port.Protocol == "tcp" && port.HostIp == "0.0.0.0");

        var appNetwork = Assert.Single(result.Networks);
        Assert.Equal("bridge", appNetwork.Driver);
        Assert.Contains(appNetwork.Containers, container => container.Name == "api-svc" &&
            container.IPv4Address == "172.20.0.10" && container.Aliases.Contains("api"));

        var listener = ListenerCollectorParser.Parse("tcp LISTEN 0 64 0.0.0.0:80 0.0.0.0:* users:((\"nginx\",pid=220,fd=6))").Single();
        Assert.Equal(api.MainPid, listener.ProcessId);
    }

    [Fact]
    public async Task CollectAsync_RedactsEnvironmentValuesBeforeReturningPersistableRawOutput()
    {
        var runner = new FixtureSshRunner();
        var server = new Server("docker-host", "docker.example.internal", 22, "deploy", "id_ed25519", "prod");
        var collector = new DockerCollector();
        var context = new ServerContext(server, runner);

        Assert.True(await collector.IsApplicableAsync(context, CancellationToken.None));
        var output = await collector.CollectAsync(context, CancellationToken.None);
        var result = collector.Parse(output);

        Assert.Equal(0, output.ExitCode);
        Assert.DoesNotContain("fixture-secret-do-not-store", output.StandardOutput);
        Assert.DoesNotContain("another-fixture-secret", output.StandardOutput);
        Assert.True(result.IsSuccess, result.Error);
        var api = result.Data!.Value.GetProperty("Containers").EnumerateArray()
            .Single(container => container.GetProperty("Name").GetString() == "api-svc");
        Assert.Contains(api.GetProperty("EnvironmentVariableNames").EnumerateArray(), value => value.GetString() == "DB_PASSWORD");
        Assert.Equal(
            ["command -v docker >/dev/null 2>&1", "docker ps -q",
                "docker inspect aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "docker network ls -q",
                "docker network inspect cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"],
            runner.Calls.Select(call => call.Command));
        Assert.All(runner.Calls, call => Assert.False(call.Options.UseSudo));
    }

    [Fact]
    public async Task CollectAsync_RejectsUntrustedIdsInsteadOfInterpolatingThemIntoCommands()
    {
        var runner = new FixtureSshRunner { ContainerIds = "abcdefabcdef;touch /tmp/nope" };
        var server = new Server("docker-host", "docker.example.internal", 22, "deploy", "id_ed25519", "prod");
        var collector = new DockerCollector();

        var output = await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);

        Assert.Equal(1, output.ExitCode);
        Assert.DoesNotContain(runner.Calls, call => call.Command.Contains("docker inspect", StringComparison.Ordinal));
        Assert.DoesNotContain("touch", string.Join(' ', runner.Calls.Select(call => call.Command)), StringComparison.Ordinal);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class FixtureSshRunner : ISshRunner
    {
        public string ContainerIds { get; init; } = Fixture("docker-ps.txt");
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            var output = command switch
            {
                "docker ps -q" => ContainerIds,
                "docker network ls -q" => Fixture("docker-network-ls.txt"),
                var value when value.StartsWith("docker inspect ", StringComparison.Ordinal) => Fixture("docker-inspect.json"),
                var value when value.StartsWith("docker network inspect ", StringComparison.Ordinal) => Fixture("docker-network-inspect.json"),
                _ => string.Empty
            };
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
