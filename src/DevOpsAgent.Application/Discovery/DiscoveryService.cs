using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Application.Discovery;

public sealed class DiscoveryService(
    IServerRepository serverRepository,
    ISnapshotRepository snapshotRepository,
    ISshRunner sshRunner,
    IEnumerable<ICollector> collectors,
    TimeProvider timeProvider)
{
    private const int MaxDegreeOfParallelism = 8;

    public async Task<Snapshot> RunAsync(
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var servers = await serverRepository.ListAsync(cancellationToken);
        var snapshot = await snapshotRepository.CreateAsync(
            servers.Select(server => server.Id).ToArray(),
            timeProvider.GetUtcNow(),
            cancellationToken);
        progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.Started,
            Message: $"Discovery started for {servers.Count} server(s)."));

        var runs = new ConcurrentBag<CollectorRun>();
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

            await snapshotRepository.CompleteAsync(snapshot, runs.ToArray(), timeProvider.GetUtcNow(),
                succeededServers, failedServers, cancelled: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await snapshotRepository.CompleteAsync(snapshot, runs.ToArray(), timeProvider.GetUtcNow(),
                succeededServers, failedServers, cancelled: true, CancellationToken.None);
            throw;
        }

        progress?.Report(new DiscoveryProgress(snapshot.Id, DiscoveryProgressStage.Completed,
            Message: $"Discovery completed: {succeededServers} succeeded, {failedServers} failed."));
        return snapshot;
    }

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
                ? CreateRun(CollectorRunStatus.Succeeded, output.ExitCode, null,
                    result.Data is { } data ? JsonSerializer.Serialize(data) : null)
                : CreateRun(CollectorRunStatus.Failed, output.ExitCode, result.Error, null);
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
