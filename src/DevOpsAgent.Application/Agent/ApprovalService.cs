using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Agent;

namespace DevOpsAgent.Application.Agent;

public enum ToolApprovalState
{
    ExecuteReadOnly,
    PendingApproval,
    Denied
}

public sealed record ToolApprovalRequest(
    string ToolName,
    JsonElement Arguments,
    IReadOnlyList<Guid> ServerIds,
    Guid? ConversationId = null);

public sealed record ToolApprovalResult(
    ToolApprovalState State,
    string Message,
    Guid? ActionId = null,
    object? ValidatedArguments = null);

public sealed class ApprovalService(
    ToolRegistry tools,
    ExecutionModeService executionModes,
    IServerRepository servers,
    IActionRecordRepository actions,
    TimeProvider timeProvider)
{
    public async Task<ToolApprovalResult> EvaluateAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!tools.TryGet(request.ToolName, out var descriptor))
        {
            return new(ToolApprovalState.Denied, $"Unknown tool '{request.ToolName}'.");
        }

        object arguments;
        try
        {
            arguments = tools.ValidateArguments(request.ToolName, request.Arguments);
        }
        catch (ArgumentException exception)
        {
            return new(ToolApprovalState.Denied, exception.Message);
        }

        var argumentIds = (arguments as IServerTargetArgs)?.ServerIds?.Distinct().ToArray() ?? [];
        var requestedIds = request.ServerIds.Distinct().ToArray();
        if (argumentIds.Length > 0 && requestedIds.Length > 0
            && !argumentIds.Order().SequenceEqual(requestedIds.Order()))
        {
            return new(ToolApprovalState.Denied, "Target servers in the tool arguments do not match the approved targets.");
        }

        var targetIds = requestedIds.Length > 0 ? requestedIds : argumentIds;
        if (descriptor.RequiresServer && targetIds.Length == 0)
        {
            return new(ToolApprovalState.Denied, $"Tool '{request.ToolName}' requires at least one target server.");
        }
        var allServers = await servers.ListAsync(cancellationToken);
        var targets = allServers.Where(server => targetIds.Contains(server.Id)).ToArray();
        if (targets.Length != targetIds.Length)
        {
            return new(ToolApprovalState.Denied, "One or more target servers no longer exist.");
        }

        if (descriptor.Access == ToolAccess.ReadOnly && !descriptor.RequiresApproval)
        {
            return new(ToolApprovalState.ExecuteReadOnly, "Read-only tool is permitted.", ValidatedArguments: arguments);
        }

        if (targets.Length == 0)
        {
            return new(ToolApprovalState.Denied, "Choose at least one target server for this action.");
        }

        var policies = new List<ExecutionModePolicy>();
        foreach (var group in targets.Select(server => server.Group).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            policies.Add(await executionModes.GetForGroupAsync(group, cancellationToken));
        }

        if (policies.Any(policy => policy.Mode == ExecutionMode.ReadOnly))
        {
            return new(ToolApprovalState.Denied, "A target server group is in read-only mode.");
        }

        var action = new ActionRecord(
            request.ToolName,
            string.Join(",", policies.Select(policy => policy.Group).Order(StringComparer.Ordinal)),
            JsonSerializer.Serialize(targets.Select(server => server.Id)),
            request.Arguments.GetRawText(),
            request.ConversationId,
            timeProvider.GetUtcNow());
        await actions.AddAsync(action, cancellationToken);
        // Every mutating call needs explicit approval unless a read-only group blocked it above (no auto-execute path exists).
        return new(ToolApprovalState.PendingApproval, "This action needs explicit approval.", action.Id, arguments);
    }
}
