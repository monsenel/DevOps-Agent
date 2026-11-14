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

        Assert.Equal(2, runner.Calls.Count);
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
    public void UpdateCommandTimeoutCanBeConfiguredWithoutChangingOtherCollectors()
    {
        var options = new UpdateCollectorOptions { CommandTimeoutSeconds = 420 };

        Assert.Equal(TimeSpan.FromSeconds(420), options.CommandTimeout);
    }

    private sealed class OfflineAptRunner : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            var output = command.Contains("update_output=$(", StringComparison.Ordinal)
                ? "__DVA_REPOSITORIES_UNREACHABLE__\n"
                : string.Empty;
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(20)));
        }
    }
}
