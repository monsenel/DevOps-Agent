using DevOpsAgent.Application.Servers;

namespace DevOpsAgent.Tests;

public sealed class SshSourceListTests
{
    [Fact]
    public void Parse_AcceptsAddressesAndRanges()
    {
        var result = SshSourceList.Parse("203.0.113.7\n192.168.20.0/24\n2001:db8::/32\n::1");

        Assert.True(result.IsValid);
        Assert.Equal(["203.0.113.7", "192.168.20.0/24", "2001:db8::/32", "::1"], result.Entries);
    }

    [Theory]
    [InlineData("10.0.0.1,10.0.0.2")]
    [InlineData("10.0.0.1;10.0.0.2")]
    [InlineData("10.0.0.1 10.0.0.2")]
    [InlineData("10.0.0.1\t10.0.0.2")]
    [InlineData("10.0.0.1\r\n10.0.0.2\r\n")]
    [InlineData("10.0.0.1 , ; 10.0.0.2")]
    [InlineData("\n\n  10.0.0.1\n\n10.0.0.2  \n")]
    public void Parse_SplitsOnLinesCommasSemicolonsAndWhitespace(string text)
    {
        var result = SshSourceList.Parse(text);

        Assert.True(result.IsValid);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], result.Entries);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        var result = SshSourceList.Parse("# office\n203.0.113.7  # the office VPN\n\n   \n# 10.9.9.9\n192.168.20.0/24");

        Assert.True(result.IsValid);
        Assert.Equal(["203.0.113.7", "192.168.20.0/24"], result.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("# only a comment")]
    public void Parse_EmptyTextIsAValidEmptyList(string? text)
    {
        var result = SshSourceList.Parse(text);

        Assert.True(result.IsValid);
        Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("  2001:DB8::/32  ", "2001:db8::/32")]
    [InlineData("::ffff:10.0.0.5", "10.0.0.5")]
    [InlineData("::FFFF:10.0.0.5", "10.0.0.5")]
    [InlineData("::ffff:a00:5", "10.0.0.5")]
    [InlineData("::ffff:10.0.0.0/104", "10.0.0.0/8")]
    [InlineData("192.168.20.5/24", "192.168.20.0/24")]
    [InlineData("10.1.2.3/32", "10.1.2.3")]
    [InlineData("2001:db8::1/128", "2001:db8::1")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("2001:db8::5/32", "2001:db8::/32")]
    public void Parse_NormalisesEntries(string entry, string expected)
    {
        var result = SshSourceList.Parse(entry);

        Assert.True(result.IsValid);
        Assert.Equal([expected], result.Entries);
    }

    [Fact]
    public void Parse_RemovesDuplicatesKeepingTheFirstPosition()
    {
        var result = SshSourceList.Parse("10.0.0.2\n10.0.0.1\n::ffff:10.0.0.2\n10.0.0.1/32\nFE80::1\nfe80::1");

        Assert.Equal(["10.0.0.2", "10.0.0.1", "fe80::1"], result.Entries);
    }

    [Fact]
    public void Parse_ReportsEveryBadEntryWithItsLineNumber()
    {
        var result = SshSourceList.Parse("10.0.0.1\n\n10.0.0.256\n# fine\n192.168.1.0/33, example.com\n10.0.0.2");

        Assert.False(result.IsValid);
        Assert.Empty(result.Entries);
        Assert.Equal(
        [
            "line 3: \"10.0.0.256\" is not an IP address or CIDR range",
            "line 5: \"192.168.1.0/33\" is not an IP address or CIDR range",
            "line 5: \"example.com\" is not an IP address or CIDR range"
        ], result.Errors);
    }

    [Theory]
    [InlineData("10.0.0")]
    [InlineData("10.1")]
    [InlineData("1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("010.0.0.1")]
    [InlineData("10.0.0.1.5")]
    [InlineData("10.0.0.-1")]
    [InlineData("10.0.0.1/")]
    [InlineData("10.0.0.1/-1")]
    [InlineData("10.0.0.1/ab")]
    [InlineData("10.0.0.1/8/8")]
    [InlineData("/24")]
    [InlineData("2001:db8::/129")]
    [InlineData("2001:db8:::1")]
    [InlineData("12345::1")]
    [InlineData("fe80::1%eth0")]
    [InlineData("[::1]")]
    [InlineData("gggg::1")]
    [InlineData("10.0.0.1:22")]
    [InlineData("*")]
    public void Parse_RejectsWhatIsNotAnAddressOrRange(string entry)
    {
        var result = SshSourceList.Parse(entry);

        Assert.False(result.IsValid);
        Assert.Equal([$"line 1: \"{entry}\" is not an IP address or CIDR range"], result.Errors);
    }

    [Fact]
    public void Parse_RejectsAnEntryLongerThan64Characters()
    {
        var tooLong = "2001:db8::" + new string('0', 60);

        var result = SshSourceList.Parse($"10.0.0.1\n{tooLong}");

        var error = Assert.Single(result.Errors);
        Assert.StartsWith("line 2: \"", error);
        Assert.EndsWith("is longer than 64 characters", error);
        Assert.True(error.Length < 100, "the quoted entry is shortened");
    }

    [Fact]
    public void Parse_AllowsExactlyOneHundredEntries()
    {
        var hundred = string.Join('\n', Enumerable.Range(1, 100).Select(index => $"10.0.0.{index}"));

        var result = SshSourceList.Parse(hundred);

        Assert.True(result.IsValid);
        Assert.Equal(100, result.Entries.Count);
    }

    [Fact]
    public void Parse_RejectsMoreThanOneHundredEntries()
    {
        var many = string.Join('\n', Enumerable.Range(0, 101).Select(index => $"10.0.{index / 250}.{index % 250 + 1}"));

        var result = SshSourceList.Parse(many);

        Assert.False(result.IsValid);
        Assert.Empty(result.Entries);
        Assert.Equal(["Too many entries: at most 100 are allowed (found 101)."], result.Errors);
    }

    [Fact]
    public void Parse_CountsEntriesAfterRemovingDuplicates()
    {
        var text = string.Join('\n', Enumerable.Range(0, 300).Select(index => $"10.0.0.{index % 100 + 1}"));

        Assert.True(SshSourceList.Parse(text).IsValid);
    }

    [Fact]
    public void Parse_RejectsAnAbsurdlyLongText()
    {
        var result = SshSourceList.Parse(new string('#', SshSourceList.MaxTextLength + 1));

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Format_PutsOneEntryPerLineAndRoundTripsThroughParse()
    {
        var entries = new[] { "203.0.113.7", "192.168.20.0/24", "2001:db8::/32" };

        var text = SshSourceList.Format(entries);

        Assert.Equal("203.0.113.7\n192.168.20.0/24\n2001:db8::/32", text);
        Assert.Equal(entries, SshSourceList.Parse(text).Entries);
        Assert.Equal(string.Empty, SshSourceList.Format([]));
    }

    [Fact]
    public void JsonRoundTripsAndToleratesDamagedJson()
    {
        var entries = new[] { "203.0.113.7", "2001:db8::/32" };

        Assert.Equal(entries, SshSourceList.FromJson(SshSourceList.ToJson(entries)));
        Assert.Equal("[]", SshSourceList.ToJson([]));
        Assert.Empty(SshSourceList.FromJson(null));
        Assert.Empty(SshSourceList.FromJson(""));
        Assert.Empty(SshSourceList.FromJson("{not json"));
        Assert.Empty(SshSourceList.FromJson("null"));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7", true)]
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    [InlineData("192.168.20.0/24", "192.168.20.0", true)]
    [InlineData("192.168.20.0/24", "192.168.20.255", true)]
    [InlineData("192.168.20.0/24", "192.168.21.1", false)]
    [InlineData("192.168.20.0/24", "192.168.19.255", false)]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("10.0.0.0/31", "10.0.0.1", true)]
    [InlineData("10.0.0.0/31", "10.0.0.2", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("0.0.0.0/0", "2001:db8::1", false)]
    [InlineData("2001:db8::/32", "2001:db8:ffff::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8::1", "2001:DB8:0:0:0:0:0:1", true)]
    [InlineData("2001:db8::1", "2001:db8::2", false)]
    [InlineData("::/0", "2001:db8::1", true)]
    [InlineData("::/0", "8.8.8.8", false)]
    [InlineData("2001:db8::/32", "10.0.0.1", false)]
    [InlineData("10.0.0.0/8", "2001:db8::1", false)]
    public void Matches_ComparesAddressesAndRanges(string entry, string address, bool expected) =>
        Assert.Equal(expected, SshSourceList.Matches([entry], address));

    [Theory]
    [InlineData("10.0.0.5", "::ffff:10.0.0.5", true)]
    [InlineData("10.0.0.0/24", "::ffff:10.0.0.200", true)]
    [InlineData("10.0.0.0/24", "::ffff:10.0.1.1", false)]
    [InlineData("::ffff:10.0.0.5", "10.0.0.5", true)]
    [InlineData("::ffff:10.0.0.0/120", "10.0.0.77", true)]
    public void Matches_TreatsIPv4MappedIPv6AsIPv4(string entry, string address, bool expected) =>
        Assert.Equal(expected, SshSourceList.Matches([entry], address));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.256")]
    [InlineData("10.1")]
    [InlineData("1")]
    [InlineData("10.0.0.1/24")]
    [InlineData("10.0.0.1:22")]
    [InlineData("2001:db8::1/64")]
    public void Matches_InvalidAddressNeverMatches(string address) =>
        Assert.False(SshSourceList.Matches(["0.0.0.0/0", "::/0", "10.0.0.1"], address));

    [Fact]
    public void Matches_ToleratesBracketsAndScopeIdOnTheAddress()
    {
        Assert.True(SshSourceList.Matches(["fe80::/10"], "fe80::1%eth0"));
        Assert.True(SshSourceList.Matches(["2001:db8::1"], "[2001:db8::1]"));
        Assert.True(SshSourceList.Matches(["10.0.0.5"], " 10.0.0.5 "));
    }

    [Fact]
    public void Matches_EmptyListMatchesNothingAndBadEntriesAreSkipped()
    {
        Assert.False(SshSourceList.Matches([], "10.0.0.1"));
        Assert.False(SshSourceList.Matches(["garbage", "10.0.0.256", ""], "10.0.0.1"));
        Assert.True(SshSourceList.Matches(["garbage", "10.0.0.1"], "10.0.0.1"));
    }

    [Fact]
    public void Matches_FindsAnyOfSeveralEntries()
    {
        string[] allowed = ["203.0.113.7", "192.168.20.0/24", "2001:db8::/32"];

        Assert.True(SshSourceList.Matches(allowed, "192.168.20.42"));
        Assert.True(SshSourceList.Matches(allowed, "2001:db8::42"));
        Assert.True(SshSourceList.Matches(allowed, "203.0.113.7"));
        Assert.False(SshSourceList.Matches(allowed, "203.0.113.8"));
    }
}
