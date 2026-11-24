using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Security;

/// <summary>
/// Runs deterministic read-only Trivy/Grype CVE scans over SSH and stores the latest report per server in AppSettings.
/// </summary>
public sealed class CveScanService(
    IServerRepository servers,
    ISshRunner sshRunner,
    IAppSettingRepository settings,
    TimeProvider timeProvider,
    ILogger<CveScanService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CveScanReport?> GetLatestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(Key(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<CveScanReport>(raw, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Stored CVE scan report for server {ServerId} could not be parsed ({ErrorType}).",
                serverId, exception.GetType().Name);
            return null;
        }
    }

    public async Task<CveScanReport> RunAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException("The requested server no longer exists.");
        var now = timeProvider.GetUtcNow();

        RemoteCommandResult result;
        try
        {
            result = await sshRunner.RunAsync(
                server.Id,
                CveScanParser.ScanCommand,
                new SshRunOptions(TimeSpan.FromMinutes(3), UseSudo: false),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var failed = new CveScanReport(server.Id, server.Name, now, "unknown", false, [],
                $"CVE scan could not run: {exception.Message}");
            await settings.SetAsync(Key(serverId), JsonSerializer.Serialize(failed, JsonOptions), cancellationToken);
            return failed;
        }

        var report = CveScanParser.Parse(server.Id, server.Name, result.StandardOutput, now);
        await settings.SetAsync(Key(serverId), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return report;
    }

    public static string Key(Guid serverId) => $"security.cve.{serverId:N}";
}
