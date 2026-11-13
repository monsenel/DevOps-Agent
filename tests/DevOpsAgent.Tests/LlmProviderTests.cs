using System.Net;
using System.Text;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Llm;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Infrastructure.Llm;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Tests;

public sealed class LlmProviderTests
{
    [Fact]
    public async Task ProviderService_ProtectsKeyAndAllowsOnlyOneDefault()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LlmProviderRepository(database.Context);
        var secrets = new TestSecretProtector();
        var service = new ProviderService(repository, secrets, new UnusedChatClientFactory(),
            new UnusedModelLister(), TimeProvider.System);

        var first = await service.CreateAsync(Input("Local", "secret-a", isDefault: true), CancellationToken.None);
        var second = await service.CreateAsync(Input("Cloud", "secret-b", isDefault: true), CancellationToken.None);
        var listed = await service.ListAsync(CancellationToken.None);

        Assert.All(listed, provider => Assert.True(provider.HasApiKey));
        Assert.Equal(new[] { true, false }, listed.OrderBy(provider => provider.Name).Select(provider => provider.IsDefault));
        Assert.DoesNotContain("secret-a", JsonSerializer.Serialize(listed));
        Assert.Equal("secret-a", secrets.Unprotect((await repository.GetAsync(first.Id, CancellationToken.None))!.ProtectedApiKey!));

        var updated = await service.UpdateAsync(first.Id, Input("Local", null, isDefault: false), CancellationToken.None);
        Assert.NotNull(updated);
        Assert.True(updated!.HasApiKey);
        Assert.Equal(new[] { true, false }, (await service.ListAsync(CancellationToken.None))
            .OrderBy(provider => provider.Name).Select(provider => provider.IsDefault));

        Assert.True(await service.DeleteAsync(second.Id, CancellationToken.None));
        Assert.False(await service.DeleteAsync(second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TestAsync_ListsModelsStreamsAndDetectsToolCallingUsingHttpHandler()
    {
        await using var database = await CreateDatabaseAsync();
        var handler = new FakeLlmHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("llm", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<ISecretProtector, TestSecretProtector>();
        services.AddSingleton<IChatClientFactory, OpenAiCompatibleChatClientFactory>();
        services.AddSingleton<IModelLister, OpenAiModelLister>();
        using var provider = services.BuildServiceProvider();
        var repository = new LlmProviderRepository(database.Context);
        var secretProtector = provider.GetRequiredService<ISecretProtector>();
        var llm = new LlmProvider("Test", ProviderType.OpenAiCompatible, "https://llm.example/v1", "tiny-model");
        llm.Update("Test", ProviderType.OpenAiCompatible, "https://llm.example/v1", "tiny-model",
            4096, 0.2, 30, true, true, false, secretProtector.Protect("test-token"));
        await repository.AddAsync(llm, true, CancellationToken.None);
        var service = new ProviderService(repository, secretProtector,
            provider.GetRequiredService<IChatClientFactory>(), provider.GetRequiredService<IModelLister>(), TimeProvider.System);

        var result = await service.TestAsync(llm.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Reachable);
        Assert.Equal(new[] { "tiny-model", "vision-model" }, result.Models);
        Assert.True(result.StreamingSupported);
        Assert.True(result.ToolCallingSupported);
        Assert.Equal(3, handler.RequestCount);
        Assert.True((await repository.GetAsync(llm.Id, CancellationToken.None))!.LastTestSucceeded);
    }

    private static LlmProviderInput Input(string name, string? apiKey, bool isDefault) => new(
        name, ProviderType.OpenAiCompatible, "https://llm.example/v1/", "tiny-model", 4096, 0.2,
        30, isDefault, true, true, false, apiKey);

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class SqliteTestDatabase(Microsoft.Data.Sqlite.SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }

    private sealed class UnusedChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(LlmProvider provider) => throw new NotSupportedException();
    }

    private sealed class UnusedModelLister : IModelLister
    {
        public Task<IReadOnlyList<string>> ListAsync(string baseUrl, string? apiKey, int timeoutSeconds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLlmHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                return JsonResponse("""{"data":[{"id":"vision-model"},{"id":"tiny-model"}]}""");
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (!root.TryGetProperty("tools", out _))
            {
                const string stream = "data: {\"id\":\"chatcmpl-stream\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"tiny-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ready\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-stream\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"tiny-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                    "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
                };
            }

            var toolName = root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString();
            return JsonResponse(JsonSerializer.Serialize(new
            {
                id = "chatcmpl-tool",
                @object = "chat.completion",
                created = 1,
                model = "tiny-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            content = (string?)null,
                            tool_calls = new[]
                            {
                                new { id = "call_probe", type = "function", function = new { name = toolName, arguments = "{}" } }
                            }
                        },
                        finish_reason = "tool_calls"
                    }
                }
            }));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
