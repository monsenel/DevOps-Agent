using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Agent;

public sealed record ActionServerResult(
    Guid ServerId,
    string ServerName,
    string Command,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    long DurationMilliseconds,
    string? Error,
    string? Endpoint = null,
    bool UseSudo = false)
{
    /// <summary>The command as the operator should review it, including the sudo wrapper that is applied when it runs.</summary>
    [JsonIgnore]
    public string DisplayCommand => UseSudo ? $"sudo {Command}" : Command;
}

public sealed record ActionLogEntry(
    ActionRecord Action,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<ActionServerResult> Results,
    IReadOnlyList<ActionServerResult> PendingCommandPreviews);

public sealed class ActionExecutionService(
    IActionRecordRepository actions,
    IServerRepository servers,
    ToolRegistry tools,
    ExecutionModeService executionModes,
    ToolCommandBuilder commandBuilder,
    ISshRunner sshRunner,
    TimeProvider timeProvider)
{
    private const int MaxParallelServers = 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ActionLogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await actions.ListAsync(cancellationToken);
        var inventory = await servers.ListAsync(cancellationToken);
        var byId = inventory.ToDictionary(server => server.Id);
        return records.Select(action =>
        {
            var serverIds = JsonSerializer.Deserialize<Guid[]>(action.ServerIdsJson, JsonOptions) ?? [];
            var names = serverIds.Select(id => byId.TryGetValue(id, out var server) ? server.Name : $"Removed server {id.ToString("N")[..8]}").ToArray();
            var targetServers = serverIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
            ActionServerResult[] results;
            try
            {
                results = JsonSerializer.Deserialize<ActionServerResult[]>(action.ResultsJson, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                results = [];
            }

            var pendingCommands = Array.Empty<ActionServerResult>();
            if (action.Status == ActionRecordStatus.Proposed && targetServers.Length > 0)
            {
                var plan = commandBuilder.Build(action);
                pendingCommands = targetServers.Select(server => new ActionServerResult(server.Id, server.Name, plan.Command,
                    null, string.Empty, string.Empty, 0, null, Endpoint(server), plan.UseSudo)).ToArray();
            }

            return new ActionLogEntry(action, names, results, pendingCommands);
        }).ToArray();
    }

    public async Task<ActionRecord> ApproveAndRunAsync(Guid actionId, string approvedBy, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ArgumentException("Approver is required.", nameof(approvedBy));
        var action = await actions.GetAsync(actionId, cancellationToken)
            ?? throw new KeyNotFoundException("The proposed action no longer exists.");
        var descriptor = tools.TryGet(action.ToolName, out var registered)
            ? registered
            : throw new InvalidOperationException("The proposed action uses an unknown tool.");
        if (descriptor.Access != ToolAccess.Mutating)
        {
            throw new InvalidOperationException("Only mutating tools can be approved for execution.");
        }

        var targetIds = JsonSerializer.Deserialize<Guid[]>(action.ServerIdsJson, JsonOptions) ?? [];
        var inventory = await servers.ListAsync(cancellationToken);
        var targets = inventory.Where(server => targetIds.Contains(server.Id)).ToArray();
        if (targets.Length != targetIds.Distinct().Count() || targets.Length == 0)
        {
            throw new InvalidOperationException("One or more target servers no longer exist.");
        }

        var groupPolicies = new List<ExecutionModePolicy>();
        foreach (var group in targets.Select(server => server.Group).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            groupPolicies.Add(await executionModes.GetForGroupAsync(group, cancellationToken));
        }

        if (groupPolicies.Any(policy => policy.Mode == ExecutionMode.ReadOnly))
        {
            throw new InvalidOperationException("A target server group is now in read-only mode; the action cannot run.");
        }

        var plan = commandBuilder.Build(action);
        // Atomic claim: only one caller can move Proposed -> Running, so an action can never execute twice.
        if (!await actions.TryTransitionFromProposedAsync(actionId, ActionRecordStatus.Running, approvedBy.Trim(),
                timeProvider.GetUtcNow(), cancellationToken))
        {
            throw new InvalidOperationException("The action was already approved, rejected, or is running.");
        }

        action = await actions.GetAsync(actionId, cancellationToken)
            ?? throw new KeyNotFoundException("The proposed action no longer exists.");

        using var throttle = new SemaphoreSlim(MaxParallelServers);
        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(targets.Select(server => ExecuteServerAsync(plan, server, throttle, cancellationToken)));
        stopwatch.Stop();
        var commands = results.ToDictionary(result => result.ServerId, result => result.Command);
        var resultsJson = JsonSerializer.Serialize(results, JsonOptions);
        var commandJson = JsonSerializer.Serialize(commands, JsonOptions);
        var exitCodes = results.Select(result => result.ExitCode).Distinct().ToArray();
        var succeeded = results.Length > 0 && results.All(result => result.ExitCode == 0 && result.Error is null);
        action.Complete(commandJson, resultsJson, exitCodes.Length == 1 ? exitCodes[0] : null,
            stopwatch.ElapsedMilliseconds, timeProvider.GetUtcNow(), succeeded);
        await actions.SaveAsync(action, CancellationToken.None);
        return action;
    }

    public async Task<ActionRecord> RejectAsync(Guid actionId, string rejectedBy, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rejectedBy)) throw new ArgumentException("Reviewer is required.", nameof(rejectedBy));
        _ = await actions.GetAsync(actionId, cancellationToken)
            ?? throw new KeyNotFoundException("The proposed action no longer exists.");
        if (!await actions.TryTransitionFromProposedAsync(actionId, ActionRecordStatus.Rejected, rejectedBy.Trim(),
                timeProvider.GetUtcNow(), cancellationToken))
        {
            throw new InvalidOperationException("Only proposed actions can be approved or rejected.");
        }

        return await actions.GetAsync(actionId, cancellationToken)
            ?? throw new KeyNotFoundException("The proposed action no longer exists.");
    }

    private static string Endpoint(Server server) => $"{server.Host}:{server.Port}";

    private async Task<ActionServerResult> ExecuteServerAsync(
        ToolCommand plan,
        Server server,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await throttle.WaitAsync(cancellationToken);
            acquired = true;
            var result = await sshRunner.RunAsync(server.Id, plan.Command,
                new SshRunOptions(TimeSpan.FromMinutes(10), plan.UseSudo), cancellationToken);
            return new(server.Id, server.Name, plan.Command, result.ExitCode, result.StandardOutput, result.StandardError,
                (long)result.Duration.TotalMilliseconds, null, Endpoint(server), plan.UseSudo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(server.Id, server.Name, plan.Command, null, string.Empty, string.Empty, 0, "Execution cancelled.", Endpoint(server), plan.UseSudo);
        }
        catch (Exception exception)
        {
            return new(server.Id, server.Name, plan.Command, null, string.Empty, string.Empty, 0, exception.Message, Endpoint(server), plan.UseSudo);
        }
        finally
        {
            if (acquired) throttle.Release();
        }
    }
}
