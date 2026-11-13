using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Web.Authentication;

public sealed class AdminCredentialVerifier
{
    private readonly string? _user;
    private readonly string? _passwordHash;
    private readonly PasswordHasher<string> _passwordHasher = new();

    public AdminCredentialVerifier(
        IOptions<AdminAuthenticationOptions> options,
        ILogger<AdminCredentialVerifier> logger)
    {
        var value = options.Value;
        _user = value.User;
        _passwordHash = value.PasswordHash;

        if (string.IsNullOrWhiteSpace(_passwordHash) && !string.IsNullOrEmpty(value.Password))
        {
            _passwordHash = _passwordHasher.HashPassword(_user ?? string.Empty, value.Password);
            value.Password = null;
            logger.LogWarning("ADMIN_PASSWORD is configured as plaintext. Use ADMIN_PASSWORD_HASH for deployments.");
        }

        if (string.IsNullOrWhiteSpace(_user) || string.IsNullOrWhiteSpace(_passwordHash))
        {
            _user = null;
            _passwordHash = null;
            logger.LogError("Admin credentials are not configured. Set ADMIN_USER and ADMIN_PASSWORD_HASH or ADMIN_PASSWORD.");
        }
    }

    public bool IsConfigured => _user is not null && _passwordHash is not null;

    public bool Verify(string username, string password)
    {
        if (_user is not { Length: > 0 } configuredUser ||
            _passwordHash is not { Length: > 0 } configuredPasswordHash ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!string.Equals(username, configuredUser, StringComparison.Ordinal))
        {
            return false;
        }

        var result = _passwordHasher.VerifyHashedPassword(configuredUser, configuredPasswordHash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
