using System.Text.Json;
using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Security;

/// <summary>
/// Runs and persists read-only security hardening audits per server (T9.3, D-065).
/// </summary>
public sealed class SecurityAuditService(
    IServerRepository servers,
    ISshRunner sshRunner,
    IAppSettingRepository settings,
    TimeProvider timeProvider,
    IUpdatePackageRepository? updatePackages = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SecurityAuditReport?> GetLatestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(Key(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try { return JsonSerializer.Deserialize<SecurityAuditReport>(stored, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public async Task<SecurityAuditReport> RunAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException("The requested server no longer exists.");
        var now = timeProvider.GetUtcNow();
        var pendingSecurity = updatePackages is null
            ? 0
            : (await updatePackages.ListAsync(cancellationToken)).Count(pkg => pkg.ServerId == serverId && pkg.IsSecurity);

        RemoteCommandResult result;
        try
        {
            result = await sshRunner.RunAsync(server.Id, SecurityAuditParser.AuditCommand,
                new SshRunOptions(TimeSpan.FromSeconds(30), UseSudo: server.CanUseSudo), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var failed = new SecurityAuditReport(server.Id, server.Name, now, [], $"Audit could not run: {exception.Message}");
            await settings.SetAsync(Key(serverId), JsonSerializer.Serialize(failed, JsonOptions), cancellationToken);
            return failed;
        }

        var report = SecurityAuditParser.Parse(server.Id, server.Name, result.StandardOutput, now, pendingSecurity);
        await settings.SetAsync(Key(serverId), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return report;
    }

    private static string Key(Guid serverId) => $"security.audit.{serverId:N}";
}
