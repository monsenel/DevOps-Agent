using System.Text.Json;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Tests;

public sealed class SshSessionAssessorTests
{
    private static readonly string[] NoJump = [];

    private static SshSessionVerdict VerdictOf(SshSessionStatus status, string address) =>
        status.Sessions.Single(item => item.Session.SourceAddress == address).Verdict;

    // ---- Assessor -----------------------------------------------------------------------------------------------

    [Fact]
    public void Assess_WithoutSessionsIsLevelNone()
    {
        var status = SshSessionAssessor.Assess([], ["10.0.0.0/24"], NoJump);

        Assert.Equal(SshSessionLevel.None, status.Level);
        Assert.Empty(status.Sessions);
    }

    [Fact]
    public void Assess_EmptyListMeansUncheckedNotice()
    {
        var status = SshSessionAssessor.Assess([new("203.0.113.7", 22, 2, false)], [], NoJump);

        Assert.Equal(SshSessionLevel.Notice, status.Level);
        Assert.Equal(SshSessionVerdict.Unchecked, VerdictOf(status, "203.0.113.7"));
        Assert.Equal("SSH from 1 source", status.Summary);
    }

    [Fact]
    public void Assess_ListedAddressAndRangeAreAllowedAndStillANotice()
    {
        SshSessionInfo[] sessions = [new("203.0.113.7", 22, 1, false), new("192.168.20.9", 22, 3, false)];

        var status = SshSessionAssessor.Assess(sessions, ["203.0.113.7", "192.168.20.0/24"], NoJump);

        Assert.Equal(SshSessionLevel.Notice, status.Level);
        Assert.Equal(SshSessionVerdict.Allowed, VerdictOf(status, "203.0.113.7"));
        Assert.Equal(SshSessionVerdict.Allowed, VerdictOf(status, "192.168.20.9"));
        Assert.Equal("SSH from 2 sources", status.Summary);
    }

    [Fact]
    public void Assess_AnAddressOutsideTheListIsAWarningAndComesFirst()
    {
        SshSessionInfo[] sessions = [new("10.0.0.5", 22, 1, false), new("198.51.100.4", 22, 1, false)];

        var status = SshSessionAssessor.Assess(sessions, ["10.0.0.0/24"], NoJump);

        Assert.Equal(SshSessionLevel.Warning, status.Level);
        Assert.Equal(SshSessionVerdict.NotAllowed, status.Sessions[0].Verdict);
        Assert.Equal("198.51.100.4", status.Sessions[0].Session.SourceAddress);
        Assert.Equal("SSH from a source that is not allowed", status.Summary);
    }

    [Fact]
    public void Assess_TheJumpHostIsAllowedWithoutAListEntry()
    {
        var sessions = new SshSessionInfo[] { new("10.0.0.2", 22, 4, false) };

        foreach (var list in new string[][] { [], ["203.0.113.7"] })
        {
            var status = SshSessionAssessor.Assess(sessions, list, ["10.0.0.2"]);

            Assert.Equal(SshSessionLevel.None, status.Level);
            Assert.Equal(SshSessionVerdict.AllowedImplicitly, VerdictOf(status, "10.0.0.2"));
        }
    }

    [Fact]
    public void Assess_TheJumpHostIsAnExactAddressNotARange()
    {
        var status = SshSessionAssessor.Assess([new("10.0.0.3", 22, 1, false)], ["203.0.113.7"], ["10.0.0.2"]);

        Assert.Equal(SshSessionVerdict.NotAllowed, VerdictOf(status, "10.0.0.3"));
    }

    [Theory]
    [InlineData("::ffff:10.0.0.2", "10.0.0.2")]
    [InlineData("10.0.0.2", "::ffff:10.0.0.2")]
    [InlineData(" 10.0.0.2 ", "10.0.0.2")]
    [InlineData("2001:DB8:0:0::5", "[2001:db8::5]")]
    [InlineData("fe80::1%eth0", "fe80::1")]
    public void Assess_JumpHostComparisonIsOnNormalisedAddresses(string session, string jump)
    {
        var status = SshSessionAssessor.Assess([new(session, 22, 1, false)], ["203.0.113.7"], [jump]);

        Assert.Equal(SshSessionVerdict.AllowedImplicitly, status.Sessions.Single().Verdict);
    }

    [Fact]
    public void Assess_BlankJumpHostAddressesAreIgnored()
    {
        var status = SshSessionAssessor.Assess([new("10.0.0.3", 22, 1, false)], [], ["", "  "]);

        Assert.Equal(SshSessionVerdict.Unchecked, VerdictOf(status, "10.0.0.3"));
    }

    [Fact]
    public void Assess_TheAgentSessionNeverRaisesTheLevel()
    {
        SshSessionInfo[] sessions = [new("10.0.0.9", 22, 2, true)];

        foreach (var list in new string[][] { [], ["203.0.113.7"] })
        {
            var status = SshSessionAssessor.Assess(sessions, list, NoJump);

            Assert.Equal(SshSessionLevel.None, status.Level);
            Assert.Equal(SshSessionVerdict.Agent, VerdictOf(status, "10.0.0.9"));
        }
    }

    // ---- Attribute reader ---------------------------------------------------------------------------------------

    private static JsonElement Attributes(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Read_PascalCaseAttributes()
    {
        var sessions = SshSessionAttributes.Read(Attributes(
            """{"Host":"10.0.0.1","SshSessions":[{"Address":"203.0.113.7","Port":22,"Count":3,"IsAgent":false},{"Address":"10.0.0.9","Port":2222,"Count":1,"IsAgent":true}]}"""));

        Assert.Equal([new SshSessionInfo("203.0.113.7", 22, 3, false), new SshSessionInfo("10.0.0.9", 2222, 1, true)], sessions);
    }

    [Fact]
    public void Read_CamelCaseAttributes()
    {
        var sessions = SshSessionAttributes.Read(Attributes(
            """{"sshSessions":[{"address":"203.0.113.7","port":22,"count":2,"isAgent":true}]}"""));

        Assert.Equal([new SshSessionInfo("203.0.113.7", 22, 2, true)], sessions);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"SshSessions":null}""")]
    [InlineData("""{"SshSessions":[]}""")]
    [InlineData("""{"SshSessions":"10.0.0.1"}""")]
    [InlineData("""{"SshSessions":{"Address":"10.0.0.1"}}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void Read_MissingOrWronglyShapedDataIsEmpty(string json)
    {
        Assert.Empty(SshSessionAttributes.Read(Attributes(json)));
    }

    [Fact]
    public void Read_UndefinedElementIsEmpty()
    {
        Assert.Empty(SshSessionAttributes.Read(default));
    }

    [Fact]
    public void Read_SkipsMalformedItemsAndKeepsTheGoodOnes()
    {
        var sessions = SshSessionAttributes.Read(Attributes(
            """{"SshSessions":[1,null,"x",{"Port":22},{"Address":5},{"Address":"  "},{"Address":"203.0.113.7","Port":"22","Count":"many","IsAgent":"yes"},{"Address":"10.0.0.9","Port":22,"Count":0,"IsAgent":false}]}"""));

        Assert.Equal([new SshSessionInfo("203.0.113.7", 0, 1, false), new SshSessionInfo("10.0.0.9", 22, 1, false)], sessions);
    }
}
