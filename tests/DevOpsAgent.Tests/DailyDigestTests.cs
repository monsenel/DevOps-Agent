using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Domain.Updates;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Web.Hosted;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class DailyDigestTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 8, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task SettingsPersistAndRejectOutOfRangeUtcHours()
    {
        await using var db = await CreateDatabaseAsync();
        var (service, _) = CreateService(db.Context, Morning);

        var initial = await service.GetSettingsAsync(CancellationToken.None);
        Assert.False(initial.Enabled);
        Assert.Equal(DailyDigestService.DefaultUtcHour, initial.UtcHour);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveSettingsAsync(true, -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveSettingsAsync(true, 24, CancellationToken.None));

        var saved = await service.SaveSettingsAsync(true, 6, CancellationToken.None);
        Assert.True(saved.Enabled);
        Assert.Equal(6, saved.UtcHour);

        var reloaded = await service.GetSettingsAsync(CancellationToken.None);
        Assert.True(reloaded.Enabled);
        Assert.Equal(6, reloaded.UtcHour);
    }

    [Fact]
    public void IsDueTriggersOncePerUtcDayAtOrAfterConfiguredHour()
    {
        var disabled = new DailyDigestSettings(false, 8);
        Assert.False(DailyDigestService.IsDue(disabled, Morning));

        var notYet = new DailyDigestSettings(true, 9);
        Assert.False(DailyDigestService.IsDue(notYet, Morning));

        var dueFirstTime = new DailyDigestSettings(true, 8);
        Assert.True(DailyDigestService.IsDue(dueFirstTime, Morning));

        var alreadySentToday = new DailyDigestSettings(true, 8, new DateTimeOffset(2026, 11, 24, 8, 1, 0, TimeSpan.Zero));
        Assert.False(DailyDigestService.IsDue(alreadySentToday, Morning));

        var sentYesterday = new DailyDigestSettings(true, 8, new DateTimeOffset(2026, 11, 23, 8, 1, 0, TimeSpan.Zero));
        Assert.True(DailyDigestService.IsDue(sentYesterday, Morning));
    }

    [Fact]
    public async Task CompileAndDeliverDigestUsesD028SafeWordingWithoutSecretsOrHostDetails()
    {
        await using var db = await CreateDatabaseAsync();
        var s1 = new Server("srv-edge", "198.51.100.10", 22, "secretuser", "id_ed25519", "prod");
        var s2 = new Server("srv-app", "198.51.100.20", 22, "secretuser", "id_ed25519", "prod");
        db.Context.Servers.AddRange(s1, s2);
        db.Context.Alerts.AddRange(
            new Alert($"server-unreachable:{s1.Id:N}", "server-unreachable", AlertSeverity.High, s1.Id, "198.51.100.10",
                "srv-edge is unreachable", "sensitive detail 198.51.100.10", Morning.AddMinutes(-10)),
            new Alert($"ssh-brute-force:{s2.Id:N}", "ssh-brute-force", AlertSeverity.Critical, s2.Id, "203.0.113.99",
                "SSH brute force from 203.0.113.99", "user=secretuser failed 40 times", Morning.AddHours(-2)),
            new Alert($"threshold-disk:{s2.Id:N}", "threshold-disk", AlertSeverity.Medium, s2.Id, "srv-app",
                "Disk high", "92%", Morning.AddMinutes(-5)),
            new Alert("low-noise", "container-state", AlertSeverity.Low, s2.Id, "worker",
                "Paused", "paused", Morning.AddMinutes(-3)));
        db.Context.UpdatePackages.AddRange(
            new UpdatePackage(s1.Id, "openssl", "3.0.11", "3.0.15", "Debian-Security", true, Morning),
            new UpdatePackage(s2.Id, "curl", "7.88", "7.89", "Debian-Security", true, Morning),
            new UpdatePackage(s2.Id, "vim", "9.0", "9.1", "Debian", false, Morning));
        await db.Context.SaveChangesAsync();

        var (service, notifier) = CreateService(db.Context, Morning);
        var notificationService = new NotificationService(new AppSettingRepository(db.Context), new PlainProtector(), notifier, new ServerRepository(db.Context));
        await notificationService.SaveAsync(new NotificationSettingsInput(true, "top-secret-bot-token", "123456",
            false, "https://ntfy.sh", "", "", AlertSeverity.Medium), CancellationToken.None);
        await service.SaveSettingsAsync(true, 8, CancellationToken.None);

        var result = await service.SendIfDueAsync(CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Equal(1, result.DeliveredEndpoints);
        Assert.Equal(2, result.Summary.ServerCount);
        Assert.Equal(1, result.Summary.UnreachableCount);
        Assert.Equal(1, result.Summary.CriticalAlerts);
        Assert.Equal(1, result.Summary.HighAlerts);
        Assert.Equal(1, result.Summary.MediumAlerts);
        Assert.Equal(1, result.Summary.LowAlerts);
        Assert.Equal(2, result.Summary.PendingSecurityUpdates);
        Assert.Equal(1, result.Summary.RecentSshAttackEvents);

        var delivered = Assert.Single(notifier.Sent);
        Assert.Contains("Servers: 2 total, 1 unreachable", delivered.Message, StringComparison.Ordinal);
        Assert.Contains("Open alerts: 4 (1 critical, 1 high, 1 medium, 1 low)", delivered.Message, StringComparison.Ordinal);
        Assert.Contains("Pending security updates: 2", delivered.Message, StringComparison.Ordinal);
        Assert.Contains("SSH security events (24h): 1", delivered.Message, StringComparison.Ordinal);
        // D-028: no secrets, IPs, usernames, or raw alert details leave the app.
        Assert.DoesNotContain("198.51.100.10", delivered.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.99", delivered.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secretuser", delivered.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret-bot-token", delivered.Message, StringComparison.Ordinal);

        // Second run same day must not send again.
        var second = await service.SendIfDueAsync(CancellationToken.None);
        Assert.False(second.Sent);
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task HostedServiceTriggersDeliveryWhenDue()
    {
        await using var db = await CreateDatabaseAsync();
        var notifier = new RecordingNotifier();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(db.Connection));
        services.AddScoped<IAppSettingRepository, AppSettingRepository>();
        services.AddScoped<IServerRepository, ServerRepository>();
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.AddScoped<IUpdatePackageRepository, UpdatePackageRepository>();
        services.AddSingleton<ISecretProtector>(new PlainProtector());
        services.AddSingleton<INotifier>(notifier);
        services.AddSingleton<TimeProvider>(new FixedTime(Morning));
        services.AddLogging();
        services.AddScoped<NotificationService>();
        services.AddScoped<DailyDigestService>();

        await using var provider = services.BuildServiceProvider();
        using (var setupScope = provider.CreateScope())
        {
            await setupScope.ServiceProvider.GetRequiredService<NotificationService>()
                .SaveAsync(new NotificationSettingsInput(false, "", "", true, "https://ntfy.sh", "secret-topic", "", AlertSeverity.Medium), CancellationToken.None);
            await setupScope.ServiceProvider.GetRequiredService<DailyDigestService>()
                .SaveSettingsAsync(true, 8, CancellationToken.None);
        }

        var hosted = new DailyDigestHostedService(provider.GetRequiredService<IServiceScopeFactory>(), new DummyLifetime(), NullLogger<DailyDigestHostedService>.Instance);
        Assert.True(await hosted.RunOnceIfDueAsync(CancellationToken.None));
        Assert.False(await hosted.RunOnceIfDueAsync(CancellationToken.None));
        Assert.Single(notifier.Sent);
    }

    private static (DailyDigestService Service, RecordingNotifier Notifier) CreateService(AppDbContext context, DateTimeOffset now)
    {
        var notifier = new RecordingNotifier();
        var settings = new AppSettingRepository(context);
        var servers = new ServerRepository(context);
        var notificationService = new NotificationService(settings, new PlainProtector(), notifier, servers);
        var service = new DailyDigestService(
            settings,
            servers,
            new AlertRepository(context),
            new UpdatePackageRepository(context),
            notificationService,
            notifier,
            new FixedTime(now),
            NullLogger<DailyDigestService>.Instance);
        return (service, notifier);
    }

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationEndpoint Endpoint, string Message)> Sent { get; } = [];
        public Task SendAsync(NotificationEndpoint endpoint, string message, CancellationToken cancellationToken)
        {
            Sent.Add((endpoint, message));
            return Task.CompletedTask;
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string ciphertext) => ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DummyLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
