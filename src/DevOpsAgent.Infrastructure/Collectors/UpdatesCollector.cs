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
            "command -v apt-get >/dev/null 2>&1 || command -v dnf >/dev/null 2>&1 || command -v apk >/dev/null 2>&1",
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
            // T8.6, read-only on every group (no sudo): the configured sources, and where each upgradable package comes from.
            var sources = await ReadOnlyAsync(context, PackageSourcesParser.AptSourcesCommand, cancellationToken);
            var names = unreachable || result.ExitCode != 0 ? [] : UpdatesParser.ParseApt(result.StandardOutput).Select(item => item.Name).Take(500).ToArray();
            var policy = names.Length == 0 ? string.Empty : await ReadOnlyAsync(context, PackageSourcesParser.AptPolicyCommand(names), cancellationToken);
            var output = unreachable
                ? new UpdateCollectionOutput("", "", "", "Repositories unreachable (apt): one or more configured package repositories could not be reached.", null, _cacheOnly,
                    sources, PackageManager: PackageManagerKind.Apt)
                : new UpdateCollectionOutput(result.StandardOutput, "", "",
                    result.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(result.StandardError) ? "apt-get update or apt list failed." : result.StandardError,
                    refreshedAt, _cacheOnly, sources, policy, PackageManager: PackageManagerKind.Apt);
            return new CollectorOutput(JsonSerializer.Serialize(output), result.StandardError, result.ExitCode == 0 || unreachable ? 0 : result.ExitCode, result.Duration);
        }

        var dnf = await context.SshRunner.RunAsync(context.Server.Id, "command -v dnf >/dev/null 2>&1",
            new SshRunOptions(TimeSpan.FromSeconds(10)), cancellationToken);
        if (dnf.ExitCode == 0)
        {
            var dnfResult = await context.SshRunner.RunAsync(context.Server.Id,
                _cacheOnly ? DnfCacheOnlyCheckCommand : DnfScanCommand(_options.RepositoryTimeout),
                new SshRunOptions(_options.CommandTimeout), cancellationToken);
            var dnfUnreachable = dnfResult.StandardOutput.Contains(RepositoriesUnreachableMarker, StringComparison.Ordinal);
            if (dnfUnreachable)
            {
                return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput("", "", "",
                    "Repositories unreachable (dnf): one or more configured package repositories could not be reached.",
                    PackageManager: PackageManagerKind.Dnf)),
                    dnfResult.StandardError, 0, dnfResult.Duration);
            }
            if (dnfResult.ExitCode is not 0 and not 100)
            {
                return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput("", "", "",
                    string.IsNullOrWhiteSpace(dnfResult.StandardError) ? "dnf check-update failed." : dnfResult.StandardError,
                    PackageManager: PackageManagerKind.Dnf)),
                    dnfResult.StandardError, dnfResult.ExitCode, dnfResult.Duration);
            }

            var security = await context.SshRunner.RunAsync(context.Server.Id,
                _cacheOnly ? "dnf -C updateinfo list security" : "dnf updateinfo list security",
                new SshRunOptions(_options.CommandTimeout), cancellationToken);
            var error = dnfResult.ExitCode == 0 || dnfResult.ExitCode == 100 ? null
                : string.IsNullOrWhiteSpace(dnfResult.StandardError) ? "dnf check-update failed." : dnfResult.StandardError;
            var dnfRefreshedAt = error is null ? await ReadRefreshTimeAsync(context, cancellationToken) : null;
            var dnfSources = await ReadOnlyAsync(context, PackageSourcesParser.DnfSourcesCommand, cancellationToken);
            return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput("", dnfResult.StandardOutput,
                security.StandardOutput, error, dnfRefreshedAt, _cacheOnly, dnfSources, PackageManager: PackageManagerKind.Dnf)),
                dnfResult.StandardError + security.StandardError,
                error is null ? 0 : dnfResult.ExitCode, dnfResult.Duration + security.Duration);
        }

        var apkResult = await context.SshRunner.RunAsync(context.Server.Id,
            _cacheOnly ? ApkCacheOnlyCommand : ApkScanCommand,
            new SshRunOptions(_options.CommandTimeout, UseSudo: !_cacheOnly), cancellationToken);
        var apkUnreachable = apkResult.StandardOutput.Contains(RepositoriesUnreachableMarker, StringComparison.Ordinal);
        var apkRefreshedAt = apkUnreachable || apkResult.ExitCode != 0 ? null : await ReadRefreshTimeAsync(context, cancellationToken);
        var apkSources = await ReadOnlyAsync(context, PackageSourcesParser.ApkSourcesCommand, cancellationToken);
        var apkOutput = apkUnreachable
            ? new UpdateCollectionOutput("", "", "",
                "Repositories unreachable (apk): one or more configured Alpine repositories could not be reached.",
                null, _cacheOnly, apkSources, PackageManager: PackageManagerKind.Apk)
            : new UpdateCollectionOutput("", "", "",
                apkResult.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(apkResult.StandardError) ? "apk update or apk list failed." : apkResult.StandardError,
                apkRefreshedAt, _cacheOnly, apkSources, ApkOutput: apkResult.StandardOutput, PackageManager: PackageManagerKind.Apk);
        return new CollectorOutput(JsonSerializer.Serialize(apkOutput), apkResult.StandardError,
            apkResult.ExitCode == 0 || apkUnreachable ? 0 : apkResult.ExitCode, apkResult.Duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        var data = JsonSerializer.Deserialize<UpdateCollectionOutput>(output.StandardOutput);
        if (data is null) return new CollectorResult(Error: "Update command produced no parseable output.");
        if (data.Error is not null) return new CollectorResult(Error: data.Error);
        var packages = data.PackageManager == PackageManagerKind.Apk || data.ApkOutput.Length > 0
            ? UpdatesParser.ParseApk(data.ApkOutput)
            : data.AptOutput.Length > 0
                ? UpdatesParser.ParseApt(data.AptOutput)
                : UpdatesParser.ParseDnf(data.DnfOutput, data.SecurityOutput);
        // apt's own suite name ("jammy") is the same for Ubuntu and, say, Docker's repository: the origin label tells them apart.
        var origins = PackageSourcesParser.ParseAptOrigins(data.AptPolicyOutput);
        if (origins.Count > 0)
            packages = packages.Select(item => origins.TryGetValue(item.Name, out var origin) ? item with { Repository = origin } : item).ToArray();
        return new CollectorResult(JsonSerializer.SerializeToElement(packages));
    }

    /// <summary>Configured sources of a collected output (T8.6).</summary>
    public static IReadOnlyList<PackageSourceDto> ParseSources(UpdateCollectionOutput output) =>
        output.SourcesOutput.Contains(PackageSourcesParser.ApkMarker, StringComparison.Ordinal)
            ? PackageSourcesParser.ParseApk(output.SourcesOutput)
            : output.SourcesOutput.Contains(PackageSourcesParser.FileMarker, StringComparison.Ordinal)
                ? PackageSourcesParser.ParseApt(output.SourcesOutput)
                : PackageSourcesParser.ParseDnf(output.SourcesOutput);

    // Best effort, read-only, no sudo: a failure only leaves the extra information out.
    private static async Task<string> ReadOnlyAsync(ServerContext context, string command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id, command, new SshRunOptions(TimeSpan.FromSeconds(20)), cancellationToken);
            return result.ExitCode == 0 ? result.StandardOutput : string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return string.Empty; }
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
    private const string ApkCacheOnlyCommand = "apk list -u 2>/dev/null || apk version -l '<'";

    public const string ApkScanCommand =
        "update_output=$(apk update 2>&1); update_status=$?; "
        + "if [ \"$update_status\" -ne 0 ] || printf '%s\\n' \"$update_output\" | grep -Eiq '(ERROR:|temporary error|network is unreachable|timed out|unable to select packages|fetch .* failed)'; then "
        + "if printf '%s\\n' \"$update_output\" | grep -Eiq '(Permission denied|locked)'; then printf '%s\\n' \"$update_output\" >&2; exit \"$update_status\"; fi; "
        + $"printf '%s\\n' '{RepositoriesUnreachableMarker}'; exit 0; fi; apk list -u 2>/dev/null || apk version -l '<'";

    public static string AptScanCommand(int repositoryTimeoutSeconds) =>
        $"update_output=$(apt-get -o Acquire::Retries=0 -o Acquire::http::Timeout={repositoryTimeoutSeconds} -o Acquire::https::Timeout={repositoryTimeoutSeconds} update 2>&1); update_status=$?; " +
        $"if [ \"$update_status\" -ne 0 ] || printf '%s\\n' \"$update_output\" | grep -Eiq '(^|[[:space:]])Err:|Failed to fetch|Could not resolve|Temporary failure resolving|Network is unreachable|Connection timed out|Some index files failed'; then " +
        $"printf '%s\\n' '{RepositoriesUnreachableMarker}'; exit 0; fi; apt list --upgradable 2>/dev/null";

    private static string DnfScanCommand(int repositoryTimeoutSeconds) =>
        $"dnf --setopt=timeout={repositoryTimeoutSeconds} --setopt=retries=0 check-update; result=$?; " +
        $"if [ \"$result\" -eq 0 ] || [ \"$result\" -eq 100 ]; then exit \"$result\"; fi; " +
        $"printf '%s\\n' '{RepositoriesUnreachableMarker}'; exit 0";
}
