using DevOpsAgent.Application.Chat;
using DevOpsAgent.Domain.Chat;
using DevOpsAgent.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Tests;

/// <summary>
/// T8.10: the per-message tool-call limit (setting, effort, cap), the final summary call without tools, Continue with a fresh
/// budget, and the loop guard. The model is scripted: its Nth answer is a function of N.
/// </summary>
public sealed class ToolCallLimitTests
{
    // The JSON tool protocol of a provider without native tool calling: a message holding {"tool_calls":[...]}.
    private static ChatMessage Calls(params (string Name, string Arguments)[] calls) => new(ChatRole.Assistant,
        "{\"tool_calls\":[" + string.Join(",", calls.Select(call => $"{{\"name\":\"{call.Name}\",\"arguments\":{call.Arguments}}}")) + "]}");

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private static (string, string) Graph(string query) => ("query_graph", $"{{\"query\":\"{query}\"}}");

    // A model that never stops: a different call every time, so only the limit can end the turn.
    private static ChatMessage Endless(int index) => Calls(Graph($"q{index}"));

    private static string[] Kinds(IEnumerable<ChatStreamEvent> events) => events.Select(item => item.Kind).ToArray();

    [Theory]
    [InlineData(15, ChatEffort.Low, 8)]
    [InlineData(15, ChatEffort.Medium, 15)]
    [InlineData(15, ChatEffort.High, 15)] // never above the setting
    [InlineData(50, ChatEffort.Low, 8)]
    [InlineData(50, ChatEffort.Medium, 15)]
    [InlineData(50, ChatEffort.High, 30)]
    [InlineData(5, ChatEffort.Low, 5)]
    [InlineData(5, ChatEffort.High, 5)]
    [InlineData(1, ChatEffort.Medium, 1)]
    public void Effort_RaisesTheLimit_ButNeverAboveTheSetting(int setting, ChatEffort effort, int expected)
    {
        Assert.Equal(expected, ToolCallLimitService.Effective(setting, effort));
    }

    [Fact]
    public async Task Setting_DefaultsTo15_AcceptsOneToFifty_AndRefusesTheRest()
    {
        await using var chat = await ChatServiceHarness.CreateAsync();
        Assert.Equal(15, await chat.Limits.GetAsync(default));

        await chat.Limits.SaveAsync(50, default);
        Assert.Equal(50, await chat.Limits.GetAsync(default));
        await chat.Limits.SaveAsync(1, default);
        Assert.Equal(1, await chat.Limits.GetAsync(default));
        foreach (var invalid in new[] { 0, 51, -3 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => chat.Limits.SaveAsync(invalid, default));
        Assert.Equal(1, await chat.Limits.GetAsync(default)); // a refused value changes nothing

        // A stored value that is not a valid limit reads as the default.
        await chat.Context.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = '999' WHERE Key = 'Chat.ToolCallLimit'");
        Assert.Equal(15, await chat.Limits.GetAsync(default));
    }

    [Fact]
    public async Task ReachingTheLimit_MakesOneLastCallWithoutTools_AndSaysSoAboveTheSummary()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(script: (index, _, _) => index < 3 ? Endless(index) : Text("SUMMARY: found nothing, unknown: everything, next: look at the logs"));
        await chat.Limits.SaveAsync(3, default);
        var conversation = await chat.Service.CreateAsync(default);

        var events = await chat.SendAsync(conversation, "why is the api slow?");

        Assert.Equal(["tool_run", "tool_run", "tool_run", "tool_turn", "tool_limit", "text"], Kinds(events));
        Assert.Equal("Tool limit reached (3/3)", events.Single(item => item.Kind == "tool_limit").Content);
        Assert.Equal("Ran 3 tools", events.Single(item => item.Kind == "tool_turn").Content);
        Assert.StartsWith("SUMMARY", events[^1].Content);
        Assert.Equal(4, chat.Requests.Count); // three rounds with one call each, then the summary

        // Earlier rounds offer the tools, the summary call does not (the JSON protocol lists them in the system message).
        Assert.Contains("Available tools", chat.Requests[2][0].Text);
        var summary = chat.Requests[3];
        Assert.DoesNotContain("Available tools", summary[0].Text);
        Assert.DoesNotContain("tool_calls", summary[0].Text);
        var instruction = summary[^1].Text;
        Assert.Contains("Do not call any tools", instruction);
        Assert.Contains("what you found so far, what is still unknown, and what you would do next", instruction);
        Assert.Contains("query_graph", instruction); // the results so far travel as text
        Assert.Contains("(3/3)", instruction);

        // The banner and the summary are stored, in that order, so the page shows them again after a reload.
        var messages = await chat.Service.GetMessagesAsync(conversation, default);
        var tail = messages.Select(item => item.Role).TakeLast(3).ToArray();
        Assert.Equal(["tool_turn", "tool_limit", "assistant"], tail);
        Assert.Equal("Tool limit reached (3/3)", messages.Single(item => item.Role == "tool_limit").Content);
    }

