using DevOpsAgent.Application.Servers;

namespace DevOpsAgent.Tests;

public sealed class ServerGroupOptionsTests
{
    [Fact]
    public void ExistingServersInOtherGroupsKeepTheirGroupSelectableOnEdit()
    {
        var options = ServerGroupOptions.Build(["prod", "dmz", "DMZ", "lab"], "dmz");

        Assert.Equal(["prod", "staging", "dmz", "lab"], options.Select(option => option.Value));
        Assert.Equal("Production", options[0].Label);
        Assert.Equal("dmz", options[2].Label);
    }

    [Fact]
    public void CurrentGroupIsAddedEvenWhenNoOtherServerUsesIt()
    {
        var options = ServerGroupOptions.Build([], "edge");

        Assert.Equal(["prod", "staging", "edge"], options.Select(option => option.Value));
        Assert.Equal(["prod", "staging"], ServerGroupOptions.Build([], "").Select(option => option.Value));
        Assert.Equal(["prod", "staging"], ServerGroupOptions.Build([], null).Select(option => option.Value));
    }

    [Fact]
    public void LegacyMixedCaseGroupsMatchTheLowerCaseOptionTheDomainUses()
    {
        var current = ServerGroupOptions.Normalize("  PROD ");
        var options = ServerGroupOptions.Build(["PROD", "Staging"], current);

        Assert.Equal("prod", current);
        Assert.Contains(options, option => option.Value == current);
        Assert.Equal(["prod", "staging"], options.Select(option => option.Value));
        Assert.Equal("dmz", ServerGroupOptions.Build([], "DMZ").Last().Value);
        Assert.Equal(string.Empty, ServerGroupOptions.Normalize(null));
    }
}
