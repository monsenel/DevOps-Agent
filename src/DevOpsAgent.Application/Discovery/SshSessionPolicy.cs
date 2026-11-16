namespace DevOpsAgent.Application.Discovery;

public enum SshSessionLevel
{
    None,
    Notice,
    Warning
}

public enum SshSessionVerdict
{
    /// <summary>The session of DevOps Agent itself.</summary>
    Agent,

    /// <summary>Always allowed without a list entry (for example the server's jump host).</summary>
    AllowedImplicitly,

    Allowed,
    NotAllowed,

    /// <summary>No allowed-sources list is configured, so the source cannot be judged.</summary>
    Unchecked
}

public sealed record SshSessionEvaluation(SshSessionInfo Session, SshSessionVerdict Verdict);

public sealed record SshSessionStatus(SshSessionLevel Level, IReadOnlyList<SshSessionEvaluation> Sessions, string Summary);

/// <summary>Pure evaluation of the SSH sessions of one server. The address matching is supplied by the caller.</summary>
public static class SshSessionPolicy
{
    public static SshSessionStatus Evaluate(
        IReadOnlyList<SshSessionInfo> sessions,
        bool allowListConfigured,
        Func<string, bool> isAllowed,
        Func<string, bool> isImplicitlyAllowed)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(isAllowed);
        ArgumentNullException.ThrowIfNull(isImplicitlyAllowed);

        var evaluations = sessions
            .Select(session => new SshSessionEvaluation(session,
                GetVerdict(session, allowListConfigured, isAllowed, isImplicitlyAllowed)))
            .OrderBy(item => item.Verdict == SshSessionVerdict.NotAllowed ? 0 : 1)
            .ThenBy(item => item.Session.SourceAddress, StringComparer.Ordinal)
            .ThenBy(item => item.Session.ServerPort)
            .ToArray();

        var relevant = evaluations.Where(item => item.Verdict is not (SshSessionVerdict.Agent or SshSessionVerdict.AllowedImplicitly))
            .ToArray();
        if (relevant.Length == 0)
        {
            return new SshSessionStatus(SshSessionLevel.None, evaluations, string.Empty);
        }

        var notAllowedSources = relevant.Where(item => item.Verdict == SshSessionVerdict.NotAllowed)
            .Select(item => item.Session.SourceAddress).Distinct(StringComparer.Ordinal).Count();
        if (notAllowedSources > 0)
        {
            return new SshSessionStatus(SshSessionLevel.Warning, evaluations, notAllowedSources == 1
                ? "SSH from a source that is not allowed"
                : $"SSH from {notAllowedSources} sources that are not allowed");
        }

        var sources = relevant.Select(item => item.Session.SourceAddress).Distinct(StringComparer.Ordinal).Count();
        return new SshSessionStatus(SshSessionLevel.Notice, evaluations,
            sources == 1 ? "SSH from 1 source" : $"SSH from {sources} sources");
    }

    private static SshSessionVerdict GetVerdict(SshSessionInfo session, bool allowListConfigured,
        Func<string, bool> isAllowed, Func<string, bool> isImplicitlyAllowed)
    {
        if (session.IsAgent) return SshSessionVerdict.Agent;
        if (isImplicitlyAllowed(session.SourceAddress)) return SshSessionVerdict.AllowedImplicitly;
        if (!allowListConfigured) return SshSessionVerdict.Unchecked;
        return isAllowed(session.SourceAddress) ? SshSessionVerdict.Allowed : SshSessionVerdict.NotAllowed;
    }
}
