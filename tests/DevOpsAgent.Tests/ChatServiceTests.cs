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

    [Fact]
    public void SelectTools_UsesContextForDailyCheckAndRestrictsReadOnlyAndContainerRestartPrompts()
    {
        var method = typeof(ChatService).GetMethod("SelectTools", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var registry = new ToolRegistry();
        ToolDescriptor[] Select(string prompt) => Assert.IsType<ToolDescriptor[]>(method!.Invoke(null, [prompt, registry]));

        Assert.Empty(Select("anything I should look at?"));
        Assert.DoesNotContain(Select("why is disk usage on srv-app what it is?"), tool => tool.Access == ToolAccess.Mutating);
        Assert.DoesNotContain(Select("why is disk usage on srv-app what it is?"), tool => tool.Name == "run_shell");
        Assert.Contains(Select("restart Docker container web on srv-app"), tool => tool.Name == "restart_container");
        Assert.DoesNotContain(Select("restart Docker container web on srv-app"), tool => tool.Name == "run_shell");
    }

    private sealed record ProbeArguments(string Value);
}