    [Fact]
    public async Task NativeToolCalling_TheSummaryCallCarriesNoToolDefinitions()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(native: true, script: (index, _, _) => index < 2
            ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call{index}", "query_graph", new Dictionary<string, object?> { ["query"] = $"q{index}" })])
            : Text("SUMMARY"));
        await chat.Limits.SaveAsync(2, default);
        var conversation = await chat.Service.CreateAsync(default);

        var events = await chat.SendAsync(conversation, "what is going on?");

        Assert.Equal(["tool_run", "tool_run", "tool_turn", "tool_limit", "text"], Kinds(events));
        Assert.All(chat.Options.Take(2), options => Assert.NotEmpty(options!.Tools!));
        Assert.True(chat.Options[2]?.Tools is null or { Count: 0 });
    }

    [Fact]
    public async Task ACallBeyondTheBudgetInOneRound_IsNotRun()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(script: (index, _, _) => index == 0 ? Calls(Graph("a"), Graph("b"), Graph("c")) : Text("SUMMARY"));
        await chat.Limits.SaveAsync(2, default);

        var events = await chat.SendAsync(await chat.Service.CreateAsync(default), "look around");

        Assert.Equal(["tool_run", "tool_run", "tool_turn", "tool_limit", "text"], Kinds(events));
        Assert.Equal("Tool limit reached (2/2)", events.Single(item => item.Kind == "tool_limit").Content);
        var transcript = chat.Requests[^1][^1].Text;
        Assert.Contains("{\"query\":\"a\"}", transcript);
        Assert.Contains("{\"query\":\"b\"}", transcript);
        Assert.DoesNotContain("{\"query\":\"c\"}", transcript); // the third call was never run
    }

    [Theory]
    [InlineData(ChatEffort.Low, 50, 8)]
    [InlineData(ChatEffort.Medium, 50, 15)]
    [InlineData(ChatEffort.High, 50, 30)]
    [InlineData(ChatEffort.High, 10, 10)]
    public async Task TheEffortOfTheConversation_SetsTheBudget_NeverAboveTheSetting(ChatEffort effort, int setting, int expected)
    {
        await using var chat = await ChatServiceHarness.CreateAsync(script: (index, messages, options) => index < expected ? Endless(index) : Text("SUMMARY"));
        await chat.Limits.SaveAsync(setting, default);
        var conversation = await chat.Service.CreateAsync(default);
        await chat.Service.SetEffortAsync(conversation, effort, default);

        var events = await chat.SendAsync(conversation, "keep digging");

        Assert.Equal(expected, events.Count(item => item.Kind == "tool_run"));
        Assert.Equal($"Tool limit reached ({expected}/{expected})", events.Single(item => item.Kind == "tool_limit").Content);
        Assert.Equal(expected + 1, chat.Requests.Count);
    }

    [Fact]
    public async Task AnswerWithinTheBudget_HasNoBanner_AndListsItsCalls()
    {
        var servers = new[] { new Server("srv-a", "10.0.0.1", 22, "dva", "key", "staging"), new Server("srv-b", "10.0.0.2", 22, "dva", "key", "staging") };
        await using var chat = await ChatServiceHarness.CreateAsync(servers: servers, script: (index, _, _) => index switch
        {
            0 => Calls(("disk_usage", $"{{\"serverIds\":[\"{servers[0].Id}\",\"{servers[1].Id}\"]}}")),
            1 => Calls(("disk_usage", $"{{\"path\":\"/var/log\",\"serverIds\":[\"{servers[0].Id}\"]}}")),
            _ => Text("All fine.")
        });

        var events = await chat.SendAsync(await chat.Service.CreateAsync(default), "how is disk usage?");

        Assert.Equal(["tool_run", "tool_run", "tool_turn", "text"], Kinds(events));
        Assert.Equal("Ran 2 tools on 2 servers", events.Single(item => item.Kind == "tool_turn").Content);
        Assert.Equal("All fine.", events[^1].Content);
        Assert.Equal(3, chat.Requests.Count); // no extra summary call
    }

    [Fact]
    public async Task Continue_ResumesTheSameTask_WithAFreshBudget()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(script: (index, _, _) => index switch
        {
            < 3 => Endless(index),
            3 => Text("SUMMARY: stopped at the limit"),
            4 => Endless(100), // the continued turn starts over: one call is fine again ...
            5 => Text("FINISHED the task"),
            _ => Text("unexpected")
        });
        await chat.Limits.SaveAsync(3, default);
        var conversation = await chat.Service.CreateAsync(default);
        await chat.SendAsync(conversation, "restart the web container after you checked the graph");

        var events = new List<ChatStreamEvent>();
        await foreach (var item in chat.Service.ContinueAsync(conversation)) events.Add(item);

        Assert.Equal(["tool_run", "tool_turn", "text"], Kinds(events));
        Assert.Equal("FINISHED the task", events[^1].Content);
        Assert.DoesNotContain(events, item => item.Kind == "tool_limit");

        // The model saw its own summary and the Continue request ...
        var resumed = chat.Requests[4];
        Assert.Contains(resumed, message => message.Role == ChatRole.Assistant && message.Text.StartsWith("SUMMARY", StringComparison.Ordinal));
        Assert.Equal(ChatService.ContinuePrompt, resumed.Last(message => message.Role == ChatRole.User && !message.Text.StartsWith("Tool result", StringComparison.Ordinal)).Text);
        // ... and the tools of the original task (a restart), not of the word "Continue".
        Assert.Contains("restart_container", resumed[0].Text);
        Assert.Equal(ChatService.ContinuePrompt, (await chat.Service.GetMessagesAsync(conversation, default)).Last(message => message.Role == "user").Content);
    }

    [Fact]
    public async Task Continue_NeedsATurnThatStoppedAtTheLimit()
    {
        await using var chat = await ChatServiceHarness.CreateAsync();
        var conversation = await chat.Service.CreateAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in chat.Service.ContinueAsync(conversation)) { } });
        await chat.SendAsync(conversation, "hello"); // an ordinary answer is nothing to continue
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in chat.Service.ContinueAsync(conversation)) { } });
    }

    [Fact]
    public async Task TheSameCallThreeTimes_StopsTheTurnEarly_AsALoopNotALimit()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(script: (index, _, _) => index < 4 ? Calls(Graph("same")) : Text("SUMMARY: I kept asking for the same thing"));
        var conversation = await chat.Service.CreateAsync(default);

        var events = await chat.SendAsync(conversation, "check it");

        // Two runs, the third request is the loop and is not run; the budget (15) had plenty left.
        Assert.Equal(["tool_run", "tool_run", "tool_turn", "tool_loop", "text"], Kinds(events));
        Assert.Equal("Stopped early: query_graph was requested 3 times with identical arguments", events.Single(item => item.Kind == "tool_loop").Content);
        Assert.DoesNotContain(events, item => item.Kind == "tool_limit");
        Assert.Equal(4, chat.Requests.Count); // three rounds, then the summary
        var instruction = chat.Requests[^1][^1].Text;
        Assert.Contains("3 times with identical arguments", instruction);
        Assert.Contains("Do not call any tools", instruction);
        Assert.DoesNotContain("Available tools", chat.Requests[^1][0].Text);

        // Not a limit: Continue is not offered for it.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in chat.Service.ContinueAsync(conversation)) { } });
        Assert.Equal("tool_loop", (await chat.Service.GetMessagesAsync(conversation, default)).Single(item => item.Role is "tool_loop" or "tool_limit").Role);
    }

    [Fact]
    public async Task IdenticalArguments_AreRecognisedWhateverTheirOrderAndSpacing_AndInOneRoundToo()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(script: (index, _, _) => index == 0
            ? Calls(("query_graph", "{\"query\":\"a\",\"view\":\"service\"}"), ("query_graph", "{ \"view\": \"service\", \"query\": \"a\" }"),
                ("query_graph", "{\"view\":\"service\",\"query\":\"a\"}"))
            : Text("SUMMARY"));

        var events = await chat.SendAsync(await chat.Service.CreateAsync(default), "check it");

        Assert.Equal(["tool_run", "tool_run", "tool_turn", "tool_loop", "text"], Kinds(events));
    }

    [Fact]
    public async Task TheSameToolWithDifferentArguments_IsNotALoop_AndTwoIdenticalCallsAreAllowed()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(script: (index, _, _) => index switch
        {
            0 => Calls(Graph("a")),
            1 => Calls(Graph("a")), // the second identical call is still fine
            2 => Calls(Graph("b")),
            3 => Calls(Graph("a")), // a third "a" is not consecutive, but it is the third in the turn
            _ => Text("SUMMARY")
        });

        var events = await chat.SendAsync(await chat.Service.CreateAsync(default), "check it");

        Assert.Equal(["tool_run", "tool_run", "tool_run", "tool_turn", "tool_loop", "text"], Kinds(events));
    }
}
