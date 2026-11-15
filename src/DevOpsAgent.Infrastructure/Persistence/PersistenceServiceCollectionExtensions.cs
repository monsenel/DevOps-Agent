using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Infrastructure.Security;
using DevOpsAgent.Infrastructure.Llm;
using DevOpsAgent.Infrastructure.Ssh;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Notifications;
using DevOpsAgent.Application.Notifications;
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
        services.Configure<SecurityMonitoringOptions>(configuration.GetSection(SecurityMonitoringOptions.SectionName));
        services.Configure<UpdateCollectorOptions>(configuration.GetSection(UpdateCollectorOptions.SectionName));
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var persistenceOptions = serviceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            options.UseSqlite(persistenceOptions.ConnectionString);
        });

        services.AddScoped<ILlmProviderRepository, LlmProviderRepository>();
        services.AddScoped<IAppSettingRepository, AppSettingRepository>();
        services.AddScoped<IActionRecordRepository, ActionRecordRepository>();
        services.AddScoped<IUpdatePackageRepository, UpdatePackageRepository>();
        services.AddScoped<IUpdateScanRepository, UpdateScanRepository>();
        services.AddScoped<IMetricRepository, MetricRepository>();
        services.AddScoped<IHealthCheckRepository, HealthCheckRepository>();
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.AddScoped<IAlertSignalSource, AlertSignalSource>();
        services.AddScoped<AuthLogScanner>();
        services.AddTransient<IHealthProbe, HealthProbe>();
        services.AddScoped<IUpdatesCollector, DevOpsAgent.Infrastructure.Collectors.UpdatesCollectorService>();
        services.AddScoped<IChatConversationRepository, ChatConversationRepository>();
        services.AddScoped<IServerRepository, ServerRepository>();
        services.AddScoped<ISnapshotRepository, SnapshotRepository>();
        services.AddScoped<IGraphRepository, GraphRepository>();
        services.AddSingleton<ICollector, HostCollector>();
        services.AddSingleton<ICollector, ListenerCollector>();
        services.AddSingleton<ICollector, DockerCollector>();
        services.AddSingleton<ICollector, SystemdCollector>();
        services.AddSingleton<ICollector, NginxCollector>();
        services.AddSingleton<ICollector, ConnectionCollector>();
        services.AddSingleton<IServerKeyStore, ServerKeyStore>();
        services.AddSingleton<SshConnectionPool>();
        services.AddSingleton<ISshConnectionPool>(serviceProvider => serviceProvider.GetRequiredService<SshConnectionPool>());
        services.AddSingleton<ISshRunner, SshRunner>();
        services.AddHttpClient("llm", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("health", client => client.Timeout = TimeSpan.FromSeconds(10));
        // Telegram credentials appear in the Bot API request path. Avoid HttpClientFactory's
        // default request URI logging for this client so a token can never enter application logs.
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<HttpNotifier>();
        services.AddSingleton<INotifier>(provider => provider.GetRequiredService<HttpNotifier>());
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

    /// <summary>
    /// A discovery run still Running at startup was cut off by a crash or restart and can never finish; mark it Failed so it
    /// is never shown as in progress and never used as a merge base. Returns how many were swept.
    /// </summary>
    public static async Task<int> RecoverInterruptedDiscoveryAsync(this IServiceProvider serviceProvider,
        TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await RecoverInterruptedDiscoveryAsync(dbContext, timeProvider.GetUtcNow(), cancellationToken);
    }

    public static async Task<int> RecoverInterruptedDiscoveryAsync(AppDbContext dbContext, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var running = await dbContext.Snapshots
            .Where(snapshot => snapshot.Status == DevOpsAgent.Domain.Discovery.SnapshotStatus.Running)
            .ToListAsync(cancellationToken);
        foreach (var snapshot in running)
            snapshot.Fail(now, snapshot.SucceededServerCount, snapshot.FailedServerCount);
        await dbContext.SaveChangesAsync(cancellationToken);
        return running.Count;
    }
}
