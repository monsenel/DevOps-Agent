using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Infrastructure.Security;
using DevOpsAgent.Infrastructure.Llm;
using DevOpsAgent.Infrastructure.Ssh;
using DevOpsAgent.Infrastructure.Collectors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace DevOpsAgent.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddDevOpsPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.Configure<ServerInventoryOptions>(configuration.GetSection(ServerInventoryOptions.SectionName));
        services.Configure<SshConnectionOptions>(configuration.GetSection(SshConnectionOptions.SectionName));
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var persistenceOptions = serviceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            options.UseSqlite(persistenceOptions.ConnectionString);
        });

        services.AddScoped<ILlmProviderRepository, LlmProviderRepository>();
        services.AddScoped<IChatConversationRepository, ChatConversationRepository>();
        services.AddScoped<IServerRepository, ServerRepository>();
        services.AddScoped<ISnapshotRepository, SnapshotRepository>();
        services.AddSingleton<ICollector, HostCollector>();
        services.AddSingleton<ICollector, ListenerCollector>();
        services.AddSingleton<IServerKeyStore, ServerKeyStore>();
        services.AddSingleton<SshConnectionPool>();
        services.AddSingleton<ISshConnectionPool>(serviceProvider => serviceProvider.GetRequiredService<SshConnectionPool>());
        services.AddSingleton<ISshRunner, SshRunner>();
        services.AddHttpClient("llm", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<OpenAiCompatibleChatClientFactory>();
        services.AddSingleton<IChatClientFactory>(serviceProvider =>
            serviceProvider.GetRequiredService<OpenAiCompatibleChatClientFactory>());
        services.AddSingleton<IModelLister, OpenAiModelLister>();

        var persistence = configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>() ?? new PersistenceOptions();
        var keysDirectory = Path.GetFullPath(persistence.DataProtectionKeysDirectory);
        Directory.CreateDirectory(keysDirectory);
        services.AddDataProtection()
            .SetApplicationName("DevOpsAgent")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        return services;
    }

    public static async Task ApplyMigrationsAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        var connection = new SqliteConnectionStringBuilder(options.ConnectionString);
        if (!string.IsNullOrWhiteSpace(connection.DataSource) && connection.DataSource != ":memory:")
        {
            var databasePath = Path.GetFullPath(connection.DataSource);
            var databaseDirectory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
