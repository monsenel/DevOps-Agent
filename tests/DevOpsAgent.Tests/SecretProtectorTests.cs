using DevOpsAgent.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DevOpsAgent.Tests;

public sealed class SecretProtectorTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsPlaintext()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"devops-agent-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keysDirectory));
            var secretProtector = new DataProtectionSecretProtector(provider);
            const string plaintext = "test-secret-value";

            var protectedValue = secretProtector.Protect(plaintext);

            Assert.NotEqual(plaintext, protectedValue);
            Assert.Equal(plaintext, secretProtector.Unprotect(protectedValue));
        }
        finally
        {
            Directory.Delete(keysDirectory, recursive: true);
        }
    }
}
