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

    [Theory]
    [InlineData("DB_PASSWORD=s3cr3tvalue", "s3cr3tvalue")]
    [InlineData("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG", "wJalrXUtnFEMI/K7MDENG")]
    [InlineData("api_key: sk-live-abcdef123456", "sk-live-abcdef123456")]
    [InlineData("{\"password\": \"hunter2 with spaces\", \"user\": \"deploy\"}", "hunter2 with spaces")]
    [InlineData("{'client_secret':'abc-def-ghi'}", "abc-def-ghi")]
    [InlineData("export GITHUB_TOKEN=ghp_abcdefghijklmnop", "ghp_abcdefghijklmnop")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.signature", "eyJhbGciOi.payload.signature")]
    [InlineData("curl -H 'Authorization: Bearer abcdef1234567890' https://api.example", "abcdef1234567890")]
    [InlineData("X-Api-Token: tok_live_123456", "tok_live_123456")]
    [InlineData("{\"clientSecret\": \"it's a \\\"quoted\\\" secret\"}", "quoted")]
    [InlineData("{\"apiKey\": 123456789}", "123456789")]
    [InlineData("password: \"unterminated-secret", "unterminated-secret")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----", "b3BlbnNzaC1rZXk")]
    public void RedactsBroadSecretShapes(string input, string secret)
    {
        var redacted = ChatContentRedactor.Redact(input);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionKeepsKeyNamesSchemesAndUnrelatedText()
    {
        var redacted = ChatContentRedactor.Redact("Authorization: Bearer abcdef1234567890\nDB_PASSWORD=s3cr3t host=db1 port=5432 \"user\": \"deploy\"");

        Assert.Contains("Authorization: Bearer [REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("DB_PASSWORD=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("host=db1 port=5432 \"user\": \"deploy\"", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("max_tokens=4096 tokens_used: 1200 PWD=/home/deploy")]
    [InlineData("Basic monitoring is enabled; the Bearer of bad news arrived")]
    [InlineData("The token count is high and the password policy is documented")]
    [InlineData("keyboard=us monkey=banana author=ops")]
    public void LeavesNamesAndProseThatOnlyContainSecretWordsAlone(string input) =>
        Assert.Equal(input, ChatContentRedactor.Redact(input));

    [Fact]
    public void MaskedJsonStaysValidJson()
    {
        var redacted = ChatContentRedactor.Redact("{\"password\": \"hunter2\", \"api_key\": 42, \"user\": \"deploy\", \"max_tokens\": 10}");

        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("password").GetString());
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("api_key").GetString());
        Assert.Equal("deploy", document.RootElement.GetProperty("user").GetString());
        Assert.Equal(10, document.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void HugeAdversarialInputFinishesQuicklyAndNeverLeaksUnscannedText()
    {
        var hostile = string.Concat(Enumerable.Repeat("a=\"", 200_000)) + "password=hunter2";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var redacted = ChatContentRedactor.Redact(hostile);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }
}
