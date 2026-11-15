using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Tests;

public sealed class UpdatesCollectorTests
{
    [Fact]
    public async Task AptRepositoryFailureReturnsClearErrorAndUsesDedicatedTimeout()
    {
        var runner = new OfflineAptRunner();
        var server = new Server("srv-db", "srv-db", 22, "dva", "key", "staging");
        var collector = new UpdatesCollector(new UpdateCollectorOptions());

        var raw = await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);
        var parsed = collector.Parse(raw);

        // The check for apt, the update scan, and (T8.6) the read-only list of package sources.
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(DevOpsAgent.Infrastructure.Parsers.PackageSourcesParser.AptSourcesCommand, runner.Calls[2].Command);
        Assert.False(runner.Calls[2].Options.UseSudo);
        var updateCall = runner.Calls[1];
        Assert.Equal(TimeSpan.FromSeconds(300), updateCall.Options.Timeout);
        Assert.True(updateCall.Options.UseSudo);
        Assert.Contains("Acquire::Retries=0", updateCall.Command, StringComparison.Ordinal);
        Assert.Contains("Acquire::http::Timeout=10", updateCall.Command, StringComparison.Ordinal);
        Assert.Contains("Acquire::https::Timeout=10", updateCall.Command, StringComparison.Ordinal);
        Assert.False(parsed.IsSuccess);
        Assert.Equal("Repositories unreachable (apt): one or more configured package repositories could not be reached.", parsed.Error);
    }

    [Fact]
    public async Task CacheOnlyAptScanNeverRefreshesListsOrUsesSudo()
    {
        var runner = new OfflineAptRunner { ProbeOutput = "1763164800.5\n" };
        var server = new Server("srv-db", "srv-db", 22, "dva", "key", "prod");
        var collector = new UpdatesCollector(new UpdateCollectorOptions(), cacheOnly: true);

        var raw = await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);
        var output = System.Text.Json.JsonSerializer.Deserialize<DevOpsAgent.Application.Updates.UpdateCollectionOutput>(raw.StandardOutput)!;

        Assert.All(runner.Calls, call => Assert.False(call.Options.UseSudo));
        Assert.All(runner.Calls.Where(call => !call.Command.StartsWith("command -v", StringComparison.Ordinal)), call =>
        {
            Assert.DoesNotContain("apt-get", call.Command, StringComparison.Ordinal);
            Assert.DoesNotContain(" update", call.Command.Replace("--upgradable", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        });
        Assert.Contains(runner.Calls, call => call.Command.StartsWith("apt list --upgradable", StringComparison.Ordinal));
        Assert.True(output.CacheOnly);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763164800), output.PackageListsRefreshedAtUtc);
    }

    [Fact]
    public async Task CacheOnlyDnfScanUsesCacheOnlyFlag()
    {
        var runner = new OfflineAptRunner { AptAvailable = false };
        var server = new Server("srv-rh", "srv-rh", 22, "dva", "key", "prod");
        var collector = new UpdatesCollector(new UpdateCollectorOptions(), cacheOnly: true);

        await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);

        var dnfCalls = runner.Calls.Where(call => call.Command.StartsWith("dnf ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(dnfCalls);
        Assert.All(dnfCalls, call => Assert.StartsWith("dnf -C ", call.Command, StringComparison.Ordinal));
        Assert.All(runner.Calls, call => Assert.False(call.Options.UseSudo));
    }

    [Fact]
    public async Task AptScan_ReadsSourcesAndOrigins_WithoutSudo_AndLabelsEachPackage()
    {
        string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var runner = new ScriptedRunner(command =>
            command.Contains("update_output=$(", StringComparison.Ordinal) ? "Listing...\ndocker-ce/jammy 5:25.0.0-1~ubuntu.22.04~jammy amd64 [upgradable from: 5:24.0.7-1~ubuntu.22.04~jammy]\nopenssl/jammy-security 3.0.2-0ubuntu1.19 amd64 [upgradable from: 3.0.2-0ubuntu1.18]\n"
            : command.StartsWith("for f in /etc/apt/", StringComparison.Ordinal) ? Fixture("apt-sources.txt")
            : command.StartsWith("apt-cache policy", StringComparison.Ordinal) ? Fixture("apt-cache-policy.txt")
            : string.Empty);
        var collector = new UpdatesCollector(new UpdateCollectorOptions());
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");

        var raw = await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);
        var packages = collector.Parse(raw).Data!.Value.Deserialize<DevOpsAgent.Application.Updates.UpdatePackageDto[]>()!;
        var output = System.Text.Json.JsonSerializer.Deserialize<DevOpsAgent.Application.Updates.UpdateCollectionOutput>(raw.StandardOutput)!;

        Assert.Equal("Docker CE · jammy", packages.Single(item => item.Name == "docker-ce").Repository);
        Assert.Equal("Ubuntu · jammy-security", packages.Single(item => item.Name == "openssl").Repository);
        Assert.True(packages.Single(item => item.Name == "openssl").IsSecurity);
        Assert.Equal(7, UpdatesCollector.ParseSources(output).Count);
        Assert.Contains(runner.Calls, call => call.Command.StartsWith("apt-cache policy", StringComparison.Ordinal)
            && call.Command.Contains("-- 'docker-ce' 'openssl'", StringComparison.Ordinal));
        Assert.All(runner.Calls.Where(call => !call.Command.Contains("update_output=$(", StringComparison.Ordinal)),
            call => Assert.False(call.Options.UseSudo));
    }

    private sealed class ScriptedRunner(Func<string, string> output) : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            return Task.FromResult(new RemoteCommandResult(0, output(command), string.Empty, TimeSpan.FromMilliseconds(5)));
        }
    }

    [Theory]
    [InlineData("1763164800\n", 1763164800L)]
    [InlineData("1700000000.123\n1763164800.9\n", 1763164800L)]
    [InlineData("", null)]
    [InlineData("garbage", null)]
    [InlineData("0", null)]
    public void PackageListProbeParsesEpochSeconds(string output, long? expected)
    {
        var parsed = DevOpsAgent.Application.Updates.PackageListProbe.Parse(output);

        Assert.Equal(expected is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null, parsed);
    }

    [Fact]
    public void PackageListFreshnessTextNamesCacheOnlyScans()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("Package lists last refreshed 3 h ago · cached lists only (read-only group)",
            DevOpsAgent.Application.Updates.PackageListProbe.Describe(now.AddHours(-3), cacheOnly: true, now));
        Assert.Equal("Package lists last refreshed: unknown",
            DevOpsAgent.Application.Updates.PackageListProbe.Describe(null, cacheOnly: false, now));
    }

    [Fact]
    public void UpdateCommandTimeoutCanBeConfiguredWithoutChangingOtherCollectors()
    {
        var options = new UpdateCollectorOptions { CommandTimeoutSeconds = 420 };

        Assert.Equal(TimeSpan.FromSeconds(420), options.CommandTimeout);
    }

    private sealed class OfflineAptRunner : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];
        public string ProbeOutput { get; init; } = string.Empty;
        public bool AptAvailable { get; init; } = true;

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            if (!AptAvailable && command.Contains("apt-get", StringComparison.Ordinal) && command.StartsWith("command -v", StringComparison.Ordinal))
                return Task.FromResult(new RemoteCommandResult(1, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5)));
            var output = command == DevOpsAgent.Application.Updates.PackageListProbe.Command ? ProbeOutput
                : command.Contains("update_output=$(", StringComparison.Ordinal) ? "__DVA_REPOSITORIES_UNREACHABLE__\n"
                : string.Empty;
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(20)));
        }
    }
}
