using System.Text.Json;
using DevOpsAgent.Application.Chat;

namespace DevOpsAgent.Tests;

public sealed class JsonToolProtocolTests
{
    [Fact]
    public void ParsesToolCallsFromFencedJsonAndClonesArguments()
    {
        var parsed = JsonToolProtocol.ParseCalls("```json\n{\"tool_calls\":[{\"name\":\"service_status\",\"arguments\":{\"unit\":\"nginx\"}}]}\n```");

        var call = Assert.Single(parsed);
        Assert.Equal("service_status", call.Name);
        Assert.Equal("nginx", call.Args.GetProperty("unit").GetString());
    }

    [Fact]
    public void ParsesToolCallsFromStreamedTextWhenAssistantTextIsEmpty()
    {
        var parsed = JsonToolProtocol.ParseCalls(string.Empty,
            "{\"tool_calls\":[{\"name\":\"list_servers\",\"arguments\":{}}]}");

        var call = Assert.Single(parsed);
        Assert.Equal("list_servers", call.Name);
        Assert.Equal(JsonValueKind.Object, call.Args.ValueKind);
    }

    [Theory]
    [InlineData("plain response")]
    [InlineData("{\"tool_calls\":[{\"name\":\"bad\"}]}")]
    [InlineData("{broken json}")]
    public void IgnoresTextAndMalformedToolCalls(string response) => Assert.Empty(JsonToolProtocol.ParseCalls(response));

    [Fact]
    public void RedactsCredentialsAndPrivateKeysWithoutChangingOrdinaryText()
    {
        const string privateKey = "-----BEGIN PRIVATE KEY-----\nkey-data\n-----END PRIVATE KEY-----";
        var redacted = ChatContentRedactor.Redact($"token=abc123 password: hunter2\n{privateKey} service nginx");

        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("key-data", redacted, StringComparison.Ordinal);
        Assert.Contains("service nginx", redacted, StringComparison.Ordinal);
    }
}
