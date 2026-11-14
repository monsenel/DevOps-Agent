using DevOpsAgent.Infrastructure.Parsers;

namespace DevOpsAgent.Tests;

public sealed class UpdatesParserTests
{
    [Fact]
    public void ParsesAptVersionsAndSeparatesSecurityOrigins()
    {
        var output = Fixture("apt-upgradable.txt");
        var parsed = UpdatesParser.ParseApt(output);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("1.24.0-2ubuntu7.2", parsed.Single(item => item.Name == "nginx").CurrentVersion);
        Assert.False(parsed.Single(item => item.Name == "nginx").IsSecurity);
        Assert.True(parsed.Single(item => item.Name == "openssl").IsSecurity);
        Assert.Equal("jammy-security", parsed.Single(item => item.Name == "openssl").Repository);
    }

    [Fact]
    public void ParsesDnfUpdatesAndMarksSecurityPackages()
    {
        var packages = UpdatesParser.ParseDnf(Fixture("dnf-check-update.txt"), Fixture("dnf-security.txt"));

        Assert.Equal(2, packages.Count);
        Assert.False(packages.Single(item => item.Name.StartsWith("nginx", StringComparison.Ordinal)).IsSecurity);
        Assert.True(packages.Single(item => item.Name.StartsWith("openssl-libs", StringComparison.Ordinal)).IsSecurity);
        Assert.Equal("appstream", packages.Single(item => item.Name.StartsWith("nginx", StringComparison.Ordinal)).Repository);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
