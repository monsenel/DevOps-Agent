namespace DevOpsAgent.Application.Servers;

public sealed record ServerInput(
    string Name,
    string Host,
    int Port,
    string User,
    string KeyFile,
    string? KeyPassphrase,
    Guid? JumpServerId,
    string Group,
    IReadOnlyList<string> Tags);

public sealed record ServerSummary(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string User,
    string KeyFile,
    bool HasKeyPassphrase,
    Guid? JumpServerId,
    string? JumpServerName,
    string Group,
    IReadOnlyList<string> Tags,
    bool? SudoCapable,
    DateTimeOffset? LastSeenAtUtc,
    string? OsInfo);

