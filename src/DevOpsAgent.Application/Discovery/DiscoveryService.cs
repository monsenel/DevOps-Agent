using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Application.Discovery;

public sealed class DiscoveryService(
    IServerRepository serverRepository,
    ISnapshotRepository snapshotRepository,
    IGraphRepository graphRepository,
    GraphBuilder graphBuilder,
    ISshRunner sshRunner,
    IEnumerable<ICollector> collectors,
    TimeProvider timeProvider)
{
    private const int MaxDegreeOfParallelism = 8;

    public async Task<Snapshot> RunAsync(
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await RunInternalAsync(null, null, progress, cancellationToken);
    }

    public async Task<Snapshot> RunServerAndMergeAsync(
        Guid serverId,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var latestSnapshot = await snapshotRepository.GetLatestAsync(cancellationToken);
        return await RunInternalAsync(serverId, latestSnapshot?.Id, progress, cancellationToken);
    }

    private async Task<Snapshot> RunInternalAsync(
        Guid? onlyServerId,
        Guid? mergeSnapshotId,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var allServers = await serverRepository.ListAsync(cancellationToken);
        var servers = onlyServerId is Guid requestedServerId
            ? allServers.Where(server => server.Id == requestedServerId).ToArray()
            : allServers;
        if (onlyServerId.HasValue && servers.Count == 0)
        {
            throw new ArgumentException("The requested server is not in the inventory.", nameof(onlyServerId));
        }

        var snapshotServerIds = mergeSnapshotId.HasValue
            ? allServers.Select(server => server.Id).ToArray()
            : servers.Select(server => server.Id).ToArray();
        var snapshot = await snapshotRepository.CreateAsync(
            snapshotServerIds,
            timeProvider.GetUtcNow(),
            cancellationToken);
        progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.Started,
            Message: $"Discovery started for {servers.Count} server(s)."));

        var runs = new ConcurrentBag<CollectorRun>();
        var serverContexts = new ConcurrentBag<ServerContext>();
        var succeededServers = 0;
        var failedServers = 0;
        var orderedCollectors = collectors.ToArray();

        try
        {
            await Parallel.ForEachAsync(
                servers,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                },
                async (server, serverCancellationToken) =>
                {
                    var serverSucceeded = true;
                    var context = new ServerContext(server, sshRunner);
                    serverContexts.Add(context);
                    progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.ServerStarted,
                        server.Id, server.Name));

                    foreach (var collector in orderedCollectors)
                    {
                        serverCancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.CollectorStarted,
                            server.Id, server.Name, collector.Name));

                        var run = await RunCollectorAsync(snapshot.Id, context, collector, serverCancellationToken);
                        runs.Add(run);
                        if (run.Status == CollectorRunStatus.Failed)
                        {
                            serverSucceeded = false;
                        }

                        progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.CollectorCompleted,
                            server.Id, server.Name, collector.Name, run.Error ?? run.Status.ToString()));
                    }

                    if (serverSucceeded)
                    {
                        Interlocked.Increment(ref succeededServers);
                    }
                    else
                    {
                        Interlocked.Increment(ref failedServers);
                    }

                    progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.ServerCompleted,
                        server.Id, server.Name, Message: serverSucceeded ? "Completed" : "Completed with errors"));
                });

            var graph = graphBuilder.Build(snapshot.Id, serverContexts.Select(context => new ServerDiscoveryData(
                context.Server,
                GetPreviousResult<HostDiscoveryResult>(context, "host"),
                GetPreviousResult<ListenerDiscoveryResult>(context, "listeners"),
                GetPreviousResult<DockerDiscoveryResult>(context, "docker"),
                GetPreviousResult<SystemdDiscoveryResult>(context, "systemd"),
                GetPreviousResult<NginxDiscoveryResult>(context, "nginx"),
                GetPreviousResult<ConnectionDiscoveryResult>(context, "connections"))).ToArray());
            if (mergeSnapshotId is Guid previousId)
            {
                await graphRepository.MergeAsync(snapshot.Id, previousId, graph, cancellationToken);
            }
            else
            {
                await graphRepository.SaveAsync(snapshot.Id, graph, cancellationToken);
            }
            await snapshotRepository.CompleteAsync(snapshot, runs.ToArray(), timeProvider.GetUtcNow(),
                succeededServers, failedServers, cancelled: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await snapshotRepository.CompleteAsync(snapshot, runs.ToArray(), timeProvider.GetUtcNow(),
                succeededServers, failedServers, cancelled: true, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            // Graph build/persist failures must not leave the snapshot Running forever.
            try
            {
                await snapshotRepository.FailAsync(snapshot, runs.ToArray(), timeProvider.GetUtcNow(),
                    succeededServers, failedServers, CancellationToken.None);
            }
            catch (Exception)
            {
                // Keep the original failure; it is the one the caller needs to see.
            }

            throw;
        }

        progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.Completed,
            Message: $"Discovery completed: {succeededServers} succeeded, {failedServers} failed."));
        return snapshot;
    }

    private static T? GetPreviousResult<T>(ServerContext context, string collectorName) where T : class =>
        context.TryGetPreviousResult<T>(collectorName, out var result) ? result : null;

    private async Task<CollectorRun> RunCollectorAsync(
        Guid snapshotId,
        ServerContext context,
        ICollector collector,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        var output = new CollectorOutput(string.Empty, string.Empty, null, TimeSpan.Zero);
        try
        {
            if (!await collector.IsApplicableAsync(context, cancellationToken))
            {
                return CreateRun(CollectorRunStatus.Skipped, null, null, null);
            }

            output = await collector.CollectAsync(context, cancellationToken);
            if (output.ExitCode is not null and not 0)
            {
                return CreateRun(CollectorRunStatus.Failed, output.ExitCode,
                    string.IsNullOrWhiteSpace(output.StandardError)
                        ? $"Collector exited with code {output.ExitCode}."
                        : output.StandardError,
                    null);
            }

            var result = collector.Parse(output);
            return result.IsSuccess
                ? RecordSuccessfulResult()
                : CreateRun(CollectorRunStatus.Failed, output.ExitCode, result.Error, null);

            CollectorRun RecordSuccessfulResult()
            {
                if (result.Data is { } data)
                {
                    context.AddResult(collector.Name, data);
                }

                return CreateRun(CollectorRunStatus.Succeeded, output.ExitCode, null,
                    result.Data is { } dataResult ? JsonSerializer.Serialize(dataResult) : null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateRun(CollectorRunStatus.Failed, output.ExitCode, exception.Message, null);
        }

        CollectorRun CreateRun(CollectorRunStatus status, int? exitCode, string? error, string? resultJson) =>
            new(snapshotId, context.Server.Id, collector.Name, status,
                output.StandardOutput, output.StandardError, exitCode, error, resultJson,
                output.Duration == TimeSpan.Zero ? stopwatch.Elapsed : output.Duration,
                startedAt);
    }
}
