using DevOpsAgent.Infrastructure.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class RemoteServiceStatusParserTests
{
    [Fact]
    public void ParseSeparatesContainerStatesAndFailedSystemdUnits()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "remote-service-status.txt"));

        var result = RemoteServiceStatusParser.Parse(output);

        Assert.Collection(result.Containers,
            item => { Assert.Equal("api", item.Name); Assert.Equal("running", item.State); },
            item => { Assert.Equal("worker", item.Name); Assert.Equal("restarting", item.State); },
            item => { Assert.Equal("old-job", item.Name); Assert.Equal("exited", item.State); });
        Assert.Equal(["payments.service"], result.FailedUnits);
    }
}
