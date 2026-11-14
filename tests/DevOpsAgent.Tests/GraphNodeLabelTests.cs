using System.Text.Json;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Tests;

public sealed class GraphNodeLabelTests
{
    [Fact]
    public void UnknownNodesAreDistinguishedByAddressAndPort()
    {
        var first = Describe("{\"kind\":\"Unknown\",\"label\":\"Unknown\",\"attributes\":{\"Address\":\"10.0.0.5\",\"Port\":5432}}");
        var second = Describe("{\"kind\":\"Unknown\",\"label\":\"Unknown\",\"attributes\":{\"Address\":\"10.0.0.6\",\"Port\":6379}}");
        var noPort = Describe("{\"kind\":\"Unknown\",\"label\":\"Unknown\",\"attributes\":{\"Address\":\"db.internal\"}}");

        Assert.Equal("Unknown 10.0.0.5:5432", first);
        Assert.Equal("Unknown 10.0.0.6:6379", second);
        Assert.NotEqual(first, second);
        Assert.Equal("Unknown db.internal", noPort);
    }

    [Fact]
    public void UnknownNodesFallBackToTheNodeKeyAndOtherKindsKeepTheirLabel()
    {
        Assert.Equal("Unknown unknown:abc:10.9.9.9:80",
            Describe("{\"kind\":\"Unknown\",\"label\":\"Unknown\",\"key\":\"unknown:abc:10.9.9.9:80\",\"attributes\":{}}"));
        Assert.Equal("Unknown", Describe("{\"kind\":\"Unknown\",\"label\":\"Unknown\"}"));
        Assert.Equal("edge-a", Describe("{\"kind\":\"Host\",\"label\":\"edge-a\",\"attributes\":{\"Address\":\"1.2.3.4\"}}"));
    }

    private static string Describe(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GraphNodeLabel.Describe(document.RootElement);
    }
}
