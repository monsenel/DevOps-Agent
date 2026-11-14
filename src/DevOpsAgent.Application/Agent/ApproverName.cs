namespace DevOpsAgent.Application.Agent;

public static class ApproverName
{
    public const string Fallback = "admin";

    /// <summary>The authenticated user name, or the single-user fallback when it is missing or blank (approval requires a name).</summary>
    public static string Resolve(string? identityName) =>
        string.IsNullOrWhiteSpace(identityName) ? Fallback : identityName.Trim();
}
