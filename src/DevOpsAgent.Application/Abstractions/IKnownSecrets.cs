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

    /// <summary>
    /// Replaces every occurrence of every secret in <paramref name="text"/>. Matches are collected on the original text and
    /// overlapping intervals are merged before substitution, so overlapping secrets (e.g. <c>abcdefgh</c> and <c>fghijk</c>
    /// in <c>abcdefghijk</c>) never leave a fragment behind (T7.R27).
    /// </summary>
    public static string Redact(string text, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets.Count == 0) return text;
        List<(int Start, int End)>? spans = null;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret)) continue;
            var index = 0;
            while ((index = text.IndexOf(secret, index, StringComparison.Ordinal)) >= 0)
            {
                spans ??= [];
                spans.Add((index, index + secret.Length));
                // Advance by 1 so self-overlapping occurrences are also captured before merging.
                index++;
            }
        }

        if (spans is null) return text;
        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var builder = new System.Text.StringBuilder(text.Length);
        var cursor = 0;
        var mergedStart = spans[0].Start;
        var mergedEnd = spans[0].End;
        for (var i = 1; i < spans.Count; i++)
        {
            var next = spans[i];
            if (next.Start < mergedEnd)
            {
                if (next.End > mergedEnd) mergedEnd = next.End;
            }
            else
            {
                builder.Append(text, cursor, mergedStart - cursor);
                builder.Append(Redacted);
                cursor = mergedEnd;
                mergedStart = next.Start;
                mergedEnd = next.End;
            }
        }

        builder.Append(text, cursor, mergedStart - cursor);
        builder.Append(Redacted);
        if (mergedEnd < text.Length) builder.Append(text, mergedEnd, text.Length - mergedEnd);
        return builder.ToString();
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
