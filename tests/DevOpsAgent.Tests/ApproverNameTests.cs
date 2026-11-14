using DevOpsAgent.Application.Agent;

namespace DevOpsAgent.Tests;

public sealed class ApproverNameTests
{
    [Theory]
    [InlineData(null, "admin")]
    [InlineData("", "admin")]
    [InlineData("   ", "admin")]
    [InlineData("owner", "owner")]
    [InlineData("  owner  ", "owner")]
    public void BlankIdentityNamesFallBackSoApprovalIsNeverBlocked(string? identity, string expected)
    {
        Assert.Equal(expected, ApproverName.Resolve(identity));
        Assert.False(string.IsNullOrWhiteSpace(ApproverName.Resolve(identity)));
    }
}
