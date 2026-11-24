using DevOpsAgent.Application.Security;
using DevOpsAgent.Infrastructure.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class SudoersProfileGeneratorTests
{
    [Fact]
    public void DefaultProfileGeneratesReadOnlyPinnableSudoersWithoutShOrAll()
    {
        var result = SudoersProfileGenerator.Generate(new SudoersProfileOptions("devops"));

        Assert.Equal("devops", result.SshUser);
        Assert.Contains("Defaults:devops !requiretty, use_pty", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias DVA_DISCOVERY", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias DVA_MONITORING", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias DVA_SECURITY", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/ss -tulpnH", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("/usr/sbin/nginx -T", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("devops ALL=(root) NOPASSWD: DVA_DISCOVERY, DVA_MONITORING, DVA_SECURITY", result.SudoersContent, StringComparison.Ordinal);
        Assert.DoesNotContain("NOPASSWD: ALL", result.SudoersContent, StringComparison.Ordinal);
        Assert.DoesNotContain("sh -c", result.SudoersContent, StringComparison.Ordinal);
        Assert.All(result.Commands, cmd => Assert.True(cmd.IsReadOnly));
    }

    [Fact]
    public void ExtendedProfileIncludesApprovedPrivilegedAliasesAndTypedHelperScript()
    {
        var result = SudoersProfileGenerator.Generate(new SudoersProfileOptions(
            "dva_ops",
            IncludeDiscovery: true,
            IncludeMonitoring: true,
            IncludeSecurityAudit: true,
            IncludeUpdates: true,
            IncludeServiceControl: true,
            IncludeFirewall: true,
            IncludeConfigEdit: true));

        Assert.Contains("Cmnd_Alias DVA_UPDATES", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias DVA_SERVICES", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias DVA_FIREWALL", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias DVA_CONFIG_EDIT", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("/usr/local/sbin/dva-sudo-helper apply-config *", result.SudoersContent, StringComparison.Ordinal);
        Assert.Contains("Path traversal rejected", result.HelperScriptTemplate, StringComparison.Ordinal);
        Assert.Contains(result.Commands, cmd => !cmd.IsReadOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("root ALL=(ALL) NOPASSWD: ALL")]
    [InlineData("BadUser")]
    [InlineData("user;id")]
    public void RejectsUnsafeSshUsernames(string invalidUser)
    {
        Assert.Throws<ArgumentException>(() => SudoersProfileGenerator.Generate(new SudoersProfileOptions(invalidUser)));
    }

    [Theory]
    [InlineData("/tmp/evil")]
    [InlineData("/usr/local/sbin/helper; id")]
    [InlineData("/usr/local/sbin/../../bin/sh")]
    public void RejectsUnsafeHelperScriptPaths(string invalidPath)
    {
        Assert.Throws<ArgumentException>(() => SudoersProfileGenerator.Generate(
            new SudoersProfileOptions("devops", IncludeConfigEdit: true, HelperScriptPath: invalidPath)));
    }

    [Fact]
    public void ExistingAppSudoCommandsRemainsCompatible()
    {
        Assert.NotEmpty(AppSudoCommands.Templates);
        Assert.False(AppSudoCommands.IsAppCommand("/usr/bin/sh -c 'cat /etc/shadow'"));
    }
}
