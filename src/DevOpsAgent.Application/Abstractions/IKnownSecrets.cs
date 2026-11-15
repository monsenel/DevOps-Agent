namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// The secrets the app holds that may turn up in server output (T7.R26): every stored sudo password of every server. Output of
/// any server, alert text and everything sent to an LLM is redacted with all of them, not only with the password of the server
/// it came from (the same account can be registered twice, a password can be typed as a user name, a log can be read from
/// another host).
/// </summary>
public interface IKnownSecrets
{
    /// <summary>Every stored sudo password, decrypted, longest first. Cached; refreshed after <see cref="Invalidate"/>.</summary>
    Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken);

    /// <summary>A server's stored password changed (created, edited, removed).</summary>
    void Invalidate();
}

public static class KnownSecretRedaction
{
    public const string Redacted = "[REDACTED]";

    /// <summary>Replaces every occurrence of every secret (longest first, so one that contains another is removed whole).</summary>
    public static string Redact(string text, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in secrets)
            if (secret.Length > 0) text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        return text;
    }

    public static RemoteCommandResult Redact(RemoteCommandResult result, IReadOnlyList<string> secrets) =>
        secrets.Count == 0
            ? result
            : result with { StandardOutput = Redact(result.StandardOutput, secrets), StandardError = Redact(result.StandardError, secrets) };
}

/// <summary>Raised when a server's stored state changes in the background (T7.R26: the Servers page updates its sudo column live).</summary>
public sealed class ServerStateEvents
{
    public event Action<Guid>? Changed;

    public void NotifyChanged(Guid serverId) => Changed?.Invoke(serverId);
}
