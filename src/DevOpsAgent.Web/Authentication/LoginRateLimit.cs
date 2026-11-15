namespace DevOpsAgent.Web.Authentication;

/// <summary>Brute-force guard for the single admin login: a few attempts per client address per window.</summary>
public static class LoginRateLimit
{
    public const string PolicyName = "login";
    public const int AttemptsPerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
}
