using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Servers;

public sealed class ServerService(
    IServerRepository repository,
    ISecretProtector secretProtector,
    IServerKeyStore keyStore,
    ISshConnectionPool connectionPool,
    IKnownSecrets? knownSecrets = null)
{
    public async Task<IReadOnlyList<ServerSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var servers = await repository.ListAsync(cancellationToken);
        return ToSummaries(servers);
    }

    public async Task<ServerSummary> CreateAsync(ServerInput input, CancellationToken cancellationToken)
    {
        var servers = await repository.ListAsync(cancellationToken);
        Validate(input, null, servers);
        EnsureUniqueName(input.Name, servers, null);
        var server = new Server(
            input.Name.Trim(), input.Host.Trim(), input.Port, input.User.Trim(), input.KeyFile.Trim(), NormalizeGroup(input.Group));
        server.Update(
            input.Name.Trim(), input.Host.Trim(), input.Port, input.User.Trim(), input.KeyFile.Trim(), input.JumpServerId,
            NormalizeGroup(input.Group), SerializeTags(input.Tags), ProtectPassphrase(input.KeyPassphrase));
        ApplySudoPassword(server, input);
        await repository.AddAsync(server, cancellationToken);
        if (input.SudoPassword is { Length: > 0 }) knownSecrets?.Invalidate();
        return ToSummary(server, servers.Append(server));
    }

    public async Task<ServerSummary?> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken)
    {
        var server = await repository.GetAsync(id, cancellationToken);
        if (server is null)
        {
            return null;
        }

        var servers = await repository.ListAsync(cancellationToken);
        Validate(input, id, servers);
        EnsureUniqueName(input.Name, servers, id);

        var connectionBefore = (server.Host, server.Port, server.User, server.KeyFile, server.JumpServerId, server.ProtectedKeyPassphrase);
        server.Update(
            input.Name.Trim(), input.Host.Trim(), input.Port, input.User.Trim(), input.KeyFile.Trim(), input.JumpServerId,
            NormalizeGroup(input.Group), SerializeTags(input.Tags), ProtectPassphrase(input.KeyPassphrase));
        // A new or removed sudo password also ends a rejection (T7.R24); the SSH connection itself is not affected by it.
        ApplySudoPassword(server, input);
        await repository.SaveAsync(server, cancellationToken);
        // Every server's output is redacted with every stored sudo password (T7.R26): the shared list follows the edit.
        if (input.SudoPassword is { Length: > 0 } || input.RemoveSudoPassword) knownSecrets?.Invalidate();
        // Only a change to how the server is reached invalidates the pooled session; renaming or retagging a server
        // must not kill live sessions (for example a running package update).
        var connectionAfter = (server.Host, server.Port, server.User, server.KeyFile, server.JumpServerId, server.ProtectedKeyPassphrase);
        if (connectionBefore != connectionAfter)
        {
            connectionPool.Evict(id);
            // Servers reached through this one tunnel through the old connection and may carry a backoff earned on the old route.
            foreach (var dependentId in DependentsOf(id, servers)) connectionPool.Evict(dependentId);
        }

        return ToSummary(server, servers);
    }

    /// <summary>
    /// Replaces the server's allowed SSH source list from the editor text (T8.18). Bad input is reported in the result and
    /// nothing is saved; only this one field changes, so the pooled SSH connection and the rest of the server stay as they are.
    /// </summary>
    public async Task<SetAllowedSshSourcesResult> SetAllowedSshSourcesAsync(Guid serverId, string? text, CancellationToken cancellationToken)
    {
        var server = await repository.GetAsync(serverId, cancellationToken);
        if (server is null) return SetAllowedSshSourcesResult.NotFound();

        var parsed = SshSourceList.Parse(text);
        if (!parsed.IsValid) return SetAllowedSshSourcesResult.Invalid(parsed.Errors);

        server.SetAllowedSshSources(parsed.Entries);
        await repository.SaveAsync(server, cancellationToken);
        return SetAllowedSshSourcesResult.Success(parsed.Entries);
    }

    /// <summary>Every server whose jump-server route passes through <paramref name="jumpId"/> (directly or through further jumps).</summary>
    private static IReadOnlyList<Guid> DependentsOf(Guid jumpId, IReadOnlyCollection<Server> servers)
    {
        var dependents = new List<Guid>();
        var seen = new HashSet<Guid> { jumpId };
        for (var added = true; added;)
        {
            added = false;
            foreach (var candidate in servers.Where(candidate => candidate.JumpServerId is Guid via && seen.Contains(via) && !seen.Contains(candidate.Id)))
            {
                seen.Add(candidate.Id);
                dependents.Add(candidate.Id);
                added = true;
            }
        }

        return dependents;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var server = await repository.GetAsync(id, cancellationToken);
        if (server is null)
        {
            return false;
        }

        var servers = await repository.ListAsync(cancellationToken);
        if (servers.Any(candidate => candidate.JumpServerId == id))
        {
            throw new InvalidOperationException("This server is a jump server for another inventory entry.");
        }

        await repository.DeleteAsync(server, cancellationToken);
        connectionPool.Evict(id);
        if (server.HasSudoPassword) knownSecrets?.Invalidate();
        return true;
    }

    public IReadOnlyList<string> ListKeyFiles()
    {
        return keyStore.ListFileNames();
    }

    private void Validate(ServerInput input, Guid? currentId, IReadOnlyCollection<Server> servers)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("Server name is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Host)) throw new ArgumentException("Host is required.", nameof(input));
        if (input.Host.Any(char.IsWhiteSpace)) throw new ArgumentException("Host cannot contain whitespace.", nameof(input));
        if (input.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(input), "Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(input.User)) throw new ArgumentException("SSH user is required.", nameof(input));
        if (input.User.Any(char.IsWhiteSpace)) throw new ArgumentException("SSH user cannot contain whitespace.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Group)) throw new ArgumentException("Group is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.KeyFile)) throw new ArgumentException("A key file is required.", nameof(input));

        var keyFile = Path.GetFileName(input.KeyFile.Trim());
        if (!string.Equals(keyFile, input.KeyFile.Trim(), StringComparison.Ordinal) || !keyStore.Contains(keyFile))
        {
            throw new ArgumentException("Choose an existing key file from /keys.", nameof(input));
        }

        if (input.JumpServerId == currentId && currentId.HasValue)
        {
            throw new ArgumentException("A server cannot use itself as its jump server.", nameof(input));
        }

        if (input.JumpServerId.HasValue && !servers
                .Any(server => server.Id == input.JumpServerId.Value && server.Id != currentId))
        {
            throw new ArgumentException("Choose an existing inventory server as the jump server.", nameof(input));
        }

        if (input.JumpServerId is Guid jumpServerId)
        {
            var byId = servers.ToDictionary(server => server.Id);
            var visited = new HashSet<Guid> { currentId ?? Guid.Empty };
            var nextId = jumpServerId;
            while (true)
            {
                if (!visited.Add(nextId))
                {
                    throw new ArgumentException("The configured jump-server route contains a cycle.", nameof(input));
                }

                if (!byId.TryGetValue(nextId, out var jumpServer) || jumpServer.JumpServerId is not Guid nextJumpId)
                {
                    break;
                }

                nextId = nextJumpId;
            }
        }

        if (input.Tags.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Tags cannot be empty.", nameof(input));
        }

        // sudo -S reads one line, so a line break would end the password early. The value is never echoed in a message.
        if (!string.IsNullOrEmpty(input.SudoPassword))
        {
            // Every stored password is removed from all server output (T7.R26); a very short one would mangle it.
            if (input.SudoPassword.Length < MinSudoPasswordLength)
                throw new ArgumentException($"The sudo password must be at least {MinSudoPasswordLength} characters.", nameof(input));
            if (input.SudoPassword.Length > MaxSudoPasswordLength)
                throw new ArgumentException($"The sudo password can be at most {MaxSudoPasswordLength} characters.", nameof(input));
            if (input.SudoPassword.Any(character => character is '\n' or '\r' or '\0'))
                throw new ArgumentException("The sudo password cannot contain line breaks.", nameof(input));
            if (input.RemoveSudoPassword)
                throw new ArgumentException("Enter a new sudo password or remove the saved one, not both.", nameof(input));
        }
    }

    public const int MaxSudoPasswordLength = 512;
    public const int MinSudoPasswordLength = 6;

    private void ApplySudoPassword(Server server, ServerInput input)
    {
        if (!string.IsNullOrEmpty(input.SudoPassword)) server.SetSudoPassword(secretProtector.Protect(input.SudoPassword));
        else if (input.RemoveSudoPassword) server.ClearSudoPassword();
    }

    private string? ProtectPassphrase(string? passphrase) => string.IsNullOrWhiteSpace(passphrase)
        ? null
        : secretProtector.Protect(passphrase);

    private static void EnsureUniqueName(string name, IEnumerable<Server> servers, Guid? currentId)
    {
        if (servers.Any(server => server.Id != currentId
            && string.Equals(server.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A server with this name already exists.", nameof(name));
        }
    }

    private static string SerializeTags(IEnumerable<string> tags) =>
        JsonSerializer.Serialize(tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    private static string NormalizeGroup(string group) => group.Trim().ToLowerInvariant();

    private static IReadOnlyList<ServerSummary> ToSummaries(IEnumerable<Server> servers)
    {
        var list = servers.ToArray();
        return list.Select(server => ToSummary(server, list)).ToArray();
    }

    private static ServerSummary ToSummary(Server server, IEnumerable<Server> allServers) => new(
        server.Id,
        server.Name,
        server.Host,
        server.Port,
        server.User,
        server.KeyFile,
        !string.IsNullOrEmpty(server.ProtectedKeyPassphrase),
        server.JumpServerId,
        allServers.FirstOrDefault(candidate => candidate.Id == server.JumpServerId)?.Name,
        server.Group,
        JsonSerializer.Deserialize<string[]>(server.TagsJson) ?? [],
        server.SudoCapable,
        server.LastSeenAtUtc,
        server.OsInfo,
        server.HasSudoPassword,
        server.SudoPasswordRejectedAtUtc,
        server.SudoPasswordOutcomeUnknownAtUtc,
        SshSourceList.FromJson(server.AllowedSshSourcesJson));
}
