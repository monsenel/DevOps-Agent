using DevOpsAgent.Application.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class MetricsParserTests
{
    [Fact]
    public void Parse_FirstSampleReadsLoadMemoryDiskAndCounters()
    {
        var result = MetricsParser.Parse(Fixture("metrics-sample-1.txt"));

        Assert.Equal(0.45, result.Values["load_1m"]);
        Assert.Equal(0.38, result.Values["load_5m"]);
        Assert.Equal(0.29, result.Values["load_15m"]);
        Assert.Equal(37.5, result.Values["memory_percent"]);
        Assert.Equal(50, result.Values["disk_percent"]);
        Assert.Equal(4, result.Values["cpu_cores"]);
        Assert.Equal(100, result.CpuCounters["user"]);
        Assert.Equal(800, result.CpuCounters["idle"]);
        Assert.Equal(100000, result.NetworkCounters["eth0:rx"]);
    }

    [Fact]
    public void Parse_SecondSampleCalculatesCpuAndNetworkRates()
    {
        var previous = MetricsParser.Parse(Fixture("metrics-sample-1.txt"));
        var result = MetricsParser.Parse(Fixture("metrics-sample-2.txt"), previous, TimeSpan.FromSeconds(60));

        Assert.Equal(1.1, result.Values["load_1m"]);
        Assert.Equal(50, result.Values["memory_percent"]);
        Assert.Equal(60, result.Values["disk_percent"]);
        Assert.Equal(1000, result.Values["network_receive_bytes_per_second"]);
        Assert.Equal(500, result.Values["network_transmit_bytes_per_second"]);
        Assert.Equal(70, result.Values["cpu_percent"]);
    }

    [Fact]
    public void CollectionCommandUsesOneReadOnlyShellInvocation()
    {
        var command = MetricsParser.CollectionCommand;

        Assert.Contains("cat /proc/loadavg", command);
        Assert.Contains("cat /proc/meminfo", command);
        Assert.Contains("head -1 /proc/stat", command);
        Assert.Contains("df -P", command);
        Assert.Contains("cat /proc/net/dev", command);
        Assert.Contains("getconf _NPROCESSORS_ONLN", command);
        Assert.Equal(6, command.Split("printf '").Length - 1);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
