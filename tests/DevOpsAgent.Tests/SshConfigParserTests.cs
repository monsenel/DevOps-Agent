using DevOpsAgent.Application.Servers;

namespace DevOpsAgent.Tests;

public sealed class SshConfigParserTests
{
    [Fact]
    public void Parse_ImportsExactAliasesAndSupportedDirectivesFromFixture()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ssh-config", "basic.conf"));

        var result = SshConfigParser.Parse(text);

        Assert.Equal(5, result.Entries.Count);
        Assert.Equal(2, result.SkippedWildcardHostCount);
        var edgeWeb = result.Entries[0];
        Assert.Equal("edge-web", edgeWeb.Alias);
        Assert.Equal("10.20.0.15", edgeWeb.HostName);
        Assert.Equal(2222, edgeWeb.Port);
        Assert.Equal("deploy", edgeWeb.User);
        Assert.Equal("~/.ssh/devops_ed25519", edgeWeb.IdentityFile);
        Assert.Equal("gateway", edgeWeb.ProxyJump);

        var gateway = result.Entries[2];
        Assert.Equal("bastion.example.test", gateway.HostName);
        Assert.Equal("/home/ops/keys/gateway key", gateway.IdentityFile);
        Assert.Equal("ops", gateway.User);

        Assert.Equal(string.Empty, result.Entries[3].User);

        var exactServer = result.Entries[4];
        Assert.Equal("exact-server", exactServer.Alias);
        Assert.Equal("exact.example.test", exactServer.HostName);
        Assert.Equal("service", exactServer.User);
    }

    [Fact]
    public void Parse_RejectsInvalidPort()
    {
        var exception = Assert.Throws<FormatException>(() => SshConfigParser.Parse("Host broken\n Port 70000\n"));

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
    }
}
