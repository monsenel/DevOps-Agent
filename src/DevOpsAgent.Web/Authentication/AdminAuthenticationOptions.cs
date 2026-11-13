namespace DevOpsAgent.Web.Authentication;

public sealed class AdminAuthenticationOptions
{
    public const string UserConfigurationKey = "ADMIN_USER";
    public const string PasswordHashConfigurationKey = "ADMIN_PASSWORD_HASH";
    public const string PasswordConfigurationKey = "ADMIN_PASSWORD";

    public string? User { get; set; }

    public string? PasswordHash { get; set; }

    public string? Password { get; set; }
}
