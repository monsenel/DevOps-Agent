using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Updates;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class UpdatesCollectorService(
    IServerRepository servers,
    ISshRunner sshRunner,
    IOptions<UpdateCollectorOptions> options) : IUpdatesCollector
{
    public async Task<CollectedUpdateResult> CollectAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException("The server no longer exists.");
        var collector = new UpdatesCollector(options.Value);
        var context = new ServerContext(server, sshRunner);
        if (!await collector.IsApplicableAsync(context, cancellationToken))
            throw new InvalidOperationException("This server has neither apt-get nor dnf installed.");
        var raw = await collector.CollectAsync(context, cancellationToken);
        if (raw.ExitCode is not null and not 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw.StandardError) ? "Update scan failed." : raw.StandardError);
        var parsed = collector.Parse(raw);
        if (!parsed.IsSuccess || parsed.Data is null)
            throw new InvalidOperationException(parsed.Error ?? "Could not parse the update scan.");
        var output = JsonSerializer.Deserialize<UpdateCollectionOutput>(raw.StandardOutput)
            ?? throw new InvalidOperationException("Update scan output is invalid.");
        var manager = output.AptOutput.Length > 0 ? PackageManagerKind.Apt : PackageManagerKind.Dnf;
        return new(manager, JsonSerializer.Serialize(parsed.Data.Value.Deserialize<UpdatePackageDto[]>() ?? []));
    }
}
