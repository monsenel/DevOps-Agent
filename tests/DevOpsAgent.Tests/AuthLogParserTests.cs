using DevOpsAgent.Infrastructure.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class AuthLogParserTests
{
    [Fact]
    public void Parse_FixtureCapturesFailedAndSuccessfulLoginsAndSudoCommand()
    {
        var events = AuthLogParser.Parse(Fixture(), DateTimeOffset.Parse("2026-11-14T12:01:00+00:00"));

        Assert.Equal(4, events.Count);
        Assert.Equal(AuthLogEventKind.FailedLogin, events[0].Kind);
        Assert.Equal("root", events[0].Username);
        Assert.Equal("203.0.113.9", events[0].IpAddress);
        Assert.Equal("2001:db8::9", events[1].IpAddress);
        Assert.Equal(AuthLogEventKind.SuccessfulLogin, events[2].Kind);
        Assert.Equal("operator", events[2].Username);
        Assert.Equal("198.51.100.14", events[2].IpAddress);
        Assert.Equal(AuthLogEventKind.SudoCommand, events[3].Kind);
        Assert.Equal("deploy", events[3].Username);
        Assert.Equal("/usr/bin/systemctl restart api", events[3].Command);
    }

    [Fact]
    public void Parse_IgnoresMalformedAndNonAuthenticationLines()
    {
        var events = AuthLogParser.Parse("bad timestamp sshd: Failed password for root from 192.0.2.1 port 22 ssh2\n"
            + "2026-11-14T12:00:01+00:00 host sshd: Accepted password for deploy from invalid-ip port 22 ssh2",
            DateTimeOffset.Parse("2026-11-14T12:01:00+00:00"));

        Assert.Empty(events);
    }

    [Fact]
    public void Parse_SyslogTimestampUsesCurrentYearAndAcceptsIpv4()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:01:00+00:00");

        var item = Assert.Single(AuthLogParser.Parse(
            "Nov 14 12:00:05 edge sshd[104]: Failed password for root from 192.0.2.12 port 51234 ssh2", now));

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 12, 0, 5, TimeSpan.Zero), item.Timestamp);
        Assert.Equal("192.0.2.12", item.IpAddress);
    }

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "auth-log.txt"));
}
