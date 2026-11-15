using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Infrastructure.Parsers;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class UpdatesCollector : ICollector
{
    private const string RepositoriesUnreachableMarker = "__DVA_REPOSITORIES_UNREACHABLE__";
    private readonly UpdateCollectorOptions _options;
    private readonly bool _cacheOnly;

    /// <param name="cacheOnly">
    /// Read-only groups: never write on the server. No <c>apt-get update</c>, no sudo; read the cached package lists only
    /// (<c>apt list --upgradable</c>, <c>dnf -C</c>).
    /// </param>
    public UpdatesCollector(UpdateCollectorOptions? options = null, bool cacheOnly = false)
    {
        _options = options ?? new UpdateCollectorOptions();
        _cacheOnly = cacheOnly;
    }

    public string Name => "updates";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id,
            "command -v apt-get >/dev/null 2>&1 || command -v dnf >/dev/null 2>&1",
            new SshRunOptions(TimeSpan.FromSeconds(10)), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var apt = await context.SshRunner.RunAsync(context.Server.Id, "command -v apt-get >/dev/null 2>&1",
            new SshRunOptions(TimeSpan.FromSeconds(10)), cancellationToken);
        if (apt.ExitCode == 0)
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id,
                _cacheOnly ? AptCacheOnlyCommand : AptScanCommand(_options.RepositoryTimeout),
                new SshRunOptions(_options.CommandTimeout, UseSudo: !_cacheOnly), cancellationToken);
            var unreachable = result.StandardOutput.Contains(RepositoriesUnreachableMarker, StringComparison.Ordinal);
            var refreshedAt = unreachable || result.ExitCode != 0 ? null : await ReadRefreshTimeAsync(context, cancellationToken);
            var output = unreachable
                ? new UpdateCollectionOutput("", "", "", "Repositories unreachable (apt): one or more configured package repositories could not be reached.", null, _cacheOnly)
                : new UpdateCollectionOutput(result.StandardOutput, "", "",
                    result.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(result.StandardError) ? "apt-get update or apt list failed." : result.StandardError,
                    refreshedAt, _cacheOnly);
            return new CollectorOutput(JsonSerializer.Serialize(output), result.StandardError, result.ExitCode == 0 || unreachable ? 0 : result.ExitCode, result.Duration);
        }

        var dnfResult = await context.SshRunner.RunAsync(context.Server.Id,
            _cacheOnly ? DnfCacheOnlyCheckCommand : DnfScanCommand(_options.RepositoryTimeout),
            new SshRunOptions(_options.CommandTimeout), cancellationToken);
        var dnfUnreachable = dnfResult.StandardOutput.Contains(RepositoriesUnreachableMarker, StringComparison.Ordinal);
        if (dnfUnreachable)
        {
            return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput("", "", "",
                "Repositories unreachable (dnf): one or more configured package repositories could not be reached.")),
                dnfResult.StandardError, 0, dnfResult.Duration);
        }
        if (dnfResult.ExitCode is not 0 and not 100)
        {
            return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput("", "", "",
                string.IsNullOrWhiteSpace(dnfResult.StandardError) ? "dnf check-update failed." : dnfResult.StandardError)),
                dnfResult.StandardError, dnfResult.ExitCode, dnfResult.Duration);
        }

        var security = await context.SshRunner.RunAsync(context.Server.Id,
            _cacheOnly ? "dnf -C updateinfo list security" : "dnf updateinfo list security",
            new SshRunOptions(_options.CommandTimeout), cancellationToken);
        var error = dnfResult.ExitCode == 0 || dnfResult.ExitCode == 100 ? null
            : string.IsNullOrWhiteSpace(dnfResult.StandardError) ? "dnf check-update failed." : dnfResult.StandardError;
        var dnfRefreshedAt = error is null ? await ReadRefreshTimeAsync(context, cancellationToken) : null;
        return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput("", dnfResult.StandardOutput,
            security.StandardOutput, error, dnfRefreshedAt, _cacheOnly)), dnfResult.StandardError + security.StandardError,
            error is null ? 0 : dnfResult.ExitCode, dnfResult.Duration + security.Duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        var data = JsonSerializer.Deserialize<UpdateCollectionOutput>(output.StandardOutput);
        if (data is null) return new CollectorResult(Error: "Update command produced no parseable output.");
        if (data.Error is not null) return new CollectorResult(Error: data.Error);
        var packages = data.AptOutput.Length > 0 ? UpdatesParser.ParseApt(data.AptOutput)
            : UpdatesParser.ParseDnf(data.DnfOutput, data.SecurityOutput);
        return new CollectorResult(JsonSerializer.SerializeToElement(packages));
    }

    // Read-only on every group: no sudo, no writes. The timestamp is best effort; unknown when the probe fails.
    private static async Task<DateTimeOffset?> ReadRefreshTimeAsync(ServerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await context.SshRunner.RunAsync(context.Server.Id, PackageListProbe.Command,
                new SshRunOptions(TimeSpan.FromSeconds(15)), cancellationToken);
            return probe.ExitCode == 0 ? PackageListProbe.Parse(probe.StandardOutput) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private const string AptCacheOnlyCommand = "apt list --upgradable 2>/dev/null";
    private const string DnfCacheOnlyCheckCommand = "dnf -C check-update";

    private static string AptScanCommand(int repositoryTimeoutSeconds) =>
        $"update_output=$(apt-get -o Acquire::Retries=0 -o Acquire::http::Timeout={repositoryTimeoutSeconds} -o Acquire::https::Timeout={repositoryTimeoutSeconds} update 2>&1); update_status=$?; " +
        $"if [ \"$update_status\" -ne 0 ] || printf '%s\\n' \"$update_output\" | grep -Eiq '(^|[[:space:]])Err:|Failed to fetch|Could not resolve|Temporary failure resolving|Network is unreachable|Connection timed out|Some index files failed'; then " +
        $"printf '%s\\n' '{RepositoriesUnreachableMarker}'; exit 0; fi; apt list --upgradable 2>/dev/null";

    private static string DnfScanCommand(int repositoryTimeoutSeconds) =>
        $"dnf --setopt=timeout={repositoryTimeoutSeconds} --setopt=retries=0 check-update; result=$?; " +
        $"if [ \"$result\" -eq 0 ] || [ \"$result\" -eq 100 ]; then exit \"$result\"; fi; " +
        $"printf '%s\\n' '{RepositoriesUnreachableMarker}'; exit 0";
}
