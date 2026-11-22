namespace DevOpsAgent.Application.Agent;

/// <summary>
/// Guardrail for <c>restart_service</c> (T7.R4): refuses restarting critical access or container-runtime units
/// (<c>ssh</c>, <c>sshd</c>, <c>docker</c>, <c>containerd</c>) unless <see cref="RestartServiceArgs.ConfirmCritical"/>
/// is explicitly set after owner confirmation.
/// </summary>
public static class CriticalServiceGuard
{
    public static bool IsProtected(string? unit) => NormalizeBase(unit) is "ssh" or "sshd" or "docker" or "containerd";

    public static bool IsSshUnit(string? unit) => NormalizeBase(unit) is "ssh" or "sshd";

    public static bool IsContainerRuntimeUnit(string? unit) => NormalizeBase(unit) is "docker" or "containerd";

    public static string? GetRefusal(RestartServiceArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.ConfirmCritical || !IsProtected(args.Unit))
        {
            return null;
        }

        return IsSshUnit(args.Unit)
            ? $"Restarting critical SSH service '{args.Unit}' can sever remote access to the server. Ask the owner to confirm explicitly and re-propose with confirmCritical: true."
            : $"Restarting critical container runtime '{args.Unit}' can stop running containers on the server. Ask the owner to confirm explicitly and re-propose with confirmCritical: true.";
    }

    internal static string NormalizeBase(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return string.Empty;
        }

        var value = unit.Trim();
        if (value.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^".service".Length];
        }
        else if (value.EndsWith(".socket", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^".socket".Length];
        }

        var at = value.IndexOf('@');
        if (at >= 0)
        {
            value = value[..at];
        }

        return value.ToLowerInvariant();
    }
}
