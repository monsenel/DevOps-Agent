using System.Reflection;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Chat;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Tests;

public sealed class ChatServiceTests
{
    [Fact]
    public void CreateDeclaration_KeepsSchemaAliveAfterSourceDocumentIsDisposed()
    {
        var method = typeof(ChatService).GetMethod("CreateDeclaration", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var descriptor = new ToolDescriptor("probe", "Probe schema lifetime.", ToolAccess.ReadOnly, typeof(ProbeArguments), false);
        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(method!.Invoke(null, [descriptor]));

        Assert.Equal("object", declaration.JsonSchema.GetProperty("type").GetString());
        Assert.Equal("string", declaration.JsonSchema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    private sealed record ProbeArguments(string Value);
}
